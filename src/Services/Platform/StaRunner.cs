namespace WriteFix.Services.Platform;

/// <summary>
/// Runs work on a dedicated STA thread. Capture and replacement need STA for the
/// clipboard, and must stay off the WPF UI thread because cross-process UI
/// Automation calls can block for a long time (ARCHITECTURE.md §5).
/// </summary>
public static class StaRunner
{
    public static Task<T> RunAsync<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "WriteFix.Sta",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return completion.Task;
    }

    public static Task RunAsync(Action work) => RunAsync<object?>(() =>
    {
        work();
        return null;
    });
}
