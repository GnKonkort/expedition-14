using Content.Server.Administration.UI;
using Content.Server.EUI;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server.Administration.Commands;

[AdminCommand(AdminFlags.Fun)]
public sealed class NpcEditorCommand : LocalizedEntityCommands
{
    [Dependency] private readonly EuiManager _eui = default!;

    public override string Command => "npceditor";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player is not { } player)
        {
            shell.WriteError(Loc.GetString("shell-cannot-run-command-from-server"));
            return;
        }

        NetEntity? target = null;
        if (args.Length >= 1 && NetEntity.TryParse(args[0], out var parsed))
            target = parsed;

        _eui.OpenEui(new NpcEditorEui(target), player);
    }
}
