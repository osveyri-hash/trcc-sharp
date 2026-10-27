using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TrccSharp.App.Rendering;
using TrccSharp.App.Sensors;

namespace TrccSharp.App;

public partial class App : Application
{
    // Один экземпляр на всю систему: две копии одновременно шлют кадры в панель,
    // и на экране получается каша из двух картинок.
    private static Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // --dump <папка>: сохранить кадры всех тем в PNG и выйти.
        // Нужен для проверки отрисовки без панели и для превью в документации.
        var dumpIndex = Array.FindIndex(e.Args, a => a.Equals("--dump", StringComparison.OrdinalIgnoreCase));
        if (dumpIndex >= 0)
        {
            var dir = dumpIndex + 1 < e.Args.Length ? e.Args[dumpIndex + 1] : ".";
            DumpThemes(dir);
            Shutdown();
            return;
        }

        _singleInstance = new Mutex(initiallyOwned: true, @"Local\TrccSharp.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show(
                "TrccSharp уже запущен.\n\nДва окна одновременно шлют кадры в панель — на экране СЖО они накладываются друг на друга.",
                "TrccSharp", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        new MainWindow().Show();
    }

    private static void DumpThemes(string directory)
    {
        Directory.CreateDirectory(directory);

        var sensors = new SensorReader().Read();
        foreach (var theme in new[] { ThemeKind.Monitor, ThemeKind.Clock, ThemeKind.Test })
        {
            var surface = ThemeRenderer.Render(theme, sensors);
            var bitmap = BitmapSource.Create(surface.Width, surface.Height, 96, 96,
                PixelFormats.Bgra32, null, surface.Bgra, surface.Width * 4);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            var path = Path.Combine(directory, $"theme-{theme.ToString().ToLowerInvariant()}.png");
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
    }
}
