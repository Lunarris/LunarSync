using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Sync.Protocol;

namespace Sync.Plugin.Collection;

public sealed record CharacterSnapshot(
    DateTime CollectedAt,
    ulong ContentId,
    Dictionary<SyncObjectKind, List<FileReplacement>> Files,
    CharacterState State,
    int SkippedFiles,
    int TransientFiles,
    string Hash)
{
    public Dictionary<string, string> LocalFiles { get; init; } = new(StringComparer.Ordinal);

    public IReadOnlyList<string> DroppedAnimations { get; init; } = [];

    public int SkeletonBones { get; init; }
}

public readonly record struct OwnedObject(SyncObjectKind Kind, nint Address, ushort Index);

public sealed class CharacterCollector : IDisposable
{
    private const int MaxTransientPerKind = 2000;
    private static readonly TimeSpan FallbackInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan OwnedCheckInterval = TimeSpan.FromMilliseconds(250);
    private static readonly string[] TransientExtensions =
        [".tmb", ".pap", ".avfx", ".atex", ".sklb", ".eid", ".phyb", ".scd", ".skp", ".shpk"];

    private readonly IFramework framework;
    private readonly IObjectTable objectTable;
    private readonly IClientState clientState;
    private readonly PluginIpc ipc;
    private readonly Func<bool> wanted;
    private readonly Action<CharacterSnapshot> changed;
    private readonly ConcurrentDictionary<string, (long Length, long Ticks, string Hash)> hashCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly TransientStore transientStore;
    private ConcurrentDictionary<SyncObjectKind, ConcurrentDictionary<string, byte>> transient = new();
    private ulong transientOwner;
    private readonly CancellationTokenSource lifetime = new();

    private OwnedObject[] owned = [];
    private DateTime nextOwnedCheck = DateTime.MinValue;
    private DateTime nextPoll = DateTime.MinValue;
    private long dirtyAt;
    private int busy;

    public CharacterCollector(IFramework framework, IObjectTable objectTable, IClientState clientState, PluginIpc ipc,
        Func<bool> wanted, Action<CharacterSnapshot> changed, TransientStore transientStore)
    {
        this.transientStore = transientStore;
        this.framework = framework;
        this.objectTable = objectTable;
        this.clientState = clientState;
        this.ipc = ipc;
        this.wanted = wanted;
        this.changed = changed;
        framework.Update += OnUpdate;
        clientState.TerritoryChanged += OnTerritoryChanged;
        clientState.ClassJobChanged += OnClassJobChanged;
        clientState.Login += OnLogin;
        clientState.Logout += OnLogout;
    }

    public CharacterSnapshot? Latest { get; private set; }
    public string Status { get; private set; } = "Waiting for the first collection.";
    public string LastTrigger { get; private set; } = string.Empty;
    public IReadOnlyCollection<string> MissingPlugins => ipc.Missing;
    public bool Busy => Volatile.Read(ref busy) != 0;
    public IReadOnlyList<OwnedObject> Owned => Volatile.Read(ref owned);
    public int TransientCount => Volatile.Read(ref transient).Values.Sum(s => s.Count);

    public void CollectNow() => Interlocked.Exchange(ref dirtyAt, DateTime.UtcNow.Ticks);

    public void MarkDirty(string source)
    {
        LastTrigger = source;
        Interlocked.CompareExchange(ref dirtyAt, (DateTime.UtcNow + Debounce).Ticks, 0);
    }

    public bool IsOwned(nint address)
    {
        foreach (var o in Volatile.Read(ref owned))
        {
            if (o.Address == address)
                return true;
        }
        return false;
    }

