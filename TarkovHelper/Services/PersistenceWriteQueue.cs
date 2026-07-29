namespace TarkovHelper.Services;

/// <summary>
/// Serializes fire-and-forget persistence writes and provides an explicit reset barrier.
/// Writes queued before a reset either finish before the barrier or are discarded.
/// Writes requested while the barrier is held are discarded so they cannot recreate
/// rows after the database has been cleared.
/// </summary>
public sealed class PersistenceWriteQueue
{
    private readonly object _sync = new();
    private Task _tail = Task.CompletedTask;
    private long _generation;
    private int _resetDepth;

    public Task Enqueue(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        lock (_sync)
        {
            if (_resetDepth > 0)
                return Task.CompletedTask;

            var generation = _generation;
            _tail = ExecuteWriteAsync(_tail, generation, operation);
            return _tail;
        }
    }

    public Task BeginResetAsync()
    {
        lock (_sync)
        {
            _resetDepth++;
            if (_resetDepth > 1)
                return _tail;

            _generation++;
            _tail = ObservePreviousAsync(_tail);
            return _tail;
        }
    }

    public void EndReset()
    {
        lock (_sync)
        {
            if (_resetDepth == 0)
                return;

            _resetDepth--;
        }
    }

    public async Task ResetAsync(Func<Task> clearOperation)
    {
        ArgumentNullException.ThrowIfNull(clearOperation);

        await BeginResetAsync().ConfigureAwait(false);
        try
        {
            await clearOperation().ConfigureAwait(false);
        }
        finally
        {
            EndReset();
        }
    }

    public Task FlushAsync()
    {
        lock (_sync)
            return _tail;
    }

    private async Task ExecuteWriteAsync(Task previous, long generation, Func<Task> operation)
    {
        await ObservePreviousAsync(previous).ConfigureAwait(false);

        lock (_sync)
        {
            if (_resetDepth > 0 || generation != _generation)
                return;
        }

        await operation().ConfigureAwait(false);
    }

    private static async Task ObservePreviousAsync(Task previous)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // A later persistence operation or reset must not be blocked by a
            // previous failed write. Individual services log their own failures.
        }
    }
}
