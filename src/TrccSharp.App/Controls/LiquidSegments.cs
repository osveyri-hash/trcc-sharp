using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace TrccSharp.App.Controls;

/// <summary>
/// Обёртка для переключателя из нескольких ToggleButton: рисует под ними
/// «жидкое стекло» и перевозит его к выбранному варианту с лёгким вытягиванием.
/// Кнопки внутри должны быть прозрачными (без своей заливки).
/// </summary>
public sealed class LiquidSegments : Grid
{
    public static readonly DependencyProperty PillBrushProperty = DependencyProperty.Register(
        nameof(PillBrush), typeof(Brush), typeof(LiquidSegments),
        new PropertyMetadata(Brushes.Black));

    public static readonly DependencyProperty PillCornerRadiusProperty = DependencyProperty.Register(
        nameof(PillCornerRadius), typeof(CornerRadius), typeof(LiquidSegments),
        new PropertyMetadata(new CornerRadius(9)));

    public static readonly DependencyProperty ShowShellsProperty = DependencyProperty.Register(
        nameof(ShowShells), typeof(bool), typeof(LiquidSegments),
        new PropertyMetadata(false));

    public static readonly DependencyProperty ShellCornerRadiusProperty = DependencyProperty.Register(
        nameof(ShellCornerRadius), typeof(CornerRadius), typeof(LiquidSegments),
        new PropertyMetadata(new CornerRadius(9)));

    private readonly Canvas _shellLayer = new() { IsHitTestVisible = false };
    private readonly Canvas _pillLayer = new() { IsHitTestVisible = false };
    private readonly Border _pill = new() { Visibility = Visibility.Collapsed };
    private readonly List<Border> _shells = new();
    private readonly List<ToggleButton> _items = new();
    private ToggleButton? _lastChecked;
    private bool _animating;

    public LiquidSegments()
    {
        // Слои добавляются первыми, поэтому XAML-содержимое окажется поверх стекла.
        Children.Add(_shellLayer);
        Children.Add(_pillLayer);

        _pill.SetBinding(Border.BackgroundProperty, new Binding(nameof(PillBrush)) { Source = this });
        _pill.SetBinding(Border.CornerRadiusProperty, new Binding(nameof(PillCornerRadius)) { Source = this });
        _pill.RenderTransformOrigin = new Point(0.5, 0.5);
        _pill.RenderTransform = new ScaleTransform();
        _pill.Effect = new DropShadowEffect
        {
            BlurRadius = 12,
            ShadowDepth = 2,
            Direction = 270,
            Opacity = 0.22,
            Color = Colors.Black
        };

        _pillLayer.Children.Add(_pill);

        Loaded += OnLoaded;
        SizeChanged += (_, _) => { if (IsLoaded) Place(false); };
    }

    public Brush PillBrush
    {
        get => (Brush)GetValue(PillBrushProperty);
        set => SetValue(PillBrushProperty, value);
    }

    public CornerRadius PillCornerRadius
    {
        get => (CornerRadius)GetValue(PillCornerRadiusProperty);
        set => SetValue(PillCornerRadiusProperty, value);
    }

    public bool ShowShells
    {
        get => (bool)GetValue(ShowShellsProperty);
        set => SetValue(ShowShellsProperty, value);
    }

    public CornerRadius ShellCornerRadius
    {
        get => (CornerRadius)GetValue(ShellCornerRadiusProperty);
        set => SetValue(ShellCornerRadiusProperty, value);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_items.Count > 0 || !IsLoaded) return;

        CollectToggles(this);

        foreach (var item in _items)
        {
            item.Checked += (_, _) =>
            {
                _lastChecked = item;
                Place(true);
            };

            item.SizeChanged += (_, _) => Place(false);
            item.IsVisibleChanged += (_, _) => { if (item.IsVisible) Place(false); };
        }

