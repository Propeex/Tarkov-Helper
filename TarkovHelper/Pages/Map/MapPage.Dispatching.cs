using System.Windows.Threading;

namespace TarkovHelper.Pages.Map;

public partial class MapPage
{
    private void DispatchUi(Action action)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        if (Dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = Dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }

    private void DispatchUi(Func<Task> action)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        _ = Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await action();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _log.Error("Deferred map UI operation failed", ex);
            }
        }), DispatcherPriority.Background);
    }
}
