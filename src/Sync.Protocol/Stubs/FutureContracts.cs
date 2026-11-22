namespace Sync.Protocol.Stubs;

public interface IShellHub
{
    Task<ShellInfo> ShellCreate(string name);
    Task<ShellInfo> ShellJoin(string inviteCode);
    Task ShellLeave(string shellId);
    Task<IReadOnlyList<ShellMember>> ShellMembers(string shellId);
}

public interface IShellHubClient
{
    Task ShellMemberJoined(string shellId, ShellMember member);
    Task ShellMemberLeft(string shellId, string uid);
}

public sealed record ShellInfo(string ShellId, string Name, string OwnerUid, string? InviteCode);

public sealed record ShellMember(string Uid, bool Online);
