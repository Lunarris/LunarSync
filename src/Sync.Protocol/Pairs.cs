namespace Sync.Protocol;

public enum PairState
{
    PendingOutgoing,
    PendingIncoming,
    Paired,
}

public sealed record PairEntry(
    string Uid,
    PairState State,
    string PairCode,
    string? Alias,
    bool Online,
    string? LocatorPublicKey)
{
    public string DisplayName => Alias ?? PairingId.FormatCode(PairCode);
}

public static class PairError
{
    public const string NotFound = "not_found";
    public const string Self = "self";
}

public static class RateLimitError
{
    public const string RateLimited = "rate_limited";
}

public sealed record PairRequestResult(string? Error, PairEntry? Entry)
{
    public bool Success => Error is null;
}

public sealed record RegenerateCodeResult(string? Error, PairingIdInfo Current)
{
    public bool Success => Error is null;
}
