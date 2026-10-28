using System.Windows;
using System.Windows.Media;

namespace TrccSharp.App;

/// <summary>
/// Палитры окна. Каждая семья — своя пара «светлая/тёмная»: «Крем» тёплая
/// бумажная, «Океан» холодная голубая, «Сакура» розовая, «Лес» зелёная,
/// «Графит» нейтральная серая, «Матрица» терминальная зелёная, «OLED» —
/// чёрная без подсветки. Кисти живут в ресурсах приложения, разметка
/// ссылается на них через DynamicResource, поэтому переключение идёт на лету.
/// </summary>
public static class Skins
{
    public const string Light = "light";
    public const string Dark = "dark";
    public const string OceanLight = "ocean-light";
    public const string OceanDark = "ocean-dark";
    public const string SakuraLight = "sakura-light";
    public const string SakuraDark = "sakura-dark";
    public const string ForestLight = "forest-light";
    public const string ForestDark = "forest-dark";
    public const string GraphiteLight = "graphite-light";
    public const string GraphiteDark = "graphite-dark";
    public const string MatrixLight = "matrix-light";
    public const string MatrixDark = "matrix-dark";
    public const string OledLight = "oled-light";
    public const string OledDark = "oled-dark";

    /// <summary>Одна палитра: девять красок, из которых живёт всё окно.</summary>
    private sealed record Palette(
        string Ink,
        string Paper,
        string Surface,
        string Grey,
        string Faint,
        string Rule,
        string Wash,
        string Rose,
        string Phantom);

    private static readonly Palette CreamLight = new(
        Ink: "#2B2733", Paper: "#FBF7F4", Surface: "#FFFFFF",
        Grey: "#8B8492", Faint: "#C6BFC9", Rule: "#EFE2DF",
        Wash: "#F7EDEA", Rose: "#D98F96", Phantom: "#221E29");

    private static readonly Palette CreamDark = new(
        Ink: "#F4EFEA", Paper: "#17151B", Surface: "#201D26",
        Grey: "#9C94A4", Faint: "#5F5868", Rule: "#2F2A38",
        Wash: "#262231", Rose: "#E8B4B9", Phantom: "#FFFFFF");

    private static readonly Palette OceanLightPalette = new(
        Ink: "#17324A", Paper: "#EFF7FB", Surface: "#FFFFFF",
        Grey: "#6C8799", Faint: "#B9CFDB", Rule: "#D8E7EF",
        Wash: "#E3F1F7", Rose: "#4C9BC4", Phantom: "#123043");

    private static readonly Palette OceanDarkPalette = new(
        Ink: "#E4F1F7", Paper: "#0B1720", Surface: "#12232E",
        Grey: "#8FA9B8", Faint: "#4F6674", Rule: "#1E3543",
        Wash: "#17303D", Rose: "#5BB6DE", Phantom: "#FFFFFF");

    // Светлая сакура: тёплая бумага с розовым подтоном, тушь — сливовая.
    private static readonly Palette SakuraLightPalette = new(
        Ink: "#3A2230", Paper: "#FFF8FA", Surface: "#FFFFFF",
        Grey: "#9A7C89", Faint: "#DCC3CF", Rule: "#F4DEE7",
        Wash: "#FDEEF3", Rose: "#D4708C", Phantom: "#3A2230");

    private static readonly Palette SakuraDarkPalette = new(
        Ink: "#F7E8EE", Paper: "#171016", Surface: "#221A20",
        Grey: "#A98F9B", Faint: "#6B5560", Rule: "#33242C",
        Wash: "#2A1E25", Rose: "#E79AB0", Phantom: "#FFFFFF");

    // Лес: светлая бумага с зеленцой, тёмная — почти хвойная.
    private static readonly Palette ForestLightPalette = new(
        Ink: "#1E2E23", Paper: "#F4F8F4", Surface: "#FFFFFF",
        Grey: "#6F8474", Faint: "#BACBBE", Rule: "#DCE9DE",
        Wash: "#EAF3EC", Rose: "#4F8A5F", Phantom: "#1E2E23");

