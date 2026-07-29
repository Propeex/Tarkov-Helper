using System.Windows.Threading;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Pages;

public partial class ItemsPage
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
                System.Diagnostics.Debug.WriteLine($"[ItemsPage] Deferred UI refresh failed: {ex}");
            }
        }), DispatcherPriority.Background);
    }
}
