using System.Diagnostics;
using System.Management;
using LibreHardwareMonitor.Hardware;
using System.Runtime.InteropServices;
using TrccSharp.Core.Sensors;

namespace TrccSharp.App.Sensors;

/// <summary>Снимок показателей на момент отрисовки кадра.</summary>
public sealed record SensorSnapshot(
    double CpuLoadPercent,
    double RamUsedGb,
    double RamTotalGb,
    double? GpuTempC,
    TimeSpan Uptime,
    DateTime Now,
    double? CpuTempC = null,
    double? GpuLoadPercent = null)
{
    public static SensorSnapshot Empty { get; } =
        new(0, 0, 0, null, TimeSpan.Zero, DateTime.Now);

    public double RamLoadPercent => RamTotalGb <= 0 ? 0 : RamUsedGb / RamTotalGb * 100;
}

/// <summary>
/// Датчики без внешних пакетов: загрузка CPU через GetSystemTimes,
/// память через GlobalMemoryStatusEx, температура GPU через AMD ADL
/// (для NVIDIA — запасной путь через nvidia-smi).
/// </summary>
public sealed class SensorReader
{
    private ulong _prevIdle, _prevKernel, _prevUser;
    private bool _hasPrevious;

    private double? _cachedGpuTemp;
    private DateTime _gpuTempAt = DateTime.MinValue;
    private bool _nvidiaSmiMissing;
    private AdlGpu? _adl;

    // WMI и драйвер датчиков опрашиваем в отдельном потоке:
    // запросы медленные, кадр они держать не должны.
    private Thread? _thermalThread;
    private volatile bool _thermalStarted;
    private volatile int _cpuTempRaw = int.MinValue;
    private volatile int _gpuLoadRaw = int.MinValue;

    // Датчики ЦП через LibreHardwareMonitor (нужен драйвер, обычно с правами админа).
    private Computer? _computer;

    public SensorSnapshot Read()
    {
        StartThermalPolling();

        return new SensorSnapshot(
            ReadCpuLoad(),
            ReadRamUsedGb(),
            ReadRamTotalGb(),
            ReadGpuTemp(),
            TimeSpan.FromMilliseconds(Environment.TickCount64),
            DateTime.Now,
            _cpuTempRaw == int.MinValue ? null : _cpuTempRaw,
            _gpuLoadRaw == int.MinValue ? null : _gpuLoadRaw);
    }

    /// <summary>Останавливает драйвер датчиков при выходе.</summary>
    public void Shutdown()
    {
        var computer = _computer;
        _computer = null;

        try
        {
            computer?.Close();
        }
        catch
        {
            // Драйвер мог уже выгрузиться — ничего страшного.
        }
    }

    private void StartThermalPolling()
    {
        if (_thermalStarted) return;

        _thermalStarted = true;

        _thermalThread = new Thread(ThermalLoop)
        {
            IsBackground = true,
            Name = "trcc-sensors"
        };

        _thermalThread.Start();
    }

    private void ThermalLoop()
    {
        // Драйвер датчиков: без прав администратора просто не даст температуру.
        try
        {
            var computer = new Computer { IsCpuEnabled = true };
            computer.Open();
            _computer = computer;
        }
        catch
        {
            _computer = null;
        }

        while (true)
        {
            try
            {
                if (ReadCpuTemperature() is { } temp) _cpuTempRaw = (int)Math.Round(temp);
                if (ReadGpuLoad() is { } load) _gpuLoadRaw = (int)Math.Round(load);
            }
            catch
            {
                // Нет WMI или счётчиков — просто не показываем значение.
            }

            Thread.Sleep(2500);
        }
    }

