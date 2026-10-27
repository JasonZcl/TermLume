namespace TermLume.Shared.Abstractions;

/// <summary>将会话回调送回工作区所属线程；调用方也应在该线程处理请求。</summary>
public interface IWorkspaceDispatcher
{
    void BeginInvoke(Action action);
    void Invoke(Action action);
    T Invoke<T>(Func<T> action);
}
