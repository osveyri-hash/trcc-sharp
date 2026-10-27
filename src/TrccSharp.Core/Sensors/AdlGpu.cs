using System.Runtime.InteropServices;

namespace TrccSharp.Core.Sensors;

/// <summary>
/// Температура GPU через AMD ADL (atiadlxx.dll). Без пакетов и без прав администратора.
/// Рабочий путь для современных карт — PMLog (Overdrive8: Navi и новее),
/// для старых — OverdriveN, в самом конце — Overdrive6.
/// </summary>
public sealed class AdlGpu : IDisposable
{
    private const string Dll = "atiadlxx.dll";

    // Типы температур, которыми оперирует вызывающий код.
    public const int TempEdge = 1;
    public const int TempMem = 2;
    public const int TempVrm = 3;
    public const int TempHotspot = 5;

    // Индексы датчиков в таблице PMLog (ADL_PMLOG_SENSORS из adl_defines.h).
    private const int PmLogEdge = 8;
    private const int PmLogMem = 9;
    private const int PmLogVrmVdd = 11;
    private const int PmLogLiquid = 12;
    private const int PmLogHotspot = 27;
    private const int PmLogGfx = 28;
    private const int PmLogSoc = 29;

    private const int PmLogSensors = 256;
    private const int PmLogSensorSize = 8;               // int supported + int value
    private const int PmLogBufferSize = 4 + PmLogSensors * PmLogSensorSize;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr MallocCallback(int size);

    private readonly MallocCallback _malloc;
    private IntPtr _context;
    private int _adapterIndex = -1;
    private bool _initialized;

    public string? LastError { get; private set; }
    public bool IsAvailable => _initialized && _adapterIndex >= 0;

    public AdlGpu()
    {
        // Колбэк должен жить всё время работы ADL, иначе сборщик его освободит.
        _malloc = size => Marshal.AllocHGlobal(size);
    }

