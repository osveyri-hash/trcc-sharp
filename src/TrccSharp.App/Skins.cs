using System.Windows;
using System.Windows.Media;

namespace TrccSharp.App;

/// <summary>
/// Палитры окна. «Крем» — тёплая светлая/тёмная пара, «Океан» — холодная
/// голубая/ночная. Кисти живут в ресурсах приложения, разметка ссылается
/// на них через DynamicResource, поэтому переключение идёт на лету.
/// </summary>
public static class Skins
{
    public const string Light = "light";
    public const string Dark = "dark";
    public const string OceanLight = "ocean-light";
    public const string OceanDark = "ocean-dark";

    public static string Normalize(string? skin) => skin switch
    {
        Dark => Dark,
        OceanLight => OceanLight,
        OceanDark => OceanDark,
        _ => Light
    };

    public static bool IsDark(string? skin) => Normalize(skin) is Dark or OceanDark;

    public static bool IsOcean(string? skin) => Normalize(skin) is OceanLight or OceanDark;

    /// <summary>Светлый или тёмный вариант внутри выбранной палитры.</summary>
    public static string Variant(string? skin) => IsDark(skin) ? Dark : Light;

    /// <summary>Собирает строку темы из палитры и варианта.</summary>
    public static string Combine(string family, string variant) => family == "ocean"
        ? (variant == Dark ? OceanDark : OceanLight)
        : (variant == Dark ? Dark : Light);

    /// <summary>Меняет палитру окна целиком.</summary>
    public static void Apply(string? skin)
    {
        var dark = IsDark(skin);
        var ocean = IsOcean(skin);

        var palette = new ResourceDictionary
        {
            // Основной цвет текста и заливки активных элементов.
            ["Ink"] = Solid(ocean
                ? (dark ? "#E4F1F7" : "#17324A")
                : (dark ? "#F4EFEA" : "#2B2733")),

            // Фон окна.
            ["Paper"] = Solid(ocean
                ? (dark ? "#0B1720" : "#EFF7FB")
                : (dark ? "#17151B" : "#FBF7F4")),

            // Карточки, поля, подложка превью.
            ["Surface"] = Solid(ocean
                ? (dark ? "#12232E" : "#FFFFFF")
                : (dark ? "#201D26" : "#FFFFFF")),

            // Второстепенный текст.
            ["Grey"] = Solid(ocean
                ? (dark ? "#8FA9B8" : "#6C8799")
                : (dark ? "#9C94A4" : "#8B8492")),

            // Самый тихий текст, ползунок прокрутки.
            ["Faint"] = Solid(ocean
                ? (dark ? "#4F6674" : "#B9CFDB")
                : (dark ? "#5F5868" : "#C6BFC9")),

            // Волосяные линии и рамки.
            ["Rule"] = Solid(ocean
                ? (dark ? "#1E3543" : "#D8E7EF")
                : (dark ? "#2F2A38" : "#EFE2DF")),

            // Подсветка под курсором и фон активной вкладки.
            ["Wash"] = Solid(ocean
                ? (dark ? "#17303D" : "#E3F1F7")
                : (dark ? "#262231" : "#F7EDEA")),

            // Акцент: иероглиф в шапке, фокус, активные мелочи.
            ["Rose"] = Solid(ocean
                ? (dark ? "#5BB6DE" : "#4C9BC4")
                : (dark ? "#E8B4B9" : "#D98F96")),

            // Фоновая тянка: на белой теме чёрная, на чёрной — белоснежная.
            ["Phantom"] = Solid(ocean
                ? (dark ? "#FFFFFF" : "#123043")
                : (dark ? "#FFFFFF" : "#221E29"))
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
