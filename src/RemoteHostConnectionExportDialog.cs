using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows.Forms;

internal sealed class RemoteHostConnectionExportDialog : Form
{
    private readonly Func<string, string, string> translate;
    private readonly int listeningPort;
    private readonly ComboBox addressBox;

    private RemoteHostConnectionExportDialog(int port, string previousUrl, Func<string, string, string> translate)
    {
        this.translate = translate;
        listeningPort = port;
        Text = T("ui.Save server connection", "Save server connection");
        Font = SystemFonts.MessageBoxFont;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(680, 280);
        MinimumSize = new Size(560, 250);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            ColumnCount = 2,
            RowCount = 4
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var intro = new Label
        {
            Text = T("ui.remoteExportAddressExplanation", "Choose the full address other computers will use to reach this server. Select a detected local or VPN address, or type a public IP address, DNS name, private-network name, reverse-proxy address, and port. Plain HTTP should be used only on a trusted local network or protected private network; use HTTPS for public Internet access."),
            AutoSize = true,
            Dock = DockStyle.Fill
        };
        layout.SetColumnSpan(intro, 2);
        layout.Controls.Add(intro, 0, 0);

        addressBox = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDown,
            MaxLength = 2048,
            AccessibleName = T("a11y.Address other computers use", "Address other computers use")
        };
        foreach (var candidate in CandidateUrls(port, previousUrl))
        {
            addressBox.Items.Add(candidate);
        }
        addressBox.Text = addressBox.Items.Count > 0 ? Convert.ToString(addressBox.Items[0]) : "";
        layout.Controls.Add(new Label
        {
            Text = T("ui.Address other computers &use", "Address other computers &use"),
            AutoSize = true,
            Anchor = AnchorStyles.Left
        }, 0, 1);
        layout.Controls.Add(addressBox, 1, 1);

        var note = new Label
        {
            Text = T("ui.remoteExportAddressNote", "The listening port and the address used by other computers can differ when a VPN, router port-forward, or reverse proxy is involved. Sensor Readout does not configure router forwarding or public HTTPS automatically."),
            AutoSize = true,
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 8, 0, 8)
        };
        layout.SetColumnSpan(note, 2);
        layout.Controls.Add(note, 0, 2);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var saveButton = new Button { Text = T("ui.&Save...", "&Save..."), AutoSize = true };
        var cancelButton = new Button { Text = T("ui.Cancel", "Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
        saveButton.Click += delegate { ValidateAndAccept(); };
        buttons.Controls.Add(saveButton);
        buttons.Controls.Add(cancelButton);
        layout.SetColumnSpan(buttons, 2);
        layout.Controls.Add(buttons, 0, 3);

        Controls.Add(layout);
        AcceptButton = saveButton;
        CancelButton = cancelButton;
    }

    public string SelectedUrl { get; private set; }

    public static bool TryChoose(IWin32Window owner, int port, string previousUrl, Func<string, string, string> translate, out string selectedUrl)
    {
        using (var dialog = new RemoteHostConnectionExportDialog(port, previousUrl, translate))
        {
            if (dialog.ShowDialog(owner) != DialogResult.OK)
            {
                selectedUrl = "";
                return false;
            }
            selectedUrl = dialog.SelectedUrl;
            return true;
        }
    }

    internal static IList<string> CandidateUrls(int port, string previousUrl)
    {
        var values = new List<string>();
        AddCandidate(values, previousUrl);
        AddCandidate(values, "http://" + Environment.MachineName + ":" + port + "/");
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces()
                .Where(item => item != null && item.OperationalStatus == OperationalStatus.Up && item.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast == null ? null : unicast.Address;
                    if (!UsefulClientAddress(address)) continue;
                    var host = address.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + address + "]" : address.ToString();
                    AddCandidate(values, "http://" + host + ":" + port + "/");
                }
            }
        }
        catch
        {
        }
        return values;
    }

    internal static bool TryNormalizeExportUrl(string input, int defaultPort, out string normalized, out string error)
    {
        normalized = "";
        error = "";
        var candidate = (input ?? "").Trim();
        if (candidate.Length == 0)
        {
            error = "message.remoteExportAddressRequired";
            return false;
        }
        if (candidate.IndexOf("://", StringComparison.Ordinal) < 0)
        {
            candidate = "http://" + candidate;
            Uri hostOnly;
            if (Uri.TryCreate(candidate, UriKind.Absolute, out hostOnly) && hostOnly.IsDefaultPort)
            {
                candidate = hostOnly.Scheme + "://" + hostOnly.Host + ":" + defaultPort + "/";
            }
        }

        Uri uri;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            error = "message.remoteExportAddressFormat";
            return false;
        }
        if (!string.IsNullOrWhiteSpace(uri.UserInfo) || !string.IsNullOrWhiteSpace(uri.Query) || !string.IsNullOrWhiteSpace(uri.Fragment))
        {
            error = "message.remoteExportAddressCredentials";
            return false;
        }
        var host = uri.Host.Trim('[', ']');
        if (uri.IsLoopback || string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase) || string.Equals(host, "::", StringComparison.OrdinalIgnoreCase))
        {
            error = "message.remoteExportAddressUnreachable";
            return false;
        }
        if (uri.Port < 1 || uri.Port > 65535)
        {
            error = "message.remoteExportAddressPort";
            return false;
        }
        normalized = uri.AbsoluteUri.TrimEnd('/') + "/";
        return true;
    }

    private void ValidateAndAccept()
    {
        string normalized;
        string errorKey;
        if (!TryNormalizeExportUrl(addressBox.Text, listeningPort, out normalized, out errorKey))
        {
            MessageBox.Show(this, T(errorKey, ExportErrorFallback(errorKey)), T("ui.Save server connection", "Save server connection"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            addressBox.Focus();
            addressBox.SelectAll();
            return;
        }
        SelectedUrl = normalized;
        DialogResult = DialogResult.OK;
        Close();
    }

    private static string ExportErrorFallback(string key)
    {
        switch (key)
        {
            case "message.remoteExportAddressRequired": return "Enter the address other computers will use.";
            case "message.remoteExportAddressFormat": return "Enter a complete HTTP or HTTPS server address.";
            case "message.remoteExportAddressCredentials": return "The server address cannot contain a username, password, query, or fragment.";
            case "message.remoteExportAddressUnreachable": return "Loopback and wildcard addresses cannot be used by another computer. Choose a local-network, private-network, VPN, DNS, or public address instead.";
            case "message.remoteExportAddressPort": return "The server port must be between 1 and 65535.";
            default: return "Enter a valid server address.";
        }
    }

    private static void AddCandidate(ICollection<string> values, string candidate)
    {
        string normalized;
        string error;
        if (!TryNormalizeExportUrl(candidate, 48673, out normalized, out error)) return;
        if (!values.Contains(normalized, StringComparer.OrdinalIgnoreCase)) values.Add(normalized);
    }

    private static bool UsefulClientAddress(IPAddress address)
    {
        if (address == null || IPAddress.IsLoopback(address)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return !(bytes[0] == 169 && bytes[1] == 254) && !address.Equals(IPAddress.Any);
        }
        return address.AddressFamily == AddressFamily.InterNetworkV6 &&
            !address.IsIPv6LinkLocal && !address.IsIPv6Multicast && !address.IsIPv6SiteLocal && !address.Equals(IPAddress.IPv6Any);
    }

    private string T(string key, string fallback)
    {
        return translate == null ? fallback : translate(key, fallback);
    }
}
