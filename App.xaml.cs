using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace MultiAI;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    public App()
    {
        DispatcherUnhandledException += (s, e) =>
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "multiai-log.txt"),
                    $"UNHANDLED: {e.Exception}\n");
            }
            catch { }
            e.Handled = true;
        };
    }

    internal static void Log(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "multiai-log.txt"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}\n");
        }
        catch { }
    }
}
