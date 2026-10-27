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
    Minimal,
    History,
    Rings,
    Bars,
    Dial,
    Typographic,
    Cells,
    List,
    Wave,
    Split,
    Custom,
    Test
}

/// <summary>
/// Отрисовка кадра для панели средствами WPF: холст 320x240 BGRA,
/// который затем поворачивается и кодируется ядром.
///
/// Оформление — монохром в духе японской графики: бумага, тушь,
/// тонкие линии, полутоновые точки-скринтон и вертикальные иероглифы.
/// Цвет акцента и подпись приходят снаружи (сохранённая тема).
/// </summary>
public static class ThemeRenderer
{
    public const int Width = 320;
    public const int Height = 240;

    /// <summary>Акцент по умолчанию — тушь, без цвета.</summary>
    public static readonly Color DefaultAccent = Color.FromRgb(0x10, 0x10, 0x12);

    /// <summary>
    /// Раскладка своего кадра. Список заменяется целиком при каждом изменении:
    /// поток отрисовки видит готовый снимок, а не половину перетаскивания.
    /// </summary>
    public static IReadOnlyList<PanelWidget> Widgets { get; set; } = Array.Empty<PanelWidget>();

    /// <summary>Габариты одного объекта раскладки на кадре.</summary>
    public const double WidgetWidth = 92;
    public const double WidgetHeight = 38;

    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("ru-RU");

    private static readonly Typeface Thin =
        new(new FontFamily("Segoe UI Light"), FontStyles.Normal, FontWeights.Light, FontStretches.Normal);
    private static readonly Typeface Regular = new("Segoe UI");
    private static readonly Typeface Jp =
        new(new FontFamily("Yu Gothic UI"), FontStyles.Normal, FontWeights.Light, FontStretches.Normal);

