using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Sync.Plugin.Apply;

namespace Sync.Plugin.Windows;

public sealed class PermissionWindow : ThemedWindow
{
    private readonly Plugin plugin;
    private readonly Dictionary<SyncFilter, bool> allowed = [];
    private string uid = string.Empty;
    private string name = string.Empty;
    private bool placeAtMouse;

    public PermissionWindow(Plugin plugin) : base("Change Permission###LunarSyncPermission")
    {
        this.plugin = plugin;
        Flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.AlwaysAutoResize
            | ImGuiWindowFlags.NoSavedSettings;
        PositionCondition = ImGuiCond.Appearing;
    }

    public void Open(string pairUid, string pairName)
    {
        uid = pairUid;
        name = pairName;
        var blocked = plugin.Configuration.BlockedByPair.GetValueOrDefault(uid);
        foreach (var (filter, _, _) in SyncFilters.All)
            allowed[filter] = (blocked & filter) == 0;
        placeAtMouse = true;
        IsOpen = true;
    }

    public override void PreDraw()
    {
        base.PreDraw();
        if (!placeAtMouse)
            return;
        Position = ImGui.GetMousePos();
        placeAtMouse = false;
    }

    protected override void DrawContents()
    {
        var config = plugin.Configuration;
        foreach (var (filter, label, _) in SyncFilters.All)
        {
            var offForAll = (config.BlockedForAll & filter) != 0;
            var value = !offForAll && allowed.GetValueOrDefault(filter, true);
            using (ImRaii.Disabled(offForAll))
            {
                if (ImGui.Checkbox($"{label}##perm-{filter}", ref value))
                    allowed[filter] = value;
            }
            if (offForAll)
                Ui.Tooltip($"{label} are off for everyone in Settings > Syncing.");
        }

        ImGui.Spacing();
        ImGui.Separator();
        if (ImGui.Button("Save"))
        {
            var blocked = SyncFilter.None;
            foreach (var (filter, _, _) in SyncFilters.All)
            {
                if (!allowed.GetValueOrDefault(filter, true))
                    blocked |= filter;
            }
            plugin.SetPairBlocked(uid, blocked);
            IsOpen = false;
        }
        ImGui.SameLine();
        if (ImGui.Button("Discard"))
            IsOpen = false;
    }
}
