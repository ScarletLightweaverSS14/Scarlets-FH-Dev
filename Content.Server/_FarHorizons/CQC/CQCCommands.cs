using Content.Server.Administration;
using Content.Shared._FarHorizons.CQC;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._FarHorizons.CQC;

[AdminCommand(AdminFlags.Fun)]
public sealed class AddCQCCommand : IConsoleCommand
{
    public string Command => "addcqc";
    public string Description => "Adds CQC (Close Quarters Combat) component to an entity.";
    public string Help => "addcqc <entity uid>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError("Usage: addcqc <entity uid>");
            return;
        }

        if (!NetEntity.TryParse(args[0], out var netEntity))
        {
            shell.WriteError("Invalid entity uid.");
            return;
        }

        var entityManager = IoCManager.Resolve<IEntityManager>();
        var entity = entityManager.GetEntity(netEntity);

        if (!entityManager.EntityExists(entity))
        {
            shell.WriteError("Entity does not exist.");
            return;
        }

        if (entityManager.HasComponent<CQCComponent>(entity))
        {
            shell.WriteLine("Entity already has CQC component.");
            return;
        }

        var comp = entityManager.AddComponent<CQCComponent>(entity);
        comp.Active = true;
        entityManager.Dirty(entity, comp);

        shell.WriteLine($"Added CQC component to entity {entity}.");
    }
}

[AdminCommand(AdminFlags.Fun)]
public sealed class RemoveCQCCommand : IConsoleCommand
{
    public string Command => "removecqc";
    public string Description => "Removes CQC component from an entity.";
    public string Help => "removecqc <entity uid>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1)
        {
            shell.WriteError("Usage: removecqc <entity uid>");
            return;
        }

        if (!NetEntity.TryParse(args[0], out var netEntity))
        {
            shell.WriteError("Invalid entity uid.");
            return;
        }

        var entityManager = IoCManager.Resolve<IEntityManager>();
        var entity = entityManager.GetEntity(netEntity);

        if (!entityManager.EntityExists(entity))
        {
            shell.WriteError("Entity does not exist.");
            return;
        }

        if (!entityManager.RemoveComponent<CQCComponent>(entity))
        {
            shell.WriteError("Entity does not have CQC component.");
            return;
        }

        shell.WriteLine($"Removed CQC component from entity {entity}.");
    }
}
