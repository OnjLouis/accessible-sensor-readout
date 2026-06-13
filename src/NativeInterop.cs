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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetDllDirectory(string lpPathName);

    [DllImport("ntdll.dll", SetLastError = false)]
    public static extern int RtlGetVersion(ref OsVersionInfo versionInfo);

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct OsVersionInfo
    {
        public uint dwOSVersionInfoSize;
        public uint dwMajorVersion;
        public uint dwMinorVersion;
        public uint dwBuildNumber;
        public uint dwPlatformId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szCSDVersion;

        public static OsVersionInfo Create()
        {
            return new OsVersionInfo { dwOSVersionInfoSize = (uint)Marshal.SizeOf(typeof(OsVersionInfo)) };
        }
    }
}

internal static class ScreenReaderOutput
{
    private static bool loadAttempted;
    private static bool loaded;
    private static ISpeechBackend activeBackend;
    private static string loadError = "No screen reader speech backend is loaded.";
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

    public static string ActiveBackendName
    {
        get
        {
            return loaded && activeBackend != null ? activeBackend.Name : "";
        }
    }

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
        if (!loaded && activeBackend == null)
        {
            return;
        }

        try
        {
            if (activeBackend != null)
            {
                activeBackend.Shutdown();
            }
        }
        catch
        {
        }

