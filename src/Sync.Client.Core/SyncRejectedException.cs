using System.Net;
using Sync.Protocol;

namespace Sync.Client.Core;

public sealed class SyncRejectedException : Exception
{
    public SyncRejectedException(HttpStatusCode statusCode, string? reason, int? minProtocol = null)
        : base(reason is null
            ? $"Server rejected the connection with HTTP {(int)statusCode}."
            : $"Server rejected the connection with HTTP {(int)statusCode}: {reason}.")
    {
        StatusCode = statusCode;
        Reason = reason;
        MinProtocol = minProtocol;
    }

    public HttpStatusCode StatusCode { get; }

    public string? Reason { get; }

    public int? MinProtocol { get; }

    public bool IsUpdateRequired => Reason is RejectReason.UpdateRequired;

    public bool IsPermanent => Reason is RejectReason.UnknownKey or RejectReason.BadSignature or RejectReason.UpdateRequired;
}
