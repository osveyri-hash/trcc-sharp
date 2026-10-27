using System.IO;
using System.Text.Json;

namespace TrccSharp.App;

/// <summary>Что приложение помнит между запусками.</summary>
public sealed class Settings
{
    public string? MediaPath { get; set; }
    public string? Theme { get; set; }

    /// <summary>Вид темы «Монитор»: Обычный, Минимал, График, Кольца и остальные.</summary>
    public string? MonitorView { get; set; }

    public int Fps { get; set; } = 2;

    /// <summary>Яркость панели, 10–100 %.</summary>
    public int Brightness { get; set; } = 100;

    /// <summary>Цвет акцента темы, «#RRGGBB».</summary>
    public string? Accent { get; set; }

    /// <summary>Своя подпись внизу кадра; пусто — подписи нет.</summary>
    public string? Caption { get; set; }

    /// <summary>Своя раскладка панели: объекты, которые двигаются по превью.</summary>
    public List<PanelWidget> Layout { get; set; } = new();

    /// <summary>Шрифт кадра: имя семейства; пусто — Segoe UI.</summary>
    public string? PanelFont { get; set; }

    /// <summary>Цвет текста кадра: пусто — тушь темы, light, grey или accent.</summary>
    public string? PanelInk { get; set; }

    /// <summary>Яркость фона кадра, 20–200 %: 100 — как есть, выше — светлее.</summary>
    public int Background { get; set; } = 100;

    /// <summary>Сдвиг и масштаб содержимого — свой для каждого вида.</summary>
    public Dictionary<string, ViewTransform> Transforms { get; set; } = new();

    /// <summary>Тема окна: "light" (бумага) или "dark" (тушь).</summary>
    public string? UiTheme { get; set; }

    /// <summary>Язык интерфейса: "ru" или "en".</summary>
    public string? Language { get; set; }

    /// <summary>Панель в чёрном варианте (та же тема, инвертированные цвета).</summary>
    public bool PanelDark { get; set; }

    /// <summary>Свой фон тем «Монитор» и «Часы» — путь к картинке; пусто — бумага темы.</summary>
    public string? Backdrop { get; set; }

    /// <summary>Недавние картинки и видео: одна строка под выбором медиа.</summary>
    public List<string> RecentMedia { get; set; } = new();

    /// <summary>Запускать экран СЖО при входе в Windows.</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>Сразу включать вывод на панель при запуске приложения.</summary>
    public bool AutoOutput { get; set; } = true;

    /// <summary>Левая панель свёрнута до одних контролов (кнопка «три полоски»).</summary>
    public bool Compact { get; set; }

    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrccSharp");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
                if (settings is not null)
                {
                    settings.RecentMedia ??= new List<string>();
                    settings.Layout ??= new List<PanelWidget>();
                    settings.Transforms ??= new Dictionary<string, ViewTransform>();
                    return settings;
                }
            }
        }
        catch
        {
            // Битые настройки не должны мешать запуску — просто начинаем с чистых.
        }

        return new Settings();
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Нет прав или места — работаем без сохранения.
        }
    }
}
