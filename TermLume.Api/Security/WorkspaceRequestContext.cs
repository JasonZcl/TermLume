using Microsoft.AspNetCore.Authentication;

namespace TermLume.Api.Security;

/// <summary>请求级上下文：身份只能来自认证处理器，设备和工作区标识仍须由仓储核验权限。</summary>
public sealed class WorkspaceRequestContext(IHttpContextAccessor accessor)
{
    private HttpContext Context => accessor.HttpContext ?? throw new InvalidOperationException("No active workspace request.");

    public ApiIdentity Identity
    {
        get
        {
            if (Context.User.Identity?.IsAuthenticated != true || Context.Features.Get<ApiIdentity>() is not { } identity)
                throw new AuthenticationFailureException("A verified Aegis identity is required.");
            var device = Context.Request.Headers["X-TermLume-Device"];
            Guid? deviceId = null;
            if (device.Count > 0)
            {
                if (device.Count != 1 || !Guid.TryParseExact(device[0], "N", out var parsed))
                    throw new InvalidDataException("设备标识无效。");
                deviceId = parsed;
            }
            var version = Context.Request.Headers["X-TermLume-Version"];
            if (version.Count > 1 || version.Count == 1 && (version[0]!.Length > 40 || version[0]!.Any(c => c < 32 || c > 126)))
                throw new InvalidDataException("客户端版本无效。");
            return identity with { DeviceId = deviceId, Version = version.Count == 1 ? version[0]! : "unknown" };
        }
    }

    public string? SelectedWorkspace
    {
        get
        {
            var header = Context.Request.Headers["X-TermLume-Workspace"];
            if (header.Count == 0) return null;
            if (header.Count != 1 || !Guid.TryParseExact(header[0], "N", out _))
                throw new InvalidDataException("工作区标识无效。");
            return header[0];
        }
    }

    public string Platform => Context.Request.Headers["X-TermLume-Platform"] switch
    {
        { Count: 0 } => "windows",
        { Count: 1 } value => value[0]!,
        _ => throw new InvalidDataException("设备平台无效。")
    };
}
