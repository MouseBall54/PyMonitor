using System.Threading;
using System.Windows.Threading;
using Xunit;

namespace PyRuntimeInspector.App.Tests;

public sealed class MainWindowSmokeTests
{
    [Fact]
    public async Task MainWindowCanRenderWithoutBindingExceptions()
    {
        var completed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var application = new App();
                application.InitializeComponent();
                var window = new MainWindow();
                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.Close();
                application.Shutdown();
                completed.TrySetResult(null);
            }
            catch (Exception exception)
            {
                completed.TrySetResult(exception);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var exception = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(exception);
        thread.Join(TimeSpan.FromSeconds(2));
    }
}
