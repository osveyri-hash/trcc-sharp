using System.Diagnostics;
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
        // --set-autostart on|off: установщик включает/выключает автозапуск
        // до первого окна. Работает и когда приложение уже запущено.
        var autoIndex = Array.FindIndex(e.Args,
            a => a.Equals("--set-autostart", StringComparison.OrdinalIgnoreCase));

        if (autoIndex >= 0)
        {
            var on = autoIndex + 1 < e.Args.Length
                     && (e.Args[autoIndex + 1].Equals("on", StringComparison.OrdinalIgnoreCase)
                         || e.Args[autoIndex + 1].Equals("1", StringComparison.OrdinalIgnoreCase));

            var settings = Settings.Load();
            settings.AutoStart = on;
            settings.Save();

            if (on) TrccSharp.App.Startup.Enable();
            else TrccSharp.App.Startup.Disable();

            Shutdown();
            return;
        }

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
            // Автозапуск не должен показывать окно с ошибкой, если приложение
            // уже открыто: просто тихо выходим, второй экземпляр не нужен.
            if (!TrccSharp.App.Startup.IsAutoStart)
            {
                MessageBox.Show(
                    "TrccSharp уже запущен.\n\nДва окна одновременно шлют кадры в панель — на экране СЖО они накладываются друг на друга.",
                    "TrccSharp", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            Shutdown();
            return;
        }

        base.OnStartup(e);

        // Фоновая программа у панели: не соревнуемся с играми и рабочим столом за CPU.
        try
        {
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch
        {
            // Политики системы могут запрещать смену приоритета.
        }

        new MainWindow().Show();
    }

    private static void DumpThemes(string directory)
    {
        Directory.CreateDirectory(directory);

        var sensors = new SensorReader().Read();
        var themes = new[]
        {
            ThemeKind.Monitor, ThemeKind.Clock, ThemeKind.Media,
            ThemeKind.Minimal, ThemeKind.History, ThemeKind.Rings,
            ThemeKind.Bars, ThemeKind.Columns, ThemeKind.Dial, ThemeKind.Typographic,
            ThemeKind.Cells, ThemeKind.List, ThemeKind.Wave, ThemeKind.Split,
            ThemeKind.Ticks, ThemeKind.Radar, ThemeKind.Test
        };

        foreach (var theme in themes)
        {
            // Чёрная панель — не отдельная тема, а вариант той же раскладки.
            foreach (var dark in new[] { false, true })
            {
                var surface = ThemeRenderer.Render(theme, sensors, null, null, "подпись темы", dark);
                var bitmap = BitmapSource.Create(surface.Width, surface.Height, 96, 96,
                    PixelFormats.Bgra32, null, surface.Bgra, surface.Width * 4);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));

                var name = theme.ToString().ToLowerInvariant() + (dark ? "-dark" : "");
                var path = Path.Combine(directory, $"theme-{name}.png");
                using var stream = File.Create(path);
                encoder.Save(stream);
            }
        }
    }
}
