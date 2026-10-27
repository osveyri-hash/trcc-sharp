using System.IO;
using Microsoft.Win32;

namespace TrccSharp.App;

/// <summary>
/// Автозапуск при входе в систему: ключ в HKCU\...\Run с флагом --autostart,
/// чтобы приложение сразу свернулось в трей и включило вывод на панель.
/// </summary>
public static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TrccSharp";

    /// <summary>Путь к запущенному exe (для single-file сборки это сам файл).</summary>
    private static string ExePath => Environment.ProcessPath ?? "";

    public static void Enable()
    {
        try
        {
            var path = ExePath;
            if (path.Length == 0) return;

            // Из dotnet run автозапуск писать бессмысленно: путь ведёт на dotnet.exe.
            if (string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet",
                    StringComparison.OrdinalIgnoreCase))
                return;

            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);

            // Кавычки обязательны: путь может содержать пробелы.
            key?.SetValue(ValueName, $"\"{path}\" --autostart");
        }
        catch
        {
            // Политики или права не дают писать в реестр — не мешаем запуску.
        }
    }

    public static void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // см. Enable
        }
    }

    /// <summary>Запущены ли мы автозагрузкой (тогда окно прячется в трей).</summary>
    public static bool IsAutoStart =>
        Environment.GetCommandLineArgs()
            .Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));

    /// <summary>Есть ли уже наш ключ в автозагрузке.</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
