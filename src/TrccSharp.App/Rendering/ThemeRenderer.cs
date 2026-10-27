using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TrccSharp.App.Media;
using TrccSharp.App.Sensors;
using TrccSharp.Core.Imaging;

namespace TrccSharp.App.Rendering;

public enum ThemeKind
{
    Monitor,
    Clock,
    Media,
    Test
}

/// <summary>
/// Отрисовка кадра для панели средствами WPF: холст 320x240 BGRA,
/// который затем поворачивается и кодируется ядром.
/// </summary>
public static class ThemeRenderer
{
    public const int Width = 320;
    public const int Height = 240;

    private static readonly Typeface Regular = new("Segoe UI");
    private static readonly Typeface Semibold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static readonly Brush Accent = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x2B)));
    private static readonly Brush Text = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF8)));
    private static readonly Brush Muted = Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x93, 0xA6)));
    private static readonly Brush Track = Freeze(new SolidColorBrush(Color.FromRgb(0x23, 0x27, 0x31)));

    // Фон не меняется — держим кисть и тестовую картинку готовыми.
    private static readonly Brush Background = Freeze(new LinearGradientBrush(
        Color.FromRgb(0x14, 0x17, 0x1E),
        Color.FromRgb(0x0A, 0x0C, 0x10),
        new Point(0, 0), new Point(1, 1)));

    // Холст, буферы пикселей и визуал переиспользуются: за секунду их создавалось
    // до десяти штук по 300 КБ. Буферов два (пинг-понг), чтобы кадр, который уже
    // ушёл в отправку, не переписывался следующим.
    private static RenderTargetBitmap? _target;
    private static DrawingVisual? _visual;
    private static readonly byte[][] Buffers =
    {
        new byte[Width * Height * 4],
        new byte[Width * Height * 4]
    };
    private static int _bufferIndex;
    private static Surface? _testPattern;

    public static Surface Render(ThemeKind theme, SensorSnapshot sensors, MediaClip? media = null)
    {
        // Медиа рисует свой кадр целиком — без фона, текста и полей.
        if (theme == ThemeKind.Media && media is not null)
            return media.Render();

        // Тестовая картинка постоянна — собираем один раз.
        if (theme == ThemeKind.Test)
            return _testPattern ??= TestPattern.Build(Width, Height);

        _visual ??= new DrawingVisual();

        using (var dc = _visual.RenderOpen())
        {
            DrawBackground(dc);

            switch (theme)
            {
                case ThemeKind.Monitor:
                    DrawMonitor(dc, sensors);
                    break;
                case ThemeKind.Clock:
                    DrawClock(dc, sensors);
                    break;
                case ThemeKind.Media:
                    DrawMediaHint(dc);
                    break;
            }
        }

        // RenderTargetBitmap умеет только Pbgra32; фон непрозрачный,
        // поэтому premultiplied-пиксели совпадают с обычными BGRA.
        _target ??= new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        _target.Render(_visual);

        var pixels = Buffers[_bufferIndex];
        _bufferIndex ^= 1;

        _target.CopyPixels(pixels, Width * 4, 0);
        return Surface.FromBgra(Width, Height, pixels);
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static void DrawBackground(DrawingContext dc) =>
        dc.DrawRectangle(Background, null, new Rect(0, 0, Width, Height));

    private static void DrawMonitor(DrawingContext dc, SensorSnapshot sensors)
    {
        DrawText(dc, "CPU", 20, 24, 18, Muted, Semibold);
        DrawText(dc, $"{sensors.CpuLoadPercent:0}%", 20, 46, 54, Text, Semibold);
        DrawBar(dc, 20, 112, Width - 40, 10, sensors.CpuLoadPercent / 100, Accent);
        DrawText(dc, $"загрузка · uptime {FormatUptime(sensors.Uptime)}", 20, 130, 13, Muted, Regular);

        DrawText(dc, "RAM", 20, 158, 16, Muted, Semibold);
        DrawText(dc, $"{sensors.RamUsedGb:0.0} / {sensors.RamTotalGb:0.0} ГБ", 20, 176, 22, Text, Semibold);
        DrawBar(dc, 20, 208, Width - 40, 6, sensors.RamLoadPercent / 100, Muted);

        if (sensors.GpuTempC is { } gpu)
        {
            DrawText(dc, "GPU", Width - 96, 24, 16, Muted, Semibold);
            DrawText(dc, $"{gpu:0}°", Width - 96, 42, 40, Text, Semibold);
        }
        else
        {
            DrawText(dc, sensors.Now.ToString("HH:mm"), Width - 96, 24, 40, Text, Semibold);
        }
    }

    private static void DrawClock(DrawingContext dc, SensorSnapshot sensors)
    {
        var time = sensors.Now.ToString("HH:mm");
        DrawText(dc, time, 20, 46, 96, Text, Semibold);
        DrawText(dc, sensors.Now.ToString("dddd", CultureInfo.GetCultureInfo("ru-RU")), 22, 160, 22, Accent, Semibold);
        DrawText(dc, sensors.Now.ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("ru-RU")), 22, 190, 18, Muted, Regular);
    }

    /// <summary>Заглушка темы «Медиа», пока файл не выбран.</summary>
    private static void DrawMediaHint(DrawingContext dc)
    {
        DrawText(dc, "МЕДИА", 20, 88, 16, Accent, Semibold);
        DrawText(dc, "Файл не выбран", 20, 112, 30, Text, Semibold);
        DrawText(dc, "кнопка «Выбрать файл» или перетащи файл в окно", 20, 156, 14, Muted, Regular);
        DrawText(dc, "GIF · PNG · JPG · BMP · MP4 · WMV · AVI", 20, 180, 13, Muted, Regular);
    }

    private static void DrawBar(DrawingContext dc, double x, double y, double width, double height, double fill, Brush brush)
    {
        dc.DrawRoundedRectangle(Track, null, new Rect(x, y, width, height), height / 2, height / 2);

        var ratio = Math.Clamp(fill, 0, 1);
        if (ratio <= 0) return;

        dc.DrawRoundedRectangle(brush, null,
            new Rect(x, y, Math.Max(height, width * ratio), height), height / 2, height / 2);
    }

    private static void DrawText(DrawingContext dc, string text, double x, double y, double size, Brush brush, Typeface typeface)
    {
        var formatted = new FormattedText(text, CultureInfo.GetCultureInfo("ru-RU"),
            FlowDirection.LeftToRight, typeface, size, brush, 1.0);

        dc.DrawText(formatted, new Point(x, y));
    }

    private static string FormatUptime(TimeSpan uptime) =>
        uptime.Days > 0
            ? $"{uptime.Days}д {uptime.Hours}ч"
            : $"{uptime.Hours}ч {uptime.Minutes:00}м";
}
