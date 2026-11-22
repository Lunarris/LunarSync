using System.Net.WebSockets;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Sync.Protocol;

namespace Sync.Client.Core;

public sealed class SyncConnection : IAsyncDisposable
{
    private readonly HubConnection _hub;
    private readonly IAuthHeaderSource _auth;

    private readonly IdentityStore? _identity;

    public SyncConnection(Uri serverUrl, IdentityStore identity, IRetryPolicy? retryPolicy = null)
        : this(serverUrl, new SignedAuthHeaderSource(identity), retryPolicy)
    {
        _identity = identity;
    }

    public SyncConnection(Uri serverUrl, IAuthHeaderSource auth, IRetryPolicy? retryPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(serverUrl);
        _auth = auth;
        HubUrl = ToHubUrl(serverUrl);

        _hub = new HubConnectionBuilder()
            .WithUrl(HubUrl, HttpTransportType.WebSockets, o =>
            {
                o.SkipNegotiation = true;
                o.Transports = HttpTransportType.WebSockets;
                o.WebSocketFactory = OpenWebSocketAsync;
            })
            .WithAutomaticReconnect(retryPolicy ?? new JitteredBackoffPolicy())
            .AddMessagePackProtocol()
            .Build();

        _hub.On<PairEntry>(SyncClientEvents.PairRequested, e => PairRequested?.Invoke(e));
        _hub.On<PairEntry>(SyncClientEvents.PairChanged, e => PairChanged?.Invoke(e));
        _hub.On<string>(SyncClientEvents.PairRemoved, uid => PairRemoved?.Invoke(uid));
        _hub.On<string>(SyncClientEvents.PairOnline, uid => PairOnline?.Invoke(uid));
        _hub.On<string>(SyncClientEvents.PairOffline, uid => PairOffline?.Invoke(uid));
        _hub.On<string, CharacterData, string>(SyncClientEvents.CharacterDataUpdated,
            (uid, data, envelope) => CharacterDataUpdated?.Invoke(uid, data, envelope));

        _hub.Reconnecting += e => Reconnecting?.Invoke(e) ?? Task.CompletedTask;
        _hub.Reconnected += id => Reconnected?.Invoke(id) ?? Task.CompletedTask;
        _hub.Closed += e => Closed?.Invoke(e) ?? Task.CompletedTask;
    }

    public Uri HubUrl { get; }

    public HubConnectionState State => _hub.State;

    public event Func<Exception?, Task>? Reconnecting;
    public event Func<string?, Task>? Reconnected;
    public event Func<Exception?, Task>? Closed;

    public event Action<PairEntry>? PairRequested;

    public event Action<PairEntry>? PairChanged;

    public event Action<string>? PairRemoved;

    public event Action<string>? PairOnline;
    public event Action<string>? PairOffline;

    public event Action<string, CharacterData, string>? CharacterDataUpdated;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _hub.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (FindRejection(ex) is { } rejection && !ReferenceEquals(rejection, ex))
        {
            throw rejection;
        }

