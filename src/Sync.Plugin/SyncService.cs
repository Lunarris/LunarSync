using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Text.Json;
using Sync.Client.Core;
using Sync.Plugin.Apply;
using Sync.Plugin.Collection;
using Sync.Protocol;

namespace Sync.Plugin;

public enum ConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Rejected,
    Failed,
}

public sealed class SyncService : IDisposable
{
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];

    private readonly object gate = new();
    private readonly Configuration configuration;
    private readonly string keyPath;
    private readonly Dictionary<string, PairEntry> pairs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (CharacterData Data, string Envelope)> early = new(StringComparer.Ordinal);
    private const int MaxEarly = 64;

    private readonly SemaphoreSlim pushGate = new(1, 1);

    private SyncConnection? connection;
    private SyncConnection? listed;
    private CharacterSnapshot? snapshot;
    private string? lastBroadcastHash;
    private CancellationTokenSource? lifetime;
    private volatile bool disposed;

    public SyncService(Configuration configuration, string configDirectory)
    {
        this.configuration = configuration;
        keyPath = Path.Combine(configDirectory, "identity.key");
        HasKey = File.Exists(keyPath);
        Identity = HasKey ? IdentityStore.LoadOrCreate(keyPath, out _) : IdentityStore.Generate();
    }

    public IdentityStore Identity { get; private set; }
    public bool HasKey { get; private set; }

    public bool CreateKey(out string message)
    {
        if (HasKey)
        {
            message = "Using your existing key.";
            return true;
        }
        if (!SaveKey(Identity, out var error))
        {
            message = $"Could not save your key: {error}";
            return false;
        }
        HasKey = true;
        message = "Key created.";
        return true;
    }

    private bool SaveKey(IdentityStore identity, out string error)
    {
        var temp = keyPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
            File.WriteAllText(temp, identity.Export());
            File.Move(temp, keyPath, overwrite: true);
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
    public string KeyPath => keyPath;

    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;
    public string StatusDetail { get; private set; } = string.Empty;
    public PairingIdInfo? MyId { get; private set; }
    public string LastMessage { get; private set; } = string.Empty;
    public bool LastMessageIsError { get; private set; }

    public bool IsRunning => Volatile.Read(ref lifetime) is not null;
    public bool IsConnected => Status == ConnectionStatus.Connected;
    public string LastPush { get; private set; } = "Nothing sent yet.";
    public bool LastPushIsError { get; private set; }
    public int UploadTotal { get; private set; }
    public int UploadDone { get; private set; }
    public bool Uploading { get; private set; }

    public event Action<string, IncomingData>? DataReceived;
    public event Action<string>? PairGone;
    public event Action? AllGone;

    public SyncConnection? Connection => IsConnected ? Volatile.Read(ref connection) : null;

    public bool HasOnlinePairs
    {
        get
        {
            lock (gate)
                return pairs.Values.Any(p => p.State == PairState.Paired && p.Online);
        }
    }

    public void OnSnapshot(CharacterSnapshot latest)
    {
        Volatile.Write(ref snapshot, latest);
        PushTo(null);
    }

    public IReadOnlyList<PairEntry> Pairs
    {
        get
        {
            lock (gate)
                return pairs.Values
                    .OrderBy(p => p.State == PairState.PendingIncoming ? 0 : p.State == PairState.Paired ? 1 : 2)
                    .ThenByDescending(p => p.Online)
                    .ThenBy(p => configuration.NameOf(p), StringComparer.OrdinalIgnoreCase)
                    .ToList();
        }
    }

    public void Start()
    {
        if (disposed || IsRunning)
            return;
        if (!HasKey)
        {
            SetStatus(ConnectionStatus.Failed, "Finish the Lunar Sync setup first.");
            return;
        }
        if (!TryGetServerUri(out var uri, out var error))
        {
            SetStatus(ConnectionStatus.Failed, error);
            return;
        }

        var cts = new CancellationTokenSource();
        Volatile.Write(ref lifetime, cts);
        _ = Task.Run(() => RunAsync(uri, cts), cts.Token);
    }

    public void Stop()
    {
        var cts = Interlocked.Exchange(ref lifetime, null);
        cts?.Cancel();
        var current = Interlocked.Exchange(ref connection, null);
        if (current is not null)
            _ = Task.Run(async () =>
            {
                try { await current.DisposeAsync(); }
                catch (Exception ex) { Plugin.Log.Error(ex, "Disconnect failed"); }
            });
        ClearPairs();
        MyId = null;
        lastBroadcastHash = null;
        SetStatus(ConnectionStatus.Disconnected, string.Empty);
        AllGone?.Invoke();
    }

    public void Restart()
    {
        Stop();
        Start();
    }

    public void RequestPair(string pairingId) => Invoke(async c =>
    {
        var result = await c.PairRequestAsync(pairingId.Trim());
        if (!result.Success)
            return Fail(DescribeError(result.Error));
        Upsert(result.Entry!);
        if (result.Entry!.State == PairState.Paired && result.Entry.Online)
            PushTo([result.Entry.Uid]);
        return Ok(result.Entry!.State == PairState.Paired
            ? $"Paired with {configuration.NameOf(result.Entry)}."
            : $"Request sent to {configuration.NameOf(result.Entry)}.");
    });

    public void Accept(PairEntry entry) => Invoke(async c =>
    {
        var accepted = await c.PairAcceptAsync(entry.Uid);
        if (accepted is null)
            return Fail($"The request from {configuration.NameOf(entry)} is no longer pending.");
        Upsert(accepted);
        return Ok($"Paired with {configuration.NameOf(accepted)}.");
    });

    public void Remove(PairEntry entry) => Invoke(async c =>
    {
        await c.PairRemoveAsync(entry.Uid);
        lock (gate)
            pairs.Remove(entry.Uid);
        PairGone?.Invoke(entry.Uid);
        return Ok(entry.State switch
        {
            PairState.PendingIncoming => $"Declined {configuration.NameOf(entry)}.",
            PairState.PendingOutgoing => $"Cancelled the request to {configuration.NameOf(entry)}.",
            _ => $"Removed {configuration.NameOf(entry)}.",
        });
    });

    public void SetAlias(string? alias) => Invoke(async c =>
    {
        var result = await c.SetAliasAsync(string.IsNullOrWhiteSpace(alias) ? null : alias.Trim());
        MyId = result.Current;
        if (!result.Success)
            return Fail(DescribeError(result.Error));
        return Ok(result.Current.Alias is null ? "Alias cleared." : $"Alias set to {result.Current.Alias}.");
    });

    public void RegenerateCode() => Invoke(async c =>
    {
        var result = await c.RegenerateMyPairCodeAsync();
        MyId = result.Current;
        if (!result.Success)
            return Fail(DescribeError(result.Error));
        return Ok($"New pair code: {result.Current.DisplayCode}.");
    });

    public bool ExportKey(string path, out string message)
    {
        try
        {
            Identity.ExportTo(path);
            message = $"Key exported to {path}. Keep it private.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            message = File.Exists(path) ? "A file with that name already exists. Choose a new name." : $"Export failed: {ex.Message}";
            return false;
        }
    }

    public bool ImportKey(string path, out string message)
    {
        IdentityStore imported;
        try
        {
            imported = IdentityStore.FromExport(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            message = $"That file is not a usable key file: {ex.Message}";
            return false;
        }

        var wasRunning = IsRunning;
        Stop();
        if (!SaveKey(imported, out var error))
        {
            message = $"Could not save the imported key: {error}";
            return false;
        }
        HasKey = true;

        Identity = imported;
        message = "Key imported. You're now signed in with that key.";
        if (wasRunning)
            Start();
        return true;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Interlocked.Exchange(ref lifetime, null)?.Cancel();
        var current = Interlocked.Exchange(ref connection, null);
        if (current is not null)
        {
            try { Task.Run(() => current.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(2)); }
            catch (Exception ex) { Plugin.Log.Error(ex, "Disconnect on unload failed"); }
        }
    }

    private async Task RunAsync(Uri uri, CancellationTokenSource cts)
    {
        var token = cts.Token;
        var attempt = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                SetStatus(ConnectionStatus.Connecting, uri.Host);
                var conn = new SyncConnection(uri, Identity);
                var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                Wire(conn, closed);
                Interlocked.Exchange(ref connection, conn);

                try
                {
                    await conn.ConnectAsync(token);
                    attempt = 0;
                    SetStatus(ConnectionStatus.Connected, uri.Host);
                    await RefreshAsync(conn, token);
                    var reason = await closed.Task.WaitAsync(token);
                    if (SyncConnection.FindRejection(reason) is { IsPermanent: true } permanent)
                    {
                        SetStatus(ConnectionStatus.Rejected, DescribeError(permanent.Reason));
                        await Release(conn);
                        return;
                    }
                }
                catch (Exception) when (token.IsCancellationRequested)
                {
                    await Release(conn);
                    return;
                }
                catch (SyncRejectedException rejected) when (rejected.IsPermanent || rejected.Reason is RejectReason.RegistrationClosed)
                {
                    SetStatus(ConnectionStatus.Rejected, DescribeError(rejected.Reason));
                    await Release(conn);
                    return;
                }
                catch (Exception ex)
                {
                    SetStatus(ConnectionStatus.Failed, ConnectError(ex));
                }

                await Release(conn);
                ClearPairs();
                lastBroadcastHash = null;
                AllGone?.Invoke();

                var delay = RetryDelays[Math.Min(attempt++, RetryDelays.Length - 1)];
                try { await Task.Delay(delay, token); }
                catch (OperationCanceledException) { return; }
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref lifetime, null, cts);
        }
    }

    private async Task Release(SyncConnection conn)
    {
        Interlocked.CompareExchange(ref connection, null, conn);
        await DisposeQuietly(conn);
    }

    private bool IsCurrent(SyncConnection conn) => ReferenceEquals(Volatile.Read(ref connection), conn);

    private void Wire(SyncConnection conn, TaskCompletionSource<Exception?> closed)
    {
        conn.PairRequested += e => { if (IsCurrent(conn)) Upsert(e); };
        conn.PairChanged += e =>
        {
            if (!IsCurrent(conn))
                return;
            bool wasOnlinePair;
            lock (gate)
                wasOnlinePair = pairs.TryGetValue(e.Uid, out var old) && old.State == PairState.Paired && old.Online;
            Upsert(e);
            if (!wasOnlinePair && e.State == PairState.Paired && e.Online)
                PushTo([e.Uid]);
        };
        conn.PairRemoved += uid =>
        {
            if (!IsCurrent(conn))
                return;
            lock (gate)
                pairs.Remove(uid);
            PairGone?.Invoke(uid);
        };
        conn.CharacterDataUpdated += (uid, data, envelope) =>
        {
            if (!IsCurrent(conn))
                return;
            PairEntry? sender;
            lock (gate)
            {
                sender = pairs.TryGetValue(uid, out var e) && e.State == PairState.Paired ? e : null;
                if (sender is null && !ReferenceEquals(listed, conn))
                {
                    if (early.Count < MaxEarly || early.ContainsKey(uid))
                        early[uid] = (data, envelope);
                    return;
                }
            }
            if (sender is not null)
                Deliver(uid, sender, data, envelope);
        };
        conn.PairOnline += uid =>
        {
            if (!IsCurrent(conn))
                return;
            SetOnline(uid, true);
            PushTo([uid]);
        };
        conn.PairOffline += uid =>
        {
            if (!IsCurrent(conn))
                return;
            SetOnline(uid, false);
            PairGone?.Invoke(uid);
        };
        conn.Reconnecting += ex =>
        {
            if (IsCurrent(conn))
                SetStatus(ConnectionStatus.Reconnecting, ex?.GetBaseException().Message ?? string.Empty);
            return Task.CompletedTask;
        };
        conn.Reconnected += async _ =>
        {
            if (!IsCurrent(conn))
                return;
            SetStatus(ConnectionStatus.Connected, conn.HubUrl.Host);
            try { await RefreshAsync(conn, CancellationToken.None); }
            catch (Exception ex) { Plugin.Log.Error(ex, "Refresh after reconnect failed"); }
        };
        conn.Closed += ex =>
        {
            closed.TrySetResult(ex);
            return Task.CompletedTask;
        };
    }

    private async Task RefreshAsync(SyncConnection conn, CancellationToken token)
    {
        var id = await conn.GetMyPairingIdAsync(token);
        var list = await conn.PairListAsync(token);
        if (!IsCurrent(conn))
            return;
        MyId = id;
        List<string> gone;
        List<(string Uid, PairEntry Sender, CharacterData Data, string Envelope)> held;
        lock (gate)
        {
            gone = pairs.Keys.Where(uid => !list.Any(e => e.Uid == uid && e.State == PairState.Paired && e.Online)).ToList();
            pairs.Clear();
            foreach (var entry in list)
                pairs[entry.Uid] = entry;
            listed = conn;
            held = early
                .Where(kv => pairs.TryGetValue(kv.Key, out var e) && e.State == PairState.Paired)
                .Select(kv => (kv.Key, pairs[kv.Key], kv.Value.Data, kv.Value.Envelope))
                .ToList();
            early.Clear();
        }
        foreach (var uid in gone)
            PairGone?.Invoke(uid);
        foreach (var h in held)
            Deliver(h.Uid, h.Sender, h.Data, h.Envelope);
        try
        {
            await conn.RequestPairDataAsync(token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Plugin.Log.Error(ex, "Fetching pairs' data failed");
        }
        lastBroadcastHash = null;
        PushTo(null);
    }

    private void Deliver(string uid, PairEntry sender, CharacterData data, string envelope)
    {
        if (CharacterPush.Open(Identity, sender, data, envelope) is not { } opened)
            return;
        var fingerprint = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { opened.ContentId, data.FileReplacements, opened.State }));
        DataReceived?.Invoke(uid, new IncomingData(opened.ContentId, data, opened.State, Convert.ToHexStringLower(fingerprint)));
    }

    private void ClearPairs()
    {
        lock (gate)
        {
            pairs.Clear();
            early.Clear();
            listed = null;
        }
    }

    private void PushTo(IReadOnlyCollection<string>? only)
    {
        var conn = Volatile.Read(ref connection);
        if (conn is null || Volatile.Read(ref snapshot) is null || !IsConnected)
            return;
        if (Recipients(only).Count == 0)
            return;

        _ = Task.Run(async () =>
        {
            await pushGate.WaitAsync();
            try
            {
                var latest = Volatile.Read(ref snapshot);
                var recipients = Recipients(only);
                if (!IsCurrent(conn) || latest is null || recipients.Count == 0)
                    return;
                if (only is null && latest.Hash == lastBroadcastHash)
                    return;

                var failedUploads = await UploadMissingAsync(conn, latest);
                if (!IsCurrent(conn))
                    return;
                var (data, envelopes) = CharacterPush.Build(Identity, latest.Files, latest.State, latest.ContentId, recipients);
                if (envelopes.Count == 0)
                    return;
                var result = await conn.PushCharacterDataAsync(data, envelopes);
                LastPushIsError = result.Error is not null || failedUploads > 0;
                if (result.Error is not null)
                {
                    LastPush = $"Send failed: {DescribeError(result.Error)}";
                    return;
                }
                if (only is null)
                    lastBroadcastHash = latest.Hash;
                LastPush = $"Data sent to {result.Delivered} pair(s) at {DateTime.Now:HH:mm}."
                    + (UploadTotal > 0 ? $" Uploaded {UploadDone - failedUploads} new file(s)." : string.Empty)
                    + (failedUploads > 0 ? $" {failedUploads} upload(s) failed." : string.Empty);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Character data push failed");
                LastPushIsError = true;
                LastPush = $"Send failed: {ex.GetBaseException().Message}";
            }
            finally
            {
                Uploading = false;
                pushGate.Release();
            }
        });
    }

    private List<PairEntry> Recipients(IReadOnlyCollection<string>? only)
    {
        lock (gate)
            return pairs.Values
                .Where(p => p.State == PairState.Paired && p.Online && (only is null || only.Contains(p.Uid)))
                .ToList();
    }

    private async Task<int> UploadMissingAsync(SyncConnection conn, CharacterSnapshot latest)
    {
        UploadTotal = UploadDone = 0;
        if (latest.LocalFiles.Count == 0)
            return 0;
        var missing = await conn.FilesMissingAsync(latest.LocalFiles.Keys);
        if (missing.Count == 0)
            return 0;
        UploadTotal = missing.Count;
        Uploading = true;
        var failed = 0;
        foreach (var hash in missing)
        {
            if (!IsCurrent(conn))
                break;
            try
            {
                var result = await conn.UploadFileAsync(hash, latest.LocalFiles[hash]);
                if (result.Error is not null)
                    failed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
            }
            UploadDone++;
        }
        return failed;
    }

    private void Upsert(PairEntry entry)
    {
        lock (gate)
            pairs[entry.Uid] = entry;
    }

    private void SetOnline(string uid, bool online)
    {
        lock (gate)
        {
            if (pairs.TryGetValue(uid, out var entry))
                pairs[uid] = entry with { Online = online };
        }
    }

    private void Invoke(Func<SyncConnection, Task<(bool Ok, string Message)>> action)
    {
        var conn = connection;
        if (conn is null || !IsConnected)
        {
            SetMessage(false, "Not connected.");
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var (ok, message) = await action(conn);
                SetMessage(ok, message);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Sync request failed");
                SetMessage(false, $"Request failed: {ex.GetBaseException().Message}");
            }
        });
    }

    private static (bool, string) Ok(string message) => (true, message);
    private static (bool, string) Fail(string message) => (false, message);

    private void SetMessage(bool ok, string message)
    {
        LastMessageIsError = !ok;
        LastMessage = message;
    }

    private void SetStatus(ConnectionStatus status, string detail)
    {
        Status = status;
        StatusDetail = detail;
    }

    private bool TryGetServerUri(out Uri uri, out string error)
    {
        uri = null!;
        var text = configuration.EffectiveServerUrl();
        if (text.Length == 0)
        {
            error = "No custom server URL set. Open Settings.";
            return false;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("https" or "http" or "wss" or "ws"))
        {
            error = "The server URL must start with https://";
            return false;
        }
        uri = parsed;
        error = string.Empty;
        return true;
    }

    private static async Task DisposeQuietly(SyncConnection conn)
    {
        try { await conn.DisposeAsync(); }
        catch (Exception ex) { Plugin.Log.Error(ex, "Connection cleanup failed"); }
    }

    private static string ConnectError(Exception ex)
    {
        var root = ex.GetBaseException();
        return root switch
        {
            System.Security.Authentication.AuthenticationException => "The server certificate is invalid.",
            SyncRejectedException { Reason: null } r => (int)r.StatusCode switch
            {
                403 or 503 => "Connection to the server was blocked.",
                502 or 504 => "Server is currently offline.",
                404 => "Server is unreachable or not found.",
                _ => $"Server refused the connection: HTTP {(int)r.StatusCode}.",
            },
            SyncRejectedException r => DescribeError(r.Reason),
            _ => root.Message,
        };
    }

    public static string DescribeError(string? code) => code switch
    {
        RejectReason.UnknownKey => "User's key is invalid. Please contact server owner.",
        RejectReason.BadSignature => "Invalid key: could not be verified.",
        RejectReason.StaleTimestamp => "PC clock is out of date. Please verify the time is up to date on your PC.",
        RejectReason.Replay => "Connection rate limited. Retrying.",
        RejectReason.RegistrationLimited => "Key creation rate limited. Please try again later.",
        RejectReason.RegistrationClosed => "Server allowance is at capacity. Please contact the owner to increase it.",
        RejectReason.BadRequest => "Server error processing the request.",
        RejectReason.UpdateRequired => "A newer version of Lunar Sync is available and is needed to connect. Update it in the plugin installer.",
        PairError.NotFound => "No one has that pair code or alias.",
        PairError.Self => "That is your own pair code or alias.",
        RateLimitError.RateLimited => "Too many attempts. Wait a minute.",
        PushError.InvalidData => "Data for connection is invalid.",
        AliasError.Invalid => $"Aliases are {PairingId.AliasMinLength}-{PairingId.AliasMaxLength} characters, start with a letter, and use letters, digits, _ - .",
        AliasError.Taken => "That alias is taken.",
        null => "An error occurred connecting to the server.",
        _ => code,
    };
}
