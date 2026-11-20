using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using TermLume.Api.Security;
using TermLume.Shared.Models;
using Volo.Abp;
using Volo.Abp.Application.Services;
using Volo.Abp.Auditing;
using Volo.Abp.Uow;

namespace TermLume.Api.Application;

public interface IWorkspaceAppService : IApplicationService
{
    Task<WorkspaceSnapshot> GetAsync(CancellationToken token);
    Task<WorkspaceSnapshot> ChangeAsync(WorkspaceChange input, CancellationToken token);
    Task<WorkspaceSnapshot> ImportAsync(WorkspaceImport input, CancellationToken token);
    Task<object> ActionAsync(WorkspaceAction input, CancellationToken token);
}

[Authorize]
[RemoteService(false)]
[DisableAuditing]
[UnitOfWork(IsDisabled = true)]
public class WorkspaceAppService(WorkspaceRepository repository, WorkspaceRequestContext request) : ApplicationService, IWorkspaceAppService
{
    // The repository owns transactions and transaction-local PostgreSQL RLS context.
    public virtual Task<WorkspaceSnapshot> GetAsync(CancellationToken token) =>
        repository.ReadAsync(request.Identity, request.Platform, token, request.SelectedWorkspace);

    public virtual Task<WorkspaceSnapshot> ChangeAsync(WorkspaceChange input, CancellationToken token) =>
        repository.ChangeAsync(request.Identity, request.Platform, input, token, request.SelectedWorkspace);

    public virtual Task<WorkspaceSnapshot> ImportAsync(WorkspaceImport input, CancellationToken token) =>
        repository.ImportAsync(request.Identity, request.Platform, input, token, request.SelectedWorkspace);

    public virtual Task<object> ActionAsync(WorkspaceAction input, CancellationToken token) =>
        repository.AdministrationAsync(request.Identity, request.Platform, request.SelectedWorkspace, input.Action, input.Payload, token);
}

public sealed record WorkspaceAction(string Action, JsonElement Payload);