    private static readonly Palette ForestDarkPalette = new(
        Ink: "#E6F0E8", Paper: "#0E1511", Surface: "#16201A",
        Grey: "#8FA795", Faint: "#56685C", Rule: "#23302A",
        Wash: "#1A2620", Rose: "#7FBF8F", Phantom: "#FFFFFF");

    // Графит: спокойный нейтральный серый, без цветного подтона.
    private static readonly Palette GraphiteLightPalette = new(
        Ink: "#23262B", Paper: "#EDEFF2", Surface: "#FFFFFF",
        Grey: "#767D87", Faint: "#C0C6CE", Rule: "#DCE0E6",
        Wash: "#E6E9EE", Rose: "#6E7A8A", Phantom: "#23262B");

    private static readonly Palette GraphiteDarkPalette = new(
        Ink: "#E8ECF1", Paper: "#0F1114", Surface: "#181B20",
        Grey: "#949DA8", Faint: "#4C545E", Rule: "#252A31",
        Wash: "#1C2027", Rose: "#A6B2C2", Phantom: "#FFFFFF");

    // Матрица: терминал — светлая бумага с зеленцой, тёмная почти чёрная.
    private static readonly Palette MatrixLightPalette = new(
        Ink: "#0F2A17", Paper: "#F0F7F1", Surface: "#FFFFFF",
        Grey: "#5C7A65", Faint: "#B2CBB9", Rule: "#D3E5D8",
        Wash: "#E3F1E6", Rose: "#2E8B4F", Phantom: "#0F2A17");

    private static readonly Palette MatrixDarkPalette = new(
        Ink: "#C6F5D3", Paper: "#060B07", Surface: "#0C130E",
        Grey: "#6E9B78", Faint: "#33503B", Rule: "#14261A",
        Wash: "#0D1C11", Rose: "#35D46F", Phantom: "#FFFFFF");

    // OLED: чистый чёрный в тёмной половине, почти белый — в светлой.
    private static readonly Palette OledLightPalette = new(
        Ink: "#000000", Paper: "#F5F5F5", Surface: "#FFFFFF",
        Grey: "#6F6F6F", Faint: "#BDBDBD", Rule: "#DEDEDE",
        Wash: "#ECECEC", Rose: "#4A4A4A", Phantom: "#000000");

    private static readonly Palette OledDarkPalette = new(
        Ink: "#FFFFFF", Paper: "#000000", Surface: "#050505",
        Grey: "#9B9B9B", Faint: "#4A4A4A", Rule: "#1C1C1C",
        Wash: "#141414", Rose: "#F2F2F2", Phantom: "#FFFFFF");

    private static readonly Dictionary<string, (Palette Light, Palette Dark)> Families = new()
    {
        ["cream"] = (CreamLight, CreamDark),
        ["ocean"] = (OceanLightPalette, OceanDarkPalette),
        ["sakura"] = (SakuraLightPalette, SakuraDarkPalette),
        ["forest"] = (ForestLightPalette, ForestDarkPalette),
        ["graphite"] = (GraphiteLightPalette, GraphiteDarkPalette),
        ["matrix"] = (MatrixLightPalette, MatrixDarkPalette),
        ["oled"] = (OledLightPalette, OledDarkPalette)
    };

    /// <summary>Одна строка меню оформления.</summary>
    /// <param name="Id">Ключ пункта — совпадает с именем значка.</param>
    /// <param name="Family">Семья палитры в <see cref="Families"/>.</param>
    /// <param name="Variant">Фиксированный вариант или null — «как сейчас».</param>
    /// <param name="NameKey">Ключ названия в <see cref="Loc"/>.</param>
    /// <param name="Icon">Имя значка для <see cref="ThemeIcons"/>.</param>
    public sealed record ThemeChoice(string Id, string Family, string? Variant, string NameKey, string Icon);

