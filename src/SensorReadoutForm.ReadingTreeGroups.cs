using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private static void AddHardwareGroups(ReadingTreeItem parent, IEnumerable<SensorRow> rows)
    {
        foreach (var hardwareGroup in rows
            .GroupBy(r => ShortHardwareName(r.Hardware))
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var hardwareItem = new ReadingTreeItem
            {
                Key = "hardware|" + parent.Key + "|" + hardwareGroup.Key,
                Text = hardwareGroup.Key
            };
            AddReadingRows(hardwareItem, hardwareGroup);
            parent.Children.Add(hardwareItem);
        }
    }

    private static void AddSpokenHotKeyGroups(ReadingTreeItem parent, IEnumerable<SensorRow> rows)
    {
        var orderedRows = (rows ?? Enumerable.Empty<SensorRow>())
            .Where(r => r != null)
            .ToList();
        var profileOrder = new List<string>();
        var profileRows = new Dictionary<string, List<SensorRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in orderedRows)
        {
            var profileName = string.IsNullOrWhiteSpace(row.Hardware)
                ? T("ui.Spoken hotkey", "Spoken hotkey")
                : row.Hardware.Trim();
            if (!profileRows.ContainsKey(profileName))
            {
                profileOrder.Add(profileName);
                profileRows[profileName] = new List<SensorRow>();
            }

            profileRows[profileName].Add(row);
        }

        foreach (var profileName in profileOrder)
        {
            var groupRows = profileRows[profileName];
            var hotKey = SpokenHotKeyGroupHotKey(groupRows);
            var groupText = string.IsNullOrWhiteSpace(hotKey)
                ? profileName
                : profileName + ": " + hotKey;
            var groupKey = "spoken-hotkey-profile|" + profileName;
            var groupItem = new ReadingTreeItem { Key = groupKey, Text = groupText };
            foreach (var row in groupRows)
            {
                groupItem.Children.Add(new ReadingTreeItem
                {
                    Key = "row|" + RowSettingsKey(row),
                    Text = SpokenHotKeyMirrorText(row),
                    Row = row
                });
            }

            parent.Children.Add(groupItem);
        }
    }

    private static string SpokenHotKeyGroupHotKey(IEnumerable<SensorRow> rows)
    {
        foreach (var row in rows ?? Enumerable.Empty<SensorRow>())
        {
            if (row == null || row.Details == null)
            {
                continue;
            }

            string value;
            if (row.Details.TryGetValue("Hotkey", out value) && !string.IsNullOrWhiteSpace(value) &&
                !string.Equals(value, T("ui.no hotkey", "no hotkey"), StringComparison.OrdinalIgnoreCase))
            {
                return value.Trim();
            }
        }

        return "";
    }

    private static string SpokenHotKeyMirrorText(SensorRow row)
    {
        if (row == null)
        {
            return "";
        }

        var name = DisplayReadingName(row.Name);
        var value = FormatValue(row);
        if (string.IsNullOrWhiteSpace(value))
        {
            return name;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return value;
        }

        return name + " " + value;
    }

    private static void AddPerformanceGroups(ReadingTreeItem parent, IEnumerable<SensorRow> rows)
    {
        var overviewRows = rows
            .Where(r => string.Equals(r.Hardware, "Overview", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (overviewRows.Count > 0)
        {
            var overviewItem = new ReadingTreeItem { Key = "performance|overview", Text = T("group.Overview", "Overview") };
            AddOverviewGroups(overviewItem, overviewRows);
            parent.Children.Add(overviewItem);
        }

        var dataSourceRows = rows
            .Where(IsDataSourceSummaryRow)
            .ToList();
        if (dataSourceRows.Count > 0)
        {
            var dataSourcesItem = new ReadingTreeItem { Key = "performance|data-sources", Text = T("ui.Data sources", "Data sources") };
            AddReadingRows(dataSourcesItem, dataSourceRows);
            parent.Children.Add(dataSourcesItem);
        }

        var systemRows = rows
            .Where(r => IsSystemPerformanceHardware(r.Hardware) && !IsOverviewHardware(r.Hardware))
            .ToList();
        if (systemRows.Count > 0)
        {
            var systemItem = new ReadingTreeItem { Key = "performance|system", Text = T("group.CPU and memory", "CPU and memory") };
            foreach (var hardwareGroup in systemRows
                .GroupBy(r => ShortHardwareName(r.Hardware))
                .OrderBy(g => PerformanceHardwareSortIndex(g.Key))
                .ThenBy(g => g.Key))
            {
                var hardwareItem = new ReadingTreeItem
                {
                    Key = "hardware|performance|system|" + hardwareGroup.Key,
                    Text = hardwareGroup.Key
                };
                if (string.Equals(hardwareGroup.Key, "CPU", StringComparison.OrdinalIgnoreCase))
                {
                    AddCpuPerformanceGroups(hardwareItem, hardwareGroup);
                }
                else
                {
                    AddReadingRows(hardwareItem, hardwareGroup);
                }

                systemItem.Children.Add(hardwareItem);
            }

            parent.Children.Add(systemItem);
        }

        var graphicsRows = rows
            .Where(IsGpuPerformanceRow)
            .ToList();
        if (graphicsRows.Count > 0)
        {
            var graphicsItem = new ReadingTreeItem { Key = "performance|graphics", Text = T("group.Graphics", "Graphics") };
            AddHardwareGroups(graphicsItem, graphicsRows);
            parent.Children.Add(graphicsItem);
        }

        var printerRows = rows
            .Where(IsPrinterPerformanceRow)
            .ToList();
        if (printerRows.Count > 0)
        {
            var printerItem = new ReadingTreeItem { Key = "performance|printers", Text = T("group.Printers", "Printers") };
            AddPrinterGroups(printerItem, printerRows);
            parent.Children.Add(printerItem);
        }

        var pciRows = rows
            .Where(IsPciExpansionPerformanceRow)
            .ToList();
        if (pciRows.Count > 0)
        {
            var pciItem = new ReadingTreeItem { Key = "performance|pcie-expansion", Text = T("group.PCIe and expansion slots", "PCIe and expansion slots") };
            AddReadingRows(pciItem, pciRows);
            parent.Children.Add(pciItem);
        }

        var storageRows = rows
            .Where(r => !IsSystemPerformanceHardware(r.Hardware) && !IsOverviewHardware(r.Hardware) && !IsDataSourceSummaryRow(r) && !IsGpuPerformanceRow(r) && !IsPrinterPerformanceRow(r) && !IsPciExpansionPerformanceRow(r))
            .ToList();
        if (storageRows.Count > 0)
        {
            var storageItem = new ReadingTreeItem { Key = "performance|storage", Text = T("group.Storage", "Storage") };
            AddStorageHardwareGroups(storageItem, storageRows);
            parent.Children.Add(storageItem);
        }
    }

    private static void AddStorageHardwareGroups(ReadingTreeItem parent, IEnumerable<SensorRow> rows)
    {
        foreach (var hardwareGroup in rows
            .GroupBy(r => ShortHardwareName(r.Hardware))
            .OrderBy(g => StorageHardwareSortIndex(g.Key))
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var hardwareItem = new ReadingTreeItem
            {
                Key = "hardware|" + parent.Key + "|" + hardwareGroup.Key,
                Text = hardwareGroup.Key
            };
            AddReadingRows(hardwareItem, hardwareGroup);
            parent.Children.Add(hardwareItem);
        }
    }

    private static int StorageHardwareSortIndex(string hardware)
    {
        return string.Equals(hardware, "Connected disks", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    }

    private static bool IsDataSourceSummaryRow(SensorRow row)
    {
        if (row == null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(row.Identifier) &&
            row.Identifier.StartsWith("data-source/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var hardware = ShortHardwareName(row.Hardware);
        return string.Equals(hardware, T("ui.Data sources", "Data sources"), StringComparison.OrdinalIgnoreCase) ||
               string.Equals(hardware, "Data sources", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddPrinterGroups(ReadingTreeItem parent, IEnumerable<SensorRow> rows)
    {
        var printerGroups = rows
            .GroupBy(PrinterGroupName)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var issueGroups = printerGroups
            .Where(g => PrinterHasIssue(g))
            .ToList();
        if (issueGroups.Count > 0)
        {
            var issuesItem = new ReadingTreeItem
            {
                Key = "performance|printer-issues",
                Text = T("group.Printer issues", "Printer issues")
            };
            foreach (var issueGroup in issueGroups)
            {
                AddPrinterGroup(issuesItem, issueGroup, true);
            }
            parent.Children.Add(issuesItem);
        }

        foreach (var printerGroup in printerGroups)
        {
            AddPrinterGroup(parent, printerGroup, false);
        }
    }

    private static void AddPrinterGroup(ReadingTreeItem parent, IGrouping<string, SensorRow> printerGroup, bool issueSummaryOnly)
    {
        var printerName = printerGroup.Key;
        var printerItem = new ReadingTreeItem
        {
            Key = (issueSummaryOnly ? "performance|printer-issue|" : "performance|printer|") + StableDeviceInventoryKey(printerName),
            Text = printerName
        };
        AddReadingRows(printerItem, issueSummaryOnly ? PrinterIssueRows(printerGroup) : printerGroup);
        parent.Children.Add(printerItem);
    }

    private static IEnumerable<SensorRow> PrinterIssueRows(IEnumerable<SensorRow> rows)
    {
        return (rows ?? Enumerable.Empty<SensorRow>())
            .Where(IsPrinterIssueRow)
            .ToList();
    }

    private static bool PrinterHasIssue(IEnumerable<SensorRow> rows)
    {
        return (rows ?? Enumerable.Empty<SensorRow>()).Any(IsPrinterIssueRow);
    }

    private static bool IsPrinterIssueRow(SensorRow row)
    {
        if (row == null)
        {
            return false;
        }

        var name = CleanSensorName(row.Name);
        var value = (row.DisplayValue ?? "").Trim();
        if (name.Equals("Offline", StringComparison.OrdinalIgnoreCase))
        {
            return value.Equals("Yes", StringComparison.OrdinalIgnoreCase);
        }

        if (name.Equals("Jobs queued", StringComparison.OrdinalIgnoreCase))
        {
            double count;
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out count) && count > 0;
        }

        if (name.Equals("Error state", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(value) &&
                !value.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
                !value.Equals("Other", StringComparison.OrdinalIgnoreCase);
        }

        if (name.Equals("Status", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Extended status", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(value) &&
                !value.Equals("Idle", StringComparison.OrdinalIgnoreCase) &&
                !value.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
                !value.Equals("Other", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool IsPrinterPerformanceRow(SensorRow row)
    {
        return row != null && string.Equals(row.Type, "Performance", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(row.Hardware, "Printers", StringComparison.OrdinalIgnoreCase) ||
            (row.Hardware ?? "").StartsWith("Printer: ", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsGpuPerformanceRow(SensorRow row)
    {
        if (row == null || !string.Equals(row.Type, "Performance", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(row.Hardware, "GPU", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(row.Hardware, "GPU memory", StringComparison.OrdinalIgnoreCase);
    }

    private static string PrinterGroupName(SensorRow row)
    {
        var name = DetailValue(row, "Printer name");
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name.Trim();
        }

        var hardware = row == null ? "" : row.Hardware ?? "";
        if (hardware.StartsWith("Printer: ", StringComparison.OrdinalIgnoreCase))
        {
            return hardware.Substring("Printer: ".Length).Trim();
        }

        return string.IsNullOrWhiteSpace(hardware)
            ? T("group.Printers", "Printers")
            : ShortHardwareName(hardware);
    }

    private static void AddOverviewGroups(ReadingTreeItem parent, IEnumerable<SensorRow> rows)
    {
        var rowList = (rows ?? Enumerable.Empty<SensorRow>()).ToList();
        AddOverviewGroup(parent, rowList, "overview|system", T("group.System", "System"), IsOverviewSystemRow);
        AddOverviewGroup(parent, rowList, "overview|windows", T("group.Windows", "Windows"), IsOverviewWindowsRow);
        AddOverviewGroup(parent, rowList, "overview|firmware-board", T("group.Firmware and board", "Firmware and board"), IsOverviewFirmwareBoardRow);
        AddOverviewGroup(parent, rowList, "overview|graphics", T("group.Graphics", "Graphics"), IsOverviewGraphicsRow);
        AddOverviewGroup(parent, rowList, "overview|printer-summary", T("group.Printer summary", "Printer summary"), IsOverviewPrinterSummaryRow);
        AddOverviewGroup(parent, rowList, "overview|battery", T("type.Battery", "Battery"), IsOverviewBatteryRow);
        AddOverviewGroup(parent, rowList, "overview|accessibility", T("group.Accessibility", "Accessibility"), IsOverviewAccessibilityRow);

        var grouped = new HashSet<SensorRow>(rowList.Where(r =>
            IsOverviewSystemRow(r) ||
            IsOverviewWindowsRow(r) ||
            IsOverviewFirmwareBoardRow(r) ||
            IsOverviewGraphicsRow(r) ||
            IsOverviewPrinterSummaryRow(r) ||
            IsOverviewBatteryRow(r) ||
            IsOverviewAccessibilityRow(r)));
        var otherRows = rowList.Where(r => !grouped.Contains(r)).ToList();
        if (otherRows.Count > 0)
        {
            AddOverviewGroup(parent, otherRows, "overview|other", T("group.Other", "Other"), r => true);
        }
    }

    private static void AddOverviewGroup(ReadingTreeItem parent, IEnumerable<SensorRow> rows, string key, string text, Func<SensorRow, bool> predicate)
    {
        var groupRows = rows.Where(predicate).ToList();
        if (groupRows.Count == 0)
        {
            return;
        }

        var item = new ReadingTreeItem { Key = key, Text = text };
        AddReadingRows(item, groupRows);
        parent.Children.Add(item);
    }

    private static bool IsOverviewSystemRow(SensorRow row)
    {
        var name = CleanSensorName(row == null ? "" : row.Name);
        return name.Equals("System uptime", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("System manufacturer", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("System model", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOverviewWindowsRow(SensorRow row)
    {
        var name = CleanSensorName(row == null ? "" : row.Name);
        return name.StartsWith("Windows ", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Secure Boot", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOverviewFirmwareBoardRow(SensorRow row)
    {
        var name = CleanSensorName(row == null ? "" : row.Name);
        return name.StartsWith("BIOS ", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Baseboard ", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("SMBIOS version", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Embedded controller version", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOverviewGraphicsRow(SensorRow row)
    {
        var name = CleanSensorName(row == null ? "" : row.Name);
        return name.IndexOf("adapter RAM", StringComparison.OrdinalIgnoreCase) >= 0 ||
            (name.EndsWith(" vendor", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith("BIOS ", StringComparison.OrdinalIgnoreCase)) ||
            (name.EndsWith(" processor", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith("BIOS ", StringComparison.OrdinalIgnoreCase)) ||
            (name.EndsWith(" BIOS", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith("BIOS ", StringComparison.OrdinalIgnoreCase)) ||
            name.IndexOf("graphics BIOS", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.EndsWith(" compute capability", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(" max graphics clock", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(" max memory clock", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(" power limit", StringComparison.OrdinalIgnoreCase) ||
            name.IndexOf("driver date", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("driver version", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsOverviewPrinterSummaryRow(SensorRow row)
    {
        var name = CleanSensorName(row == null ? "" : row.Name);
        return name.Equals("Printer count", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Default printer", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOverviewBatteryRow(SensorRow row)
    {
        var name = CleanSensorName(row == null ? "" : row.Name);
        return name.StartsWith("Battery ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOverviewAccessibilityRow(SensorRow row)
    {
        var name = CleanSensorName(row == null ? "" : row.Name);
        return name.Equals("Screen reader output", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Detected screen readers", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("High contrast", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Sticky Keys", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Toggle Keys", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Filter Keys", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Show sounds", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Audio descriptions", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPciExpansionPerformanceRow(SensorRow row)
    {
        return row != null && string.Equals(ShortHardwareName(row.Hardware), "PCIe and expansion slots", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddTemperatureGroups(ReadingTreeItem parent, IEnumerable<SensorRow> rows)
    {
        AddTypedHardwareGroup(parent, rows, "temperature|system", T("group.System", "System"), IsSystemTemperatureRow);
        AddTypedHardwareGroup(parent, rows, "temperature|graphics", T("group.Graphics", "Graphics"), IsGraphicsTemperatureRow);
        AddTypedHardwareGroup(parent, rows, "temperature|storage", T("group.Storage", "Storage"), IsStorageTemperatureRow);

        var grouped = new HashSet<SensorRow>(rows.Where(r => IsSystemTemperatureRow(r) || IsGraphicsTemperatureRow(r) || IsStorageTemperatureRow(r)));
        var otherRows = rows.Where(r => !grouped.Contains(r)).ToList();
        if (otherRows.Count > 0)
        {
            AddTypedHardwareGroup(parent, otherRows, "temperature|other", T("group.Other", "Other"), r => true);
        }
    }

    private static void AddTypedHardwareGroup(ReadingTreeItem parent, IEnumerable<SensorRow> rows, string key, string text, Func<SensorRow, bool> predicate)
    {
        var groupRows = rows.Where(predicate).ToList();
        if (groupRows.Count == 0)
        {
            return;
        }

        var item = new ReadingTreeItem { Key = key, Text = text };
        AddHardwareGroups(item, groupRows);
        parent.Children.Add(item);
    }

    private static bool IsSystemTemperatureRow(SensorRow row)
    {
        var hardware = ShortHardwareName(row == null ? "" : row.Hardware);
        return IsCpuHardwareName(hardware) ||
            hardware.IndexOf("motherboard", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("system", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("chipset", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsGraphicsTemperatureRow(SensorRow row)
    {
        var hardware = ShortHardwareName(row == null ? "" : row.Hardware);
        return hardware.IndexOf("gpu", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("geforce", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("radeon", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("nvidia", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("intel graphics", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsStorageTemperatureRow(SensorRow row)
    {
        var hardware = ShortHardwareName(row == null ? "" : row.Hardware);
        return hardware.IndexOf("ssd", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("hdd", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("nvme", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("disk", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("samsung", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("seagate", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("western digital", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("wd ", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("crucial", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("micron", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("sandisk", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("kingston", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("toshiba", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("m371", StringComparison.OrdinalIgnoreCase) >= 0 ||
            hardware.IndexOf("ct", StringComparison.OrdinalIgnoreCase) == 0;
    }

    private static void AddUsbGroups(ReadingTreeItem parent, IEnumerable<SensorRow> rows)
    {
        var devices = rows
            .Where(r => !IsUsbHubOrController(r))
            .ToList();
        if (devices.Count > 0)
        {
            var devicesItem = new ReadingTreeItem { Key = "usb|devices", Text = T("group.USB devices", "Connected devices") };
            AddReadingRows(devicesItem, devices);
            parent.Children.Add(devicesItem);
        }

        var hubs = rows
            .Where(IsUsbHubOrController)
            .ToList();
        if (hubs.Count > 0)
        {
            var hubsItem = new ReadingTreeItem { Key = "usb|hubs", Text = T("group.USB hubs", "Hubs and controllers") };
            AddReadingRows(hubsItem, hubs);
            parent.Children.Add(hubsItem);
        }
    }

    private static void AddAudioGroups(ReadingTreeItem parent, IEnumerable<SensorRow> rows)
    {
        foreach (var deviceGroup in rows
            .GroupBy(r => AudioDeviceGroupName(r))
            .OrderBy(g => AudioDeviceSortIndex(g.Key))
            .ThenBy(g => g.Key))
        {
            var deviceItem = new ReadingTreeItem
            {
                Key = "audio|device|" + deviceGroup.Key,
                Text = deviceGroup.Key
            };

            var deviceRows = deviceGroup
                .Where(r => string.Equals(CleanSensorName(r.Name), "Device", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (deviceRows.Count > 0)
            {
                AddReadingRows(deviceItem, deviceRows);
            }

            AddAudioDirectionGroup(deviceItem, deviceGroup, "Playback");
            AddAudioDirectionGroup(deviceItem, deviceGroup, "Recording");

            var otherRows = deviceGroup
                .Where(r => !string.Equals(CleanSensorName(r.Name), "Device", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(AudioEndpointDirection(r), "Playback", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(AudioEndpointDirection(r), "Recording", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (otherRows.Count > 0)
            {
                var otherItem = new ReadingTreeItem { Key = "audio|device|" + deviceGroup.Key + "|other", Text = T("group.Other", "Other") };
                AddReadingRows(otherItem, otherRows);
                deviceItem.Children.Add(otherItem);
            }

            parent.Children.Add(deviceItem);
        }
    }

    private static void AddAudioDirectionGroup(ReadingTreeItem deviceItem, IEnumerable<SensorRow> rows, string direction)
    {
        var directionRows = rows
            .Where(r => string.Equals(AudioEndpointDirection(r), direction, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => AudioEndpointSortIndex(r))
            .ThenBy(r => AudioEndpointName(r))
            .ToList();
        if (directionRows.Count == 0)
        {
            return;
        }

        var directionItem = new ReadingTreeItem
        {
            Key = "audio|device|" + deviceItem.Text + "|" + direction.ToLowerInvariant(),
            Text = T("group.Audio " + direction.ToLowerInvariant(), direction)
        };
        AddReadingRows(directionItem, directionRows);
        deviceItem.Children.Add(directionItem);
    }

    private static void AddDeviceInventoryGroups(ReadingTreeItem parent, IEnumerable<SensorRow> rows)
    {
        foreach (var group in rows
            .GroupBy(r => ShortHardwareName(r.Hardware))
            .OrderBy(g => DeviceInventoryGroupSortIndex(g.Key))
            .ThenBy(g => g.Key))
        {
            var groupItem = new ReadingTreeItem
            {
                Key = "devices|group|" + group.Key,
                Text = group.Key
            };

            foreach (var deviceGroup in group
                .GroupBy(DeviceInventoryDeviceKey)
                .OrderBy(g => DeviceInventoryDeviceName(g)))
            {
                var deviceRow = DeviceInventorySummaryRow(deviceGroup);
                var deviceName = DeviceInventoryDeviceName(deviceRow);
                var deviceItem = new ReadingTreeItem
                {
                    Key = "devices|device|" + group.Key + "|" + deviceGroup.Key,
                    Text = deviceName,
                    Row = deviceRow
                };
                groupItem.Children.Add(deviceItem);
            }

            parent.Children.Add(groupItem);
        }
    }

    private static string DeviceInventoryDeviceName(SensorRow row)
    {
        if (row != null && row.Details != null)
        {
            string value;
            if (row.Details.TryGetValue("Device name", out value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return row == null || string.IsNullOrWhiteSpace(row.Hardware) ? T("group.Other", "Other") : ShortHardwareName(row.Hardware);
    }

    private static string DeviceInventoryDeviceName(IEnumerable<SensorRow> rows)
    {
        var list = (rows ?? Enumerable.Empty<SensorRow>()).Where(r => r != null).ToList();
        foreach (var row in list)
        {
            var name = DeviceInventoryDeviceName(row);
            if (!string.IsNullOrWhiteSpace(name) && name != ShortHardwareName(row.Hardware))
            {
                return name;
            }
        }

        var first = list.FirstOrDefault();
        return first == null ? T("group.Other", "Other") : DeviceInventoryDeviceName(first);
    }

    private static string DeviceInventoryDeviceKey(SensorRow row)
    {
        if (row == null || string.IsNullOrWhiteSpace(row.Identifier))
        {
            return "";
        }

        if (row.Identifier.StartsWith("device/", StringComparison.OrdinalIgnoreCase) &&
            row.Identifier.IndexOf('/', "device/".Length) < 0)
        {
            return row.Identifier;
        }

        var lastSeparator = row.Identifier.LastIndexOf('/');
        return lastSeparator > 0 ? row.Identifier.Substring(0, lastSeparator) : row.Identifier;
    }

    private static SensorRow DeviceInventorySummaryRow(IEnumerable<SensorRow> rows)
    {
        var list = (rows ?? Enumerable.Empty<SensorRow>()).Where(r => r != null).ToList();
        if (list.Count == 0)
        {
            return null;
        }

        if (list.Count == 1 && list[0].Details != null && list[0].Details.Count > 0)
        {
            return list[0];
        }

        var first = list[0];
        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in list)
        {
            if (row.Details != null)
            {
                foreach (var detail in row.Details)
                {
                    if (!string.IsNullOrWhiteSpace(detail.Key) && !string.IsNullOrWhiteSpace(detail.Value) && !details.ContainsKey(detail.Key))
                    {
                        details[detail.Key] = detail.Value;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(row.Name) && !string.IsNullOrWhiteSpace(row.DisplayValue) && !details.ContainsKey(row.Name))
            {
                details[row.Name] = row.DisplayValue;
            }
        }

        var deviceName = DeviceInventoryDeviceName(list);
        return new SensorRow
        {
            Type = "Devices",
            Hardware = first.Hardware,
            Name = deviceName,
            Identifier = DeviceInventoryDeviceKey(first),
            DisplayValue = first.DisplayValue,
            Source = first.Source,
            Details = details
        };
    }

    private static int DeviceInventoryGroupSortIndex(string group)
    {
        if (DeviceInventoryGroupEquals(group, "group.Device nonworking", "Non-working devices")) return 0;
        if (DeviceInventoryGroupEquals(group, "group.Device PCI and system", "PCI and system devices")) return 1;
        if (DeviceInventoryGroupEquals(group, "group.Device storage", "Storage devices and controllers")) return 2;
        if (DeviceInventoryGroupEquals(group, "group.Device USB", "USB devices and controllers")) return 3;
        if (DeviceInventoryGroupEquals(group, "group.Device network", "Network devices")) return 4;
        if (DeviceInventoryGroupEquals(group, "group.Device audio", "Audio devices")) return 5;
        if (DeviceInventoryGroupEquals(group, "group.Device display", "Display devices")) return 6;
        if (DeviceInventoryGroupEquals(group, "group.Device input", "Input devices")) return 7;
        if (DeviceInventoryGroupEquals(group, "group.Device bluetooth", "Bluetooth")) return 8;
        if (DeviceInventoryGroupEquals(group, "group.Device imaging", "Cameras and imaging")) return 9;
        if (DeviceInventoryGroupEquals(group, "group.Device printers", "Printers")) return 10;
        if (DeviceInventoryGroupEquals(group, "group.Device security", "Security devices")) return 11;
        return 99;
    }

    private static bool DeviceInventoryGroupEquals(string group, string key, string fallback)
    {
        return string.Equals(group, fallback, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(group, T(key, fallback), StringComparison.OrdinalIgnoreCase);
    }

    private static void AddCpuPerformanceGroups(ReadingTreeItem parent, IEnumerable<SensorRow> rows)
    {
        AddNamedReadingGroup(parent, rows, "cpu|identity", T("group.CPU identity", "Identity"), new[]
        {
            "CPU name",
            "CPU vendor",
            "CPU architecture",
            "CPU socket",
            "CPU processor ID"
        });
        AddNamedReadingGroup(parent, rows, "cpu|cores-clocks", T("group.CPU cores and clocks", "Cores and clocks"), new[]
        {
            "CPU usage",
            "CPU cores",
            "CPU threads",
            "CPU current clock",
            "CPU max clock"
        });
        AddNamedReadingGroup(parent, rows, "cpu|features", T("group.CPU features", "Features"), new[]
        {
            "CPU instruction sets",
            "CPU virtualization extensions",
            "CPU virtualization enabled in firmware",
            "CPU hardware VM memory translation (SLAT/EPT/NPT)",
            "CPU data execution prevention"
        });
        AddNamedReadingGroup(parent, rows, "cpu|cache", T("group.CPU cache", "Cache"), new[]
        {
            "CPU L1 cache",
            "CPU L2 cache",
            "CPU L3 cache"
        });

        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CPU name",
            "CPU vendor",
            "CPU architecture",
            "CPU socket",
            "CPU processor ID",
            "CPU usage",
            "CPU cores",
            "CPU threads",
            "CPU current clock",
            "CPU max clock",
            "CPU instruction sets",
            "CPU virtualization extensions",
            "CPU virtualization enabled in firmware",
            "CPU hardware VM memory translation (SLAT/EPT/NPT)",
            "CPU data execution prevention",
            "CPU L1 cache",
            "CPU L2 cache",
            "CPU L3 cache"
        };

        var remaining = rows.Where(r => !handled.Contains(CleanSensorName(r.Name))).ToList();
        if (remaining.Count > 0)
        {
            AddNamedReadingGroup(parent, remaining, "cpu|other", T("group.Other", "Other"), remaining.Select(r => CleanSensorName(r.Name)).ToArray());
        }
    }

    private static void AddNamedReadingGroup(ReadingTreeItem parent, IEnumerable<SensorRow> rows, string key, string text, string[] names)
    {
        var wanted = new HashSet<string>(names ?? new string[0], StringComparer.OrdinalIgnoreCase);
        var groupRows = rows.Where(r => wanted.Contains(CleanSensorName(r.Name))).ToList();
        if (groupRows.Count == 0)
        {
            return;
        }

        var item = new ReadingTreeItem { Key = key, Text = text };
        AddReadingRows(item, groupRows);
        parent.Children.Add(item);
    }

    private static string AudioDeviceGroupName(SensorRow row)
    {
        if (row == null)
        {
            return T("group.Audio devices", "Audio devices");
        }

        var deviceName = DetailValue(row, "Device name");
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            return deviceName;
        }

        var hardware = ShortHardwareName(row.Hardware);
        return string.IsNullOrWhiteSpace(hardware) ? T("group.Audio devices", "Audio devices") : hardware;
    }

    private static int AudioDeviceSortIndex(string name)
    {
        return string.Equals(name, T("group.Audio devices", "Audio devices"), StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    private static string AudioEndpointDirection(SensorRow row)
    {
        return DetailValue(row, "Endpoint direction");
    }

    private static string AudioEndpointName(SensorRow row)
    {
        var name = DetailValue(row, "Name");
        return string.IsNullOrWhiteSpace(name) ? ShortHardwareName(row == null ? "" : row.Hardware) : name;
    }

    private static int AudioEndpointSortIndex(SensorRow row)
    {
        var name = AudioEndpointName(row);
        if (name.IndexOf("main", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("speaker", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return 0;
        }

        if (name.IndexOf("spdif", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.IndexOf("s/pdif", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return 2;
        }

        return 1;
    }

    private static string DetailValue(SensorRow row, string key)
    {
        if (row == null || row.Details == null || string.IsNullOrWhiteSpace(key))
        {
            return "";
        }

        string value;
        return row.Details.TryGetValue(key, out value) ? value ?? "" : "";
    }

    private static bool IsUsbHubOrController(SensorRow row)
    {
        if (row == null)
        {
            return false;
        }

        var name = (row.Hardware + " " + row.DisplayValue).ToLowerInvariant();
        return name.IndexOf("hub") >= 0 || name.IndexOf("controller") >= 0 || name.IndexOf("host") >= 0;
    }

    private static bool IsSystemPerformanceHardware(string hardware)
    {
        return string.Equals(hardware, "CPU", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hardware, "Memory", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hardware, "System", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOverviewHardware(string hardware)
    {
        return string.Equals(hardware, "Overview", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hardware, "System", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hardware, "BIOS", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hardware, "GPU", StringComparison.OrdinalIgnoreCase);
    }

    private static int PerformanceHardwareSortIndex(string hardware)
    {
        if (string.Equals(hardware, "CPU", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (string.Equals(hardware, "Memory", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 2;
    }

    private static void AddReadingRows(ReadingTreeItem parent, IEnumerable<SensorRow> rows)
    {
        foreach (var row in rows
            .OrderBy(r => TypeSortIndex(r.Type))
            .ThenBy(r => ReadingSortIndex(r.Name))
            .ThenBy(r => CleanSensorName(r.Name)))
        {
            parent.Children.Add(new ReadingTreeItem
            {
                Key = "row|" + RowSettingsKey(row),
                Text = IsDeviceSummaryType(row.Type) ? ShortHardwareName(row.Hardware) + ": " + FormatValue(row) : DisplayReadingName(row.Name) + ": " + FormatValue(row),
                Row = row
            });
        }
    }

    private static bool IsDeviceSummaryType(string type)
    {
        return string.Equals(type, "USB", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "Audio", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "Display", StringComparison.OrdinalIgnoreCase);
    }

    private static string DisplayReadingName(string name)
    {
        var clean = CleanSensorName(name);
        var translated = T("reading." + clean, clean);
        if (!string.Equals(translated, clean, StringComparison.Ordinal))
        {
            return translated;
        }

        if (clean.StartsWith("Temperature #", StringComparison.OrdinalIgnoreCase))
        {
            return T("reading.Temperature #", "Temperature #") + clean.Substring("Temperature #".Length);
        }

        return translated;
    }

}
