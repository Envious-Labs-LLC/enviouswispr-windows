using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace EnviousWispr.App;

public static class Program
{
    private static App? _application;

    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            .SetArgs(args)
            .SetAutoApplyOnStartup(false)
            .Run();

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
