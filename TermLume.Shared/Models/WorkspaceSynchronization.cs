using System.Text.Json;

namespace TermLume.Shared.Models;

public sealed record WorkspaceSnapshot(long Revision, Workspace Workspace, WorkspaceDescriptor? Context = null);
public sealed record WorkspaceChange(long ExpectedRevision, string Operation, JsonElement Payload, string? OperationId = null, string? NewId = null);
public sealed record WorkspaceImport(long ExpectedRevision, Workspace Workspace);

public sealed class WorkspaceConflictException() : IOException("其他设备已修改工作区，请刷新后重试。");
public sealed class WorkspaceAccessException() : IOException("工作区授权已失效，请重新登录。");
public sealed class WorkspacePermissionException(string message = "你没有此工作区的操作权限。") : IOException(message);

public sealed record WorkspaceDescriptor(string Id, string Name, string Kind, string Role, long Revision)
{
    public bool CanWrite => Role is "owner" or "admin" or "editor";
    public bool CanManage => Role is "owner" or "admin";
}
public sealed record DeviceRegistration(string Id, string Name, string Platform, string Version);
public sealed record WorkspaceCreate(string Name);
public sealed record WorkspaceInviteRequest(string Role, int Hours = 24);
public sealed record WorkspaceJoin(string Code);
public sealed record WorkspaceMemberChange(string UserId, string Role);
public sealed record WorkspaceRestore(long ExpectedRevision, long Revision);
public sealed record ActivityUpload(List<ActivityLogEntry> Entries);
public sealed record TrustPublish(KnownHostEntry Entry);
