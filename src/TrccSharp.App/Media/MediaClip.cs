using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TrccSharp.Core.Imaging;

namespace TrccSharp.App.Media;

/// <summary>
/// Источник кадров для темы «Медиа»: фото, анимированный GIF или видео.
/// Каждый вызов <see cref="Render"/> отдаёт готовый кадр 320×240 для панели.
/// Рисовать можно только в UI-потоке (внутри работает WPF).
/// </summary>
public abstract class MediaClip : IDisposable
{
    // Холст и буферы пикселей создаются один раз на клип: при 10 кадрах/с
    // RenderTargetBitmap на каждый кадр — это лишние 300 КБ и работа композитора.
    // Буферов два (пинг-понг), чтобы отправляемый кадр не переписывался следующим.
    private RenderTargetBitmap? _target;
    private readonly byte[][] _buffers;
    private int _bufferIndex;

    protected MediaClip(int width, int height)
    {
        Width = width;
        Height = height;
        _buffers = new[]
        {
            new byte[width * height * 4],
            new byte[width * height * 4]
        };
    }

    public int Width { get; }
    public int Height { get; }

    public abstract string Kind { get; }

    /// <summary>Кадр не меняется сам по себе — можно не перерисовывать каждый цикл.</summary>
    public virtual bool IsStatic => false;

    /// <summary>
    /// «Родная» частота файла, кадр/с. 0 — своей частоты нет, работает ползунок.
    /// По ней задаётся ритм темы «Медиа».
    /// </summary>
    public virtual double NativeFps => 0;

    public abstract Surface Render();

    public virtual void Dispose() { }

    /// <summary>Растягивает картинку на весь экран с обрезкой лишнего (без чёрных полей).</summary>
    protected Surface DrawCover(BitmapSource bitmap)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, Width, Height));

            var scale = Math.Max((double)Width / bitmap.PixelWidth, (double)Height / bitmap.PixelHeight);
            var w = bitmap.PixelWidth * scale;
            var h = bitmap.PixelHeight * scale;

            dc.DrawImage(bitmap, new Rect((Width - w) / 2, (Height - h) / 2, w, h));
        }

        return Compose(visual);
    }

    /// <summary>Переносит готовый DrawingVisual в BGRA-холст.</summary>
    protected Surface Compose(DrawingVisual visual)
    {
        _target ??= new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        _target.Render(visual);

        var pixels = _buffers[_bufferIndex];
        _bufferIndex ^= 1;

        _target.CopyPixels(pixels, Width * 4, 0);
        return Surface.FromBgra(Width, Height, pixels);
    }
}
