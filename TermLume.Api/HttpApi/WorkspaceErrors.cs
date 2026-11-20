using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using TermLume.Shared.Models;
using Volo.Abp.AspNetCore.Mvc.ExceptionHandling;
using Volo.Abp.Authorization;
using Volo.Abp.Validation;

namespace TermLume.Api.HttpApi;

public sealed record WorkspaceErrorResponse(string Error);

internal static class WorkspaceErrors
{
    public static (int Status, string Message) Describe(Exception error) => error switch
    {
        WorkspaceConflictException => (409, "其他设备已修改工作区，请刷新后重试。"),
        WorkspaceAccessException or AuthenticationFailureException => (401, "设备或登录授权已撤销，请重新登录。"),
        WorkspacePermissionException permission => (403, permission.Message),
        AbpAuthorizationException => (403, "你没有此工作区的操作权限。"),
        KeyNotFoundException => (404, "记录不存在或无权访问。"),
        InvalidDataException invalid => (400, invalid.Message),
        JsonException or AbpValidationException => (400, "请求数据格式无效。"),
        BadHttpRequestException bad => (bad.StatusCode, "请求数据无效或超出大小限制。"),
        _ => (503, "工作区服务暂时不可用，请稍后重试。")
    };

    public static void LogUnavailable(ILogger logger, Exception error, HttpContext context, int status)
    {
        if (status == 503) logger.LogWarning("Workspace request failed: {ErrorType}, trace {TraceId}", error.GetType().Name, context.TraceIdentifier);
    }
}

/// <summary>保留桌面端错误协议；未知异常仅记录类型，避免输出令牌、SQL 和请求正文。</summary>
public class WorkspaceExceptionFilter(ILogger<WorkspaceExceptionFilter> logger) : AbpExceptionFilter
{
    public override Task OnExceptionAsync(ExceptionContext context)
    {
        if (context.ExceptionHandled) return Task.CompletedTask;
        if (context.Exception is OperationCanceledException && context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            context.ExceptionHandled = true;
            context.Result = new EmptyResult();
            return Task.CompletedTask;
        }
        var (status, message) = WorkspaceErrors.Describe(context.Exception);
        WorkspaceErrors.LogUnavailable(logger, context.Exception, context.HttpContext, status);
        context.Result = new ObjectResult(new WorkspaceErrorResponse(message)) { StatusCode = status };
        context.ExceptionHandled = true;
        return Task.CompletedTask;
    }
}

public sealed class WorkspaceApiBoundaryMiddleware(RequestDelegate next, ILogger<WorkspaceApiBoundaryMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        try
        {
            if (context.Request.Path.StartsWithSegments("/v1") && context.Request.Headers.ContainsKey("Origin"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception error)
        {
            var (status, message) = WorkspaceErrors.Describe(error);
            WorkspaceErrors.LogUnavailable(logger, error, context, status);
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = status;
                await context.Response.WriteAsJsonAsync(new WorkspaceErrorResponse(message), context.RequestAborted);
            }
        }
    }
}
