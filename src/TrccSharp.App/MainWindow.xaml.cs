using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using TrccSharp.App.Media;
using TrccSharp.App.Rendering;
using TrccSharp.App.Sensors;
using TrccSharp.Core.Imaging;
using TrccSharp.Core.Protocol;

namespace TrccSharp.App;

public partial class MainWindow : Window
{
    private static readonly string[] ThemeNames = { "Монитор", "Часы", "Медиа", "Тест" };
    private const int MediaThemeIndex = 2;

    private readonly SensorReader _sensors = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Settings _settings = Settings.Load();

    private volatile PanelDevice? _device;
    private Surface? _frame;
    private SensorSnapshot _snapshot = SensorSnapshot.Empty;
    private bool _running;
    private int _sendErrors;

    // Тема «Медиа»: гифка, фото или видео на весь экран панели.
    private MediaClip? _clip;
    private string? _mediaPath;

    // Статичное фото не перерисовываем и не шлём 10 раз в секунду:
    // кадр уходит при изменении и редким «keep-alive», иначе панель вернётся к своей картинке.
    // 1000 мс оказалось много: панель успевала сбросить картинку и фото «мигало» раз в секунду.
    private const int KeepAliveMs = 300;
    private bool _frameStatic;
    private bool _frameDirty = true;
    private long _frameVersion;
    private long _sentVersion = -1;
    private DateTime _lastSendAt = DateTime.MinValue;

    // Частота: ползунок задаёт её для тем «Монитор», «Часы», «Тест».
    // В теме «Медиа» ритм берётся у самого файла, поэтому рабочая частота — отдельная.
    private const int MaxMediaFps = 30;
    private int _sliderFps = 2;
    private volatile int _effectiveFps = 2;
    private double _measuredFps;
    private string _infoText = "";

    // Яркость панели (программная, как в TRCC): 10–100 %.
    private int _brightness = 100;

    // Датчики меняются медленно — незачем читать ADL/GetSystemTimes на каждый кадр.
    private const int SensorRefreshMs = 500;
    private DateTime _sensorsAt = DateTime.MinValue;

    // Пока true, настройки только читаются: события контролов при инициализации
    // не должны перезаписывать файл значениями по умолчанию.
    private bool _loading = true;

    public MainWindow()
    {
        InitializeComponent();

        ThemeBox.ItemsSource = ThemeNames;
        ThemeBox.SelectedIndex = 0;

        var args = Environment.GetCommandLineArgs();

        // Восстанавливаем прошлый сеанс.
        if (_settings.Fps is >= 1 and <= 10)
            FpsSlider.Value = _settings.Fps;

        var savedTheme = Array.IndexOf(ThemeNames, _settings.Theme);
        if (savedTheme >= 0)
            ThemeBox.SelectedIndex = savedTheme;

        if (_settings.MediaPath is { Length: > 0 } savedMedia && File.Exists(savedMedia))
            ApplyMedia(savedMedia, switchTheme: false);

        // Командная строка главнее сохранённого: --fps N и --media <файл>.
        var fpsIndex = Array.FindIndex(args, a => a.Equals("--fps", StringComparison.OrdinalIgnoreCase));
        if (fpsIndex >= 0 && fpsIndex + 1 < args.Length && double.TryParse(args[fpsIndex + 1], out var fpsValue))
            FpsSlider.Value = Math.Clamp(fpsValue, FpsSlider.Minimum, FpsSlider.Maximum);

        var mediaIndex = Array.FindIndex(args, a => a.Equals("--media", StringComparison.OrdinalIgnoreCase));
        if (mediaIndex >= 0 && mediaIndex + 1 < args.Length)
            ApplyMedia(args[mediaIndex + 1], switchTheme: true);

        // Яркость: сначала из настроек, потом из командной строки (--brightness 60).
        if (_settings.Brightness is >= 10 and <= 100)
            BrightnessSlider.Value = _settings.Brightness;

        var brightnessIndex = Array.FindIndex(args, a => a.Equals("--brightness", StringComparison.OrdinalIgnoreCase));
        if (brightnessIndex >= 0 && brightnessIndex + 1 < args.Length
            && int.TryParse(args[brightnessIndex + 1], out var brightnessValue))
        {
            BrightnessSlider.Value = Math.Clamp(brightnessValue,
                BrightnessSlider.Minimum, BrightnessSlider.Maximum);
        }

        ApplyFrameRate();
        ApplyBrightness();
        RenderFrame();

        // Настройки восстановлены — только теперь можно их сохранять.
        _loading = false;

        // Одна петля на всё приложение: рисуем кадр → отправляем его на панель → пауза.
        _ = Task.Run(() => FrameLoop(_lifetime.Token));

        // --auto: сразу начинать вывод (для автозагрузки и проверки)
        if (args.Any(a => a.Equals("--auto", StringComparison.OrdinalIgnoreCase)))
            Loaded += async (_, _) => await StartSendingAsync();
    }

