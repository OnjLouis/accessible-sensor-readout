using System;
using System.Collections.Generic;
using System.Globalization;
using System.Management;
using SensorReadout.PluginSdk;

namespace SensorReadout.LenovoThinkPadPlugIn
{
    public sealed partial class LenovoThinkPadPlugIn
    {
        private static IEnumerable<SensorReading> ReadStorageHealth(IPluginContext context, Dictionary<string, string> summaryDetails)
        {
            var rows = new List<SensorReading>();
            const string storageNamespace = @"ROOT\Microsoft\Windows\Storage";
            try
            {
                // SELECT * is required so the disk object carries its full key path (ObjectId) — GetRelated needs it to traverse the storage reliability association.
                using (var searcher = new ManagementObjectSearcher(storageNamespace, "SELECT * FROM MSFT_PhysicalDisk"))
                using (var instances = searcher.Get())
                {
                    var count = 0;
                    foreach (ManagementObject disk in instances)
                    {
                        count++;
                        var details = ReadDetails(disk);
                        details["Namespace"] = storageNamespace;
                        details["WMI class"] = "MSFT_PhysicalDisk";

                        var friendly = FirstValue(details, "FriendlyName");
                        if (string.IsNullOrWhiteSpace(friendly))
                        {
                            friendly = "Disk " + count.ToString(CultureInfo.InvariantCulture);
                        }

                        var diskSlug = StableIdentifier(friendly);
                        var idPrefix = "storage/" + diskSlug + "/";

                        var health = FirstNumber(details, "HealthStatus");
                        if (health.HasValue)
                        {
                            rows.Add(new SensorReading
                            {
                                Type = "Performance",
                                Hardware = "Storage",
                                Name = friendly + " health",
                                Identifier = StableIdentifier(idPrefix + "health"),
                                Value = (float)health.Value,
                                DisplayValue = DescribeDiskHealth((int)health.Value),
                                Source = "Lenovo Laptop Support Plug-In",
                                Details = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase)
                            });
                        }

                        var opStatus = FirstValue(details, "OperationalStatus");
                        if (!string.IsNullOrWhiteSpace(opStatus))
                        {
                            rows.Add(new SensorReading
                            {
                                Type = "Performance",
                                Hardware = "Storage",
                                Name = friendly + " operational status",
                                Identifier = StableIdentifier(idPrefix + "operational-status"),
                                DisplayValue = DescribeOperationalStatus(opStatus),
                                Source = "Lenovo Laptop Support Plug-In",
                                Details = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase)
                            });
                        }

                        // Reliability counter is reached through the disk's association set rather than direct query.
                        rows.AddRange(ReadDiskReliabilityCounter(context, summaryDetails, disk, friendly, idPrefix));
                    }

                    summaryDetails["MSFT_PhysicalDisk instances"] = count.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex)
            {
                summaryDetails["MSFT_PhysicalDisk error"] = ex.Message;
                if (context != null)
                {
                    context.Log("Debug", "Storage probe failed for " + storageNamespace + " MSFT_PhysicalDisk: " + ex.Message);
                }
            }

            return rows;
        }

        private static IEnumerable<SensorReading> ReadDiskReliabilityCounter(IPluginContext context, Dictionary<string, string> summaryDetails, ManagementObject disk, string friendly, string idPrefix)
        {
            var rows = new List<SensorReading>();
            try
            {
                using (var related = disk.GetRelated("MSFT_StorageReliabilityCounter"))
                {
                    foreach (ManagementObject counter in related)
                    {
                        var details = ReadDetails(counter);
                        details["Namespace"] = @"ROOT\Microsoft\Windows\Storage";
                        details["WMI class"] = "MSFT_StorageReliabilityCounter";

                        var temp = FirstNumber(details, "Temperature");
                        if (temp.HasValue && temp.Value > 0 && temp.Value < 150)
                        {
                            rows.Add(new SensorReading
                            {
                                Type = "Temperature",
                                Hardware = "Storage",
                                Name = friendly + " temperature",
                                Identifier = StableIdentifier(idPrefix + "temperature-c"),
                                Value = (float)temp.Value,
                                DisplayValue = FormatNumber(temp.Value) + " C",
                                Source = "Lenovo Laptop Support Plug-In",
                                Details = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase)
                            });
                        }

                        var maxTemp = FirstNumber(details, "TemperatureMax");
                        if (maxTemp.HasValue && maxTemp.Value > 0 && maxTemp.Value < 150)
                        {
                            rows.Add(new SensorReading
                            {
                                Type = "Temperature",
                                Hardware = "Storage",
                                Name = friendly + " max-rated temperature",
                                Identifier = StableIdentifier(idPrefix + "temperature-max-c"),
                                Value = (float)maxTemp.Value,
                                DisplayValue = FormatNumber(maxTemp.Value) + " C",
                                Source = "Lenovo Laptop Support Plug-In",
                                Details = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase)
                            });
                        }

                        var wear = FirstNumber(details, "Wear");
                        if (wear.HasValue && wear.Value >= 0 && wear.Value <= 100)
                        {
                            rows.Add(new SensorReading
                            {
                                Type = "Performance",
                                Hardware = "Storage",
                                Name = friendly + " wear",
                                Identifier = StableIdentifier(idPrefix + "wear-percent"),
                                Value = (float)wear.Value,
                                DisplayValue = FormatNumber(wear.Value) + "%",
                                Source = "Lenovo Laptop Support Plug-In",
                                Details = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase)
                            });
                        }

                        var hours = FirstNumber(details, "PowerOnHours");
                        if (hours.HasValue && hours.Value > 0)
                        {
                            rows.Add(new SensorReading
                            {
                                Type = "Performance",
                                Hardware = "Storage",
                                Name = friendly + " power-on hours",
                                Identifier = StableIdentifier(idPrefix + "power-on-hours"),
                                Value = (float)hours.Value,
                                DisplayValue = FormatNumber(hours.Value) + " h",
                                Source = "Lenovo Laptop Support Plug-In",
                                Details = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase)
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (context != null)
                {
                    context.Log("Debug", "Storage reliability probe failed for " + friendly + ": " + ex.Message);
                }
            }

            return rows;
        }

        private static string DescribeDiskHealth(int code)
        {
            switch (code)
            {
                case 0: return "Healthy";
                case 1: return "Warning";
                case 2: return "Unhealthy";
                case 5: return "Unknown";
                default: return "Code " + code.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static string DescribeOperationalStatus(string raw)
        {
            // CIM operational status codes from CIM_ManagedSystemElement. Comma-separated when multiple are active.
            if (string.IsNullOrWhiteSpace(raw))
            {
                return "Unknown";
            }

            var parts = raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            var labels = new List<string>();
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                int code;
                if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out code))
                {
                    labels.Add(DescribeOperationalStatusCode(code));
                }
                else
                {
                    labels.Add(trimmed);
                }
            }

            return string.Join(", ", labels.ToArray());
        }

        private static string DescribeOperationalStatusCode(int code)
        {
            switch (code)
            {
                case 0: return "Unknown";
                case 1: return "Other";
                case 2: return "OK";
                case 3: return "Degraded";
                case 4: return "Stressed";
                case 5: return "Predictive Failure";
                case 8: return "Starting";
                case 9: return "Stopping";
                case 10: return "Stopped";
                case 11: return "In Service";
                case 12: return "No Contact";
                case 13: return "Lost Communication";
                case 14: return "Aborted";
                case 15: return "Dormant";
                case 16: return "Supporting Entity in Error";
                case 17: return "Completed";
                default: return "Code " + code.ToString(CultureInfo.InvariantCulture);
            }
        }

    }
}
