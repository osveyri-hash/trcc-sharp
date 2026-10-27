using System.IO;
using System.Windows;
using System.Windows.Media;
using TrccSharp.Core.Imaging;

namespace TrccSharp.App.Media;

/// <summary>
/// Видео через MediaPlayer: проигрывается сам, кадр берётся текущий.
/// Зацикливание — по событию MediaEnded (позиция в начало, снова play).
/// </summary>
public sealed class VideoClip : MediaClip
{
    private readonly MediaPlayer _player = new();
    private readonly DrawingVisual _visual = new();
    private readonly string _name;

    public string? LastError { get; private set; }

    public VideoClip(string path, int width, int height) : base(width, height)
    {
        _name = Path.GetFileName(path);

        _player.Open(new Uri(path));
        _player.ScrubbingEnabled = true;
        _player.IsMuted = true;
        _player.Volume = 0;

        _player.MediaEnded += (_, _) =>
        {
            // Зацикливаем без переоткрытия файла.
            _player.Position = TimeSpan.Zero;
            _player.Play();
        };

        _player.MediaFailed += (_, e) => LastError = e.ErrorException?.Message ?? "видео не открылось";

        _player.Play();
    }

    public override string Kind => "видео";

    /// <summary>Плеер уже знает размер кадра — можно рисовать не чёрный прямоугольник.</summary>
    public bool IsReady => _player.NaturalVideoWidth > 0 && _player.NaturalVideoHeight > 0;

    /// <summary>
    /// MediaPlayer не отдаёт fps файла, поэтому берём типичные 30 кадр/с:
    /// видео всё равно проигрывается в реальном времени, а сколько кадров успеет
    /// уйти по USB — ограничит сама панель (это видно в строке «частота»).
    /// </summary>
    public override double NativeFps => 30;

    /// <summary>Отдаёт ошибку плеера один раз, чтобы не спамить в лог каждый кадр.</summary>
    public string? TakeError()
    {
        var error = LastError;
        LastError = null;
        return error;
    }

    public override Surface Render()
    {
        using (var dc = _visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, Width, Height));

            var videoWidth = _player.NaturalVideoWidth;
            var videoHeight = _player.NaturalVideoHeight;

            if (videoWidth > 0 && videoHeight > 0)
            {
                // Тот же «cover», что и у картинок: заполняем весь экран, лишнее обрезаем.
                var scale = Math.Max((double)Width / videoWidth, (double)Height / videoHeight);
                var w = videoWidth * scale;
                var h = videoHeight * scale;

                dc.DrawVideo(_player, new Rect((Width - w) / 2, (Height - h) / 2, w, h));
            }
        }

        return Compose(_visual);
    }

    public override void Dispose()
    {
        try
        {
            _player.Stop();
            _player.Close();
        }
        catch
        {
            // Плеер уже мог быть закрыт — не мешает.
        }
    }

    public override string ToString() => $"{Kind}: {_name}";
}