    /// <summary>Температура ЦП через драйвер датчиков (LibreHardwareMonitor).</summary>
    private double? ReadCpuTemperature()
    {
        var computer = _computer;
        if (computer is null) return null;

        try
        {
            foreach (var hardware in computer.Hardware)
            {
                if (hardware.HardwareType != HardwareType.Cpu) continue;

                hardware.Update();
                foreach (var sub in hardware.SubHardware) sub.Update();

                double? best = null;

                foreach (var sensor in hardware.Sensors)
                {
                    if (sensor.SensorType != SensorType.Temperature || sensor.Value is not { } value) continue;

                    if (sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase)
                        || sensor.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase)
                        || sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                        best = Math.Max(best ?? double.MinValue, value);
                }

                if (best is > 1 and < 120) return best;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Загрузка ГП по счётчикам Windows: сумма движков 3D.</summary>
    private static double? ReadGpuLoad()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, UtilizationPercentage FROM " +
                "Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");

            double sum = 0;

            foreach (ManagementObject engine in searcher.Get())
            {
                var name = engine["Name"] as string ?? "";

                if (!name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase)) continue;

                sum += Convert.ToDouble(engine["UtilizationPercentage"]);
            }

            return Math.Clamp(sum, 0, 100);
        }
        catch
        {
            return null;
        }
    }

    private double ReadCpuLoad()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;

        var idleTicks = idle.ToUInt64();
        var kernelTicks = kernel.ToUInt64();
        var userTicks = user.ToUInt64();

        if (!_hasPrevious)
        {
            _prevIdle = idleTicks;
            _prevKernel = kernelTicks;
            _prevUser = userTicks;
            _hasPrevious = true;
            return 0;
        }

        var idleDelta = idleTicks - _prevIdle;
        var totalDelta = (kernelTicks - _prevKernel) + (userTicks - _prevUser);

        _prevIdle = idleTicks;
        _prevKernel = kernelTicks;
        _prevUser = userTicks;

        if (totalDelta == 0) return 0;
        var load = (1.0 - (double)idleDelta / totalDelta) * 100;
        return Math.Clamp(load, 0, 100);
    }

    private static double ReadRamTotalGb() =>
        TryReadMemory(out var status) ? status.TotalPhys / 1024.0 / 1024 / 1024 : 0;

    private static double ReadRamUsedGb() =>
        TryReadMemory(out var status) ? (status.TotalPhys - status.AvailPhys) / 1024.0 / 1024 / 1024 : 0;

    private static bool TryReadMemory(out MemoryStatusEx status)
    {
        status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status);
    }

    /// <summary>
    /// Температура GPU: сначала AMD ADL, затем nvidia-smi (для NVIDIA).
    /// Кэш на 3 секунды, чтобы не дёргать драйвер каждый кадр.
    /// </summary>
    private double? ReadGpuTemp()
    {
        if (DateTime.UtcNow - _gpuTempAt < TimeSpan.FromSeconds(3)) return _cachedGpuTemp;

        _gpuTempAt = DateTime.UtcNow;
        _cachedGpuTemp = ReadAmdGpuTemp() ?? ReadNvidiaSmiTemp();
        return _cachedGpuTemp;
    }

    private double? ReadAmdGpuTemp()
    {
        try
        {
            _adl ??= new AdlGpu();
            if (!_adl.TryInitialize())
            {
                // Нет карты AMD — ADL больше не нужен.
                _adl.Dispose();
                _adl = null;
                return null;
            }

            return _adl.ReadTemperature(AdlGpu.TempEdge);
        }
        catch
        {
            _adl?.Dispose();
            _adl = null;
            return null;
        }
    }

    private double? ReadNvidiaSmiTemp()
    {
        if (_nvidiaSmiMissing) return null;

        try
        {
            using var process = Process.Start(new ProcessStartInfo("nvidia-smi",
                "--query-gpu=temperature.gpu --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process is null)
            {
                _nvidiaSmiMissing = true;
                return null;
            }

            var text = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(1500);

            return double.TryParse(text.Split('\n')[0].Trim(), out var temp) ? temp : null;
        }
        catch
        {
            _nvidiaSmiMissing = true;
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong ToUInt64() => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
