using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using TrccSharp.App.Animations;
using TrccSharp.App.Media;
using TrccSharp.App.Rendering;
using TrccSharp.App.Sensors;
using TrccSharp.Core.Imaging;
using TrccSharp.Core.Protocol;

namespace TrccSharp.App;

public partial class MainWindow : Window
{
    private readonly SensorReader _sensors = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Settings _settings = Settings.Load();
    private readonly ThemeStore _themes = ThemeStore.Load();

    private readonly (ToggleButton Tile, ThemeKind Kind)[] _tiles;

    private volatile PanelDevice? _device;
    private Surface? _frame;
    private SensorSnapshot _snapshot = SensorSnapshot.Empty;
    private bool _running;
    private int _sendErrors;

    // Тема «Медиа»: гифка, фото или видео на весь экран панели.
    private MediaClip? _clip;
    private string? _mediaPath;

    // Свой фон тем «Монитор» и «Часы» — картинка под содержимым кадра.
    private ImageSource? _backdrop;
    private string? _backdropPath;

    // Оформление темы: акцент внизу кадра (выбирается только пресетом).
    private ThemeKind _theme = ThemeKind.Monitor;
    private Color _accent = ThemeRenderer.DefaultAccent;

    // Тема самого окна: светлая или тёмная.
    private string _skin = Skins.Light;

    // Чёрный вариант панели — тот же рисунок, но тушь светлая.
    private bool _panelDark;

    // Вид темы «Монитор»: обычный или минимал (раньше был отдельной плиткой).
    private bool _minimal;

    // Левая панель свёрнута до одних контролов (кнопка «три полоски»).
    private bool _compact;

    // Стеклянная заливка под плитками тем уже стоит на месте.
    private bool _glassPlaced;
    private bool _glassTracking;
    private DateTime _glassTrackUntil;

    // Трей: закрытие окна не выключает экран СЖО.
    private TrayIcon? _tray;
    private bool _exiting;
    private bool _trayHintShown;
    private bool _loaded;

    // Недавние файлы: плитки и стеклянная рамка активного.
    private readonly List<(string Path, Border Tile)> _recentTiles = new();
    private bool _recentClick;

    // Статичное фото не перерисовываем и не шлём 10 раз в секунду:
    // кадр уходит при изменении и редким «keep-alive», иначе панель вернётся к своей картинке.
    // 1000 мс оказалось много: панель успевала сбросить картинку и фото «мигало» раз в секунду.
    private const int KeepAliveMs = 300;
    private bool _frameStatic;
    private bool _frameDirty = true;
    private long _frameVersion;
    private long _sentVersion = -1;
    private DateTime _lastSendAt = DateTime.MinValue;

    // Частота: ползунок задаёт её для тем «Монитор», «Часы», «Минимал», «Тест».
    // В теме «Медиа» ритм берётся у самого файла, поэтому рабочая частота — отдельная.
    private const int MaxMediaFps = 30;
    private int _sliderFps = 2;
    private volatile int _effectiveFps = 2;
    private double _measuredFps;
    private string _infoText = "";

    // Яркость панели (программная, как в TRCC): 10–100 %.
    private int _brightness = 100;

    // Тексты, которые задаются из кода: их надо переписать при смене языка.
    private string _statusKey = "status.off";
    private object[] _statusArgs = Array.Empty<object>();
    private string? _logKey;
    private object[] _logArgs = Array.Empty<object>();
    private string _logRaw = "";

    // Датчики меняются медленно — незачем читать ADL/GetSystemTimes на каждый кадр.
    private const int SensorRefreshMs = 500;
    private DateTime _sensorsAt = DateTime.MinValue;

    // Пока true, настройки только читаются: события контролов при инициализации
    // и при применении сохранённой темы не должны перезаписывать файл.
    private bool _loading = true;