    public void OnResourceResolved(nint address, string gamePath, string resolvedPath)
    {
        SyncObjectKind? kind = null;
        foreach (var o in Volatile.Read(ref owned))
        {
            if (o.Address == address)
            {
                kind = o.Kind;
                break;
            }
        }
        if (kind is null || !TransientExtensions.Any(e => gamePath.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            return;
        if (resolvedPath.StartsWith('|'))
        {
            var end = resolvedPath.IndexOf('|', 1);
            if (end < 0)
                return;
            resolvedPath = resolvedPath[(end + 1)..];
        }
        var game = gamePath.Replace('\\', '/').ToLowerInvariant();
        if (string.Equals(resolvedPath.Replace('\\', '/'), game, StringComparison.OrdinalIgnoreCase) || !CharacterDataLimits.IsGamePath(game))
            return;
        var set = Volatile.Read(ref transient).GetOrAdd(kind.Value, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        if (set.Count < MaxTransientPerKind && set.TryAdd(game, 0))
        {
            transientStore.Changed();
            MarkDirty("emote or VFX file");
        }
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        clientState.TerritoryChanged -= OnTerritoryChanged;
        clientState.ClassJobChanged -= OnClassJobChanged;
        clientState.Login -= OnLogin;
        clientState.Logout -= OnLogout;
        lifetime.Cancel();
        transientStore.SaveIfDue(now: true);
    }

    private void OnTerritoryChanged(uint territory) => MarkDirty("zone change");

    private void OnClassJobChanged(uint job) => MarkDirty("job change");

    private void OnLogin() => MarkDirty("login");

    private void OnLogout(int type, int code) => transientStore.SaveIfDue(now: true);

    private void OnUpdate(IFramework fw)
    {
        var now = DateTime.UtcNow;
        if (now >= nextOwnedCheck)
        {
            nextOwnedCheck = now + OwnedCheckInterval;
            var current = FindOwned();
            if (current.Length > 0)
                UseTransientFor(current[0].Address);
            transientStore.SaveIfDue();
            var previous = Volatile.Read(ref owned);
            if (!current.SequenceEqual(previous))
            {
                Volatile.Write(ref owned, current);
                if (current.Length > 0)
                    MarkDirty(previous.Length == 0 ? "character loaded" : "minion, mount or pet");
            }
        }

        var dirty = Interlocked.Read(ref dirtyAt);
        var due = (dirty != 0 && now.Ticks >= dirty) || now >= nextPoll;
        if (!due || Busy || !wanted())
            return;
        Interlocked.Exchange(ref dirtyAt, 0);
        nextPoll = now + FallbackInterval;

        var raw = Capture(Volatile.Read(ref owned));
        if (raw is null)
        {
            Status = "Not logged in.";
            return;
        }

        Interlocked.Exchange(ref busy, 1);
        var token = lifetime.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var snapshot = await BuildAsync(raw, token);
                Status = $"Collected at {DateTime.Now:HH:mm:ss}.";
                if (snapshot.Hash == Latest?.Hash)
                    return;
                Latest = snapshot;
                changed(snapshot);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Status = $"Collection failed: {ex.Message}";
                Plugin.Log.Error(ex, "Character collection failed");
            }
            finally
            {
                Interlocked.Exchange(ref busy, 0);
            }
        }, token);
    }

    private unsafe void UseTransientFor(nint player)
    {
        var contentId = ((Character*)player)->ContentId;
        if (contentId == 0 || contentId == transientOwner)
            return;
        transientOwner = contentId;
        Volatile.Write(ref transient, transientStore.For(contentId));
        MarkDirty("saved emote and VFX paths");
    }

    private OwnedObject[] FindOwned()
    {
        var player = objectTable.LocalPlayer;
        return player is null ? [] : OwnedObjects.Find(objectTable, player.Address, player.ObjectIndex);
    }

    private sealed record RawObject(SyncObjectKind Kind, Dictionary<string, HashSet<string>>? Paths);

    private sealed record RawCapture(ulong ContentId, List<RawObject> Objects, CharacterState State, int Transient, int SkeletonBones);

    private unsafe RawCapture? Capture(OwnedObject[] objects)
    {
        if (objects.Length == 0 || objects[0].Kind != SyncObjectKind.Player)
            return null;
        var player = objects[0];
        var state = new CharacterState();
        var raw = new List<RawObject>();
        var penumbra = ipc.PenumbraAvailable;
        var transientUsed = 0;

        foreach (var obj in objects)
        {
            Dictionary<string, HashSet<string>>? paths = null;
            if (penumbra)
            {
                paths = ipc.ResourcePaths(obj.Index) ?? new Dictionary<string, HashSet<string>>();
                transientUsed += AddTransient(obj, paths);
            }
            raw.Add(new RawObject(obj.Kind, paths));
            Put(state.Glamourer, obj.Kind, ipc.Glamourer(obj.Index));
            Put(state.CustomizePlus, obj.Kind, ipc.CustomizePlus(obj.Index));
            Put(state.Loci, obj.Kind, ipc.Loci(obj.Address));
        }

        state.Manipulation = penumbra ? ipc.PenumbraMeta() : string.Empty;
        state.Heels = ipc.Heels();
        state.Honorific = ipc.Honorific();
        state.Moodles = ipc.Moodles(player.Address);
        state.PetNames = ipc.PetNames();
        state.ClientBuild = Plugin.Build;

        if (!penumbra)
            ipc.Missing.Add("Penumbra");
        return new RawCapture(((Character*)player.Address)->ContentId, raw, state, transientUsed,
            AnimationCheck.SkeletonBoneCount(player.Address));
    }

