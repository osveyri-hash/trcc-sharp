using System.Windows.Media;
using System.Windows.Media.Imaging;
using TrccSharp.Core.Imaging;

namespace TrccSharp.App.Media;

/// <summary>Кадр <see cref="Surface"/> как картинка WPF — для анимированного фона.</summary>
public static class SurfaceImage
{
    public static BitmapSource ToBitmap(Surface surface)
    {
        var bitmap = BitmapSource.Create(surface.Width, surface.Height, 96, 96,
            PixelFormats.Bgra32, null, surface.Bgra, surface.Width * 4);

        bitmap.Freeze();
        return bitmap;
    }
}