    /// <summary>Ползунок: частота для тем «Монитор», «Часы», «Тест».</summary>
    private void ApplyFrameRate()
    {
        // Событие значения может прийти до готовности элементов (Value задан в XAML).
        if (FpsSlider is null) return;

        _sliderFps = (int)Math.Max(1, Math.Round(FpsSlider.Value));

        if (FpsLabel is not null)
            FpsLabel.Text = $"{_sliderFps} кадр/с";

        RefreshEffectiveFps();
    }

    /// <summary>
    /// Для темы «Медиа» ритм задаёт сам файл: у GIF — задержки кадров, у видео — его частота.
    /// Ползунок в этом случае не участвует, иначе анимация шла бы рывками.
    /// </summary>
    private void RefreshEffectiveFps()
    {
        var isMedia = CurrentTheme == ThemeKind.Media && _clip is not null;
        var mediaFps = isMedia ? _clip!.NativeFps : 0;

        _effectiveFps = mediaFps > 0
            ? (int)Math.Clamp(Math.Round(mediaFps), 1, MaxMediaFps)
            : _sliderFps;

        if (FpsLabel is null) return;

        // В теме «Медиа» ползунок не при чём: у GIF и видео ритм свой,
        // а статичное фото вообще не перерисовывается.
        if (FpsSlider is not null)
            FpsSlider.IsEnabled = !isMedia;

        FpsLabel.Text = !isMedia
            ? $"{_sliderFps} кадр/с"
            : mediaFps > 0
                ? $"{_effectiveFps} кадр/с — по файлу (ползунок не участвует)"
                : "статичный кадр — перерисовки нет";
    }

