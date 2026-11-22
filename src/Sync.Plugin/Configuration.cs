using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Sync.Protocol;

namespace Sync.Plugin;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public const string DefaultServerUrl = "https://sync.xiv-archive.com";

    public int Version { get; set; } = 2;

    public bool SetupComplete { get; set; } = false;

    public bool UseCustomServer { get; set; } = false;

    public string ServerUrl { get; set; } = string.Empty;

    public string EffectiveServerUrl() => UseCustomServer ? ServerUrl.Trim() : DefaultServerUrl;

    public bool Migrate(bool hasKey, string legacyCacheFolder)
    {
        if (Version >= 2)
            return false;
        if (Version < 1)
            UseCustomServer = ServerUrl.Trim().Length > 0
                && !string.Equals(ServerUrl.Trim().TrimEnd('/'), DefaultServerUrl, StringComparison.OrdinalIgnoreCase);
        if (hasKey)
        {
            SetupComplete = true;
            if (CacheFolder.Length == 0)
                CacheFolder = legacyCacheFolder;
        }
        Version = 2;
        return true;
    }

    public bool AutoConnect { get; set; } = false;

    public bool ApplyPairs { get; set; } = true;

    public string CacheFolder { get; set; } = string.Empty;

    public double MaxCacheGigabytes { get; set; } = 20;

    public HashSet<string> PausedPairs { get; set; } = new(StringComparer.Ordinal);

    public Apply.SyncFilter BlockedForAll { get; set; } = Apply.SyncFilter.None;

    public Dictionary<string, Apply.SyncFilter> BlockedByPair { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> PairNicknames { get; set; } = new(StringComparer.Ordinal);

    public bool AutoNicknameFromCharacter { get; set; }

    public Dictionary<string, Apply.BlockedFile> BlockedFiles { get; set; } = new(StringComparer.Ordinal);

    public bool UseTheme { get; set; }

    public Windows.ThemeScheme ThemeScheme { get; set; } = Windows.ThemeScheme.Midnight;

    public HashSet<string> AutoNicknamed { get; set; } = new(StringComparer.Ordinal);

    public string? NicknameOf(string uid) => PairNicknames.TryGetValue(uid, out var nickname) ? nickname : null;

    public string NameOf(PairEntry pair) => NicknameOf(pair.Uid) ?? pair.DisplayName;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
