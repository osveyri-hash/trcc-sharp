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

    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrccSharp");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
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
