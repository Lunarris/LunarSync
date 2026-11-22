using System.Text;

namespace Sync.Protocol;

public static class AuthProtocol
{
    public const string HubPath = "/hub";

    public const string PublicKeyHeader = "X-Sync-PublicKey";
    public const string TimestampHeader = "X-Sync-Timestamp";
    public const string SignatureHeader = "X-Sync-Signature";

    public const string RejectReasonHeader = "X-Sync-Reject-Reason";

    public const string ProtocolHeader = "X-Sync-Protocol";
    public const string MinProtocolHeader = "X-Sync-Min-Protocol";

    public const int ProtocolVersion = 2;
    public const int UnversionedProtocol = 1;

    public const string SigningVersion = "sync-auth-v1";

    public const int DefaultTimestampWindowSeconds = 60;

    public const int PublicKeyLength = 32;
    public const int PrivateKeyLength = 32;
    public const int SignatureLength = 64;

    public static byte[] BuildSigningBytes(long unixSeconds) =>
        Encoding.UTF8.GetBytes($"{SigningVersion}\n{unixSeconds}\n{HubPath}");
}

public static class RejectReason
{
    public const string BadRequest = "bad_request";
    public const string UnknownKey = "unknown_key";
    public const string BadSignature = "bad_signature";
    public const string StaleTimestamp = "stale_timestamp";
    public const string Replay = "replay";

    public const string RegistrationLimited = "registration_limited";

    public const string RegistrationClosed = "registration_closed";

    public const string UpdateRequired = "update_required";
}