    public MainWindow()
    {
        InitializeComponent();

        // Иконка-бургер слегка пружинит при нажатии — движение читается как «щелчок».
        CompactToggle.RenderTransformOrigin = new Point(0.5, 0.5);
        CompactToggle.RenderTransform = new ScaleTransform(1, 1);

        // Ширина строки «недавних» меняется вместе с окном — рамку ставим по месту.
        RecentHost.SizeChanged += (_, _) => PlaceRecentGlass(animate: false);

        // Тема окна — раньше всего остального: от неё зависят все кисти.
        _skin = Skins.Normalize(_settings.UiTheme);
        ApplySkin(_skin);

        Loc.Set(_settings.Language);
        UpdateLangButtons();
        Loc.Changed += OnLanguageChanged;

        _panelDark = _settings.PanelDark;
        UpdatePanelButtons();

        _compact = _settings.Compact;
        ApplyCompact(_compact);

        // Автозапуск: ключ в HKCU\Run пишем при каждом старте, чтобы он
        // не потерялся после переустановки/переименования папки.
        if (_settings.AutoStart) Startup.Enable();
        else Startup.Disable();

        _tiles = new[]
        {
            (TileMonitor, ThemeKind.Monitor),
            (TileClock, ThemeKind.Clock),
            (TileMedia, ThemeKind.Media)
        };

        PresetList.ItemsSource = _themes.Items;
        RefreshPresetHint();
        SeedRecentFromSettings();
        SetUpTray();

        var args = Environment.GetCommandLineArgs();

        // Восстанавливаем прошлый сеанс.
        // Старое имя «MonitorDark» теперь означает «Монитор» + чёрная панель.
        if (IsOldDarkTheme(_settings.Theme))
        {
            _panelDark = true;
            UpdatePanelButtons();
        }

        SelectTheme(ParseTheme(_settings.Theme), apply: false);

        if (_settings.Fps is >= 1 and <= 10)
            FpsSlider.Value = _settings.Fps;

        if (_settings.MediaPath is { Length: > 0 } savedMedia && File.Exists(savedMedia))
            ApplyMedia(savedMedia, switchTheme: false);

        if (_settings.Brightness is >= 10 and <= 100)
            BrightnessSlider.Value = _settings.Brightness;

        SetAccent(ThemePreset.ToColor(_settings.Accent));

        // Командная строка главнее сохранённого: --fps N, --media <файл>, --brightness N.
        var fpsIndex = Array.FindIndex(args, a => a.Equals("--fps", StringComparison.OrdinalIgnoreCase));
        if (fpsIndex >= 0 && fpsIndex + 1 < args.Length && double.TryParse(args[fpsIndex + 1], out var fpsValue))
            FpsSlider.Value = Math.Clamp(fpsValue, FpsSlider.Minimum, FpsSlider.Maximum);

        var mediaIndex = Array.FindIndex(args, a => a.Equals("--media", StringComparison.OrdinalIgnoreCase));
        if (mediaIndex >= 0 && mediaIndex + 1 < args.Length)
            ApplyMedia(args[mediaIndex + 1], switchTheme: true);

        var brightnessIndex = Array.FindIndex(args, a => a.Equals("--brightness", StringComparison.OrdinalIgnoreCase));
        if (brightnessIndex >= 0 && brightnessIndex + 1 < args.Length
            && int.TryParse(args[brightnessIndex + 1], out var brightnessValue))
        {
            BrightnessSlider.Value = Math.Clamp(brightnessValue,
                BrightnessSlider.Minimum, BrightnessSlider.Maximum);
        }

        ApplyFrameRate();
        ApplyBrightness();
        SetStatus("status.off");
        UpdateToggleLabel();
        RefreshMediaText();
        RenderFrame();

        // Настройки восстановлены — только теперь можно их сохранять.
        _loading = false;

        // Одна петля на всё приложение: рисуем кадр → отправляем его на панель → пауза.
        _ = Task.Run(() => FrameLoop(_lifetime.Token));

        // Фон-арт собираем после появления окна, чтобы не задерживать запуск.
        Loaded += (_, _) =>
        {
            _loaded = true;

            if (BackdropMask.ImageSource is null)
                BackdropMask.ImageSource = Backdrop.Load();

            UpdateAutoStartButtons();
            RefreshRecent();
            PlaceGlass(animate: false);

            // Автозапуск: окно не нужно, экран СЖО работает из трея.
            if (Startup.IsAutoStart) HideToTray();
        };

        // Окно поменяло размер — плитки пересчитались, стекло встаёт по месту без анимации.
        SizeChanged += (_, _) =>
        {
            if (_glassPlaced) PlaceGlass(animate: false);
        };

        // --auto (ярлык) и --autostart (Windows): сразу начинать вывод.
        if (Startup.IsAutoStart || args.Any(a => a.Equals("--auto", StringComparison.OrdinalIgnoreCase)))
            Loaded += async (_, _) => await StartSendingWithRetryAsync();

        if (Startup.IsAutoStart)
        {
            // Окно не должно мигнуть на старте системы.
            Opacity = 0;
            ShowInTaskbar = false;
        }
    }

