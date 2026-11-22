using Dalamud.Game.Command;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Sync.Plugin.Apply;
using Sync.Plugin.Collection;
using Sync.Plugin.Interop;
using Sync.Plugin.Transfer;
using Sync.Plugin.Windows;

namespace Sync.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IContextMenu ContextMenu { get; private set; } = null!;
    [PluginService] internal static INotificationManager Notifications { get; private set; } = null!;

    private const string CommandName = "/lunarsync";

    public readonly WindowSystem WindowSystem = new("LunarSync");

    public Configuration Configuration { get; }
    public SyncService Sync { get; }
    public CharacterCollector Collector { get; }
    public FileCache Cache { get; }
    public PairSyncManager PairSync { get; }
    public CrashGuard CrashGuard { get; }

    private readonly ChangeEvents changeEvents;
    private readonly PairContextMenu pairMenu;
    private bool wantConnected;
    private readonly HashSet<string> notifiedDropped = new(StringComparer.Ordinal);

    private readonly MainWindow mainWindow;
    private readonly SettingsWindow settingsWindow;
    private readonly PreviewWindow previewWindow;
    private readonly SetupWindow setupWindow;
    private readonly PermissionWindow permissionWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        var configDirectory = PluginInterface.GetPluginConfigDirectory();
        if (Configuration.Migrate(File.Exists(Path.Combine(configDirectory, "identity.key")), Path.Combine(configDirectory, "cache")))
            Configuration.Save();
        Sync = new SyncService(Configuration, PluginInterface.GetPluginConfigDirectory());

        mainWindow = new MainWindow(this);
        settingsWindow = new SettingsWindow(this);
        previewWindow = new PreviewWindow(this);
        setupWindow = new SetupWindow(this);
        permissionWindow = new PermissionWindow(this);
        WindowSystem.AddWindow(permissionWindow);
        WindowSystem.AddWindow(setupWindow);
        WindowSystem.AddWindow(mainWindow);
        WindowSystem.AddWindow(settingsWindow);
        WindowSystem.AddWindow(previewWindow);
        Cache = new FileCache(CacheFolder, Configuration.MaxCacheGigabytes);
        CrashGuard = new CrashGuard(Path.Combine(configDirectory, "crash-guard.txt"), Configuration);
        PairSync = new PairSyncManager(Framework, ObjectTable, new ApplyIpc(PluginInterface), Cache, Configuration, CrashGuard, () => Sync.Connection);
        if (CrashGuard.Recovered > 0)
        {
            Notifications.AddNotification(new Notification
            {
                Title = "Lunar Sync: blocked files that may have crashed your game",
                Content = $"Your game closed while {CrashGuard.Recovered} files from your pairs were loading, so Lunar Sync blocked them. You can unblock them in Settings > Syncing.",
                Type = NotificationType.Warning,
                Minimized = false,
            });
        }
        Sync.DataReceived += PairSync.OnData;
        Sync.PairGone += PairSync.OnGone;
        Sync.AllGone += PairSync.OnAllGone;

        Collector = new CharacterCollector(Framework, ObjectTable, ClientState, new PluginIpc(PluginInterface),
            () => previewWindow.IsOpen || (Sync.IsConnected && Sync.HasOnlinePairs), OnSnapshot,
            new TransientStore(Path.Combine(PluginInterface.GetPluginConfigDirectory(), "transient.json")));
        changeEvents = new ChangeEvents(PluginInterface, Collector.IsOwned, PairSync.IsPairCollection,
            Collector.MarkDirty, Collector.OnResourceResolved);

        pairMenu = new PairContextMenu(ContextMenu, this);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Lunar Sync. \"/lunarsync settings\" opens settings, \"preview\" shows what would sync, \"connect\" and \"disconnect\" control the connection, \"setup\" reopens first-time setup.",
        });

        PluginInterface.UiBuilder.Draw += DrawUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleSettingsUi;

        ClientState.Login += OnLogin;
        ClientState.Logout += OnLogout;
        if (!Configuration.SetupComplete)
            setupWindow.IsOpen = true;
        else if (Configuration.AutoConnect && Configuration.EffectiveServerUrl().Length > 0)
            Connect();
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= DrawUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleSettingsUi;
        CommandManager.RemoveHandler(CommandName);
        ClientState.Login -= OnLogin;
        ClientState.Logout -= OnLogout;
        WindowSystem.RemoveAllWindows();
        pairMenu.Dispose();
        changeEvents.Dispose();
        Collector.Dispose();
        Sync.DataReceived -= PairSync.OnData;
        Sync.PairGone -= PairSync.OnGone;
        Sync.AllGone -= PairSync.OnAllGone;
        Sync.Dispose();
        PairSync.Dispose();
        CrashGuard.Dispose();
    }

    public static string Build { get; } = ReadBuild();

    private void OnSnapshot(CharacterSnapshot snapshot)
    {
        var fresh = snapshot.DroppedAnimations.Where(notifiedDropped.Add).ToList();
        if (fresh.Count > 0)
        {
            Framework.RunOnFrameworkThread(() => Notifications.AddNotification(new Notification
            {
                Title = "Lunar Sync: some animations weren't sent",
                Content = $"{fresh.Count} animations don't fit your character's skeleton and could crash your pairs, so they were held back. Open the sync preview to see which.",
                Type = NotificationType.Warning,
                Minimized = false,
            }));
        }
        Sync.OnSnapshot(snapshot);
    }

    private static string ReadBuild()
    {
        var assembly = typeof(Plugin).Assembly;
        var version = assembly.GetName().Version?.ToString(3) ?? "?";
        return $"{version}";
    }

    public string CacheFolder => Configuration.CacheFolder;

    public void FinishSetup()
    {
        setupWindow.IsOpen = false;
        mainWindow.IsOpen = true;
        Connect();
    }

    public bool WaitingForLogin => wantConnected && !Sync.IsRunning && !ClientState.IsLoggedIn;

    public bool ConnectionWanted => Sync.IsRunning || WaitingForLogin;

    public void Connect()
    {
        if (!Configuration.SetupComplete)
        {
            setupWindow.IsOpen = true;
            return;
        }
        wantConnected = true;
        if (ClientState.IsLoggedIn)
            Sync.Start();
    }

    public void Disconnect()
    {
        wantConnected = false;
        Sync.Stop();
    }

    private void OnLogin()
    {
        if (wantConnected)
            Sync.Start();
    }

    private void OnLogout(int type, int code)
    {
        if (Sync.IsRunning)
            Sync.Stop();
    }

    public void ToggleMainUi()
    {
        if (Configuration.SetupComplete)
            mainWindow.Toggle();
        else
            setupWindow.Toggle();
    }
    public void ToggleSettingsUi() => settingsWindow.Toggle();

    private void DrawUi()
    {
        var (useTheme, scheme) = settingsWindow.ThemePreview ?? (Configuration.UseTheme, Configuration.ThemeScheme);
        Theme.Update(useTheme, scheme);
        WindowSystem.Draw();
    }
    public void OpenSetup() => setupWindow.IsOpen = true;

    public void SetPaused(string uid, bool paused)
    {
        var changed = paused ? Configuration.PausedPairs.Add(uid) : Configuration.PausedPairs.Remove(uid);
        if (changed)
            Configuration.Save();
    }

    public void SetBlocked(string? uid, Apply.SyncFilter filter, bool blocked)
    {
        if (uid is null)
        {
            Configuration.BlockedForAll = blocked ? Configuration.BlockedForAll | filter : Configuration.BlockedForAll & ~filter;
        }
        else
        {
            var current = Configuration.BlockedByPair.GetValueOrDefault(uid);
            var next = blocked ? current | filter : current & ~filter;
            if (next == Apply.SyncFilter.None)
                Configuration.BlockedByPair.Remove(uid);
            else
                Configuration.BlockedByPair[uid] = next;
        }
        Configuration.Save();
        PairSync.FiltersChanged();
    }

    public void SetPairBlocked(string uid, Apply.SyncFilter blocked)
    {
        var current = Configuration.BlockedByPair.GetValueOrDefault(uid);
        if (current == blocked)
            return;
        if (blocked == Apply.SyncFilter.None)
            Configuration.BlockedByPair.Remove(uid);
        else
            Configuration.BlockedByPair[uid] = blocked;
        Configuration.Save();
        PairSync.FiltersChanged();
    }

    public void OpenPermissions(string uid, string name) => permissionWindow.Open(uid, name);

    public void ForgetPair(string uid)
    {
        var changed = Configuration.PausedPairs.Remove(uid) | Configuration.BlockedByPair.Remove(uid)
            | Configuration.PairNicknames.Remove(uid) | Configuration.AutoNicknamed.Remove(uid);
        if (changed)
            Configuration.Save();
    }

    public void SetNickname(string uid, string? nickname)
    {
        nickname = string.IsNullOrWhiteSpace(nickname) ? null : nickname.Trim();
        if (Configuration.NicknameOf(uid) == nickname)
            return;
        if (nickname is null)
            Configuration.PairNicknames.Remove(uid);
        else
            Configuration.PairNicknames[uid] = nickname;
        Configuration.Save();
    }

    public void ShowPair(string uid)
    {
        mainWindow.IsOpen = true;
        mainWindow.Highlight(uid);
    }

    public void TogglePreviewUi() => previewWindow.Toggle();

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "settings":
            case "config":
                ToggleSettingsUi();
                break;
            case "preview":
                TogglePreviewUi();
                break;
            case "setup":
                OpenSetup();
                break;
            case "connect":
                Connect();
                break;
            case "disconnect":
                Disconnect();
                break;
            default:
                ToggleMainUi();
                break;
        }
    }
}
