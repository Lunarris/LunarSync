using System;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Sync.Plugin.Windows;

public sealed class SetupWindow : ThemedWindow
{
    private readonly Plugin plugin;
    private readonly FileDialogManager fileDialog = new();
    private string folder = string.Empty;
    private bool autoConnect = true;
    private string message = string.Empty;
    private bool messageIsError;

    public SetupWindow(Plugin plugin) : base("Lunar Sync Setup###LunarSyncSetup")
    {
        this.plugin = plugin;
        Flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoFocusOnAppearing;
        Size = new Vector2(520, 470);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void OnOpen()
    {
        folder = plugin.Configuration.SetupComplete ? plugin.CacheFolder : string.Empty;
        autoConnect = plugin.Configuration.SetupComplete ? plugin.Configuration.AutoConnect : true;
        message = string.Empty;
    }

    protected override void DrawContents()
    {
        var sync = plugin.Sync;
        Ui.Section("1. Choose a folder for downloaded files");
        ImGui.TextWrapped("Your pairs' mod files are saved here. Pick an empty folder that no other sync plugin uses.");
        if (ImGui.Button("Choose folder..."))
        {
            fileDialog.OpenFolderDialog("Choose a folder for Lunar Sync", (ok, path) =>
            {
                if (ok)
                {
                    folder = path;
                    message = string.Empty;
                }
            });
        }
        ImGui.SameLine();
        if (folder.Length == 0)
            ImGui.TextDisabled("No folder chosen yet.");
        else
            ImGui.TextUnformatted(folder);

        Ui.Section("2. Your key");
        if (sync.HasKey)
        {
            ImGui.TextWrapped("Found your existing key. Lunar Sync will use it.");
        }
        else
        {
            ImGui.TextWrapped("Lunar Sync makes a private key so the server knows it's you. One key works for all your characters, and you can back it up in Settings later.");
            if (ImGui.SmallButton("Import existing key..."))
            {
                fileDialog.OpenFileDialog("Import Lunar Sync key", ".key,.*", (ok, path) =>
                {
                    if (ok)
                        SetMessage(!sync.ImportKey(path, out var text), text);
                });
            }
        }

        Ui.Section("3. Connect");
        var server = plugin.Configuration.EffectiveServerUrl();
        ImGui.TextWrapped($"Server: {(Uri.TryCreate(server, UriKind.Absolute, out var uri) ? uri.Host : server)}. You can choose a different server later in Settings.");
        ImGui.Checkbox("Connect automatically when the game starts", ref autoConnect);

        ImGui.Spacing();
        using (ImRaii.Disabled(folder.Length == 0))
        {
            if (ImGui.Button(sync.HasKey ? "Finish and connect" : "Create my key and connect"))
                Finish();
        }

        if (message.Length > 0)
        {
            ImGui.Spacing();
            using (ImRaii.PushColor(ImGuiCol.Text, messageIsError ? Ui.Red : Ui.Grey))
                ImGui.TextWrapped(message);
        }
        fileDialog.Draw();
    }

    private void Finish()
    {
        var chosen = Path.TrimEndingDirectorySeparator(folder.Trim());
        try
        {
            if (!Path.IsPathFullyQualified(chosen))
                throw new IOException("Choose a folder with the button above.");
            if (string.Equals(Path.GetPathRoot(chosen)?.TrimEnd('\\', '/'), chosen.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Choose or create a folder inside the drive, not the whole drive.");
            Directory.CreateDirectory(chosen);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            SetMessage(true, $"That folder can't be used: {ex.Message}");
            return;
        }

        if (!plugin.Sync.CreateKey(out var keyMessage))
        {
            SetMessage(true, keyMessage);
            return;
        }

        var config = plugin.Configuration;
        config.CacheFolder = chosen;
        config.AutoConnect = autoConnect;
        config.SetupComplete = true;
        config.Save();
        plugin.Cache.Configure(chosen, config.MaxCacheGigabytes);
        plugin.FinishSetup();
    }

    private void SetMessage(bool isError, string text)
    {
        messageIsError = isError;
        message = text;
    }
}
