using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

public sealed partial class SensorReadoutForm : Form
{
    private sealed class NetworkToolAddressResult
    {
        public IPAddress Address;
        public string Scope = "";
        public string ReverseDns = "";
        public readonly List<IPAddress> ReverseDnsAddresses = new List<IPAddress>();
    }

    private sealed class NetworkToolPingResult
    {
        public int Sent;
        public int Received;
        public long MinimumMilliseconds;
        public double AverageMilliseconds;
        public long MaximumMilliseconds;
        public string Error = "";
    }

    private sealed class NetworkToolPublicResult
    {
        public string Address = "";
        public InternetIpInfo Info;
    }

    private sealed class NetworkToolResult
    {
        public string NormalizedTarget = "";
        public string ResolutionError = "";
        public readonly List<NetworkToolAddressResult> Addresses = new List<NetworkToolAddressResult>();
        public NetworkToolPingResult Ping;
        public readonly List<NetworkToolPublicResult> PublicResults = new List<NetworkToolPublicResult>();
    }

    private void ShowNetworkToolsDialog()
    {
        using (var dialog = new Form())
        {
            dialog.Text = T("ui.Network tools", "Network tools");
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Size = new Size(820, 610);
            dialog.MinimumSize = new Size(620, 440);
            dialog.ShowInTaskbar = false;
            dialog.KeyPreview = true;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                Padding = new Padding(10)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var intro = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(760, 0),
                Text = T("ui.Network tools intro", "Look up an IP address or host name, resolve DNS, classify its addresses, and optionally test ping latency and packet loss. Public-address metadata uses an online lookup service; private and local addresses are never sent to it. Network Tools does not run a speed test or port scan and performs no background polling.")
            };

            var targetPanel = new TableLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Top,
                ColumnCount = 3,
                RowCount = 1,
                Margin = new Padding(0, 10, 0, 4)
            };
            targetPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            targetPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            targetPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var targetLabel = new Label
            {
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Text = T("ui.&Address or host name:", "&Address or host name:")
            };
            var targetBox = new TextBox
            {
                Dock = DockStyle.Fill,
                AccessibleName = T("a11y.Address or host name", "Address or host name")
            };
            targetBox.Enter += delegate { targetBox.SelectAll(); };
            targetLabel.Tag = targetBox;
            targetLabel.Click += delegate { targetBox.Focus(); };
            var runButton = new Button
            {
                AutoSize = true,
                Text = T("ui.&Run", "&Run")
            };
            targetPanel.Controls.Add(targetLabel, 0, 0);
            targetPanel.Controls.Add(targetBox, 1, 0);
            targetPanel.Controls.Add(runButton, 2, 0);

