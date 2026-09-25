using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace EnviousWispr.App;

public static class Program
{
    private static App? _application;

    [STAThread]
    public static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(unused =>
        {
            _ = unused;
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _application = new App();
        });
    }
}
