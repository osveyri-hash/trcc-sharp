using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using TrccSharp.Core.Imaging;

namespace TrccSharp.App.Media;

/// <summary>
/// Обычное фото. Кадр всегда один и тот же, поэтому 320×240 собирается
/// один раз при открытии файла и дальше просто отдаётся.
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

    public override string Kind => "фото";

    public override bool IsStatic => true;

    public override Surface Render() => _rendered ??= DrawCover(_bitmap);
}

/// <summary>
/// Анимированный GIF. Кадр выбирается по времени, а не по счётчику кадров:
/// поэтому при 2 кадрах/с анимация всё равно идёт с нормальной скоростью (пропуская кадры).
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
            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

        _frames = decoder.Frames.Select(frame => (BitmapSource)frame).ToArray();

        _endsMs = new double[_frames.Length];
        double total = 0;

        for (var i = 0; i < _frames.Length; i++)
        {
            total += DelayMs(decoder.Frames[i]);
            _endsMs[i] = total;
            _frames[i].Freeze();
        }

        _totalMs = total;
    }

    public override string Kind => _frames.Length > 1 ? "GIF" : "фото";

    public override bool IsStatic => _frames.Length == 1;

    /// <summary>
    /// Средняя частота анимации: сколько кадров GIF показывает за секунду.
    /// При задержках 100 мс и 10 кадрах это 10 кадр/с.
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

    /// <summary>Задержка кадра GIF хранится в сотых долях секунды.</summary>
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
}