        activeBackend = null;
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
            if (activeBackend == null || !activeBackend.Output(text, interrupt, out error))
            {
                if (activeBackend is PrismSpeechBackend && TrySwitchToTolk(text, interrupt, out error))
                {
                    return true;
                }

                if (string.IsNullOrWhiteSpace(error))
                {
                    error = "No supported screen reader or SAPI voice accepted the message.";
                }

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

    private static bool TrySwitchToTolk(string text, bool interrupt, out string error)
    {
        error = "";
        try
        {
            string tolkError;
            var tolk = TryLoadTolk(out tolkError);
            if (tolk == null)
            {
                error = tolkError;
                return false;
            }

            if (activeBackend != null)
            {
                activeBackend.Shutdown();
            }

            activeBackend = tolk;
            loaded = true;
            return activeBackend.Output(text, interrupt, out error);
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

        string prismError;
        var prism = TryLoadPrism(out prismError);
        if (prism != null)
        {
            activeBackend = prism;
            loaded = true;
            error = "";
            return true;
        }

        string tolkError;
        var tolk = TryLoadTolk(out tolkError);
        if (tolk != null)
        {
            activeBackend = tolk;
            loaded = true;
            error = "";
            return true;
        }

        loadError = FirstNonEmpty(prismError, tolkError, "No screen reader speech backend is available.");
        error = loadError;
        return false;
    }

    private static ISpeechBackend TryLoadTolk(out string error)
    {
        try
        {
            Tolk_TrySAPI(true);
            Tolk_PreferSAPI(false);
            Tolk_Load();
            if (Tolk_IsLoaded())
            {
                error = "";
                return new TolkSpeechBackend();
            }
        }
        catch (DllNotFoundException)
        {
            loadError = "Place Tolk.dll in the Resources folder to enable screen-reader speech.";
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
        return null;
    }

    private static ISpeechBackend TryLoadPrism(out string error)
    {
        error = "";
        if (!IsWindows10OrLater())
        {
            error = "Prism requires Windows 10 or later.";
            return null;
        }

        PrismSpeechBackend backend = null;
        try
        {
            backend = PrismSpeechBackend.TryCreate(DetectSupportedScreenReaders(), out error);
            return backend;
        }
        catch (DllNotFoundException)
        {
            error = "Place prism.dll in the Resources folder to enable Prism screen-reader speech.";
        }
        catch (BadImageFormatException)
        {
            error = "The prism.dll beside Sensor Readout.exe is not a compatible 64-bit build.";
        }
        catch (EntryPointNotFoundException ex)
        {
            error = "The prism.dll beside Sensor Readout.exe is not compatible with this Sensor Readout build: " + ex.Message;
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (backend != null)
        {
            backend.Shutdown();
        }

        return null;
    }

    private static bool IsWindows10OrLater()
    {
        try
        {
            var version = NativeMethods.OsVersionInfo.Create();
            if (NativeMethods.RtlGetVersion(ref version) == 0)
            {
                return version.dwMajorVersion >= 10;
            }
        }
        catch
        {
        }

        try
        {
            return Environment.OSVersion.Version.Major >= 10;
        }
        catch
        {
            return false;
        }
    }

    private static string FirstNonEmpty(params string[] values)
    {
        if (values == null)
        {
            return "";
        }

        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return "";
    }

    private interface ISpeechBackend
    {
        string Name { get; }
        bool Output(string text, bool interrupt, out string error);
        void Shutdown();
    }

    private sealed class TolkSpeechBackend : ISpeechBackend
    {
        public string Name { get { return "Tolk"; } }

        public bool Output(string text, bool interrupt, out string error)
        {
            if (!Tolk_Output(text, interrupt))
            {
                error = "No supported screen reader or SAPI voice accepted the message.";
                return false;
            }

            error = "";
            return true;
        }

        public void Shutdown()
        {
            Tolk_Unload();
        }
    }

    private sealed class PrismSpeechBackend : ISpeechBackend
    {
        private const ulong PrismBackendSapi = 0x1D6DF72422CEEE66;
        private const ulong PrismBackendNvda = 0x89CC19C5C4AC1A56;
        private const ulong PrismBackendJaws = 0xAC3D60E9BD84B53E;
        private const ulong PrismBackendOneCore = 0x6797D32F0D994CB4;
        private const ulong PrismBackendUia = 0x6238F019DB678F8E;
        private const ulong PrismBackendZoomText = 0xAE439D62DC7B1479;
        private const ulong PrismBackendSystemAccess = 0x8380F2A37B2C3EB6;

        private readonly IntPtr context;
        private readonly IntPtr backend;
        private readonly string backendName;
        private bool disposed;

        private PrismSpeechBackend(IntPtr context, IntPtr backend, string backendName)
        {
            this.context = context;
            this.backend = backend;
            this.backendName = string.IsNullOrWhiteSpace(backendName) ? "Prism" : "Prism " + backendName;
        }

        public string Name { get { return backendName; } }

        [StructLayout(LayoutKind.Sequential)]
        private struct PrismConfig
        {
            public byte version;
        }

        [DllImport("prism.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern PrismConfig prism_config_init();

        [DllImport("prism.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr prism_init(ref PrismConfig cfg);

        [DllImport("prism.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void prism_shutdown(IntPtr ctx);

        [DllImport("prism.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr prism_registry_create(IntPtr ctx, ulong id);

        [DllImport("prism.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int prism_backend_initialize(IntPtr backend);

        [DllImport("prism.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr prism_backend_name(IntPtr backend);

        [DllImport("prism.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void prism_backend_free(IntPtr backend);

        [DllImport("prism.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int prism_backend_output(IntPtr backend, byte[] text, [MarshalAs(UnmanagedType.I1)] bool interrupt);

        [DllImport("prism.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr prism_error_string(int error);

        public static PrismSpeechBackend TryCreate(IEnumerable<string> detectedScreenReaders, out string error)
        {
            error = "";
            var config = prism_config_init();
            var context = prism_init(ref config);
            if (context == IntPtr.Zero)
            {
                error = "Prism could not initialize.";
                return null;
            }

            var errors = new List<string>();
            foreach (var id in PreferredBackendIds(detectedScreenReaders))
            {
                var backend = IntPtr.Zero;
                try
                {
                    backend = prism_registry_create(context, id);
                    if (backend == IntPtr.Zero)
                    {
                        errors.Add("backend " + id.ToString("X16") + " was not available");
                        continue;
                    }

                    var initError = prism_backend_initialize(backend);
                    if (initError == 0)
                    {
                        return new PrismSpeechBackend(context, backend, PtrToUtf8(prism_backend_name(backend)));
                    }

                    errors.Add(PtrToUtf8(prism_error_string(initError)));
                }
                catch (Exception ex)
                {
                    errors.Add(ex.Message);
                }

                if (backend != IntPtr.Zero)
                {
                    prism_backend_free(backend);
                }
            }

            prism_shutdown(context);
            error = "Prism did not initialize a usable backend." + (errors.Count == 0 ? "" : " " + string.Join("; ", errors.Distinct().ToArray()));
            return null;
        }

        public bool Output(string text, bool interrupt, out string error)
        {
            var result = prism_backend_output(backend, Utf8NullTerminated(text), interrupt);
            if (result == 0)
            {
                error = "";
                return true;
            }

            error = PtrToUtf8(prism_error_string(result));
            return false;
        }

        public void Shutdown()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (backend != IntPtr.Zero)
            {
                prism_backend_free(backend);
            }

            if (context != IntPtr.Zero)
            {
                prism_shutdown(context);
            }
        }

        private static IEnumerable<ulong> PreferredBackendIds(IEnumerable<string> detectedScreenReaders)
        {
            var ids = new List<ulong>();
            if (detectedScreenReaders != null)
            {
                foreach (var reader in detectedScreenReaders)
                {
                    if (string.Equals(reader, "NVDA", StringComparison.OrdinalIgnoreCase)) AddUnique(ids, PrismBackendNvda);
                    else if (string.Equals(reader, "JAWS", StringComparison.OrdinalIgnoreCase)) AddUnique(ids, PrismBackendJaws);
                    else if (string.Equals(reader, "ZoomText", StringComparison.OrdinalIgnoreCase) || string.Equals(reader, "Fusion", StringComparison.OrdinalIgnoreCase)) AddUnique(ids, PrismBackendZoomText);
                    else if (string.Equals(reader, "System Access", StringComparison.OrdinalIgnoreCase)) AddUnique(ids, PrismBackendSystemAccess);
                    else if (string.Equals(reader, "Narrator", StringComparison.OrdinalIgnoreCase)) AddUnique(ids, PrismBackendUia);
                }
            }

            AddUnique(ids, PrismBackendNvda);
            AddUnique(ids, PrismBackendJaws);
            AddUnique(ids, PrismBackendUia);
            AddUnique(ids, PrismBackendOneCore);
            AddUnique(ids, PrismBackendSapi);
            return ids;
        }

        private static void AddUnique(List<ulong> ids, ulong id)
        {
            if (!ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        private static byte[] Utf8NullTerminated(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                text = "Sensor Readout";
            }

            return System.Text.Encoding.UTF8.GetBytes(text + "\0");
        }

        private static string PtrToUtf8(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero)
            {
                return "";
            }

            var bytes = new List<byte>();
            var offset = 0;
            while (true)
            {
                var value = Marshal.ReadByte(ptr, offset++);
                if (value == 0)
                {
                    break;
                }

                bytes.Add(value);
            }

            return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
        }
    }
}
