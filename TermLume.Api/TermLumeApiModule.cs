using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using TermLume.Api.HttpApi;
using TermLume.Api.Security;
using Volo.Abp;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.AspNetCore.Mvc.ExceptionHandling;
using Volo.Abp.Auditing;
using Volo.Abp.Autofac;
using Volo.Abp.Json.SystemTextJson;
using Volo.Abp.Modularity;

namespace TermLume.Api;

/// <summary>工作区 API 的 ABP 启动模块，集中配置认证、桌面协议及服务生命周期。</summary>
[DependsOn(typeof(AbpAspNetCoreMvcModule), typeof(AbpAutofacModule))]
public sealed class TermLumeApiModule : AbpModule
{
    public const string ApiVersion = "0.9.0";

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var configuration = context.Services.GetConfiguration();
        var connection = configuration.GetConnectionString("Workspace")
            ?? throw new InvalidOperationException("Configure the server-side workspace database connection.");
        context.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(options =>
            options.Limits.MaxRequestBodySize = 3 * 1024 * 1024);
        context.Services.AddSingleton(_ => NpgsqlDataSource.Create(connection));
        context.Services.AddSingleton<WorkspaceRepository>();
        context.Services.AddHostedService<ServerLogRetentionService>();
        context.Services.AddSingleton<AegisAccessTokenValidator>();
        context.Services.AddScoped<WorkspaceRequestContext>();
        context.Services.AddHttpContextAccessor();
        // This bearer-only host issues no cookies or persistent data-protection payloads.
        context.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        context.Services.AddAuthentication(AegisBearerHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, AegisBearerHandler>(AegisBearerHandler.SchemeName, _ => { });
        context.Services.AddAuthorization();

        // Keep the desktop wire format, strict input parsing and safe error messages.
        Configure<AbpSystemTextJsonSerializerOptions>(options => ConfigureJson(options.JsonSerializerOptions));
        Configure<JsonOptions>(options => ConfigureJson(options.JsonSerializerOptions));
        Configure<ApiBehaviorOptions>(options => options.InvalidModelStateResponseFactory = _ =>
            new BadRequestObjectResult(new WorkspaceErrorResponse("请求数据格式无效。")));
        context.Services.Replace(ServiceDescriptor.Transient<AbpExceptionFilter, WorkspaceExceptionFilter>());

        // Business auditing stays in PostgreSQL. Parameter auditing could copy private data.
        Configure<AbpAuditingOptions>(options => options.IsEnabled = false);
        context.Services.AddControllers().ConfigureApplicationPartManager(manager =>
            manager.FeatureProviders.Add(new WorkspaceControllerFeatureProvider()));
    }

    private static void ConfigureJson(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.PropertyNameCaseInsensitive = true;
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        options.RespectRequiredConstructorParameters = true;
        options.ReadCommentHandling = JsonCommentHandling.Disallow;
        options.AllowTrailingCommas = false;
    }

    public override void OnApplicationInitialization(ApplicationInitializationContext context)
    {
        var app = context.GetApplicationBuilder();
        app.UseMiddleware<WorkspaceApiBoundaryMiddleware>();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseConfiguredEndpoints();
    }

    private sealed class WorkspaceControllerFeatureProvider : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            // Expose only the documented desktop endpoints, including no ABP infrastructure APIs.
            foreach (var controller in feature.Controllers.ToArray())
                if (controller.AsType() != typeof(WorkspaceController) && controller.AsType() != typeof(HealthController))
                    feature.Controllers.Remove(controller);
        }
    }
}
