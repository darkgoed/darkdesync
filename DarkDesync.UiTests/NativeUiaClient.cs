using System.Collections.Concurrent;

// UI Automation client calls stay on one windowless MTA, never on the WPF UI
// thread or a rotating/starved ThreadPool COM apartment. One bounded request.
internal sealed class NativeUiaClient : IDisposable
{
    private readonly BlockingCollection<(Action Action, TaskCompletionSource Completion)> _requests = new(1);
    private readonly Thread _thread;
    public NativeUiaClient()
    {
        _thread = new Thread(() =>
        {
            foreach (var request in _requests.GetConsumingEnumerable())
            {
                try { request.Action(); request.Completion.SetResult(); }
                catch (Exception ex) { request.Completion.SetException(ex); }
            }
        }) { IsBackground = true, Name = "DarkDesync-UIA-client" };
        _thread.SetApartmentState(ApartmentState.MTA); _thread.Start();
    }
    public Task InvokeAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests.Add((action, completion)); return completion.Task;
    }
    public void Dispose()
    {
        _requests.CompleteAdding();
        if (_thread.Join(TimeSpan.FromSeconds(2))) _requests.Dispose();
    }
}
