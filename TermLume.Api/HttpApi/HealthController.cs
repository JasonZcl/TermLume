using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.Auditing;
using Volo.Abp.Uow;

namespace TermLume.Api.HttpApi;

[ApiController]
[AllowAnonymous]
[Route("healthz")]
[DisableAuditing]
[UnitOfWork(IsDisabled = true)]
public class HealthController(NpgsqlDataSource database) : AbpController
{
    [HttpGet]
    public async Task<object> GetAsync(CancellationToken token)
    {
        await using var command = database.CreateCommand("SELECT 1");
        await command.ExecuteScalarAsync(token);
        return new { status = "ok", version = TermLumeApiModule.ApiVersion, framework = "ABP" };
    }
}
