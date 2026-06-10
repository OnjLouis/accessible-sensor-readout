using System;
using System.Drawing;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private Form visualSpokenFeedbackForm;
    private Label visualSpokenFeedbackLabel;
    private Timer visualSpokenFeedbackTimer;

    private bool ShowVisualSpokenFeedbackIfNeeded(string text)
    {
        if (settings == null || !settings.VisualSpokenFeedbackEnabled)
        {
            return false;
        }

        ShowVisualSpokenFeedback(text);
        return true;
    }

    private void ShowVisualSpokenFeedback(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            text = "Sensor Readout";
        }

        if (InvokeRequired)
        {
            BeginInvoke((MethodInvoker)delegate { ShowVisualSpokenFeedback(text); });
            return;
        }

        EnsureVisualSpokenFeedbackForm();
        visualSpokenFeedbackLabel.Text = text;
        visualSpokenFeedbackForm.Size = VisualSpokenFeedbackSize(text);
        PositionVisualSpokenFeedbackForm();
        visualSpokenFeedbackForm.Show();
        visualSpokenFeedbackForm.TopMost = true;
        visualSpokenFeedbackForm.BringToFront();

        visualSpokenFeedbackTimer.Stop();
        visualSpokenFeedbackTimer.Interval = NormalizeVisualSpokenFeedbackTimeoutSeconds(settings.VisualSpokenFeedbackTimeoutSeconds) * 1000;
        visualSpokenFeedbackTimer.Start();
    }

    private void EnsureVisualSpokenFeedbackForm()
    {
        if (visualSpokenFeedbackForm != null && !visualSpokenFeedbackForm.IsDisposed)
        {
            return;
        }

        visualSpokenFeedbackLabel = new Label
        {
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            Font = new Font(Font.FontFamily, Math.Max(11f, Font.Size + 1f), FontStyle.Regular),
            Padding = new Padding(14),
            TextAlign = ContentAlignment.MiddleLeft
        };

        visualSpokenFeedbackForm = new VisualSpokenFeedbackForm
        {
            FormBorderStyle = FormBorderStyle.FixedSingle,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            TopMost = true,
            Text = "Sensor Readout",
            Icon = Icon ?? SystemIcons.Application,
            ShowIcon = true,
            ControlBox = false,
            MinimizeBox = false,
            MaximizeBox = false
        };
        visualSpokenFeedbackForm.Controls.Add(visualSpokenFeedbackLabel);

        visualSpokenFeedbackTimer = new Timer();
        visualSpokenFeedbackTimer.Tick += delegate
        {
            visualSpokenFeedbackTimer.Stop();
            if (visualSpokenFeedbackForm != null && !visualSpokenFeedbackForm.IsDisposed)
            {
                visualSpokenFeedbackForm.Hide();
            }
        };
    }

    private Size VisualSpokenFeedbackSize(string text)
    {
        var working = Screen.PrimaryScreen == null ? SystemInformation.WorkingArea : Screen.PrimaryScreen.WorkingArea;
        var width = Math.Min(Math.Max(360, working.Width / 3), Math.Max(360, working.Width - 80));
        var measured = TextRenderer.MeasureText(text ?? "", visualSpokenFeedbackLabel.Font, new Size(width - 28, 0), TextFormatFlags.WordBreak);
        var height = Math.Min(Math.Max(96, measured.Height + 52), Math.Max(120, working.Height / 3));
        return new Size(width, height);
    }

    private void PositionVisualSpokenFeedbackForm()
    {
        var working = Screen.PrimaryScreen == null ? SystemInformation.WorkingArea : Screen.PrimaryScreen.WorkingArea;
        var size = visualSpokenFeedbackForm.Size;
        var margin = 24;
        int x;
        int y;

        switch (NormalizeVisualSpokenFeedbackPlacement(settings.VisualSpokenFeedbackPlacement))
        {
            case "TopLeft":
                x = working.Left + margin;
                y = working.Top + margin;
                break;
            case "TopRight":
                x = working.Right - size.Width - margin;
                y = working.Top + margin;
                break;
            case "BottomLeft":
                x = working.Left + margin;
                y = working.Bottom - size.Height - margin;
                break;
            case "Center":
                x = working.Left + (working.Width - size.Width) / 2;
                y = working.Top + (working.Height - size.Height) / 2;
                break;
            default:
                x = working.Right - size.Width - margin;
                y = working.Bottom - size.Height - margin;
                break;
        }

        visualSpokenFeedbackForm.Location = new Point(Math.Max(working.Left, x), Math.Max(working.Top, y));
    }

    private sealed class VisualSpokenFeedbackForm : Form
    {
        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_NOACTIVATE = 0x08000000;
                var parameters = base.CreateParams;
                parameters.ExStyle |= WS_EX_NOACTIVATE;
                return parameters;
            }
        }
    }
}
