using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SensorReadout.CorsairPlugIn
{
    /// <summary>
    /// Identifies one enumerated Corsair HID interface (a hub, PSU, or similar vendor device
    /// exposing a writable HID output report). Fields only, by design, so callers can treat this
    /// as plain data.
    /// </summary>
    public sealed class CorsairHidDeviceInfo
    {
        public string Path;             // \\?\hid#vid_1b1c&pid_0c3f...
        public ushort VendorId;
        public ushort ProductId;
        public int InputReportLength;   // caps.InputReportByteLength (includes report id)
        public int OutputReportLength;  // caps.OutputReportByteLength (includes report id)
        public string Product;          // may be ""
        public string SerialNumber;     // may be ""
    }

    /// <summary>
    /// Enumerates present HID device interfaces belonging to Corsair's vendor ID that expose a
    /// writable output report (hub/PSU control endpoints). Read-only device probing: opens each
    /// candidate with zero access rights just to query attributes/capabilities/strings, never to
    /// read or write report data.
    /// </summary>
    public static class CorsairHidEnumerator
    {
        private const ushort CorsairVendorId = 0x1B1C;
        private const int DigcfPresent = 0x2;
        private const int DigcfDeviceInterface = 0x10;
        private const uint FileShareRead = 0x1;
        private const uint FileShareWrite = 0x2;
        private const uint OpenExisting = 3;
        private const int DetailDataCbSizeX64 = 8;
        private const int HidpStatusSuccess = 0x00110000;
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        // All present HID interfaces with VID 0x1B1C and OutputReportLength > 1.
        public static List<CorsairHidDeviceInfo> FindCorsairDevices(Action<string> logDebug)
        {
            var results = new List<CorsairHidDeviceInfo>();

            Guid hidGuid;
            CorsairNativeMethods.HidD_GetHidGuid(out hidGuid);

            var deviceInfoSet = CorsairNativeMethods.SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
            if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == InvalidHandleValue)
            {
                var enumError = Marshal.GetLastWin32Error();
                Log(logDebug, "Corsair plug-in: SetupDiGetClassDevs failed to enumerate HID device interfaces (error " + enumError.ToString(CultureInfo.InvariantCulture) + ").");
                return results;
            }

            try
            {
                var memberIndex = 0;
                while (true)
                {
                    var interfaceData = new CorsairNativeMethods.SpDeviceInterfaceData();
                    interfaceData.cbSize = Marshal.SizeOf(typeof(CorsairNativeMethods.SpDeviceInterfaceData));

                    if (!CorsairNativeMethods.SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref hidGuid, memberIndex, ref interfaceData))
                    {
                        break;
                    }

                    memberIndex++;

                    string path;
                    if (!TryGetDevicePath(deviceInfoSet, ref interfaceData, out path))
                    {
                        continue;
                    }

                    CorsairHidDeviceInfo deviceInfo;
                    if (TryProbeDevice(path, logDebug, out deviceInfo) && deviceInfo != null)
                    {
                        results.Add(deviceInfo);
                    }
                }
            }
            finally
            {
                CorsairNativeMethods.SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }

            return results;
        }

        private static bool TryGetDevicePath(IntPtr deviceInfoSet, ref CorsairNativeMethods.SpDeviceInterfaceData interfaceData, out string path)
        {
            path = null;

            int requiredSize;
            CorsairNativeMethods.SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, out requiredSize, IntPtr.Zero);
            if (requiredSize <= 0)
            {
                return false;
            }

            var detailBuffer = Marshal.AllocHGlobal(requiredSize);
            try
            {
                // SP_DEVICE_INTERFACE_DETAIL_DATA.cbSize is the size of the fixed part of the
                // struct only (not the variable-length path that follows), which is 8 on x64
                // regardless of the actual allocated buffer size.
                Marshal.WriteInt32(detailBuffer, 0, DetailDataCbSizeX64);

                if (!CorsairNativeMethods.SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, detailBuffer, requiredSize, out requiredSize, IntPtr.Zero))
                {
                    return false;
                }

                path = Marshal.PtrToStringUni(new IntPtr(detailBuffer.ToInt64() + 4));
                return !string.IsNullOrEmpty(path);
            }
            finally
            {
                Marshal.FreeHGlobal(detailBuffer);
            }
        }

        private static bool TryProbeDevice(string path, Action<string> logDebug, out CorsairHidDeviceInfo deviceInfo)
        {
            deviceInfo = null;

            // Access = 0: query-only handle. Lets us read attributes/caps/strings even while
            // another process (e.g. iCUE, Fan Control) holds the device open for read/write.
            var metaHandle = CorsairNativeMethods.CreateFile(path, 0, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (metaHandle == null || metaHandle.IsInvalid)
            {
                if (metaHandle != null)
                {
                    metaHandle.Dispose();
                }

                return false;
            }

            var preparsedData = IntPtr.Zero;
            try
            {
                var attributes = new CorsairNativeMethods.HiddAttributes();
                attributes.Size = Marshal.SizeOf(typeof(CorsairNativeMethods.HiddAttributes));
                if (!CorsairNativeMethods.HidD_GetAttributes(metaHandle, ref attributes))
                {
                    Log(logDebug, "Corsair plug-in: HidD_GetAttributes failed for " + path + ".");
                    return false;
                }

                if (attributes.VendorID != CorsairVendorId)
                {
                    return false;
                }

                if (!CorsairNativeMethods.HidD_GetPreparsedData(metaHandle, out preparsedData) || preparsedData == IntPtr.Zero)
                {
                    Log(logDebug, "Corsair plug-in: HidD_GetPreparsedData failed for " + path + ".");
                    return false;
                }

                var caps = new CorsairNativeMethods.HidpCaps();
                var status = CorsairNativeMethods.HidP_GetCaps(preparsedData, ref caps);
                if (status != HidpStatusSuccess)
                {
                    Log(logDebug, "Corsair plug-in: HidP_GetCaps failed for " + path + " (status 0x" + status.ToString("X8", CultureInfo.InvariantCulture) + ").");
                    return false;
                }

                if (caps.OutputReportByteLength <= 1)
                {
                    return false;
                }

                deviceInfo = new CorsairHidDeviceInfo();
                deviceInfo.Path = path;
                deviceInfo.VendorId = attributes.VendorID;
                deviceInfo.ProductId = attributes.ProductID;
                deviceInfo.InputReportLength = caps.InputReportByteLength;
                deviceInfo.OutputReportLength = caps.OutputReportByteLength;
                deviceInfo.Product = ReadHidString(metaHandle, true);
                deviceInfo.SerialNumber = ReadHidString(metaHandle, false);

                Log(logDebug, "Corsair plug-in: found HID device " + path
                    + " vid=0x" + deviceInfo.VendorId.ToString("X4", CultureInfo.InvariantCulture)
                    + " pid=0x" + deviceInfo.ProductId.ToString("X4", CultureInfo.InvariantCulture)
                    + " in=" + deviceInfo.InputReportLength.ToString(CultureInfo.InvariantCulture)
                    + " out=" + deviceInfo.OutputReportLength.ToString(CultureInfo.InvariantCulture) + ".");
                return true;
            }
            finally
            {
                if (preparsedData != IntPtr.Zero)
                {
                    CorsairNativeMethods.HidD_FreePreparsedData(preparsedData);
                }

                metaHandle.Dispose();
            }
        }

        private static string ReadHidString(SafeFileHandle device, bool isProduct)
        {
            var buffer = new byte[256];
            var ok = isProduct
                ? CorsairNativeMethods.HidD_GetProductString(device, buffer, buffer.Length)
                : CorsairNativeMethods.HidD_GetSerialNumberString(device, buffer, buffer.Length);
            if (!ok)
            {
                return "";
            }

            var text = Encoding.Unicode.GetString(buffer);
            var nullIndex = text.IndexOf('\0');
            return nullIndex >= 0 ? text.Substring(0, nullIndex) : text;
        }

        private static void Log(Action<string> logDebug, string message)
        {
            if (logDebug != null)
            {
                logDebug(message);
            }
        }
    }

    /// <summary>
    /// One overlapped-I/O HID connection to a Corsair device. Each Read/Write is a bounded,
    /// synchronous-from-the-caller's-perspective operation built on an overlapped handle so a
    /// stuck device cannot hang the calling thread past <c>timeoutMs</c>.
    /// </summary>
    public sealed class CorsairHidStream : IDisposable
    {
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareRead = 0x1;
        private const uint FileShareWrite = 0x2;
        private const uint OpenExisting = 3;
        private const uint FileFlagOverlapped = 0x40000000;
        private const int ErrorIoPending = 997;
        private const int ErrorDeviceNotConnected = 1167;
        private const int ErrorNoSuchDevice = 433;
        private const uint WaitObject0 = 0x0;
        private const uint WaitTimeout = 0x102;
        private const int DrainMaxReads = 64;
        private const int DrainBudgetMs = 250;

        private readonly SafeFileHandle handle;
        private readonly CorsairHidDeviceInfo info;
        private bool isDeviceGone;
        private bool disposed;

        private CorsairHidStream(SafeFileHandle handle, CorsairHidDeviceInfo info)
        {
            this.handle = handle;
            this.info = info;
        }

        public CorsairHidDeviceInfo Info { get { return info; } }

        public bool IsDeviceGone { get { return isDeviceGone; } }

        // null on failure
        public static CorsairHidStream Open(CorsairHidDeviceInfo info)
        {
            if (info == null || string.IsNullOrEmpty(info.Path))
            {
                return null;
            }

            var handle = CorsairNativeMethods.CreateFile(
                info.Path,
                GenericRead | GenericWrite,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOverlapped,
                IntPtr.Zero);

            if (handle == null || handle.IsInvalid)
            {
                if (handle != null)
                {
                    handle.Dispose();
                }

                return null;
            }

            return new CorsairHidStream(handle, info);
        }

        // buffer.Length == OutputReportLength
        public bool Write(byte[] buffer, int timeoutMs)
        {
            return ExecuteOverlapped(true, buffer, timeoutMs);
        }

        // buffer.Length == InputReportLength
        public bool Read(byte[] buffer, int timeoutMs)
        {
            return ExecuteOverlapped(false, buffer, timeoutMs);
        }

        public void DrainInput()
        {
            if (info == null || info.InputReportLength <= 0)
            {
                return;
            }

            var buffer = new byte[info.InputReportLength];
            var reads = 0;
            var startTicks = Environment.TickCount;
            while (Read(buffer, 3))
            {
                // Discard stale reports left in the queue by other tools/firmware chatter.
                reads++;
                if (reads >= DrainMaxReads)
                {
                    Trace.WriteLine("Corsair plug-in: DrainInput on " + info.Path + " stopped after "
                        + reads.ToString(CultureInfo.InvariantCulture) + " reads (cap " + DrainMaxReads.ToString(CultureInfo.InvariantCulture)
                        + "); a device may be flooding input reports.");
                    return;
                }

                // unchecked: Environment.TickCount wraps every ~24.9 days, and unchecked
                // subtraction still yields the correct elapsed duration across that wraparound.
                if (unchecked(Environment.TickCount - startTicks) >= DrainBudgetMs)
                {
                    Trace.WriteLine("Corsair plug-in: DrainInput on " + info.Path + " stopped after exceeding its "
                        + DrainBudgetMs.ToString(CultureInfo.InvariantCulture) + " ms budget; a device may be flooding input reports.");
                    return;
                }
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (handle != null)
            {
                handle.Dispose();
            }
        }

        private bool ExecuteOverlapped(bool isWrite, byte[] buffer, int timeoutMs)
        {
            if (buffer == null || buffer.Length == 0 || disposed || info == null)
            {
                return false;
            }

            // Validate against the caps-derived report length up front rather than letting the
            // driver fail the request with ERROR_INVALID_USER_BUFFER and no local diagnostic.
            var expectedLength = isWrite ? info.OutputReportLength : info.InputReportLength;
            if (buffer.Length != expectedLength)
            {
                return false;
            }

            if (timeoutMs < 0)
            {
                // Guard against (uint)(-1) silently becoming INFINITE.
                timeoutMs = 0;
            }

            var eventHandle = CorsairNativeMethods.CreateEvent(IntPtr.Zero, true, false, IntPtr.Zero);
            if (eventHandle == IntPtr.Zero)
            {
                return false;
            }

            // Pin the managed buffer for the full lifetime of the overlapped operation. The
            // interop marshaler would otherwise only pin a byte[] argument for the duration of the
            // P/Invoke call itself, but after ERROR_IO_PENDING the kernel keeps writing to (Read)
            // or reading from (Write) that pre-call address until the operation completes or is
            // cancelled — a compacting GC relocating the array in the meantime would corrupt
            // memory on Read or transmit garbage to the device on Write. GCHandle.Free() below only
            // runs after every GetOverlappedResult/cancel path below has already completed.
            var pinnedBuffer = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var overlapped = new CorsairNativeMethods.NativeOverlapped2();
                overlapped.EventHandle = eventHandle;
                var bufferPtr = pinnedBuffer.AddrOfPinnedObject();

                var completedImmediately = isWrite
                    ? CorsairNativeMethods.WriteFile(handle, bufferPtr, (uint)buffer.Length, IntPtr.Zero, ref overlapped)
                    : CorsairNativeMethods.ReadFile(handle, bufferPtr, (uint)buffer.Length, IntPtr.Zero, ref overlapped);

                if (!completedImmediately)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (IsDeviceGoneError(error))
                    {
                        isDeviceGone = true;
                        return false;
                    }

                    if (error != ErrorIoPending)
                    {
                        // Synchronous failure: nothing was queued, so there is nothing to wait for.
                        return false;
                    }

                    var waitResult = CorsairNativeMethods.WaitForSingleObject(eventHandle, (uint)timeoutMs);
                    if (waitResult == WaitTimeout)
                    {
                        CorsairNativeMethods.CancelIoEx(handle, IntPtr.Zero);
                        uint cancelledBytes;
                        if (!CorsairNativeMethods.GetOverlappedResult(handle, ref overlapped, out cancelledBytes, true))
                        {
                            if (IsDeviceGoneError(Marshal.GetLastWin32Error()))
                            {
                                isDeviceGone = true;
                            }
                        }

                        return false;
                    }

                    if (waitResult != WaitObject0)
                    {
                        // WAIT_FAILED or unexpected result: cancel defensively, then give up.
                        CorsairNativeMethods.CancelIoEx(handle, IntPtr.Zero);
                        uint cancelledBytes;
                        if (!CorsairNativeMethods.GetOverlappedResult(handle, ref overlapped, out cancelledBytes, true))
                        {
                            if (IsDeviceGoneError(Marshal.GetLastWin32Error()))
                            {
                                isDeviceGone = true;
                            }
                        }

                        return false;
                    }
                }

                uint bytesTransferred;
                var ok = CorsairNativeMethods.GetOverlappedResult(handle, ref overlapped, out bytesTransferred, true);
                if (!ok)
                {
                    if (IsDeviceGoneError(Marshal.GetLastWin32Error()))
                    {
                        isDeviceGone = true;
                    }

                    return false;
                }

                return bytesTransferred == (uint)buffer.Length;
            }
            finally
            {
                pinnedBuffer.Free();
                CorsairNativeMethods.CloseHandle(eventHandle);
            }
        }

        private static bool IsDeviceGoneError(int error)
        {
            return error == ErrorDeviceNotConnected || error == ErrorNoSuchDevice;
        }
    }

    /// <summary>
    /// Raw hid.dll / setupapi.dll / kernel32.dll P/Invoke surface used by
    /// <see cref="CorsairHidEnumerator"/> and <see cref="CorsairHidStream"/>. Not for use outside
    /// this file.
    /// </summary>
    internal static class CorsairNativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct HiddAttributes
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HidpCaps
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SpDeviceInterfaceData
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeOverlapped2
        {
            public IntPtr Internal;
            public IntPtr InternalHigh;
            public uint OffsetLow;
            public uint OffsetHigh;
            public IntPtr EventHandle;
        }

        [DllImport("hid.dll")]
        internal static extern void HidD_GetHidGuid(out Guid hidGuid);

        // HidD_* functions return BOOLEAN (1 byte), not Win32 BOOL (4 bytes). Without an explicit
        // U1 marshal, the default 4-byte bool marshaling reads three undefined bytes above EAX
        // along with the real result byte.
        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_GetAttributes(SafeFileHandle device, ref HiddAttributes attributes);

        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr preparsedData);

        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll")]
        internal static extern int HidP_GetCaps(IntPtr preparsedData, ref HidpCaps caps);

        [DllImport("hid.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_GetProductString(SafeFileHandle device, byte[] buffer, int bufferLength);

        [DllImport("hid.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_GetSerialNumberString(SafeFileHandle device, byte[] buffer, int bufferLength);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags); // flags = DIGCF_PRESENT(0x2) | DIGCF_DEVICEINTERFACE(0x10)

        [DllImport("setupapi.dll", SetLastError = true)]
        internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, int memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, out int requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        // IntPtr overloads for the overlapped, GC-relocation-sensitive path: CorsairHidStream pins
        // its buffer with GCHandle and passes the pinned address directly, instead of letting the
        // interop marshaler pin a byte[] only for the duration of the call (which is not long
        // enough once ERROR_IO_PENDING hands the buffer address to the kernel).
        [DllImport("kernel32.dll", EntryPoint = "ReadFile", SetLastError = true)]
        internal static extern bool ReadFile(SafeFileHandle handle, IntPtr buffer, uint bytesToRead, IntPtr bytesRead, ref NativeOverlapped2 overlapped);

        [DllImport("kernel32.dll", EntryPoint = "WriteFile", SetLastError = true)]
        internal static extern bool WriteFile(SafeFileHandle handle, IntPtr buffer, uint bytesToWrite, IntPtr bytesWritten, ref NativeOverlapped2 overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetOverlappedResult(SafeFileHandle handle, ref NativeOverlapped2 overlapped, out uint bytesTransferred, bool wait);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr CreateEvent(IntPtr security, bool manualReset, bool initialState, IntPtr name);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}
