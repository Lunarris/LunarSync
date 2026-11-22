using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Sync.Plugin.Collection;
using Sync.Protocol;

namespace Sync.Plugin.Windows;

public sealed class PreviewWindow : ThemedWindow
{
    private readonly Plugin plugin;
    private string filter = string.Empty;

    public PreviewWindow(Plugin plugin) : base("Lunar Sync Preview###LunarSyncPreview")
    {
        this.plugin = plugin;
        Flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoFocusOnAppearing;
        Size = new Vector2(640, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void OnOpen() => plugin.Collector.CollectNow();

    protected override void DrawContents()
    {
        var collector = plugin.Collector;
        var sync = plugin.Sync;
        var snapshot = collector.Latest;

        ImGui.TextWrapped("This is what your online pairs get. The server can see your mod files, but your plugin settings (Glamourer, Customize+ and the rest) are locked so only your pairs can read them.");
        using (ImRaii.Disabled(collector.Busy))
        {
            if (ImGui.Button("Collect now"))
                collector.CollectNow();
        }
        ImGui.SameLine();
        ImGui.TextDisabled(collector.Busy ? "Checking your files..." : collector.Status);
        if (collector.LastTrigger.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"Last change: {collector.LastTrigger}");
        }

        if (sync.Uploading)
        {
            var fraction = sync.UploadTotal == 0 ? 0 : (float)sync.UploadDone / sync.UploadTotal;
            ImGui.ProgressBar(fraction, new Vector2(-1, 0), $"Uploading {sync.UploadDone}/{sync.UploadTotal} files");
        }
        else
        {
            using (ImRaii.PushColor(ImGuiCol.Text, sync.LastPushIsError ? Ui.Red : Ui.Grey))
                ImGui.TextWrapped(sync.LastPush);
        }
        if (collector.MissingPlugins.Count > 0)
            ImGui.TextDisabled($"Not available: {string.Join(", ", collector.MissingPlugins.OrderBy(n => n))}");

        if (snapshot is null)
        {
            ImGui.Separator();
            ImGui.TextDisabled("Nothing collected yet.");
            return;
        }

        Ui.Section("Plugin state");
        DrawState(snapshot.State);
        Ui.Section("Files");
        DrawFiles(snapshot, collector.TransientCount);
    }

    private static void DrawState(CharacterState state)
    {
        using var table = ImRaii.Table("##state", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH);
        if (!table.Success)
            return;
        ImGui.TableSetupColumn("Plugin", ImGuiTableColumnFlags.WidthFixed, 130);
        ImGui.TableSetupColumn("Data", ImGuiTableColumnFlags.WidthStretch);

        Row("Glamourer", PerKind(state.Glamourer));
        Row("Customize+", PerKind(state.CustomizePlus));
        Row("Loci", PerKind(state.Loci));
        Row("Penumbra meta", Size(state.Manipulation));
        Row("Simple Heels", Size(state.Heels));
        Row("Honorific", Size(state.Honorific));
        Row("Moodles", Size(state.Moodles));
        Row("Pet Nicknames", Size(state.PetNames));

        static void Row(string name, string value)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(name);
            ImGui.TableNextColumn();
            if (value.Length == 0)
                ImGui.TextDisabled("none");
            else
                ImGui.TextUnformatted(value);
        }

        static string Size(string value) => value.Length == 0 ? string.Empty : $"{value.Length:N0} chars";

        static string PerKind(Dictionary<SyncObjectKind, string> values) =>
            string.Join(", ", values.Where(kv => kv.Value.Length > 0).OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value.Length:N0} chars"));
    }

    private void DrawFiles(CharacterSnapshot snapshot, int transient)
    {
        var total = snapshot.Files.Sum(kv => kv.Value.Count);
        ImGui.TextUnformatted($"{total} modded items using {snapshot.LocalFiles.Count} files");
        ImGui.SameLine();
        ImGui.TextDisabled($"({transient} emote and effect files seen this session)");
        if (snapshot.SkippedFiles > 0)
            ImGui.TextDisabled($"{snapshot.SkippedFiles} files left out: they couldn't be read, were over 512 MB, aren't a type that can sync, or looked like a program instead of a mod.");
        if (snapshot.DroppedAnimations.Count > 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, Ui.Red))
                ImGui.TextUnformatted($"{snapshot.DroppedAnimations.Count} animations weren't sent. They don't fit your character's skeleton and could crash your pairs' games.");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Join("\n", snapshot.DroppedAnimations.Take(30))
                    + (snapshot.DroppedAnimations.Count > 30 ? $"\n...and {snapshot.DroppedAnimations.Count - 30} more" : string.Empty));
        }
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##filter", "Filter by game path", ref filter, 128);

        using var child = ImRaii.Child("##files", new Vector2(0, 0), false);
        if (!child.Success)
            return;
        foreach (var (kind, files) in snapshot.Files.OrderBy(kv => kv.Key))
        {
            var shown = filter.Length == 0 ? files : files.Where(f => f.GamePaths.Any(g => g.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToList();
            var swaps = shown.Count(f => !string.IsNullOrEmpty(f.FileSwapPath));
            using var node = ImRaii.TreeNode($"{kind}: {shown.Count - swaps} modded, {swaps} swaps###{kind}", ImGuiTreeNodeFlags.DefaultOpen);
            if (!node.Success)
                continue;
            foreach (var file in shown)
            {
                ImGui.TextUnformatted(string.Join(", ", file.GamePaths));
                using (ImRaii.PushIndent())
                    ImGui.TextDisabled(string.IsNullOrEmpty(file.FileSwapPath) ? file.Hash : $"swap to {file.FileSwapPath}");
            }
        }
    }
}
