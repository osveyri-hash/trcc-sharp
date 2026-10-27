using System.Windows;
using System.Windows.Media;

namespace TrccSharp.App;

/// <summary>
/// Две темы окна: нежная светлая (крем и роза) и мягкая тёмная (слива и пыль).
/// Кисти живут в ресурсах приложения, разметка ссылается на них через DynamicResource,
/// поэтому переключение происходит на лету, без перезапуска.
/// </summary>
public static class Skins
{
    public const string Light = "light";
    public const string Dark = "dark";

    public static string Normalize(string? skin) => skin == Dark ? Dark : Light;

    public static bool IsDark(string? skin) => Normalize(skin) == Dark;

    /// <summary>Меняет палитру окна целиком.</summary>
    public static void Apply(string? skin)
    {
        var dark = IsDark(skin);

        var palette = new ResourceDictionary
        {
            // Основной цвет текста и заливки активных элементов.
            ["Ink"] = Solid(dark ? "#F4EFEA" : "#2B2733"),

            // Фон окна: тёплый крем или мягкая слива.
            ["Paper"] = Solid(dark ? "#17151B" : "#FBF7F4"),

            // Карточки, поля, подложка превью.
            ["Surface"] = Solid(dark ? "#201D26" : "#FFFFFF"),

            // Второстепенный текст.
            ["Grey"] = Solid(dark ? "#9C94A4" : "#8B8492"),

            // Самый тихий текст, ползунок прокрутки.
            ["Faint"] = Solid(dark ? "#5F5868" : "#C6BFC9"),

            // Волосяные линии и рамки — чуть розовые, чтобы было нежно.
            ["Rule"] = Solid(dark ? "#2F2A38" : "#EFE2DF"),

            // Подсветка под курсором и фон активной вкладки.
            ["Wash"] = Solid(dark ? "#262231" : "#F7EDEA"),

            // Нежный акцент: иероглиф в шапке, фокус, активные мелочи.
            ["Rose"] = Solid(dark ? "#E8B4B9" : "#D98F96"),

            // Фоновая тянка: на белой теме чёрная, на чёрной — белоснежная.
            ["Phantom"] = Solid(dark ? "#FFFFFF" : "#221E29")
        };

        var merged = Application.Current.Resources.MergedDictionaries;
        merged.Clear();
        merged.Add(palette);
    }

    /// <summary>Цвет кисти из текущей палитры (для заголовка окна).</summary>
    public static Color ColorOf(string key) =>
        Application.Current.Resources[key] is SolidColorBrush brush ? brush.Color : Colors.Black;

    private static SolidColorBrush Solid(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
