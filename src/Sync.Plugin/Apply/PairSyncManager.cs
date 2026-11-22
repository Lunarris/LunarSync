using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Sync.Client.Core;
using Sync.Plugin.Collection;
using Sync.Plugin.Interop;
using Sync.Plugin.Transfer;
using Sync.Protocol;

namespace Sync.Plugin.Apply;

public sealed record IncomingData(ulong ContentId, CharacterData Data, CharacterState State, string Hash);

public enum PairSyncPhase
{
    Waiting,
    Downloading,
    NotNearby,
    Applied,
    Paused,
}

public sealed record PairSyncView(PairSyncPhase Phase, int FilesTotal, int FilesReady, int FilesFailed, DateTime? AppliedAt, string? Error, string Build);

public sealed class PairSyncManager : IDisposable
{
    private const int DownloadAttempts = 4;
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(250);

    private readonly IFramework framework;
    private readonly IObjectTable objectTable;
    private readonly ApplyIpc ipc;
    private readonly FileCache cache;
    private readonly Configuration configuration;
    private readonly CrashGuard crashGuard;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<string, PairRecord> records = new(StringComparer.Ordinal);
    private DateTime nextScan = DateTime.MinValue;

    private sealed class PairRecord(string uid)
    {
        public readonly string Uid = uid;

        public IncomingData? Data;
        public int Version;
        public int ReadyVersion = -1;
        public int StartedVersion = -1;
        public bool Remove;
        public CancellationTokenSource? Downloads;
        public int FilesTotal;
        public int FilesReady;
        public int FilesFailed;
        public string? Error;

        public bool Dead;
        public PairSyncPhase Phase = PairSyncPhase.Waiting;
        public int AppliedVersion = -1;
        public CharacterState? AppliedState;
        public OwnedObject[] Objects = [];
        public ushort AppliedIndex;
        public string? AppliedName;
        public DateTime? AppliedAt;
        public Guid Collection;
        public string? ModKey;
        public readonly Dictionary<SyncObjectKind, Guid> Customize = [];
    }

    public PairSyncManager(IFramework framework, IObjectTable objectTable, ApplyIpc ipc, FileCache cache,
        Configuration configuration, CrashGuard crashGuard, Func<SyncConnection?> connection)
    {
        this.crashGuard = crashGuard;
        this.framework = framework;
        this.objectTable = objectTable;
        this.ipc = ipc;
        this.cache = cache;
        this.configuration = configuration;
        Downloader = new Downloader(cache, connection, lifetime.Token);
        framework.Update += OnUpdate;
    }

    public Downloader Downloader { get; }

    public bool IsPairCollection(Guid id) => id != Guid.Empty && records.Values.Any(r => r.Collection == id);

    public int AppliedCount => records.Values.Count(r => r.Phase == PairSyncPhase.Applied);

    public void ReapplyAll()
    {
        foreach (var r in records.Values)
            r.AppliedVersion = -1;
    }

    public PairSyncView? View(string uid)
    {
        if (!records.TryGetValue(uid, out var r))
            return null;
        lock (r)
            return new PairSyncView(r.Phase, r.FilesTotal, r.FilesReady, r.FilesFailed, r.AppliedAt, r.Error, r.Data?.State.ClientBuild ?? string.Empty);
    }

    public void OnData(string uid, IncomingData data)
    {
        PairRecord r;
        while (true)
        {
            r = records.GetOrAdd(uid, u => new PairRecord(u));
            lock (r)
            {
                if (!r.Dead)
                    break;
            }
        }
        lock (r)
        {
            var wasRemoved = r.Remove;
            r.Remove = false;
            if (!wasRemoved && r.Data?.Hash == data.Hash)
                return;
            r.Data = data;
            r.Version++;
            r.Downloads?.Cancel();
            r.Downloads = null;
            r.Error = null;
            CountFiles(r, data);
        }
    }

    public SyncFilter BlockedFor(string uid) =>
        configuration.BlockedForAll | configuration.BlockedByPair.GetValueOrDefault(uid);