    /// <summary>
    /// Пункты меню. Колонка 1 — базовые светлые палитры, колонка 2 —
    /// тёмные: UniformGrid идёт по строкам, поэтому пары идут подряд.
    /// </summary>
    public static IReadOnlyList<ThemeChoice> Menu { get; } = new[]
    {
        new ThemeChoice("light", "cream", Light, "theme.app.light", "sun"),
        new ThemeChoice("dark", "cream", Dark, "theme.app.dark", "moon"),
        new ThemeChoice("ocean", "ocean", null, "theme.app.ocean", "wave"),
        new ThemeChoice("graphite", "graphite", null, "theme.app.graphite", "layers"),
        new ThemeChoice("sakura", "sakura", null, "theme.app.sakura", "blossom"),
        new ThemeChoice("matrix", "matrix", null, "theme.app.matrix", "matrix"),
        new ThemeChoice("forest", "forest", null, "theme.app.forest", "tree"),
        new ThemeChoice("oled", "oled", Dark, "theme.app.oled", "oled")
    };

    public static string Normalize(string? skin) => skin switch
    {
        Dark => Dark,
        OceanLight => OceanLight,
        OceanDark => OceanDark,
        SakuraLight => SakuraLight,
        SakuraDark => SakuraDark,
        ForestLight => ForestLight,
        ForestDark => ForestDark,
        GraphiteLight => GraphiteLight,
        GraphiteDark => GraphiteDark,
        MatrixLight => MatrixLight,
        MatrixDark => MatrixDark,
        OledLight => OledLight,
        OledDark => OledDark,
        _ => Light
    };

    public static bool IsDark(string? skin) => Normalize(skin) is
        Dark or OceanDark or SakuraDark or ForestDark or GraphiteDark or MatrixDark or OledDark;

    /// <summary>Семейство палитры: cream, ocean, sakura, forest, graphite, matrix, oled.</summary>
    public static string Family(string? skin) => Normalize(skin) switch
    {
        OceanLight or OceanDark => "ocean",
        SakuraLight or SakuraDark => "sakura",
        ForestLight or ForestDark => "forest",
        GraphiteLight or GraphiteDark => "graphite",
        MatrixLight or MatrixDark => "matrix",
        OledLight or OledDark => "oled",
        _ => "cream"
    };

    /// <summary>Светлый или тёмный вариант внутри выбранной палитры.</summary>
    public static string Variant(string? skin) => IsDark(skin) ? Dark : Light;

    /// <summary>Собирает строку темы из семейства и варианта.</summary>
    public static string Combine(string family, string variant)
    {
        var key = Families.ContainsKey(family) ? family : "cream";
        var dark = variant == Dark;

        if (key == "cream") return dark ? Dark : Light;

        return $"{key}-{(dark ? Dark : Light)}";
    }

    /// <summary>Какая тема получится, если нажать этот пункт меню.</summary>
    public static string Resolve(ThemeChoice choice, string? current) =>
        Combine(choice.Family, choice.Variant ?? Variant(current));

    /// <summary>Подсвечен ли пункт при текущей теме окна.</summary>
    public static bool IsActive(ThemeChoice choice, string? current)
    {
        var skin = Normalize(current);

        return choice.Variant is not null
            ? skin == Combine(choice.Family, choice.Variant)
            : Family(skin) == choice.Family;
    }

    /// <summary>Меняет палитру окна целиком.</summary>
    public static void Apply(string? skin)
    {
        var normalized = Normalize(skin);
        var pair = Families[Family(normalized)];
        var palette = IsDark(normalized) ? pair.Dark : pair.Light;

        var resources = new ResourceDictionary
        {
            // Основной цвет текста и заливки активных элементов.
            ["Ink"] = Solid(palette.Ink),

            // Фон окна.
            ["Paper"] = Solid(palette.Paper),

            // Карточки, поля, подложка превью.
            ["Surface"] = Solid(palette.Surface),

            // Второстепенный текст.
            ["Grey"] = Solid(palette.Grey),

            // Самый тихий текст, ползунок прокрутки.
            ["Faint"] = Solid(palette.Faint),

            // Волосяные линии и рамки.
            ["Rule"] = Solid(palette.Rule),

            // Подсветка под курсором и фон активной вкладки.
            ["Wash"] = Solid(palette.Wash),

            // Акцент: иероглиф в шапке, фокус, активные мелочи.
            ["Rose"] = Solid(palette.Rose),

            // Фоновая тянка: на белой теме чёрная, на чёрной — белоснежная.
            ["Phantom"] = Solid(palette.Phantom)
        };

        var merged = Application.Current.Resources.MergedDictionaries;
        merged.Clear();
        merged.Add(resources);
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
