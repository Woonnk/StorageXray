using System.IO;
using System.Windows;

namespace StorageXray.App;
public partial class App : Application
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StorageXray");
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            try { Directory.CreateDirectory(DataDirectory); File.AppendAllText(Path.Combine(DataDirectory, "errors.log"), DateTime.UtcNow + "\n" + args.Exception + "\n"); } catch { }
            MessageBox.Show("That action could not finish. Your files were not automatically removed.\n\n" + args.Exception.Message, "StorageXray", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}
