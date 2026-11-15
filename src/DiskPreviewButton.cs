using System;
using System.Windows.Forms;

internal sealed class DiskPreviewButton : Button
{
    private const int HoldDelayMilliseconds = 250;
    private readonly Timer holdTimer = new Timer { Interval = HoldDelayMilliseconds };
    private bool pressing;
    private bool holding;
    private bool spacePress;

    internal event Action<bool> PreviewRequested;
    internal event Action PreviewReleased;

    internal DiskPreviewButton()
    {
        holdTimer.Tick += delegate
        {
            holdTimer.Stop();
            if (!pressing || !Enabled) return;
            holding = true;
            var requested = PreviewRequested;
            if (requested != null) requested(true);
        };
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) BeginPress(false);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        try { base.OnMouseUp(e); }
        finally { if (e.Button == MouseButtons.Left && !spacePress) EndPress(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (!spacePress) EndPress();
        base.OnMouseLeave(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyData == Keys.Space) BeginPress(true);
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        try { base.OnKeyUp(e); }
        finally { if (e.KeyCode == Keys.Space && spacePress) EndPress(); }
    }

    protected override void OnLostFocus(EventArgs e)
    {
        EndPress();
        base.OnLostFocus(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        if (!Enabled) EndPress();
        base.OnEnabledChanged(e);
    }

    protected override void OnClick(EventArgs e)
    {
        if (holding) return;
        base.OnClick(e);
        var requested = PreviewRequested;
        if (requested != null) requested(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && holdTimer != null)
        {
            EndPress();
            holdTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    private void BeginPress(bool keyboard)
    {
        if (pressing) return;
        pressing = true;
        spacePress = keyboard;
        holdTimer.Start();
    }

    private void EndPress()
    {
        if (!pressing) return;
        holdTimer.Stop();
        pressing = false;
        spacePress = false;
        if (!holding) return;
        holding = false;
        var released = PreviewReleased;
        if (released != null) released();
    }
}
