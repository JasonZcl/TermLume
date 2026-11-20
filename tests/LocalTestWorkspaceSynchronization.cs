using TermLume.Shared.Abstractions;
using TermLume.Shared.Models;

// 纯本地核心回归显式注入存储替身；生产入口始终创建真实 API 客户端。
internal sealed class LocalTestWorkspaceSynchronization : IWorkspaceSynchronization
{
    public bool Enabled => false;
    public string Scope => "isolated-core-test";
    public Task<WorkspaceSnapshot> ReadAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected synchronization in a local core regression");
    public Task<WorkspaceSnapshot> ChangeAsync(WorkspaceChange change, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected synchronization in a local core regression");
    public Task<WorkspaceSnapshot> ImportAsync(WorkspaceImport import, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected synchronization in a local core regression");
    public void Dispose() { }
}
