using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace Sync.Plugin.Windows;

public static class Ui
{
    public static readonly Vector4 Green = new(0.35f, 0.85f, 0.45f, 1f);
    public static readonly Vector4 Yellow = new(1f, 0.75f, 0.2f, 1f);
    public static readonly Vector4 Red = new(1f, 0.4f, 0.4f, 1f);
    public static readonly Vector4 Grey = new(0.6f, 0.6f, 0.6f, 1f);
    public static readonly Vector4 Accent = new(0.55f, 0.75f, 1f, 1f);

    public static void Dot(Vector4 color)
    {
        var size = ImGui.GetTextLineHeight();
        var pos = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddCircleFilled(pos + new Vector2(size / 2, size / 2), size * 0.28f, ImGui.GetColorU32(color));
        ImGui.Dummy(new Vector2(size, size));
    }

    public static void Tooltip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(text);
    }

    public static float IconButtonWidth(FontAwesomeIcon icon)
    {
        float width;
        using (ImRaii.PushFont(UiBuilder.IconFont))
            width = ImGui.CalcTextSize(icon.ToIconString()).X;
        return width + ImGui.GetStyle().FramePadding.X * 2;
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => $"{bytes} B",
    };

    public static void Centered(string text, Vector4 color, float scale)
    {
        ImGui.SetWindowFontScale(scale);
        var width = ImGui.CalcTextSize(text).X;
        var avail = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + System.Math.Max(0, (avail - width) / 2));
        using (ImRaii.PushColor(ImGuiCol.Text, color))
            ImGui.TextUnformatted(text);
        ImGui.SetWindowFontScale(1f);
    }

    public static void Icon(FontAwesomeIcon icon, Vector4 color)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
        using (ImRaii.PushColor(ImGuiCol.Text, color))
            ImGui.TextUnformatted(icon.ToIconString());
    }

    public static void HelpText(string text)
    {
        using (ImRaii.TextWrapPos(0f))
        using (ImRaii.PushColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]))
            ImGui.TextUnformatted(text);
    }

    public static bool OptionCheckbox(string label, ref bool value, string description)
    {
        var changed = ImGui.Checkbox(label, ref value);
        HelpText(description);
        ImGui.Spacing();
        return changed;
    }

    public static void Section(string title)
    {
        ImGui.Spacing();
        using (ImRaii.PushColor(ImGuiCol.Text, Accent))
            ImGui.TextUnformatted(title);
        ImGui.Separator();
    }
}
