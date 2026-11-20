using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TermLume.Api.Application;
using TermLume.Shared.Models;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.Auditing;
using Volo.Abp.Uow;

namespace TermLume.Api.HttpApi;

[ApiController]
[Authorize]
[Route("v1/workspace")]
[DisableAuditing]
[UnitOfWork(IsDisabled = true)]
[IgnoreAntiforgeryToken]
public class WorkspaceController(IWorkspaceAppService service) : AbpController
{
    [HttpGet]
    public Task<WorkspaceSnapshot> GetAsync(CancellationToken token) => service.GetAsync(token);

    [HttpPost("changes")]
    public Task<WorkspaceSnapshot> ChangeAsync([FromBody] WorkspaceChange input, CancellationToken token) => service.ChangeAsync(input, token);

    [HttpPost("import")]
    public Task<WorkspaceSnapshot> ImportAsync([FromBody] WorkspaceImport input, CancellationToken token) => service.ImportAsync(input, token);

    [HttpPost("actions")]
    public Task<object> ActionAsync([FromBody] WorkspaceAction input, CancellationToken token) => service.ActionAsync(input, token);
}
