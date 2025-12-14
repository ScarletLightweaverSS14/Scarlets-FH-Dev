using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Shared._FarHorizons.CQC;

/// <summary>
/// Types of CQC attacks that can be performed.
/// </summary>
public enum CQCAttackType : byte
{
    Shove,  // Disarm attack
    Attack, // Normal melee attack
    Drag    // Drag/Pull action
}

/// <summary>
/// Component for entities that can perform Close Quarters Combat (CQC) techniques.
/// Provides enhanced melee combat capabilities including combos and special techniques.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class CQCComponent : Component
{
    /// <summary>
    /// Damage multiplier applied to melee attacks while using CQC.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField, AutoNetworkedField]
    public float DamageMultiplier = 1.2f;

    /// <summary>
    /// Attack rate multiplier for CQC attacks.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField, AutoNetworkedField]
    public float AttackRateMultiplier = 1.15f;

    /// <summary>
    /// Current combo count. Resets after ComboTimeout expires.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField, AutoNetworkedField]
    public int ComboCount = 0;

    /// <summary>
    /// Maximum combo count before reset.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField]
    public int MaxComboCount = 5;

    /// <summary>
    /// Time in seconds before combo resets if no attacks are made.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField]
    public float ComboTimeout = 5f;

    /// <summary>
    /// Time when the last attack was made for combo tracking.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoNetworkedField]
    public TimeSpan LastAttackTime;

    /// <summary>
    /// Whether CQC is currently active.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField, AutoNetworkedField]
    public bool Active = false;

    /// <summary>
    /// Stamina cost per CQC attack.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField]
    public float StaminaCostPerAttack = 5f;

    /// <summary>
    /// Bonus damage added per combo hit.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField]
    public float ComboBonusDamagePerHit = 2f;

    /// <summary>
    /// Sound to play on successful CQC hit.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField]
    public SoundSpecifier? HitSound = new SoundPathSpecifier("/Audio/Weapons/punch1.ogg");

    /// <summary>
    /// Sound to play when achieving a combo.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField]
    public SoundSpecifier? ComboSound = null; // Disabled for now

    /// <summary>
    /// Whether this CQC user can perform takedowns (special attack at high combo).
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField]
    public bool CanPerformTakedowns = true;

    /// <summary>
    /// Combo count required to perform a takedown.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField]
    public int TakedownComboRequirement = 4;

    /// <summary>
    /// Additional stun duration applied on takedown attacks.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField]
    public float TakedownStunDuration = 2f;

    /// <summary>
    /// List tracking the sequence of attack types for combo detection.
    /// Maximum of 5 entries (matches MaxComboCount).
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField, AutoNetworkedField]
    public List<CQCAttackType> AttackSequence = new();

    /// <summary>
    /// Current combo technique text to display.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField, AutoNetworkedField]
    public string ComboText = string.Empty;

    /// <summary>
    /// Time window to counter enemy CQC techniques.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField]
    public float CounterWindow = 1.5f;

    /// <summary>
    /// Last time a technique was used against this entity (for countering).
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoNetworkedField]
    public TimeSpan LastTechniqueTargetTime;

    /// <summary>
    /// Entity currently being choked by this CQC user.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField, AutoNetworkedField]
    public EntityUid? ChokingTarget;

    /// <summary>
    /// Time when choking started.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoNetworkedField]
    public TimeSpan ChokeStartTime;

    /// <summary>
    /// Asphyxiation damage per second while choking.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField]
    public float ChokeDamagePerSecond = 5f;

    /// <summary>
    /// Blunt damage per second while choking.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField]
    public float ChokeBluntDamagePerSecond = 2f;

    /// <summary>
    /// How often to apply choke damage (in seconds).
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField]
    public float ChokeDamageInterval = 1f;

    /// <summary>
    /// Whether the next disarm should succeed (for combo techniques).
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField, AutoNetworkedField]
    public bool GuaranteeNextDisarm = false;

    /// <summary>
    /// Last entity that was pulled (for throw detection).
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField, AutoNetworkedField]
    public EntityUid? LastPulledEntity;

    /// <summary>
    /// Entity we're currently building combo against.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite), DataField, AutoNetworkedField]
    public EntityUid? ComboTarget;

    /// <summary>
    /// Flag to indicate a throw is pending and the next disarm should be cancelled.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public bool ThrowPending = false;
}
