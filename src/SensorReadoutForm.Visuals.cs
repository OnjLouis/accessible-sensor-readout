using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private enum ReadingVisualSignal
    {
        Neutral,
        Good,
        Active,
        Caution,
        Critical,
        Offline,
        Unavailable,
        Paused
    }

    private sealed class TrayBadgeVisual
    {
        public SensorRow Row;
        public ReadingVisualSignal Signal;
        public string Text = "SR";
    }

    internal static Icon LoadApplicationIcon()
    {
        try
        {
            using (var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
            {
                if (icon != null)
                {
                    return (Icon)icon.Clone();
                }
            }
        }
        catch
        {
        }

        return CreateFallbackApplicationIcon();
    }

    private static Icon CreateFallbackApplicationIcon()
    {
        using (var bitmap = new Bitmap(32, 32))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var background = new SolidBrush(Color.FromArgb(35, 41, 47)))
            using (var goodPen = new Pen(Color.FromArgb(27, 153, 111), 3.2f))
            using (var cautionPen = new Pen(Color.FromArgb(219, 139, 35), 3.2f))
            using (var criticalPen = new Pen(Color.FromArgb(203, 61, 68), 3.2f))
            using (var needlePen = new Pen(Color.White, 2.2f))
            using (var hub = new SolidBrush(Color.White))
            {
                graphics.FillEllipse(background, 1, 1, 30, 30);
                graphics.DrawArc(goodPen, 5, 5, 22, 22, 135, 105);
                graphics.DrawArc(cautionPen, 5, 5, 22, 22, 245, 55);
                graphics.DrawArc(criticalPen, 5, 5, 22, 22, 305, 50);
                graphics.DrawLine(needlePen, 16, 17, 11, 8);
                graphics.FillEllipse(hub, 13.5f, 14.5f, 5, 5);
            }

            return IconFromBitmap(bitmap);
        }
    }

    private void ApplyMainWindowVisualStyle(FlowLayoutPanel topPanel, SplitContainer splitContainer)
    {
        if (topPanel != null)
        {
            topPanel.BackColor = SystemColors.ControlLightLight;
        }

        if (splitContainer != null)
        {
            splitContainer.SplitterWidth = 6;
            splitContainer.BackColor = SystemColors.ControlDark;
            splitContainer.Panel1.BackColor = SystemColors.Window;
            splitContainer.Panel2.BackColor = SystemColors.Window;
        }

        if (deviceList != null)
        {
            deviceList.BackColor = SystemColors.Window;
            deviceList.ForeColor = SystemColors.WindowText;
        }

        if (readingTree != null)
        {
            readingTree.BackColor = SystemColors.Window;
            readingTree.ForeColor = SystemColors.WindowText;
        }

        if (selectedMeterValueLabel != null)
        {
            selectedMeterValueLabel.BackColor = SystemColors.ControlLightLight;
            selectedMeterValueLabel.ForeColor = SystemColors.ControlText;
            selectedMeterValueLabel.Font = new Font(Font, FontStyle.Bold);
        }

        if (statusLabel != null)
        {
            statusLabel.BackColor = SystemColors.ControlLight;
            statusLabel.ForeColor = SystemColors.ControlText;
            statusLabel.BorderStyle = BorderStyle.FixedSingle;
        }

        UpdatePauseVisualState();
    }

    private void UpdatePauseVisualState()
    {
        if (pauseCheckBox == null)
        {
            return;
        }

        if (pauseCheckBox.Checked)
        {
            pauseCheckBox.ForeColor = SystemInformation.HighContrast
                ? SystemColors.Highlight
                : Color.FromArgb(151, 82, 0);
        }
        else
        {
            pauseCheckBox.ForeColor = SystemColors.ControlText;
        }
    }

    private TrayBadgeVisual BuildTrayBadgeVisual(IList<SensorRow> rows)
    {
        if (!settings.AutoRefreshEnabled && !reportViewMode)
        {
            return new TrayBadgeVisual { Signal = ReadingVisualSignal.Paused, Text = "||" };
        }

        var availableRows = (rows ?? new List<SensorRow>())
            .Where(r => r != null)
            .ToList();
        if (availableRows.Count == 0)
        {
            return new TrayBadgeVisual { Signal = ReadingVisualSignal.Unavailable, Text = "--" };
        }

        var baseRow = availableRows.FirstOrDefault(HasVisualReadingValue) ?? availableRows[0];
        var baseSignal = VisualSignalForRow(baseRow);
        var result = new TrayBadgeVisual
        {
            Row = baseRow,
            Signal = baseSignal,
            Text = TrayIconText(baseRow, baseSignal)
        };

        var overrideRow = availableRows
            .Select(r => new { Row = r, Signal = VisualSignalForRow(r) })
            .Where(i => VisualSignalPriority(i.Signal) >= VisualSignalPriority(ReadingVisualSignal.Offline))
            .OrderByDescending(i => VisualSignalPriority(i.Signal))
            .FirstOrDefault();
        if (overrideRow != null && VisualSignalPriority(overrideRow.Signal) > VisualSignalPriority(baseSignal))
        {
            result.Row = overrideRow.Row;
            result.Signal = overrideRow.Signal;
            result.Text = TrayIconText(overrideRow.Row, overrideRow.Signal);
        }

        return result;
    }

    private static int VisualSignalPriority(ReadingVisualSignal signal)
    {
        switch (signal)
        {
            case ReadingVisualSignal.Critical: return 6;
            case ReadingVisualSignal.Caution: return 5;
            case ReadingVisualSignal.Offline: return 4;
            case ReadingVisualSignal.Unavailable: return 3;
            case ReadingVisualSignal.Good: return 2;
            case ReadingVisualSignal.Active: return 2;
            default: return 1;
        }
    }

    private static ReadingVisualSignal VisualSignalForRow(SensorRow row)
    {
        if (row == null || !HasVisualReadingValue(row))
        {
            return ReadingVisualSignal.Unavailable;
        }

        var name = ((row.Hardware ?? "") + " " + (row.Name ?? "")).Trim().ToLowerInvariant();
        var display = (row.DisplayValue ?? "").Trim().ToLowerInvariant();
        if (IsInactiveStatusText(display) || IsInactiveStatusText(FormatValue(row)))
        {
            return ReadingVisualSignal.Offline;
        }

        if (IsStatusLikeReadingName(name))
        {
            if (IsCriticalStatusText(display)) return ReadingVisualSignal.Critical;
            if (IsCautionStatusText(display)) return ReadingVisualSignal.Caution;
            if (IsHealthyStatusText(display)) return ReadingVisualSignal.Good;
        }

        if (string.Equals(row.Type, "Temperature", StringComparison.OrdinalIgnoreCase) && row.Value.HasValue)
        {
            if (row.Value.Value >= 80f) return ReadingVisualSignal.Critical;
            if (row.Value.Value >= 65f) return ReadingVisualSignal.Caution;
            return ReadingVisualSignal.Good;
        }

        var percent = ExtractPercent(row);
        if (percent.HasValue)
        {
            var value = Math.Max(0f, Math.Min(100f, percent.Value));
            if (name.Contains("signal") || name.Contains("strength"))
            {
                if (value <= 10f) return ReadingVisualSignal.Critical;
                if (value <= 25f) return ReadingVisualSignal.Caution;
                return ReadingVisualSignal.Good;
            }

            if (name.Contains("battery") || name.Contains("charge level") || name.Contains("remaining charge"))
            {
                if (value <= 10f) return ReadingVisualSignal.Critical;
                if (value <= 25f) return ReadingVisualSignal.Caution;
                return ReadingVisualSignal.Good;
            }

            if (name.Contains("health") || name.Contains("life remaining"))
            {
                if (value <= 50f) return ReadingVisualSignal.Critical;
                if (value <= 80f) return ReadingVisualSignal.Caution;
                return ReadingVisualSignal.Good;
            }

            if (name.Contains("free") || name.Contains("available"))
            {
                if (value <= 10f) return ReadingVisualSignal.Critical;
                if (value <= 20f) return ReadingVisualSignal.Caution;
                return ReadingVisualSignal.Neutral;
            }

            if (name.Contains("wear"))
            {
                if (value >= 90f) return ReadingVisualSignal.Critical;
                if (value >= 75f) return ReadingVisualSignal.Caution;
                return ReadingVisualSignal.Neutral;
            }

            if (name.Contains("memory") || name.Contains("space used") || name.Contains("used space") || name.Contains("disk usage"))
            {
                if (value >= 95f) return ReadingVisualSignal.Critical;
                if (value >= 85f) return ReadingVisualSignal.Caution;
                return ReadingVisualSignal.Neutral;
            }

            if (name.Contains("cpu") || name.Contains("gpu") || name.Contains("usage") || name.Contains("load") || name.Contains("activity"))
            {
                return value >= 95f ? ReadingVisualSignal.Caution : ReadingVisualSignal.Active;
            }
        }

        if (string.Equals(row.Type, "Fan", StringComparison.OrdinalIgnoreCase))
        {
            return ReadingVisualSignal.Active;
        }

        return ReadingVisualSignal.Neutral;
    }

    private static bool HasVisualReadingValue(SensorRow row)
    {
        if (row == null)
        {
            return false;
        }

        if (row.Value.HasValue && !float.IsNaN(row.Value.Value) && !float.IsInfinity(row.Value.Value))
        {
            return true;
        }

        var value = (row.DisplayValue ?? "").Trim();
        if (value.Length == 0)
        {
            return false;
        }

        var normalized = value.ToLowerInvariant();
        return normalized != "n/a" && normalized != "unknown" && normalized != "not available" &&
            normalized != "unavailable" && normalized != "no data" && normalized != "not ready";
    }

    private static bool IsCriticalStatusText(string text)
    {
        var value = (text ?? "").Trim().ToLowerInvariant();
        return value == "critical" || value == "failed" || value == "failure" || value == "error" ||
            value == "fault" || value == "problem" || value.StartsWith("critical ", StringComparison.Ordinal) ||
            value.StartsWith("failed ", StringComparison.Ordinal);
    }

    private static bool IsCautionStatusText(string text)
    {
        var value = (text ?? "").Trim().ToLowerInvariant();
        return value == "warning" || value == "degraded" || value == "attention" ||
            value.StartsWith("warning ", StringComparison.Ordinal) || value.StartsWith("degraded ", StringComparison.Ordinal);
    }

    private static bool IsHealthyStatusText(string text)
    {
        var value = (text ?? "").Trim().ToLowerInvariant();
        return value == "ok" || value == "good" || value == "healthy" || value == "up" ||
            value == "online" || value == "connected" || value == "available" || value == "ready";
    }

    private static Color TrayBadgeColor(TrayBadgeVisual visual)
    {
        if (visual == null)
        {
            return Color.FromArgb(74, 85, 99);
        }

        switch (visual.Signal)
        {
            case ReadingVisualSignal.Good: return Color.FromArgb(18, 122, 84);
            case ReadingVisualSignal.Active: return Color.FromArgb(42, 101, 174);
            case ReadingVisualSignal.Caution: return Color.FromArgb(190, 108, 0);
            case ReadingVisualSignal.Critical: return Color.FromArgb(180, 40, 48);
            case ReadingVisualSignal.Offline: return Color.FromArgb(91, 98, 108);
            case ReadingVisualSignal.Unavailable: return Color.FromArgb(78, 84, 92);
            case ReadingVisualSignal.Paused: return Color.FromArgb(173, 99, 0);
        }

        if (visual.Row != null && string.Equals(visual.Row.Type, "SMART", StringComparison.OrdinalIgnoreCase))
        {
            return Color.FromArgb(108, 78, 158);
        }

        return Color.FromArgb(70, 84, 101);
    }

    private static Icon CreateTrayIcon(TrayBadgeVisual visual)
    {
        using (var bitmap = new Bitmap(16, 16))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
            DrawTrayBadgeBackground(graphics, visual);

            var text = visual == null || string.IsNullOrWhiteSpace(visual.Text) ? "SR" : visual.Text;
            using (var font = new Font("Segoe UI", text.Length > 2 ? 5.5f : 6.5f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(Color.White))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                graphics.DrawString(text, font, brush, new RectangleF(0, 1, 16, 14), format);
            }

            return IconFromBitmap(bitmap);
        }
    }

    private static void DrawTrayBadgeBackground(Graphics graphics, TrayBadgeVisual visual)
    {
        var signal = visual == null ? ReadingVisualSignal.Neutral : visual.Signal;
        var color = TrayBadgeColor(visual);
        using (var background = new SolidBrush(color))
        using (var border = new Pen(Color.White))
        {
            if (signal == ReadingVisualSignal.Caution)
            {
                var points = new[]
                {
                    new PointF(4, 0), new PointF(11, 0), new PointF(15, 4), new PointF(15, 11),
                    new PointF(11, 15), new PointF(4, 15), new PointF(0, 11), new PointF(0, 4)
                };
                graphics.FillPolygon(background, points);
                graphics.DrawPolygon(border, points);
            }
            else if (signal == ReadingVisualSignal.Critical)
            {
                var points = new[]
                {
                    new PointF(3, 0), new PointF(12, 0), new PointF(15, 3), new PointF(15, 12),
                    new PointF(12, 15), new PointF(3, 15), new PointF(0, 12), new PointF(0, 3)
                };
                graphics.FillPolygon(background, points);
                graphics.DrawPolygon(border, points);
                graphics.DrawRectangle(border, 3, 3, 9, 9);
            }
            else if (signal == ReadingVisualSignal.Offline || signal == ReadingVisualSignal.Paused)
            {
                graphics.FillRectangle(background, 1, 1, 14, 14);
                graphics.DrawRectangle(border, 1, 1, 14, 14);
            }
            else
            {
                graphics.FillEllipse(background, 0, 0, 15, 15);
                graphics.DrawEllipse(border, 0, 0, 15, 15);
                if (signal == ReadingVisualSignal.Unavailable)
                {
                    graphics.DrawEllipse(border, 3, 3, 9, 9);
                }
            }
        }
    }

    private static string TrayIconText(SensorRow row, ReadingVisualSignal signal)
    {
        if (signal == ReadingVisualSignal.Paused) return "||";
        if (signal == ReadingVisualSignal.Offline) return "X";
        if (signal == ReadingVisualSignal.Unavailable) return "--";
        if (row == null || !row.Value.HasValue)
        {
            return signal == ReadingVisualSignal.Critical || signal == ReadingVisualSignal.Caution ? "!" : "SR";
        }

        if (string.Equals(row.Type, "Temperature", StringComparison.OrdinalIgnoreCase))
        {
            var unit = NormalizeTemperatureUnit(activeTemperatureUnit);
            var value = unit == "F" || unit == "FC"
                ? (row.Value.Value * 9.0 / 5.0) + 32.0
                : row.Value.Value;
            return FormatNumber(Math.Round(value, 0), "0");
        }

        if (string.Equals(row.Type, "Fan", StringComparison.OrdinalIgnoreCase))
        {
            var rpm = row.Value.Value;
            return rpm >= 1000 ? FormatNumber(Math.Round(rpm / 1000, 1), "0.#") + "k" : FormatNumber(Math.Round(rpm, 0), "0");
        }

        var display = FormatNumber(Math.Round(row.Value.Value, 0), "0");
        return display.Length <= 3 ? display : display.Substring(0, 3);
    }

    private static string TrayBadgeSignature(TrayBadgeVisual visual)
    {
        if (visual == null)
        {
            return "none";
        }

        return ((int)visual.Signal).ToString(CultureInfo.InvariantCulture) + "|" +
            TrayBadgeColor(visual).ToArgb().ToString(CultureInfo.InvariantCulture) + "|" + (visual.Text ?? "");
    }

    private static int MeterVisualState(SensorRow row, float percent)
    {
        var name = ((row == null ? "" : row.Hardware) + " " + (row == null ? "" : row.Name)).ToLowerInvariant();
        if (name.Contains("signal") || name.Contains("strength") || name.Contains("battery") ||
            name.Contains("charge level") || name.Contains("health") || name.Contains("life remaining") ||
            name.Contains("free") || name.Contains("available"))
        {
            if (percent <= 10f) return MeterProgressBar.ErrorState;
            if (percent <= 25f) return MeterProgressBar.WarningState;
            return MeterProgressBar.NormalState;
        }

        if (name.Contains("wear"))
        {
            if (percent >= 90f) return MeterProgressBar.ErrorState;
            if (percent >= 75f) return MeterProgressBar.WarningState;
            return MeterProgressBar.NormalState;
        }

        if (name.Contains("memory") || name.Contains("space used") || name.Contains("used space") || name.Contains("disk usage"))
        {
            if (percent >= 95f) return MeterProgressBar.ErrorState;
            if (percent >= 85f) return MeterProgressBar.WarningState;
            return MeterProgressBar.NormalState;
        }

        return percent >= 95f ? MeterProgressBar.WarningState : MeterProgressBar.NormalState;
    }
}