    public void FiltersChanged()
    {
        foreach (var r in records.Values)
        {
            lock (r)
            {
                if (r.Data is null)
                    continue;
                r.Version++;
                r.Downloads?.Cancel();
                r.Downloads = null;
                r.Error = null;
                CountFiles(r, r.Data);
            }
        }
    }

    private void CountFiles(PairRecord r, IncomingData data)
    {
        var needed = Needed(r.Uid, data);
        r.FilesTotal = needed.Count;
        r.FilesReady = needed.Count(n => cache.Has(n.Hash));
        r.FilesFailed = 0;
    }

    public bool IsPaused(string uid) => !configuration.ApplyPairs || configuration.PausedPairs.Contains(uid);

    private void StartDownload(PairRecord r, IncomingData data, int version)
    {
        CancellationTokenSource cts;
        lock (r)
        {
            if (r.Version != version || r.StartedVersion == version)
                return;
            r.StartedVersion = version;
            r.Downloads?.Cancel();
            r.Downloads = cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        }
        _ = Task.Run(() => DownloadAsync(r, data, version, cts.Token));
    }

    private List<(string Hash, string Extension)> Needed(string uid, IncomingData data)
    {
        var blocked = BlockedFor(uid);
        return data.Data.FileReplacements.Values
            .SelectMany(l => l)
            .Where(f => string.IsNullOrEmpty(f.FileSwapPath) && f.GamePaths.Any(g => !SyncFilters.Blocks(blocked, g)))
            .GroupBy(f => f.Hash, StringComparer.Ordinal)
            .Select(g => (Hash: g.Key, Extension: Path.GetExtension(g.First().GamePaths[0])))
            .ToList();
    }

    public void OnGone(string uid)
    {
        if (!records.TryGetValue(uid, out var r))
            return;
        lock (r)
        {
            r.Remove = true;
            r.Downloads?.Cancel();
        }
    }

    public void OnAllGone()
    {
        foreach (var uid in records.Keys)
            OnGone(uid);
    }

    public string? UidFor(ulong contentId)
    {
        foreach (var r in records.Values)
        {
            lock (r)
            {
                if (!r.Remove && r.Data?.ContentId == contentId)
                    return r.Uid;
            }
        }
        return null;
    }

    public void Reapply(string uid)
    {
        if (records.TryGetValue(uid, out var r))
            Revert(r);
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        lifetime.Cancel();
        void RevertAll()
        {
            foreach (var r in records.Values)
                Revert(r);
        }
        try
        {
            if (framework.IsInFrameworkUpdateThread)
                RevertAll();
            else
                framework.RunOnFrameworkThread(RevertAll).Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Reverting pairs on unload failed");
        }
    }

    private async Task DownloadAsync(PairRecord r, IncomingData data, int version, CancellationToken token)
    {
        var needed = Needed(r.Uid, data);
        try
        {
            lock (r)
            {
                r.FilesTotal = needed.Count;
                r.FilesFailed = 0;
            }
            for (var attempt = 0; attempt < DownloadAttempts; attempt++)
            {
                var missing = needed.Where(n => !cache.Has(n.Hash)).ToList();
                SetReady(r, version, needed.Count - missing.Count);
                if (missing.Count == 0)
                    break;
                var tasks = missing.Select(async n =>
                {
                    var ok = await Downloader.EnsureAsync(n.Hash, n.Extension, SyncTier.Pair);
                    if (ok)
                        lock (r)
                            if (r.Version == version)
                                r.FilesReady++;
                    return ok;
                }).ToList();
                var results = await Task.WhenAll(tasks);
                token.ThrowIfCancellationRequested();
                if (results.All(ok => ok))
                    break;
                if (attempt < DownloadAttempts - 1)
                    await Task.Delay(TimeSpan.FromSeconds(5 * (attempt + 1)), token);
            }

            lock (r)
            {
                if (r.Version != version)
                    return;
                r.FilesReady = needed.Count(n => cache.Has(n.Hash));
                r.FilesFailed = needed.Count - r.FilesReady;
                r.Error = r.FilesFailed > 0 ? $"{r.FilesFailed} file(s) could not be downloaded. {Downloader.LastError}" : null;
                r.ReadyVersion = version;
            }
            cache.Evict(InUse());
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Pair download failed");
            lock (r)
            {
                r.Error = ex.Message;
                if (r.Version == version)
                    r.ReadyVersion = version;
            }
        }
    }

