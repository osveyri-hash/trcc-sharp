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
