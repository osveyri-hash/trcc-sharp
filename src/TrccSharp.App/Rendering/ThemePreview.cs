using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TrccSharp.App.Media;
using TrccSharp.App.Sensors;
using TrccSharp.Core.Imaging;

namespace TrccSharp.App.Rendering;

/// <summary>
/// Миниатюра сохранённой темы: тот же рендерер, что и для панели, только
/// результат остаётся в памяти картинкой для списка тем.
/// </summary>
public static class ThemePreview
{
    /// <summary>
    /// Рисует кадр темы. null — если файл не открылся или тема битая;
    /// список тогда просто покажет пустую плашку, а не уронит окно.
    /// </summary>
    public static async Task<ImageSource?> CreateAsync(ThemePreset preset, SensorSnapshot sensors)
    {
        MediaClip? clip = null;

        try
        {
            var kind = preset.ToKind();

            if (kind == ThemeKind.Media
                && preset.MediaPath is { Length: > 0 } path
                && File.Exists(path))
            {
                clip = MediaClipFactory.Open(path, ThemeRenderer.Width, ThemeRenderer.Height);

                // MediaPlayer открывает видео асинхронно: сразу после Open кадр чёрный.
                if (clip is VideoClip video)
                {
                    for (var i = 0; i < 20 && !video.IsReady; i++)
                        await Task.Delay(100);

                    if (!video.IsReady)
                    {
                        // Не дождались — покажем карточку темы, а не чёрный прямоугольник.
                        clip.Dispose();
                        clip = null;
                    }
                }
            }

            var surface = ThemeRenderer.Render(kind, sensors, clip,
                ThemePreset.ToColor(preset.Accent), null, preset.Dark);

            return ToBitmap(surface);
        }
        catch
        {
            return null;
        }
        finally
        {
            clip?.Dispose();
        }
    }

    private static BitmapSource ToBitmap(Surface surface)
    {
        var bitmap = BitmapSource.Create(
            surface.Width, surface.Height, 96, 96,
            PixelFormats.Bgra32, null,
            surface.Bgra, surface.Width * 4);

        bitmap.Freeze();
        return bitmap;
    }
}
