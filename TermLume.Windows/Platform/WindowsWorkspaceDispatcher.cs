using System.Windows.Threading;
using TermLume.Shared.Abstractions;

namespace TermLume.Windows.Platform;

internal sealed class WindowsWorkspaceDispatcher(Dispatcher dispatcher) : IWorkspaceDispatcher
{
    public void BeginInvoke(Action action) => dispatcher.BeginInvoke(action);
    public void Invoke(Action action) => dispatcher.Invoke(action);
    public T Invoke<T>(Func<T> action) => dispatcher.Invoke(action);
}