    // Две палитры: бумага с тушью и чёрная доска с белой тушью.
    private static readonly Brush PaperLight = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0xF3, 0xEE)));
    private static readonly Brush InkLight = Freeze(new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x12)));
    private static readonly Brush GreyLight = Freeze(new SolidColorBrush(Color.FromRgb(0x6E, 0x6B, 0x66)));
    private static readonly Brush FaintLight = Freeze(new SolidColorBrush(Color.FromRgb(0xB9, 0xB5, 0xAD)));
    private static readonly Brush RuleLight = Freeze(new SolidColorBrush(Color.FromRgb(0xD5, 0xD1, 0xC8)));
    private static readonly Brush WatermarkLight = Freeze(new SolidColorBrush(Color.FromArgb(0x1F, 0x10, 0x10, 0x12)));

    private static readonly Brush PaperDark = Freeze(new SolidColorBrush(Color.FromRgb(0x0E, 0x0E, 0x10)));
    private static readonly Brush InkDark = Freeze(new SolidColorBrush(Color.FromRgb(0xED, 0xEA, 0xE3)));
    private static readonly Brush GreyDark = Freeze(new SolidColorBrush(Color.FromRgb(0x8E, 0x8A, 0x82)));
    private static readonly Brush FaintDark = Freeze(new SolidColorBrush(Color.FromRgb(0x5E, 0x5B, 0x55)));
    private static readonly Brush RuleDark = Freeze(new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E)));
    private static readonly Brush WatermarkDark = Freeze(new SolidColorBrush(Color.FromArgb(0x26, 0xED, 0xEA, 0xE3)));

    // Скринтон: точка в тайле 5×5 — как растр в манге.
    private static readonly Brush HalftoneLight = CreateHalftone(0x10, 0x10, 0x12);
    private static readonly Brush HalftoneDark = CreateHalftone(0xED, 0xEA, 0xE3);

    private static readonly Pen RulePenLight = FreezePen(new Pen(RuleLight, 1));
    private static readonly Pen RulePenDark = FreezePen(new Pen(RuleDark, 1));

    // Вуаль поверх фотографии: бумага (или доска) с прозрачностью — картинка видна,
    // но тушь и тонкие линии остаются читаемыми.
    private static readonly Brush VeilLight = Freeze(new SolidColorBrush(Color.FromArgb(0xB3, 0xF5, 0xF3, 0xEE)));
    private static readonly Brush VeilDark = Freeze(new SolidColorBrush(Color.FromArgb(0xC2, 0x0E, 0x0E, 0x10)));

    // Текущая палитра кадра — переключается в начале Render.
    private static Brush Paper = PaperLight;
    private static Brush Ink = InkLight;
    private static Brush Grey = GreyLight;
    private static Brush Faint = FaintLight;
    private static Brush Rule = RuleLight;
    private static Brush Watermark = WatermarkLight;
    private static Brush Halftone = HalftoneLight;
    private static Brush Veil = VeilLight;
    private static Pen RulePen = RulePenLight;
    private static bool _dark;

    // Акцент темы: кисть пересобирается только при смене цвета.
    private static Color _accentColor = DefaultAccent;
    private static Brush _accentBrush = Ink;

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

    // История для темы «График»: кольцевой буфер из 96 отсчётов.
    private const int HistorySamples = 96;
    private static readonly double[] CpuHistory = new double[HistorySamples];
    private static readonly double[] RamHistory = new double[HistorySamples];
    private static int _historyHead;
    private static int _historyCount;

    // Кадр «Медиа» с подписью: свой буфер, чтобы не портить кэш клипа.
    private static Surface? _mediaFrame;
    private static MediaClip? _mediaSource;
    private static string? _mediaCaption;

    // Подпись рисуется один раз в прозрачный слой и дальше накладывается пикселями.
    private static Overlay? _captionOverlay;
    private static string? _overlayCaption;
    private static Color _overlayAccent;

    public static Surface Render(ThemeKind theme, SensorSnapshot sensors, MediaClip? media = null,
        Color? accent = null, string? caption = null, bool dark = false, ImageSource? backdrop = null)
    {
        // Чёрная панель: та же раскладка, но тушь светлая, а бумага — доска.
        UsePalette(dark);
        EnsureAccent(ReadableAccent(accent ?? DefaultAccent));
        var text = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim();

        // Медиа рисует свой кадр целиком — подпись накладываем поверх.
        if (theme == ThemeKind.Media && media is not null)
            return ComposeMedia(media, text);

        // Тестовая картинка постоянна — собираем один раз.
        if (theme == ThemeKind.Test)
            return _testPattern ??= TestPattern.Build(Width, Height);

        _visual ??= new DrawingVisual();

        using (var dc = _visual.RenderOpen())
        {
            dc.DrawRectangle(Paper, null, new Rect(0, 0, Width, Height));

            // Свой фон темы: фото на весь экран, поверх — полупрозрачная бумага.
            if (backdrop is not null)
            {
                DrawBackdrop(dc, backdrop);
                dc.DrawRectangle(Veil, null, new Rect(0, 0, Width, Height));
            }

            dc.DrawRectangle(null, RulePen, new Rect(8.5, 8.5, Width - 17, Height - 17));

            switch (theme)
            {
                case ThemeKind.Monitor:
                    DrawMonitor(dc, sensors, text);
                    break;
                case ThemeKind.Clock:
                    DrawClock(dc, sensors, text);
                    break;
                case ThemeKind.Minimal:
                    DrawMinimal(dc, sensors, text);
                    break;
                case ThemeKind.History:
                    DrawHistory(dc, sensors, text);
                    break;
                case ThemeKind.Rings:
                    DrawRings(dc, sensors, text);
                    break;
                case ThemeKind.Bars:
                    DrawBars(dc, sensors, text);
                    break;
                case ThemeKind.Dial:
                    DrawDial(dc, sensors, text);
                    break;
                case ThemeKind.Typographic:
                    DrawTypographic(dc, sensors, text);
                    break;
                case ThemeKind.Cells:
                    DrawCells(dc, sensors, text);
                    break;
                case ThemeKind.List:
                    DrawList(dc, sensors, text);
                    break;
                case ThemeKind.Wave:
                    DrawWave(dc, sensors, text);
                    break;
                case ThemeKind.Split:
                    DrawSplit(dc, sensors, text);
                    break;
                case ThemeKind.Custom:
                    DrawCustom(dc, sensors, text);
                    break;
                case ThemeKind.Media:
                    DrawMediaHint(dc, text);
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

    /// <summary>Сбросить кэш подписи (например, после смены акцента).</summary>
    public static void InvalidateCaption()
    {
        _captionOverlay = null;
        _overlayCaption = null;
        _mediaCaption = null;
    }

    /// <summary>Фон темы: фото растягивается на весь кадр, лишнее обрезается по краям.</summary>
    private static void DrawBackdrop(DrawingContext dc, ImageSource image)
    {
        if (image.Width <= 0 || image.Height <= 0) return;

        var scale = Math.Max(Width / image.Width, Height / image.Height);
        var w = image.Width * scale;
        var h = image.Height * scale;

        dc.DrawImage(image, new Rect((Width - w) / 2, (Height - h) / 2, w, h));
    }

    private static void EnsureAccent(Color color)
    {
        if (color == _accentColor) return;

        _accentColor = color;
        _accentBrush = Freeze(new SolidColorBrush(color));
        InvalidateCaption();
    }

    /// <summary>Переключение палитры кадра: бумага с тушью или чёрная доска.</summary>
    private static void UsePalette(bool dark)
    {
        if (_dark == dark) return;
        _dark = dark;

        Paper = dark ? PaperDark : PaperLight;
        Ink = dark ? InkDark : InkLight;
        Grey = dark ? GreyDark : GreyLight;
        Faint = dark ? FaintDark : FaintLight;
        Rule = dark ? RuleDark : RuleLight;
        Watermark = dark ? WatermarkDark : WatermarkLight;
        Halftone = dark ? HalftoneDark : HalftoneLight;
        RulePen = dark ? RulePenDark : RulePenLight;
        Veil = dark ? VeilDark : VeilLight;

        // Подпись и медиа-кадр кэшируются вместе с цветами — собираем заново.
        InvalidateCaption();
        _mediaFrame = null;
    }

    /// <summary>На чёрной панели тёмный акцент не виден — берём светлую тушь.</summary>
    private static Color ReadableAccent(Color accent) =>
        _dark && 0.2126 * accent.R + 0.7152 * accent.G + 0.0722 * accent.B < 90
            ? Color.FromRgb(0xED, 0xEA, 0xE3)
            : accent;

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static Pen FreezePen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }

    private static Brush CreateHalftone(byte r, byte g, byte b)
    {
        var dotBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x3D, r, g, b)));
        var geometry = new GeometryDrawing(dotBrush, null, new EllipseGeometry(new Point(2.5, 2.5), 1.15, 1.15));
        geometry.Freeze();

        var brush = new DrawingBrush(geometry)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 5, 5),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None
        };

        brush.Freeze();
        return brush;
    }

    // ── Темы ────────────────────────────────────────────────────────────────

    private static void DrawMonitor(DrawingContext dc, SensorSnapshot sensors, string? caption)
    {
        DrawHeader(dc, "監視", "МОНИТОР");
        dc.DrawRectangle(Halftone, null, new Rect(266, 17, 30, 30));

        var load = Math.Clamp(sensors.CpuLoadPercent / 100, 0, 1);
        DrawRing(dc, 86, 108, 46, 3, load);

        DrawCentered(dc, $"{sensors.CpuLoadPercent:0}", 86, 108, 34, Ink, Thin);
        DrawTrackedCentered(dc, "ЗАГРУЗКА ЦП", 86, 166, 8.5, Grey, 1.6);

        const double x = 154;

        DrawTracked(dc, "ОЗУ", x, 40, 9, Grey, 1.6);
        DrawText(dc, $"{sensors.RamUsedGb:0.0} / {sensors.RamTotalGb:0.0} ГБ", x, 54, 17, Ink, Regular);
        DrawRule(dc, x, 84, 144);

        DrawTracked(dc, "ГП", x, 94, 9, Grey, 1.6);
        DrawText(dc, sensors.GpuTempC is { } gpu ? $"{gpu:0} °C" : "—", x, 108, 17, Ink, Regular);
        DrawRule(dc, x, 138, 144);

        DrawTracked(dc, "СИСТЕМА", x, 148, 9, Grey, 1.6);
        DrawText(dc, FormatUptime(sensors.Uptime), x, 162, 17, Ink, Regular);

        DrawCaption(dc, caption);
    }

    private static void DrawClock(DrawingContext dc, SensorSnapshot sensors, string? caption)
    {
        DrawHeader(dc, "時刻", "ЧАСЫ");

        // Крупный иероглиф-водяной знак, обрезанный рамкой — как кадр из манги.
        DrawText(dc, "時", 234, 36, 64, Watermark, Jp);

        DrawText(dc, sensors.Now.ToString("HH:mm"), 22, 50, 88, Ink, Thin);
        DrawRule(dc, 24, 152, 178);

        DrawText(dc, sensors.Now.ToString("dddd", Culture), 24, 160, 13, Ink, Regular);
        DrawText(dc, sensors.Now.ToString("d MMMM yyyy", Culture), 24, 180, 12, Grey, Regular);

        DrawCaption(dc, caption);
    }

    private static void DrawMinimal(DrawingContext dc, SensorSnapshot sensors, string? caption)
    {
        DrawHeader(dc, "静", "МИНИМАЛ");
        dc.DrawRectangle(Halftone, null, new Rect(244, 60, 54, 54));

        var number = Text($"{sensors.CpuLoadPercent:0}", 84, Ink, Thin);
        dc.DrawText(number, new Point(20, 42));
        DrawText(dc, "%", 24 + number.Width, 100, 20, Grey, Regular);

        DrawTracked(dc, "ЗАГРУЗКА ЦП", 24, 140, 8.5, Grey, 1.6);
        DrawRule(dc, 22, 172, 276);

        DrawText(dc, $"ОЗУ {sensors.RamUsedGb:0.0} ГБ", 22, 182, 12, Ink, Regular);
        DrawText(dc, sensors.GpuTempC is { } gpu ? $"ГП {gpu:0}°" : "ГП —", 150, 182, 12, Ink, Regular);
        DrawText(dc, FormatUptime(sensors.Uptime), 250, 182, 12, Grey, Regular);

        DrawCaption(dc, caption);
    }

    /// <summary>
    /// Тема «График»: крупная загрузка ЦП и две кривые за последние полсотни секунд.
    /// История копится только пока тема на экране — в остальных темах она не нужна.
    /// </summary>
    private static void DrawHistory(DrawingContext dc, SensorSnapshot sensors, string? caption)
    {
        DrawHeader(dc, "履歴", "ГРАФИК");

        PushHistory(sensors);

        var big = Text($"{sensors.CpuLoadPercent:0}", 44, Ink, Thin);
        dc.DrawText(big, new Point(20, 28));
        DrawText(dc, "%", 24 + big.Width, 60, 16, Grey, Regular);
        DrawTracked(dc, "ЗАГРУЗКА ЦП", 22, 92, 8.5, Grey, 1.6);

        DrawTracked(dc, "ОЗУ", 206, 34, 8.5, Grey, 1.6);
        DrawText(dc, $"{sensors.RamUsedGb:0.0} / {sensors.RamTotalGb:0.0} ГБ", 206, 46, 13, Ink, Regular);

        DrawTracked(dc, "ГП", 206, 72, 8.5, Grey, 1.6);
        DrawText(dc, sensors.GpuTempC is { } gpu ? $"{gpu:0} °C" : "—", 206, 84, 13, Ink, Regular);

        // Поле графика: сетка по четвертям и две кривые — ЦП акцентом, ОЗУ серым.
        const double left = 20, right = 300, top = 106, bottom = 172;

        for (var i = 0; i <= 3; i++)
        {
            var y = top + (bottom - top) * i / 3;
            DrawRule(dc, left, Math.Round(y), right - left);
        }

        var accentPen = new Pen(_accentBrush, 1.6)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        accentPen.Freeze();

        var greyPen = new Pen(Grey, 1)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        greyPen.Freeze();

        DrawSpark(dc, RamHistory, left, top, right - left, bottom - top, greyPen);
        DrawSpark(dc, CpuHistory, left, top, right - left, bottom - top, accentPen);

        // Легенда слева, время работы справа — одной тихой строкой.
        dc.DrawRectangle(_accentBrush, null, new Rect(22, 188, 9, 2));
        DrawTracked(dc, "ЦП", 35, 184, 8, Grey, 1.3);
        dc.DrawRectangle(Grey, null, new Rect(60, 188, 9, 2));
        DrawTracked(dc, "ОЗУ", 73, 184, 8, Grey, 1.3);

        var uptime = Text($"СИСТЕМА {FormatUptime(sensors.Uptime)}", 8, Faint, Regular);
        dc.DrawText(uptime, new Point(300 - uptime.Width, 184));

        DrawCaption(dc, caption);
    }

    /// <summary>
    /// Тема «Кольца»: три круговых индикатора — ЦП, ОЗУ и температура ГП.
    /// Температура переводится в долю по шкале 30…90 °C.
    /// </summary>
    private static void DrawRings(DrawingContext dc, SensorSnapshot sensors, string? caption)
    {
        DrawHeader(dc, "環", "КОЛЬЦА");

        var cpu = Math.Clamp(sensors.CpuLoadPercent / 100, 0, 1);
        var ram = Math.Clamp(sensors.RamLoadPercent / 100, 0, 1);
        var gpu = sensors.GpuTempC is { } temp ? Math.Clamp((temp - 30) / 60, 0, 1) : 0;

        Ring(dc, 62, cpu, $"{sensors.CpuLoadPercent:0}", "ЦП", $"{sensors.CpuLoadPercent:0} %");
        Ring(dc, 160, ram, $"{sensors.RamLoadPercent:0}", "ОЗУ", $"{sensors.RamUsedGb:0.0} ГБ");
        Ring(dc, 258, gpu, sensors.GpuTempC is { } t ? $"{t:0}" : "—", "ГП",
            sensors.GpuTempC is { } c ? $"{c:0} °C" : "нет данных");

        DrawRule(dc, 22, 186, 276);

        DrawText(dc, $"СИСТЕМА {FormatUptime(sensors.Uptime)}", 22, 194, 9, Faint, Regular);
        DrawText(dc, $"ОЗУ {sensors.RamTotalGb:0.0} ГБ", 226, 194, 9, Faint, Regular);

        DrawCaption(dc, caption);
    }

    /// <summary>Один индикатор темы «Кольца»: дуга, число в центре, подписи снизу.</summary>
    private static void Ring(DrawingContext dc, double cx, double fraction, string value, string title, string detail)
    {
        const double cy = 108, radius = 32, thickness = 3.5;

        DrawRing(dc, cx, cy, radius, thickness, fraction);
        DrawCentered(dc, value, cx, cy, 20, Ink, Thin);
        DrawTrackedCentered(dc, title, cx, 150, 8.5, Grey, 1.6);
        DrawCentered(dc, detail, cx, 172, 10, Grey, Regular);
    }

    /// <summary>Добавляет текущие показания в историю темы «График».</summary>
    private static void PushHistory(SensorSnapshot sensors)
    {
        var cpu = Math.Clamp(sensors.CpuLoadPercent, 0, 100);
        var ram = Math.Clamp(sensors.RamLoadPercent, 0, 100);

        // Первый кадр — ровная линия на всю ширину, чтобы поле не пустовало.
        if (_historyCount == 0)
        {
            Array.Fill(CpuHistory, cpu);
            Array.Fill(RamHistory, ram);
            _historyCount = HistorySamples;
            _historyHead = 0;
        }

        CpuHistory[_historyHead] = cpu;
        RamHistory[_historyHead] = ram;

        _historyHead = (_historyHead + 1) % HistorySamples;
        if (_historyCount < HistorySamples) _historyCount++;
    }

    /// <summary>Кривая истории: свежий отсчёт прижат к правому краю поля.</summary>
    private static void DrawSpark(DrawingContext dc, double[] values,
        double x, double y, double width, double height, Pen pen)
    {
        if (_historyCount < 2) return;

        var step = width / (HistorySamples - 1);
        var geometry = new StreamGeometry();

        using (var ctx = geometry.Open())
        {
            for (var i = 0; i < _historyCount; i++)
            {
                var index = (_historyHead - _historyCount + i + HistorySamples * 2) % HistorySamples;
                var value = Math.Clamp(values[index], 0, 100);
                var px = x + width - (_historyCount - 1 - i) * step;
                var py = y + height - height * value / 100;

                if (i == 0) ctx.BeginFigure(new Point(px, py), false, false);
                else ctx.LineTo(new Point(px, py), true, false);
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);

        // Свежий отсчёт — точкой на правом краю поля.
        var lastIndex = (_historyHead - 1 + HistorySamples) % HistorySamples;
        var lastValue = Math.Clamp(values[lastIndex], 0, 100);
        var lastPoint = new Point(x + width, y + height - height * lastValue / 100);
        dc.DrawEllipse(pen.Brush, null, lastPoint, 2.2, 2.2);
    }

    // ── Виды «Монитора» ─────────────────────────────────────────────────────

    /// <summary>Кисть акцента с заданной прозрачностью — для заливок и плашек.</summary>
    private static Brush AccentFill(double opacity)
    {
        var brush = new SolidColorBrush(_accentColor) { Opacity = Math.Clamp(opacity, 0, 1) };
        brush.Freeze();
        return brush;
    }

    /// <summary>Дуга окружности: старт в градусах, размах со знаком.</summary>
    private static void DrawArc(DrawingContext dc, double cx, double cy, double radius, Pen pen,
        double startDegrees, double sweepDegrees)
    {
        if (Math.Abs(sweepDegrees) < 0.1) return;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(PointOnCircle(cx, cy, radius, startDegrees), false, false);
            ctx.ArcTo(PointOnCircle(cx, cy, radius, startDegrees + sweepDegrees), new Size(radius, radius),
                0, Math.Abs(sweepDegrees) > 180,
                sweepDegrees > 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, false);
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    /// <summary>Текст, прижатый правым краем к заданной координате.</summary>
    private static void DrawRight(DrawingContext dc, string text, double right, double y, double size,
        Brush brush, Typeface typeface)
    {
        var formatted = Text(text, size, brush, typeface);
        dc.DrawText(formatted, new Point(right - formatted.Width, y));
    }

    /// <summary>Полосы: три горизонтальных шкалы с подписями и значениями.</summary>
    private static void DrawBars(DrawingContext dc, SensorSnapshot sensors, string? caption)
    {
        DrawHeader(dc, "棒", "ПОЛОСЫ");

        var gpu = sensors.GpuTempC is { } temp ? Math.Clamp((temp - 30) / 60, 0, 1) : 0;
        var rows = new (string Title, double Fraction, string Value, Brush Brush)[]
        {
            ("ЦП", Math.Clamp(sensors.CpuLoadPercent / 100, 0, 1), $"{sensors.CpuLoadPercent:0} %", _accentBrush),
            ("ОЗУ", Math.Clamp(sensors.RamLoadPercent / 100, 0, 1), $"{sensors.RamUsedGb:0.0} ГБ", Ink),
            ("ГП", gpu, sensors.GpuTempC is { } c ? $"{c:0} °C" : "—", Grey)
        };

        var y = 52.0;
        foreach (var row in rows)
        {
            DrawTracked(dc, row.Title, 22, y, 9, Grey, 1.6);
            DrawRight(dc, row.Value, 298, y - 6, 15, Ink, Regular);

            var top = y + 18;
            dc.DrawRectangle(Rule, null, new Rect(22, top, 276, 5));

            var width = 276 * Math.Clamp(row.Fraction, 0, 1);
            if (width > 0.5) dc.DrawRectangle(row.Brush, null, new Rect(22, top, width, 5));

            y += 46;
        }

        DrawCaption(dc, caption);
    }

    /// <summary>Шкала: большая дуга-спидометр на 240° и две метрики справа.</summary>
    private static void DrawDial(DrawingContext dc, SensorSnapshot sensors, string? caption)
    {
        DrawHeader(dc, "計", "ШКАЛА");

        const double cx = 104, cy = 128, radius = 74;
        var fraction = Math.Clamp(sensors.CpuLoadPercent / 100, 0, 1);

        var track = new Pen(Rule, 6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        track.Freeze();
        DrawArc(dc, cx, cy, radius, track, 150, 240);

        var pen = new Pen(_accentBrush, 6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        DrawArc(dc, cx, cy, radius, pen, 150, 240 * fraction);

        // Деления по 10 %.
        for (var i = 0; i <= 10; i++)
        {
            var angle = 150 + 24.0 * i;
            var inner = PointOnCircle(cx, cy, radius - (i % 5 == 0 ? 19 : 12), angle);
            var outer = PointOnCircle(cx, cy, radius - 7, angle);
            dc.DrawLine(RulePen, inner, outer);
        }

        DrawCentered(dc, $"{sensors.CpuLoadPercent:0}", cx, cy - 20, 40, Ink, Thin);
        DrawTrackedCentered(dc, "ЦП", cx, cy + 12, 8.5, Grey, 1.6);

        DrawTracked(dc, "ОЗУ", 214, 88, 9, Grey, 1.6);
        DrawText(dc, $"{sensors.RamUsedGb:0.0} ГБ", 214, 102, 16, Ink, Regular);
        DrawRule(dc, 214, 134, 84);

        DrawTracked(dc, "ГП", 214, 144, 9, Grey, 1.6);
        DrawText(dc, sensors.GpuTempC is { } temp ? $"{temp:0} °C" : "—", 214, 158, 16, Ink, Regular);
        DrawRule(dc, 214, 190, 84);

        DrawCaption(dc, caption);
    }

    /// <summary>Типограф: гигантская цифра и тихая строка метрик под линейкой.</summary>
    private static void DrawTypographic(DrawingContext dc, SensorSnapshot sensors, string? caption)
    {
        DrawHeader(dc, "字", "ТИПОГРАФ");

        var big = Text($"{sensors.CpuLoadPercent:0}", 84, Ink, Thin);
        dc.DrawText(big, new Point(16, 40));
        DrawText(dc, "%", 24 + big.Width, 88, 22, Grey, Regular);
        DrawTracked(dc, "ЗАГРУЗКА ЦП", 22, 146, 9, Grey, 2.0);

        DrawRule(dc, 22, 176, 276);

        DrawText(dc, $"ОЗУ {sensors.RamUsedGb:0.0} / {sensors.RamTotalGb:0.0} ГБ", 22, 184, 11, Ink, Regular);
        DrawText(dc, sensors.GpuTempC is { } temp ? $"ГП {temp:0} °C" : "ГП —", 176, 184, 11, Ink, Regular);
        DrawRight(dc, FormatUptime(sensors.Uptime), 298, 184, 11, Grey, Regular);

        DrawCaption(dc, caption);
    }

    /// <summary>Сетка: четыре ячейки, разделённые волосяными линиями.</summary>
    private static void DrawCells(DrawingContext dc, SensorSnapshot sensors, string? caption)
    {
        DrawHeader(dc, "格", "СЕТКА");

        dc.DrawRectangle(Rule, null, new Rect(20, 116, 280, 1));
        dc.DrawRectangle(Rule, null, new Rect(160, 36, 1, 162));

        var cells = new (double X, double Y, string Title, string Value)[]
        {
            (22, 46, "ЦП", $"{sensors.CpuLoadPercent:0} %"),
            (172, 46, "ОЗУ", $"{sensors.RamLoadPercent:0} %"),
            (22, 130, "ГП", sensors.GpuTempC is { } temp ? $"{temp:0} °C" : "—"),
            (172, 130, "СИСТЕМА", FormatUptime(sensors.Uptime))
        };

        foreach (var cell in cells)
        {
            DrawTracked(dc, cell.Title, cell.X, cell.Y, 8.5, Grey, 1.8);
            DrawText(dc, cell.Value, cell.X, cell.Y + 20, 24, Ink, Regular);
        }

        DrawCaption(dc, caption);
    }

    /// <summary>Список: подпись слева, значение справа, волосяные линии между строками.</summary>
    private static void DrawList(DrawingContext dc, SensorSnapshot sensors, string? caption)
    {
        DrawHeader(dc, "表", "СПИСОК");

        var rows = new (string Title, string Value)[]
        {
            ("ПРОЦЕССОР", $"{sensors.CpuLoadPercent:0} %"),
            ("ПАМЯТЬ", $"{sensors.RamUsedGb:0.0} / {sensors.RamTotalGb:0.0} ГБ"),
            ("ГРАФИКА", sensors.GpuTempC is { } temp ? $"{temp:0} °C" : "—"),
            ("СИСТЕМА", FormatUptime(sensors.Uptime))
        };

        var y = 48.0;
        foreach (var row in rows)
        {
            DrawTracked(dc, row.Title, 22, y + 5, 9, Grey, 1.6);
            DrawRight(dc, row.Value, 298, y, 17, Ink, Regular);
            DrawRule(dc, 22, y + 30, 276);
            y += 38;
        }

        DrawCaption(dc, caption);
    }

    /// <summary>Волна: одна кривая загрузки ЦП с заливкой под ней.</summary>
    private static void DrawWave(DrawingContext dc, SensorSnapshot sensors, string? caption)
    {
        DrawHeader(dc, "波", "ВОЛНА");

        PushHistory(sensors);

        var big = Text($"{sensors.CpuLoadPercent:0}", 46, Ink, Thin);
        dc.DrawText(big, new Point(20, 28));
        DrawText(dc, "%", 24 + big.Width, 58, 18, Grey, Regular);

        DrawTracked(dc, "ОЗУ", 206, 36, 8.5, Grey, 1.6);
        DrawText(dc, $"{sensors.RamUsedGb:0.0} ГБ", 206, 48, 14, Ink, Regular);

        DrawTracked(dc, "ГП", 206, 74, 8.5, Grey, 1.6);
        DrawText(dc, sensors.GpuTempC is { } temp ? $"{temp:0} °C" : "—", 206, 86, 14, Ink, Regular);

        const double left = 20, right = 300, top = 108, bottom = 190;

        DrawSparkArea(dc, CpuHistory, left, top, right - left, bottom - top, AccentFill(0.16));

        var pen = new Pen(_accentBrush, 2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();

        DrawSpark(dc, CpuHistory, left, top, right - left, bottom - top, pen);
        DrawRule(dc, left, bottom, right - left);

        DrawCaption(dc, caption);
    }

    /// <summary>Разрез: левая половина подкрашена акцентом, справа — две метрики.</summary>
    private static void DrawSplit(DrawingContext dc, SensorSnapshot sensors, string? caption)
    {
        DrawHeader(dc, "断", "РАЗРЕЗ");

        dc.DrawRectangle(AccentFill(0.1), null, new Rect(10, 10, 145, Height - 20));

        var big = Text($"{sensors.CpuLoadPercent:0}", 52, Ink, Thin);
        dc.DrawText(big, new Point(24, 56));
        DrawText(dc, "%", 28 + big.Width, 96, 18, Grey, Regular);
        DrawTracked(dc, "ЗАГРУЗКА ЦП", 26, 138, 8.5, Grey, 1.6);

        DrawTracked(dc, "ОЗУ", 176, 58, 9, Grey, 1.6);
        DrawText(dc, $"{sensors.RamUsedGb:0.0}", 176, 72, 26, Ink, Regular);
        DrawText(dc, $"из {sensors.RamTotalGb:0.0} ГБ", 176, 106, 11, Grey, Regular);

        DrawRule(dc, 176, 136, 108);

        DrawTracked(dc, "ГП", 176, 148, 9, Grey, 1.6);
        DrawText(dc, sensors.GpuTempC is { } temp ? $"{temp:0} °C" : "—", 176, 162, 26, Ink, Regular);

        DrawCaption(dc, caption);
    }

    /// <summary>
    /// Свой кадр: объекты раскладки — подпись и значение. Пусто — подсказка,
    /// что объекты добавляются в разделе «СВОЁ».
    /// </summary>
    private static void DrawCustom(DrawingContext dc, SensorSnapshot sensors, string? caption)
    {
        var widgets = Widgets;

        if (widgets.Count == 0)
        {
            DrawHeader(dc, "自", "СВОЙ");
            DrawText(dc, "Пусто", 22, 92, 26, Ink, Thin);
            DrawText(dc, "Добавь объекты в разделе «СВОЁ»", 22, 132, 11, Grey, Regular);
            DrawCaption(dc, caption);
            return;
        }

        foreach (var widget in widgets)
            DrawWidget(dc, sensors, widget);

        DrawCaption(dc, caption);
    }

    /// <summary>Один объект раскладки: тихая подпись сверху, значение под ней.</summary>
    private static void DrawWidget(DrawingContext dc, SensorSnapshot sensors, PanelWidget widget)
    {
        var (label, value) = WidgetText(sensors, widget.Type);

        DrawTracked(dc, label, widget.X, widget.Y, 8.5, Grey, 1.6);
        DrawText(dc, value, widget.X, widget.Y + 12, 21, Ink, Thin);
    }

    /// <summary>Что писать в объекте выбранного типа.</summary>
    private static (string Label, string Value) WidgetText(SensorSnapshot sensors, string type) => type switch
    {
        "ram" => ("ОЗУ", $"{sensors.RamUsedGb:0.0} ГБ"),
        "gpu" => ("ГП", sensors.GpuTempC is { } temp ? $"{temp:0} °C" : "—"),
        "uptime" => ("СИСТЕМА", FormatUptime(sensors.Uptime)),
        "clock" => ("ВРЕМЯ", DateTime.Now.ToString("HH:mm", Culture)),
        _ => ("ЦП", $"{sensors.CpuLoadPercent:0} %")
    };

    /// <summary>Габариты объекта на кадре — по ним же идёт перетаскивание мышью.</summary>
    public static Rect WidgetBounds(PanelWidget widget) =>
        new(widget.X, widget.Y, WidgetWidth, WidgetHeight);

    /// <summary>Заливка под кривой истории — замкнутый контур до нижней границы.</summary>
    private static void DrawSparkArea(DrawingContext dc, double[] values,
        double x, double y, double width, double height, Brush brush)
    {
        if (_historyCount < 2) return;

        var step = width / (HistorySamples - 1);
        var firstX = x + width - (_historyCount - 1) * step;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(firstX, y + height), true, true);

            for (var i = 0; i < _historyCount; i++)
            {
                var index = (_historyHead - _historyCount + i + HistorySamples * 2) % HistorySamples;
                var value = Math.Clamp(values[index], 0, 100);
                var px = firstX + i * step;
                var py = y + height - height * value / 100;
                ctx.LineTo(new Point(px, py), true, false);
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(brush, null, geometry);
    }

    /// <summary>Заглушка темы «Медиа», пока файл не выбран.</summary>
    private static void DrawMediaHint(DrawingContext dc, string? caption)
    {
        DrawHeader(dc, "画像", "МЕДИА");

        DrawText(dc, "Файл не выбран", 22, 78, 30, Ink, Thin);
        DrawText(dc, "Перетащи файл в окно или нажми «Выбрать»", 22, 124, 12, Grey, Regular);
        DrawTracked(dc, "GIF · PNG · JPG · BMP · MP4 · WMV · AVI", 22, 148, 8.5, Faint, 1.4);

        DrawCaption(dc, caption);
    }

    // ── Общие элементы ──────────────────────────────────────────────────────

    /// <summary>Шапка кадра: иероглиф темы, подпись и тонкая линия до края.</summary>
    private static void DrawHeader(DrawingContext dc, string kanji, string title)
    {
        var mark = Text(kanji, 15, Ink, Jp);
        dc.DrawText(mark, new Point(22, 14));

        var start = 22 + mark.Width + 10;
        var width = DrawTracked(dc, title, start, 20, 8.5, Grey, 1.6);
        var lineStart = start + width + 12;

        if (lineStart < 296)
            dc.DrawRectangle(Rule, null, new Rect(lineStart, 25, 296 - lineStart, 1));
    }

    /// <summary>Подпись темы: акцентная чёрточка и текст внизу кадра.</summary>
    private static void DrawCaption(DrawingContext dc, string? caption)
    {
        if (caption is null) return;

        dc.DrawRectangle(_accentBrush, null, new Rect(22, 213, 10, 2));
        DrawTracked(dc, caption.ToUpperInvariant(), 38, 207, 10, Grey, 1.4);
    }

    private static void DrawRule(DrawingContext dc, double x, double y, double width) =>
        dc.DrawRectangle(Rule, null, new Rect(x, y, width, 1));

    /// <summary>Кольцо-индикатор: серая окружность и дуга загрузки от 12 часов.</summary>
    private static void DrawRing(DrawingContext dc, double cx, double cy, double radius, double thickness, double fraction)
    {
        dc.DrawEllipse(null, RulePen, new Point(cx, cy), radius, radius);

        var ratio = Math.Clamp(fraction, 0, 1);
        if (ratio <= 0.002) return;

        var pen = new Pen(_accentBrush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        pen.Freeze();

        if (ratio >= 0.998)
        {
            dc.DrawEllipse(null, pen, new Point(cx, cy), radius, radius);
            return;
        }

        var sweep = 360 * ratio;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(PointOnCircle(cx, cy, radius, -90), false, false);
            ctx.ArcTo(PointOnCircle(cx, cy, radius, -90 + sweep), new Size(radius, radius),
                0, sweep > 180, SweepDirection.Clockwise, true, false);
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private static Point PointOnCircle(double cx, double cy, double radius, double degrees)
    {
        var angle = degrees * Math.PI / 180;
        return new Point(cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle));
    }

    private static FormattedText Text(string text, double size, Brush brush, Typeface typeface) =>
        new(text, Culture, FlowDirection.LeftToRight, typeface, size, brush, 1.0);

    private static void DrawText(DrawingContext dc, string text, double x, double y, double size,
        Brush brush, Typeface typeface) =>
        dc.DrawText(Text(text, size, brush, typeface), new Point(x, y));

    private static void DrawCentered(DrawingContext dc, string text, double cx, double cy, double size,
        Brush brush, Typeface typeface)
    {
        var formatted = Text(text, size, brush, typeface);
        dc.DrawText(formatted, new Point(cx - formatted.Width / 2, cy - formatted.Height / 2));
    }

    /// <summary>Текст с разрядкой — так подписи выглядят «типографски».</summary>
    private static double DrawTracked(DrawingContext dc, string text, double x, double y, double size,
        Brush brush, double tracking)
    {
        var cursor = x;

        foreach (var ch in text)
        {
            var formatted = Text(ch.ToString(), size, brush, Regular);
            dc.DrawText(formatted, new Point(cursor, y));
            cursor += formatted.WidthIncludingTrailingWhitespace + tracking;
        }

        return Math.Max(0, cursor - x - tracking);
    }

    private static void DrawTrackedCentered(DrawingContext dc, string text, double cx, double y, double size,
        Brush brush, double tracking)
    {
        var width = MeasureTracked(text, size, tracking);
        DrawTracked(dc, text, cx - width / 2, y, size, brush, tracking);
    }

    private static double MeasureTracked(string text, double size, double tracking)
    {
        double width = 0;
        foreach (var ch in text)
            width += Text(ch.ToString(), size, Ink, Regular).WidthIncludingTrailingWhitespace + tracking;

        return Math.Max(0, width - tracking);
    }

    // ── Подпись поверх медиа ────────────────────────────────────────────────

    private sealed record Overlay(byte[] Pixels, int X, int Y, int Width, int Height);

    private static Surface ComposeMedia(MediaClip clip, string? caption)
    {
        var frame = clip.Render();
        if (caption is null) return frame;

        // Статичное фото с подписью собирается один раз.
        if (clip.IsStatic && ReferenceEquals(_mediaSource, clip) && _mediaCaption == caption && _mediaFrame is not null)
            return _mediaFrame;

        _mediaFrame ??= new Surface(Width, Height);
        Buffer.BlockCopy(frame.Bgra, 0, _mediaFrame.Bgra, 0, frame.Bgra.Length);

        var overlay = CaptionOverlay(caption);
        BlendOver(_mediaFrame, overlay);

        _mediaSource = clip;
        _mediaCaption = caption;
        return _mediaFrame;
    }

    /// <summary>Подпись на медиа: светлая плашка, акцентная чёрточка, текст.</summary>
    private static Overlay CaptionOverlay(string caption)
    {
        if (_captionOverlay is not null && _overlayCaption == caption && _overlayAccent == _accentColor)
            return _captionOverlay;

        var upper = caption.ToUpperInvariant();
        var textWidth = MeasureTracked(upper, 10, 1.4);

        const double left = 18;
        const double top = 196;
        var plateWidth = 16 + 10 + 8 + textWidth + 14;
        var width = (int)Math.Ceiling(Math.Min(plateWidth, Width - left - 10));
        var height = 28;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Freeze(new SolidColorBrush(Color.FromArgb(0xE6, 0xF5, 0xF3, 0xEE))), null,
                new Rect(0, 0, width, height));

            dc.DrawRectangle(_accentBrush, null, new Rect(16, 14, 10, 2));
            DrawTracked(dc, upper, 34, 8, 10, Ink, 1.4);
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);

        var pixels = new byte[width * height * 4];
        target.CopyPixels(pixels, width * 4, 0);

        _captionOverlay = new Overlay(pixels, (int)left, (int)top, width, height);
        _overlayCaption = caption;
        _overlayAccent = _accentColor;

        return _captionOverlay;
    }

    /// <summary>Наложение premultiplied-слоя на кадр: dst = src + dst × (1 − a).</summary>
    private static void BlendOver(Surface target, Overlay overlay)
    {
        for (var y = 0; y < overlay.Height; y++)
        {
            var ty = overlay.Y + y;
            if (ty < 0 || ty >= target.Height) continue;

            for (var x = 0; x < overlay.Width; x++)
            {
                var tx = overlay.X + x;
                if (tx < 0 || tx >= target.Width) continue;

                var si = (y * overlay.Width + x) * 4;
                var alpha = overlay.Pixels[si + 3];
                if (alpha == 0) continue;

                var di = (ty * target.Width + tx) * 4;
                var inverse = 255 - alpha;

                target.Bgra[di] = (byte)Math.Min(255, overlay.Pixels[si] + target.Bgra[di] * inverse / 255);
                target.Bgra[di + 1] = (byte)Math.Min(255, overlay.Pixels[si + 1] + target.Bgra[di + 1] * inverse / 255);
                target.Bgra[di + 2] = (byte)Math.Min(255, overlay.Pixels[si + 2] + target.Bgra[di + 2] * inverse / 255);
                target.Bgra[di + 3] = 255;
            }
        }
    }

    private static string FormatUptime(TimeSpan uptime) =>
        uptime.Days > 0
            ? $"{uptime.Days}д {uptime.Hours}ч"
            : $"{uptime.Hours}ч {uptime.Minutes:00}м";
}
