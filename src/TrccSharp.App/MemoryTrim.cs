using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TrccSharp.App;

/// <summary>
/// Свёрнутое в трей приложение не должно держать в памяти рабочий набор окна.
/// Собираем мусор и отдаём системе неиспользуемые страницы. Когда окно снова
/// откроют, они подгрузятся с диска.
/// </summary>
internal static class MemoryTrim
{
    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr process);

    public static void Trim()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

            EmptyWorkingSet(Process.GetCurrentProcess().Handle);
        }
        catch
        {
            // Не критично, если политика системы не разрешает.
        }
    }
}
