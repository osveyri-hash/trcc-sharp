using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TrccSharp.Core.WinUsb;

/// <summary>
/// P/Invoke над setupapi.dll + winusb.dll.
/// Устройства Thermalright на интерфейсе MI_01 сидят на штатном WINUSB-драйвере,
/// поэтому libusb/Zadig не нужны: WinUSB API работает поверх in-box драйвера.
/// </summary>
internal static class WinUsbNative
{
    internal const int DigcfPresent = 0x02;
    internal const int DigcfDeviceInterface = 0x10;

    /// <summary>GUID_DEVINTERFACE_USB_DEVICE — стандартный интерфейс USB-устройства.</summary>
    internal static readonly Guid UsbDeviceInterfaceGuid =
        new("A5DCBF10-6530-11D2-901F-00C04FB951ED");

    // Политики пайпов (winusb.h)
    internal const uint PipeTransferTimeout = 0x03;

    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint OpenExisting = 3;
    internal const uint FileAttributeNormal = 0x00000080;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpDeviceInterfaceData
    {
        public int CbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UsbInterfaceDescriptor
    {
        public byte BLength;
        public byte BDescriptorType;
        public byte BInterfaceNumber;
        public byte BAlternateSetting;
        public byte BNumEndpoints;
        public byte BInterfaceClass;
        public byte BInterfaceSubClass;
        public byte BInterfaceProtocol;
        public byte IInterface;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WinUsbPipeInformation
    {
        public int PipeType;
        public byte PipeId;
        public ushort MaximumPacketSize;
        public byte Interval;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UsbDeviceDescriptor
    {
        public byte BLength;
        public byte BDescriptorType;
        public ushort BcdUsb;
        public byte BDeviceClass;
        public byte BDeviceSubClass;
        public byte BDeviceProtocol;
        public byte BMaxPacketSize0;
        public ushort IdVendor;
        public ushort IdProduct;
        public ushort BcdDevice;
        public byte IManufacturer;
        public byte IProduct;
        public byte ISerialNumber;
        public byte BNumConfigurations;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr devInfo, IntPtr devInfoData, ref Guid classGuid, int memberIndex, ref SpDeviceInterfaceData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devInfo, ref SpDeviceInterfaceData data, IntPtr detail, int detailSize, out int requiredSize, IntPtr devInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr devInfo);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsb_Initialize(SafeFileHandle file, out IntPtr handle);

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsb_Free(IntPtr handle);

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsb_QueryInterfaceSettings(IntPtr handle, byte altSetting, out UsbInterfaceDescriptor descriptor);

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsb_QueryPipe(IntPtr handle, byte altSetting, byte pipeIndex, out WinUsbPipeInformation pipe);

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsb_WritePipe(IntPtr handle, byte pipeId, IntPtr buffer, uint length, out uint transferred, IntPtr overlapped);

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsb_ReadPipe(IntPtr handle, byte pipeId, IntPtr buffer, uint length, out uint transferred, IntPtr overlapped);

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsb_SetPipePolicy(IntPtr handle, byte pipeId, uint policyType, uint valueLength, ref uint value);

    [DllImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WinUsb_GetDescriptor(IntPtr handle, byte descriptorType, byte index, ushort languageId, IntPtr buffer, uint length, out uint transferred);
}
