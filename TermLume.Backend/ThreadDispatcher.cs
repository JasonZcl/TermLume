using System.Collections.Concurrent;

namespace TermLume.Backend;

internal sealed class ThreadDispatcher : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<Action> actions = new();
    private readonly int owner = Environment.CurrentManagedThreadId;

    public override void Post(SendOrPostCallback callback, object? state) => BeginInvoke(() => callback(state));

    public void BeginInvoke(Action action)
    {
        try { actions.Add(action); }
        catch (InvalidOperationException) { }
    }

    public void Invoke(Action action) => Invoke(() => { action(); return true; });

    public T Invoke<T>(Func<T> action)
    {
        if (Environment.CurrentManagedThreadId == owner) return action();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginInvoke(() =>
        {
            try { completion.TrySetResult(action()); }
            catch (Exception exception) { completion.TrySetException(exception); }
        });
        return completion.Task.GetAwaiter().GetResult();
    }

    public void Run()
    {
        SetSynchronizationContext(this);
        foreach (var action in actions.GetConsumingEnumerable()) action();
    }

    public void Stop() => actions.CompleteAdding();
    public void Dispose() => actions.Dispose();
}
