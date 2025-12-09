using Content.Shared.Actions.Events;
using Content.Shared.CombatMode;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Movement.Pulling.Events;
using Content.Shared.Popups;
using Content.Shared.Standing;
using Content.Shared.Stunnable;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Melee.Events;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Shared._FarHorizons.CQC;

/// <summary>
/// System that handles Close Quarters Combat (CQC) mechanics.
/// Provides enhanced melee combat with combos and special techniques.
/// </summary>
public abstract class SharedCQCSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly SharedStunSystem _stun = default!;
    [Dependency] private readonly StandingStateSystem _standing = default!;

    // Combo patterns for techniques
    private static readonly Dictionary<CQCTechnique, CQCAttackType[]> TechniquePatterns = new()
    {
        { CQCTechnique.DB_Sweep, new[] { CQCAttackType.Shove, CQCAttackType.Attack } },
        { CQCTechnique.DB_Uppercut, new[] { CQCAttackType.Attack, CQCAttackType.Attack } },
        { CQCTechnique.DB_Tackle, new[] { CQCAttackType.Shove, CQCAttackType.Shove } },
        { CQCTechnique.DB_Choke, new[] { CQCAttackType.Drag, CQCAttackType.Shove } }
    };

    private static readonly Dictionary<CQCTechnique, string> TechniqueNames = new()
    {
        { CQCTechnique.DB_Sweep, "Shove → Attack" },
        { CQCTechnique.DB_Uppercut, "Attack → Attack" },
        { CQCTechnique.DB_Tackle, "Shove → Shove" },
        { CQCTechnique.DB_Choke, "Drag → Shove" }
    };

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CQCComponent, MeleeHitEvent>(OnMeleeHit);
        SubscribeLocalEvent<CQCComponent, GetMeleeDamageEvent>(OnGetMeleeDamage);
        SubscribeLocalEvent<CQCComponent, GetMeleeAttackRateEvent>(OnGetMeleeAttackRate);
        SubscribeLocalEvent<CQCComponent, DisarmedEvent>(OnDisarmed);
        SubscribeLocalEvent<CQCComponent, DisarmAttemptEvent>(OnDisarmAttempt);
        SubscribeLocalEvent<CQCComponent, PullStartedMessage>(OnPullStarted);
        SubscribeLocalEvent<CQCComponent, PullStoppedMessage>(OnPullStopped);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // Update combo timers and choke damage
        var query = EntityQueryEnumerator<CQCComponent>();
        while (query.MoveNext(out var uid, out var cqc))
        {
            if (!cqc.Active)
                continue;

            // Update combo timer
            if (cqc.ComboCount > 0)
            {
                var timeSinceLastAttack = _timing.CurTime - cqc.LastAttackTime;
                if (timeSinceLastAttack.TotalSeconds > cqc.ComboTimeout)
                {
                    // Reset combo
                    ResetCombo(uid, cqc);
                }
            }

            // Apply choke damage
            if (cqc.ChokingTarget != null && EntityManager.EntityExists(cqc.ChokingTarget.Value))
            {
                var timeSinceChokeStart = _timing.CurTime - cqc.ChokeStartTime;
                var intervals = (int)(timeSinceChokeStart.TotalSeconds / cqc.ChokeDamageInterval);
                
                // Apply damage every interval
                if (intervals > 0 && timeSinceChokeStart.TotalSeconds % cqc.ChokeDamageInterval < frameTime)
                {
                    ApplyChokeDamage(uid, cqc.ChokingTarget.Value, cqc);
                }
            }
        }
    }

    private void OnDisarmed(EntityUid uid, CQCComponent component, DisarmedEvent args)
    {
        if (!component.Active || args.Handled)
            return;

        // Track shove (disarm) in attack sequence
        TrackAttack(uid, CQCAttackType.Shove, component);
        
        // Reset guarantee flag after use
        component.GuaranteeNextDisarm = false;
        Dirty(uid, component);
    }

    private void OnDisarmAttempt(EntityUid uid, CQCComponent component, ref DisarmAttemptEvent args)
    {
        if (!component.Active)
            return;

        // CQC users always succeed at the shove push (no RNG)
        // But only guarantee disarm if we're doing a combo technique
        if (!component.GuaranteeNextDisarm)
        {
            // Mark that we should NOT disarm items, only push
            // We'll set a flag on the component to be checked later
        }
    }

    private void OnPullStarted(EntityUid uid, CQCComponent component, PullStartedMessage args)
    {
        if (!component.Active)
            return;

        // Track drag in attack sequence
        TrackAttack(uid, CQCAttackType.Drag, component);
    }

    private void OnPullStopped(EntityUid uid, CQCComponent component, PullStoppedMessage args)
    {
        if (!component.Active)
            return;

        // Stop choking if we were choking this target
        if (component.ChokingTarget == args.PulledUid)
        {
            StopChoking(uid, component);
        }
    }

    private void OnGetMeleeDamage(EntityUid uid, CQCComponent component, ref GetMeleeDamageEvent args)
    {
        if (!component.Active)
            return;

        // Apply CQC damage multiplier
        var damageModEvent = new CQCDamageModifierEvent(uid, args.Weapon, args.Damage, component.ComboCount, component.DamageMultiplier);
        RaiseLocalEvent(uid, ref damageModEvent);

        // Apply base multiplier and combo bonus
        var totalMultiplier = damageModEvent.Multiplier;
        var comboBonus = component.ComboCount * component.ComboBonusDamagePerHit;
        
        args.Damage *= totalMultiplier;
        args.Damage += new DamageSpecifier { DamageDict = { ["Blunt"] = comboBonus } };
    }

    private void OnGetMeleeAttackRate(EntityUid uid, CQCComponent component, ref GetMeleeAttackRateEvent args)
    {
        if (!component.Active)
            return;

        // Apply CQC attack rate multiplier
        args.Multipliers *= component.AttackRateMultiplier;
    }

    private void OnMeleeHit(EntityUid uid, CQCComponent component, MeleeHitEvent args)
    {
        if (!component.Active || args.HitEntities.Count == 0)
            return;

        // Track attack in sequence
        TrackAttack(uid, CQCAttackType.Attack, component);

        // Check for technique execution
        var technique = CheckForTechnique(component);
        
        foreach (var target in args.HitEntities)
        {
            if (technique != CQCTechnique.None)
            {
                ExecuteTechnique(uid, target, technique, component);
            }
        }

        // Play combo sound
        if (component.ComboSound != null && component.ComboCount > 1)
        {
            _audio.PlayPvs(component.ComboSound, uid);
        }
    }

    private void TrackAttack(EntityUid uid, CQCAttackType attackType, CQCComponent component)
    {
        // Update combo count and timing
        component.ComboCount = Math.Min(component.ComboCount + 1, component.MaxComboCount);
        component.LastAttackTime = _timing.CurTime;

        // Add to attack sequence
        component.AttackSequence.Add(attackType);
        
        // Keep only the last MaxComboCount attacks
        if (component.AttackSequence.Count > component.MaxComboCount)
        {
            component.AttackSequence.RemoveAt(0);
        }

        // Update combo text display
        UpdateComboText(uid, component);
        
        Dirty(uid, component);
    }

    private void UpdateComboText(EntityUid uid, CQCComponent component)
    {
        if (component.AttackSequence.Count == 0)
        {
            component.ComboText = string.Empty;
            return;
        }

        // Build display string
        var textParts = new List<string>();
        foreach (var attack in component.AttackSequence)
        {
            textParts.Add(attack switch
            {
                CQCAttackType.Shove => "Shove",
                CQCAttackType.Attack => "Attack",
                CQCAttackType.Drag => "Drag",
                _ => "Unknown"
            });
        }

        component.ComboText = string.Join(" → ", textParts);
        Dirty(uid, component);
    }

    private CQCTechnique CheckForTechnique(CQCComponent component)
    {
        if (component.AttackSequence.Count < 2)
            return CQCTechnique.None;

        // Check each technique pattern
        foreach (var (technique, pattern) in TechniquePatterns)
        {
            if (MatchesPattern(component.AttackSequence, pattern))
            {
                return technique;
            }
        }

        return CQCTechnique.None;
    }

    private bool MatchesPattern(List<CQCAttackType> sequence, CQCAttackType[] pattern)
    {
        if (sequence.Count < pattern.Length)
            return false;

        // Check if the last N attacks match the pattern
        var startIndex = sequence.Count - pattern.Length;
        for (int i = 0; i < pattern.Length; i++)
        {
            if (sequence[startIndex + i] != pattern[i])
                return false;
        }

        return true;
    }

    private void ExecuteTechnique(EntityUid user, EntityUid target, CQCTechnique technique, CQCComponent component)
    {
        // Check for counter
        if (TryComp<CQCComponent>(target, out var targetCqc) && targetCqc.Active)
        {
            var timeSinceTargeted = _timing.CurTime - targetCqc.LastTechniqueTargetTime;
            if (timeSinceTargeted.TotalSeconds <= targetCqc.CounterWindow)
            {
                // Technique was countered!
                _popup.PopupEntity($"{Name(target)} counters your {technique}!", user, user, PopupType.LargeCaution);
                _popup.PopupEntity($"You counter {Name(user)}'s {technique}!", target, target, PopupType.Large);
                
                var counterEvent = new CQCTechniqueExecutedEvent(user, target, technique, WasCountered: true);
                RaiseLocalEvent(user, ref counterEvent);
                return;
            }
        }

        // Update target's counter window
        if (targetCqc != null)
        {
            targetCqc.LastTechniqueTargetTime = _timing.CurTime;
            Dirty(target, targetCqc);
        }

        // Execute the technique
        switch (technique)
        {
            case CQCTechnique.DB_Sweep:
                ExecuteSweep(user, target, component);
                break;
            case CQCTechnique.DB_Uppercut:
                ExecuteUppercut(user, target, component);
                break;
            case CQCTechnique.DB_Tackle:
                ExecuteTackle(user, target, component);
                break;
            case CQCTechnique.DB_Choke:
                ExecuteChoke(user, target, component);
                break;
        }

        var techEvent = new CQCTechniqueExecutedEvent(user, target, technique);
        RaiseLocalEvent(user, ref techEvent);

        // Reset combo after technique (except for choke which is ongoing)
        if (technique != CQCTechnique.DB_Choke)
        {
            ResetCombo(user, component);
        }
    }

    private void ExecuteSweep(EntityUid user, EntityUid target, CQCComponent component)
    {
        // Guarantee next disarm for technique
        component.GuaranteeNextDisarm = true;
        Dirty(user, component);

        // Stun like a single taser hit (about 3 seconds)
        _stun.TryAddStunDuration(target, TimeSpan.FromSeconds(3));

        // Deal 20% stamina damage (assuming 100 max stamina = 20 damage)
        if (TryComp<StaminaComponent>(target, out var stamina))
        {
            var staminaDamage = stamina.CritThreshold * 0.2f;
            var damage = new DamageSpecifier { DamageDict = { ["Stamina"] = staminaDamage } };
            _damageable.TryChangeDamage(target, damage);
        }

        // Deal 15 blunt damage
        var bluntDamage = new DamageSpecifier { DamageDict = { ["Blunt"] = 15f } };
        _damageable.TryChangeDamage(target, bluntDamage);

        _popup.PopupEntity("DB_SWEEP! You sweep their legs!", user, user, PopupType.Large);
        _popup.PopupEntity($"{Name(user)} sweeps your legs!", target, target, PopupType.LargeCaution);
        _popup.PopupEntity($"{Name(user)} legsweeps {Name(target)}!", user, Filter.PvsExcept(user).RemoveWhereAttachedEntity(e => e == target), true, PopupType.Medium);
    }

    private void ExecuteUppercut(EntityUid user, EntityUid target, CQCComponent component)
    {
        // Deal 35 blunt damage
        var damage = new DamageSpecifier { DamageDict = { ["Blunt"] = 35f } };
        _damageable.TryChangeDamage(target, damage);

        _popup.PopupEntity("DB_UPPERCUT! Devastating strike!", user, user, PopupType.Large);
        _popup.PopupEntity($"{Name(user)} delivers a devastating uppercut!", target, target, PopupType.LargeCaution);
        _popup.PopupEntity($"{Name(user)} uppercuts {Name(target)}!", user, Filter.PvsExcept(user).RemoveWhereAttachedEntity(e => e == target), true, PopupType.Medium);
    }

    private void ExecuteTackle(EntityUid user, EntityUid target, CQCComponent component)
    {
        // Guarantee next disarm for technique
        component.GuaranteeNextDisarm = true;
        Dirty(user, component);

        // Stamina crit both user and target
        if (TryComp<StaminaComponent>(target, out var targetStamina))
        {
            var damage = new DamageSpecifier { DamageDict = { ["Stamina"] = targetStamina.CritThreshold } };
            _damageable.TryChangeDamage(target, damage);
        }

        if (TryComp<StaminaComponent>(user, out var userStamina))
        {
            var damage = new DamageSpecifier { DamageDict = { ["Stamina"] = userStamina.CritThreshold } };
            _damageable.TryChangeDamage(user, damage);
        }

        // Make both fall down
        _standing.Down(target);
        _standing.Down(user);

        _popup.PopupEntity("DB_TACKLE! You tackle them to the ground!", user, user, PopupType.Large);
        _popup.PopupEntity($"{Name(user)} tackles you to the ground!", target, target, PopupType.LargeCaution);
        _popup.PopupEntity($"{Name(user)} tackles {Name(target)} to the ground!", user, Filter.PvsExcept(user).RemoveWhereAttachedEntity(e => e == target), true, PopupType.Medium);
    }

    private void ExecuteChoke(EntityUid user, EntityUid target, CQCComponent component)
    {
        // Guarantee next disarm for technique
        component.GuaranteeNextDisarm = true;
        Dirty(user, component);

        // Start choking the target
        component.ChokingTarget = target;
        component.ChokeStartTime = _timing.CurTime;
        Dirty(user, component);

        _popup.PopupEntity("DB_CHOKE! You grab them in a chokehold!", user, user, PopupType.Large);
        _popup.PopupEntity($"{Name(user)} grabs you in a chokehold!", target, target, PopupType.LargeCaution);
        _popup.PopupEntity($"{Name(user)} grabs {Name(target)} in a chokehold!", user, Filter.PvsExcept(user).RemoveWhereAttachedEntity(e => e == target), true, PopupType.Medium);

        // Apply initial damage
        ApplyChokeDamage(user, target, component);
    }

    private void ApplyChokeDamage(EntityUid user, EntityUid target, CQCComponent component)
    {
        if (!EntityManager.EntityExists(target))
        {
            StopChoking(user, component);
            return;
        }

        // Apply asphyxiation and blunt damage
        var damage = new DamageSpecifier 
        { 
            DamageDict = 
            { 
                ["Asphyxiation"] = component.ChokeDamagePerSecond * component.ChokeDamageInterval,
                ["Blunt"] = component.ChokeBluntDamagePerSecond * component.ChokeDamageInterval
            } 
        };
        _damageable.TryChangeDamage(target, damage);

        // Show periodic feedback
        _popup.PopupEntity("You're choking them!", user, user, PopupType.Small);
        _popup.PopupEntity($"{Name(user)} is choking you!", target, target, PopupType.MediumCaution);
    }

    private void StopChoking(EntityUid uid, CQCComponent component)
    {
        if (component.ChokingTarget == null)
            return;

        var target = component.ChokingTarget.Value;
        component.ChokingTarget = null;
        Dirty(uid, component);

        if (EntityManager.EntityExists(target))
        {
            _popup.PopupEntity("You release the chokehold!", uid, uid);
            _popup.PopupEntity($"{Name(uid)} releases the chokehold!", target, target);
        }

        // Reset combo after releasing choke
        ResetCombo(uid, component);
    }

    /// <summary>
    /// Toggles CQC mode on or off for an entity.
    /// </summary>
    public void ToggleCQC(EntityUid uid, CQCComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        component.Active = !component.Active;
        Dirty(uid, component);

        var message = component.Active 
            ? "CQC Mode Activated" 
            : "CQC Mode Deactivated";
        _popup.PopupEntity(message, uid, uid);

        // Reset combo when toggling off
        if (!component.Active)
        {
            ResetCombo(uid, component);
        }

        // Raise toggle event
        var toggleEvent = new CQCToggleEvent(uid, component.Active);
        RaiseLocalEvent(uid, ref toggleEvent);
    }

    /// <summary>
    /// Resets the combo counter for an entity.
    /// </summary>
    public void ResetCombo(EntityUid uid, CQCComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        if (component.ComboCount > 0)
        {
            var comboEvent = new CQCComboEvent(uid, 0, ComboBroken: true);
            RaiseLocalEvent(uid, ref comboEvent);
        }

        component.ComboCount = 0;
        component.AttackSequence.Clear();
        component.ComboText = string.Empty;
        
        // Stop any active choking
        if (component.ChokingTarget != null)
        {
            StopChoking(uid, component);
        }
        
        Dirty(uid, component);
    }

    /// <summary>
    /// Checks if an entity can perform a CQC technique.
    /// </summary>
    public bool CanPerformTechnique(EntityUid user, EntityUid target, CQCComponent? component = null)
    {
        if (!Resolve(user, ref component))
            return false;

        if (!component.Active)
            return false;

        var ev = new CQCTechniqueAttemptEvent(user, target);
        RaiseLocalEvent(user, ref ev);

        return !ev.Cancelled;
    }
}
