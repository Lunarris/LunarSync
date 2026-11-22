using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace Sync.Plugin.Interop;

public sealed class PairContextMenu : IDisposable
{
    private readonly IContextMenu menu;
    private readonly Plugin plugin;

    public PairContextMenu(IContextMenu menu, Plugin plugin)
    {
        this.menu = menu;
        this.plugin = plugin;
        menu.OnMenuOpened += OnMenuOpened;
    }

    public void Dispose() => menu.OnMenuOpened -= OnMenuOpened;

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (args.MenuType != ContextMenuType.Default || args.Target is not MenuTargetDefault target)
            return;
        var contentId = ContentIdOf(target);
        if (contentId == 0 || plugin.PairSync.UidFor(contentId) is not { } uid)
            return;
        var name = plugin.Sync.Pairs.FirstOrDefault(p => p.Uid == uid) is { } pair ? plugin.Configuration.NameOf(pair) : "Pair";

        args.AddMenuItem(new MenuItem
        {
            Name = "Lunar Sync",
            PrefixChar = 'L',
            IsSubmenu = true,
            OnClicked = clicked => clicked.OpenSubmenu($"Lunar Sync: {name}", Items(uid, name)),
        });
    }

    private List<MenuItem> Items(string uid, string name)
    {
        var config = plugin.Configuration;
        var paused = config.PausedPairs.Contains(uid);
        return
        [
            new MenuItem
            {
                Name = paused ? "Resume" : "Pause",
                PrefixChar = 'L',
                OnClicked = _ => plugin.SetPaused(uid, !paused),
            },
            new MenuItem
            {
                Name = "Re-apply mods",
                PrefixChar = 'L',
                IsEnabled = !paused && plugin.Configuration.ApplyPairs,
                OnClicked = _ => plugin.PairSync.Reapply(uid),
            },
            new MenuItem
            {
                Name = "Change permissions",
                PrefixChar = 'L',
                OnClicked = _ => plugin.OpenPermissions(uid, name),
            },
            new MenuItem
            {
                Name = "Show in Lunar Sync",
                PrefixChar = 'L',
                OnClicked = _ => plugin.ShowPair(uid),
            },
        ];
    }

    private static unsafe ulong ContentIdOf(MenuTargetDefault target)
    {
        if (target.TargetContentId != 0)
            return target.TargetContentId;
        return target.TargetObject is ICharacter character && character.Address != nint.Zero
            ? ((Character*)character.Address)->ContentId
            : 0;
    }
}
