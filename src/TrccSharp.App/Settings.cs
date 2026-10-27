using System.IO;
using System.Text.Json;

namespace TrccSharp.App;

/// <summary>Что приложение помнит между запусками.</summary>
public sealed class Settings
{
    public string? MediaPath { get; set; }
    public string? Theme { get; set; }
    public int Fps { get; set; } = 2;

    /// <summary>Яркость панели, 10–100 %.</summary>
    public int Brightness { get; set; } = 100;

    /// <summary>Цвет акцента темы, «#RRGGBB».</summary>
    public string? Accent { get; set; }

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
