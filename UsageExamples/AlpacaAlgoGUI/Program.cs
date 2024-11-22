using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Terminal.Gui;

class Program
{
    // These MUST remain static because serviceProvider is static and is used to create these instances
    static IServiceProvider serviceProvider;
    static MeanReversionWithCrypto? meanReversionWithCrypto;
    static CancellationTokenSource? cancellationTokenSource;
    static bool algorithmRunning = false;

    static void Main(string[] args)
    {
        // Setup logging (using an in-memory logger for simplicity in this example)
        serviceProvider = new ServiceCollection()
            .AddLogging(builder => builder.AddSimpleConsole(options => options.TimestampFormat = "[HH:mm:ss] "))
            .AddSingleton<MeanReversionWithCrypto>()
            .BuildServiceProvider();
        var logger = serviceProvider.GetRequiredService<ILogger<MeanReversionWithCrypto>>();

        Application.Init();
        var top = Application.Top;

        // Create the main window
        var win = new Window("Alpaca Algorithm Manager") { Width = Dim.Fill(), Height = Dim.Fill() };
        top.Add(win);

        // Add a button to start/stop the algorithm
        var startButton = new Button("Start Algorithm") { X = 1, Y = 1 };
        startButton.Clicked += async () =>
        {
            if (!algorithmRunning)
            {
                meanReversionWithCrypto = serviceProvider.GetRequiredService<MeanReversionWithCrypto>();
                cancellationTokenSource = new CancellationTokenSource();

                try
                {
                    await Task.Run(async () => await meanReversionWithCrypto.run(cancellationTokenSource.Token));
                }
                catch (OperationCanceledException) { /* Expected when Stop is clicked */ }
                catch (Exception ex)
                {
                    logger.LogError(ex, "An error occurred in the algorithm.");
                    //Consider more sophisticated error handling here, maybe display error in GUI
                }
                finally
                {
                    cancellationTokenSource.Dispose();
                    algorithmRunning = false; // Ensure this is set to false even on exceptions
                    startButton.Text = "Start Algorithm"; // Update button text in finally block
                }
            }
            else
            {
                cancellationTokenSource?.Cancel();
                algorithmRunning = false;
                startButton.Text = "Start Algorithm";
            }
        };

        // Add a basic status display area
        var statusLabel = new Label("Algorithm Status: Stopped") { X = 1, Y = 3 };

        win.Add(startButton, statusLabel);
        Application.Run();
    }
}
