using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private List<SensorRow> AddDataSourceSummaryRows(List<SensorRow> rows)
    {
        rows = rows ?? new List<SensorRow>();
        var sourceGroups = rows
            .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Source))
            .GroupBy(r => r.Source.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key)
            .ToList();
        foreach (var group in sourceGroups)
        {
            var details = group
                .GroupBy(r => DisplayTypeName(r.Type), StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key)
                .ToDictionary(g => g.Key, g => g.Count() + " " + (g.Count() == 1 ? T("ui.reading", "reading") : T("ui.readings", "readings")), StringComparer.OrdinalIgnoreCase);
            rows.Add(new SensorRow
            {
                Type = "Performance",
                Hardware = T("ui.Data sources", "Data sources"),
                Name = group.Key,
                Identifier = "data-source/" + SafeIdentifier(group.Key),
                DisplayValue = group.Count() + " " + (group.Count() == 1 ? T("ui.reading", "reading") : T("ui.readings", "readings")),
                Source = "Sensor Readout",
                Details = details
            });
        }

        return rows;
    }

    private static string SafeIdentifier(string value)
    {
        var builder = new StringBuilder();
        foreach (var ch in value ?? "")
        {
            builder.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-');
        }

        return builder.ToString().Trim('-');
    }

    private void ToggleTrendLoggingEnabled()
    {
        settings.TrendLoggingEnabled = !settings.TrendLoggingEnabled;
        SaveSettings(settings);
        UpdateTrendLoggingMenuState();
        statusLabel.Text = settings.TrendLoggingEnabled
            ? T("status.Reading history logging is on.", "Reading history logging is on.")
            : T("status.Reading history logging is off.", "Reading history logging is off.");
    }

    private void ToggleSelectedReadingTrendLogging()
    {
        var row = GetSelectedReadingRow();
        if (reportViewMode || !IsSelectableReadoutRow(row))
        {
            System.Media.SystemSounds.Beep.Play();
            statusLabel.Text = T("status.Select a reading that can be logged.", "Select a reading that can be logged.");
            return;
        }

        settings.TrendLoggingKeys = settings.TrendLoggingKeys ?? new List<string>();
        var key = RowSettingsKey(row);
        if (settings.TrendLoggingKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
        {
            settings.TrendLoggingKeys = settings.TrendLoggingKeys.Where(k => !string.Equals(k, key, StringComparison.OrdinalIgnoreCase)).ToList();
            statusLabel.Text = T("status.Removed reading from history log.", "Removed reading from history log.");
        }
        else
        {
            settings.TrendLoggingKeys.Add(key);
            settings.TrendLoggingEnabled = true;
            statusLabel.Text = T("status.Added reading to history log.", "Added reading to history log.");
        }

        SaveSettings(settings);
        UpdateTrendLoggingMenuState();
        UpdateSelectedTreeCommandVisibility();
    }

    private bool CanToggleSelectedReadingTrendLogging()
    {
        return !reportViewMode && IsSelectableReadoutRow(GetSelectedReadingRow());
    }

    private string SelectedTrendLoggingMenuText()
    {
        var row = GetSelectedReadingRow();
        var key = RowSettingsKey(row);
        return settings.TrendLoggingKeys != null && settings.TrendLoggingKeys.Contains(key, StringComparer.OrdinalIgnoreCase)
            ? T("ui.Remove from history &log", "Remove from history &log")
            : T("ui.Add to history &log", "Add to history &log");
    }

    private void UpdateTrendLoggingMenuState()
    {
        if (trendLoggingMenuItem != null)
        {
            trendLoggingMenuItem.Checked = settings.TrendLoggingEnabled;
        }
        var visible = CanToggleSelectedReadingTrendLogging();
        if (editTrendLogMenuItem != null)
        {
            editTrendLogMenuItem.Visible = visible;
            editTrendLogMenuItem.Text = SelectedTrendLoggingMenuText();
        }
        if (treeTrendLogMenuItem != null)
        {
            treeTrendLogMenuItem.Visible = visible;
            treeTrendLogMenuItem.Text = SelectedTrendLoggingMenuText();
        }
    }

    private void LogTrendRows(List<SensorRow> rows)
    {
        if (!settings.TrendLoggingEnabled || settings.TrendLoggingKeys == null || settings.TrendLoggingKeys.Count == 0 || rows == null || rows.Count == 0)
        {
            return;
        }

        try
        {
            var wanted = new HashSet<string>(settings.TrendLoggingKeys, StringComparer.OrdinalIgnoreCase);
            var selected = rows.Where(r => wanted.Contains(RowSettingsKey(r))).ToList();
            if (selected.Count == 0)
            {
                return;
            }

            var path = GetTrendLogFilePath();
            EnsureDirectoryForFile(path);
            var exists = System.IO.File.Exists(path);
            var lines = new List<string>();
            if (!exists)
            {
                lines.Add("Timestamp,Type,Hardware,Name,Value,DisplayValue,Source,Key");
            }
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            foreach (var row in selected)
            {
                lines.Add(string.Join(",", new[]
                {
                    Csv(timestamp),
                    Csv(row.Type),
                    Csv(row.Hardware),
                    Csv(row.Name),
                    Csv(row.Value.HasValue ? row.Value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : ""),
                    Csv(row.DisplayValue),
                    Csv(row.Source),
                    Csv(RowSettingsKey(row))
                }));
            }

            System.IO.File.AppendAllLines(path, lines.ToArray(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            LogMessage("Error", "Trend logging failed. " + ex.Message);
        }
    }

    private static string Csv(string value)
    {
        value = value ?? "";
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string GetTrendLogFilePath()
    {
        return System.IO.Path.Combine(GetLogsFolderPath(), "ReadingHistory-" + SafeReportFileName(Environment.MachineName) + "-" + DateTime.Now.ToString("yyyyMMdd") + ".csv");
    }
}
