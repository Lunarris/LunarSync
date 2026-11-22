namespace Sync.Protocol;

public interface ISyncHub
{
    Task<PingResult> Ping();
    Task<string> Whoami();

    Task<PairingIdInfo> GetMyPairingId();

    Task<SetAliasResult> SetAlias(string? alias);

    Task<RegenerateCodeResult> RegenerateMyPairCode();

    Task<SetLocatorKeyResult> SetLocatorKey(string publicKeyBase64);

    Task<PairRequestResult> PairRequest(string pairingId);

    Task<PairEntry?> PairAccept(string uid);

    Task<bool> PairRemove(string uid);

    Task<List<PairEntry>> PairList();

    Task<PushResult> PushCharacterData(CharacterData data, Dictionary<string, string> envelopes);

    Task<int> RequestPairData();

    Task<List<string>> FilesMissing(List<string> hashes);

    Task<UploadResult> UploadFile(string hash, IAsyncEnumerable<byte[]> content);

    IAsyncEnumerable<byte[]> DownloadFile(string hash, CancellationToken cancellationToken);
}

public interface ISyncHubClient
{
    Task PairRequested(PairEntry entry);

    Task PairChanged(PairEntry entry);

    Task PairRemoved(string uid);

    Task PairOnline(string uid);

    Task PairOffline(string uid);

    Task CharacterDataUpdated(string uid, CharacterData data, string envelope);
}

public static class SyncHubMethods
{
    public const string Ping = nameof(ISyncHub.Ping);
    public const string Whoami = nameof(ISyncHub.Whoami);
    public const string GetMyPairingId = nameof(ISyncHub.GetMyPairingId);
    public const string SetAlias = nameof(ISyncHub.SetAlias);
    public const string RegenerateMyPairCode = nameof(ISyncHub.RegenerateMyPairCode);
    public const string SetLocatorKey = nameof(ISyncHub.SetLocatorKey);
    public const string PairRequest = nameof(ISyncHub.PairRequest);
    public const string PairAccept = nameof(ISyncHub.PairAccept);
    public const string PairRemove = nameof(ISyncHub.PairRemove);
    public const string PairList = nameof(ISyncHub.PairList);
    public const string PushCharacterData = nameof(ISyncHub.PushCharacterData);
    public const string RequestPairData = nameof(ISyncHub.RequestPairData);
    public const string FilesMissing = nameof(ISyncHub.FilesMissing);
    public const string UploadFile = nameof(ISyncHub.UploadFile);
    public const string DownloadFile = nameof(ISyncHub.DownloadFile);
}

public static class SyncClientEvents
{
    public const string PairRequested = nameof(ISyncHubClient.PairRequested);
    public const string PairChanged = nameof(ISyncHubClient.PairChanged);
    public const string PairRemoved = nameof(ISyncHubClient.PairRemoved);
    public const string PairOnline = nameof(ISyncHubClient.PairOnline);
    public const string PairOffline = nameof(ISyncHubClient.PairOffline);
    public const string CharacterDataUpdated = nameof(ISyncHubClient.CharacterDataUpdated);
}

public sealed record PingResult(string Message, DateTimeOffset ServerTimeUtc)
{
    public const string Pong = "pong";
}

public enum SyncTier
{
    Pair = 0,
    Shell = 1,
    Zone = 2,
}
