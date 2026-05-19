using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using Microsoft.Win32;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private sealed class PrinterInfo
    {
        public string Name;
        public bool IsDefault;
        public bool IsNetwork;
        public bool IsShared;
        public bool WorkOffline;
        public string DriverName;
        public string PortName;
        public string Location;
        public string Comment;
        public string ShareName;
        public string PrinterStatus;
        public string ErrorState;
        public string ExtendedStatus;
        public string Jobs;
        public string PaperSize;
        public string Resolution;
        public string Color;
        public string Duplex;
        public Dictionary<string, string> Details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class PrinterLevel
    {
        public string PrinterName;
        public string Name;
        public int Percent;
        public string Source;
    }

    private static void AddPrinterOverviewRows(List<SensorRow> rows)
    {
        if (rows == null)
        {
            return;
        }

        var printers = GetPrinterInfos();
        if (printers.Count == 0)
        {
            return;
        }

        AddOverviewTextRow(rows, "Printer count", printers.Count.ToString(), "Windows printers");
        var defaultPrinter = printers.FirstOrDefault(p => p.IsDefault);
        if (defaultPrinter != null)
        {
            AddOverviewTextRow(rows, "Default printer", defaultPrinter.Name, "Windows printers");
        }

        foreach (var printer in printers.OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var details = BuildPrinterDetails(printer);
            AddPrinterTextRow(rows, printer.Name, "Status", printer.PrinterStatus, "Windows printers", details);
            AddPrinterTextRow(rows, printer.Name, "Error state", printer.ErrorState, "Windows printers", details);
            AddPrinterTextRow(rows, printer.Name, "Extended status", printer.ExtendedStatus, "Windows printers", details);
            AddPrinterTextRow(rows, printer.Name, "Driver", printer.DriverName, "Windows printers", details);
            AddPrinterTextRow(rows, printer.Name, "Port", printer.PortName, "Windows printers", details);
            AddPrinterTextRow(rows, printer.Name, "Type", printer.IsNetwork ? "Network" : "Local", "Windows printers", details);
            AddPrinterBooleanRow(rows, printer.Name, "Offline", printer.WorkOffline, "Windows printers", details);
            AddPrinterBooleanRow(rows, printer.Name, "Shared", printer.IsShared, "Windows printers", details);
            AddPrinterTextRow(rows, printer.Name, "Share name", printer.ShareName, "Windows printers", details);
            AddPrinterTextRow(rows, printer.Name, "Location", printer.Location, "Windows printers", details);
            AddPrinterTextRow(rows, printer.Name, "Comment", printer.Comment, "Windows printers", details);
            AddPrinterIntegerRow(rows, printer.Name, "Jobs queued", printer.Jobs, "Windows printers", details);
            AddPrinterTextRow(rows, printer.Name, "Paper size", printer.PaperSize, "Windows printers", details);
            AddPrinterTextRow(rows, printer.Name, "Resolution", printer.Resolution, "Windows printers", details);
            AddPrinterTextRow(rows, printer.Name, "Color", printer.Color, "Windows printers", details);
            AddPrinterTextRow(rows, printer.Name, "Duplex", printer.Duplex, "Windows printers", details);
        }

        foreach (var level in GetPrinterSupplyLevels().OrderBy(l => l.PrinterName, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
        {
            rows.Add(new SensorRow
            {
                Type = "Performance",
                Hardware = PrinterPerformanceHardware(level.PrinterName),
                Name = level.Name + " level",
                Identifier = "printer/" + StableDeviceInventoryKey(level.PrinterName) + "/" + StableDeviceInventoryKey(level.Name) + "-level",
                Value = level.Percent,
                DisplayValue = level.Percent + "%",
                Source = level.Source
            });
        }
    }

    private static void AddPrinterTextRow(List<SensorRow> rows, string printerName, string name, string value, string source, Dictionary<string, string> details)
    {
        AddPrinterRow(rows, printerName, name, null, value, source, details);
    }

    private static void AddPrinterBooleanRow(List<SensorRow> rows, string printerName, string name, bool value, string source, Dictionary<string, string> details)
    {
        AddPrinterRow(rows, printerName, name, value ? 1f : 0f, value ? "Yes" : "No", source, details);
    }

    private static void AddPrinterIntegerRow(List<SensorRow> rows, string printerName, string name, string value, string source, Dictionary<string, string> details)
    {
        int number;
        AddPrinterRow(rows, printerName, name, TryConvertToInt32(value, out number) ? (float?)number : null, value, source, details);
    }

    private static void AddPrinterRow(List<SensorRow> rows, string printerName, string name, float? numericValue, string displayValue, string source, Dictionary<string, string> details)
    {
        if (rows == null || string.IsNullOrWhiteSpace(printerName) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(displayValue))
        {
            return;
        }

        rows.Add(new SensorRow
        {
            Type = "Performance",
            Hardware = PrinterPerformanceHardware(printerName),
            Name = name.Trim(),
            Identifier = "printer/" + StableDeviceInventoryKey(printerName) + "/" + StableDeviceInventoryKey(name),
            Value = numericValue,
            DisplayValue = displayValue.Trim(),
            Source = source,
            Details = CloneDetails(details)
        });
    }

    private static string PrinterPerformanceHardware(string printerName)
    {
        return "Printer: " + (printerName ?? "").Trim();
    }

    private static List<PrinterInfo> GetPrinterInfos()
    {
        var printers = new Dictionary<string, PrinterInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Name, Default, Network, Shared, WorkOffline, PrinterStatus, DetectedErrorState, ExtendedPrinterStatus, DriverName, PortName, Location, Comment, ShareName, JobCountSinceLastReset FROM Win32_Printer"))
            {
                foreach (ManagementObject printer in searcher.Get())
                {
                    var name = CleanWmiText(Convert.ToString(printer["Name"]));
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var info = new PrinterInfo
                    {
                        Name = name,
                        IsDefault = ToBool(printer["Default"]),
                        IsNetwork = ToBool(printer["Network"]),
                        IsShared = ToBool(printer["Shared"]),
                        WorkOffline = ToBool(printer["WorkOffline"]),
                        DriverName = CleanWmiText(Convert.ToString(printer["DriverName"])),
                        PortName = CleanWmiText(Convert.ToString(printer["PortName"])),
                        Location = CleanWmiText(Convert.ToString(printer["Location"])),
                        Comment = CleanWmiText(Convert.ToString(printer["Comment"])),
                        ShareName = CleanWmiText(Convert.ToString(printer["ShareName"])),
                        PrinterStatus = DecodePrinterStatus(printer["PrinterStatus"]),
                        ErrorState = DecodePrinterErrorState(printer["DetectedErrorState"]),
                        ExtendedStatus = DecodeExtendedPrinterStatus(printer["ExtendedPrinterStatus"]),
                        Jobs = FormatNonNegativeInteger(printer["JobCountSinceLastReset"])
                    };
                    AddPrinterInfoDetails(info, printer);
                    printers[name] = info;
                }
            }
        }
        catch
        {
        }

        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Name, Color, Duplex, HorizontalResolution, VerticalResolution, PaperSize FROM Win32_PrinterConfiguration"))
            {
                foreach (ManagementObject config in searcher.Get())
                {
                    var name = CleanWmiText(Convert.ToString(config["Name"]));
                    PrinterInfo printer;
                    if (string.IsNullOrWhiteSpace(name) || !printers.TryGetValue(name, out printer))
                    {
                        continue;
                    }

                    printer.PaperSize = CleanWmiText(Convert.ToString(config["PaperSize"]));
                    printer.Color = DecodePrinterColor(config["Color"]);
                    printer.Duplex = DecodePrinterDuplex(config["Duplex"]);
                    printer.Resolution = FormatPrinterResolution(config["HorizontalResolution"], config["VerticalResolution"]);
                    AddRawWmiDetails(printer.Details, "Configuration WMI", config);
                }
            }
        }
        catch
        {
        }

        return printers.Values.ToList();
    }

    private static void AddPrinterInfoDetails(PrinterInfo printer, ManagementObject wmi)
    {
        if (printer == null)
        {
            return;
        }

        var details = printer.Details ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        printer.Details = details;
        AddDetail(details, "Printer name", printer.Name);
        AddDetail(details, "Default printer", printer.IsDefault ? "Yes" : "No");
        AddDetail(details, "Network printer", printer.IsNetwork ? "Yes" : "No");
        AddDetail(details, "Shared printer", printer.IsShared ? "Yes" : "No");
        AddDetail(details, "Work offline", printer.WorkOffline ? "Yes" : "No");
        AddDetail(details, "Driver", printer.DriverName);
        AddDetail(details, "Port", printer.PortName);
        AddDetail(details, "Location", printer.Location);
        AddDetail(details, "Comment", printer.Comment);
        AddDetail(details, "Share name", printer.ShareName);
        AddDetail(details, "Status", printer.PrinterStatus);
        AddDetail(details, "Error state", printer.ErrorState);
        AddDetail(details, "Extended status", printer.ExtendedStatus);
        AddDetail(details, "Jobs queued", printer.Jobs);
        AddRawWmiDetails(details, "WMI", wmi);
        AddPrinterRegistryDetails(details, printer.Name);
    }

    private static Dictionary<string, string> BuildPrinterDetails(PrinterInfo printer)
    {
        if (printer == null)
        {
            return null;
        }

        var details = CloneDetails(printer.Details) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddDetail(details, "Paper size", printer.PaperSize);
        AddDetail(details, "Resolution", printer.Resolution);
        AddDetail(details, "Color support", printer.Color);
        AddDetail(details, "Duplex mode", printer.Duplex);
        return details;
    }

    private static void AddPrinterRegistryDetails(Dictionary<string, string> details, string printerName)
    {
        if (details == null || string.IsNullOrWhiteSpace(printerName))
        {
            return;
        }

        try
        {
            using (var printersKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Print\Printers"))
            using (var printerKey = printersKey == null ? null : printersKey.OpenSubKey(printerName))
            {
                AddPrinterRegistryValues(details, "Registry", printerKey);
                using (var driverData = printerKey == null ? null : printerKey.OpenSubKey("PrinterDriverData"))
                {
                    AddPrinterRegistryValues(details, "Driver data", driverData);
                }
            }
        }
        catch
        {
        }
    }

    private static void AddPrinterRegistryValues(Dictionary<string, string> details, string prefix, RegistryKey key)
    {
        if (details == null || key == null || string.IsNullOrWhiteSpace(prefix))
        {
            return;
        }

        foreach (var name in key.GetValueNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            AddDetail(details, prefix + " " + name, PrinterRegistryValueToString(key.GetValue(name)));
        }
    }

    private static string PrinterRegistryValueToString(object value)
    {
        var bytes = value as byte[];
        if (bytes != null)
        {
            return bytes.Length == 0 ? "" : "Binary data (" + bytes.Length + " bytes)";
        }

        return RegistryValueToString(value);
    }

    private static List<PrinterLevel> GetPrinterSupplyLevels()
    {
        var levels = new List<PrinterLevel>();
        AddPrinterPropertyLevels(levels);
        AddPrinterRegistryLevels(levels);
        return levels
            .GroupBy(l => (l.PrinterName + "|" + l.Name).ToLowerInvariant())
            .Select(g => g.First())
            .ToList();
    }

    private static void AddPrinterPropertyLevels(List<PrinterLevel> levels)
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher(@"root\StandardCimv2", "SELECT * FROM MSFT_PrinterProperty"))
            {
                foreach (ManagementObject property in searcher.Get())
                {
                    var printerName = FirstNonEmptyProperty(property, "PrinterName", "Name");
                    var propertyName = FirstNonEmptyProperty(property, "PropertyName", "Name");
                    var value = GetWmiPropertyValue(property, "Value");
                    AddPrinterLevelIfUseful(levels, printerName, propertyName, value, "Windows printer property");
                }
            }
        }
        catch
        {
        }
    }

    private static void AddPrinterRegistryLevels(List<PrinterLevel> levels)
    {
        try
        {
            using (var printersKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Print\Printers"))
            {
                if (printersKey == null)
                {
                    return;
                }

                foreach (var printerName in printersKey.GetSubKeyNames())
                {
                    using (var printerKey = printersKey.OpenSubKey(printerName))
                    {
                        AddPrinterRegistryLevelsFromKey(levels, printerName, printerKey, "Windows printer registry");
                        using (var driverData = printerKey == null ? null : printerKey.OpenSubKey("PrinterDriverData"))
                        {
                            AddPrinterRegistryLevelsFromKey(levels, printerName, driverData, "Windows printer driver data");
                        }
                    }
                }
            }
        }
        catch
        {
        }
    }

    private static void AddPrinterRegistryLevelsFromKey(List<PrinterLevel> levels, string printerName, RegistryKey key, string source)
    {
        if (key == null)
        {
            return;
        }

        foreach (var valueName in key.GetValueNames())
        {
            AddPrinterLevelIfUseful(levels, printerName, valueName, key.GetValue(valueName), source);
        }
    }

    private static void AddPrinterLevelIfUseful(List<PrinterLevel> levels, string printerName, string rawName, object rawValue, string source)
    {
        if (levels == null || string.IsNullOrWhiteSpace(printerName) || string.IsNullOrWhiteSpace(rawName))
        {
            return;
        }

        if (!LooksLikePrinterSupplyName(rawName))
        {
            return;
        }

        int percent;
        if (!TryParsePrinterPercent(rawValue, out percent))
        {
            return;
        }

        levels.Add(new PrinterLevel
        {
            PrinterName = CleanWmiText(printerName),
            Name = FriendlyPrinterSupplyName(rawName),
            Percent = percent,
            Source = source
        });
    }

    private static bool LooksLikePrinterSupplyName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var text = name.ToLowerInvariant();
        return (text.Contains("ink") ||
            text.Contains("toner") ||
            text.Contains("cartridge") ||
            text.Contains("supply") ||
            text.Contains("consumable")) &&
            (text.Contains("level") ||
            text.Contains("remain") ||
            text.Contains("percent") ||
            text.Contains("amount") ||
            text.Contains("capacity"));
    }

    private static bool TryParsePrinterPercent(object value, out int percent)
    {
        percent = 0;
        if (value == null)
        {
            return false;
        }

        var array = value as Array;
        if (array != null && array.Length == 1)
        {
            value = array.GetValue(0);
        }

        int intValue;
        if (TryConvertToInt32(value, out intValue))
        {
            if (intValue >= 0 && intValue <= 100)
            {
                percent = intValue;
                return true;
            }
        }

        var text = Convert.ToString(value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = System.Text.RegularExpressions.Regex.Match(text, @"(?<!\d)(100|[0-9]{1,2})(?:\s*%)?(?!\d)");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out intValue))
        {
            return false;
        }

        percent = intValue;
        return true;
    }

    private static string FriendlyPrinterSupplyName(string rawName)
    {
        var name = CleanWmiText(rawName)
            .Replace("_", " ")
            .Replace("-", " ")
            .Replace(".", " ");
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\s+", " ").Trim();
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\b(level|remaining|remain|percent|percentage|amount|capacity)\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        return string.IsNullOrWhiteSpace(name) ? "Supply" : name;
    }

    private static string FirstNonEmptyProperty(ManagementBaseObject obj, params string[] names)
    {
        foreach (var name in names)
        {
            var text = CleanWmiText(Convert.ToString(GetWmiPropertyValue(obj, name)));
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return "";
    }

    private static bool ToBool(object value)
    {
        try
        {
            return value != null && Convert.ToBoolean(value);
        }
        catch
        {
            return false;
        }
    }

    private static string FormatNonNegativeInteger(object value)
    {
        int number;
        return TryConvertToInt32(value, out number) && number >= 0 ? number.ToString() : "";
    }

    private static string FormatPrinterResolution(object horizontal, object vertical)
    {
        int x;
        int y;
        if (!TryConvertToInt32(horizontal, out x) || !TryConvertToInt32(vertical, out y) || x <= 0 || y <= 0)
        {
            return "";
        }

        return x == y ? x + " dpi" : x + " x " + y + " dpi";
    }

    private static string DecodePrinterStatus(object value)
    {
        int status;
        if (!TryConvertToInt32(value, out status))
        {
            return "";
        }

        switch (status)
        {
            case 1: return "Other";
            case 2: return "Unknown";
            case 3: return "Idle";
            case 4: return "Printing";
            case 5: return "Warmup";
            case 6: return "Stopped printing";
            case 7: return "Offline";
            default: return status.ToString();
        }
    }

    private static string DecodePrinterErrorState(object value)
    {
        int state;
        if (!TryConvertToInt32(value, out state) || state == 0)
        {
            return "";
        }

        switch (state)
        {
            case 1: return "Other";
            case 2: return "Unknown";
            case 3: return "Low paper";
            case 4: return "No paper";
            case 5: return "Low toner";
            case 6: return "No toner";
            case 7: return "Door open";
            case 8: return "Jammed";
            case 9: return "Offline";
            case 10: return "Service requested";
            case 11: return "Output bin full";
            default: return state.ToString();
        }
    }

    private static string DecodeExtendedPrinterStatus(object value)
    {
        int status;
        if (!TryConvertToInt32(value, out status))
        {
            return "";
        }

        switch (status)
        {
            case 1: return "Other";
            case 2: return "Unknown";
            case 3: return "Idle";
            case 4: return "Printing";
            case 5: return "Warmup";
            case 6: return "Stopped printing";
            case 7: return "Offline";
            case 8: return "Paused";
            case 9: return "Error";
            case 10: return "Busy";
            case 11: return "Not available";
            case 12: return "Waiting";
            case 13: return "Processing";
            case 14: return "Initialization";
            case 15: return "Power save";
            case 16: return "Pending deletion";
            case 17: return "I/O active";
            case 18: return "Manual feed";
            default: return status.ToString();
        }
    }

    private static string DecodePrinterColor(object value)
    {
        int color;
        if (!TryConvertToInt32(value, out color))
        {
            return "";
        }

        if (color == 1) return "Monochrome";
        if (color == 2) return "Color";
        return color.ToString();
    }

    private static string DecodePrinterDuplex(object value)
    {
        int duplex;
        if (!TryConvertToInt32(value, out duplex) || duplex == 0)
        {
            return "";
        }

        if (duplex == 1) return "Simplex";
        if (duplex == 2) return "Horizontal";
        if (duplex == 3) return "Vertical";
        return duplex.ToString();
    }
}
