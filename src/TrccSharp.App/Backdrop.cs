using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TrccSharp.App;

/// <summary>
/// Фон окна: картинка assets/backdrop.jpg, переведённая в маску по яркости.
/// Маска белая с прозрачностью, поэтому цвет берётся из темы окна:
/// на белой теме рисунок чёрный, на чёрной — белоснежный.
/// </summary>
public static class Backdrop
{
    public static BitmapSource? Load()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("assets/backdrop.jpg", UriKind.Relative));
            if (resource is null) return null;

            using var stream = resource.Stream;
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var source = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);

            var width = source.PixelWidth;
            var height = source.PixelHeight;
            var pixels = new byte[width * height * 4];
            source.CopyPixels(pixels, width * 4, 0);

            // Белая бумага становится прозрачной, чёрные точки — плотной маской.
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var luminance = (byte)((pixels[i + 2] * 299 + pixels[i + 1] * 587 + pixels[i] * 114) / 1000);
                var alpha = (byte)(255 - luminance);

                // Pbgra32: цвет уже умножен на альфу, а маска белая.
                pixels[i] = alpha;
                pixels[i + 1] = alpha;
                pixels[i + 2] = alpha;
                pixels[i + 3] = alpha;
            }

            var mask = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
            mask.Freeze();
            return mask;
        }
        catch
        {
            // Нет картинки или битый файл — окно просто останется без фона.
            return null;
        }
    }
}
