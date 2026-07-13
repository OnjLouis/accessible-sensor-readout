using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using SensorReadout.PluginSdk;

namespace SensorReadout.HuaweiMateBookPlugIn
{
    public sealed class HuaweiMateBookPlugIn : ISensorReadoutPlugin
    {
        private const string SourceName = "Huawei MateBook Support Plug-In";
        private readonly PluginInfo info = new PluginInfo
        {
            Id = "sensorreadout.huawei.matebook.experimental",
            Name = "Huawei MateBook Support (experimental)",
            Version = "0.1.0",
            Author = "Sensor Readout",
            Description = "Experimental, read-only Huawei MateBook fan probe using Huawei PC Manager's local hardware SDK helper when available."
        };

        private DateTime cachedRowsUtc = DateTime.MinValue;
        private List<SensorReading> cachedRows = new List<SensorReading>();

        public PluginInfo Info { get { return info; } }

        public IEnumerable<SensorReading> GetReadings(IPluginContext context)
        {
            if (!IsHuaweiComputer(context))
            {
                return Enumerable.Empty<SensorReading>();
            }

            var interval = context != null && context.DiagnosticsMode
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds(30);
            if (cachedRows.Count > 0 && DateTime.UtcNow - cachedRowsUtc < interval)
            {
                return cachedRows.Select(CloneReading).ToList();
            }

            var rows = Probe(context).ToList();
            cachedRows = rows.Select(CloneReading).ToList();
            cachedRowsUtc = DateTime.UtcNow;
            return rows;
        }

        private static bool IsHuaweiComputer(IPluginContext context)
        {
            var machine = context == null ? null : context.Machine;
            var manufacturer = machine == null ? "" : machine.Manufacturer ?? "";
            var model = machine == null ? "" : machine.Model ?? "";
            return ContainsAny(manufacturer, "Huawei", "Honor")
                || ContainsAny(model, "Huawei", "Honor", "MateBook");
        }

        private static IEnumerable<SensorReading> Probe(IPluginContext context)
        {
            var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var result = RunHelper(context, details);

            var fanRows = new List<SensorReading>();
            AddFanRow(fanRows, result, 0, "Fan 1");
            AddFanRow(fanRows, result, 1, "Fan 2");
            if (fanRows.Count > 0)
            {
                foreach (var row in fanRows)
                {
                    foreach (var detail in details)
                    {
                        row.Details[detail.Key] = detail.Value;
                    }
                }

                return fanRows;
            }

            var status = result.Status;
            if (string.IsNullOrWhiteSpace(status))
            {
                status = "Huawei fan speed not exposed";
            }

            return new[]
            {
                new SensorReading
                {
                    Type = "Performance",
                    Hardware = "Overview",
                    Name = "Huawei Plug-In",
                    Identifier = StableIdentifier("huawei/status"),
                    DisplayValue = status,
                    Source = SourceName,
                    Details = details
                }
            };
        }

        private static void AddFanRow(List<SensorReading> rows, HelperResult result, int index, string name)
        {
            uint speed;
            if (!result.Fans.TryGetValue(index, out speed) || speed == 0 || speed >= 30000)
            {
                return;
            }

            var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Mode", "Read-only Huawei PC Manager helper" },
                { "Huawei fan index", index.ToString(CultureInfo.InvariantCulture) }
            };
            foreach (var temperature in result.Temperatures.OrderBy(pair => pair.Key))
            {
                details["Huawei temperature sensor " + temperature.Key.ToString(CultureInfo.InvariantCulture)] =
                    temperature.Value.ToString(CultureInfo.InvariantCulture) + " C";
            }

            rows.Add(new SensorReading
            {
                Type = "Fan",
                Hardware = "Huawei MateBook",
                Name = name,
                Identifier = StableIdentifier("huawei/fan/" + index.ToString(CultureInfo.InvariantCulture)),
                Value = speed,
                DisplayValue = speed.ToString(CultureInfo.InvariantCulture) + " RPM",
                Source = SourceName,
                Details = details
            });
        }

