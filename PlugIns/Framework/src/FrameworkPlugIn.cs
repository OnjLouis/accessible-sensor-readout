using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SensorReadout.PluginSdk;

namespace SensorReadout.FrameworkPlugIn
{
    public sealed class FrameworkPlugIn : ISensorReadoutPlugin
    {
        private readonly PluginInfo info = new PluginInfo
        {
            Id = "sensorreadout.framework",
            Name = "Framework Laptop",
            Version = "1.0.0",
            Author = "Sensor Readout",
            Description = "Framework Control and Framework EC sensor readings."
        };

        private DateTime cachedRowsUtc = DateTime.MinValue;
        private List<SensorReading> cachedRows = new List<SensorReading>();
        private string cachedToolPath = "";
        private DateTime apiRetryUtc = DateTime.MinValue;
        private bool apiAvailable;

        public PluginInfo Info { get { return info; } }

        public IEnumerable<SensorReading> GetReadings(IPluginContext context)
        {
            if (!IsFrameworkComputer(context))
            {
                return Enumerable.Empty<SensorReading>();
            }

            var apiRows = GetFrameworkControlApiRows(context).ToList();
            if (apiRows.Count > 0)
            {
                return apiRows;
            }

            var toolPath = FindFrameworkEcTool(context);
            if (string.IsNullOrWhiteSpace(toolPath))
            {
                return new[]
                {
                    new SensorReading
                    {
                        Type = "Performance",
                        Hardware = "Overview",
                        Name = "Framework EC helper",
                        DisplayValue = "ectool.exe not found",
                        Source = "Framework Plug-In"
                    }
                };
            }

            if (string.Equals(cachedToolPath, toolPath, StringComparison.OrdinalIgnoreCase) &&
                cachedRows.Count > 0 &&
                DateTime.UtcNow - cachedRowsUtc < TimeSpan.FromSeconds(5))
            {
                return cachedRows.Select(CloneReading).ToList();
            }

            var rows = new List<SensorReading>();
            var thermalGet = RunTool(toolPath, "thermalget");
            var sensorNames = ParseThermalNames(thermalGet.Output);
            var temps = RunTool(toolPath, "temps all");
            var fanResults = new[]
            {
                RunTool(toolPath, "pwmgetfanrpm"),
                RunTool(toolPath, "pwmgetfanrpm all"),
                RunTool(toolPath, "pwmgetfanrpm 0"),
                RunTool(toolPath, "pwmgetfanrpm 1"),
                RunTool(toolPath, "pwmgetfanrpm 2"),
                RunTool(toolPath, "pwmgetfanrpm 3")
            };

            LogOutput(context, "thermalget", thermalGet);
            LogOutput(context, "temps all", temps);
            foreach (var fanResult in fanResults)
            {
                LogOutput(context, fanResult.Arguments, fanResult);
            }

            rows.AddRange(ParseTemperatureRows(temps.Output, sensorNames));
            rows.AddRange(ParseFanRows(fanResults));

            if (rows.Count == 0)
            {
                rows.Add(new SensorReading
                {
                    Type = "Performance",
                    Hardware = "Overview",
                    Name = "Framework EC helper",
                    DisplayValue = "ectool.exe found, but no parseable EC sensor output",
                    Source = "Framework Plug-In"
                });
            }

            cachedToolPath = toolPath;
            cachedRows = rows.Select(CloneReading).ToList();
            cachedRowsUtc = DateTime.UtcNow;
            return rows;
        }

        private IEnumerable<SensorReading> GetFrameworkControlApiRows(IPluginContext context)
        {
            if (!apiAvailable && DateTime.UtcNow < apiRetryUtc)
            {
                return Enumerable.Empty<SensorReading>();
            }

            foreach (var baseUrl in GetApiBaseUrls())
            {
                var rows = TryGetThermalRows(context, baseUrl).ToList();
                if (rows.Count > 0)
                {
                    apiAvailable = true;
                    apiRetryUtc = DateTime.MinValue;
                    return rows;
                }
            }

            apiAvailable = false;
            apiRetryUtc = DateTime.UtcNow.AddSeconds(60);
            return Enumerable.Empty<SensorReading>();
        }

