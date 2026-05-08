using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;

public sealed partial class SensorReadoutForm : Form
{
    private DateTime frameworkEcRowsUtc = DateTime.MinValue;
    private List<SensorRow> cachedFrameworkEcRows = new List<SensorRow>();
    private string cachedFrameworkEcPath = "";
    private DateTime frameworkControlApiRetryUtc = DateTime.MinValue;
    private bool frameworkControlApiAvailable;

    private IEnumerable<SensorRow> GetFrameworkEcRows()
    {
        var apiRows = GetFrameworkControlApiRows().ToList();
        if (apiRows.Count > 0)
        {
            return apiRows;
        }

        var toolPath = FindFrameworkEcTool();
        if (string.IsNullOrWhiteSpace(toolPath))
        {
            if (IsFrameworkComputer())
            {
                return new[]
                {
                    new SensorRow
                    {
                        Type = "Performance",
                        Hardware = "Overview",
                        Name = "Framework EC helper",
                        DisplayValue = "ectool.exe not found",
                        Source = "Framework EC"
                    }
                };
            }

            return Enumerable.Empty<SensorRow>();
        }

        lock (cachedFrameworkEcRows)
        {
            if (string.Equals(cachedFrameworkEcPath, toolPath, StringComparison.OrdinalIgnoreCase) &&
                cachedFrameworkEcRows.Count > 0 &&
                DateTime.UtcNow - frameworkEcRowsUtc < TimeSpan.FromSeconds(5))
            {
                return cachedFrameworkEcRows.Select(CloneSensorRow).ToList();
            }
        }

        var rows = new List<SensorRow>();
        var thermalGet = RunFrameworkEcTool(toolPath, "thermalget");
        var sensorNames = ParseFrameworkThermalNames(thermalGet.Output);
        var temps = RunFrameworkEcTool(toolPath, "temps all");
        var fanResults = new[]
        {
            RunFrameworkEcTool(toolPath, "pwmgetfanrpm"),
            RunFrameworkEcTool(toolPath, "pwmgetfanrpm all"),
            RunFrameworkEcTool(toolPath, "pwmgetfanrpm 0"),
            RunFrameworkEcTool(toolPath, "pwmgetfanrpm 1"),
            RunFrameworkEcTool(toolPath, "pwmgetfanrpm 2"),
            RunFrameworkEcTool(toolPath, "pwmgetfanrpm 3")
        };

        if (!string.IsNullOrWhiteSpace(thermalGet.Output))
        {
            LogFrameworkEcOutput("thermalget", thermalGet);
        }
        LogFrameworkEcOutput("temps all", temps);
        foreach (var fanResult in fanResults)
        {
            LogFrameworkEcOutput(fanResult.Arguments, fanResult);
        }

        rows.AddRange(ParseFrameworkTemperatureRows(temps.Output, sensorNames));
        rows.AddRange(ParseFrameworkFanRows(fanResults));

        if (rows.Count == 0)
        {
            rows.Add(new SensorRow
            {
                Type = "Performance",
                Hardware = "Overview",
                Name = "Framework EC helper",
                DisplayValue = "ectool.exe found, but no parseable EC sensor output",
                Source = "Framework EC"
            });
        }

        lock (cachedFrameworkEcRows)
        {
            cachedFrameworkEcPath = toolPath;
            cachedFrameworkEcRows = rows.Select(CloneSensorRow).ToList();
            frameworkEcRowsUtc = DateTime.UtcNow;
        }

        return rows;
    }

    private IEnumerable<SensorRow> GetFrameworkControlApiRows()
    {
        if (!frameworkControlApiAvailable && DateTime.UtcNow < frameworkControlApiRetryUtc)
        {
            return Enumerable.Empty<SensorRow>();
        }

        foreach (var baseUrl in GetFrameworkControlApiBaseUrls())
        {
            var rows = TryGetFrameworkControlThermalRows(baseUrl).ToList();
            if (rows.Count > 0)
            {
                frameworkControlApiAvailable = true;
                frameworkControlApiRetryUtc = DateTime.MinValue;
                return rows;
            }
        }

        frameworkControlApiAvailable = false;
        frameworkControlApiRetryUtc = DateTime.UtcNow.AddSeconds(60);
        return Enumerable.Empty<SensorRow>();
    }