        private static HelperResult RunHelper(IPluginContext context, Dictionary<string, string> details)
        {
            var result = new HelperResult { Status = "Huawei fan helper not run" };
            var pluginDirectory = context == null ? "" : context.PluginDirectory ?? "";
            var helperPath = Path.Combine(pluginDirectory, "HuaweiMateBookHelper.exe");
            details["Mode"] = "Read-only experimental probe";
            details["Helper path"] = helperPath;
            if (!File.Exists(helperPath))
            {
                result.Status = "Huawei fan helper not found";
                details["Helper status"] = result.Status;
                return result;
            }

            try
            {
                using (var process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = helperPath,
                        WorkingDirectory = pluginDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    process.Start();
                    if (!process.WaitForExit(3000))
                    {
                        TryKill(process);
                        result.Status = "Huawei fan helper timed out";
                        details["Helper status"] = result.Status;
                    }
                    else
                    {
                        details["Helper exit code"] = process.ExitCode.ToString(CultureInfo.InvariantCulture);
                    }

                    var output = process.StandardOutput.ReadToEnd();
                    var error = process.StandardError.ReadToEnd();
                    ParseHelperOutput(output, result, details);
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        details["Helper error output"] = error.Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                result.Status = "Huawei fan helper failed";
                details["Helper status"] = result.Status;
                details["Helper error"] = ex.Message;
                if (context != null)
                {
                    context.Log("Debug", "Huawei fan helper failed: " + ex.Message);
                }
            }

            if (result.Fans.Count == 0 && string.Equals(result.Status, "OK", StringComparison.OrdinalIgnoreCase))
            {
                result.Status = "Huawei fan helper ran, but no live fan speed was returned";
            }

            details["Helper result"] = result.Status;
            if (result.Fans.Count > 0)
            {
                details["Live fans"] = result.Fans.Count.ToString(CultureInfo.InvariantCulture);
            }

            return result;
        }

        private static void ParseHelperOutput(string output, HelperResult result, Dictionary<string, string> details)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return;
            }

            details["Helper output"] = output.Trim();
            using (var reader = new StringReader(output))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var equals = line.IndexOf('=');
                    if (equals <= 0)
                    {
                        continue;
                    }

                    var key = line.Substring(0, equals).Trim();
                    var value = line.Substring(equals + 1).Trim();
                    if (key.Equals("STATUS", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Status = value;
                    }
                    else if (key.Equals("SDK", StringComparison.OrdinalIgnoreCase))
                    {
                        details["Huawei SDK folder"] = value;
                    }
                    else if (key.StartsWith("FAN", StringComparison.OrdinalIgnoreCase))
                    {
                        int index;
                        uint speed;
                        if (int.TryParse(key.Substring(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out index) &&
                            uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out speed))
                        {
                            result.Fans[index] = speed;
                        }
                    }
                    else if (key.StartsWith("TEMP", StringComparison.OrdinalIgnoreCase))
                    {
                        int index;
                        int temperature;
                        if (int.TryParse(key.Substring(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out index) &&
                            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out temperature))
                        {
                            result.Temperatures[index] = temperature;
                        }
                    }
                    else if (key.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
                    {
                        details["Helper reported error"] = value;
                    }
                }
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (process != null && !process.HasExited)
                {
                    process.Kill();
                }
            }
            catch
            {
            }
        }

        private static bool ContainsAny(string value, params string[] needles)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            foreach (var needle in needles)
            {
                if (value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string StableIdentifier(string value)
        {
            return "plugin/" + value.Trim().ToLowerInvariant().Replace('\\', '/').Replace(' ', '-');
        }

        private static SensorReading CloneReading(SensorReading reading)
        {
            return new SensorReading
            {
                Type = reading.Type,
                Hardware = reading.Hardware,
                Name = reading.Name,
                Identifier = reading.Identifier,
                Value = reading.Value,
                DisplayValue = reading.DisplayValue,
                Source = reading.Source,
                Details = reading.Details == null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(reading.Details, StringComparer.OrdinalIgnoreCase)
            };
        }

        private sealed class HelperResult
        {
            public string Status = "";
            public readonly Dictionary<int, uint> Fans = new Dictionary<int, uint>();
            public readonly Dictionary<int, int> Temperatures = new Dictionary<int, int>();
        }
    }
}
