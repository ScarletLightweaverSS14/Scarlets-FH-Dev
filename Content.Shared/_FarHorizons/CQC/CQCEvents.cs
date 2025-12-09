using Content.Shared.Damage;

namespace Content.Shared._FarHorizons.CQC;

/// <summary>
/// CQC combo techniques that can be executed.
/// </summary>
public enum CQCTechnique
{
    None,
    DB_Sweep,    // Shove + Attack: Stun + 20% stamina + 15 blunt
    DB_Uppercut, // Attack + Attack: 35 blunt (or kick if target is down)
    DB_Tackle,   // Shove + Shove: Chest kick - 4s stun, 25 blunt, knockdown
    DB_Choke     // Drag + Shove: Chokehold with DoT damage
}

/// <summary>
/// Raised when a CQC attack is performed.
/// </summary>
[ByRefEvent]
public record struct CQCAttackEvent(EntityUid User, EntityUid Target, int ComboCount, bool IsTakedown);

/// <summary>
/// Raised when a combo is achieved or broken.
/// </summary>
[ByRefEvent]
public record struct CQCComboEvent(EntityUid User, int ComboCount, bool ComboBroken = false);

/// <summary>
/// Raised when checking if a CQC technique can be used.
/// </summary>
[ByRefEvent]
public record struct CQCTechniqueAttemptEvent(EntityUid User, EntityUid Target, bool Cancelled = false, string? Message = null);

/// <summary>
/// Raised to modify CQC damage before it's applied.
/// </summary>
[ByRefEvent]
public record struct CQCDamageModifierEvent(EntityUid User, EntityUid Weapon, DamageSpecifier Damage, int ComboCount, float Multiplier = 1.0f);

/// <summary>
/// Raised when a CQC technique is executed.
/// </summary>
[ByRefEvent]
public record struct CQCTechniqueExecutedEvent(EntityUid User, EntityUid Target, CQCTechnique Technique, bool WasCountered = false);

/// <summary>
/// Raised when CQC mode is toggled on or off.
/// </summary>
[ByRefEvent]
public record struct CQCToggleEvent(EntityUid User, bool Active);
