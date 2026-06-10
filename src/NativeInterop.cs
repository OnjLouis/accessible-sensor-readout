using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

internal static class NativeMethods
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = false)]
    public static extern bool IsProcessorFeaturePresent(uint processorFeature);

    [DllImport("kernel32.dll", SetLastError = false)]
    public static extern ulong GetTickCount64();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool Shell_NotifyIcon(uint dwMessage, ref NotifyIconData lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref HighContrast pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref StickyKeys pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref ToggleKeys pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref FilterKeys pvParam, uint fWinIni);

    public const uint NimModify = 0x00000001;
    public const uint NifTip = 0x00000004;
    public const uint SpiGetHighContrast = 0x0042;
    public const uint SpiGetStickyKeys = 0x003A;
    public const uint SpiGetToggleKeys = 0x0034;
    public const uint SpiGetFilterKeys = 0x0032;
    public const uint HcfHighContrastOn = 0x00000001;
    public const uint SkfStickyKeysOn = 0x00000001;
    public const uint TkfToggleKeysOn = 0x00000001;
    public const uint FkfFilterKeysOn = 0x00000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;

        public ulong ToUInt64()
        {
            return ((ulong)HighDateTime << 32) | LowDateTime;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;

        public static MemoryStatusEx Create()
        {
            return new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)) };
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct HighContrast
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr lpszDefaultScheme;

        public static HighContrast Create()
        {
            return new HighContrast { cbSize = (uint)Marshal.SizeOf(typeof(HighContrast)) };
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StickyKeys
    {
        public uint cbSize;
        public uint dwFlags;

        public static StickyKeys Create()
        {
            return new StickyKeys { cbSize = (uint)Marshal.SizeOf(typeof(StickyKeys)) };
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ToggleKeys
    {
        public uint cbSize;
        public uint dwFlags;

        public static ToggleKeys Create()
        {
            return new ToggleKeys { cbSize = (uint)Marshal.SizeOf(typeof(ToggleKeys)) };
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FilterKeys
    {
        public uint cbSize;
        public uint dwFlags;
        public uint iWaitMSec;
        public uint iDelayMSec;
        public uint iRepeatMSec;
        public uint iBounceMSec;

        public static FilterKeys Create()
        {
            return new FilterKeys { cbSize = (uint)Marshal.SizeOf(typeof(FilterKeys)) };
        }
    }
}

internal static class ScreenReaderOutput
{
    private static bool loadAttempted;
    private static bool loaded;
    private static string loadError = "Tolk screen reader library is not loaded.";
    private static readonly object detectLock = new object();
    private static List<string> cachedDetectedScreenReaders = new List<string>();
    private static DateTime cachedDetectedScreenReadersUtc = DateTime.MinValue;
    private static readonly TimeSpan DetectedScreenReadersCacheAge = TimeSpan.FromSeconds(2);
    private static readonly Dictionary<string, string> SupportedScreenReaderProcessNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "nvda", "NVDA" },
        { "jfw", "JAWS" },
        { "narrator", "Narrator" },
        { "supernova", "SuperNova" },
        { "zoomtext", "ZoomText" },
        { "fusion", "Fusion" },
        { "systemaccess", "System Access" }
    };

    [DllImport("Tolk.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern void Tolk_Load();

    [DllImport("Tolk.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool Tolk_IsLoaded();

    [DllImport("Tolk.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern void Tolk_Unload();

    [DllImport("Tolk.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern void Tolk_TrySAPI([MarshalAs(UnmanagedType.I1)] bool trySAPI);

    [DllImport("Tolk.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern void Tolk_PreferSAPI([MarshalAs(UnmanagedType.I1)] bool preferSAPI);

    [DllImport("Tolk.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool Tolk_Output([MarshalAs(UnmanagedType.LPWStr)] string text, [MarshalAs(UnmanagedType.I1)] bool interrupt);

    public static bool TrySpeak(string text, out string error)
    {
        return TryOutput(text, true, out error);
    }

    public static bool TrySpeakPolite(string text, out string error)
    {
        return TryOutput(text, false, out error);
    }

    public static bool TrySpeakForActiveScreenReader(string text, out string error)
    {
        return TryOutputForActiveScreenReader(text, true, out error);
    }

    public static bool TrySpeakPoliteForActiveScreenReader(string text, out string error)
    {
        return TryOutputForActiveScreenReader(text, false, out error);
    }

    public static bool IsAvailable
    {
        get
        {
            string error;
            return EnsureLoaded(out error);
        }
    }

    public static bool IsActiveScreenReaderDetected
    {
        get { return DetectSupportedScreenReaders().Count > 0; }
    }

    public static bool IsActiveScreenReaderOutputAvailable
    {
        get
        {
            string error;
            return IsActiveScreenReaderDetected && EnsureLoaded(out error);
        }
    }

    public static List<string> DetectSupportedScreenReaders()
    {
        lock (detectLock)
        {
            if ((DateTime.UtcNow - cachedDetectedScreenReadersUtc) <= DetectedScreenReadersCacheAge)
            {
                return new List<string>(cachedDetectedScreenReaders);
            }
        }

        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    string label;
                    if (SupportedScreenReaderProcessNames.TryGetValue(process.ProcessName ?? "", out label) && !string.IsNullOrWhiteSpace(label))
                    {
                        found.Add(label);
                    }
                }
            }
        }
        catch
        {
        }

        var result = found.ToList();
        lock (detectLock)
        {
            cachedDetectedScreenReaders = new List<string>(result);
            cachedDetectedScreenReadersUtc = DateTime.UtcNow;
        }

        return result;
    }

    public static void Shutdown()
    {
        if (!loaded)
        {
            return;
        }

        try
        {
            Tolk_Unload();
        }
        catch
        {
        }

        loaded = false;
    }

    private static bool TryOutputForActiveScreenReader(string text, bool interrupt, out string error)
    {
        if (!IsActiveScreenReaderDetected)
        {
            error = "No supported screen reader process is active.";
            return false;
        }

        return TryOutput(text, interrupt, out error);
    }

    private static bool TryOutput(string text, bool interrupt, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            text = "Sensor Readout";
        }

        if (!EnsureLoaded(out error))
        {
            return false;
        }

        try
        {
            if (!Tolk_Output(text, interrupt))
            {
                error = "No supported screen reader or SAPI voice accepted the message.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool EnsureLoaded(out string error)
    {
        if (loaded)
        {
            error = "";
            return true;
        }

        if (loadAttempted)
        {
            error = loadError;
            return false;
        }

        loadAttempted = true;

        try
        {
            Tolk_TrySAPI(true);
            Tolk_PreferSAPI(false);
            Tolk_Load();
            loaded = Tolk_IsLoaded();
            if (loaded)
            {
                error = "";
                return true;
            }
        }
        catch (DllNotFoundException)
        {
            loadError = "Place Tolk.dll beside Sensor Readout.exe to enable screen-reader speech.";
        }
        catch (BadImageFormatException)
        {
            loadError = "The Tolk.dll beside Sensor Readout.exe is not a compatible 64-bit build.";
        }
        catch (Exception ex)
        {
            loadError = ex.Message;
        }

        error = loadError;
        return false;
    }
}
