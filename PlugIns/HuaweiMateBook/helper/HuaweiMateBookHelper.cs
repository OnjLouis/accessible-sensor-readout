using System;
using System.IO;
using System.Runtime.InteropServices;

internal static class HuaweiMateBookHelper
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string lpPathName);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("HardwareSdk.dll", EntryPoint = "??0CHotInterface@@QEAA@XZ", CallingConvention = CallingConvention.ThisCall)]
    private static extern IntPtr CHotInterfaceCtor(IntPtr self);

    [DllImport("HardwareSdk.dll", EntryPoint = "?GetFan0Speed@CHotInterface@@QEAAIXZ", CallingConvention = CallingConvention.ThisCall)]
    private static extern uint GetFan0Speed(IntPtr self);

    [DllImport("HardwareSdk.dll", EntryPoint = "?GetFan1Speed@CHotInterface@@QEAAIXZ", CallingConvention = CallingConvention.ThisCall)]
    private static extern uint GetFan1Speed(IntPtr self);

    [DllImport("HardwareSdk.dll", EntryPoint = "?GetSensorTemperature@CHotInterface@@QEAAH_K@Z", CallingConvention = CallingConvention.ThisCall)]
    private static extern int GetSensorTemperature(IntPtr self, ulong sensor);

    public static int Main()
    {
        try
        {
            var sdkDirectory = FindSdkDirectory();
            if (string.IsNullOrWhiteSpace(sdkDirectory))
            {
                Console.WriteLine("STATUS=SDK_NOT_FOUND");
                return 3;
            }

            Console.WriteLine("SDK=" + sdkDirectory);
            SetDllDirectory(sdkDirectory);

            var self = Marshal.AllocHGlobal(256);
            for (var i = 0; i < 256; i++)
            {
                Marshal.WriteByte(self, i, 0);
            }

            try
            {
                CHotInterfaceCtor(self);
                Console.WriteLine("FAN0=" + GetFan0Speed(self).ToString());
                Console.WriteLine("FAN1=" + GetFan1Speed(self).ToString());
                for (ulong sensor = 0; sensor < 16; sensor++)
                {
                    var value = GetSensorTemperature(self, sensor);
                    if (value > 0 && value < 150)
                    {
                        Console.WriteLine("TEMP" + sensor.ToString() + "=" + value.ToString());
                    }
                }
                Console.WriteLine("STATUS=OK");
                Console.Out.Flush();
                Console.Error.Flush();

                // Huawei's C++ teardown path can leave the helper alive on some systems.
                // This helper is intentionally one-shot, so terminate only this helper after flushing output.
                TerminateProcess(GetCurrentProcess(), 0);
                return 0;
            }
            finally
            {
                Marshal.FreeHGlobal(self);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("STATUS=ERROR");
            Console.WriteLine("ERROR=" + ex.GetType().Name + ": " + ex.Message);
            return 2;
        }
    }

    private static string FindSdkDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Huawei", "PCManager"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Huawei", "PCManager")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, "HardwareSdk.dll")))
            {
                return candidate;
            }
        }

        return "";
    }
}
