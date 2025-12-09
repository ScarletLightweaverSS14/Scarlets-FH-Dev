using Content.Shared._FarHorizons.CQC;

namespace Content.Server._FarHorizons.CQC;

/// <summary>
/// Server-side system for CQC (Close Quarters Combat).
/// Handles server-specific logic for CQC mechanics.
/// </summary>
public sealed class CQCSystem : SharedCQCSystem
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<CQCComponent, ComponentStartup>(OnComponentStartup);
    }

    private void OnComponentStartup(EntityUid uid, CQCComponent component, ComponentStartup args)
    {
        // Initialize component on server startup if needed
    }
}
