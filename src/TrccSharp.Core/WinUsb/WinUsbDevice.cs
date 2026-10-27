using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TrccSharp.Core.WinUsb;

/// <summary>Пайп устройства: адрес, тип и максимальный размер пакета.</summary>
public sealed record UsbPipe(byte Id, int Type, ushort MaxPacketSize, byte Interval)
{
    public bool IsIn => (Id & 0x80) != 0;
    public bool IsBulk => Type == 2;
    public override string ToString() => $"0x{Id:X2} type={Type} mps={MaxPacketSize}";
}

/// <summary>
/// Открытое WinUSB-устройство: синхронные bulk-передачи с таймаутом на пайп.
/// </summary>
public sealed class WinUsbDevice : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint PipeTransferTimeout = 0x03;

    private IntPtr _handle;
    private readonly SafeFileHandle _file;
    private readonly Dictionary<byte, UsbPipe> _pipes = new();
    private bool _disposed;

    public string DevicePath { get; }
    public ushort VendorId { get; }
    public ushort ProductId { get; }
    public ushort BcdDevice { get; }
    public byte InterfaceNumber { get; }
    public IReadOnlyDictionary<byte, UsbPipe> Pipes => _pipes;

    private WinUsbDevice(string path, IntPtr handle, SafeFileHandle file,
        ushort vid, ushort pid, ushort bcdDevice, byte interfaceNumber)
    {
        DevicePath = path;
        _handle = handle;
        _file = file;
        VendorId = vid;
        ProductId = pid;
        BcdDevice = bcdDevice;
        InterfaceNumber = interfaceNumber;
    }

    public static WinUsbDevice Open(UsbDeviceNode node, uint readTimeoutMs = 1000, uint writeTimeoutMs = 1000)
    {
        var file = CreateFile(node.DevicePath, GenericRead | GenericWrite, FileShareReadWrite,
            IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);

        if (file.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"CreateFile не открыл {node.VidPid}: {node.DevicePath}");

        if (!WinUsbNative.WinUsb_Initialize(file, out var handle))
        {
            var err = Marshal.GetLastWin32Error();
            file.Dispose();
            throw new Win32Exception(err, $"WinUsb_Initialize не сработал для {node.VidPid} (код {err})");
        }

        var device = new WinUsbDevice(node.DevicePath, handle, file, node.VendorId, node.ProductId,
            ReadBcdDevice(handle), node.InterfaceNumber);

        if (!WinUsbNative.WinUsb_QueryInterfaceSettings(handle, 0, out var iface))
        {
            device.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WinUsb_QueryInterfaceSettings");
        }

        for (byte i = 0; i < iface.BNumEndpoints; i++)
        {
            if (!WinUsbNative.WinUsb_QueryPipe(handle, 0, i, out var pipe)) continue;
            var info = new UsbPipe(pipe.PipeId, pipe.PipeType, pipe.MaximumPacketSize, pipe.Interval);
            device._pipes[info.Id] = info;
        }

        foreach (var pipe in device._pipes.Values)
        {
            var timeout = pipe.IsIn ? readTimeoutMs : writeTimeoutMs;
            WinUsbNative.WinUsb_SetPipePolicy(handle, pipe.Id, PipeTransferTimeout, sizeof(uint), ref timeout);
        }

        return device;
    }

    private static ushort ReadBcdDevice(IntPtr handle)
    {
        const int size = 18;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!WinUsbNative.WinUsb_GetDescriptor(handle, 0x01, 0, 0, buffer, size, out _))
                return 0;

            return Marshal.PtrToStructure<WinUsbNative.UsbDeviceDescriptor>(buffer).BcdDevice;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Запись в bulk-OUT пайп срезом по буферу, без копирования.</summary>
    public unsafe uint Write(byte pipeId, byte[] buffer, int offset, int count)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WinUsbDevice));
        if (count <= 0) return 0;

        uint transferred;
        fixed (byte* p = &buffer[offset])
        {
            if (!WinUsbNative.WinUsb_WritePipe(_handle, pipeId, (IntPtr)p, (uint)count, out transferred, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"WinUsb_WritePipe 0x{pipeId:X2}");
        }

        return transferred;
    }

    public unsafe uint Write(byte pipeId, ReadOnlySpan<byte> data)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WinUsbDevice));
        if (data.IsEmpty) return 0;

        uint transferred;
        fixed (byte* p = data)
        {
            if (!WinUsbNative.WinUsb_WritePipe(_handle, pipeId, (IntPtr)p, (uint)data.Length, out transferred, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"WinUsb_WritePipe 0x{pipeId:X2}");
        }

        return transferred;
    }

    /// <summary>Чтение из bulk-IN пайпа. Возвращает -1 при ошибке.</summary>
    public unsafe int Read(byte pipeId, byte[] buffer, int count)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WinUsbDevice));
        if (count <= 0) return 0;

        uint transferred;
        fixed (byte* p = buffer)
        {
            if (!WinUsbNative.WinUsb_ReadPipe(_handle, pipeId, (IntPtr)p, (uint)count, out transferred, IntPtr.Zero))
                return -1;
        }

        return (int)transferred;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_handle != IntPtr.Zero)
        {
            WinUsbNative.WinUsb_Free(_handle);
            _handle = IntPtr.Zero;
        }

        _file.Dispose();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr sa,
        uint disposition, uint flags, IntPtr template);
}
