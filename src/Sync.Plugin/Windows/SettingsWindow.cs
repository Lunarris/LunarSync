using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Sync.Plugin.Windows;

public sealed class SettingsWindow : ThemedWindow
{
    private static readonly string[] Tabs = ["Server", "Syncing", "Storage", "Key", "Theme", "About"];

    private readonly Plugin plugin;
    private readonly FileDialogManager fileDialog = new();
    private int tab;

    private bool useCustom;
    private string serverUrl = string.Empty;
    private bool autoConnect;
    private bool autoNickname;
    private bool useTheme;
    private ThemeScheme themeScheme;
    private string cacheFolder = string.Empty;
    private float cacheGigabytes;

    private string message = string.Empty;
    private bool messageIsError;

    public SettingsWindow(Plugin plugin) : base("Lunar Sync Settings###LunarSyncSettings")
    {
        this.plugin = plugin;
        Flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
        Size = new Vector2(600, 460);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void OnOpen()
    {
        Load();
        message = string.Empty;
    }

    private void Load()
    {
        var config = plugin.Configuration;
        useCustom = config.UseCustomServer;
        serverUrl = config.ServerUrl;
        autoConnect = config.AutoConnect;
        autoNickname = config.AutoNicknameFromCharacter;
        useTheme = config.UseTheme;
        themeScheme = config.ThemeScheme;
        cacheFolder = plugin.CacheFolder;
        cacheGigabytes = (float)config.MaxCacheGigabytes;
    }

    private bool Changed()
    {
        var config = plugin.Configuration;
        return useCustom != config.UseCustomServer
            || serverUrl.Trim() != config.ServerUrl
            || autoConnect != config.AutoConnect
            || autoNickname != config.AutoNicknameFromCharacter
            || useTheme != config.UseTheme
            || themeScheme != config.ThemeScheme
            || !string.Equals(Path.TrimEndingDirectorySeparator(cacheFolder.Trim()), Path.TrimEndingDirectorySeparator(plugin.CacheFolder), StringComparison.OrdinalIgnoreCase)
            || Math.Abs(cacheGigabytes - config.MaxCacheGigabytes) > 0.01;
    }

    protected override void DrawContents()
    {
        var style = ImGui.GetStyle();
        var bottom = ImGui.GetFrameHeight() + style.ItemSpacing.Y * 3 + (message.Length > 0 ? ImGui.GetTextLineHeightWithSpacing() : 0);
        var height = Math.Max(ImGui.GetFrameHeight() * 4, ImGui.GetContentRegionAvail().Y - bottom);

        using (var tabs = ImRaii.Child("##tabs", new Vector2(110 * ImGuiHelpers.GlobalScale, height), true))
        {
            if (tabs.Success)
            {
                for (var i = 0; i < Tabs.Length; i++)
                {
                    if (ImGui.Selectable($"{Tabs[i]}###tab{i}", tab == i))
                        tab = i;
                }
            }
        }
        ImGui.SameLine();
        using (var content = ImRaii.Child("##content", new Vector2(0, height), false))
        {
            if (content.Success)
            {
                switch (tab)
                {
                    case 0: DrawServer(); break;
                    case 1: DrawSyncing(); break;
                    case 2: DrawStorage(); break;
                    case 3: DrawKey(); break;
                    case 4: DrawTheme(); break;
                    default: DrawAbout(); break;
                }
            }
        }

        if (message.Length > 0)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, messageIsError ? Ui.Red : Ui.Grey))
                ImGui.TextUnformatted(message);
            Ui.Tooltip(message);
        }
        ImGui.Separator();

        var changed = Changed();
        using (ImRaii.Disabled(!changed))
        {
            if (ImGui.Button("Save"))
                Save();
            ImGui.SameLine();
            if (ImGui.Button("Save and close") && Save())
                IsOpen = false;
            ImGui.SameLine();
            if (ImGui.Button("Discard"))
            {
                Load();
                SetMessage(false, "Changes discarded.");
            }
        }
        fileDialog.Draw();
    }

    private void DrawServer()
    {
        ImGui.TextUnformatted("Server");
        if (ImGui.RadioButton($"Lunar Sync server ({new Uri(Configuration.DefaultServerUrl).Host})", !useCustom))
            useCustom = false;
        if (ImGui.RadioButton("Custom server", useCustom))
            useCustom = true;
        using (ImRaii.Disabled(!useCustom))
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##server", "https://sync.example.net", ref serverUrl, 256);
        }
        Ui.HelpText(useCustom
            ? "Only use a server run by someone you trust. It stores the mod files you share."
            : "The default server. Changing servers reconnects you; your pairs are per server.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        Ui.OptionCheckbox("Connect automatically when the game starts", ref autoConnect,
            "Lunar Sync connects once a character is logged in, and disconnects when you log out.");
    }

    private void DrawSyncing()
    {
        var config = plugin.Configuration;
        ImGui.TextUnformatted("Load from all pairs");
        foreach (var (filter, name, types) in Apply.SyncFilters.All)
        {
            var allowed = (config.BlockedForAll & filter) == 0;
            if (ImGui.Checkbox($"{name}##all-{filter}", ref allowed))
                plugin.SetBlocked(null, filter, !allowed);
            ImGui.SameLine();
            ImGui.TextDisabled(types);
        }
        Ui.HelpText("Check the options you'd like synced. This is global, regardless of each pair's settings. To limit it for one pair, edit their permissions from the main window.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Paused pairs");
        Ui.HelpText(config.PausedPairs.Count == 0
            ? "None. Pause someone with the pause button on their row in the main window."
            : $"{config.PausedPairs.Count} pair(s) paused. Resume them from their row in the main window.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawBlockedFiles(config);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        Ui.OptionCheckbox("Nickname pairs with their character name", ref autoNickname,
            "The first time a pair's character is near you, their character name becomes their nickname, if you haven't given them one. It only happens once per pair, so you can change or clear it afterwards. Nicknames stay on your PC and are never sent to the server.");
    }

    private void DrawBlockedFiles(Configuration config)
    {
        ImGui.TextUnformatted("Blocked files");
        if (config.BlockedFiles.Count == 0)
        {
            Ui.HelpText("Nothing blocked. If your game ever crashes while a pair's new animations, effects or models are loading, those files are blocked here so it can't happen twice.");
            ImGui.Spacing();
            return;
        }
        Ui.HelpText("Your game closed while these were loading. They may not be to blame, so unblock any you want to try again.");
        string? unblock = null;
        foreach (var (hash, file) in config.BlockedFiles.OrderByDescending(kv => kv.Value.BlockedAt))
        {
            if (ImGui.SmallButton($"Unblock##{hash}"))
                unblock = hash;
            ImGui.SameLine();
            ImGui.TextUnformatted(file.GamePath);
            Ui.Tooltip($"From {file.From}, blocked {file.BlockedAt:g}");
        }
        if (unblock is not null && plugin.CrashGuard.Unblock(unblock))
            plugin.PairSync.ReapplyAll();
        ImGui.Spacing();
    }

    private static readonly (string Name, string[] InternalNames)[] SyncedPlugins =
    [
        ("Penumbra", ["Penumbra"]),
        ("Glamourer", ["Glamourer"]),
        ("Customize+", ["CustomizePlus"]),
        ("Simple Heels", ["SimpleHeels"]),
        ("Honorific", ["Honorific"]),
        ("Moodles", ["Moodles", "Loci"]),
        ("Pet Nicknames", ["PetRenamer"]),
    ];

    private void DrawStorage()
    {
        var cache = plugin.Cache;
        ImGui.TextUnformatted("Downloaded files");
        Ui.HelpText(cache.Scanning
            ? "Reading the folder..."
            : $"{Ui.Size(cache.TotalBytes)} of {Ui.Size(cache.MaxBytes)} used, {cache.Count:N0} files.");
        if (cache.Error is { } error)
        {
            using (ImRaii.PushColor(ImGuiCol.Text, Ui.Red))
                ImGui.TextWrapped(error);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Folder");
        var browse = "Browse...";
        ImGui.SetNextItemWidth(-(ImGui.CalcTextSize(browse).X + ImGui.GetStyle().FramePadding.X * 2 + ImGui.GetStyle().ItemSpacing.X));
        ImGui.InputText("##cacheFolder", ref cacheFolder, 260);
        ImGui.SameLine();
        if (ImGui.Button(browse))
        {
            fileDialog.OpenFolderDialog("Choose a folder for downloaded files", (ok, path) =>
            {
                if (ok)
                    cacheFolder = path;
            }, Directory.Exists(cacheFolder) ? cacheFolder : null);
        }
        if (!string.Equals(Path.TrimEndingDirectorySeparator(cacheFolder.Trim()), Path.TrimEndingDirectorySeparator(plugin.CacheFolder), StringComparison.OrdinalIgnoreCase))
            Ui.HelpText("Files already downloaded stay in the old folder; delete them yourself if you like.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextUnformatted("Size limit");
        ImGui.SetNextItemWidth(-1);
        ImGui.SliderFloat("##cacheLimit", ref cacheGigabytes, 1, 200, "%.0f GB");
        Ui.HelpText("Max storage allotted for the sync service. Oldest files not currently in use are deleted first.");
        ImGui.Spacing();

        using (ImRaii.Disabled(!ImGui.GetIO().KeyCtrl))
        {
            if (ImGui.Button("Clear unused files"))
            {
                var removed = cache.Clear(plugin.PairSync.InUse());
                SetMessage(false, $"Deleted {removed} downloaded file(s) no current pair uses.");
            }
        }
        Ui.Tooltip("Hold Ctrl and click. Deletes downloaded files no online pair is using right now.");
    }

    private void DrawKey()
    {
        var sync = plugin.Sync;
        ImGui.TextUnformatted("Your key");
        Ui.HelpText("Your connection key verifies who you are without exposing character information. It is unique to you and is used for all your characters. Keep this key private.");
        ImGui.Spacing();

        ImGui.TextUnformatted("UID");
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, Ui.Accent))
            ImGui.TextUnformatted(sync.Identity.Uid);
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy##uid"))
            ImGui.SetClipboardText(sync.Identity.Uid);
        ImGui.Spacing();

        if (ImGui.Button("Export key..."))
        {
            fileDialog.SaveFileDialog("Export Lunar Sync key", ".key", "lunarsync-identity", ".key", (ok, path) =>
            {
                if (ok)
                    SetMessage(!sync.ExportKey(path, out var text), text);
            });
        }
        Ui.Tooltip("Save a backup of your key. Applies right away.");
        ImGui.SameLine();
        using (ImRaii.Disabled(!ImGui.GetIO().KeyCtrl))
        {
            if (ImGui.Button("Import key..."))
            {
                fileDialog.OpenFileDialog("Import Lunar Sync key", ".key,.*", (ok, path) =>
                {
                    if (ok)
                        SetMessage(!sync.ImportKey(path, out var text), text);
                });
            }
        }
        Ui.Tooltip("Hold Ctrl and click. This replaces your current key, so export it first if you want to keep it.");
    }

    public (bool UseTheme, ThemeScheme Scheme)? ThemePreview => IsOpen ? (useTheme, themeScheme) : null;

    private void DrawTheme()
    {
        Ui.OptionCheckbox("Use dark theme", ref useTheme, "Off uses the standard Dalamud style. Changes preview right away; Save keeps them.");

        using var disabled = ImRaii.Disabled(!useTheme);
        ImGui.SetNextItemWidth(150 * ImGuiHelpers.GlobalScale);
        using var combo = ImRaii.Combo("Color scheme", Theme.SchemeName(themeScheme));
        if (!combo.Success)
            return;
        foreach (var scheme in Enum.GetValues<ThemeScheme>())
        {
            if (ImGui.Selectable(Theme.SchemeName(scheme), scheme == themeScheme))
                themeScheme = scheme;
        }
    }

    private void DrawAbout()
    {
        ImGui.TextUnformatted($"Lunar Sync {Plugin.Build}");
        Ui.HelpText("Sync's mods and other plugins.");
        if (ImGui.Button("Run first-time setup again"))
            plugin.OpenSetup();
        Ui.Tooltip("Choose the download folder again. Your key and pairs stay.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextUnformatted("What gets synced");
        var installed = Plugin.PluginInterface.InstalledPlugins.ToList();
        foreach (var (name, internalNames) in SyncedPlugins)
        {
            var matches = installed.Where(p => internalNames.Contains(p.InternalName, StringComparer.OrdinalIgnoreCase)).ToList();
            var found = matches.FirstOrDefault(p => p.IsLoaded) ?? matches.FirstOrDefault();
            var loaded = found?.IsLoaded == true;
            using (ImRaii.PushColor(ImGuiCol.Text, loaded ? Ui.Green : Ui.Red))
                ImGui.TextUnformatted(name);
            Ui.Tooltip(loaded ? "Installed and enabled" : found is null ? "Not installed" : "Installed but disabled");
        }

    }

    private bool Save()
    {
        var config = plugin.Configuration;
        if (useCustom && serverUrl.Trim().Length == 0)
        {
            SetMessage(true, "Enter the custom server's address, or choose the Lunar Sync server.");
            tab = 0;
            return false;
        }

        var folder = Path.TrimEndingDirectorySeparator(cacheFolder.Trim());
        var folderChanged = !string.Equals(folder, Path.TrimEndingDirectorySeparator(plugin.CacheFolder), StringComparison.OrdinalIgnoreCase);
        if (folderChanged)
        {
            try
            {
                if (!Path.IsPathFullyQualified(folder))
                    throw new IOException("Use a full path, such as D:\\LunarSync.");
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                SetMessage(true, $"That folder can't be used: {ex.Message}");
                tab = 2;
                return false;
            }
        }

        var serverBefore = config.EffectiveServerUrl();
        config.UseCustomServer = useCustom;
        config.ServerUrl = serverUrl.Trim();
        config.AutoConnect = autoConnect;
        config.AutoNicknameFromCharacter = autoNickname;
        config.UseTheme = useTheme;
        config.ThemeScheme = themeScheme;
        config.CacheFolder = folder;
        config.MaxCacheGigabytes = Math.Round(cacheGigabytes);
        config.Save();

        plugin.Cache.Configure(plugin.CacheFolder, config.MaxCacheGigabytes);
        if (serverBefore != config.EffectiveServerUrl() && plugin.Sync.IsRunning)
            plugin.Sync.Restart();
        Load();
        SetMessage(false, "Settings saved.");
        return true;
    }

    private void SetMessage(bool isError, string text)
    {
        messageIsError = isError;
        message = text;
    }
}
