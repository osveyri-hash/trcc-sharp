using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using TrccSharp.App.Rendering;

namespace TrccSharp.App;

/// <summary>
/// Сохранённая тема: всё, что нужно, чтобы вернуть экран к прежнему виду —
/// тип, медиа, яркость, частота, цвет акцента и подпись.
/// </summary>
public sealed class ThemePreset : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = nameof(ThemeKind.Monitor);
    public string? MediaPath { get; set; }
    public int Brightness { get; set; } = 100;
    public int Fps { get; set; } = 2;
    public string Accent { get; set; } = "#101012";

    /// <summary>Панель в чёрном варианте.</summary>
    public bool Dark { get; set; }

    private ImageSource? _preview;

    /// <summary>Миниатюра темы для списка — тот же кадр, что уходит на панель.</summary>
    [JsonIgnore]
    public ImageSource? Preview
    {
        get => _preview;
        set
        {
            if (ReferenceEquals(_preview, value)) return;

            _preview = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preview)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    [JsonIgnore]
    public string KindLabel => ToKind() switch
    {
        ThemeKind.Clock => Loc.T("tile.clock"),
        ThemeKind.Media => Loc.T("tile.media"),
        ThemeKind.History => Loc.T("tile.history"),
        ThemeKind.Rings => Loc.T("tile.rings"),
        ThemeKind.Minimal => Loc.T("tile.minimal"),
        ThemeKind.Test => Loc.T("tile.test"),
        _ => Loc.T("tile.monitor")
    };

    [JsonIgnore]
    public string Summary
    {
        get
        {
            var parts = new List<string> { KindLabel };

            if (Dark) parts.Add(Loc.T("theme.dark"));

            parts.Add($"{Brightness} %");
            parts.Add(string.Format(Loc.T("fps.value"), Fps));

            if (Kind == nameof(ThemeKind.Media) && !string.IsNullOrWhiteSpace(MediaPath))
                parts.Add(Path.GetFileName(MediaPath));

            return string.Join(" · ", parts);
        }
    }

    [JsonIgnore]
    public Brush AccentBrush => new SolidColorBrush(ToColor(Accent));

    public ThemeKind ToKind() =>
        Enum.TryParse<ThemeKind>(Kind, true, out var kind) ? kind : ThemeKind.Monitor;

    /// <summary>«#RRGGBB» → Color; мусор превращается в тушь.</summary>
    public static Color ToColor(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                return (Color)ColorConverter.ConvertFromString(hex);
            }
            catch
            {
                // Некорректный цвет в файле — берём акцент по умолчанию.
            }
        }

        return ThemeRenderer.DefaultAccent;
    }

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public ThemePreset Clone() => new()
    {
        Name = Name,
        Kind = Kind,
        MediaPath = MediaPath,
        Brightness = Brightness,
        Fps = Fps,
        Accent = Accent,
        Dark = Dark
    };
}

/// <summary>
/// Список сохранённых тем: %APPDATA%\TrccSharp\themes.json.
/// Битый файл не мешает запуску — начинаем с пустого списка.
/// </summary>
public sealed class ThemeStore
{
    /// <summary>Живая коллекция: список в окне обновляется сам при сохранении и удалении.</summary>
    public ObservableCollection<ThemePreset> Items { get; } = new();

    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrccSharp");

    public static string FilePath => Path.Combine(Folder, "themes.json");

    public static ThemeStore Load()
    {
        var store = new ThemeStore();

        try
        {
            if (!File.Exists(FilePath)) return store;

            var items = JsonSerializer.Deserialize<List<ThemePreset>>(File.ReadAllText(FilePath));
            if (items is null) return store;

            foreach (var item in items.Where(i => !string.IsNullOrWhiteSpace(i.Name)))
                store.Items.Add(item);
        }
        catch
        {
            // Битая тема не должна ронять приложение.
        }

        return store;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(Items, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Нет прав или места — работаем без сохранения.
        }
    }

    /// <summary>
    /// Десять готовых тем «Монитора»: пять светлых и пять тёмных с разными
    /// акцентами. Уже существующие темы с такими именами не перезаписываются.
    /// </summary>
    public void EnsureStarterThemes()
    {
        var starters = new (string Name, string Accent, bool Dark)[]
        {
            ("Роза · светлая",     "#D98F96", false),
            ("Роза · тёмная",      "#D98F96", true),
            ("Лаванда · светлая",  "#8E7CC3", false),
            ("Лаванда · тёмная",   "#8E7CC3", true),
            ("Олива · светлая",    "#7A9A5A", false),
            ("Олива · тёмная",     "#7A9A5A", true),
            ("Янтарь · светлая",   "#C7953F", false),
            ("Янтарь · тёмная",    "#C7953F", true),
            ("Сталь · светлая",    "#6B8CAE", false),
            ("Сталь · тёмная",     "#6B8CAE", true)
        };

        var added = false;

        foreach (var starter in starters)
        {
            if (IndexOf(starter.Name) >= 0) continue;

            Items.Add(new ThemePreset
            {
                Name       = starter.Name,
                Kind       = nameof(ThemeKind.Monitor),
                Accent     = starter.Accent,
                Dark       = starter.Dark,
                Brightness = 100,
                Fps        = 2
            });

            added = true;
        }

        if (added) Save();
    }

    /// <summary>Тема с таким именем заменяется целиком (имя — ключ, без учёта регистра).</summary>
    public ThemePreset Upsert(ThemePreset preset)
    {
        var index = IndexOf(preset.Name);

        if (index >= 0)
            Items[index] = preset;
        else
            Items.Add(preset);

        Save();
        return preset;
    }

    public bool Remove(string name)
    {
        var index = IndexOf(name);
        if (index < 0) return false;

        Items.RemoveAt(index);
        Save();
        return true;
    }

    private int IndexOf(string name)
    {
        for (var i = 0; i < Items.Count; i++)
        {
            if (string.Equals(Items[i].Name, name, StringComparison.CurrentCultureIgnoreCase))
                return i;
        }

        return -1;
    }
}
