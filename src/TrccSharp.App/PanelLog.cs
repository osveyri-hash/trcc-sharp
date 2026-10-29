using System.IO;
using System.Text;

namespace TrccSharp.App;

/// <summary>
/// Журнал панели: короткие строки о том, когда канал поднялся, отвалился
/// и когда прошла попытка переподключения. Нужен, чтобы после очередного
/// «экран погас» было видно причину, а не гадать.
///
/// Файл лежит рядом с настройками (в git и в установщик не попадает),
/// при разрастании переписывается заново — хвост важнее начала.
/// </summary>
internal static class PanelLog
{
    private const long MaxBytes = 512 * 1024;

    private static readonly object Gate = new();

    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrccSharp");

    private static string FilePath => Path.Combine(Folder, "panel.log");

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}";

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Folder);

                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                    File.WriteAllText(FilePath, $"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss} журнал обрезан ---{Environment.NewLine}");

                File.AppendAllText(FilePath, line, Encoding.UTF8);
            }
            catch
            {
                // Журнал — не повод падать: нет прав или места, работаем дальше.
            }
        }
    }
}