    private static void SetReady(PairRecord r, int version, int ready)
    {
        lock (r)
        {
            if (r.Version == version)
                r.FilesReady = ready;
        }
    }

    public HashSet<string> InUse()
    {
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in records.Values)
        {
            IncomingData? data;
            lock (r)
                data = r.Data;
            if (data is null)
                continue;
            foreach (var f in data.Data.FileReplacements.Values.SelectMany(l => l))
            {
                if (string.IsNullOrEmpty(f.FileSwapPath))
                    keep.Add(f.Hash);
            }
        }
        return keep;
    }

    private void OnUpdate(IFramework fw)
    {
        crashGuard.Tick();
        foreach (var r in records.Values)
        {
            if (r.Objects.Length > 0 && !OwnedObjects.StillAt(objectTable, r.Objects[0].Index, r.Objects[0].Address))
                Revert(r);
        }

        var now = DateTime.UtcNow;
        if (now < nextScan)
            return;
        nextScan = now + ScanInterval;
        foreach (var r in records.Values)
        {
            try
            {
                Process(r);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Applying pair data failed");
                lock (r)
                    r.Error = ex.Message;
            }
        }
    }

    private void Process(PairRecord r)
    {
        IncomingData? data;
        int version, ready;
        bool remove;
        lock (r)
        {
            data = r.Data;
            version = r.Version;
            ready = r.ReadyVersion;
            remove = r.Remove;
        }

        if (remove || IsPaused(r.Uid))
        {
            Revert(r);
            r.Phase = PairSyncPhase.Paused;
            if (remove)
            {
                lock (r)
                {
                    if (r.Remove)
                    {
                        r.Dead = true;
                        records.TryRemove(r.Uid, out _);
                    }
                }
            }
            return;
        }
        if (data is null)
        {
            r.Phase = PairSyncPhase.Waiting;
            return;
        }

        var player = OwnedObjects.FindPlayer(objectTable, data.ContentId, r.AppliedIndex);
        if (player is null)
        {
            Revert(r);
            r.Phase = PairSyncPhase.NotNearby;
            return;
        }
        AutoNickname(r.Uid, player.Value.Index);
        if (r.Objects.Length > 0 && r.Objects[0].Address != player.Value.Address)
            Revert(r);
        if (ready < version)
        {
            StartDownload(r, data, version);
            if (r.AppliedVersion < 0)
                r.Phase = PairSyncPhase.Downloading;
            return;
        }

        var owned = OwnedObjects.Find(objectTable, player.Value.Address, player.Value.Index);
        if (r.AppliedVersion == version && owned.SequenceEqual(r.Objects))
        {
            r.Phase = PairSyncPhase.Applied;
            return;
        }
        Apply(r, data, version, owned);
    }

    private void AutoNickname(string uid, int index)
    {
        if (!configuration.AutoNicknameFromCharacter || configuration.AutoNicknamed.Contains(uid))
            return;
        var name = objectTable[index]?.Name.TextValue;
        if (string.IsNullOrWhiteSpace(name))
            return;
        configuration.AutoNicknamed.Add(uid);
        configuration.PairNicknames.TryAdd(uid, name);
        configuration.Save();
    }

    private void Apply(PairRecord r, IncomingData data, int version, OwnedObject[] owned)
    {
        var state = data.State;
        var previous = r.AppliedState;
        var fresh = owned.Where(o => !r.Objects.Contains(o)).ToHashSet();
        foreach (var gone in r.Objects.Where(o => !owned.Contains(o)))
        {
            if (r.Customize.Remove(gone.Kind, out var id))
                ipc.CustomizeDelete(id);
        }

        var risky = new List<(string Hash, string GamePath)>();
        var paths = BuildPaths(r, data, risky);
        var modKey = ModKey(paths, state.Manipulation);
        var redraw = new HashSet<ushort>();
        if (paths.Count > 0 || state.Manipulation.Length > 0 || r.Collection != Guid.Empty)
        {
            if (r.Collection == Guid.Empty)
                r.Collection = ipc.CreateCollection("LunarSync_" + r.Uid[..8]);
            if (r.Collection != Guid.Empty)
            {
                foreach (var o in fresh)
                {
                    if (ipc.AssignCollection(r.Collection, o.Index))
                        redraw.Add(o.Index);
                }
                if (modKey != r.ModKey)
                    crashGuard.Arm(risky, configuration.NicknameOf(r.Uid) ?? "pair " + r.Uid[..8]);
                if (modKey != r.ModKey && ipc.SetMod(r.Collection, paths, state.Manipulation))
                {
                    r.ModKey = modKey;
                    foreach (var o in owned)
                        redraw.Add(o.Index);
                }
            }
        }

        foreach (var o in owned)
        {
            var isNew = fresh.Contains(o);
            var glamourer = Get(state.Glamourer, o.Kind);
            var oldGlamourer = previous is null ? string.Empty : Get(previous.Glamourer, o.Kind);
            if (glamourer.Length > 0 && (isNew || glamourer != oldGlamourer))
                ipc.GlamourerApply(o.Index, glamourer);
            else if (glamourer.Length == 0 && oldGlamourer.Length > 0 && !isNew)
                ipc.GlamourerRevert(o.Index);

            var customize = Get(state.CustomizePlus, o.Kind);
            var oldCustomize = previous is null ? string.Empty : Get(previous.CustomizePlus, o.Kind);
            if (customize.Length > 0 && (isNew || customize != oldCustomize))
            {
                if (r.Customize.Remove(o.Kind, out var oldId))
                    ipc.CustomizeDelete(oldId);
                if (ipc.CustomizeSet(o.Index, customize) is { } id)
                    r.Customize[o.Kind] = id;
            }
            else if (customize.Length == 0 && r.Customize.Remove(o.Kind, out var staleId))
            {
                ipc.CustomizeDelete(staleId);
            }

            var loci = Get(state.Loci, o.Kind);
            var oldLoci = previous is null ? string.Empty : Get(previous.Loci, o.Kind);
            if (loci.Length > 0 && (isNew || loci != oldLoci))
                ipc.LociSet(o.Address, loci);
            else if (loci.Length == 0 && oldLoci.Length > 0 && !isNew)
                ipc.LociClear(o.Address);
        }

        var player = owned[0];
        var freshPlayer = fresh.Contains(player);
        Single(previous?.Heels, state.Heels, freshPlayer, v => ipc.HeelsSet(player.Index, v), () => ipc.HeelsClear(player.Index));
        Single(previous?.Honorific, state.Honorific, freshPlayer, v => ipc.HonorificSet(player.Index, v), () => ipc.HonorificClear(player.Index));
        Single(previous?.Moodles, state.Moodles, freshPlayer, v => ipc.MoodlesSet(player.Address, v), () => ipc.MoodlesClear(player.Address));
        Single(previous?.PetNames, state.PetNames, freshPlayer, v => ipc.PetNamesSet(v), () => ipc.PetNamesClear(player.Index));

        foreach (var index in redraw)
            ipc.Redraw(index);

        r.AppliedState = state;
        r.AppliedVersion = version;
        r.Objects = owned;
        r.AppliedIndex = player.Index;
        r.AppliedName = objectTable[player.Index]?.Name.TextValue;
        r.AppliedAt = DateTime.Now;
        r.Phase = PairSyncPhase.Applied;
    }

    private void Revert(PairRecord r)
    {
        if (r.Objects.Length == 0 && r.Collection == Guid.Empty && r.Customize.Count == 0)
            return;
        var previous = r.AppliedState;
        var hadMod = r.ModKey is not null;
        if (r.Collection != Guid.Empty)
        {
            ipc.DeleteCollection(r.Collection);
            r.Collection = Guid.Empty;
            r.ModKey = null;
        }

        foreach (var o in r.Objects)
        {
            var present = OwnedObjects.StillAt(objectTable, o.Index, o.Address);
            if (previous is not null && Get(previous.Glamourer, o.Kind).Length > 0)
            {
                if (present)
                    ipc.GlamourerRevert(o.Index);
                else if (o.Kind == SyncObjectKind.Player && !string.IsNullOrEmpty(r.AppliedName))
                    ipc.GlamourerRevertByName(r.AppliedName);
            }
            if (present && previous is not null && Get(previous.Loci, o.Kind).Length > 0)
                ipc.LociClear(o.Address);
            if (present && hadMod)
                ipc.Redraw(o.Index);
        }
        foreach (var id in r.Customize.Values)
            ipc.CustomizeDelete(id);
        r.Customize.Clear();

        if (r.Objects.Length > 0 && previous is not null)
        {
            var player = r.Objects[0];
            var present = OwnedObjects.StillAt(objectTable, player.Index, player.Address);
            var reusable = present || OwnedObjects.SlotEmpty(objectTable, player.Index);
            if (previous.Heels.Length > 0 && reusable)
                ipc.HeelsClear(player.Index);
            if (previous.Honorific.Length > 0 && reusable)
                ipc.HonorificClear(player.Index);
            if (previous.Moodles.Length > 0 && present)
                ipc.MoodlesClear(player.Address);
            if (previous.PetNames.Length > 0 && reusable)
                ipc.PetNamesClear(player.Index);
        }

        r.Objects = [];
        r.AppliedState = null;
        r.AppliedVersion = -1;
        r.AppliedName = null;
        r.AppliedAt = null;
        if (r.Phase == PairSyncPhase.Applied)
            r.Phase = PairSyncPhase.NotNearby;
    }

    private Dictionary<string, string> BuildPaths(PairRecord r, IncomingData data, List<(string Hash, string GamePath)> risky)
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var blocked = BlockedFor(r.Uid);
        foreach (var (_, files) in data.Data.FileReplacements.OrderBy(kv => kv.Key))
        {
            foreach (var f in files)
            {
                string? target = !string.IsNullOrEmpty(f.FileSwapPath) ? f.FileSwapPath : cache.PathFor(f.Hash);
                if (target is null)
                    continue;
                var file = string.IsNullOrEmpty(f.FileSwapPath);
                if (file && crashGuard.IsBlocked(f.Hash))
                    continue;
                foreach (var gamePath in f.GamePaths)
                {
                    if (!SyncFilters.Blocks(blocked, gamePath) && paths.TryAdd(gamePath, target) && file && CrashGuard.IsRisky(gamePath))
                        risky.Add((f.Hash, gamePath));
                }
            }
        }
        return paths;
    }

    private static string ModKey(Dictionary<string, string> paths, string manipulations)
    {
        var text = new StringBuilder();
        foreach (var (game, target) in paths.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            text.Append(game).Append('>').Append(target).Append('\n');
        text.Append(manipulations);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static string Get(Dictionary<SyncObjectKind, string> values, SyncObjectKind kind) =>
        values.TryGetValue(kind, out var value) ? value ?? string.Empty : string.Empty;

    private static void Single(string? previous, string current, bool fresh, Action<string> set, Action clear)
    {
        previous ??= string.Empty;
        current ??= string.Empty;
        if (current.Length > 0 && (fresh || current != previous))
            set(current);
        else if (current.Length == 0 && previous.Length > 0 && !fresh)
            clear();
    }
}
