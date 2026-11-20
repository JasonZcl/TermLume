using TermLume.Shared.Models;

namespace TermLume.Shared.Abstractions;

public interface IWorkspaceSynchronization : IDisposable
{
    bool Enabled { get; }
    string Scope { get; }
    Task<WorkspaceSnapshot> ReadAsync(CancellationToken cancellationToken);
    Task<WorkspaceSnapshot> ChangeAsync(WorkspaceChange change, CancellationToken cancellationToken);
    Task<WorkspaceSnapshot> ImportAsync(WorkspaceImport import, CancellationToken cancellationToken);
}

// 访问令牌只供共享核心调用业务服务，不属于发送给网页的账号状态。
public interface IWorkspaceAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);
}

public interface IWorkspaceAdministration
{
    string? SelectedWorkspaceId { get; set; }
    Task<System.Text.Json.JsonElement> ExecuteAsync(string action, System.Text.Json.JsonElement payload, CancellationToken cancellationToken);
}
