using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sync.Protocol;

namespace Sync.Plugin.Collection;

public sealed class TransientStore
{
    private const int MaxPerKind = 2000;
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(60);

    private readonly string path;
    private readonly ConcurrentDictionary<ulong, ConcurrentDictionary<SyncObjectKind, ConcurrentDictionary<string, byte>>> characters = new();
    private readonly object writeGate = new();
    private long saveAt;

    public TransientStore(string path)
    {
        this.path = path;
        Load();
    }

    public ConcurrentDictionary<SyncObjectKind, ConcurrentDictionary<string, byte>> For(ulong contentId) =>
        characters.GetOrAdd(contentId, _ => new());

    public void Changed() => Interlocked.CompareExchange(ref saveAt, (DateTime.UtcNow + SaveDelay).Ticks, 0);

    public void SaveIfDue(bool now = false)
    {
        var at = Interlocked.Read(ref saveAt);
        if (at == 0 || (!now && DateTime.UtcNow.Ticks < at))
            return;
        Interlocked.Exchange(ref saveAt, 0);
        var data = characters
            .Select(c => (Key: c.Key.ToString(CultureInfo.InvariantCulture),
                Kinds: c.Value.Where(k => !k.Value.IsEmpty).ToDictionary(k => k.Key, k => k.Value.Keys.OrderBy(p => p, StringComparer.Ordinal).ToList())))
            .Where(c => c.Kinds.Count > 0)
            .ToDictionary(c => c.Key, c => c.Kinds);
        if (now)
            Write(data);
        else
            Task.Run(() => Write(data));
    }

    private void Write(Dictionary<string, Dictionary<SyncObjectKind, List<string>>> data)
    {
        lock (writeGate)
        {
            var temp = path + ".tmp";
            try
            {
                File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(data));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Plugin.Log.Error(ex, "Saving emote and VFX paths failed");
            }
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(path))
                return;
            var data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<SyncObjectKind, List<string>>>>(File.ReadAllBytes(path));
            if (data is null)
                return;
            foreach (var (key, kinds) in data)
            {
                if (!ulong.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var contentId) || contentId == 0 || kinds is null)
                    continue;
                var character = For(contentId);
                foreach (var (kind, paths) in kinds)
                {
                    if (!Enum.IsDefined(kind) || paths is null)
                        continue;
                    var set = character.GetOrAdd(kind, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
                    foreach (var p in paths.Where(CharacterDataLimits.IsGamePath).Take(MaxPerKind))
                        set.TryAdd(p, 0);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Plugin.Log.Error(ex, "Reading saved emote and VFX paths failed");
        }
    }
}
