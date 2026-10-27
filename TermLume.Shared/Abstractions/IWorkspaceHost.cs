namespace TermLume.Shared.Abstractions;

/// <summary>平台提供的事件出口和原生交互；核心不依赖窗口或进程通信实现。</summary>
public interface IWorkspaceHost
{
    void Send(object message);
    Task<T?> RequestAsync<T>(string operation, object? payload = null);
}
