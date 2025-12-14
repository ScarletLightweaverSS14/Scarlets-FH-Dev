using Content.Shared.Actions.Events;
using Content.Shared.CombatMode;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Movement.Pulling.Events;
using Content.Shared.Popups;
using Content.Shared.Standing;
using Content.Shared.Stunnable;
using Content.Shared.Throwing;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Melee.Events;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Network;
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
    [Dependency] private readonly ThrowingSystem _throwing = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly INetManager _netMan = default!;

    // Combo patterns for techniques
    private static readonly Dictionary<CQCTechnique, CQCAttackType[]> TechniquePatterns = new()
    {
        { CQCTechnique.DB_Sweep, new[] { CQCAttackType.Shove, CQCAttackType.Attack } },
        { CQCTechnique.DB_Uppercut, new[] { CQCAttackType.Attack, CQCAttackType.Attack } },
        { CQCTechnique.DB_Tackle, new[] { CQCAttackType.Shove, CQCAttackType.Shove } },
        { CQCTechnique.DB_Choke, new[] { CQCAttackType.Drag, CQCAttackType.Shove } },
        { CQCTechnique.DB_Throw, new[] { CQCAttackType.Drag, CQCAttackType.Shove } }
    };

    private static readonly Dictionary<CQCTechnique, string> TechniqueNames = new()
    {
        { CQCTechnique.DB_Sweep, "Shove → Attack" },
        { CQCTechnique.DB_Uppercut, "Attack → Attack" },
        { CQCTechnique.DB_Tackle, "Shove → Shove" },
        { CQCTechnique.DB_Choke, "Drag → Shove" },
        { CQCTechnique.DB_Throw, "Drag → Shove" }
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
        SubscribeAllEvent<DisarmAttackEvent>(OnDisarmAttack);
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

        // Check for technique execution BEFORE tracking this shove
        var technique = CheckForTechnique(component);
        
        if (technique != CQCTechnique.None)
        {
            // Execute technique
            ExecuteTechnique(uid, args.Target, technique, component);
            
            // Mark event as handled AND clear popup to prevent vanilla system from showing messages
            args.Handled = true;
            args.PopupPrefix = string.Empty;
            
            // Reset combo immediately after technique
            ResetCombo(uid, component);
            return; // Exit early to prevent tracking this shove
        }

        // Track shove (disarm) in attack sequence only if no technique
        // Set combo target
        component.ComboTarget = args.Target;
        TrackAttack(uid, CQCAttackType.Shove, component);
    }

    private void OnDisarmAttack(DisarmAttackEvent msg, EntitySessionEventArgs args)
    {
        if (args.SenderSession.AttachedEntity is not {} user)
            return;

        if (!TryComp<CQCComponent>(user, out var component) || !component.Active)
            return;

        var target = GetEntity(msg.Target);
        if (target == null)
            return;

        // Check if this is a throw attempt (RMB on pulled entity with Drag combo ready)
        if (component.LastPulledEntity != null && target == component.LastPulledEntity)
        {
            // Check if we have Drag in sequence (start of throw combo)
            if (component.AttackSequence.Count > 0 && 
                component.AttackSequence[0] == CQCAttackType.Drag)
            {
                // This is the throw combo! Set combo target, track shove, execute throw, and mark to cancel disarm
                component.ComboTarget = target.Value;
                TrackAttack(user, CQCAttackType.Shove, component);
                ExecuteThrow(user, target.Value, component);
                ResetCombo(user, component);
                // Set flag to cancel the disarm in DisarmAttemptEvent
                component.ThrowPending = true;
                return;
            }
        }
    }

    private void OnDisarmAttempt(EntityUid uid, CQCComponent component, ref DisarmAttemptEvent args)
    {
        if (!component.Active)
            return;

        // If throw is pending, cancel the disarm
        if (component.ThrowPending)
        {
            args.Cancelled = true;
            component.ThrowPending = false;
            return;
        }

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

        // Track drag in attack sequence and remember who we're pulling
        component.LastPulledEntity = args.PulledUid;
        component.ComboTarget = args.PulledUid;
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
        
        // Clear last pulled entity when pull stops
        if (component.LastPulledEntity == args.PulledUid)
        {
            component.LastPulledEntity = null;
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

        var target = args.HitEntities[0];

        // Check for technique execution BEFORE tracking this attack
        var technique = CheckForTechnique(component);
        
        if (technique != CQCTechnique.None)
        {
            // Execute technique only on first target to prevent multi-trigger
            ExecuteTechnique(uid, target, technique, component);
            
            // Reset combo immediately after technique - DON'T track this attack
            ResetCombo(uid, component);
            return; // Exit early to prevent tracking this attack
        }

        // Track attack in sequence only if no technique was executed
        // Set or verify combo target
        if (component.ComboTarget == null || component.ComboTarget == target)
        {
            component.ComboTarget = target;
            TrackAttack(uid, CQCAttackType.Attack, component);
        }
        else
        {
            // Different target, reset combo
            ResetCombo(uid, component);
            component.ComboTarget = target;
            TrackAttack(uid, CQCAttackType.Attack, component);
        }

        // Play combo sound for normal hits
        if (component.ComboCount > 1 && component.ComboSound != null)
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
            case CQCTechnique.DB_Throw:
                // DB_THROW is handled in OnThrowAttempt, not here
                break;
        }

        var techEvent = new CQCTechniqueExecutedEvent(user, target, technique);
        RaiseLocalEvent(user, ref techEvent);
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
        // Check if target is down - if so, kick instead of uppercut
        var isDown = _standing.IsDown(target);
        
        // Deal 35 blunt damage
        var damage = new DamageSpecifier { DamageDict = { ["Blunt"] = 35f } };
        _damageable.TryChangeDamage(target, damage);

        if (isDown)
        {
            _popup.PopupEntity("DB_KICK! You kick them while they're down!", user, user, PopupType.Large);
            _popup.PopupEntity($"{Name(user)} kicks you while you're down!", target, target, PopupType.LargeCaution);
            _popup.PopupEntity($"{Name(user)} kicks {Name(target)} while they're down!", user, Filter.PvsExcept(user).RemoveWhereAttachedEntity(e => e == target), true, PopupType.Medium);
        }
        else
        {
            _popup.PopupEntity("DB_UPPERCUT! Devastating strike!", user, user, PopupType.Large);
            _popup.PopupEntity($"{Name(user)} delivers a devastating uppercut!", target, target, PopupType.LargeCaution);
            _popup.PopupEntity($"{Name(user)} uppercuts {Name(target)}!", user, Filter.PvsExcept(user).RemoveWhereAttachedEntity(e => e == target), true, PopupType.Medium);
        }
    }

    private void ExecuteTackle(EntityUid user, EntityUid target, CQCComponent component)
    {
        // Guarantee next disarm for technique
        component.GuaranteeNextDisarm = true;
        Dirty(user, component);

        // Chest kick - stun the target and deal damage
        _stun.TryAddStunDuration(target, TimeSpan.FromSeconds(4));
        
        // Deal significant blunt damage to the chest
        var damage = new DamageSpecifier { DamageDict = { ["Blunt"] = 25f } };
        _damageable.TryChangeDamage(target, damage);
        
        // Knockdown the target
        _standing.Down(target);

        _popup.PopupEntity("DB_CHEST_KICK! You kick them in the chest!", user, user, PopupType.Large);
        _popup.PopupEntity($"{Name(user)} kicks you in the chest!", target, target, PopupType.LargeCaution);
        _popup.PopupEntity($"{Name(user)} kicks {Name(target)} in the chest!", user, Filter.PvsExcept(user).RemoveWhereAttachedEntity(e => e == target), true, PopupType.Medium);
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

    private void ExecuteThrow(EntityUid user, EntityUid thrown, CQCComponent component)
    {
        // Get user's facing direction
        var userXform = Transform(user);
        var direction = userXform.LocalRotation.ToWorldVec();
        
        // Throw with MUCH enhanced velocity - really far!
        _throwing.TryThrow(thrown, direction * 25f, 25f, user, pushbackRatio: 0f);

        _popup.PopupEntity("DB_THROW! You hurl them away!", user, user, PopupType.Large);
        _popup.PopupEntity($"{Name(user)} hurls you through the air!", thrown, thrown, PopupType.LargeCaution);
        _popup.PopupEntity($"{Name(user)} hurls {Name(thrown)} through the air!", user, Filter.PvsExcept(user).RemoveWhereAttachedEntity(e => e == thrown), true, PopupType.Medium);
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
        component.ComboTarget = null;
        component.LastPulledEntity = null;
        
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
