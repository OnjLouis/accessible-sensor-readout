using System;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

internal sealed class ContextHelp : IMessageFilter, IDisposable
{
    private const int KeyDownMessage = 0x0100;
    private const int KeyUpMessage = 0x0101;
    private static readonly ConditionalWeakTable<Form, ContextHelp> Owners = new ConditionalWeakTable<Form, ContextHelp>();
    private readonly Form main;
    private readonly Action openManual;
    private bool disposed;
    private bool showing;
    private bool consumeKeyUp;

    internal ContextHelp(Form main, Action openManual)
    {
        this.main = main;
        this.openManual = openManual;
        Owners.Add(main, this);
        Application.AddMessageFilter(this);
        main.Disposed += MainDisposed;
    }

    internal static bool IsHelpKey(Keys keys)
    {
        return keys == Keys.F1;
    }

    internal static bool ShowFor(Form owner)
    {
        for (var form = owner; form != null; form = form.Owner)
        {
            ContextHelp help;
            if (!Owners.TryGetValue(form, out help) || help.disposed) continue;
            help.Show(owner);
            return true;
        }
        return false;
    }

    public bool PreFilterMessage(ref Message message)
    {
        return FilterKeyMessage(ref message, null);
    }

    private bool FilterKeyMessage(ref Message message, Keys? modifiers)
    {
        if (disposed || (message.Msg != KeyDownMessage && message.Msg != KeyUpMessage) || (Keys)message.WParam.ToInt32() != Keys.F1) return false;
        var control = Control.FromChildHandle(message.HWnd);
        var owner = control == null ? null : control.FindForm();
        if (!Owns(owner)) return false;
        if (message.Msg == KeyUpMessage && consumeKeyUp)
        {
            consumeKeyUp = false;
            return true;
        }
        if (message.Msg != KeyDownMessage || !IsHelpKey(Keys.F1 | (modifiers ?? Control.ModifierKeys))) return false;
        consumeKeyUp = true;
        // Auto-repeat must not reopen help while F1 is still held.
        if ((message.LParam.ToInt64() & (1L << 30)) == 0) Show(owner);
        return true;
    }

    private bool Owns(Form owner)
    {
        for (var form = owner; form != null; form = form.Owner)
            if (form == main) return true;
        return false;
    }

    internal static Control FocusedControl(Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (!child.ContainsFocus) continue;
            return FocusedControl(child) ?? child;
        }
        return root.Focused ? root : null;
    }

    private void Show(Form owner)
    {
        if (showing || owner == null || owner.IsDisposed) return;
        var focused = FocusedControl(owner);
        showing = true;
        try
        {
            using (var dialog = CreateDialog(focused, openManual))
            {
                dialog.Icon = main.Icon;
                dialog.Font = owner.Font;
                dialog.ShowDialog(owner);
            }
        }
        finally
        {
            showing = false;
            if (focused != null && !focused.IsDisposed && focused.CanFocus) focused.Focus();
        }
    }

    internal static Form CreateDialog(Control control, Action manual)
    {
        string name;
        var explanation = ControlHelpCatalog.Describe(control, out name);
        var dialog = new Form
        {
            Text = SensorReadoutForm.L("help.Title", "Help: {0}").Replace("{0}", name),
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(620, 330),
            MinimumSize = new Size(420, 250),
            ShowInTaskbar = false,
            MinimizeBox = false,
            MaximizeBox = false
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var text = new TextBox
        {
            Text = explanation,
            AccessibleName = SensorReadoutForm.L("help.Explanation", "Control help"),
            ReadOnly = true,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            TabStop = true
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        var copy = new Button { Text = SensorReadoutForm.L("help.Copy", "&Copy help"), AutoSize = true };
        var fullManual = new Button { Text = SensorReadoutForm.L("help.Manual", "&Manual"), AutoSize = true };
        var close = new Button { Text = SensorReadoutForm.L("help.Close", "C&lose"), AutoSize = true };
        copy.Click += delegate
        {
            try { if (text.Text.Length > 0) Clipboard.SetText(text.Text); }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                MessageBox.Show(dialog, SensorReadoutForm.L("status.Could not copy to clipboard.", "Could not copy {0} to clipboard.")
                    .Replace("{0}", SensorReadoutForm.L("help.Explanation", "Control help")), "Sensor Readout", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        fullManual.Click += delegate { if (manual != null) manual(); };
        close.Click += delegate { dialog.Close(); };
        buttons.Controls.Add(copy);
        buttons.Controls.Add(fullManual);
        buttons.Controls.Add(close);
        layout.Controls.Add(text, 0, 0);
        layout.Controls.Add(buttons, 0, 1);
        dialog.Controls.Add(layout);
        dialog.CancelButton = close;
        dialog.Shown += delegate { text.Focus(); text.Select(0, 0); };
        return dialog;
    }

    private void MainDisposed(object sender, EventArgs e) { Dispose(); }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Application.RemoveMessageFilter(this);
        Owners.Remove(main);
        main.Disposed -= MainDisposed;
    }
}
