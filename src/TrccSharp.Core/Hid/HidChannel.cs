using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TrccSharp.Core.Hid;

/// <summary>
/// Канал к LCD-экрану Thermalright через HID-интерфейс (MI_00).
/// Вендорский интерфейс WinUSB (MI_01) на этой машине не публикует ни одного
/// device interface GUID (его регистрирует INF из TRCC), поэтому работать
/// приходится через HID: output-report 513 байт = Report ID (0) + 512 данных,
/// input-report 37 байт.
///
/// Кадр — это ~300 отчётов подряд, поэтому всё, что можно переиспользовать,
/// переиспользуется: событие, буферы и закреплённые указатели создаются один раз
/// на канал, а не на каждый отчёт.
/// </summary>
public sealed class HidChannel : Protocol.ILcdLink
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const int ErrorIoPending = 997;

    private readonly SafeFileHandle _handle;
    private readonly byte[] _report;
    private readonly byte[] _input;
    private readonly ManualResetEvent _ioDone = new(false);
    private readonly object _ioLock = new();

    private GCHandle _reportPin;
    private GCHandle _inputPin;
    private bool _disposed;

    public string DevicePath { get; }
    public string Description => $"HID {DevicePath}";
    public int OutputReportLength { get; }
    public int InputReportLength { get; }

    /// <summary>ILcdLink: порция кадра идёт одним 512-байтным отчётом.</summary>
    public void WriteChunk(ReadOnlySpan<byte> data) => WriteReport(data);

    /// <summary>ILcdLink: ответ устройства (null — тишина).</summary>
    public byte[]? TryRead(int timeoutMs) => TryReadReport(timeoutMs);

    private HidChannel(string path, SafeFileHandle handle, int outputReportLength, int inputReportLength)
    {
        DevicePath = path;
        _handle = handle;
        OutputReportLength = outputReportLength;
        InputReportLength = inputReportLength;

        _report = new byte[outputReportLength];
        _input = new byte[inputReportLength];
        _reportPin = GCHandle.Alloc(_report, GCHandleType.Pinned);
        _inputPin = GCHandle.Alloc(_input, GCHandleType.Pinned);
    }

    /// <summary>Открывает первый HID-интерфейс с указанными VID/PID.</summary>
    public static HidChannel Open(ushort vendorId = 0x0416, ushort productId = 0x5302)
    {
        var info = HidEnumerator.Enumerate()
            .FirstOrDefault(d => d.VendorId == vendorId && d.ProductId == productId);

        if (info is null)
            throw new InvalidOperationException(
                $"HID-интерфейс {vendorId:X4}:{productId:X4} не найден. Проверь USB-кабель от помпы.");

        var handle = CreateFile(info.DevicePath,
            GenericRead | GenericWrite, FileShareReadWrite, IntPtr.Zero,
            OpenExisting, FileFlagOverlapped, IntPtr.Zero);

        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось открыть HID-интерфейс экрана");

        return new HidChannel(info.DevicePath, handle, info.OutputReportLength, info.InputReportLength);
    }

    /// <summary>
    /// Отправка одного отчёта: Report ID 0 + данные. Размер отчёта диктует
    /// устройство (513 байт), поэтому payload должен быть 512 байт.
    /// </summary>
    public void WriteReport(ReadOnlySpan<byte> payload, int timeoutMs = 2000)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(HidChannel));
        if (payload.Length > _report.Length - 1)
            throw new ArgumentException($"Отчёт не влезает: {payload.Length} > {_report.Length - 1}");

        lock (_ioLock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HidChannel));

            var target = _report.AsSpan(1);
            payload.CopyTo(target);

            // Хвост прошлого отчёта не должен уехать вместе с коротким.
            if (payload.Length < target.Length)
                target[payload.Length..].Clear();

            var written = Transfer(_reportPin.AddrOfPinnedObject(), _report.Length, write: true, timeoutMs);
            if (written != _report.Length)
                throw new IOException($"Записано {written} из {_report.Length} байт отчёта");
        }
    }

    /// <summary>
    /// Чтение одного input-отчёта с таймаутом. Возвращает null, если за отведённое
    /// время ответа не было. Первый байт ответа — Report ID.
    /// </summary>
    public byte[]? TryReadReport(int timeoutMs)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(HidChannel));

        lock (_ioLock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HidChannel));

            var read = Transfer(_inputPin.AddrOfPinnedObject(), _input.Length, write: false, timeoutMs);
            return read <= 0 ? null : _input[..read];
        }
    }

    /// <summary>
    /// Общий путь для ReadFile/WriteFile на handle, открытом с FILE_FLAG_OVERLAPPED:
    /// запускаем операцию, ждём событие, забираем результат. 0 = таймаут.
    /// Буфер и событие — поля канала, поэтому вызовы сериализуются через _ioLock.
    /// </summary>
    private int Transfer(IntPtr buffer, int length, bool write, int timeoutMs)
    {
        _ioDone.Reset();
        var overlapped = new NativeOverlapped { EventHandle = _ioDone.SafeWaitHandle.DangerousGetHandle() };

        var ok = write
            ? WriteFile(_handle, buffer, (uint)length, out _, ref overlapped)
            : ReadFile(_handle, buffer, (uint)length, out _, ref overlapped);

        if (!ok)
        {
            var err = Marshal.GetLastWin32Error();
            if (err != ErrorIoPending)
                throw new Win32Exception(err, $"{(write ? "WriteFile" : "ReadFile")} (HID), код {err}");
        }

        if (!_ioDone.WaitOne(timeoutMs))
        {
            // Отменяем и обязательно «собираем» отменённую операцию:
            // иначе её сигнал придёт уже следующему вызову (событие одно на канал).
            CancelIoEx(_handle, ref overlapped);
            GetOverlappedResult(_handle, ref overlapped, out _, wait: true);
            return 0;
        }

        if (!GetOverlappedResult(_handle, ref overlapped, out var transferred, false))
        {
            var err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err, $"GetOverlappedResult, код {err}");
        }

        return (int)transferred;
    }

    public void Dispose()
    {
        if (_disposed) return;

        lock (_ioLock)
        {
            if (_disposed) return;
            _disposed = true;

            CancelIoEx(_handle, IntPtr.Zero);
            _handle.Dispose();
        }

        if (_reportPin.IsAllocated) _reportPin.Free();
        if (_inputPin.IsAllocated) _inputPin.Free();
        _ioDone.Dispose();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr sa,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(SafeFileHandle handle, IntPtr buffer, uint count, out uint written, ref NativeOverlapped overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(SafeFileHandle handle, IntPtr buffer, uint count, out uint read, ref NativeOverlapped overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOverlappedResult(SafeFileHandle handle, ref NativeOverlapped overlapped, out uint transferred, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(SafeFileHandle handle, ref NativeOverlapped overlapped);
}