    private int AddTransient(OwnedObject obj, Dictionary<string, HashSet<string>> paths)
    {
        if (!Volatile.Read(ref transient).TryGetValue(obj.Kind, out var set) || set.IsEmpty)
            return 0;
        var games = set.Keys.ToArray();
        string?[] resolved = obj.Kind == SyncObjectKind.Player
            ? ipc.ResolvePlayer(games) ?? new string?[games.Length]
            : games.Select(g => ipc.ResolveFor(g, obj.Index)).ToArray();
        var used = 0;
        for (var i = 0; i < games.Length && i < resolved.Length; i++)
        {
            var target = resolved[i];
            if (string.IsNullOrEmpty(target) || string.Equals(target.Replace('\\', '/'), games[i], StringComparison.OrdinalIgnoreCase))
                continue;
            if (!paths.TryGetValue(target, out var gamePaths))
                paths[target] = gamePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (gamePaths.Add(games[i]))
                used++;
        }
        return used;
    }

    private static void Put(Dictionary<SyncObjectKind, string> target, SyncObjectKind kind, string value)
    {
        if (value.Length > 0)
            target[kind] = value;
    }

    private async Task<CharacterSnapshot> BuildAsync(RawCapture raw, CancellationToken token)
    {
        var files = new Dictionary<SyncObjectKind, List<FileReplacement>>();
        var local = new Dictionary<string, string>(StringComparer.Ordinal);
        var skipped = 0;
        var dropped = new List<string>();
        foreach (var obj in raw.Objects)
        {
            if (obj.Paths is null)
                continue;
            var list = new List<FileReplacement>();
            foreach (var (resolved, gamePaths) in obj.Paths)
            {
                token.ThrowIfCancellationRequested();
                var normalizedResolved = resolved.Replace('\\', '/');
                var paths = gamePaths
                    .Select(g => g.Replace('\\', '/').ToLowerInvariant())
                    .Where(g => !string.Equals(g, normalizedResolved, StringComparison.OrdinalIgnoreCase))
                    .Where(CharacterDataLimits.HasAllowedExtension)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(g => g, StringComparer.Ordinal)
                    .ToArray();
                if (paths.Length == 0)
                    continue;

                FileReplacement entry;
                if (Path.IsPathRooted(resolved))
                {
                    var hash = await HashAsync(resolved, token);
                    if (hash is null)
                    {
                        skipped++;
                        continue;
                    }
                    entry = new FileReplacement(paths, hash, null);
                    if (obj.Kind == SyncObjectKind.Player && raw.SkeletonBones > 0 && IsAnimation(paths)
                        && !AnimationCheck.Fits(await MaxBoneAsync(hash, resolved), raw.SkeletonBones))
                    {
                        dropped.AddRange(paths);
                        continue;
                    }
                    local.TryAdd(hash, resolved);
                }
                else
                {
                    entry = new FileReplacement(paths, string.Empty, normalizedResolved.ToLowerInvariant());
                }

                if (CharacterDataLimits.IsValidEntry(entry))
                    list.Add(entry);
                else
                    skipped++;
            }
            if (list.Count > 0)
                files[obj.Kind] = list.OrderBy(f => f.GamePaths[0], StringComparer.Ordinal).ToList();
        }

        var fingerprint = JsonSerializer.SerializeToUtf8Bytes(new { raw.ContentId, Files = files.OrderBy(kv => kv.Key).ToList(), raw.State });
        var snapshotHash = Convert.ToHexStringLower(SHA256.HashData(fingerprint));
        return new CharacterSnapshot(DateTime.Now, raw.ContentId, files, raw.State, skipped, raw.Transient, snapshotHash)
        {
            LocalFiles = local,
            DroppedAnimations = dropped.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            SkeletonBones = raw.SkeletonBones,
        };
    }

    private static bool IsAnimation(string[] gamePaths) =>
        gamePaths.Any(g => g.EndsWith(".pap", StringComparison.OrdinalIgnoreCase));

    private async Task<int> MaxBoneAsync(string hash, string path) =>
        AnimationCheck.TryCached(hash, out var cached)
            ? cached
            : await framework.RunOnFrameworkThread(() => AnimationCheck.MaxBoneIndex(hash, path));

    private async Task<string?> HashAsync(string path, CancellationToken token)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > FileTransferLimits.MaxFileBytes)
                return null;
            var ticks = info.LastWriteTimeUtc.Ticks;
            if (hashCache.TryGetValue(path, out var cached) && cached.Length == info.Length && cached.Ticks == ticks)
                return cached.Hash.Length == 0 ? null : cached.Hash;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var head = new byte[FileContent.HeadBytes];
            var headLength = await stream.ReadAtLeastAsync(head, head.Length, false, token);
            if (FileContent.IsBlocked(head.AsSpan(0, headLength)))
            {
                hashCache[path] = (info.Length, ticks, string.Empty);
                return null;
            }
            stream.Position = 0;
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
            hashCache[path] = (info.Length, ticks, hash);
            return hash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
