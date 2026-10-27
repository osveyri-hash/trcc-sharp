using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TrccSharp.Core.Imaging;

namespace TrccSharp.App.Media;

/// <summary>
/// Неподвижный кадр. Файл читается один раз, поэтому 320×240 собирается
/// тоже один раз — дальше битмап просто отдаётся.
/// </summary>
public sealed class StillClip : MediaClip
{
    private readonly BitmapSource _bitmap;
    private Surface? _rendered;

    public StillClip(string path, int width, int height) : base(width, height)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(path);
        image.CacheOption = BitmapCacheOption.OnLoad;   // файл не держим открытым
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        image.EndInit();
        image.Freeze();

        _bitmap = image;
    }

    public override string Kind => "Фото";

    public override bool IsStatic => true;

    public override Surface Render() => _rendered ??= DrawCover(_bitmap);
}

/// <summary>
/// Анимированный GIF. Кадры не просто листаются: при загрузке они собираются
/// в один холст по правилам GIF (смещение кадра, прозрачность, способ очистки) —
/// иначе на части файлов остаются хвосты и артефакты от предыдущих кадров.
/// </summary>
public sealed class GifClip : MediaClip
{
    private readonly BitmapSource[] _frames;
    private readonly double[] _endsMs;
    private readonly double _totalMs;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public GifClip(string path, int width, int height) : base(width, height)
    {
        var decoder = new GifBitmapDecoder(new Uri(path),
            BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        var source = decoder.Frames;
        var count = source.Count;

        var canvasWidth = source.Max(frame => frame.PixelWidth);
        var canvasHeight = source.Max(frame => frame.PixelHeight);

        var pixels = new byte[canvasWidth * canvasHeight * 4];
        byte[]? saved = null;

        _frames = new BitmapSource[count];
        _endsMs = new double[count];

        double total = 0;

        byte previousDisposal = 0;
        var lastLeft = 0;
        var lastTop = 0;
        var lastWidth = 0;
        var lastHeight = 0;

        for (var i = 0; i < count; i++)
        {
            var frame = source[i];
            var meta = frame.Metadata as BitmapMetadata;

            var left = ReadInt(meta, "/imgdesc/Left");
            var top = ReadInt(meta, "/imgdesc/Top");
            var disposal = (byte)ReadInt(meta, "/grctlext/Disposal");

            // Сначала убираем след предыдущего кадра так, как велит его способ очистки.
            if (i > 0)
            {
                if (previousDisposal == 3 && saved is not null)
                    Array.Copy(saved, pixels, pixels.Length);
                else if (previousDisposal == 2)
                    ClearRect(pixels, canvasWidth, canvasHeight, lastLeft, lastTop, lastWidth, lastHeight);
            }

            if (disposal == 3)
                saved = (byte[])pixels.Clone();

            Blend(pixels, canvasWidth, canvasHeight, left, top, frame);

            lastLeft = left;
            lastTop = top;
            lastWidth = frame.PixelWidth;
            lastHeight = frame.PixelHeight;
            previousDisposal = disposal;

            var composed = BitmapSource.Create(canvasWidth, canvasHeight, 96, 96,
                PixelFormats.Bgra32, null, pixels, canvasWidth * 4);
            composed.Freeze();
            _frames[i] = composed;

            total += DelayMs(frame);
            _endsMs[i] = total;
        }

        _totalMs = total;
    }

    public override string Kind => _frames.Length > 1 ? "GIF" : "Фото";

    public override bool IsStatic => _frames.Length == 1;

    /// <summary>
    /// Средний темп анимации: сколько кадров GIF выдаёт за секунду.
    /// Ограничивается 100 мс и 10 мс на кадр на стороне вызывающего.
    /// </summary>
    public override double NativeFps =>
        _frames.Length > 1 && _totalMs > 0 ? _frames.Length * 1000.0 / _totalMs : 0;

    public override Surface Render()
    {
        if (_frames.Length == 1 || _totalMs <= 0)
            return DrawCover(_frames[0]);

        var position = _clock.Elapsed.TotalMilliseconds % _totalMs;

        var index = Array.FindIndex(_endsMs, end => position < end);
        if (index < 0) index = _frames.Length - 1;

        return DrawCover(_frames[index]);
    }

    /// <summary>Задержка GIF хранится в сотых долях секунды.</summary>
    private static double DelayMs(BitmapFrame frame)
    {
        const double fallback = 100;   // 0,1 с — типичное значение по умолчанию

        if (frame.Metadata is BitmapMetadata meta && meta.ContainsQuery("/grctlext/Delay")
            && meta.GetQuery("/grctlext/Delay") is ushort hundredths && hundredths > 0)
        {
            return hundredths * 10.0;
        }

        return fallback;
    }

    private static int ReadInt(BitmapMetadata? meta, string query)
    {
        try
        {
            if (meta is not null && meta.ContainsQuery(query)
                && meta.GetQuery(query) is { } value)
                return Convert.ToInt32(value);
        }
        catch
        {
            // Битая метка кадра — считаем нулём.
        }

        return 0;
    }

    private static void ClearRect(byte[] pixels, int width, int height,
        int left, int top, int rectWidth, int rectHeight)
    {
        for (var y = Math.Max(0, top); y < Math.Min(height, top + rectHeight); y++)
        {
            var offset = (y * width + Math.Max(0, left)) * 4;
            var end = offset + Math.Min(width - Math.Max(0, left), rectWidth) * 4;

            for (var i = offset; i < end; i++)
                pixels[i] = 0;
        }
    }

    /// <summary>Накладывает кадр на холст с учётом прозрачности.</summary>
    private static void Blend(byte[] canvas, int canvasWidth, int canvasHeight,
        int left, int top, BitmapSource frame)
    {
        BitmapSource source = frame.Format == PixelFormats.Bgra32
            ? frame
            : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);

        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var pixels = new byte[width * height * 4];

        source.CopyPixels(pixels, width * 4, 0);

        for (var y = 0; y < height; y++)
        {
            var targetY = top + y;
            if (targetY < 0 || targetY >= canvasHeight) continue;

            for (var x = 0; x < width; x++)
            {
                var targetX = left + x;
                if (targetX < 0 || targetX >= canvasWidth) continue;

                var from = (y * width + x) * 4;
                var to = (targetY * canvasWidth + targetX) * 4;

                var alpha = pixels[from + 3];
                if (alpha == 0) continue;

                if (alpha == 255)
                {
                    canvas[to] = pixels[from];
                    canvas[to + 1] = pixels[from + 1];
                    canvas[to + 2] = pixels[from + 2];
                    canvas[to + 3] = 255;
                    continue;
                }

                var sa = alpha / 255.0;
                var da = canvas[to + 3] / 255.0;
                var outA = sa + da * (1 - sa);

                canvas[to] = Mix(pixels[from], canvas[to], sa, da, outA);
                canvas[to + 1] = Mix(pixels[from + 1], canvas[to + 1], sa, da, outA);
                canvas[to + 2] = Mix(pixels[from + 2], canvas[to + 2], sa, da, outA);
                canvas[to + 3] = (byte)Math.Round(outA * 255);
            }
        }
    }

    private static byte Mix(byte source, byte target, double sourceAlpha, double targetAlpha, double outAlpha)
    {
        if (outAlpha <= 0) return 0;

        var value = (source * sourceAlpha + target * targetAlpha * (1 - sourceAlpha)) / outAlpha;
        return (byte)Math.Clamp(Math.Round(value), 0, 255);
    }
}