    private static IEnumerable<string> GetFrameworkControlApiBaseUrls()
    {
        var envPort = Environment.GetEnvironmentVariable("FRAMEWORK_CONTROL_PORT");
        var urls = new List<string> { "http://127.0.0.1/api" };
        var ports = new List<string>();
        if (!string.IsNullOrWhiteSpace(envPort))
        {
            ports.Add(envPort.Trim());
        }

        ports.AddRange(new[] { "30912", "8090", "8091", "8080" });
        urls.AddRange(ports
            .Where(p => Regex.IsMatch(p, @"^\d{2,5}$"))
            .Distinct()
            .Select(p => "http://127.0.0.1:" + p + "/api"));
        return urls.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private IEnumerable<SensorRow> TryGetFrameworkControlThermalRows(string baseUrl)
    {
        string json;
        var thermalResponded = TryGetFrameworkControlJson(baseUrl + "/thermal", out json);
        if (thermalResponded && string.IsNullOrWhiteSpace(json))
        {
            json = TryGetLatestFrameworkControlHistoryJson(baseUrl + "/thermal/history");
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return Enumerable.Empty<SensorRow>();
        }

        LogMessage("Debug", "Framework Control API returned thermal data from " + baseUrl + ".");
        return ParseFrameworkControlThermalJson(json, baseUrl);
    }

    private bool TryGetFrameworkControlJson(string url, out string json)
    {
        json = "";
        try
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Timeout = 200;
            request.ReadWriteTimeout = 200;
            request.UserAgent = "Sensor Readout";

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream))
            {
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    return true;
                }

