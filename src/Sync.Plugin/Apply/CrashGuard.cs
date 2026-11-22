using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sync.Protocol;

namespace Sync.Plugin.Apply;

public sealed class BlockedFile
{
    public string GamePath { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public DateTime BlockedAt { get; set; }
}

public sealed class CrashGuard : IDisposable
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    private static readonly string[] RiskyExtensions = [".pap", ".tmb", ".avfx", ".mdl"];

    private readonly string markerPath;
    private readonly Configuration configuration;
    private readonly object gate = new();
    private readonly HashSet<string> survived = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BlockedFile> pending = new(StringComparer.Ordinal);
    private DateTime clearAt;

    public CrashGuard(string markerPath, Configuration configuration)
    {
        this.markerPath = markerPath;
        this.configuration = configuration;
        Recovered = Recover();
    }

    public int Recovered { get; }

    public static bool IsRisky(string gamePath) =>
        RiskyExtensions.Any(e => gamePath.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    public bool IsBlocked(string hash)
    {
        lock (gate)
            return configuration.BlockedFiles.ContainsKey(hash);
    }

    public void Arm(IEnumerable<(string Hash, string GamePath)> files, string from)
    {
        lock (gate)
        {
            var added = false;
            foreach (var (hash, gamePath) in files)
            {
                if (!IsRisky(gamePath) || survived.Contains(hash) || pending.ContainsKey(hash) || configuration.BlockedFiles.ContainsKey(hash))
                    continue;
                pending[hash] = new BlockedFile { GamePath = gamePath, From = from };
                added = true;
            }
            if (!added)
                return;
            clearAt = DateTime.UtcNow + Window;
            WriteMarker();
        }
    }

    public void Tick()
    {
        lock (gate)
        {
            if (pending.Count == 0 || DateTime.UtcNow < clearAt)
                return;
            survived.UnionWith(pending.Keys);
            pending.Clear();
            DeleteMarker();
        }
    }

    public bool Unblock(string hash)
    {
        lock (gate)
        {
            if (!configuration.BlockedFiles.Remove(hash))
                return false;
        }
        configuration.Save();
        return true;
    }

    public void Dispose()
    {
        lock (gate)
        {
            pending.Clear();
            DeleteMarker();
        }
    }

    private int Recover()
    {
        string[] lines;
        try
        {
            if (!File.Exists(markerPath))
                return 0;
            lines = File.ReadAllLines(markerPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Plugin.Log.Error(ex, "Reading the crash guard marker failed");
            return 0;
        }

        var now = DateTime.Now;
        var count = 0;
        foreach (var line in lines)
        {
            var parts = line.Split('\t');
            if (parts.Length < 3 || !FileTransferLimits.IsHash(parts[0]))
                continue;
            if (configuration.BlockedFiles.TryAdd(parts[0], new BlockedFile { GamePath = parts[1], From = parts[2], BlockedAt = now }))
                count++;
        }
        if (count > 0)
            configuration.Save();
        DeleteMarker();
        return count;
    }

    private void WriteMarker()
    {
        try
        {
            File.WriteAllLines(markerPath, pending.Select(kv => $"{kv.Key}\t{Clean(kv.Value.GamePath)}\t{Clean(kv.Value.From)}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Plugin.Log.Error(ex, "Writing the crash guard marker failed");
        }
    }

    private void DeleteMarker()
    {
        try
        {
            if (File.Exists(markerPath))
                File.Delete(markerPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Plugin.Log.Error(ex, "Deleting the crash guard marker failed");
        }
    }

    private static string Clean(string value) => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