    /// <summary>Светлая или чёрная полоса заголовка — в тон выбранной теме окна.</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        UpdateTitleBar();
    }

    // ── Тема окна ───────────────────────────────────────────────────────────

    private void ApplySkin(string skin)
    {
        _skin = Skins.Normalize(skin);
        Skins.Apply(_skin);

        if (SkinLight is not null)
        {
            SkinLight.IsChecked = _skin == Skins.Light;
            SkinDark.IsChecked = _skin == Skins.Dark;
        }

        // Программные подложки LiquidSegments не всегда ловят смену палитры сами.
        // Обновляем их после того, как выставлены IsChecked, иначе стекло
        // встанет на старый вариант.
        SkinSegments?.Refresh();
        PanelSegments?.Refresh();
        ViewSegments?.Refresh();
        AutoSegments?.Refresh();
        TabSegments?.Refresh();
        LangSegments?.Refresh();

        UpdateTitleBar();
    }

    private void OnSkinClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton option) return;

        ApplySkin(option.Tag as string ?? Skins.Light);
        option.IsChecked = true;
        SaveSettings();
    }

    private void UpdateTitleBar()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        var caption = ToColorRef(Skins.ColorOf("Paper"));
        var text = ToColorRef(Skins.ColorOf("Ink"));
        var dark = _skin == Skins.Dark ? 1 : 0;

        try
        {
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));    // светлая/тёмная полоса
            DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int)); // цвет полосы
            DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));    // цвет заголовка
        }
        catch
        {
            // Старая Windows — заголовок останется системным, на работу не влияет.
        }
    }

    private static int ToColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

    // ── Чёрная панель ───────────────────────────────────────────────────────

    private void UpdatePanelButtons()
    {
        if (PanelLight is null) return;

        PanelLight.IsChecked = !_panelDark;
        PanelDark.IsChecked = _panelDark;

        // В «Медиа» панель показывает файл как есть — выбор светлой/чёрной
        // панели ни на что не влияет, поэтому строка неактивна, как и «вид».
        var isThemed = _theme != ThemeKind.Media;
        PanelRow.IsEnabled = isThemed;
        PanelRow.Opacity = isThemed ? 1.0 : 0.4;
    }

    private void OnPanelClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton option) return;

        _panelDark = (option.Tag as string) == "dark";
        UpdatePanelButtons();

        _frameDirty = true;
        SaveSettings();

        if (IsLoaded) RenderFrame();
    }

    // ── Вид «Монитора»: обычный или минимал ─────────────────────────────────

    private void UpdateViewButtons()
    {
        if (ViewFull is null) return;

        ViewFull.IsChecked = !_minimal;
        ViewMinimal.IsChecked = _minimal;

        // Вид есть только у «Монитора» — у остальных тем строка неактивна.
        var isMonitor = _theme == ThemeKind.Monitor;
        ViewRow.IsEnabled = isMonitor;
        ViewRow.Opacity = isMonitor ? 1.0 : 0.4;
    }

    private void OnViewClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton option) return;

        _minimal = (option.Tag as string) == "minimal";
        UpdateViewButtons();

        _frameDirty = true;
        SaveSettings();

        if (IsLoaded) RenderFrame();
    }

    // ── Автозапуск ──────────────────────────────────────────────────────────

    private void UpdateAutoStartButtons()
    {
        if (AutoOff is null || AutoOn is null) return;

        AutoOff.IsChecked = !_settings.AutoStart;
        AutoOn.IsChecked = _settings.AutoStart;
    }

    private void OnAutoStartClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton option) return;

        _settings.AutoStart = (option.Tag as string) == "on";
        if (_settings.AutoStart) Startup.Enable();
        else Startup.Disable();

        UpdateAutoStartButtons();
        SaveSettings();
        SetLog("log.autostart", Loc.T(_settings.AutoStart ? "startup.on" : "startup.off"));
    }

    // ── Три полоски: свёрнутая левая панель ─────────────────────────────────

    /// <summary>
    /// Свёрнутый режим: подписи разделов прячутся, остаются только сами контролы,
    /// а левая колонка сужается — превью становится больше.
    /// </summary>
    private void ApplyCompact(bool compact, bool animate = false)
    {
        _compact = compact;

        if (CompactToggle is not null)
            CompactToggle.IsChecked = compact;

        var labels = CollapsibleLabels().ToArray();
        var targetWidth = compact ? 250 : 452;
        var targetLabelWidth = compact ? 0 : 64;

        if (!animate || !_loaded)
        {
            if (LeftColumn is not null) LeftColumn.Width = new GridLength(targetWidth);

            foreach (var label in labels)
            {
                label.BeginAnimation(OpacityProperty, null);
                label.Opacity = compact ? 0 : 1;
                label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            }

            SetLabelColumns(targetLabelWidth);
            UpdateCompactTip();
            RefreshRecent();
            return;
        }

        // Ширина колонки и подписи уезжают вместе: панель не «прыгает», а складывается.
        var duration = TimeSpan.FromMilliseconds(260);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        AnimateColumn(LeftColumn, targetWidth, duration, easing);
        AnimateLabelColumn(WindowLabelColumn, targetLabelWidth, duration, easing);
        AnimateLabelColumn(PanelLabelColumn, targetLabelWidth, duration, easing);
        AnimateLabelColumn(ViewLabelColumn, targetLabelWidth, duration, easing);
        AnimateLabelColumn(AutoLabelColumn, targetLabelWidth, duration, easing);

        foreach (var label in labels)
        {
            if (!compact) label.Visibility = Visibility.Visible;

            var fade = new DoubleAnimation(compact ? 1 : 0, compact ? 0 : 1,
                TimeSpan.FromMilliseconds(compact ? 150 : 220))
            {
                EasingFunction = easing
            };

            if (compact)
            {
                fade.Completed += (_, _) =>
                {
                    if (_compact) label.Visibility = Visibility.Collapsed;
                };
            }

            label.BeginAnimation(OpacityProperty, fade);
        }

        UpdateCompactTip();
        RefreshRecent();
    }

    private void SetLabelColumns(double width)
    {
        var value = new GridLength(width);
        if (WindowLabelColumn is not null) WindowLabelColumn.Width = value;
        if (PanelLabelColumn is not null) PanelLabelColumn.Width = value;
        if (ViewLabelColumn is not null) ViewLabelColumn.Width = value;
        if (AutoLabelColumn is not null) AutoLabelColumn.Width = value;
    }

    private static void AnimateColumn(ColumnDefinition? column, double to, TimeSpan duration, IEasingFunction easing)
    {
        if (column is null) return;

        column.BeginAnimation(ColumnDefinition.WidthProperty, new GridLengthAnimation
        {
            From = column.Width,
            To = new GridLength(to),
            Duration = duration,
            EasingFunction = easing
        });
    }

    private static void AnimateLabelColumn(ColumnDefinition? column, double to, TimeSpan duration, IEasingFunction easing) =>
        AnimateColumn(column, to, duration, easing);

    private IEnumerable<TextBlock> CollapsibleLabels() => new[]
    {
        SectionTheme, RowWindowLabel, RowPanelLabel, RowViewLabel, RowAutoStartLabel,
        SectionMedia, MediaText, RecentLabel,
        SectionMyThemes, ThemesHint
    };

    private void UpdateCompactTip()
    {
        if (CompactToggle is null) return;

        CompactToggle.ToolTip = Loc.T(_compact ? "compact.show" : "compact.hide");
    }

    private void OnCompactClick(object sender, RoutedEventArgs e)
    {
        AnimateBurgerPress();
        ApplyCompact(CompactToggle.IsChecked == true, animate: true);
        SaveSettings();
    }

    private void AnimateBurgerPress()
    {
        if (CompactToggle?.RenderTransform is not ScaleTransform scale) return;

        var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(260) };
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0.9,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(60)),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)),
            new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut }));

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    // ── Вкладки и язык ──────────────────────────────────────────────────────

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tab) return;

        ShowTab((tab.Tag as string) == "themes" ? "themes" : "settings");
    }

    private void ShowTab(string tab)
    {
        var themes = tab == "themes";

        SettingsPanel.Visibility = themes ? Visibility.Collapsed : Visibility.Visible;
        ThemesPanel.Visibility = themes ? Visibility.Visible : Visibility.Collapsed;

        TabSettings.IsChecked = !themes;
        TabThemes.IsChecked = themes;

        // Миниатюры рисуем при первом показе вкладки: к этому моменту датчики
        // уже прочитаны, и кадр в списке совпадает с тем, что видно на панели.
        if (themes) _ = RefreshPresetPreviewsAsync();
    }

    private void OnLangClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton option) return;

        Loc.Set(option.Tag as string);
        UpdateLangButtons();
        SaveSettings();
    }

    private void UpdateLangButtons()
    {
        if (LangRu is null) return;

        LangRu.IsChecked = !Loc.English;
        LangEn.IsChecked = Loc.English;
    }

    /// <summary>Язык сменился: переписываем тексты, которые задаются из кода.</summary>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        UpdateLangButtons();
        UpdateCompactTip();
        UpdateToggleLabel();
        RefreshStatus();
        RefreshLog();
        UpdateMediaSection();
        RefreshEffectiveFps();
        PresetList.Items.Refresh();

        _infoText = "";
        UpdateInfoText(CurrentTheme);

        if (IsLoaded) RenderFrame();
    }

    private void SetStatus(string key, params object[] args)
    {
        _statusKey = key;
        _statusArgs = args;
        RefreshStatus();
    }

    private void RefreshStatus() => StatusText.Text = Loc.Format(_statusKey, _statusArgs);

    private void SetLog(string key, params object[] args)
    {
        _logKey = key;
        _logArgs = args;
        _logRaw = "";
        RefreshLog();
    }

    private void SetLogRaw(string text)
    {
        _logKey = null;
        _logRaw = text;
        RefreshLog();
    }

    private void ClearLog()
    {
        _logKey = null;
        _logRaw = "";
        RefreshLog();
    }

    private void RefreshLog() =>
        LogText.Text = _logKey is null ? _logRaw : Loc.Format(_logKey, _logArgs);

    // ── Тема панели ─────────────────────────────────────────────────────────

    private ThemeKind CurrentTheme => _theme;

    /// <summary>Что рисовать: «Минимал» — это вид темы «Монитор», а не своя тема.</summary>
    private ThemeKind RenderKind() =>
        _theme == ThemeKind.Monitor && _minimal ? ThemeKind.Minimal : _theme;

    private static bool IsOldDarkTheme(string? value) =>
        value is "MonitorDark" or "Чёрный" or "Черный";

    private static ThemeKind ParseTheme(string? value) => value switch
    {
        "Чёрный" => ThemeKind.Monitor,
        "Черный" => ThemeKind.Monitor,
        "MonitorDark" => ThemeKind.Monitor,
        "Часы" => ThemeKind.Clock,
        "Медиа" => ThemeKind.Media,
        "Минимал" => ThemeKind.Minimal,
        "Тест" => ThemeKind.Test,
        _ => Enum.TryParse<ThemeKind>(value, true, out var kind) ? kind : ThemeKind.Monitor
    };

    private void SelectTheme(ThemeKind kind, bool apply)
    {
        // «Минимал» — не отдельная тема, а вид темы «Монитор».
        if (kind == ThemeKind.Minimal)
        {
            _minimal = true;
            kind = ThemeKind.Monitor;
        }

        _theme = kind;

        ToggleButton? selectedTile = null;
        foreach (var (tile, tileKind) in _tiles)
        {
            var current = tileKind == kind;
            tile.IsChecked = current;
            if (current) selectedTile = tile;
        }

        UpdateViewButtons();
        UpdatePanelButtons();
        PlaceGlass(animate: IsLoaded && _glassPlaced, target: selectedTile);
        ApplyBackdrop();
        UpdateMediaSection();

        // В теме «Медиа» частота берётся у файла — ползунок темпа там не нужен вовсе.
        if (FpsBlock is not null)
            FpsBlock.Visibility = kind == ThemeKind.Media ? Visibility.Collapsed : Visibility.Visible;

        RefreshEffectiveFps();

        if (!apply) return;

        _frameDirty = true;
        SaveSettings();

        if (IsLoaded) RenderFrame();
    }

    private void OnThemeTileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tile) return;

        if (tile.Tag is string tag && Enum.TryParse<ThemeKind>(tag, true, out var kind))
            SelectTheme(kind, apply: true);

        // Активную тему нельзя «снять» повторным щелчком.
        tile.IsChecked = true;
    }

    // ── Жидкое стекло под плитками тем ──────────────────────────────────────

    /// <summary>
    /// Ставит стеклянную заливку под выбранную плитку. При переключении она
    /// переезжает с вытягиванием по ходу движения — переход читается как жидкость,
    /// а не как ползунок.
    /// </summary>
    private void PlaceGlass(bool animate, ToggleButton? target = null)
    {
        if (TileGlass is null || TileGlassLayer is null) return;

        target ??= CurrentTile();
        if (target is null || target.ActualWidth <= 0) return;

        var point = target.TranslatePoint(new Point(0, 0), TileGlassLayer);

        if (!animate || !_glassPlaced)
        {
            StopGlassTracking();

            TileGlass.BeginAnimation(Canvas.LeftProperty, null);
            TileGlass.BeginAnimation(FrameworkElement.WidthProperty, null);
            TileGlassScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

            Canvas.SetLeft(TileGlass, point.X);
            Canvas.SetTop(TileGlass, point.Y);
            TileGlass.Width = target.ActualWidth;
            TileGlass.Height = target.ActualHeight;
            TileGlassScale.ScaleY = 1;

            _glassPlaced = true;
            SyncGlassClip();
            return;
        }

        var fromX = Canvas.GetLeft(TileGlass);
        if (double.IsNaN(fromX)) fromX = point.X;

        var fromWidth = double.IsNaN(TileGlass.Width) ? target.ActualWidth : TileGlass.Width;
        var targetWidth = target.ActualWidth;

        // Чем дальше переезд, тем сильнее стекло вытягивается по ходу движения:
        // на месте — ноль, через две плитки — максимум. Высота при этом проседает.
        var stretch = Math.Min(0.22,
            Math.Abs(point.X - fromX) / Math.Max(1.0, targetWidth) * 0.32);

        var duration = TimeSpan.FromMilliseconds(360);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        var move = new DoubleAnimationUsingKeyFrames { Duration = duration };
        move.KeyFrames.Add(new LinearDoubleKeyFrame(fromX, KeyTime.FromPercent(0)));
        move.KeyFrames.Add(new EasingDoubleKeyFrame(point.X, KeyTime.FromPercent(1), easing));

        // Ширина, а не ScaleX: тот же прямоугольник уходит в клип подписей,
        // поэтому инверсия текста едет ровно по краю стекла.
        var width = new DoubleAnimationUsingKeyFrames { Duration = duration };
        width.KeyFrames.Add(new LinearDoubleKeyFrame(fromWidth, KeyTime.FromPercent(0)));
        width.KeyFrames.Add(new EasingDoubleKeyFrame(targetWidth * (1 + stretch), KeyTime.FromPercent(0.34),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        width.KeyFrames.Add(new EasingDoubleKeyFrame(targetWidth, KeyTime.FromPercent(1), easing));

        var squash = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(480) };
        squash.KeyFrames.Add(new LinearDoubleKeyFrame(TileGlassScale.ScaleY, KeyTime.FromPercent(0)));
        squash.KeyFrames.Add(new EasingDoubleKeyFrame(1 - stretch * 0.8, KeyTime.FromPercent(0.3),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        squash.KeyFrames.Add(new EasingDoubleKeyFrame(1.03, KeyTime.FromPercent(0.62),
            new CubicEase { EasingMode = EasingMode.EaseInOut }));
        squash.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1),
            new CubicEase { EasingMode = EasingMode.EaseOut }));

        TileGlass.BeginAnimation(Canvas.LeftProperty, move);
        TileGlass.BeginAnimation(FrameworkElement.WidthProperty, width);
        TileGlassScale.BeginAnimation(ScaleTransform.ScaleYProperty, squash);

        StartGlassTracking();
    }

    private ToggleButton? CurrentTile()
    {
        foreach (var (tile, kind) in _tiles)
        {
            if (kind == _theme) return tile;
        }

        return null;
    }

    /// <summary>Клип подписей должен совпадать со стеклом на каждом кадре анимации.</summary>
    private void StartGlassTracking()
    {
        _glassTrackUntil = DateTime.UtcNow.AddMilliseconds(650);

        if (_glassTracking) return;

        _glassTracking = true;
        CompositionTarget.Rendering += OnGlassRendering;
    }

    private void StopGlassTracking()
    {
        if (!_glassTracking) return;

        _glassTracking = false;
        CompositionTarget.Rendering -= OnGlassRendering;
    }

    private void OnGlassRendering(object? sender, EventArgs e)
    {
        SyncGlassClip();

        if (DateTime.UtcNow >= _glassTrackUntil) StopGlassTracking();
    }

    private void SyncGlassClip()
    {
        if (TileGlass is null) return;

        var x = Canvas.GetLeft(TileGlass);
        var y = Canvas.GetTop(TileGlass);
        if (double.IsNaN(x) || double.IsNaN(y)) return;

        var rect = new Rect(x, y, TileGlass.Width, TileGlass.Height);

        if (TileInvertClip is not null) TileInvertClip.Rect = rect;
        if (TileHoleRect is not null) TileHoleRect.Rect = rect;
    }

    // ── Оформление ──────────────────────────────────────────────────────────

    /// <summary>Акцент приходит из сохранённой темы; отдельного выбора в окне нет.</summary>
    private void SetAccent(Color color)
    {
        _accent = color;
        ThemeRenderer.InvalidateCaption();
        _frameDirty = true;
    }

    // ── Частота и яркость ───────────────────────────────────────────────────

    /// <summary>Ползунок: частота для тем, которые рисуются сами.</summary>
    private void ApplyFrameRate()
    {
        // Событие значения может прийти до готовности элементов (Value задан в XAML).
        if (FpsSlider is null) return;

        _sliderFps = (int)Math.Max(1, Math.Round(FpsSlider.Value));

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

        if (FpsSlider is not null)
            FpsSlider.IsEnabled = !isMedia;
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

    private void SaveSettings()
    {
        // Пока окно восстанавливает прошлый сеанс, сохранять нельзя:
        // иначе значения по умолчанию затирают файл раньше, чем мы его прочитали.
        if (_loading) return;
        if (FpsSlider is null) return;

        _settings.Fps = (int)Math.Round(FpsSlider.Value);
        _settings.Brightness = BrightnessSlider is null ? _brightness : (int)Math.Round(BrightnessSlider.Value);
        _settings.Theme = RenderKind().ToString();
        _settings.Accent = ThemePreset.ToHex(_accent);
        _settings.UiTheme = _skin;
        _settings.PanelDark = _panelDark;
        _settings.Compact = _compact;
        _settings.Language = Loc.Language;
        _settings.Save();
    }

    // ── Сохранённые темы ────────────────────────────────────────────────────

    private void OnSavePresetClick(object sender, RoutedEventArgs e)
    {
        var name = PresetNameBox.Text.Trim();

        if (name.Length == 0)
        {
            name = $"{KindName(RenderKind())} {_themes.Items.Count + 1}";
            PresetNameBox.Text = name;
        }

        var preset = new ThemePreset
        {
            Name = name,
            Kind = RenderKind().ToString(),
            MediaPath = _mediaPath,
            Brightness = _brightness,
            Fps = _sliderFps,
            Accent = ThemePreset.ToHex(_accent),
            Dark = _panelDark
        };

        _themes.Upsert(preset);
        PresetList.SelectedItem = preset;
        RefreshPresetHint();
        _ = UpdatePresetPreviewAsync(preset);

        SetLog("log.presetSaved", name);
    }

    /// <summary>Дорисовывает миниатюры тем, у которых их ещё нет.</summary>
    private async Task RefreshPresetPreviewsAsync()
    {
        var snapshot = _snapshot;

        foreach (var preset in _themes.Items)
        {
            if (preset.Preview is not null) continue;

            var preview = await ThemePreview.CreateAsync(preset, snapshot);
            if (preview is not null) preset.Preview = preview;
        }
    }

    private async Task UpdatePresetPreviewAsync(ThemePreset preset)
    {
        var preview = await ThemePreview.CreateAsync(preset, _snapshot);
        if (preview is not null) preset.Preview = preview;
    }

    private void RefreshPresetHint()
    {
        if (PresetEmpty is null) return;

        PresetEmpty.Visibility = _themes.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnApplyPresetClick(object sender, RoutedEventArgs e) => ApplySelectedPreset();

    private void OnPresetDoubleClick(object sender, MouseButtonEventArgs e) => ApplySelectedPreset();

    private void ApplySelectedPreset()
    {
        if (PresetList.SelectedItem is not ThemePreset preset)
        {
            SetLog("log.pickTheme");
            return;
        }

        ApplyPreset(preset);
    }

    private void ApplyPreset(ThemePreset preset)
    {
        // Пока раскладываем значения по контролам, файл настроек не трогаем:
        // иначе промежуточные состояния успеют в него попасть.
        _loading = true;

        try
        {
            if (preset.ToKind() == ThemeKind.Media)
            {
                if (preset.MediaPath is { Length: > 0 } path && File.Exists(path))
                    ApplyMedia(path, switchTheme: false);
                else if (preset.MediaPath is { Length: > 0 })
                    SetLog("log.presetMissing", preset.MediaPath);
            }

            FpsSlider.Value = Math.Clamp(preset.Fps, FpsSlider.Minimum, FpsSlider.Maximum);
            BrightnessSlider.Value = Math.Clamp(preset.Brightness,
                BrightnessSlider.Minimum, BrightnessSlider.Maximum);

            SetAccent(ThemePreset.ToColor(preset.Accent));
            PresetNameBox.Text = preset.Name;

            _panelDark = preset.Dark;
            UpdatePanelButtons();

            _minimal = preset.ToKind() == ThemeKind.Minimal;

            SelectTheme(preset.ToKind(), apply: false);
        }
        finally
        {
            _loading = false;
        }

        ApplyFrameRate();
        ApplyBrightness();
        _frameDirty = true;
        SaveSettings();

        RenderFrame();

        SetLog("log.presetApplied", preset.Name);
    }

    private void OnDeletePresetClick(object sender, RoutedEventArgs e)
    {
        if (PresetList.SelectedItem is not ThemePreset preset)
        {
            SetLog("log.pickTheme");
            return;
        }

        _themes.Remove(preset.Name);
        RefreshPresetHint();
        SetLog("log.presetDeleted", preset.Name);
    }

    private static string KindName(ThemeKind kind) => kind switch
    {
        ThemeKind.Clock => Loc.T("tile.clock"),
        ThemeKind.Media => Loc.T("tile.media"),
        ThemeKind.Minimal => Loc.T("tile.minimal"),
        ThemeKind.Test => Loc.T("tile.test"),
        _ => Loc.T("tile.monitor")
    };

    // ── Петля кадров ────────────────────────────────────────────────────────

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
                            await Dispatcher.InvokeAsync(() => SetLog("log.sendError", message));
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
        var theme = RenderKind();

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
                SetLog("log.video", videoError);

            var frame = ThemeRenderer.Render(theme, _snapshot, _clip, _accent, null, _panelDark, _backdrop);
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
                ? Loc.Format("info.static", Volatile.Read(ref _measuredFps))
                : Loc.Format("info.rate", Volatile.Read(ref _measuredFps), _effectiveFps)
            : Loc.T("info.stopped");

        var gpu = _snapshot.GpuTempC is { } g ? $"{g:0}°C" : Loc.T("info.nogpu");

        var text = theme is ThemeKind.Media
            ? rate
            : string.Join("   ",
                  Loc.Format("info.cpu", _snapshot.CpuLoadPercent),
                  Loc.Format("info.ram", _snapshot.RamUsedGb, _snapshot.RamTotalGb),
                  Loc.Format("info.gpu", gpu))
              + "\n" + rate;

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

    // ── Медиа ───────────────────────────────────────────────────────────────

    /// <summary>Загружает гифку, фото или видео и (по желанию) включает тему «Медиа».</summary>
    private void ApplyMedia(string path, bool switchTheme)
    {
        if (!File.Exists(path))
        {
            SetLog("log.noFile", path);
            return;
        }

        if (!MediaClipFactory.IsSupported(path))
        {
            SetLog("log.format", Path.GetExtension(path));
            return;
        }

        try
        {
            var clip = MediaClipFactory.Open(path, ThemeRenderer.Width, ThemeRenderer.Height);

            var previous = _clip;
            _clip = clip;
            previous?.Dispose();

            _mediaPath = path;
            RefreshMediaText();
            ClearLog();

            if (switchTheme)
                SelectTheme(ThemeKind.Media, apply: true);

            _settings.MediaPath = path;
            RememberRecent(path);
            SaveSettings();

            // Новый файл — новый кадр: сбрасываем «статичность» и пересчитываем частоту по файлу.
            _frameDirty = true;
            RefreshEffectiveFps();

            RenderFrame();
        }
        catch (Exception ex)
        {
            SetLog("log.openFail", Path.GetFileName(path), ex.Message);
        }
    }

    private void RefreshMediaText()
    {
        if (MediaText is null) return;

        // У «Монитора» и «Часов» вместо файла медиа показываем фон темы.
        if (IsBackdropTheme)
        {
            MediaText.Text = _backdropPath is null
                ? Loc.T("media.defaultBg")
                : Path.GetFileName(_backdropPath);
            return;
        }

        MediaText.Text = _clip is null || _mediaPath is null
            ? Loc.T("media.none")
            : $"{Loc.T(ClipKey(_clip))}: {Path.GetFileName(_mediaPath)}";
    }

    private static string ClipKey(MediaClip clip) => clip switch
    {
        VideoClip => "media.video",
        GifClip => "media.gif",
        _ => "media.still"
    };

    private void OnPickClick(object sender, RoutedEventArgs e)
    {
        if (IsBackdropTheme)
            PickBackdrop();
        else
            PickMedia();
    }

    private void PickMedia()
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.T("media.dialog"),
            Filter = MediaClipFactory.FileFilter,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
            ApplyMedia(dialog.FileName, switchTheme: true);
    }

    private void PickBackdrop()
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.T("backdrop.dialog"),
            Filter = MediaClipFactory.ImageFilter,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
            SetBackdrop(dialog.FileName);
    }

    // ── Фон тем «Монитор» и «Часы» ──────────────────────────────────────────

    /// <summary>Фон меняется только у тем с датчиками: «Монитор» и «Часы».</summary>
    private bool IsBackdropTheme => _theme is ThemeKind.Monitor or ThemeKind.Clock;

    /// <summary>Подхватывает сохранённый фон; он общий для «Монитора» и «Часов».</summary>
    private void ApplyBackdrop()
    {
        _backdropPath = _settings.Backdrop is { Length: > 0 } path && File.Exists(path) ? path : null;
        _backdrop = _backdropPath is null ? null : LoadImage(_backdropPath);
    }

    /// <summary>Подпись раздела и кнопок: «МЕДИА» или «ФОН».</summary>
    private void UpdateMediaSection()
    {
        if (SectionMedia is null) return;

        var backdrop = IsBackdropTheme;

        SectionMedia.Text = Loc.T(backdrop ? "section.background" : "section.media");
        MediaButton.Content = Loc.T(backdrop ? "media.pickBg" : "media.pick");
        ResetBackdropButton.Visibility = backdrop ? Visibility.Visible : Visibility.Collapsed;
        ResetBackdropButton.IsEnabled = _backdropPath is not null;

        RefreshMediaText();
        RefreshRecent();
    }

    private void SetBackdrop(string path)
    {
        if (LoadImage(path) is null)
        {
            SetLog("log.openFail", Path.GetFileName(path), Loc.T("backdrop.bad"));
            return;
        }

        _settings.Backdrop = path;
        ApplyBackdrop();
        UpdateMediaSection();
        RememberRecent(path);
        SaveSettings();
        ClearLog();

        _frameDirty = true;
        RenderFrame();
    }

    private void OnResetBackdropClick(object sender, RoutedEventArgs e)
    {
        if (_settings.Backdrop is null) return;

        _settings.Backdrop = null;
        ApplyBackdrop();
        UpdateMediaSection();
        SaveSettings();

        _frameDirty = true;
        RenderFrame();
    }

    // ── Недавние файлы ──────────────────────────────────────────────────────

    /// <summary>При первом запуске новой версии подхватываем текущие файлы как недавние.</summary>
    private void SeedRecentFromSettings()
    {
        if (_settings.RecentMedia.Count > 0) return;

        if (_settings.Backdrop is { Length: > 0 } backdrop && File.Exists(backdrop))
            _settings.RecentMedia.Add(backdrop);

        if (_settings.MediaPath is { Length: > 0 } media && File.Exists(media))
            _settings.RecentMedia.Add(media);

        if (_settings.RecentMedia.Count > 0) SaveSettings();
    }

    private void RememberRecent(string path)
    {
        // Клик по уже показанному «недавнему» не переставляет строку:
        // иначе стеклянная рамка прыгала бы вместе с плитками.
        if (_recentClick) return;

        _settings.RecentMedia.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _settings.RecentMedia.Insert(0, path);

        while (_settings.RecentMedia.Count > 4)
            _settings.RecentMedia.RemoveAt(_settings.RecentMedia.Count - 1);

        RefreshRecent();
    }

    private void RefreshRecent()
    {
        if (RecentGrid is null || RecentLabel is null || RecentGlass is null) return;
        if (_recentClick) return;

        var paths = _settings.RecentMedia
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .Where(p => IsBackdropTheme ? MediaClipFactory.IsImage(p) : MediaClipFactory.IsSupported(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();

        RecentGrid.Children.Clear();
        _recentTiles.Clear();

        foreach (var path in paths)
        {
            var tile = BuildRecentTile(path);
            RecentGrid.Children.Add(tile);
            _recentTiles.Add((path, tile));
        }

        var hasItems = paths.Length > 0;
        RecentGrid.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        RecentLabel.Visibility = hasItems && !_compact ? Visibility.Visible : Visibility.Collapsed;

        PlaceRecentGlass(animate: false);

        // После пересборки плитки ещё не измерены — ставим рамку, когда layout дойдёт.
        Dispatcher.BeginInvoke(new Action(() => PlaceRecentGlass(animate: false)),
            DispatcherPriority.Loaded);
    }

    private Border BuildRecentTile(string path)
    {
        var width = _compact ? 52.0 : 96.0;
        var height = _compact ? 36.0 : 64.0;

        var grid = new Grid();

        if (!MediaClipFactory.IsImage(path))
        {
            grid.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x15, 0x14, 0x1A))
            });

            grid.Children.Add(new TextBlock
            {
                Text = "▶",
                FontSize = _compact ? 11 : 18,
                Foreground = Brushes.White,
                Opacity = 0.85,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        else
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(path);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = (int)(width * 2);
                image.EndInit();
                image.Freeze();

                grid.Children.Add(new Image
                {
                    Source = image,
                    Stretch = Stretch.UniformToFill,
                    Clip = new RectangleGeometry(new Rect(0, 0, width, height), 8, 8)
                });
            }
            catch
            {
                // Битую картинку показываем пустой плашкой.
            }
        }

        var format = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        if (format.Length == 0) format = "FILE";

        var badge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x14, 0x12, 0x19)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(_compact ? 3 : 4, 1, _compact ? 3 : 4, 1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(3),
            Child = new TextBlock
            {
                Text = format,
                FontSize = _compact ? 7 : 8,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White
            }
        };

        grid.Children.Add(badge);

        var tile = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 6, 0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = Path.GetFileName(path),
            Child = grid
        };

        tile.SetResourceReference(Border.BorderBrushProperty, "Rule");
        tile.MouseLeftButtonUp += (_, _) => ApplyRecent(path, tile);

        System.Windows.Automation.AutomationProperties.SetAutomationId(tile, "RecentTile" + _recentTiles.Count);
        System.Windows.Automation.AutomationProperties.SetName(tile, Path.GetFileName(path));

        return tile;
    }

    private void ApplyRecent(string path, Border tile)
    {
        if (!File.Exists(path))
        {
            RefreshRecent();
            return;
        }

        _recentClick = true;

        try
        {
            // В «Мониторе» и «Часах» недавняя картинка становится фоном, в «Медиа» — кадром.
            if (IsBackdropTheme)
            {
                if (MediaClipFactory.IsImage(path)) SetBackdrop(path);
            }
            else
            {
                ApplyMedia(path, switchTheme: false);
            }
        }
        finally
        {
            _recentClick = false;
        }

        // Строка не пересобиралась — просто перевозим стеклянную рамку к выбранному файлу.
        PlaceRecentGlass(animate: true, tile);
    }

    private Border? FindActiveRecentTile()
    {
        var active = IsBackdropTheme ? _backdropPath : _mediaPath;
        if (string.IsNullOrWhiteSpace(active)) return null;

        foreach (var (path, tile) in _recentTiles)
        {
            if (string.Equals(path, active, StringComparison.OrdinalIgnoreCase))
                return tile;
        }

        return null;
    }

    /// <summary>Стеклянная рамка вокруг активного «недавнего» файла переезжает как остальное.</summary>
    private void PlaceRecentGlass(bool animate, Border? target = null)
    {
        if (RecentGlass is null || RecentGlassLayer is null) return;

        var tile = target ?? FindActiveRecentTile();
        if (tile is null || tile.ActualWidth <= 0)
        {
            RecentGlass.Visibility = Visibility.Collapsed;
            return;
        }

        var point = tile.TranslatePoint(new Point(0, 0), RecentGlassLayer);

        RecentGlass.Visibility = Visibility.Visible;
        RecentGlass.Height = tile.ActualHeight;

        var fromX = Canvas.GetLeft(RecentGlass);
        if (!animate || double.IsNaN(fromX) || RecentGlass.Width <= 0)
        {
            RecentGlass.BeginAnimation(Canvas.LeftProperty, null);
            RecentGlass.BeginAnimation(FrameworkElement.WidthProperty, null);

            Canvas.SetLeft(RecentGlass, point.X);
            Canvas.SetTop(RecentGlass, point.Y);
            RecentGlass.Width = tile.ActualWidth;
            return;
        }

        var stretch = Math.Min(0.14,
            Math.Abs(point.X - fromX) / Math.Max(1.0, tile.ActualWidth) * 0.25);

        var duration = TimeSpan.FromMilliseconds(240);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        RecentGlass.BeginAnimation(Canvas.LeftProperty,
            new DoubleAnimation(fromX, point.X, duration) { EasingFunction = easing });

        var width = new DoubleAnimationUsingKeyFrames { Duration = duration };
        width.KeyFrames.Add(new LinearDoubleKeyFrame(RecentGlass.Width, KeyTime.FromPercent(0)));
        width.KeyFrames.Add(new EasingDoubleKeyFrame(tile.ActualWidth * (1 + stretch),
            KeyTime.FromPercent(0.4), easing));
        width.KeyFrames.Add(new EasingDoubleKeyFrame(tile.ActualWidth, KeyTime.FromPercent(1), easing));

        RecentGlass.BeginAnimation(FrameworkElement.WidthProperty, width);
    }

    /// <summary>
    /// Картинка фона. Уменьшаем при загрузке до двойного размера кадра:
    /// фото с телефона иначе занимало бы десятки мегабайт ради 320×240.
    /// </summary>
    private static BitmapSource? LoadImage(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.CacheOption = BitmapCacheOption.OnLoad;   // файл не держим открытым
            image.DecodePixelWidth = ThemeRenderer.Width * 2;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedFile(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDropFile(object sender, DragEventArgs e)
    {
        if (TryGetDroppedFile(e) is not { } path) return;

        // В «Мониторе» и «Часах» брошенная картинка становится фоном темы.
        if (IsBackdropTheme)
            SetBackdrop(path);
        else
            ApplyMedia(path, switchTheme: true);

        e.Handled = true;
    }

    private string? TryGetDroppedFile(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return null;

        var path = files[0];

        // Фоном может быть только картинка, в «Медиа» — ещё и видео.
        return IsBackdropTheme
            ? MediaClipFactory.IsImage(path) ? path : null
            : MediaClipFactory.IsSupported(path) ? path : null;
    }

    // ── Вывод на панель ─────────────────────────────────────────────────────

    private void UpdateToggleLabel() =>
        ToggleButton.Content = Loc.T(_running ? "run.stop" : "run.start");

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
        UpdateToggleLabel();
        ToggleButton.IsEnabled = false;
        SetStatus("status.connecting");
        ClearLog();

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
            SetStatus("status.connected", device.Profile.Width, device.Profile.Height,
                device.Info.Pm, device.Info.Fbl);
        }
        catch (Exception ex)
        {
            _running = false;
            UpdateToggleLabel();
            SetStatus("status.failed");
            SetLogRaw(ex.Message);
        }
        finally
        {
            ToggleButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Автозапуск может обогнать USB: панель ещё не готова — пробуем несколько раз.
    /// </summary>
    private async Task StartSendingWithRetryAsync()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (_lifetime.IsCancellationRequested) return;

            await StartSendingAsync();
            if (_running) return;

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
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

        UpdateToggleLabel();
        SetStatus("status.stopped");
    }

    // ── Трей: закрытие окна не гасит экран СЖО ──────────────────────────────

    private void SetUpTray()
    {
        try
        {
            _tray = new TrayIcon(
                Loc.T("tray.title"),
                Loc.T("tray.open"),
                Loc.T("tray.exit"));

            _tray.Activate += ShowFromTray;
            _tray.ExitRequested += ExitFromTray;
        }
        catch
        {
            // Без иконки приложение всё равно работает; закрытие тогда завершит его.
            _tray = null;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Крестик прячет окно, но вывод продолжается: панель не должна
        // вернуться к своей картинке, пока приложение действительно не выйдет.
        if (!_exiting)
        {
            e.Cancel = true;

            if (_tray is not null && !_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowBalloon(Loc.T("tray.hint.title"), Loc.T("tray.hint"));
            }

            HideToTray();
            return;
        }

        base.OnClosing(e);
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Opacity = 1;
        Hide();
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        Opacity = 1;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        _exiting = true;

        _tray?.Dispose();
        _tray = null;

        Close();
        Application.Current.Shutdown();
    }

    protected override void OnClosed(EventArgs e)
    {
        _lifetime.Cancel();
        Loc.Changed -= OnLanguageChanged;

        var device = _device;
        _device = null;
        device?.Dispose();

        _clip?.Dispose();
        _clip = null;

        _tray?.Dispose();
        _tray = null;

        base.OnClosed(e);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