                json = reader.ReadToEnd();
                return true;
            }
        }
        catch (Exception ex)
        {
            LogMessage("Debug", "Framework Control API probe failed for " + url + ": " + ex.Message);
            return false;
        }
    }

    private string TryGetLatestFrameworkControlHistoryJson(string url)
    {
        string json;
        if (!TryGetFrameworkControlJson(url, out json))
        {
            return "";
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return "";
        }

        try
        {
            var array = JArray.Parse(json);
            return array.Count == 0 ? "" : array[array.Count - 1].ToString();
        }
        catch
        {
            return "";
        }
    }

    private static IEnumerable<SensorRow> ParseFrameworkControlThermalJson(string json, string baseUrl)
    {
        var rows = new List<SensorRow>();
        try
        {
            var obj = JObject.Parse(json);
            var temps = obj["temps"] as JObject;
            if (temps != null)
            {
                foreach (var property in temps.Properties())
                {
                    double value;
                    if (!double.TryParse(Convert.ToString(property.Value), out value))
                    {
                        continue;
                    }

                    rows.Add(new SensorRow
                    {
                        Type = "Temperature",
                        Hardware = "Framework Control",
                        Name = CleanFrameworkEcName(property.Name),
                        Identifier = "framework/control/temperature/" + SlugifyFrameworkEcName(property.Name),
                        Value = (float)value,
                        DisplayValue = FormatNumber(Math.Round(value, 1), "0.0") + " C",
                        Source = "Framework Control API"
                    });
                }
            }

            var rpms = obj["rpms"] as JArray;
            if (rpms != null)
            {
                for (var i = 0; i < rpms.Count; i++)
                {
                    double value;
                    if (!double.TryParse(Convert.ToString(rpms[i]), out value) || value < 0 || value >= 20000)
                    {
                        continue;
                    }

                    rows.Add(new SensorRow
                    {
                        Type = "Fan",
                        Hardware = "Framework Control",
                        Name = "Fan " + (i + 1),
                        Identifier = "framework/control/fan/" + i + "/rpm",
                        Value = (float)value,
                        DisplayValue = FormatNumber(Math.Round(value, 0), "0") + " RPM",
                        Source = "Framework Control API"
                    });
                }
            }
        }
        catch
        {
            rows.Add(new SensorRow
            {
                Type = "Performance",
                Hardware = "Overview",
                Name = "Framework Control API",
                DisplayValue = "found at " + baseUrl + ", but thermal JSON could not be parsed",
                Source = "Framework Control API"
            });
        }

        return rows;
    }

    private string FindFrameworkEcTool()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "ectool.exe"),
            Path.Combine(baseDir, "fw-ectool.exe"),
            Path.Combine(baseDir, "Tools", "ectool.exe"),
            Path.Combine(baseDir, "Tools", "Framework", "ectool.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "crosec", "ectool.exe")
        };

        return candidates.FirstOrDefault(File.Exists) ?? "";
    }

    private static bool IsFrameworkComputer()
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
            {
                foreach (ManagementObject system in searcher.Get())
                {
                    var manufacturer = Convert.ToString(system["Manufacturer"]) ?? "";
                    var model = Convert.ToString(system["Model"]) ?? "";
                    return manufacturer.IndexOf("Framework", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        model.IndexOf("Framework", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        model.IndexOf("Laptop 16", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private FrameworkEcCommandResult RunFrameworkEcTool(string toolPath, string arguments)
    {
        var result = new FrameworkEcCommandResult { Arguments = arguments };
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = toolPath,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    result.Error = "Process did not start.";
                    return result;
                }

                if (!process.WaitForExit(2000))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                    }

                    result.Error = "Timed out.";
                    return result;
                }

                result.ExitCode = process.ExitCode;
                result.Output = process.StandardOutput.ReadToEnd();
                result.Error = process.StandardError.ReadToEnd();
            }
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        return result;
    }

    private void LogFrameworkEcOutput(string command, FrameworkEcCommandResult result)
    {
        if (result == null)
        {
            return;
        }

        var text = (result.Output ?? "").Trim();
        var error = (result.Error ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(error))
        {
            return;
        }

        LogMessage("Debug", "Framework EC " + command + " exit " + result.ExitCode + ".");
        foreach (var line in SplitFrameworkEcLines(text).Concat(SplitFrameworkEcLines(error)).Take(80))
        {
            LogMessage("Debug", "Framework EC " + command + ": " + line);
        }
    }

    private static Dictionary<int, string> ParseFrameworkThermalNames(string output)
    {
        var names = new Dictionary<int, string>();
        foreach (var line in SplitFrameworkEcLines(output))
        {
            var match = Regex.Match(line, @"^\s*(?<id>\d+)\s+\S+\s+\S+\s+\S+\s+\S+\s+\S+\s+(?<name>.+?)\s*$");
            int id;
            if (match.Success && int.TryParse(match.Groups["id"].Value, out id))
            {
                names[id] = CleanFrameworkEcName(match.Groups["name"].Value);
            }
        }

        return names;
    }

    private static IEnumerable<SensorRow> ParseFrameworkTemperatureRows(string output, Dictionary<int, string> sensorNames)
    {
        var rows = new List<SensorRow>();
        foreach (var line in SplitFrameworkEcLines(output))
        {
            var namedMatch = Regex.Match(line, @"^\s*(?<name>[A-Za-z0-9_.@/# -]+?)\s+\d+\s*K\s+\(=\s*(?<value>-?\d+(?:\.\d+)?)\s*C\)", RegexOptions.IgnoreCase);
            if (namedMatch.Success)
            {
                AddFrameworkTemperatureRow(rows, CleanFrameworkEcName(namedMatch.Groups["name"].Value), namedMatch.Groups["value"].Value);
                continue;
            }

            var indexedMatch = Regex.Match(line, @"^\s*(?<id>\d+)\s*:\s*(?<kelvin>\d+)\s*K\b", RegexOptions.IgnoreCase);
            int id;
            double kelvin;
            if (indexedMatch.Success &&
                int.TryParse(indexedMatch.Groups["id"].Value, out id) &&
                double.TryParse(indexedMatch.Groups["kelvin"].Value, out kelvin))
            {
                string name;
                if (sensorNames == null || !sensorNames.TryGetValue(id, out name) || string.IsNullOrWhiteSpace(name))
                {
                    name = "Sensor " + id;
                }

                AddFrameworkTemperatureRow(rows, name, (kelvin - 273.15).ToString("0.0"));
            }
        }

        return rows;
    }

    private static void AddFrameworkTemperatureRow(List<SensorRow> rows, string name, string valueText)
    {
        double value;
        if (!double.TryParse(valueText, out value) || value < -50 || value > 150)
        {
            return;
        }

        rows.Add(new SensorRow
        {
            Type = "Temperature",
            Hardware = "Framework EC",
            Name = string.IsNullOrWhiteSpace(name) ? "Temperature" : name,
            Identifier = "framework/ec/temperature/" + SlugifyFrameworkEcName(name),
            Value = (float)value,
            DisplayValue = FormatNumber(Math.Round(value, 1), "0.0") + " C",
            Source = "Framework EC ectool"
        });
    }

    private static IEnumerable<SensorRow> ParseFrameworkFanRows(IEnumerable<FrameworkEcCommandResult> results)
    {
        var rows = new List<SensorRow>();
        var seen = new HashSet<int>();
        foreach (var result in results ?? Enumerable.Empty<FrameworkEcCommandResult>())
        {
            foreach (var line in SplitFrameworkEcLines(result.Output))
            {
                int id;
                int rpm;
                if (!TryParseFrameworkFanLine(line, result.Arguments, out id, out rpm) || seen.Contains(id))
                {
                    continue;
                }

                seen.Add(id);
                rows.Add(new SensorRow
                {
                    Type = "Fan",
                    Hardware = "Framework EC",
                    Name = "Fan " + id,
                    Identifier = "framework/ec/fan/" + id + "/rpm",
                    Value = rpm,
                    DisplayValue = rpm + " RPM",
                    Source = "Framework EC ectool"
                });
            }
        }

        return rows;
    }

    private static bool TryParseFrameworkFanLine(string line, string command, out int id, out int rpm)
    {
        id = ExtractFanIndexFromCommand(command);
        rpm = 0;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var match = Regex.Match(line, @"Fan\s*(?<id>\d+)?[^0-9]*(?<rpm>\d{2,5})\s*RPM", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            match = Regex.Match(line, @"Fan\s*(?<id>\d+)?[^0-9]*(?<rpm>\d{2,5})\b", RegexOptions.IgnoreCase);
        }

        if (!match.Success)
        {
            match = Regex.Match(line, @"(?<rpm>\d{2,5})\s*RPM", RegexOptions.IgnoreCase);
        }

        if (!match.Success || !int.TryParse(match.Groups["rpm"].Value, out rpm))
        {
            return false;
        }

        int parsedId;
        if (match.Groups["id"].Success && int.TryParse(match.Groups["id"].Value, out parsedId))
        {
            id = parsedId;
        }

        if (id < 0)
        {
            id = 0;
        }

        return rpm >= 0 && rpm < 20000;
    }

    private static int ExtractFanIndexFromCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return -1;
        }

        var match = Regex.Match(command, @"\s(?<id>\d+)\s*$");
        int id;
        return match.Success && int.TryParse(match.Groups["id"].Value, out id) ? id : -1;
    }

    private static IEnumerable<string> SplitFrameworkEcLines(string text)
    {
        return (text ?? "")
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("--"));
    }

    private static string CleanFrameworkEcName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        name = name.Trim();
        var at = name.IndexOf('@');
        if (at > 0)
        {
            name = name.Substring(0, at);
        }

        return name.Replace('_', ' ').Trim();
    }

    private static string SlugifyFrameworkEcName(string name)
    {
        var cleaned = Regex.Replace((name ?? "").Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(cleaned) ? "sensor" : cleaned;
    }

    private sealed class FrameworkEcCommandResult
    {
        public string Arguments;
        public int ExitCode = -1;
        public string Output = "";
        public string Error = "";
    }
}
