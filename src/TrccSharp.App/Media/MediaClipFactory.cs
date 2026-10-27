using System.IO;

namespace TrccSharp.App.Media;

/// <summary>Выбор типа клипа по расширению файла.</summary>
public static class MediaClipFactory
{
    private static readonly string[] VideoExtensions =
        { ".mp4", ".m4v", ".wmv", ".avi", ".mov", ".mkv", ".webm", ".mpg", ".mpeg" };

    public const string FileFilter =
        "Медиа|*.gif;*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.mp4;*.m4v;*.wmv;*.avi;*.mov;*.mkv;*.webm" +
        "|Картинки и GIF|*.gif;*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff" +
        "|Видео|*.mp4;*.m4v;*.wmv;*.avi;*.mov;*.mkv;*.webm" +
        "|Все файлы|*.*";

    public static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".gif" or ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff"
               || VideoExtensions.Contains(ext);
    }

    public static MediaClip Open(string path, int width, int height)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();

        if (VideoExtensions.Contains(ext))
            return new VideoClip(path, width, height);

        return ext switch
        {
            ".gif" => new GifClip(path, width, height),
            _ => new StillClip(path, width, height)
        };
    }
}
