namespace TarkovHelper.Services;

/// <summary>
/// Serializes fire-and-forget persistence writes and provides a reset barrier.
/// Writes queued before a reset either finish before the clear or are discarded.
/// Writes requested while a reset is active are discarded so they cannot recreate
/// rows after the reset has completed.
/// </summary>
public sealed class PersistenceWriteQueue
{
    private readonly object _sync = new();
    private Task _tail = Task.CompletedTask;
    private long _generation;
    private bool _resetInProgress;

    public Task Enqueue(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        lock (_sync)
        {
            if (_resetInProgress)
                return Task.CompletedTask;

            var generation = _generation;
            _tail = ExecuteWriteAsync(_tail, generation, operation);
            return _tail;
        }
    }

    public Task ResetAsync(Func<Task> clearOperation)
    {
        ArgumentNullException.ThrowIfNull(clearOperation);

        lock (_sync)
        {
            _generation++;
            _resetInProgress = true;
            var generation = _generation;
            _tail = ExecuteResetAsync(_tail, generation, clearOperation);
            return _tail;
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
            if (_resetInProgress || generation != _generation)
                return;
        }

        await operation().ConfigureAwait(false);
    }

    private async Task ExecuteResetAsync(Task previous, long generation, Func<Task> clearOperation)
    {
        try
        {
            await ObservePreviousAsync(previous).ConfigureAwait(false);
            await clearOperation().ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                if (generation == _generation)
                    _resetInProgress = false;
            }
        }
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