        private static IEnumerable<string> GetApiBaseUrls()
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

        private IEnumerable<SensorReading> TryGetThermalRows(IPluginContext context, string baseUrl)
        {
            string json;
            var thermalResponded = TryGetJson(context, baseUrl + "/thermal", out json);
            if (thermalResponded && string.IsNullOrWhiteSpace(json))
            {
                json = TryGetLatestHistoryJson(context, baseUrl + "/thermal/history");
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                return Enumerable.Empty<SensorReading>();
            }

            context.Log("Debug", "Framework Control API returned thermal data from " + baseUrl + ".");
            return ParseThermalJson(json, baseUrl);
        }

        private static bool TryGetJson(IPluginContext context, string url, out string json)
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
                context.Log("Debug", "Framework Control API probe failed for " + url + ": " + ex.Message);
                return false;
            }
        }

        private static string TryGetLatestHistoryJson(IPluginContext context, string url)
        {
            string json;
            if (!TryGetJson(context, url, out json) || string.IsNullOrWhiteSpace(json))
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

        private static IEnumerable<SensorReading> ParseThermalJson(string json, string baseUrl)
        {
            var rows = new List<SensorReading>();
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

                        rows.Add(new SensorReading
                        {
                            Type = "Temperature",
                            Hardware = "Framework Control",
                            Name = CleanName(property.Name),
                            Identifier = "framework/control/temperature/" + Slugify(property.Name),
                            Value = (float)value,
                            DisplayValue = Format(value, "0.0") + " C",
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

                        rows.Add(new SensorReading
                        {
                            Type = "Fan",
                            Hardware = "Framework Control",
                            Name = "Fan " + (i + 1),
                            Identifier = "framework/control/fan/" + i + "/rpm",
                            Value = (float)value,
                            DisplayValue = Format(Math.Round(value, 0), "0") + " RPM",
                            Source = "Framework Control API"
                        });
                    }
                }
            }
            catch
            {
                rows.Add(new SensorReading
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

        private static string FindFrameworkEcTool(IPluginContext context)
        {
            var baseDir = context.PluginDirectory;
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

        private static CommandResult RunTool(string toolPath, string arguments)
        {
            var result = new CommandResult { Arguments = arguments };
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
                        try { process.Kill(); } catch { }
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

        private static void LogOutput(IPluginContext context, string command, CommandResult result)
        {
            var text = (result.Output ?? "").Trim();
            var error = (result.Error ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(error))
            {
                return;
            }

            context.Log("Debug", "Framework EC " + command + " exit " + result.ExitCode + ".");
            foreach (var line in SplitLines(text).Concat(SplitLines(error)).Take(80))
            {
                context.Log("Debug", "Framework EC " + command + ": " + line);
            }
        }

        private static Dictionary<int, string> ParseThermalNames(string output)
        {
            var names = new Dictionary<int, string>();
            foreach (var line in SplitLines(output))
            {
                var match = Regex.Match(line, @"^\s*(?<id>\d+)\s+\S+\s+\S+\s+\S+\s+\S+\s+\S+\s+(?<name>.+?)\s*$");
                int id;
                if (match.Success && int.TryParse(match.Groups["id"].Value, out id))
                {
                    names[id] = CleanName(match.Groups["name"].Value);
                }
            }

            return names;
        }

        private static IEnumerable<SensorReading> ParseTemperatureRows(string output, Dictionary<int, string> sensorNames)
        {
            var rows = new List<SensorReading>();
            foreach (var line in SplitLines(output))
            {
                var namedMatch = Regex.Match(line, @"^\s*(?<name>[A-Za-z0-9_.@/# -]+?)\s+\d+\s*K\s+\(=\s*(?<value>-?\d+(?:\.\d+)?)\s*C\)", RegexOptions.IgnoreCase);
                if (namedMatch.Success)
                {
                    AddTemperatureRow(rows, CleanName(namedMatch.Groups["name"].Value), namedMatch.Groups["value"].Value);
                    continue;
                }

                var indexedMatch = Regex.Match(line, @"^\s*(?<id>\d+)\s*:\s*(?<kelvin>\d+)\s*K\b", RegexOptions.IgnoreCase);
                int id;
                double kelvin;
                if (indexedMatch.Success && int.TryParse(indexedMatch.Groups["id"].Value, out id) && double.TryParse(indexedMatch.Groups["kelvin"].Value, out kelvin))
                {
                    string name;
                    if (sensorNames == null || !sensorNames.TryGetValue(id, out name) || string.IsNullOrWhiteSpace(name))
                    {
                        name = "Sensor " + id;
                    }

                    AddTemperatureRow(rows, name, (kelvin - 273.15).ToString("0.0", CultureInfo.InvariantCulture));
                }
            }

            return rows;
        }

        private static void AddTemperatureRow(List<SensorReading> rows, string name, string valueText)
        {
            double value;
            if (!double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || value < -50 || value > 150)
            {
                return;
            }

            rows.Add(new SensorReading
            {
                Type = "Temperature",
                Hardware = "Framework EC",
                Name = string.IsNullOrWhiteSpace(name) ? "Temperature" : name,
                Identifier = "framework/ec/temperature/" + Slugify(name),
                Value = (float)value,
                DisplayValue = Format(Math.Round(value, 1), "0.0") + " C",
                Source = "Framework EC ectool"
            });
        }

        private static IEnumerable<SensorReading> ParseFanRows(IEnumerable<CommandResult> results)
        {
            var rows = new List<SensorReading>();
            var seen = new HashSet<int>();
            foreach (var result in results ?? Enumerable.Empty<CommandResult>())
            {
                foreach (var line in SplitLines(result.Output))
                {
                    int id;
                    int rpm;
                    if (!TryParseFanLine(line, result.Arguments, out id, out rpm) || seen.Contains(id))
                    {
                        continue;
                    }

                    seen.Add(id);
                    rows.Add(new SensorReading
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

        private static bool TryParseFanLine(string line, string command, out int id, out int rpm)
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

        private static bool IsFrameworkComputer(IPluginContext context)
        {
            return Contains(context.Machine.Manufacturer, "Framework") || Contains(context.Machine.Model, "Framework");
        }

        private static bool Contains(string text, string value)
        {
            return !string.IsNullOrWhiteSpace(text) &&
                !string.IsNullOrWhiteSpace(value) &&
                text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static IEnumerable<string> SplitLines(string text)
        {
            return (text ?? "")
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith("--"));
        }

        private static string CleanName(string name)
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

        private static string Slugify(string name)
        {
            var cleaned = Regex.Replace((name ?? "").Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
            return string.IsNullOrWhiteSpace(cleaned) ? "sensor" : cleaned;
        }

        private static string Format(double value, string format)
        {
            return value.ToString(format, CultureInfo.InvariantCulture);
        }

        private static SensorReading CloneReading(SensorReading row)
        {
            return new SensorReading
            {
                Type = row.Type,
                Hardware = row.Hardware,
                Name = row.Name,
                Identifier = row.Identifier,
                Value = row.Value,
                DisplayValue = row.DisplayValue,
                Source = row.Source,
                Details = row.Details == null ? null : new Dictionary<string, string>(row.Details, StringComparer.OrdinalIgnoreCase)
            };
        }

        private sealed class CommandResult
        {
            public string Arguments = "";
            public int ExitCode = -1;
            public string Output = "";
            public string Error = "";
        }
    }
}