        if (_identity is not null)
        {
            var result = await SetLocatorKeyAsync(_identity.LocatorPublicKeyBase64, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                await _hub.StopAsync(CancellationToken.None).ConfigureAwait(false);
                throw new InvalidOperationException($"Server refused the locator key: {result.Error}.");
            }
        }
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default) => _hub.StopAsync(cancellationToken);

    public Task<PingResult> PingAsync(CancellationToken cancellationToken = default) =>
        _hub.InvokeAsync<PingResult>(SyncHubMethods.Ping, cancellationToken);

    public Task<string> WhoamiAsync(CancellationToken cancellationToken = default) =>
        _hub.InvokeAsync<string>(SyncHubMethods.Whoami, cancellationToken);

    public Task<PairingIdInfo> GetMyPairingIdAsync(CancellationToken cancellationToken = default) =>
        _hub.InvokeAsync<PairingIdInfo>(SyncHubMethods.GetMyPairingId, cancellationToken);

    public Task<SetAliasResult> SetAliasAsync(string? alias, CancellationToken cancellationToken = default) =>
        _hub.InvokeAsync<SetAliasResult>(SyncHubMethods.SetAlias, alias, cancellationToken);

    public Task<RegenerateCodeResult> RegenerateMyPairCodeAsync(CancellationToken cancellationToken = default) =>
        _hub.InvokeAsync<RegenerateCodeResult>(SyncHubMethods.RegenerateMyPairCode, cancellationToken);

    public Task<PairRequestResult> PairRequestAsync(string pairingId, CancellationToken cancellationToken = default) =>
        _hub.InvokeAsync<PairRequestResult>(SyncHubMethods.PairRequest, pairingId, cancellationToken);

    public Task<PairEntry?> PairAcceptAsync(string uid, CancellationToken cancellationToken = default) =>
        _hub.InvokeAsync<PairEntry?>(SyncHubMethods.PairAccept, uid, cancellationToken);

    public Task<bool> PairRemoveAsync(string uid, CancellationToken cancellationToken = default) =>
        _hub.InvokeAsync<bool>(SyncHubMethods.PairRemove, uid, cancellationToken);

    public Task<List<PairEntry>> PairListAsync(CancellationToken cancellationToken = default) =>
        _hub.InvokeAsync<List<PairEntry>>(SyncHubMethods.PairList, cancellationToken);

    public Task<PushResult> PushCharacterDataAsync(CharacterData data, Dictionary<string, string> envelopes,
        CancellationToken cancellationToken = default) =>
        _hub.InvokeAsync<PushResult>(SyncHubMethods.PushCharacterData, data, envelopes, cancellationToken);

    public Task<int> RequestPairDataAsync(CancellationToken cancellationToken = default) =>
        _hub.InvokeAsync<int>(SyncHubMethods.RequestPairData, cancellationToken);

    public Task<List<string>> FilesMissingAsync(IEnumerable<string> hashes, CancellationToken cancellationToken = default) =>
        _hub.InvokeAsync<List<string>>(SyncHubMethods.FilesMissing, hashes.ToList(), cancellationToken);

    public async Task<UploadResult> UploadFileAsync(string hash, string path, CancellationToken cancellationToken = default)
    {
        using var sending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            return await _hub.InvokeAsync<UploadResult>(SyncHubMethods.UploadFile, hash,
                FileCodec.EncodeFileAsync(path, sending.Token), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await sending.CancelAsync().ConfigureAwait(false);
        }
    }

    public async Task<(long Size, long Received)> DownloadFileAsync(string hash, string destination, CancellationToken cancellationToken = default)
    {
        if (!FileTransferLimits.IsHash(hash))
            throw new ArgumentException("Not a file hash.", nameof(hash));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            long size;
            long received = 0;
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            using (var decoder = new ChunkDecoder(file))
            {
                await foreach (var chunk in _hub.StreamAsync<byte[]>(SyncHubMethods.DownloadFile, hash, cancellationToken).ConfigureAwait(false))
                {
                    received += chunk.Length;
                    await decoder.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
                }
                if (!string.Equals(decoder.Finish(), hash, StringComparison.Ordinal))
                    throw new InvalidDataException("The downloaded file does not match its hash.");
                size = decoder.Size;
            }
            File.Move(temp, destination, overwrite: true);
            return (size, received);
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch (IOException)
            {
            }
        }
    }

    public static bool IsFileError(Exception ex, string code) =>
        ex is HubException hub && hub.Message.EndsWith(code, StringComparison.Ordinal);

    public Task<SetLocatorKeyResult> SetLocatorKeyAsync(string publicKeyBase64, CancellationToken cancellationToken = default) =>
        _hub.InvokeAsync<SetLocatorKeyResult>(SyncHubMethods.SetLocatorKey, publicKeyBase64, cancellationToken);

    public ValueTask DisposeAsync() => _hub.DisposeAsync();

    public static SyncRejectedException? FindRejection(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SyncRejectedException rejected)
                return rejected;
            if (current is AggregateException agg)
            {
                foreach (var inner in agg.InnerExceptions)
                {
                    if (FindRejection(inner) is { } found)
                        return found;
                }
            }
        }
        return null;
    }

    private async ValueTask<WebSocket> OpenWebSocketAsync(WebSocketConnectionContext context, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        foreach (var (name, value) in _auth.Create().AsPairs())
            socket.Options.SetRequestHeader(name, value);

        try
        {
            await socket.ConnectAsync(ToWebSocketUri(context.Uri), cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch (WebSocketException) when (socket.HttpStatusCode != 0)
        {
            string? reason = null;
            int? minProtocol = null;
            if (socket.HttpResponseHeaders is { } headers)
            {
                if (headers.TryGetValue(AuthProtocol.RejectReasonHeader, out var values))
                    reason = values.FirstOrDefault();
                if (headers.TryGetValue(AuthProtocol.MinProtocolHeader, out var mins) &&
                    int.TryParse(mins.FirstOrDefault(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var min))
                    minProtocol = min;
            }
            var status = socket.HttpStatusCode;
            socket.Dispose();
            throw new SyncRejectedException(status, reason, minProtocol);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static Uri ToHubUrl(Uri serverUrl)
    {
        var builder = new UriBuilder(serverUrl);
        if (!builder.Path.TrimEnd('/').EndsWith(AuthProtocol.HubPath, StringComparison.OrdinalIgnoreCase))
            builder.Path = builder.Path.TrimEnd('/') + AuthProtocol.HubPath;
        return builder.Uri;
    }

    private static Uri ToWebSocketUri(Uri uri)
    {
        var builder = new UriBuilder(uri);
        builder.Scheme = builder.Scheme switch
        {
            "http" => "ws",
            "https" => "wss",
            _ => builder.Scheme,
        };
        if (builder.Uri.IsDefaultPort)
            builder.Port = -1;
        return builder.Uri;
    }
}
