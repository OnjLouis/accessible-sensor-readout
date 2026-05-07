using System;
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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);
}

internal static class NvdaController
{
    private delegate int NvdaTestIfRunningDelegate();
    private delegate int NvdaSpeakTextDelegate([MarshalAs(UnmanagedType.LPWStr)] string text);
    private delegate int NvdaSpeakSsmlDelegate(
        [MarshalAs(UnmanagedType.LPWStr)] string ssml,
        int symbolLevel,
        int priority,
        [MarshalAs(UnmanagedType.I1)] bool asynchronous);

    private static NvdaTestIfRunningDelegate testIfRunning;
    private static NvdaSpeakTextDelegate speakText;
    private static NvdaSpeakSsmlDelegate speakSsml;
    private static bool loadAttempted;
    private static string loadError = "NVDA controller client DLL not loaded.";

    public static bool TrySpeak(string text, out string error)
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
            var running = testIfRunning == null ? 0 : testIfRunning();
            if (running != 0)
            {
                error = "NVDA does not appear to be running.";
                return false;
            }

            var result = speakText(text);
            if (result != 0)
            {
                error = "NVDA returned error code " + result + ".";
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

    public static bool TrySpeakPolite(string text, out string error)
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

        if (speakSsml == null)
        {
            return TrySpeak(text, out error);
        }

        try
        {
            var running = testIfRunning == null ? 0 : testIfRunning();
            if (running != 0)
            {
                error = "NVDA does not appear to be running.";
                return false;
            }

            const int symbolLevelUnchanged = -1;
            const int speechPriorityNormal = 0;
            var result = speakSsml("<speak>" + EscapeSsml(text) + "</speak>", symbolLevelUnchanged, speechPriorityNormal, true);
            if (result != 0)
            {
                error = "NVDA returned error code " + result + ".";
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
        if (speakText != null)
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
        var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            System.IO.Path.Combine(baseDirectory, "nvdaControllerClient.dll"),
            System.IO.Path.Combine(baseDirectory, "nvdaControllerClient64.dll")
        };

        foreach (var candidate in candidates)
        {
            if (!System.IO.File.Exists(candidate))
            {
                continue;
            }

            var module = NativeMethods.LoadLibrary(candidate);
            if (module == IntPtr.Zero)
            {
                continue;
            }

            var speakPointer = NativeMethods.GetProcAddress(module, "nvdaController_speakText");
            var speakSsmlPointer = NativeMethods.GetProcAddress(module, "nvdaController_speakSsml");
            var testPointer = NativeMethods.GetProcAddress(module, "nvdaController_testIfRunning");
            if (speakPointer == IntPtr.Zero)
            {
                continue;
            }

            speakText = (NvdaSpeakTextDelegate)Marshal.GetDelegateForFunctionPointer(speakPointer, typeof(NvdaSpeakTextDelegate));
            if (speakSsmlPointer != IntPtr.Zero)
            {
                speakSsml = (NvdaSpeakSsmlDelegate)Marshal.GetDelegateForFunctionPointer(speakSsmlPointer, typeof(NvdaSpeakSsmlDelegate));
            }
            if (testPointer != IntPtr.Zero)
            {
                testIfRunning = (NvdaTestIfRunningDelegate)Marshal.GetDelegateForFunctionPointer(testPointer, typeof(NvdaTestIfRunningDelegate));
            }

            error = "";
            return true;
        }

        loadError = "Place nvdaControllerClient.dll beside Sensor Readout.exe to enable NVDA speech.";
        error = loadError;
        return false;
    }

    private static string EscapeSsml(string text)
    {
        return (text ?? "")
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }
}
