using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sync.Protocol;

namespace Sync.Plugin.Transfer;

public sealed class FileCache
{
    private readonly ConcurrentDictionary<string, (string Path, long Size)> entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> usedThisSession = new(StringComparer.Ordinal);
    private static readonly TimeSpan StalePart = TimeSpan.FromHours(1);
    private long totalBytes;
    private int scanning;

    public FileCache(string folder, double maxGigabytes)
    {
        Folder = folder;
        MaxBytes = ToBytes(maxGigabytes);
        Rescan();
    }

    public string Folder { get; private set; }
    public long MaxBytes { get; private set; }
    public long TotalBytes => Interlocked.Read(ref totalBytes);
    public int Count => entries.Count;
    public bool Scanning => Volatile.Read(ref scanning) != 0;
    public string? Error { get; private set; }

    public static long ToBytes(double gigabytes) => (long)(Math.Clamp(gigabytes, 1, 1000) * 1024 * 1024 * 1024);

    public void Configure(string folder, double maxGigabytes)
    {
        MaxBytes = ToBytes(maxGigabytes);
        if (Folder.Length > 0 && string.Equals(Path.GetFullPath(folder), Path.GetFullPath(Folder), StringComparison.OrdinalIgnoreCase))
            return;
        Folder = folder;
        Rescan();
    }

    public string? PathFor(string hash)
    {
        if (!entries.TryGetValue(hash, out var entry))
            return null;
        usedThisSession[hash] = 0;
        return entry.Path;
    }

    public bool Has(string hash) => entries.ContainsKey(hash);

    public string TargetPath(string hash, string extension) =>
        Path.Combine(Folder, hash[..2], hash + extension);

    public void Added(string hash, string path, long size)
    {
        if (entries.TryAdd(hash, (path, size)))
            Interlocked.Add(ref totalBytes, size);
        usedThisSession[hash] = 0;
    }

    public int Evict(ISet<string> keep) => Trim(keep, MaxBytes);

    public int Clear(ISet<string> keep) => Trim(keep, 0);

    private int Trim(ISet<string> keep, long target)
    {
        if (TotalBytes <= target)
            return 0;
        var candidates = entries
            .Where(e => !keep.Contains(e.Key))
            .Select(e => (e.Key, e.Value.Path, e.Value.Size, Used: usedThisSession.ContainsKey(e.Key), Written: LastWrite(e.Value.Path)))
            .OrderBy(e => e.Used)
            .ThenBy(e => e.Written)
            .ToList();
        var removed = 0;
        foreach (var c in candidates)
        {
            if (TotalBytes <= target)
                break;
            try
            {
                File.Delete(c.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (entries.TryRemove(c.Key, out var gone))
            {
                Interlocked.Add(ref totalBytes, -gone.Size);
                usedThisSession.TryRemove(c.Key, out _);
                removed++;
            }
        }
        return removed;
    }

    private static DateTime LastWrite(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception)
        {
            return DateTime.MinValue;
        }
    }

    private void Rescan()
    {
        entries.Clear();
        Interlocked.Exchange(ref totalBytes, 0);
        var folder = Folder;
        if (folder.Length == 0)
            return;
        Interlocked.Exchange(ref scanning, 1);
        Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(folder);
                Error = null;
                foreach (var dir in Directory.EnumerateDirectories(folder))
                {
                    foreach (var file in Directory.EnumerateFiles(dir))
                    {
                        var name = Path.GetFileName(file);
                        if (name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                        {
                            if (DateTime.UtcNow - LastWrite(file) > StalePart)
                                TryDelete(file);
                            continue;
                        }
                        var hash = Path.GetFileNameWithoutExtension(name);
                        if (!CharacterDataLimits.IsHash(hash) || !string.Equals(folder, Folder, StringComparison.Ordinal))
                            continue;
                        var size = new FileInfo(file).Length;
                        if (entries.TryAdd(hash, (file, size)))
                            Interlocked.Add(ref totalBytes, size);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Error = $"Lunar Sync can't use the download folder: {ex.Message}";
            }
            finally
            {
                Interlocked.Exchange(ref scanning, 0);
            }
        });
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