    private void OnFpsChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ApplyFrameRate();
        SaveSettings();
    }

    /// <summary>
    /// Яркость 10–100 %. Как и в оригинальном TRCC, это программная подстройка:
    /// каналы умножаются перед кодированием в RGB565, команд в панель не уходит.
    /// Превью затемняется тем же коэффициентом, чтобы показывать реальный результат.
    /// </summary>
    private void ApplyBrightness()
    {
        if (BrightnessSlider is null) return;

        _brightness = (int)Math.Round(BrightnessSlider.Value);

        if (BrightnessLabel is not null)
            BrightnessLabel.Text = $"{_brightness} %";

        if (PreviewDim is not null)
            PreviewDim.Opacity = 1.0 - _brightness / 100.0;

        if (_device is { } device)
            device.Brightness = _brightness;
    }

    private void OnBrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ApplyBrightness();
        SaveSettings();
    }

    private ThemeKind CurrentTheme => ThemeBox.SelectedIndex switch
    {
        1 => ThemeKind.Clock,
        MediaThemeIndex => ThemeKind.Media,
        3 => ThemeKind.Test,
        _ => ThemeKind.Monitor
    };

    private void OnThemeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        SaveSettings();

        _frameDirty = true;
        RefreshEffectiveFps();

        if (IsLoaded) RenderFrame();
    }

    private void SaveSettings()
    {
        // Пока окно восстанавливает прошлый сеанс, сохранять нельзя:
        // иначе значения по умолчанию затирают файл раньше, чем мы его прочитали.
        if (_loading) return;
        if (ThemeBox is null || FpsSlider is null) return;

        _settings.Fps = (int)Math.Round(FpsSlider.Value);
        _settings.Brightness = BrightnessSlider is null ? _brightness : (int)Math.Round(BrightnessSlider.Value);
        _settings.Theme = ThemeBox.SelectedIndex is >= 0 and < 4 ? ThemeNames[ThemeBox.SelectedIndex] : null;
        _settings.Save();
    }

    /// <summary>
    /// Петля кадров. Порядок жёсткий: сначала кадр рисуется в окне, потом уходит на панель.
    /// Поэтому превью не может убежать вперёд панели — они всегда показывают один кадр,
    /// а частота ограничена тем, что реально успевает USB.
    /// </summary>
    private async Task FrameLoop(CancellationToken token)
    {
        var cycle = new Stopwatch();
        var window = Stopwatch.StartNew();
        var sent = 0;

        while (!token.IsCancellationRequested)
        {
            cycle.Restart();

            // 1. Отрисовка — только в UI-потоке (WPF RenderTargetBitmap).
            try
            {
                await Dispatcher.InvokeAsync(RenderFrame, DispatcherPriority.Render, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // 2. Отправка того же кадра на панель.
            var device = _device;
            if (device is not null)
            {
                var version = Volatile.Read(ref _frameVersion);
                var keepAliveDue = (DateTime.UtcNow - _lastSendAt).TotalMilliseconds >= KeepAliveMs;

                // Статичный кадр не гоняем повторно: панели достаточно изменений и keep-alive.
                if (version != _sentVersion || keepAliveDue)
                {
                    try
                    {
                        var frame = Volatile.Read(ref _frame);
                        if (frame is not null)
                        {
                            device.Send(frame);
                            sent++;

                            _sentVersion = version;
                            _lastSendAt = DateTime.UtcNow;
                        }

                        _sendErrors = 0;
                    }
                    catch (ObjectDisposedException)
                    {
                        // Вывод остановили между кадрами — это не ошибка.
                    }
                    catch (Exception ex)
                    {
                        var message = ex.Message;
                        _sendErrors++;

                        if (_sendErrors >= 3)
                            await Dispatcher.InvokeAsync(StopSending);
                        else
                            await Dispatcher.InvokeAsync(() => LogText.Text = $"ошибка отправки: {message}");
                    }
                }
            }

            // 3. Реальная частота за последние 2 секунды: по ней видно, если USB просел.
            if (window.Elapsed.TotalSeconds >= 2)
            {
                Volatile.Write(ref _measuredFps, sent / window.Elapsed.TotalSeconds);
                sent = 0;
                window.Restart();
            }

            // 4. Добираем остаток периода, чтобы не превысить выбранную частоту.
            // Для статичного кадра период задаёт keep-alive: панель не должна
            // успеть откатиться к своей картинке между отправками.
            var period = 1000.0 / _effectiveFps;
            if (_frameStatic) period = Math.Min(period, KeepAliveMs);

            var remaining = period - cycle.Elapsed.TotalMilliseconds;
            if (remaining > 1)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(remaining), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Отрисовка текущего кадра и превью. Вызывается только из UI-потока.
    /// Для статичного фото тяжёлая часть (рендер 320×240 + превью) выполняется
    /// только при изменении темы или файла — иначе каждый цикл рисовал бы одно и то же.
    /// </summary>
    private void RenderFrame()
    {
        var theme = CurrentTheme;

        // Датчики нужны только темам, которые их рисуют. В «Медиа» кадр — это файл,
        // поэтому CPU/RAM/GPU там не опрашиваются вовсе.
        // И читаем их не чаще двух раз в секунду: ADL на каждый кадр — лишняя нагрузка.
        if (theme != ThemeKind.Media
            && (DateTime.UtcNow - _sensorsAt).TotalMilliseconds >= SensorRefreshMs)
        {
            _snapshot = _sensors.Read();
            _sensorsAt = DateTime.UtcNow;
        }

        if (_frameDirty || !_frameStatic || _frame is null)
        {
            // Видео может не открыться (нет кодека) — сообщаем один раз, а не каждый кадр.
            if (_clip is VideoClip video && video.TakeError() is { } videoError)
                LogText.Text = $"видео: {videoError}";

            var frame = ThemeRenderer.Render(theme, _snapshot, _clip);
            Volatile.Write(ref _frame, frame);

            Preview.Source = ToBitmapSource(frame);

            _frameStatic = theme == ThemeKind.Media && _clip is { IsStatic: true };
            _frameDirty = false;
            Volatile.Write(ref _frameVersion, _frameVersion + 1);
        }

        UpdateInfoText(theme);
    }

    /// <summary>
    /// Текст под статусом. Переписывается только когда действительно изменился,
    /// чтобы статичное фото не трогало UI десять раз в секунду.
    /// </summary>
    private void UpdateInfoText(ThemeKind theme)
    {
        var rate = _running
            ? _frameStatic
                ? $"частота: {Volatile.Read(ref _measuredFps):0.0} кадр/с · кадр статичный"
                : $"частота: {Volatile.Read(ref _measuredFps):0.0} из {_effectiveFps} кадр/с"
            : "вывод остановлен";

        var text = theme is ThemeKind.Media
            ? rate
            : $"CPU {_snapshot.CpuLoadPercent:0}%   RAM {_snapshot.RamUsedGb:0.0}/{_snapshot.RamTotalGb:0.0} ГБ\n" +
              $"GPU {(_snapshot.GpuTempC is { } g ? $"{g:0}°C" : "нет данных")}\n" +
              rate;

        if (text != _infoText)
        {
            _infoText = text;
            InfoText.Text = text;
        }
    }

    private static BitmapSource ToBitmapSource(Surface surface)
    {
        var bitmap = BitmapSource.Create(
            surface.Width, surface.Height, 96, 96,
            PixelFormats.Bgra32, null,
            surface.Bgra, surface.Width * 4);

        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Загружает гифку, фото или видео и (по желанию) включает тему «Медиа».</summary>
    private void ApplyMedia(string path, bool switchTheme)
    {
        if (!File.Exists(path))
        {
            LogText.Text = $"файл не найден: {path}";
            return;
        }

        if (!MediaClipFactory.IsSupported(path))
        {
            LogText.Text = $"формат не поддерживается: {Path.GetExtension(path)}";
            return;
        }

        try
        {
            var clip = MediaClipFactory.Open(path, ThemeRenderer.Width, ThemeRenderer.Height);

            var previous = _clip;
            _clip = clip;
            previous?.Dispose();

            _mediaPath = path;
            MediaText.Text = $"{clip.Kind}: {Path.GetFileName(path)}";
            LogText.Text = "";

            if (switchTheme)
                ThemeBox.SelectedIndex = MediaThemeIndex;

            _settings.MediaPath = path;
            SaveSettings();

            // Новый файл — новый кадр: сбрасываем «статичность» и пересчитываем частоту по файлу.
            _frameDirty = true;
            RefreshEffectiveFps();

            RenderFrame();
        }
        catch (Exception ex)
        {
            LogText.Text = $"не удалось открыть «{Path.GetFileName(path)}»: {ex.Message}";
        }
    }

    private void OnPickMediaClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Гифка, фото или видео для экрана СЖО",
            Filter = MediaClipFactory.FileFilter,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
            ApplyMedia(dialog.FileName, switchTheme: true);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedFile(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDropFile(object sender, DragEventArgs e)
    {
        if (TryGetDroppedFile(e) is { } path)
        {
            ApplyMedia(path, switchTheme: true);
            e.Handled = true;
        }
    }

    private static string? TryGetDroppedFile(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return null;

        return MediaClipFactory.IsSupported(files[0]) ? files[0] : null;
    }

    private async void OnToggleClick(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            StopSending();
            return;
        }

        await StartSendingAsync();
    }

    private async Task StartSendingAsync()
    {
        if (_running) return;

        _running = true;
        ToggleButton.Content = "Остановить";
        ToggleButton.IsEnabled = false;
        StatusText.Text = "Подключаюсь к панели…";
        LogText.Text = "";

        Volatile.Write(ref _measuredFps, 0);
        _sendErrors = 0;

        try
        {
            var device = await Task.Run(() =>
            {
                var d = PanelDevice.ConnectDefault();
                d.Brightness = _brightness;
                d.Connect();
                return d;
            }, _lifetime.Token);

            _device = device;
            StatusText.Text = $"Подключено: {device.Profile.Width}×{device.Profile.Height}, " +
                              $"PM={device.Info.Pm} FBL={device.Info.Fbl}";
        }
        catch (Exception ex)
        {
            _running = false;
            ToggleButton.Content = "Запустить вывод";
            StatusText.Text = "Не удалось подключиться";
            LogText.Text = ex.Message;
        }
        finally
        {
            ToggleButton.IsEnabled = true;
        }
    }

    private void StopSending()
    {
        var device = _device;
        _device = null;
        device?.Dispose();

        _running = false;
        _sendErrors = 0;
        Volatile.Write(ref _measuredFps, 0);

        ToggleButton.Content = "Запустить вывод";
        StatusText.Text = "Остановлено";
    }

    protected override void OnClosed(EventArgs e)
    {
        _lifetime.Cancel();

        var device = _device;
        _device = null;
        device?.Dispose();

        _clip?.Dispose();
        _clip = null;

        base.OnClosed(e);
    }
}