        BuildShells();
        Place(false);
    }

    private void CollectToggles(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is ToggleButton button)
                _items.Add(button);
            else
                CollectToggles(child);
        }
    }

    private void BuildShells()
    {
        foreach (var shell in _shells)
            _shellLayer.Children.Remove(shell);

        _shells.Clear();

        if (!ShowShells) return;

        foreach (var _ in _items)
        {
            var shell = new Border
            {
                CornerRadius = ShellCornerRadius,
                BorderThickness = new Thickness(1),
                IsHitTestVisible = false
            };

            shell.SetResourceReference(Border.BackgroundProperty, "Surface");
            shell.SetResourceReference(Border.BorderBrushProperty, "Rule");

            _shellLayer.Children.Add(shell);
            _shells.Add(shell);
        }
    }

    private void Place(bool animate)
    {
        if (_items.Count == 0) return;

        UpdateShellBounds();

        var target = _lastChecked is { IsChecked: true }
            ? _lastChecked
            : _items.LastOrDefault(item => item.IsChecked == true);

        if (target is null || target.ActualWidth <= 0)
        {
            _pill.Visibility = Visibility.Collapsed;
            return;
        }

        var point = target.TranslatePoint(new Point(0, 0), this);
        var width = target.ActualWidth;
        var height = target.ActualHeight;

        _pill.Visibility = Visibility.Visible;
        _pill.Height = height;

        var fromX = Canvas.GetLeft(_pill);
        if (!animate || double.IsNaN(fromX) || _pill.Width <= 0)
        {
            _animating = false;
            _pill.BeginAnimation(Canvas.LeftProperty, null);
            _pill.BeginAnimation(FrameworkElement.WidthProperty, null);

            Canvas.SetLeft(_pill, point.X);
            Canvas.SetTop(_pill, point.Y);
            _pill.Width = width;
            return;
        }

        // Небольшое вытягивание: движение читается как капля, а не как ползунок.
        var stretch = Math.Min(0.12,
            Math.Abs(point.X - fromX) / Math.Max(1.0, width) * 0.22);

        var duration = TimeSpan.FromMilliseconds(220);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        var move = new DoubleAnimation(fromX, point.X, duration) { EasingFunction = easing };
        move.Completed += (_, _) => _animating = false;

        _animating = true;
        _pill.BeginAnimation(Canvas.LeftProperty, move);

        var widthAnimation = new DoubleAnimationUsingKeyFrames { Duration = duration };
        widthAnimation.KeyFrames.Add(new LinearDoubleKeyFrame(_pill.Width, KeyTime.FromPercent(0)));
        widthAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(width * (1 + stretch), KeyTime.FromPercent(0.4), easing));
        widthAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(width, KeyTime.FromPercent(1), easing));

        _pill.BeginAnimation(FrameworkElement.WidthProperty, widthAnimation);
    }

    private void UpdateShellBounds()
    {
        if (ShowShells && _shells.Count != _items.Count) BuildShells();

        for (var i = 0; i < _items.Count && i < _shells.Count; i++)
        {
            var item = _items[i];
            var shell = _shells[i];
            var point = item.TranslatePoint(new Point(0, 0), this);

            Canvas.SetLeft(shell, point.X);
            Canvas.SetTop(shell, point.Y);
            shell.Width = item.ActualWidth;
            shell.Height = item.ActualHeight;
        }
    }

    /// <summary>
    /// Тема окна сменилась: программно созданные подложки не всегда сами
    /// подхватывают новые кисти, поэтому перекрашиваем их явно.
    /// </summary>
    public void Refresh()
    {
        if (TryFindResource("Surface") is Brush surface)
        {
            foreach (var shell in _shells)
                shell.Background = surface;
        }

        if (TryFindResource("Rule") is Brush rule)
        {
            foreach (var shell in _shells)
                shell.BorderBrush = rule;
        }

        _pill.Background = PillBrush;

        // Если капля сейчас едет, не сбиваем анимацию: она уже летит к нужной кнопке.
        if (!_animating) Place(false);
    }
}
