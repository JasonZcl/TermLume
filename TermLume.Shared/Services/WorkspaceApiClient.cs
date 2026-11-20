using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TermLume.Shared.Abstractions;
using TermLume.Shared.Models;

namespace TermLume.Shared.Services;

public sealed class WorkspaceApiOptions
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:18741";

    public static WorkspaceApiOptions Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "workspace-api.json");
        var result = File.Exists(path) ? JsonSerializer.Deserialize<WorkspaceApiOptions>(File.ReadAllText(path), WorkspaceStore.JsonOptions)
            ?? throw new InvalidDataException("工作区服务配置无效。") : new WorkspaceApiOptions();
        result.BaseUrl = Environment.GetEnvironmentVariable("TERMLUME_WORKSPACE_API_URL") ?? result.BaseUrl;
        result.Validate();
        return result;
    }

    public void Validate()
    {
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 || uri.AbsolutePath != "/" || BaseUrl.Contains('\\') || BaseUrl.Any(char.IsControl) ||
            uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
            throw new InvalidDataException("工作区服务必须为 HTTPS 来源，本机回环地址允许 HTTP。");
        BaseUrl = BaseUrl.TrimEnd('/');
    }
}

public sealed class WorkspaceApiClient : IWorkspaceSynchronization, IWorkspaceAdministration
{
    private readonly WorkspaceApiOptions options;
    private readonly IWorkspaceAccessTokenProvider authentication;
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
    private readonly DeviceRegistration? device;
    public string? SelectedWorkspaceId { get; set; }
    private static string Platform => OperatingSystem.IsMacOS() ? "macos" : "windows";
    public bool Enabled => true;
    public string Scope => options.BaseUrl;

    public WorkspaceApiClient(WorkspaceApiOptions options, IWorkspaceAccessTokenProvider authentication, DeviceRegistration? device = null)
    {
        options.Validate();
        this.options = options;
        this.authentication = authentication;
        this.device = device;
    }

    public Task<WorkspaceSnapshot> ReadAsync(CancellationToken cancellationToken) => SendAsync(HttpMethod.Get, "/v1/workspace", null, cancellationToken);
    public Task<WorkspaceSnapshot> ChangeAsync(WorkspaceChange change, CancellationToken cancellationToken) => SendAsync(HttpMethod.Post, "/v1/workspace/changes", change, cancellationToken);
    public Task<WorkspaceSnapshot> ImportAsync(WorkspaceImport import, CancellationToken cancellationToken) => SendAsync(HttpMethod.Post, "/v1/workspace/import", import, cancellationToken);

    public Task<JsonElement> ExecuteAsync(string action, JsonElement payload, CancellationToken cancellationToken) => RequestAsync(HttpMethod.Post, "/v1/workspace/actions", new { action, payload }, cancellationToken);

    private async Task<WorkspaceSnapshot> SendAsync(HttpMethod method, string route, object? body, CancellationToken cancellationToken)
    {
        var json = await RequestAsync(method, route, body, cancellationToken);
        var snapshot = json.Deserialize<WorkspaceSnapshot>(WorkspaceStore.JsonOptions) ?? throw new InvalidDataException("工作区服务返回了无效数据。");
        WorkspaceChanges.Validate(snapshot.Workspace, Platform == "macos");
        if (snapshot.Revision < 0) throw new InvalidDataException("工作区版本无效。");
        return snapshot;
    }

    private async Task<JsonElement> RequestAsync(HttpMethod method, string route, object? body, CancellationToken cancellationToken)
    {
        var selected = SelectedWorkspaceId;
        using var request = new HttpRequestMessage(method, options.BaseUrl + route);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await authentication.GetAccessTokenAsync(cancellationToken));
        request.Headers.Add("X-TermLume-Platform", Platform);
        if (selected is not null) request.Headers.Add("X-TermLume-Workspace", selected);
        if (device is not null) { request.Headers.Add("X-TermLume-Device", device.Id); request.Headers.Add("X-TermLume-Version", device.Version); }
        if (body is not null) request.Content = JsonContent.Create(body, options: WorkspaceStore.JsonOptions);
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new WorkspaceAccessException();
            if (response.StatusCode == HttpStatusCode.Forbidden) throw new WorkspacePermissionException();
            if (response.StatusCode == HttpStatusCode.Conflict) throw new WorkspaceConflictException();
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode is 400 or 404 or 413)
                {
                    var problem = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
                    if (problem.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String && error.GetString() is { Length: < 400 } message)
                        throw new InvalidDataException(message);
                }
                throw new IOException("工作区服务暂时不可用，请稍后重试。");
            }
            return await response.Content.ReadFromJsonAsync<JsonElement>(WorkspaceStore.JsonOptions, cancellationToken);
        }
        catch (HttpRequestException) { throw new IOException("无法连接工作区服务，请检查服务地址和网络。"); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new IOException("工作区服务响应超时，请重试。"); }
    }

    public void Dispose() => http.Dispose();
}