    public bool TryInitialize()
    {
        if (_initialized) return IsAvailable;

        try
        {
            var result = Adl2MainControlCreate(_malloc, 1, out _context);
            if (result != 0 || _context == IntPtr.Zero)
            {
                LastError = $"ADL2_Main_Control_Create вернул {result}";
                return false;
            }

            if (NumberOfAdaptersGet(_context, out var count) != 0 || count <= 0)
            {
                LastError = $"ADL не нашёл ни одного адаптера (count={count})";
                return false;
            }

            for (var i = 0; i < count; i++)
            {
                if (AdapterActiveGet(_context, i, out var active) == 0 && active == 1)
                {
                    _adapterIndex = i;
                    break;
                }
            }

            if (_adapterIndex < 0)
            {
                LastError = "Ни один адаптер ADL не активен";
                return false;
            }

            _initialized = true;
            return true;
        }
        catch (DllNotFoundException)
        {
            LastError = "atiadlxx.dll не найден (нет драйвера AMD)";
            return false;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>Температура в градусах Цельсия или null, если датчик недоступен.</summary>
    public double? ReadTemperature(int type = TempEdge)
    {
        if (!TryInitialize()) return null;

        // PMLog отдаёт уже готовые градусы Цельсия.
        if (TryPmLog(type, out var edge)) return edge;

        // OverdriveN и Overdrive6 отдают тысячные доли (OD6 — целые).
        if (TryOverdriveN(type, out var rawN)) return Normalize(rawN);
        if (TryOverdrive6(out var raw6)) return Normalize(raw6);

        return null;
    }

    /// <summary>Сырое значение без нормализации — для диагностики единиц.</summary>
    public int? ReadRawTemperature(int type = TempEdge)
    {
        if (!TryInitialize()) return null;
        if (TryPmLog(type, out var edge)) return edge;
        if (TryOverdriveN(type, out var rawN)) return rawN;
        if (TryOverdrive6(out var raw6)) return raw6;
        return null;
    }

    /// <summary>Подробный разбор: какие функции и датчики реально отвечают на этом драйвере.</summary>
    public IReadOnlyList<string> Diagnose()
    {
        if (!TryInitialize())
            return new[] { $"ADL недоступен: {LastError}" };

        var lines = new List<string> { $"активный адаптер: #{_adapterIndex}" };

        var table = QueryPmLog(out var pmLogResult);
        lines.Add($"PMLog: rc={pmLogResult}");
        if (table is not null)
        {
            foreach (var (name, index) in new[]
                     {
                         ("edge", PmLogEdge),
                         ("mem", PmLogMem),
                         ("vrmvdd", PmLogVrmVdd),
                         ("liquid", PmLogLiquid),
                         ("hotspot", PmLogHotspot),
                         ("gfx", PmLogGfx),
                         ("soc", PmLogSoc)
                     })
            {
                lines.Add($"  PMLog[{index,2}] {name,-8} = {(table[index] < 0 ? "нет данных" : table[index].ToString())}");
            }
        }

        for (var type = 1; type <= 5; type++)
        {
            int rc, raw;
            try
            {
                rc = OverdriveNTemperatureGet(_context, _adapterIndex, type, out raw);
            }
            catch (EntryPointNotFoundException)
            {
                lines.Add($"OverdriveN type={type}: точки входа нет");
                continue;
            }

            lines.Add($"OverdriveN type={type}: rc={rc} raw={raw}");
        }

        lines.Add(ProbeOverdrive6());
        return lines;
    }

    /// <summary>
    /// Один запрос к PMLog. Возвращает таблицу значений по индексам ADL_PMLOG_SENSORS,
    /// -1 в элементе, который драйвер не поддерживает.
    /// </summary>
    private int[]? QueryPmLog(out int result)
    {
        result = 0;
        var buffer = Marshal.AllocHGlobal(PmLogBufferSize);
        try
        {
            // Драйвер ожидает в первом поле размер структуры, остальное — нули.
            Marshal.WriteInt32(buffer, 0, PmLogBufferSize);
            for (var offset = 4; offset < PmLogBufferSize; offset += 4)
                Marshal.WriteInt32(buffer, offset, 0);

            result = QueryPmLogDataGet(_context, _adapterIndex, buffer);
            if (result != 0) return null;

            var values = new int[PmLogSensors];
            for (var i = 0; i < PmLogSensors; i++)
            {
                var supported = Marshal.ReadInt32(buffer, 4 + i * PmLogSensorSize);
                var value = Marshal.ReadInt32(buffer, 4 + i * PmLogSensorSize + 4);
                values[i] = supported != 0 ? value : -1;
            }

            return values;
        }
        catch (EntryPointNotFoundException)
        {
            result = -1;
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private bool TryPmLog(int type, out int value)
    {
        value = 0;
        var table = QueryPmLog(out _);
        if (table is null) return false;

        var index = type switch
        {
            TempMem => PmLogMem,
            TempVrm => PmLogVrmVdd,
            TempHotspot => PmLogHotspot,
            _ => PmLogEdge
        };

        // Отрицательное и нулевое значение — датчика нет.
        if (table[index] <= 0) return false;

        value = table[index];
        return true;
    }

    private string ProbeOverdrive6()
    {
        try
        {
            var rc = Overdrive6TemperatureGet(_context, _adapterIndex, out var raw);
            return $"Overdrive6: rc={rc} raw={raw}";
        }
        catch (EntryPointNotFoundException)
        {
            return "Overdrive6: точки входа нет в этой версии ADL";
        }
    }

    private bool TryOverdriveN(int type, out int raw)
    {
        raw = 0;
        try
        {
            // 0 = ADL_OK. Отдельные типы датчиков могут быть не поддержаны — это нормально.
            return OverdriveNTemperatureGet(_context, _adapterIndex, type, out raw) == 0 && raw > 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private bool TryOverdrive6(out int raw)
    {
        raw = 0;
        try
        {
            return Overdrive6TemperatureGet(_context, _adapterIndex, out raw) == 0 && raw > 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>OverdriveN отдаёт тысячные доли градуса, Overdrive6 — целые.</summary>
    private static double Normalize(int raw) => raw > 1000 ? raw / 1000.0 : raw;

    public void Dispose()
    {
        if (_context != IntPtr.Zero)
        {
            try { Adl2MainControlDestroy(_context); } catch { /* уже не важно */ }
            _context = IntPtr.Zero;
        }

        _initialized = false;
    }

    [DllImport(Dll, EntryPoint = "ADL2_Main_Control_Create", CallingConvention = CallingConvention.StdCall)]
    private static extern int Adl2MainControlCreate(MallocCallback callback, int enumConnectedAdapters, out IntPtr context);

    [DllImport(Dll, EntryPoint = "ADL2_Main_Control_Destroy", CallingConvention = CallingConvention.StdCall)]
    private static extern int Adl2MainControlDestroy(IntPtr context);

    [DllImport(Dll, EntryPoint = "ADL2_Adapter_NumberOfAdapters_Get", CallingConvention = CallingConvention.StdCall)]
    private static extern int NumberOfAdaptersGet(IntPtr context, out int count);

    [DllImport(Dll, EntryPoint = "ADL2_Adapter_Active_Get", CallingConvention = CallingConvention.StdCall)]
    private static extern int AdapterActiveGet(IntPtr context, int adapterIndex, out int status);

    [DllImport(Dll, EntryPoint = "ADL2_New_QueryPMLogData_Get", CallingConvention = CallingConvention.StdCall)]
    private static extern int QueryPmLogDataGet(IntPtr context, int adapterIndex, IntPtr dataOutput);

    [DllImport(Dll, EntryPoint = "ADL2_OverdriveN_Temperature_Get", CallingConvention = CallingConvention.StdCall)]
    private static extern int OverdriveNTemperatureGet(IntPtr context, int adapterIndex, int type, out int temperature);

    [DllImport(Dll, EntryPoint = "ADL2_Overdrive6_Temperature_Get", CallingConvention = CallingConvention.StdCall)]
    private static extern int Overdrive6TemperatureGet(IntPtr context, int adapterIndex, out int temperature);
}
