using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class DiskInterfaceNotifications
{
    private const int DeviceChange = 0x0219;
    private const int DeviceNodesChanged = 0x0007;
    private const int DeviceArrival = 0x8000;
    private const int DeviceRemovalComplete = 0x8004;
    private const int DeviceInterface = 5;
    private const int Volume = 2;
    private static readonly Guid DiskInterface = new Guid("53f56307-b6bf-11d0-94f2-00a0c91efb8b");
    private static readonly int FilterSize = Marshal.SizeOf(typeof(DeviceInterfaceFilter));

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInterfaceFilter
    {
        internal int Size;
        internal int Type;
        internal int Reserved;
        internal Guid InterfaceClass;
        internal short Name;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterDeviceNotification(IntPtr window, ref DeviceInterfaceFilter filter, uint flags);

    [DllImport("user32.dll")]
    internal static extern bool UnregisterDeviceNotification(IntPtr registration);

    internal static IntPtr Register(IntPtr window)
    {
        var filter = new DeviceInterfaceFilter { Size = FilterSize, Type = DeviceInterface, InterfaceClass = DiskInterface };
        return RegisterDeviceNotification(window, ref filter, 0);
    }

    internal static bool IsDiskChange(Message message, bool registered)
    {
        if (message.Msg != DeviceChange) return false;
        var change = message.WParam.ToInt32();
        if (!registered && change == DeviceNodesChanged) return true;
        if (change != DeviceArrival && change != DeviceRemovalComplete) return false;
        var data = message.LParam;
        if (data == IntPtr.Zero || Marshal.ReadInt32(data) < FilterSize || Marshal.ReadInt32(data, sizeof(int)) != DeviceInterface)
            return false;
        var filter = (DeviceInterfaceFilter)Marshal.PtrToStructure(data, typeof(DeviceInterfaceFilter));
        return filter.InterfaceClass == DiskInterface;
    }

    internal static bool IsVolumeChange(Message message)
    {
        if (message.Msg != DeviceChange || message.LParam == IntPtr.Zero) return false;
        var change = message.WParam.ToInt32();
        return (change == DeviceArrival || change == DeviceRemovalComplete) &&
            Marshal.ReadInt32(message.LParam) >= 3 * sizeof(int) &&
            Marshal.ReadInt32(message.LParam, sizeof(int)) == Volume;
    }
}