            var optionPanel = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0, 0, 0, 6)
            };
            var pingCheckBox = new CheckBox
            {
                AutoSize = true,
                Checked = true,
                Text = T("ui.Include &ping test", "Include &ping test"),
                AccessibleDescription = T("a11y.Sends four ICMP echo requests and reports latency and packet loss. Some networks block ping replies.", "Sends four ICMP echo requests and reports latency and packet loss. Some networks block ping replies.")
            };
            var operationStatus = new Label
            {
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(14, 4, 0, 0),
                Text = T("status.Enter an address or host name.", "Enter an address or host name.")
            };
            optionPanel.Controls.Add(pingCheckBox);
            optionPanel.Controls.Add(operationStatus);

            var resultsTree = new TreeView
            {
                Dock = DockStyle.Fill,
                HideSelection = false,
                ShowNodeToolTips = true,
                AccessibleName = T("a11y.Network tools results", "Network tools results"),
                AccessibleDescription = T("a11y.Network tools results description", "Results are grouped by target, resolved address, ping test, and public network information. Press F3 to find, F4 to review text, Control C to copy, Control Shift C to copy only values, Control M to copy matching lines, or Escape to close.")
            };

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 8, 0, 0)
            };
            var closeButton = CreateCloseButton();
            closeButton.Text = StripMenuMnemonic(closeButton.Text);
            var copyButton = CreateNetworkToolShortcutButton(T("ui.&Copy", "&Copy"), "Ctrl+C", Keys.Control | Keys.C);
            var copyValueButton = CreateNetworkToolShortcutButton(T("ui.Copy &value only", "Copy &value only"), "Ctrl+Shift+C", Keys.Control | Keys.Shift | Keys.C);
            var copyMatchingButton = CreateNetworkToolShortcutButton(T("ui.Copy &matching...", "Copy &matching..."), "Ctrl+M", Keys.Control | Keys.M);
            var collapseAllButton = CreateNetworkToolShortcutButton(T("ui.C&ollapse all", "C&ollapse all"), "Ctrl+Shift+Left", Keys.Control | Keys.Shift | Keys.Left);
            var expandAllButton = CreateNetworkToolShortcutButton(T("ui.&Expand all", "&Expand all"), "Ctrl+Shift+Right", Keys.Control | Keys.Shift | Keys.Right);
            var findButton = CreateNetworkToolShortcutButton(T("ui.&Find...", "&Find..."), "F3", Keys.F3);
            closeButton.Click += delegate { dialog.Close(); };
            copyButton.Click += delegate { CopyDetailsTree(resultsTree); };
            copyValueButton.Click += delegate { CopyDetailsTreeValueOnly(resultsTree); };
            copyMatchingButton.Click += delegate { CopyMatchingDetailsTreeLines(resultsTree); };
            collapseAllButton.Click += delegate { CollapseDetailsTree(resultsTree); };
            expandAllButton.Click += delegate { ExpandDetailsTree(resultsTree); };
            findButton.Click += delegate { ShowDetailsTreeSearchDialog(resultsTree); };
            buttons.Controls.Add(closeButton);
            buttons.Controls.Add(copyButton);
            buttons.Controls.Add(copyValueButton);
            buttons.Controls.Add(copyMatchingButton);
            buttons.Controls.Add(collapseAllButton);
            buttons.Controls.Add(expandAllButton);
            buttons.Controls.Add(findButton);

            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add(CreateShortcutMenuItem(T("ui.&Find...", "&Find..."), Keys.F3, delegate { ShowDetailsTreeSearchDialog(resultsTree); }));
            contextMenu.Items.Add(CreateShortcutMenuItem(T("ui.&Copy", "&Copy"), Keys.Control | Keys.C, delegate { CopyDetailsTree(resultsTree); }));
            contextMenu.Items.Add(CreateShortcutMenuItem(T("ui.Copy &value only", "Copy &value only"), Keys.Control | Keys.Shift | Keys.C, delegate { CopyDetailsTreeValueOnly(resultsTree); }));
            contextMenu.Items.Add(CreateShortcutMenuItem(T("ui.Copy &matching...", "Copy &matching..."), Keys.Control | Keys.M, delegate { CopyMatchingDetailsTreeLines(resultsTree); }));
            contextMenu.Items.Add(CreateShortcutMenuItem(T("ui.&Expand all", "&Expand all"), Keys.Control | Keys.Shift | Keys.Right, delegate { ExpandDetailsTree(resultsTree); }));
            contextMenu.Items.Add(CreateShortcutMenuItem(T("ui.C&ollapse all", "C&ollapse all"), Keys.Control | Keys.Shift | Keys.Left, delegate { CollapseDetailsTree(resultsTree); }));
            resultsTree.ContextMenuStrip = contextMenu;

            runButton.Click += delegate
            {
                string normalized;
                string validationError;
                if (!TryNormalizeNetworkToolTarget(targetBox.Text, out normalized, out validationError))
                {
                    operationStatus.Text = T("status.Enter a valid IP address or host name.", "Enter a valid IP address or host name.");
                    System.Media.SystemSounds.Beep.Play();
                    SpeakTextWithScreenReader(operationStatus.Text, "network tools");
                    targetBox.Focus();
                    targetBox.SelectAll();
                    return;
                }

                runButton.Enabled = false;
                targetBox.Enabled = false;
                pingCheckBox.Enabled = false;
                resultsTree.Nodes.Clear();
                operationStatus.Text = T("status.Running network checks...", "Running network checks...");
                SpeakTextWithScreenReader(operationStatus.Text, "network tools");
                var includePing = pingCheckBox.Checked;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    var result = RunNetworkTools(normalized, includePing);
                    if (dialog.IsDisposed || !dialog.IsHandleCreated)
                    {
                        return;
                    }

                    try
                    {
                        dialog.BeginInvoke((MethodInvoker)delegate
                        {
                            if (dialog.IsDisposed)
                            {
                                return;
                            }

                            PopulateNetworkToolsTree(resultsTree, result);
                            runButton.Enabled = true;
                            targetBox.Enabled = true;
                            pingCheckBox.Enabled = true;
                            operationStatus.Text = result.Addresses.Count > 0
                                ? T("status.Network checks complete.", "Network checks complete.")
                                : T("status.Network checks completed without a resolved address.", "Network checks completed without a resolved address.");
                            SpeakTextWithScreenReader(operationStatus.Text, "network tools");
                            if (resultsTree.Nodes.Count > 0)
                            {
                                resultsTree.SelectedNode = resultsTree.Nodes[0];
                                resultsTree.Nodes[0].Expand();
                                resultsTree.Focus();
                            }
                        });
                    }
                    catch (InvalidOperationException)
                    {
                    }
                });
            };

            resultsTree.KeyDown += delegate(object sender, KeyEventArgs e) { HandleDetailsTreeKey(resultsTree, e); };
            dialog.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    dialog.Close();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };

            layout.Controls.Add(intro, 0, 0);
            layout.Controls.Add(targetPanel, 0, 1);
            layout.Controls.Add(optionPanel, 0, 2);
            layout.Controls.Add(resultsTree, 0, 3);
            layout.Controls.Add(buttons, 0, 4);
            dialog.Controls.Add(layout);
            dialog.AcceptButton = runButton;
            dialog.CancelButton = closeButton;
            dialog.Shown += delegate { targetBox.Focus(); };
            dialog.ShowDialog(this);
        }
    }

    private NetworkToolResult RunNetworkTools(string normalizedTarget, bool includePing)
    {
        var result = new NetworkToolResult
        {
            NormalizedTarget = normalizedTarget
        };

        try
        {
            IPAddress literal;
            var addresses = new List<IPAddress>();
            var literalTarget = IPAddress.TryParse(normalizedTarget, out literal);
            if (literalTarget)
            {
                addresses.Add(literal);
            }
            else
            {
                var task = Dns.GetHostAddressesAsync(normalizedTarget);
                if (!task.Wait(TimeSpan.FromSeconds(5)))
                {
                    result.ResolutionError = T("value.DNS lookup timed out.", "DNS lookup timed out.");
                    return result;
                }

                addresses.AddRange(task.Result ?? new IPAddress[0]);
            }

            foreach (var address in addresses
                .Where(a => a != null)
                .Distinct()
                .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                .Take(8))
            {
                var addressResult = new NetworkToolAddressResult
                {
                    Address = address,
                    Scope = ClassifyNetworkToolAddress(address),
                    ReverseDns = ResolveNetworkToolReverseDns(address)
                };
                if (literalTarget && !string.IsNullOrWhiteSpace(addressResult.ReverseDns))
                {
                    addressResult.ReverseDnsAddresses.AddRange(ResolveNetworkToolHostAddresses(addressResult.ReverseDns));
                }
                result.Addresses.Add(addressResult);
            }
        }
        catch (Exception ex)
        {
            result.ResolutionError = ex.Message;
        }

        var preferredAddress = result.Addresses.Select(a => a.Address).FirstOrDefault();
        if (includePing && preferredAddress != null)
        {
            result.Ping = RunNetworkToolPing(preferredAddress);
        }

        var publicAddresses = result.Addresses
            .Where(a => IsPublicNetworkToolAddress(a.Address))
            .Select(a => a.Address)
            .Distinct()
            .ToList();
        if (publicAddresses.Count > 0)
        {
            var lookupTasks = publicAddresses.Select(address =>
            {
                var lookupAddress = address.ToString();
                return Task.Factory.StartNew(() => new NetworkToolPublicResult
                {
                    Address = lookupAddress,
                    Info = FetchInternetIpInfo(lookupAddress)
                });
            }).ToArray();

            try
            {
                Task.WaitAll(lookupTasks, TimeSpan.FromSeconds(10));
            }
            catch
            {
            }

            for (var index = 0; index < lookupTasks.Length; index++)
            {
                result.PublicResults.Add(lookupTasks[index].Status == TaskStatus.RanToCompletion
                    ? lookupTasks[index].Result
                    : new NetworkToolPublicResult
                    {
                        Address = publicAddresses[index].ToString(),
                        Info = new InternetIpInfo { Success = false, Error = T("value.Unavailable", "Unavailable") }
                    });
            }
        }

        return result;
    }

    private void PopulateNetworkToolsTree(TreeView tree, NetworkToolResult result)
    {
        tree.BeginUpdate();
        try
        {
            tree.Nodes.Clear();
            var targetNode = tree.Nodes.Add(T("ui.Target", "Target") + ": " + result.NormalizedTarget);
            AddNetworkToolTreeValue(targetNode, T("ui.Resolved address count", "Resolved address count"), result.Addresses.Count.ToString(CultureInfo.CurrentCulture));
            if (!string.IsNullOrWhiteSpace(result.ResolutionError))
            {
                AddNetworkToolTreeValue(targetNode, T("ui.DNS status", "DNS status"), result.ResolutionError);
            }

            for (var index = 0; index < result.Addresses.Count; index++)
            {
                var address = result.Addresses[index];
                var addressNode = tree.Nodes.Add(string.Format(T("ui.Resolved address {0}", "Resolved address {0}"), index + 1));
                AddNetworkToolTreeValue(addressNode, T("ui.IP address", "IP address"), address.Address.ToString());
                AddNetworkToolTreeValue(addressNode, T("ui.IP version", "IP version"), address.Address.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6");
                AddNetworkToolTreeValue(addressNode, T("ui.Address scope", "Address scope"), LocalizeNetworkToolScope(address.Scope));
                AddNetworkToolTreeValue(addressNode, T("ui.Reverse DNS", "Reverse DNS"), FirstNonEmpty(address.ReverseDns, T("value.Not available", "Not available")));
                if (address.ReverseDnsAddresses.Count > 0)
                {
                    var relatedNode = addressNode.Nodes.Add(T("ui.Addresses for reverse DNS name", "Addresses for reverse DNS name") + ": " + address.ReverseDns);
                    foreach (var relatedAddress in address.ReverseDnsAddresses)
                    {
                        var version = relatedAddress.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6";
                        AddNetworkToolTreeValue(relatedNode, version, relatedAddress.ToString());
                    }
                }
                var publicResult = result.PublicResults.FirstOrDefault(item => string.Equals(item.Address, address.Address.ToString(), StringComparison.OrdinalIgnoreCase));
                if (publicResult != null)
                {
                    AddNetworkToolPublicInformation(addressNode, publicResult);
                }
            }

            if (result.Ping != null)
            {
                var pingNode = tree.Nodes.Add(T("ui.Ping test", "Ping test"));
                AddNetworkToolTreeValue(pingNode, T("ui.Packets sent", "Packets sent"), result.Ping.Sent.ToString(CultureInfo.CurrentCulture));
                AddNetworkToolTreeValue(pingNode, T("ui.Packets received", "Packets received"), result.Ping.Received.ToString(CultureInfo.CurrentCulture));
                AddNetworkToolTreeValue(pingNode, T("ui.Packet loss", "Packet loss"), FormatNetworkToolPacketLoss(result.Ping));
                if (result.Ping.Received > 0)
                {
                    AddNetworkToolTreeValue(pingNode, T("ui.Minimum latency", "Minimum latency"), result.Ping.MinimumMilliseconds.ToString(CultureInfo.CurrentCulture) + " ms");
                    AddNetworkToolTreeValue(pingNode, T("ui.Average latency", "Average latency"), result.Ping.AverageMilliseconds.ToString("0.0", CultureInfo.CurrentCulture) + " ms");
                    AddNetworkToolTreeValue(pingNode, T("ui.Maximum latency", "Maximum latency"), result.Ping.MaximumMilliseconds.ToString(CultureInfo.CurrentCulture) + " ms");
                }
                if (!string.IsNullOrWhiteSpace(result.Ping.Error))
                {
                    AddNetworkToolTreeValue(pingNode, T("ui.Ping status", "Ping status"), result.Ping.Error);
                }
                if (ShouldShowNetworkToolPingBlockedNote(result.Ping))
                {
                    AddNetworkToolTreeValue(pingNode, T("ui.Note", "Note"), T("ui.Ping blocked note", "No ping reply does not prove that a host is offline; some systems and networks block ICMP echo replies."));
                }
            }

            if (result.PublicResults.Count == 0)
            {
                var publicNode = tree.Nodes.Add(T("ui.Public network information", "Public network information"));
                AddNetworkToolTreeValue(publicNode, T("ui.Status", "Status"), T("ui.Private lookup note", "Online lookup was skipped because all resolved addresses are private, local, or special-use. Sensor Readout does not send those addresses to the lookup service."));
            }
        }
        finally
        {
            tree.EndUpdate();
        }
    }

    private void AddNetworkToolPublicInformation(TreeNode addressNode, NetworkToolPublicResult publicResult)
    {
        if (addressNode == null || publicResult == null)
        {
            return;
        }

        var publicNode = addressNode.Nodes.Add(T("ui.Public network information", "Public network information"));
        var info = publicResult.Info;
        if (info == null || !info.Success)
        {
            AddNetworkToolTreeValue(publicNode, T("ui.Status", "Status"), FirstNonEmpty(info == null ? "" : info.Error, T("value.Unavailable", "Unavailable")));
            return;
        }

        AddNetworkToolTreeValue(publicNode, T("ui.Country", "Country"), info.Country);
        AddNetworkToolTreeValue(publicNode, T("ui.Region", "Region"), info.Region);
        AddNetworkToolTreeValue(publicNode, T("ui.City", "City"), info.City);
        AddNetworkToolTreeValue(publicNode, T("ui.Postal code", "Postal code"), info.PostalCode);
        AddNetworkToolTreeValue(publicNode, T("ui.Coordinates", "Coordinates"), info.Coordinates);
        AddNetworkToolTreeValue(publicNode, T("ui.Time zone", "Time zone"), info.TimeZone);
        AddNetworkToolTreeValue(publicNode, T("ui.Internet provider", "Internet provider"), info.Isp);
        AddNetworkToolTreeValue(publicNode, T("ui.Organization", "Organization"), info.Organization);
        AddNetworkToolTreeValue(publicNode, T("ui.Autonomous system", "Autonomous system"), info.AutonomousSystem);
        AddNetworkToolTreeValue(publicNode, T("ui.Connection type", "Connection type"), LocalizeNetworkToolConnectionType(info.ConnectionType));
        AddNetworkToolTreeValue(publicNode, T("ui.Note", "Note"), T("ui.Public metadata approximate note", "Public-IP metadata and location are estimates from an online lookup service and may be approximate."));
    }

    private static void AddNetworkToolTreeValue(TreeNode parent, string label, string value)
    {
        if (parent == null || string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        parent.Nodes.Add(label.TrimEnd(':') + ": " + value.Trim());
    }

    private static ShortcutButton CreateNetworkToolShortcutButton(string text, string shortcutText, Keys shortcutKeys)
    {
        return new ShortcutButton
        {
            Text = StripMenuMnemonic(text),
            AutoSize = true,
            ShortcutText = shortcutText,
            ShortcutKeys = shortcutKeys
        };
    }

    private static bool TryNormalizeNetworkToolTarget(string input, out string normalized, out string error)
    {
        normalized = "";
        error = "";
        var value = (input ?? "").Trim();
        if (value.Length == 0 || value.Length > 2048)
        {
            error = "An address or host name is required.";
            return false;
        }

        IPAddress address;
        if (IPAddress.TryParse(value.Trim('[', ']'), out address))
        {
            normalized = address.ToString();
            return true;
        }

        Uri uri;
        if (value.IndexOf("://", StringComparison.Ordinal) >= 0)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || string.IsNullOrWhiteSpace(uri.Host))
            {
                error = "The address or host name is not valid.";
                return false;
            }
            value = uri.Host;
        }
        else if (value.IndexOf('/') >= 0 || value.IndexOf(':') >= 0)
        {
            if (Uri.TryCreate("http://" + value, UriKind.Absolute, out uri) && !string.IsNullOrWhiteSpace(uri.Host))
            {
                value = uri.Host;
            }
        }

        value = value.Trim().TrimEnd('.');
        if (value.Length == 0 || value.Length > 253 || value.Any(char.IsWhiteSpace) || Uri.CheckHostName(value) == UriHostNameType.Unknown)
        {
            error = "The address or host name is not valid.";
            return false;
        }

        normalized = value;
        return true;
    }

    private static string ResolveNetworkToolReverseDns(IPAddress address)
    {
        if (address == null)
        {
            return "";
        }

        try
        {
            var task = Dns.GetHostEntryAsync(address);
            return task.Wait(TimeSpan.FromSeconds(2)) && task.Result != null ? task.Result.HostName : "";
        }
        catch
        {
            return "";
        }
    }

    private static List<IPAddress> ResolveNetworkToolHostAddresses(string hostName)
    {
        if (string.IsNullOrWhiteSpace(hostName))
        {
            return new List<IPAddress>();
        }

        try
        {
            var task = Dns.GetHostAddressesAsync(hostName.Trim().TrimEnd('.'));
            if (!task.Wait(TimeSpan.FromSeconds(5)))
            {
                return new List<IPAddress>();
            }

            return (task.Result ?? new IPAddress[0])
                .Where(address => address != null)
                .Distinct()
                .OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                .Take(8)
                .ToList();
        }
        catch
        {
            return new List<IPAddress>();
        }
    }

    private NetworkToolPingResult RunNetworkToolPing(IPAddress address)
    {
        var result = new NetworkToolPingResult();
        var successfulTimes = new List<long>();
        try
        {
            using (var ping = new Ping())
            {
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    result.Sent++;
                    try
                    {
                        var reply = ping.Send(address, 1500);
                        if (reply != null && reply.Status == IPStatus.Success)
                        {
                            result.Received++;
                            successfulTimes.Add(reply.RoundtripTime);
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Error = ex.Message;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        if (successfulTimes.Count > 0)
        {
            result.MinimumMilliseconds = successfulTimes.Min();
            result.MaximumMilliseconds = successfulTimes.Max();
            result.AverageMilliseconds = successfulTimes.Average();
        }
        else if (string.IsNullOrWhiteSpace(result.Error))
        {
            result.Error = T("value.No ping replies were received.", "No ping replies were received.");
        }

        return result;
    }

    private static string FormatNetworkToolPacketLoss(NetworkToolPingResult result)
    {
        if (result == null || result.Sent <= 0)
        {
            return "";
        }

        var lost = Math.Max(0, result.Sent - result.Received);
        var percent = lost * 100.0 / result.Sent;
        return lost.ToString(CultureInfo.CurrentCulture) + " (" + percent.ToString("0.#", CultureInfo.CurrentCulture) + "%)";
    }

    private static bool ShouldShowNetworkToolPingBlockedNote(NetworkToolPingResult result)
    {
        return result != null && result.Sent > 0 && result.Received == 0;
    }

    private static string ClassifyNetworkToolAddress(IPAddress address)
    {
        if (address == null)
        {
            return "Special";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return ClassifyNetworkToolAddress(address.MapToIPv4());
        }

        if (IPAddress.IsLoopback(address))
        {
            return "Loopback";
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            if (bytes[0] == 10 ||
                (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168))
            {
                return "Private";
            }
            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return "LinkLocal";
            }
            if (bytes[0] >= 224 && bytes[0] <= 239)
            {
                return "Multicast";
            }
            if (bytes[0] == 0 || bytes[0] >= 240 ||
                (bytes[0] == 64 && bytes[1] == 0 && bytes[2] == 0) ||
                (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) ||
                (bytes[0] == 192 && bytes[1] == 0 && (bytes[2] == 0 || bytes[2] == 2)) ||
                (bytes[0] == 192 && bytes[1] == 31 && bytes[2] == 196) ||
                (bytes[0] == 192 && bytes[1] == 52 && bytes[2] == 193) ||
                (bytes[0] == 192 && bytes[1] == 88 && bytes[2] == 99) ||
                (bytes[0] == 192 && bytes[1] == 175 && bytes[2] == 48) ||
                (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19 || bytes[1] == 51)) ||
                (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113))
            {
                return "Special";
            }
            return "Public";
        }

        if (address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Any))
        {
            return "Special";
        }
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
        {
            return "LinkLocal";
        }
        if (address.IsIPv6Multicast)
        {
            return "Multicast";
        }
        if ((bytes[0] & 0xFE) == 0xFC)
        {
            return "UniqueLocal";
        }
        if (bytes.Length >= 4 && bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8)
        {
            return "Special";
        }

        // Fail closed: only the currently allocated global-unicast block is
        // eligible for the online metadata lookup.
        return (bytes[0] & 0xE0) == 0x20 ? "Public" : "Special";
    }

    private static bool IsPublicNetworkToolAddress(IPAddress address)
    {
        return string.Equals(ClassifyNetworkToolAddress(address), "Public", StringComparison.Ordinal);
    }

    private string LocalizeNetworkToolScope(string scope)
    {
        switch (scope ?? "")
        {
            case "Public": return T("value.Public internet", "Public internet");
            case "Private": return T("value.Private network", "Private network");
            case "Loopback": return T("value.Loopback", "Loopback");
            case "LinkLocal": return T("value.Link-local", "Link-local");
            case "UniqueLocal": return T("value.Unique-local IPv6", "Unique-local IPv6");
            case "Multicast": return T("value.Multicast", "Multicast");
            default: return T("value.Reserved or special-use", "Reserved or special-use");
        }
    }

    private string LocalizeNetworkToolConnectionType(string connectionType)
    {
        if (string.IsNullOrWhiteSpace(connectionType))
        {
            return "";
        }

        return connectionType
            .Replace("ISP or residential connection", T("value.ISP or residential connection", "ISP or residential connection"))
            .Replace("Proxy or VPN", T("value.Proxy or VPN", "Proxy or VPN"))
            .Replace("Hosting or datacenter", T("value.Hosting or datacenter", "Hosting or datacenter"))
            .Replace("Mobile", T("value.Mobile", "Mobile"));
    }
}
