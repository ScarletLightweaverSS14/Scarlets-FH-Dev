using Content.Client.CombatMode;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Light.Components;
using Robust.Client.GameObjects;
using Robust.Client.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.Maths;

namespace Content.Client._FarHorizons.Light;

/// <summary>
/// System that rotates directional lights (like flashlights) to point in the direction
/// the player is facing when in combat mode.
/// </summary>
public sealed class DirectionalLightRotationSystem : EntitySystem
{
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly CombatModeSystem _combatModeSystem = default!;
    [Dependency] private readonly SharedHandsSystem _handsSystem = default!;
    [Dependency] private readonly TransformSystem _transformSystem = default!;

    private readonly Dictionary<EntityUid, Angle> _originalRotations = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<HandheldLightComponent, ComponentStartup>(OnHandheldLightStartup);
        SubscribeLocalEvent<HandheldLightComponent, ComponentShutdown>(OnHandheldLightShutdown);
    }

    private void OnHandheldLightStartup(EntityUid uid, HandheldLightComponent component, ComponentStartup args)
    {
        // Mark this entity as having a directional light that should rotate with combat mode
        EnsureComp<DirectionalHandheldLightComponent>(uid);
    }

    private void OnHandheldLightShutdown(EntityUid uid, HandheldLightComponent component, ComponentShutdown args)
    {
        // Clean up tracking
        _originalRotations.Remove(uid);
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        // Get the local player entity
        var localEntity = _playerManager.LocalEntity;
        if (localEntity == null)
            return;

        // Check if the player is in combat mode
        if (!_combatModeSystem.IsInCombatMode())
        {
            // If not in combat mode, reset any rotations we applied
            ResetDirectionalLights(localEntity.Value);
            return;
        }

        // Get the player's hands component
        if (!TryComp<HandsComponent>(localEntity.Value, out var hands))
            return;

        // Get the player's facing direction
        var playerRotation = _transformSystem.GetWorldRotation(localEntity.Value);

        // Check all hands for directional lights
        foreach (var handId in hands.Hands.Keys)
        {
            var heldItem = _handsSystem.GetHeldItem((localEntity.Value, hands), handId);
            if (heldItem == null)
                continue;

            // Check if this item has a directional handheld light
            if (!HasComp<DirectionalHandheldLightComponent>(heldItem.Value))
                continue;

            // Check if it has a point light with auto-rotation enabled
            if (!TryComp<PointLightComponent>(heldItem.Value, out var light) || !light.MaskAutoRotate)
                continue;

            // Store original rotation if not already stored
            if (!_originalRotations.ContainsKey(heldItem.Value))
            {
                var itemTransform = Transform(heldItem.Value);
                _originalRotations[heldItem.Value] = itemTransform.LocalRotation;
            }

            // Set the item's rotation to match the player's facing direction
            // Since MaskAutoRotate is true, the light mask will follow the entity rotation
            _transformSystem.SetLocalRotation(heldItem.Value, playerRotation);
        }
    }

    private void ResetDirectionalLights(EntityUid playerUid)
    {
        // Get the player's hands component
        if (!TryComp<HandsComponent>(playerUid, out var hands))
            return;

        // Reset all directional lights to their original rotation
        foreach (var handId in hands.Hands.Keys)
        {
            var heldItem = _handsSystem.GetHeldItem((playerUid, hands), handId);
            if (heldItem == null)
                continue;

            // Check if this item has a directional handheld light
            if (!HasComp<DirectionalHandheldLightComponent>(heldItem.Value))
                continue;

            // Restore original rotation if we have it stored
            if (_originalRotations.TryGetValue(heldItem.Value, out var originalRotation))
            {
                _transformSystem.SetLocalRotation(heldItem.Value, originalRotation);
                _originalRotations.Remove(heldItem.Value);
            }
        }
    }
}

/// <summary>
/// Marker component to identify handheld flashlights with directional lights that should rotate with combat mode.
/// </summary>
[RegisterComponent]
public sealed partial class DirectionalHandheldLightComponent : Component
{
}
