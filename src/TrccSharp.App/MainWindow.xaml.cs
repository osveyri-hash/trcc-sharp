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

    // Настройки пишутся с задержкой: ползунки не должны грузить диск на каждый пиксель.
    private readonly DispatcherTimer _saveDelay = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly Settings _settings = Settings.Load();
    private readonly ThemeStore _themes = ThemeStore.Load();


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

    /// <summary>Анимированный фон (GIF или видео) — играет вместо статичного фото.</summary>
    private MediaClip? _backdropClip;

    // Оформление темы: акцент внизу кадра (выбирается только пресетом).
    private ThemeKind _theme = ThemeKind.Monitor;
    private Color _accent = ThemeRenderer.DefaultAccent;


    // Своя раскладка: объекты панели и выделение (одно или несколько).
    private List<PanelWidget> _widgets = new();
    private readonly Dictionary<ThemeKind, List<PanelWidget>> _layouts = new();
    private readonly List<PanelWidget> _selection = new();
    private bool _dragging;
    private readonly Dictionary<PanelWidget, Point> _dragOrigins = new();
    private Point _dragStart;

    // Рамка выделения: тянем по пустому месту и обводим несколько объектов.
    private bool _marquee;
    private Point _marqueeStart;

    // Оформление кадра: шрифт, цвет текста и яркость фона.
    private string _panelFont = "Segoe UI";
    private string _panelInk = "";
    private int _background = 100;
    private int _widgetBrightness = 100;

    // Сдвиг и масштаб содержимого — свой для каждого вида.
    private readonly Dictionary<ThemeKind, ViewTransform> _transforms = new();
    private bool _syncingScale;
    private bool _syncingFont;
    private bool _syncingText;
    private readonly Dictionary<PanelWidget, string?> _fontPreview = new();

    // «Жидкое стекло» в меню добавления объектов.
    private Button? _glassItem;

    // Своя палитра цвета: тон, насыщенность, яркость и что сейчас тянут.
    private double _hue;
    private double _saturation = 1;
    private double _value = 1;
    private bool _pickingSquare;
    private bool _pickingHue;
    private bool _movingView;
    private Point _moveStart;
    private double _moveFromX;
    private double _moveFromY;

    // Тема самого окна: светлая или тёмная.
    private string _skin = Skins.Light;

    // Чёрный вариант панели — тот же рисунок, но тушь светлая.
    private bool _panelDark;

    // Вид темы «Монитор»: обычный, минимал, график, кольца и ещё десять —
    // все они живут внутри одной плитки «Монитор» строкой «ВИД».

    // Левая панель свёрнута до одних контролов (кнопка «три полоски»).
    private bool _compact;

    // Стеклянная заливка под плитками тем уже стоит на месте.

    // Превью обновляется в один и тот же WriteableBitmap — без нового BitmapSource на кадр.
    private WriteableBitmap? _previewBitmap;
    private volatile bool _hiddenToTray;

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

        // Меню закрылось (щелчок мимо) — стекло не должно остаться висеть.
        AddMenuPopup.Closed += (_, _) => ResetAddMenuGlass();

        _saveDelay.Tick += (_, _) =>
        {
            _saveDelay.Stop();
            SaveSettings();
        };

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

        _compact = _settings.Compact;
        ApplyCompact(_compact);

        // Автозапуск: ключ в HKCU\Run пишем при каждом старте, чтобы он
        // не потерялся после переустановки/переименования папки.
        if (_settings.AutoStart) Startup.Enable();
        else Startup.Disable();


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
        }

        // Настройки кадра — до выбора вида. Иначе «Свой» вид успевает добавить
        // стартовый набор объектов, и он ложится поверх сохранённой раскладки.
        foreach (var pair in _settings.Layouts)
        {
            if (Enum.TryParse<ThemeKind>(pair.Key, true, out var layoutKind) && pair.Value is not null)
                _layouts[layoutKind] = pair.Value;
        }

        _widgets = LayoutFor(RenderKind());
        PublishWidgets();

        _panelFont = string.IsNullOrWhiteSpace(_settings.PanelFont) ? "Segoe UI" : _settings.PanelFont;
        ThemeRenderer.FontName = _panelFont;

        _panelInk = _settings.PanelInk == "light" ? "" : _settings.PanelInk ?? "";
        _background = _settings.Background is >= 20 and <= 100 ? _settings.Background : 50;
        ThemeRenderer.BackgroundBrightness = _background / 100.0;

        _widgetBrightness = _settings.WidgetBrightness is >= 50 and <= 200 ? _settings.WidgetBrightness : 100;
        ThemeRenderer.WidgetBrightness = _widgetBrightness / 100.0;

        foreach (var pair in _settings.Transforms)
        {
            if (Enum.TryParse<ThemeKind>(pair.Key, true, out var kind) && pair.Value is not null)
                _transforms[kind] = pair.Value.Clone();
        }

        SetAccent(ThemePreset.ToColor(_settings.Accent));
        ApplyInk();

        SelectTheme(ParseTheme(_settings.Theme), apply: false);



        if (_settings.Fps is >= 1 and <= 30)
            FpsSlider.Value = _settings.Fps;

        if (_settings.MediaPath is { Length: > 0 } savedMedia && File.Exists(savedMedia))
            ApplyMedia(savedMedia, switchTheme: false);

        if (_settings.Brightness is >= 10 and <= 100)
            BrightnessSlider.Value = _settings.Brightness;


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

        FillFonts();
        UpdateInkButtons();
        if (BackgroundSlider is not null) BackgroundSlider.Value = _background;
        if (TextBrightnessSlider is not null) TextBrightnessSlider.Value = _widgetBrightness;
        ApplyViewTransform();

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
            UpdateAutoOutputButtons();
            RefreshRecent();
            RenderFrame();

            // Автозапуск: окно не нужно, экран СЖО работает из трея.
            if (Startup.IsAutoStart) HideToTray();
        };

        // Окно поменяло размер — плитки пересчитались, стекло встаёт по месту без анимации.
        SizeChanged += (_, _) =>
        {
        };

        // --auto (ярлык), --autostart (Windows) или включённый «АВТОВЫВОД».
        if (Startup.IsAutoStart
            || _settings.AutoOutput
            || args.Any(a => a.Equals("--auto", StringComparison.OrdinalIgnoreCase)))
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

        UpdatePaletteButtons();

        // Программные подложки LiquidSegments не всегда ловят смену палитры сами.
        // Обновляем их после того, как выставлены IsChecked, иначе стекло
        // встанет на старый вариант.
        PaletteSegments?.Refresh();
        AutoSegments?.Refresh();
        AutoOutputSegments?.Refresh();
        TabSegments?.Refresh();
        LangSegments?.Refresh();

        UpdateTitleBar();
    }

    private void OnPaletteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton option) return;

        var family = option.Tag as string ?? "cream";
        var variant = Skins.Variant(_skin);

        ApplySkin(Skins.Combine(family, variant));
        option.IsChecked = true;
        SaveSettings();
    }

    /// <summary>Значок палитры в шапке открывает выбор палитры окна.</summary>
    private void OnThemePaletteClick(object sender, RoutedEventArgs e)
    {
        if (ThemePalettePopup is null) return;

        UpdatePaletteButtons();
        ThemePalettePopup.IsOpen = true;
    }

    /// <summary>Чёрно-белый кружок рядом: светлый и тёмный вариант одной палитры.</summary>
    private void OnThemeModeClick(object sender, RoutedEventArgs e)
    {
        var variant = Skins.Variant(_skin) == Skins.Dark ? Skins.Light : Skins.Dark;

        ApplySkin(Skins.Combine(Skins.Family(_skin), variant));
        SaveSettings();
    }

    private void UpdatePaletteButtons()
    {
        if (PaletteCream is null || PaletteOcean is null) return;

        var family = Skins.Family(_skin);

        PaletteCream.IsChecked = family == "cream";
        PaletteOcean.IsChecked = family == "ocean";
        PaletteSakura.IsChecked = family == "sakura";
        PaletteForest.IsChecked = family == "forest";
    }

    private void UpdateTitleBar()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        var caption = ToColorRef(Skins.ColorOf("Paper"));
        var text = ToColorRef(Skins.ColorOf("Ink"));
        var dark = Skins.IsDark(_skin) ? 1 : 0;

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

    // ── Вид «Монитора»: четырнадцать вариантов одной темы ──────────────────

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

    // ── Автовывод при запуске ───────────────────────────────────────────────

    private void UpdateAutoOutputButtons()
    {
        if (AutoOutputOff is null || AutoOutputOn is null) return;

        AutoOutputOff.IsChecked = !_settings.AutoOutput;
        AutoOutputOn.IsChecked = _settings.AutoOutput;
    }

    private void OnAutoOutputClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton option) return;

        _settings.AutoOutput = (option.Tag as string) == "on";
        UpdateAutoOutputButtons();
        SaveSettings();
        SetLog("log.autoOutput", Loc.T(_settings.AutoOutput ? "startup.on" : "startup.off"));
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
        AnimateLabelColumn(AutoLabelColumn, targetLabelWidth, duration, easing);
        AnimateLabelColumn(AutoOutputLabelColumn, targetLabelWidth, duration, easing);
        AnimateLabelColumn(LabelCaptionColumn, targetLabelWidth, duration, easing);
        AnimateLabelColumn(TextLabelColumn, targetLabelWidth, duration, easing);
        AnimateLabelColumn(FontLabelColumn, targetLabelWidth, duration, easing);
        AnimateLabelColumn(InkLabelColumn, targetLabelWidth, duration, easing);
        AnimateLabelColumn(BackgroundLabelColumn, targetLabelWidth, duration, easing);
        AnimateLabelColumn(TextBrightnessLabelColumn, targetLabelWidth, duration, easing);
        AnimateLabelColumn(ScaleLabelColumn, targetLabelWidth, duration, easing);
        AnimateLabelColumn(LabelAlignCaptionColumn, targetLabelWidth, duration, easing);

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
        if (AutoLabelColumn is not null) AutoLabelColumn.Width = value;
        if (AutoOutputLabelColumn is not null) AutoOutputLabelColumn.Width = value;
        if (LabelCaptionColumn is not null) LabelCaptionColumn.Width = value;
        if (TextLabelColumn is not null) TextLabelColumn.Width = value;
        if (FontLabelColumn is not null) FontLabelColumn.Width = value;
        if (InkLabelColumn is not null) InkLabelColumn.Width = value;
        if (BackgroundLabelColumn is not null) BackgroundLabelColumn.Width = value;
        if (TextBrightnessLabelColumn is not null) TextBrightnessLabelColumn.Width = value;
        if (ScaleLabelColumn is not null) ScaleLabelColumn.Width = value;
        if (LabelAlignCaptionColumn is not null) LabelAlignCaptionColumn.Width = value;
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
        RowAutoStartLabel, RowAutoOutputLabel,
        SectionCustom, RowLabelCaption, RowTextLabel,
        RowFontLabel, RowInkLabel, RowBackgroundLabel, RowTextBrightnessLabel, RowScaleLabel,
        RowLabelAlign,
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

        ShowTab(tab.Tag as string ?? "settings");
    }

    private void ShowTab(string tab)
    {
        var settings = tab == "settings";
        var startup = tab == "startup";
        var presets = tab == "presets";

        SettingsPanel.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        StartupPanel.Visibility = startup ? Visibility.Visible : Visibility.Collapsed;
        PresetsPanel.Visibility = presets ? Visibility.Visible : Visibility.Collapsed;

        TabSettings.IsChecked = settings;
        TabStartup.IsChecked = startup;
        TabPresets.IsChecked = presets;

        // Миниатюры рисуем при первом показе вкладки: к этому моменту датчики
        // уже прочитаны, и кадр в списке совпадает с тем, что видно на панели.
        if (presets) _ = RefreshPresetPreviewsAsync();
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

    /// <summary>Что рисовать: тема панели как есть.</summary>
    private ThemeKind RenderKind() => _theme;

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
        _theme = kind;

        BindWidgets();
        ApplyViewTransform();
        ApplyBackdrop();
        UpdateMediaSection();

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
    // ── Жидкое стекло под плитками тем ──────────────────────────────────────

    /// <summary>
    /// Ставит стеклянную заливку под выбранную плитку. При переключении она
    /// переезжает с вытягиванием по ходу движения — переход читается как жидкость,
    /// а не как ползунок.
    /// </summary>
    // ── Оформление ──────────────────────────────────────────────────────────

    /// <summary>Акцент приходит из сохранённой темы; отдельного выбора в окне нет.</summary>
    private void SetAccent(Color color)
    {
        _accent = color;
        ThemeRenderer.InvalidateCaption();
        _frameDirty = true;
    }

    // ── Вернуть всё к исходному виду ───────────────────────────────────────

    /// <summary>Возврат к заводскому кадру: шрифт, цвет, фон, акцент, размеры и объекты.</summary>
    private void OnRestoreDefaultsClick(object sender, RoutedEventArgs e)
    {
        _panelFont = "Segoe UI";
        ThemeRenderer.FontName = _panelFont;

        _panelInk = "";
        SetAccent(ThemeRenderer.DefaultAccent);
        ApplyInk();

        _background = 50;
        ThemeRenderer.BackgroundBrightness = 0.5;

        _widgetBrightness = 100;
        ThemeRenderer.WidgetBrightness = 1;

        _transforms.Clear();
        _selection.Clear();

        // Раскладка — стартовые объекты во всех видах.
        _layouts.Clear();
        _widgets = LayoutFor(RenderKind());

        FillFonts();
        UpdateInkButtons();

        if (BackgroundSlider is not null) BackgroundSlider.Value = _background;
        if (TextBrightnessSlider is not null) TextBrightnessSlider.Value = _widgetBrightness;

        ApplyViewTransform();
        UpdateWidgetSelection();

        _frameDirty = true;
        SaveSettings();

        if (IsLoaded) RenderFrame();
    }

    // ── Своя раскладка: объекты на панели ──────────────────────────────────

    /// <summary>Потоку отрисовки отдаётся снимок: он не должен видеть половину перетаскивания.</summary>
    private void PublishWidgets() =>
        ThemeRenderer.Widgets = _widgets.Select(widget => widget.Clone()).ToArray();

    private void OnAddWidgetClick(object sender, RoutedEventArgs e)
    {
        if (AddMenuPopup is null) return;

        // Меню появляется по центру окна, чуть выше середины.
        ResetAddMenuGlass();

        AddMenuPopup.PlacementTarget = this;
        AddMenuPopup.Placement = PlacementMode.Center;
        AddMenuPopup.HorizontalOffset = 0;
        AddMenuPopup.VerticalOffset = -110;
        AddMenuPopup.IsOpen = true;
    }

    /// <summary>Стекло в меню добавления переезжает к пункту под курсором.</summary>
    private void OnAddMenuHover(object sender, MouseEventArgs e)
    {
        if (sender is Button item && item.ActualHeight > 0)
            MoveAddMenuGlass(item);
    }

    /// <summary>Курсор ушёл с меню — стекло растворяется, а не висит на старом пункте.</summary>
    private void OnAddMenuLeave(object sender, MouseEventArgs e)
    {
        _glassItem = null;

        if (AddMenuGlass is null) return;

        var fade = new DoubleAnimation(AddMenuGlass.Opacity, 0, TimeSpan.FromMilliseconds(120));

        fade.Completed += (_, _) =>
        {
            if (_glassItem is null) AddMenuGlass.Visibility = Visibility.Collapsed;
        };

        AddMenuGlass.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>Ставит стекло на пункт и даёт ему «каплю» — лёгкое вытягивание по ходу движения.</summary>
    private void MoveAddMenuGlass(Button item)
    {
        if (AddMenuGlass is null || AddMenuGlassLayer is null) return;
        if (ReferenceEquals(item, _glassItem)) return;

        _glassItem = item;

        var point = item.TranslatePoint(new Point(0, 0), AddMenuGlassLayer);

        // Небольшой отступ, чтобы свечение не обрезалось краем списка.
        const double inset = 1;
        var left = point.X + inset;
        var top = point.Y + inset;
        var width = Math.Max(4, item.ActualWidth - inset * 2);
        var height = Math.Max(4, item.ActualHeight - inset * 2);

        var first = AddMenuGlass.Visibility != Visibility.Visible;

        AddMenuGlass.Width = width;
        AddMenuGlass.Height = height;
        AddMenuGlass.Visibility = Visibility.Visible;

        if (first)
        {
            Canvas.SetLeft(AddMenuGlass, left);
            Canvas.SetTop(AddMenuGlass, top);

            AddMenuGlass.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));

            PulseAddMenuGlass();
            return;
        }

        var fromLeft = Canvas.GetLeft(AddMenuGlass);
        var fromTop = Canvas.GetTop(AddMenuGlass);

        if (double.IsNaN(fromLeft)) fromLeft = left;
        if (double.IsNaN(fromTop)) fromTop = top;

        var duration = TimeSpan.FromMilliseconds(190);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        AddMenuGlass.BeginAnimation(Canvas.LeftProperty,
            new DoubleAnimation(fromLeft, left, duration) { EasingFunction = easing });

        AddMenuGlass.BeginAnimation(Canvas.TopProperty,
            new DoubleAnimation(fromTop, top, duration) { EasingFunction = easing });

        // Прозрачность могла остаться на нуле после «растворения».
        AddMenuGlass.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(AddMenuGlass.Opacity, 1, TimeSpan.FromMilliseconds(90)));

        PulseAddMenuGlass();
    }

    /// <summary>Растяжение по вертикали при переезде: капля тянется и собирается обратно.</summary>
    private void PulseAddMenuGlass()
    {
        if (AddMenuGlassScale is null) return;

        var pulse = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(300)
        };

        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.16,
            KeyTime.FromPercent(0.3), new CubicEase { EasingMode = EasingMode.EaseOut }));

        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(0.97, KeyTime.FromPercent(0.72)));

        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0)));

        AddMenuGlassScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
    }

    /// <summary>Сбрасывает стекло: каждое открытие меню начинается с чистого листа.</summary>
    private void ResetAddMenuGlass()
    {
        _glassItem = null;

        if (AddMenuGlass is null) return;

        AddMenuGlass.BeginAnimation(UIElement.OpacityProperty, null);
        AddMenuGlass.BeginAnimation(Canvas.LeftProperty, null);
        AddMenuGlass.BeginAnimation(Canvas.TopProperty, null);

        AddMenuGlass.Opacity = 0;
        AddMenuGlass.Visibility = Visibility.Collapsed;

        if (AddMenuGlassScale is null) return;

        AddMenuGlassScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        AddMenuGlassScale.ScaleY = 1;

        // Свечение подстраивается под палитру окна.
        if (AddMenuGlassGlow is not null && TryFindResource("Rose") is SolidColorBrush rose)
            AddMenuGlassGlow.Color = rose.Color;
    }

    private void OnAddWidgetType(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement item) return;

        AddMenuPopup.IsOpen = false;
        AddWidget(item.Tag as string ?? "cpu");
    }

    /// <summary>Показывает поля ввода подписи и текста для выделенного объекта.</summary>
    private void UpdateTextRow()
    {
        if (TextRow is null) return;

        var single = _selection.Count == 1 ? _selection[0] : null;
        var text = single?.Type == "text" ? single : null;
        var labelled = single is not null && single.Type is not ("text" or "ring") ? single : null;

        TextRow.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        if (LabelRow is not null)
            LabelRow.Visibility = labelled is null ? Visibility.Collapsed : Visibility.Visible;
        if (LabelAlignRow is not null)
            LabelAlignRow.Visibility = labelled is null ? Visibility.Collapsed : Visibility.Visible;

        if (labelled is not null) SyncLabelAlign(labelled.LabelAlign);

        if (text is not null)
        {
            _syncingText = true;
            WidgetTextBox.Text = text.Text ?? "";
            _syncingText = false;
        }

        if (labelled is not null && WidgetLabelBox is not null)
        {
            _syncingText = true;
            WidgetLabelBox.Text = labelled.Label ?? ThemeRenderer.LabelFor(_snapshot, labelled.Type);
            _syncingText = false;
        }
    }

    /// <summary>Положение подписи: слева, по центру числа или по его правому краю.</summary>
    private void OnLabelAlignClick(object sender, RoutedEventArgs e)
    {
        if (_loading || _selection.Count == 0) return;

        var align = (sender as FrameworkElement)?.Tag as string ?? "left";

        foreach (var widget in _selection)
            widget.LabelAlign = align;

        SyncLabelAlign(align);

        PublishWidgets();
        _frameDirty = true;
        SaveSettingsSoon();

        if (IsLoaded) RenderFrame();
    }

    /// <summary>Показывает в переключателе текущее положение подписи.</summary>
    private void SyncLabelAlign(string? align)
    {
        if (LabelAlignLeft is null) return;

        var value = align is "center" or "right" ? align : "left";

        LabelAlignLeft.IsChecked = value == "left";
        LabelAlignCenter.IsChecked = value == "center";
        LabelAlignRight.IsChecked = value == "right";
    }

    private void OnWidgetLabelChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingText || _selection.Count != 1) return;

        var widget = _selection[0];
        if (widget.Type is "text" or "ring") return;

        var text = WidgetLabelBox.Text.Trim();

        // Пустая строка — подпись убрать совсем, а не вернуть стандартную.
        widget.Label = text;
        PublishWidgets();
        _frameDirty = true;
        SaveSettingsSoon();

        if (IsLoaded) RenderFrame();
    }

    private void OnWidgetTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingText || _selection.Count != 1 || _selection[0].Type != "text") return;

        _selection[0].Text = WidgetTextBox.Text;
        PublishWidgets();
        _frameDirty = true;
        SaveSettingsSoon();

        if (IsLoaded) RenderFrame();
    }

    private void AddWidget(string type)
    {
        // Объекты живут в «Мониторе»; если открыта другая тема — переключаемся на него.
        if (!PanelLayouts.IsObjectView(RenderKind()))
            SelectTheme(ThemeKind.Monitor, apply: false);

        var index = _widgets.Count;
        var widget = new PanelWidget
        {
            Type = type,
            Text = type == "text" ? "Текст" : null,
            X = Math.Clamp(24 + index * 10, 0, ThemeRenderer.Width - ThemeRenderer.WidgetWidth),
            Y = Math.Clamp(44 + index * 12, 0, ThemeRenderer.Height - ThemeRenderer.WidgetHeight)
        };

        _widgets.Add(widget);

        _selection.Clear();
        _selection.Add(widget);

        PublishWidgets();
        UpdateWidgetSelection();
        _frameDirty = true;
        SaveSettings();

        if (IsLoaded) RenderFrame();

        if (type == "text" && WidgetTextBox is not null)
        {
            WidgetTextBox.Focus();
            WidgetTextBox.SelectAll();
        }
    }

    private void OnRemoveWidgetClick(object sender, RoutedEventArgs e) => RemoveSelectedWidget();

    private void RemoveSelectedWidget()
    {
        // Ничего не выделено — удаляем последний объект, чтобы кнопка работала.
        var targets = _selection.Count > 0 ? _selection.ToList() : new List<PanelWidget>();

        if (targets.Count == 0 && _widgets.LastOrDefault() is { } last)
            targets.Add(last);

        if (targets.Count == 0) return;

        foreach (var target in targets)
            _widgets.Remove(target);

        _selection.Clear();

        PublishWidgets();
        UpdateWidgetSelection();
        _frameDirty = true;
        SaveSettings();

        if (IsLoaded) RenderFrame();
    }

    /// <summary>Рамки выделения поверх превью и переключение доступных настроек.</summary>
    private void UpdateWidgetSelection()
    {
        if (WidgetOverlay is null) return;

        // Мусорные выделения (объект удалили, вид сменили) не держим.
        _selection.RemoveAll(widget => !_widgets.Contains(widget));

        // Старые рамки убираем, рамка-выделение остаётся первой.
        for (var i = WidgetOverlay.Children.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(WidgetOverlay.Children[i], MarqueeRect))
                WidgetOverlay.Children.RemoveAt(i);
        }

        var isCustom = PanelLayouts.IsObjectView(RenderKind());

        if (isCustom)
        {
            foreach (var widget in _selection)
            {
                var rect = ThemeRenderer.WidgetBounds(widget, _snapshot);

                var box = new Border
                {
                    Width = rect.Width + 6,
                    Height = rect.Height + 6,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    BorderBrush = (Brush)FindResource("Rose"),
                    IsHitTestVisible = false
                };

                Canvas.SetLeft(box, rect.X - 3);
                Canvas.SetTop(box, rect.Y - 3);
                WidgetOverlay.Children.Add(box);
            }
        }

        var canEdit = isCustom && _selection.Count > 0;

        // Без выделения цвет и шрифт не меняются вовсе.
        foreach (var dot in new[] { InkDefault, InkGrey, InkAccentDot, InkCustomDot })
        {
            if (dot is null) continue;
            dot.IsEnabled = canEdit;
            dot.Opacity = canEdit ? 1.0 : 0.4;
        }

        if (FontPicker is not null)
        {
            FontPicker.IsEnabled = canEdit;
            FontPicker.Opacity = canEdit ? 1.0 : 0.4;
        }





        ApplyViewTransform();
        SyncFontPicker();
        UpdateInkButtons();
        UpdateTextRow();
    }


    /// <summary>Раскладка вида: сохранённая или стартовая.</summary>
    private List<PanelWidget> LayoutFor(ThemeKind kind)
    {
        if (_layouts.TryGetValue(kind, out var saved)) return saved;

        var created = PanelLayouts.Defaults(kind);
        _layouts[kind] = created;
        return created;
    }

    /// <summary>Подхватывает раскладку текущего вида.</summary>
    private void BindWidgets()
    {
        var kind = RenderKind();

        _widgets = PanelLayouts.IsObjectView(kind) ? LayoutFor(kind) : new List<PanelWidget>();
        _selection.Clear();

        PublishWidgets();
        UpdateWidgetSelection();
    }
    // Перетаскивание: координаты мыши приходят в пикселях кадра 320×240,
    // потому что картинка живёт в своей сетке и растягивается вьюбоксом.
    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Клавиатурный фокус — на превью, чтобы Delete удалял выделенные объекты.
        Preview.Focus();

        var point = e.GetPosition(Preview);

        if (!PanelLayouts.IsObjectView(RenderKind()))
        {
            // В готовых видах тащим всё содержимое кадра: рамка остаётся на месте.
            _movingView = true;
            _moveStart = point;
            _moveFromX = CurrentTransform().X;
            _moveFromY = CurrentTransform().Y;
            Preview.CaptureMouse();
            return;
        }

        // Клик по объекту: если он уже выделен — тащим всё выделение, иначе только его.
        var hit = _widgets.LastOrDefault(widget => ThemeRenderer.WidgetBounds(widget, _snapshot).Contains(point));

        if (hit is null)
        {
            // Пустое место: начинаем рамку выделения.
            _selection.Clear();
            _marquee = true;
            _marqueeStart = point;
            ShowMarquee(point, point);
            Preview.CaptureMouse();
            UpdateWidgetSelection();
            return;
        }

        if (!_selection.Contains(hit))
        {
            _selection.Clear();
            _selection.Add(hit);
        }

        _dragOrigins.Clear();
        foreach (var widget in _selection)
            _dragOrigins[widget] = new Point(widget.X, widget.Y);

        _dragStart = point;
        _dragging = true;
        Preview.CaptureMouse();

        if (GridOverlay is not null) GridOverlay.Visibility = Visibility.Visible;

        UpdateWidgetSelection();
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _movingView = false;
            _dragging = false;
            _marquee = false;
            HideMarquee();
            return;
        }

        if (_movingView)
        {
            var point = e.GetPosition(Preview);
            var transform = CurrentTransform();

            transform.X = _moveFromX + (point.X - _moveStart.X);
            transform.Y = _moveFromY + (point.Y - _moveStart.Y);
            StoreTransform(transform);
            return;
        }

        if (_marquee)
        {
            ShowMarquee(_marqueeStart, e.GetPosition(Preview));
            return;
        }

        if (!_dragging || _selection.Count == 0) return;

        var position = e.GetPosition(Preview);
        var dx = position.X - _dragStart.X;
        var dy = position.Y - _dragStart.Y;

        // Двигаем всё выделение одним куском, не выпуская за кадр.
        foreach (var widget in _selection)
        {
            if (!_dragOrigins.TryGetValue(widget, out var origin)) continue;

            var box = ThemeRenderer.WidgetBounds(widget, _snapshot);
            var offset = ThemeRenderer.LabelOffset(widget);

            // Притягиваем к сетке 16 px — так объекты встают ровно.
            var x = Snap(origin.X + dx);
            var y = Snap(origin.Y + dy);

            widget.X = Math.Clamp(x, 0, Math.Max(0, ThemeRenderer.Width - box.Width));
            widget.Y = Math.Clamp(y, -offset, Math.Max(-offset, ThemeRenderer.Height - box.Height));
        }

        PublishWidgets();
        UpdateWidgetSelection();
        _frameDirty = true;

        if (IsLoaded) RenderFrame();
    }

    private const double GridStep = 8;

    private static double Snap(double value) => Math.Round(value / GridStep) * GridStep;

    /// <summary>Рамка выделения: тянется по пустому месту и обводит объекты.</summary>
    private void ShowMarquee(Point from, Point to)
    {
        if (MarqueeRect is null) return;

        var x = Math.Min(from.X, to.X);
        var y = Math.Min(from.Y, to.Y);
        var width = Math.Abs(to.X - from.X);
        var height = Math.Abs(to.Y - from.Y);

        MarqueeRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(MarqueeRect, x);
        Canvas.SetTop(MarqueeRect, y);
        MarqueeRect.Width = width;
        MarqueeRect.Height = height;
    }

    private void HideMarquee()
    {
        if (MarqueeRect is not null) MarqueeRect.Visibility = Visibility.Collapsed;
    }

    /// <summary>Завершает рамку: в выделение попадают все пересечённые объекты.</summary>
    private void FinishMarquee()
    {
        if (MarqueeRect is null) return;

        var rect = new Rect(Canvas.GetLeft(MarqueeRect), Canvas.GetTop(MarqueeRect),
            Math.Max(0, MarqueeRect.Width), Math.Max(0, MarqueeRect.Height));

        _selection.Clear();

        foreach (var widget in _widgets)
        {
            if (ThemeRenderer.WidgetBounds(widget, _snapshot).IntersectsWith(rect))
                _selection.Add(widget);
        }

        HideMarquee();
        UpdateWidgetSelection();
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e) => EndDrag();

    private void OnPreviewMouseLeave(object sender, MouseEventArgs e) => EndDrag();

    /// <summary>Отпускание мыши где угодно завершает и перетаскивание, и рамку.</summary>
    protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseUp(e);

        if (_marquee || _dragging || _movingView) EndDrag();
    }

    /// <summary>Окно потеряло фокус — рамка выделения не должна висеть.</summary>
    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);

        if (_marquee)
        {
            _marquee = false;
            HideMarquee();
            Preview.ReleaseMouseCapture();
        }
    }

    /// <summary>Delete убирает выделенные объекты.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (e.Key != Key.Delete || _selection.Count == 0) return;

        // В полях ввода Delete должен стирать текст, а не объекты.
        if (Keyboard.FocusedElement is TextBox or ComboBox) return;

        RemoveSelectedWidget();
        e.Handled = true;
    }

    private void EndDrag()
    {
        if (_marquee)
        {
            _marquee = false;
            FinishMarquee();
            Preview.ReleaseMouseCapture();
            return;
        }

        if (!_dragging && !_movingView) return;

        _dragging = false;
        _movingView = false;
        Preview.ReleaseMouseCapture();

        if (GridOverlay is not null) GridOverlay.Visibility = Visibility.Collapsed;

        SaveSettings();
    }

    // ── Оформление кадра: шрифт, цвет, фон, сдвиг вида ─────────────────────

    private void FillFonts()
    {
        if (FontPicker is null) return;

        var names = Fonts.SystemFontFamilies
            .Select(family => family.Source)
            .Where(name => !SymbolFonts.Contains(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        FontPicker.ItemsSource = names;
        FontPicker.SelectedItem =
            names.FirstOrDefault(n => string.Equals(n, _panelFont, StringComparison.OrdinalIgnoreCase))
            ?? names.FirstOrDefault();
    }

    /// <summary>
    /// Шрифты-пиктограммы: в списке они выглядят набором значков и для текста
    /// кадра бесполезны, поэтому в выборе их нет.
    /// </summary>
    private static readonly HashSet<string> SymbolFonts = new(StringComparer.OrdinalIgnoreCase)
    {
        "Wingdings",
        "Wingdings 2",
        "Wingdings 3",
        "Webdings",
        "Symbol",
        "Marlett",
        "Bookshelf Symbol 7",
        "MS Reference Specialty",
        "Segoe MDL2 Assets",
        "Segoe Fluent Icons",
        "Segoe UI Emoji",
        "Segoe UI Historic",
        "Segoe UI Symbol"
    };

    /// <summary>Цвет текста кадра: тушь темы, серый или акцент.</summary>
    private void ApplyInk()
    {
        ThemeRenderer.InkColor = _panelInk switch
        {
            "" => null,
            "grey" => Color.FromRgb(0x9A, 0xA0, 0xA6),
            "accent" => _accent,
            _ => ThemePreset.ToColor(_panelInk)
        };

        if (InkAccentDot is not null)
            InkAccentDot.Background = new SolidColorBrush(_accent);

        // Кружок «тушь темы» показывает настоящий цвет туши выбранной панели.
        if (InkDefault is not null)
        {
            InkDefault.Background = new SolidColorBrush(_panelDark
                ? Color.FromRgb(0xED, 0xEA, 0xE3)
                : Color.FromRgb(0x10, 0x10, 0x12));
        }

        _frameDirty = true;
    }

    private void UpdateInkButtons()
    {
        if (InkDefault is null) return;

        // Показываем цвет первого выделенного объекта; без выделения — ничего не выбрано.
        var ink = _selection.FirstOrDefault()?.Ink;

        var mode = !string.IsNullOrWhiteSpace(ink) ? ColourMode(ink) : "";

        InkDefault.IsChecked = _selection.Count > 0 && mode.Length == 0;
        InkGrey.IsChecked = _selection.Count > 0 && mode == "grey";
        InkAccentDot.IsChecked = _selection.Count > 0 && mode == "accent";
        InkCustomDot.IsChecked = _selection.Count > 0 && mode.StartsWith("#", StringComparison.Ordinal);
    }

    /// <summary>Сводит цвет к режиму строки «Цвет»: тушь, серый, акцент или свой.</summary>
    private string ColourMode(string hex)
    {
        if (string.Equals(hex, "#9AA0A6", StringComparison.OrdinalIgnoreCase)) return "grey";
        if (string.Equals(hex, ThemePreset.ToHex(_accent), StringComparison.OrdinalIgnoreCase)) return "accent";

        return hex;
    }

    /// <summary>Наведение на шрифт в списке — живой пример на выделенных объектах.</summary>
    private void OnFontItemHover(object sender, MouseEventArgs e)
    {
        if (sender is not ComboBoxItem item || item.DataContext is not string family) return;

        PreviewFontOnSelection(family);
    }

    /// <summary>То же, но по движению мыши: пункты списка живут в отдельном окне попапа.</summary>
    private void OnFontPreviewMove(object sender, MouseEventArgs e)
    {
        if (!FontPicker.IsDropDownOpen) return;

        var item = FindAncestorItem(e.OriginalSource as DependencyObject);

        if (item?.DataContext is string family)
            PreviewFontOnSelection(family);
    }

    private void OnFontDropDownOpened(object? sender, EventArgs e)
    {
        // Открыли список заново — прошлый «пример» уже откатили.
        _fontPreview.Clear();
    }

    private static ComboBoxItem? FindAncestorItem(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is ComboBoxItem item) return item;

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return null;
    }

    /// <summary>Показывает шрифт на выделенных объектах, не сохраняя выбор.</summary>
    private void PreviewFontOnSelection(string family)
    {
        if (_selection.Count == 0) return;

        if (_fontPreview.Count == 0)
        {
            foreach (var widget in _selection)
                _fontPreview[widget] = widget.Font;
        }

        var changed = false;

        foreach (var widget in _selection)
        {
            if (widget.Font == family) continue;
            widget.Font = family;
            changed = true;
        }

        if (!changed) return;

        PublishWidgets();
        _frameDirty = true;

        if (IsLoaded) RenderFrame();
    }

    /// <summary>Список закрыли, ничего не выбрав — возвращаем прежние шрифты.</summary>
    private void OnFontDropDownClosed(object? sender, EventArgs e)
    {
        if (_fontPreview.Count == 0) return;

        foreach (var pair in _fontPreview)
            pair.Key.Font = pair.Value;

        _fontPreview.Clear();

        PublishWidgets();
        _frameDirty = true;
        SyncFontPicker();

        if (IsLoaded) RenderFrame();
    }

    private void OnFontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _syncingFont || FontPicker.SelectedItem is not string family) return;

        // Шрифт меняется только у выделенных объектов; без выделения ползунок неактивен.
        if (_selection.Count == 0) return;

        _fontPreview.Clear();

        foreach (var widget in _selection)
            widget.Font = family;

        CommitWidgets();
    }

    /// <summary>Ползунок шрифта показывает шрифт выбранного объекта или кадра.</summary>
    private void SyncFontPicker()
    {
        if (FontPicker?.ItemsSource is not IEnumerable<string> names) return;

        var family = _selection.FirstOrDefault()?.Font ?? _panelFont;
        var match = names.FirstOrDefault(n => string.Equals(n, family, StringComparison.OrdinalIgnoreCase));

        _syncingFont = true;
        FontPicker.SelectedItem = match;
        _syncingFont = false;
    }

    private void OnInkClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton dot) return;
        if (_selection.Count == 0) return;

        var mode = dot.Tag as string ?? "";

        var ink = mode switch
        {
            "grey" => "#9AA0A6",
            "accent" => ThemePreset.ToHex(_accent),
            _ => null
        };

        foreach (var widget in _selection)
            widget.Ink = ink;

        PublishWidgets();
        UpdateWidgetSelection();
        _frameDirty = true;
        SaveSettings();

        if (IsLoaded) RenderFrame();
        UpdateInkButtons();
    }

    /// <summary>Радужный кружок: любой цвет через системную палитру.</summary>
    /// <summary>Радужный кружок: открывает свою палитру.</summary>
    private void OnInkPick(object sender, RoutedEventArgs e) => OpenColorPicker();

    /// <summary>
    /// Своя палитра: слева квадрат «насыщенность × яркость» (сверху белое,
    /// снизу чёрное), справа вертикальная линейка оттенков.
    /// </summary>
    private void OpenColorPicker()
    {
        if (_selection.Count == 0) return;

        var start = _selection.FirstOrDefault()?.Ink is { Length: > 0 } own
            ? ThemePreset.ToColor(own)
            : _panelInk switch
            {
                "grey" => Color.FromRgb(0x9A, 0xA0, 0xA6),
                "accent" => _accent,
                _ => _accent
            };

        var (h, s, v) = ToHsv(start);

        _hue = h;
        _saturation = s;
        _value = v;

        UpdateColorPicker();
        ColorPopup.IsOpen = true;
    }

    /// <summary>Перекрашивает квадрат под текущий оттенок и ставит кружок на место.</summary>
    private void UpdateColorPicker()
    {
        if (ColorHueBase is null || ColorSquare is null) return;

        var hueColor = FromHsv(_hue, 1, 1);

        // Квадрат: слева белое, справа чистый оттенок, снизу чёрное.
        ColorHueBase.Fill = new LinearGradientBrush(Colors.White, hueColor, 0);

        Canvas.SetLeft(ColorCursor, _saturation * ColorSquare.ActualWidth - 6);
        Canvas.SetTop(ColorCursor, (1 - _value) * ColorSquare.ActualHeight - 6);

        if (HueCursor is not null && HueStrip is not null)
            Canvas.SetTop(HueCursor, _hue / 360 * HueStrip.ActualHeight - 2);

        ColorPreview.Fill = new SolidColorBrush(FromHsv(_hue, _saturation, _value));
    }

    private void OnColorSquareDown(object sender, MouseButtonEventArgs e)
    {
        _pickingSquare = true;
        ColorSquare.CaptureMouse();
        PickFromSquare(e);
    }

    private void OnColorSquareMove(object sender, MouseEventArgs e)
    {
        if (_pickingSquare) PickFromSquare(e);
    }

    private void OnColorSquareUp(object sender, MouseButtonEventArgs e)
    {
        _pickingSquare = false;
        ColorSquare.ReleaseMouseCapture();
    }

    private void PickFromSquare(MouseEventArgs e)
    {
        var point = e.GetPosition(ColorSquare);
        var width = Math.Max(1, ColorSquare.ActualWidth);
        var height = Math.Max(1, ColorSquare.ActualHeight);

        _saturation = Math.Clamp(point.X / width, 0, 1);
        _value = 1 - Math.Clamp(point.Y / height, 0, 1);

        UpdateColorPicker();
        ApplyPickedColor();
    }

    private void OnHueDown(object sender, MouseButtonEventArgs e)
    {
        _pickingHue = true;
        HueStrip.CaptureMouse();
        PickFromHue(e);
    }

    private void OnHueMove(object sender, MouseEventArgs e)
    {
        if (_pickingHue) PickFromHue(e);
    }

    private void OnHueUp(object sender, MouseButtonEventArgs e)
    {
        _pickingHue = false;
        HueStrip.ReleaseMouseCapture();
    }

    private void PickFromHue(MouseEventArgs e)
    {
        var point = e.GetPosition(HueStrip);
        var height = Math.Max(1, HueStrip.ActualHeight);

        _hue = Math.Clamp(point.Y / height, 0, 0.999) * 360;

        UpdateColorPicker();
        ApplyPickedColor();
    }

    /// <summary>Применяет выбранный цвет выделенным объектам.</summary>
    private void ApplyPickedColor()
    {
        if (_selection.Count == 0) return;

        var color = FromHsv(_hue, _saturation, _value);
        var hex = ThemePreset.ToHex(color);

        foreach (var widget in _selection)
            widget.Ink = hex;

        PublishWidgets();
        UpdateWidgetSelection();
        _frameDirty = true;
        SaveSettings();

        if (IsLoaded) RenderFrame();
        UpdateInkButtons();
    }

    private static (double H, double S, double V) ToHsv(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        double hue = 0;

        if (delta > 0)
        {
            if (max == r) hue = 60 * (((g - b) / delta + 6) % 6);
            else if (max == g) hue = 60 * ((b - r) / delta + 2);
            else hue = 60 * ((r - g) / delta + 4);
        }

        return (hue, max <= 0 ? 0 : delta / max, max);
    }

    private static Color FromHsv(double hue, double saturation, double value)
    {
        var h = ((hue % 360) + 360) % 360;
        var c = value * saturation;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = value - c;

        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    /// <summary>Сохраняет изменения объекта и перерисовывает кадр.</summary>
    private void CommitWidgets()    {
        PublishWidgets();
        UpdateWidgetSelection();
        _frameDirty = true;
        SaveSettings();

        if (IsLoaded) RenderFrame();
    }

    private void OnBackgroundChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;

        _background = (int)Math.Round(e.NewValue);
        ThemeRenderer.BackgroundBrightness = _background / 100.0;
        _frameDirty = true;
        SaveSettingsSoon();

        if (IsLoaded) RenderFrame();
    }

    private void OnTextBrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;

        _widgetBrightness = (int)Math.Round(e.NewValue);
        ThemeRenderer.WidgetBrightness = _widgetBrightness / 100.0;
        _frameDirty = true;
        SaveSettingsSoon();

        if (IsLoaded) RenderFrame();
    }

    private void OnScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)    {
        if (_loading || _syncingScale) return;

        // В «Своём» масштаб относится к выделенным объектам, в остальных видах — ко всему кадру.
        if (PanelLayouts.IsObjectView(RenderKind()))
        {
            if (_selection.Count == 0) return;

            foreach (var widget in _selection)
                SetWidgetScale(widget, e.NewValue / 100.0);

            PublishWidgets();
            UpdateWidgetSelection();
            _frameDirty = true;
            SaveSettings();

            if (IsLoaded) RenderFrame();
            return;
        }

        var transform = CurrentTransform();
        transform.Scale = e.NewValue / 100.0;
        StoreTransform(transform);
    }

    private void OnResetViewClick(object sender, RoutedEventArgs e)
    {
        // В «Своём» сброс возвращает размер выделенным объектам (или всем сразу).
        if (PanelLayouts.IsObjectView(RenderKind()))
        {
            if (_selection.Count > 0)
            {
                foreach (var widget in _selection)
                    SetWidgetScale(widget, 1);
            }
            else
            {
                foreach (var item in _widgets) SetWidgetScale(item, 1);
            }

            PublishWidgets();
            UpdateWidgetSelection();
            _frameDirty = true;
            SaveSettings();

            if (IsLoaded) RenderFrame();
            return;
        }

        _transforms.Remove(RenderKind());
        ApplyViewTransform();
        SaveSettings();

        if (IsLoaded) RenderFrame();
    }

    /// <summary>
    /// Меняет размер объекта вокруг его центра: ползунок масштаба не тянет
    /// объект вправо-вниз, а растит его в обе стороны от середины.
    /// </summary>
    private void SetWidgetScale(PanelWidget widget, double scale)
    {
        var before = ThemeRenderer.WidgetBounds(widget, _snapshot);

        widget.Scale = Math.Clamp(scale, 0.5, 3);

        var after = ThemeRenderer.WidgetBounds(widget, _snapshot);

        // Центр до и после — разницу отдаём координатам, центр остаётся на месте.
        widget.X += (before.X + before.Width / 2) - (after.X + after.Width / 2);
        widget.Y += (before.Y + before.Height / 2) - (after.Y + after.Height / 2);

        // За кадр не выпускаем: тот же зажим, что и при перетаскивании.
        var offset = ThemeRenderer.LabelOffset(widget);

        widget.X = Math.Clamp(widget.X, 0, Math.Max(0, ThemeRenderer.Width - after.Width));
        widget.Y = Math.Clamp(widget.Y, -offset,
            Math.Max(-offset, ThemeRenderer.Height - after.Height));
    }

    private ViewTransform CurrentTransform() =>
        _transforms.TryGetValue(RenderKind(), out var transform) ? transform : new ViewTransform();    private void StoreTransform(ViewTransform transform)
    {
        if (transform.IsIdentity) _transforms.Remove(RenderKind());
        else _transforms[RenderKind()] = transform;

        ThemeRenderer.Transform = transform;
        _frameDirty = true;
        SaveSettingsSoon();

        if (IsLoaded) RenderFrame();
    }

    /// <summary>Подтягивает сдвиг вида: свой у каждого вида, свой и ползунок масштаба.</summary>
    private void ApplyViewTransform()
    {
        if (ScaleSlider is null) return;

        _syncingScale = true;

        if (PanelLayouts.IsObjectView(RenderKind()))
        {
            // В «Своём» ползунок масштаба меняет размер выделенных объектов.
            var has = _selection.Count > 0;
            var scale = has ? _selection[0].Scale : 1;

            ScaleSlider.IsEnabled = has;
            ScaleSlider.Opacity = has ? 1.0 : 0.4;
            ScaleSlider.Value = Math.Clamp(scale * 100, ScaleSlider.Minimum, ScaleSlider.Maximum);
            ThemeRenderer.Transform = new ViewTransform();
        }
        else
        {
            var transform = CurrentTransform();
            ScaleSlider.IsEnabled = true;
            ScaleSlider.Opacity = 1.0;
            ScaleSlider.Value = Math.Clamp(transform.Scale * 100,
                ScaleSlider.Minimum, ScaleSlider.Maximum);
            ThemeRenderer.Transform = transform;
        }

        _syncingScale = false;
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

        // Для «Медиа» ритм задаёт файл; в остальных видах — всегда ползунок,
        // даже если на фоне GIF или видео: анимация идёт своим темпом,
        // а панель обновляется с выбранной частотой.
        _effectiveFps = isMedia
            ? (int)Math.Clamp(Math.Round(_clip!.NativeFps), 1, MaxMediaFps)
            : _sliderFps;

        if (FpsSlider is not null)
            FpsSlider.IsEnabled = !isMedia;
    }

    private void OnFpsChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ApplyFrameRate();
        SaveSettingsSoon();
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
        SaveSettingsSoon();
    }

    private void SaveSettingsSoon()
    {
        if (_loading) return;

        _saveDelay.Stop();
        _saveDelay.Start();
    }

    private void SaveSettings()
    {
        // Пока окно восстанавливает прошлый сеанс, сохранять нельзя:
        // иначе значения по умолчанию затирают файл раньше, чем мы его прочитали.
        if (_loading) return;
        if (FpsSlider is null) return;

        _settings.Fps = (int)Math.Round(FpsSlider.Value);
        _settings.Brightness = BrightnessSlider is null ? _brightness : (int)Math.Round(BrightnessSlider.Value);
        _settings.Theme = _theme.ToString();
        _settings.Accent = ThemePreset.ToHex(_accent);
        _settings.Layout = _widgets.Select(widget => widget.Clone()).ToList();
        _settings.Layouts = _layouts.ToDictionary(pair => pair.Key.ToString(),
            pair => pair.Value.Select(widget => widget.Clone()).ToList());
        _settings.PanelFont = _panelFont;
        _settings.PanelInk = _panelInk;
        _settings.Background = _background;
        _settings.WidgetBrightness = _widgetBrightness;
        _settings.Transforms = _transforms.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value.Clone());
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
            Dark = _panelDark,
            Widgets = _widgets.Select(widget => widget.Clone()).ToList()
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
            ApplyInk();
            UpdateInkButtons();
            PresetNameBox.Text = preset.Name;

            _panelDark = preset.Dark;
    
            SelectTheme(preset.ToKind(), apply: false);

            // Тема возвращает не только фон: раскладку, размеры, шрифты и цвета объектов.
            if (PanelLayouts.IsObjectView(preset.ToKind()) && preset.Widgets is { } saved)
            {
                var layout = saved.Select(widget => widget.Clone()).ToList();

                _layouts[preset.ToKind()] = layout;
                _widgets = layout;
                _selection.Clear();

                PublishWidgets();
                ApplyViewTransform();
                UpdateWidgetSelection();
            }
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

    private static string KindName(ThemeKind kind) => ThemePreset.ViewName(kind);

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

            // Остановлено и свёрнуто в трей — не рисуем и не дёргаем UI.
            if (_device is null && _hiddenToTray)
            {
                try
                {
                    await Task.Delay(1000, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

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

            var backdrop = _backdropClip is { } clip
                ? SurfaceImage.ToBitmap(clip.Render())
                : _backdrop;

            var frame = ThemeRenderer.Render(theme, _snapshot, _clip, _accent, null, _panelDark, backdrop);
            Volatile.Write(ref _frame, frame);

            if (IsVisible)
            {
                if (_previewBitmap is null
                    || _previewBitmap.PixelWidth != frame.Width
                    || _previewBitmap.PixelHeight != frame.Height)
                {
                    _previewBitmap = new WriteableBitmap(
                        frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
                    Preview.Source = _previewBitmap;
                }

                _previewBitmap.WritePixels(
                    new Int32Rect(0, 0, frame.Width, frame.Height),
                    frame.Bgra, frame.Width * 4, 0);
            }

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
            Filter = MediaClipFactory.BackdropFilter,
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
        _backdropClip?.Dispose();
        _backdropClip = null;

        _backdropPath = _settings.Backdrop is { Length: > 0 } path && File.Exists(path) ? path : null;
        _backdrop = _backdropPath is null ? null : LoadImage(_backdropPath);

        // GIF или видео на фоне: кадры идут из клипа, статичная картинка не нужна.
        if (_backdropPath is null || !MediaClipFactory.IsSupported(_backdropPath)) return;

        try
        {
            var clip = MediaClipFactory.Open(_backdropPath, ThemeRenderer.Width, ThemeRenderer.Height);

            if (clip.IsStatic) clip.Dispose();
            else _backdropClip = clip;
        }
        catch
        {
            _backdropClip = null;
        }
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
        if (MediaClipFactory.IsImage(path) && LoadImage(path) is null)
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
        _hiddenToTray = true;
        ShowInTaskbar = false;
        Opacity = 1;
        Hide();

        // Свёрнутому приложению незачем держать рабочий набор окна.
        MemoryTrim.Trim();
    }

    private void ShowFromTray()
    {
        _hiddenToTray = false;
        ShowInTaskbar = true;
        Opacity = 1;
        Show();
        WindowState = WindowState.Normal;
        Activate();

        // Пока окно было скрыто, превью не обновлялось — догоняем кадр.
        _frameDirty = true;
        RenderFrame();
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
        // Отложенное сохранение могло ещё не сработать — пишем сразу.
        _saveDelay.Stop();
        SaveSettings();

        _lifetime.Cancel();
        Loc.Changed -= OnLanguageChanged;

        // Драйвер датчиков выгружаем вместе с окном.
        _sensors.Shutdown();

        var device = _device;
        _device = null;
        device?.Dispose();

        _clip?.Dispose();
        _clip = null;

        _backdropClip?.Dispose();
        _backdropClip = null;

        _tray?.Dispose();
        _tray = null;

        base.OnClosed(e);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
