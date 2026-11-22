namespace Sync.Protocol;

public enum SyncObjectKind
{
    Player = 0,
    MinionOrMount = 1,
    Companion = 2,
    Pet = 3,
}

public sealed record FileReplacement(string[] GamePaths, string Hash, string? FileSwapPath);

public sealed record CharacterData(Dictionary<SyncObjectKind, List<FileReplacement>> FileReplacements, string SealedState);

public sealed class CharacterState
{
    public Dictionary<SyncObjectKind, string> Glamourer { get; set; } = [];
    public Dictionary<SyncObjectKind, string> CustomizePlus { get; set; } = [];
    public Dictionary<SyncObjectKind, string> Loci { get; set; } = [];
    public string Manipulation { get; set; } = string.Empty;
    public string Heels { get; set; } = string.Empty;
    public string Honorific { get; set; } = string.Empty;
    public string Moodles { get; set; } = string.Empty;
    public string PetNames { get; set; } = string.Empty;

    public string ClientBuild { get; set; } = string.Empty;
}

public sealed record PushResult(string? Error, int Delivered);

public static class PushError
{
    public const string InvalidData = "invalid_data";
}

public static class CharacterDataLimits
{
    public const int MaxFileEntries = 10_000;
    public const int MaxGamePathsPerEntry = 64;
    public const int MaxPathLength = 260;
    public const int HashLength = 64;
    public const int MaxSealedStateChars = 4 * 1024 * 1024;
    public const int MaxRecipients = 500;
    public const int MaxEnvelopeChars = 256;
    public const long MaxMessageBytes = 16 * 1024 * 1024;

    public static readonly string[] AllowedExtensions =
        [".mdl", ".tex", ".mtrl", ".tmb", ".pap", ".avfx", ".atex", ".sklb", ".eid", ".phyb", ".pbd", ".scd", ".skp", ".shpk"];

    public static bool HasAllowedExtension(string path) =>
        AllowedExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    public static string? Validate(CharacterData? data)
    {
        if (data?.FileReplacements is null || data.SealedState is null)
            return "missing fields";
        if (data.SealedState.Length > MaxSealedStateChars)
            return "sealed state too large";
        var total = 0;
        foreach (var (kind, list) in data.FileReplacements)
        {
            if (!Enum.IsDefined(kind))
                return "unknown object kind";
            if (list is null)
                return "missing file list";
            total += list.Count;
            if (total > MaxFileEntries)
                return "too many files";
            if (!list.All(IsValidEntry))
                return "bad file entry";
        }
        return null;
    }

    public static bool IsValidEntry(FileReplacement? file)
    {
        if (file?.GamePaths is null || file.GamePaths.Length == 0 || file.GamePaths.Length > MaxGamePathsPerEntry
            || !file.GamePaths.All(IsGamePath))
            return false;
        return string.IsNullOrEmpty(file.FileSwapPath)
            ? IsHash(file.Hash)
            : string.IsNullOrEmpty(file.Hash) && IsGamePath(file.FileSwapPath);
    }

    public static bool IsGamePath(string? path) =>
        !string.IsNullOrEmpty(path) && path.Length <= MaxPathLength && !path.Contains(':') && !path.Contains('\\')
        && !path.StartsWith('/') && !path.Contains("..") && HasAllowedExtension(path);

    public static bool IsHash(string? hash) =>
        hash is { Length: HashLength } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
