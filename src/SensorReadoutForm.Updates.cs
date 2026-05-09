using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;

public sealed partial class SensorReadoutForm : Form
{
    private void CheckForUpdates()
    {
        CheckForUpdates(true, true);
    }

    private void CheckForUpdates(bool showUpToDate, bool showErrors)
    {
        try
        {
            var release = FetchLatestRelease();
            var latest = (release == null ? "" : release.TagName) ?? "";
            var latestVersion = latest.Trim().TrimStart('v', 'V');
            if (string.IsNullOrWhiteSpace(latestVersion))
            {
                if (showErrors)
                {
                    MessageBox.Show(this, T("message.couldNotReadLatestVersion", "Could not read the latest release version."), UpdateCheckDialogTitle(), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return;
            }

            Version current;
            Version remote;
            if (Version.TryParse(AppVersion, out current) && Version.TryParse(latestVersion, out remote) && remote > current)
            {
                ShowUpdateAvailableDialog(release, latest);
                CheckPawnIoFromManualUpdateCheck(showUpToDate, showErrors);
                return;
            }

            if (showUpToDate)
            {
                MessageBox.Show(this, string.Format(T("message.upToDate", "Sensor Readout is up to date. Current version: {0}."), AppVersion), UpdateCheckDialogTitle(), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            CheckPawnIoFromManualUpdateCheck(showUpToDate, showErrors);
        }
        catch (WebException ex)
        {
            if (showErrors)
            {
                MessageBox.Show(this, T("message.couldNotCheckUpdates", "Could not check for updates. GitHub releases may not exist yet, or the network request failed.") + Environment.NewLine + Environment.NewLine + ex.Message, UpdateCheckDialogTitle(), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            if (showErrors)
            {
                MessageBox.Show(this, ex.Message, UpdateCheckDialogTitle(), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private void CheckPawnIoFromManualUpdateCheck(bool showUpToDate, bool showErrors)
    {
        if (!showUpToDate || !showErrors || IsPawnIoInstalled())
        {
            return;
        }

        var result = MessageBox.Show(
            this,
            T("message.pawnIoMissingAfterUpdateCheck", "PawnIO is not installed. Motherboard sensors and fan controls may be missing without it.") + Environment.NewLine + Environment.NewLine +
            T("message.installPawnIoNow", "Do you want to install PawnIO now using winget?"),
            UpdateCheckDialogTitle(),
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Information);

        if (result == DialogResult.Yes)
        {
            RunPrerequisiteInstaller();
        }
    }

    private void StartAutomaticUpdateChecks()
    {
        if (updateCheckTimer != null)
        {
            updateCheckTimer.Stop();
        }

        CheckAutomaticUpdateSchedule();
        if (updateCheckTimer != null && AutomaticUpdateInterval(settings.UpdateCheckFrequency).HasValue)
        {
            updateCheckTimer.Start();
        }
    }

    private void CheckAutomaticUpdateSchedule()
    {
        var frequency = NormalizeUpdateCheckFrequency(settings.UpdateCheckFrequency);
        if (frequency == "Never")
        {
            return;
        }

        if (frequency == "Startup")
        {
            if (!automaticUpdateCheckStartedThisRun)
            {
                automaticUpdateCheckStartedThisRun = true;
                BeginSilentAutomaticUpdateCheck(false);
            }
            return;
        }

        var interval = AutomaticUpdateInterval(frequency);
        if (!interval.HasValue)
        {
            return;
        }

        DateTime last;
        if (!DateTime.TryParse(settings.LastAutomaticUpdateCheckUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out last) ||
            DateTime.UtcNow - last >= interval.Value)
        {
            BeginSilentAutomaticUpdateCheck(true);
        }
    }

    private void BeginSilentAutomaticUpdateCheck(bool recordAttempt)
    {
        Task.Run(delegate
        {
            try
            {
                if (recordAttempt)
                {
                    RecordAutomaticUpdateCheckAttempt();
                }

                var release = FetchLatestRelease();
                var latest = (release == null ? "" : release.TagName) ?? "";
                var latestVersion = latest.Trim().TrimStart('v', 'V');
                Version current;
                Version remote;
                if (!Version.TryParse(AppVersion, out current) || !Version.TryParse(latestVersion, out remote) || remote <= current)
                {
                    return;
                }

                BeginInvoke((MethodInvoker)delegate
                {
                    if (!IsDisposed)
                    {
                        ShowUpdateAvailableDialog(release, latest);
                    }
                });
            }
            catch
            {
                // Startup checks are intentionally silent unless an update is available.
            }
        });
    }

    private void RecordAutomaticUpdateCheckAttempt()
    {
        try
        {
            BeginInvoke((MethodInvoker)delegate
            {
                if (IsDisposed)
                {
                    return;
                }

                settings.LastAutomaticUpdateCheckUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                SaveSettings(settings);
            });
        }
        catch
        {
        }
    }

    public static string NormalizeUpdateCheckFrequency(string value)
    {
        if (string.Equals(value, "Hour", StringComparison.OrdinalIgnoreCase)) return "Hourly";
        if (string.Equals(value, "Hourly", StringComparison.OrdinalIgnoreCase)) return "Hourly";
        if (string.Equals(value, "6Hours", StringComparison.OrdinalIgnoreCase)) return "6Hours";
        if (string.Equals(value, "12Hours", StringComparison.OrdinalIgnoreCase)) return "12Hours";
        if (string.Equals(value, "Daily", StringComparison.OrdinalIgnoreCase)) return "Daily";
        if (string.Equals(value, "Weekly", StringComparison.OrdinalIgnoreCase)) return "Weekly";
        if (string.Equals(value, "Never", StringComparison.OrdinalIgnoreCase)) return "Never";
        return "Startup";
    }

    public static TimeSpan? AutomaticUpdateInterval(string frequency)
    {
        switch (NormalizeUpdateCheckFrequency(frequency))
        {
            case "Hourly": return TimeSpan.FromHours(1);
            case "6Hours": return TimeSpan.FromHours(6);
            case "12Hours": return TimeSpan.FromHours(12);
            case "Daily": return TimeSpan.FromDays(1);
            case "Weekly": return TimeSpan.FromDays(7);
            default: return null;
        }
    }

    private static GitHubReleaseInfo FetchLatestRelease()
    {
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
        using (var client = new WebClient())
        {
            client.Headers.Add("User-Agent", "Sensor Readout " + AppVersion);
            var json = client.DownloadString(ProjectUrl.Replace("https://github.com/", "https://api.github.com/repos/") + "/releases/latest");
            return JsonConvert.DeserializeObject<GitHubReleaseInfo>(json);
        }
    }

    private static string FormatReleaseNotesForDialog(string markdown, string fallback)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return fallback;
        }

        var lines = markdown
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Split('\n');
        var result = new System.Text.StringBuilder();
        var previousWasBlank = true;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0)
            {
                if (!previousWasBlank)
                {
                    result.AppendLine();
                    previousWasBlank = true;
                }

                continue;
            }

            if (trimmed.StartsWith("### "))
            {
                line = trimmed.Substring(4).Trim();
            }
            else if (trimmed.StartsWith("## "))
            {
                line = trimmed.Substring(3).Trim();
            }
            else if (trimmed.StartsWith("# "))
            {
                line = trimmed.Substring(2).Trim();
            }
            else if (trimmed.StartsWith("- "))
            {
                line = "- " + trimmed.Substring(2).Trim();
            }

            result.AppendLine(line);
            previousWasBlank = false;
        }

        var text = result.ToString().TrimEnd();
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }

    private void ShowUpdateAvailableDialog(GitHubReleaseInfo release, string latest)
    {
        var releaseUrl = release == null || string.IsNullOrWhiteSpace(release.HtmlUrl) ? ProjectUrl + "/releases" : release.HtmlUrl;
        var zipAsset = FindPortableZipAsset(release);

        using (var dialog = new Form())
        {
            dialog.Text = T("ui.Update available", "Update available");
            dialog.StartPosition = FormStartPosition.CenterParent;
            dialog.Width = 720;
            dialog.Height = 520;
            dialog.MinimizeBox = false;
            dialog.MaximizeBox = false;
            dialog.ShowIcon = false;
            dialog.ShowInTaskbar = false;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(12)
            };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var header = new Label
            {
                AutoSize = true,
                Dock = DockStyle.Top,
                Text = string.Format(T("message.updateAvailableHeader", "Sensor Readout {0} is available."), latest),
                Padding = new Padding(0, 0, 0, 8)
            };

            var notes = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Text = FormatReleaseNotesForDialog(release == null ? "" : release.Body, T("message.noReleaseNotes", "No release notes were provided for this update.")),
                AccessibleName = T("a11y.Release notes", "Release notes")
            };

            var buttons = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(0, 8, 0, 0)
            };

            var laterButton = new Button { Text = T("ui.Later", "&Later"), DialogResult = DialogResult.Cancel, AutoSize = true };
            var releaseButton = new Button { Text = T("ui.Open release page", "Open &release page"), AutoSize = true };
            releaseButton.Click += delegate
            {
                Process.Start(new ProcessStartInfo { FileName = releaseUrl, UseShellExecute = true });
            };

            if (zipAsset != null)
            {
                var installButton = new Button { Text = T("ui.Download and install", "&Download and install"), AutoSize = true };
                installButton.Click += delegate
                {
                    dialog.DialogResult = DialogResult.OK;
                    dialog.Close();
                    StartSelfUpdate(zipAsset.BrowserDownloadUrl);
                };
                buttons.Controls.Add(installButton);
                dialog.AcceptButton = installButton;
            }
            buttons.Controls.Add(releaseButton);
            buttons.Controls.Add(laterButton);

            dialog.CancelButton = laterButton;
            layout.Controls.Add(header, 0, 0);
            layout.Controls.Add(notes, 0, 1);
            layout.Controls.Add(buttons, 0, 2);
            dialog.Controls.Add(layout);
            dialog.ShowDialog(this);
        }
    }

    private static GitHubReleaseAsset FindPortableZipAsset(GitHubReleaseInfo release)
    {
        if (release == null || release.Assets == null)
        {
            return null;
        }

        return release.Assets
            .Where(a => a != null && !string.IsNullOrWhiteSpace(a.BrowserDownloadUrl) && !string.IsNullOrWhiteSpace(a.Name))
            .Where(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.Name.IndexOf("portable", StringComparison.OrdinalIgnoreCase) >= 0)
            .ThenByDescending(a => a.Name.IndexOf("sensor", StringComparison.OrdinalIgnoreCase) >= 0)
            .FirstOrDefault();
    }

    private void StartSelfUpdate(string zipUrl)
    {
        if (string.IsNullOrWhiteSpace(zipUrl))
        {
            MessageBox.Show(this, T("message.noUpdatePackage", "This GitHub release does not include a downloadable ZIP package. Please open the release page instead."), UpdateCheckDialogTitle(), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var confirm = MessageBox.Show(
            this,
            T("message.updateRestartRequired", "Sensor Readout will close, download the update, replace the files in this folder, and restart. Your settings and logs will be kept."),
            StripMenuMnemonic(T("ui.Download and install", "Download and install")),
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Information);
        if (confirm != DialogResult.OK)
        {
            return;
        }

        try
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            var exePath = Application.ExecutablePath;
            var scriptPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SensorReadoutUpdater-" + Guid.NewGuid().ToString("N") + ".ps1");
            System.IO.File.WriteAllText(scriptPath, BuildUpdaterScript(zipUrl, appDir, exePath, Process.GetCurrentProcess().Id));
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, T("ui.Could not start updater", "Could not start updater"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private string UpdateCheckDialogTitle()
    {
        return StripMenuMnemonic(T("ui.Check for updates...", "Check for updates..."));
    }

    private static string BuildUpdaterScript(string zipUrl, string targetDir, string exePath, int processId)
    {
        return
            "$ErrorActionPreference = 'Stop'\r\n" +
            "Add-Type -AssemblyName System.Windows.Forms\r\n" +
            "$zipUrl = " + PowerShellQuote(zipUrl) + "\r\n" +
            "$target = " + PowerShellQuote(targetDir) + "\r\n" +
            "$exe = " + PowerShellQuote(exePath) + "\r\n" +
            "$pidToWait = " + processId.ToString(CultureInfo.InvariantCulture) + "\r\n" +
            "try {\r\n" +
            "  $root = Join-Path $env:TEMP ('SensorReadoutUpdate_' + [guid]::NewGuid().ToString('N'))\r\n" +
            "  $zip = Join-Path $root 'update.zip'\r\n" +
            "  $stage = Join-Path $root 'stage'\r\n" +
            "  New-Item -ItemType Directory -Force -Path $root, $stage | Out-Null\r\n" +
            "  Invoke-WebRequest -Uri $zipUrl -OutFile $zip -UseBasicParsing\r\n" +
            "  Expand-Archive -LiteralPath $zip -DestinationPath $stage -Force\r\n" +
            "  $source = $stage\r\n" +
            "  if (-not (Test-Path -LiteralPath (Join-Path $source 'Sensor Readout.exe'))) {\r\n" +
            "    $candidate = Get-ChildItem -LiteralPath $stage -Recurse -Filter 'Sensor Readout.exe' -File | Select-Object -First 1\r\n" +
            "    if ($candidate) { $source = $candidate.DirectoryName }\r\n" +
            "  }\r\n" +
            "  if (-not (Test-Path -LiteralPath (Join-Path $source 'Sensor Readout.exe'))) { throw 'The downloaded ZIP does not contain Sensor Readout.exe.' }\r\n" +
            "  Get-Process -Id $pidToWait -ErrorAction SilentlyContinue | Wait-Process\r\n" +
            "  $backup = Join-Path (Join-Path $target 'Config\\Update Backups') (Get-Date -Format 'yyyyMMdd-HHmmss')\r\n" +
            "  foreach ($name in @('Langs')) {\r\n" +
            "    $existing = Join-Path $target $name\r\n" +
            "    if (Test-Path -LiteralPath $existing) { New-Item -ItemType Directory -Force -Path $backup | Out-Null; Copy-Item -LiteralPath $existing -Destination (Join-Path $backup $name) -Recurse -Force }\r\n" +
            "  }\r\n" +
            "  foreach ($name in @('Docs','Langs')) {\r\n" +
            "    $lower = Join-Path $target $name.ToLowerInvariant()\r\n" +
            "    $proper = Join-Path $target $name\r\n" +
            "    $tmpCase = Join-Path $target ($name + '_case_tmp')\r\n" +
            "    if ((Test-Path -LiteralPath $lower) -and -not (Test-Path -LiteralPath $proper)) { Rename-Item -LiteralPath $lower -NewName $name -ErrorAction SilentlyContinue }\r\n" +
            "    elseif (Test-Path -LiteralPath $lower) { Rename-Item -LiteralPath $lower -NewName ($name + '_case_tmp') -ErrorAction SilentlyContinue; if (Test-Path -LiteralPath $tmpCase) { Rename-Item -LiteralPath $tmpCase -NewName $name -ErrorAction SilentlyContinue } }\r\n" +
            "  }\r\n" +
            "  Get-ChildItem -LiteralPath $source -Force | ForEach-Object {\r\n" +
            "    $destination = Join-Path $target $_.Name\r\n" +
            "    if ($_.PSIsContainer -and (Test-Path -LiteralPath $destination)) {\r\n" +
            "      Get-ChildItem -LiteralPath $_.FullName -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $destination $_.Name) -Recurse -Force }\r\n" +
            "    } else {\r\n" +
            "      Copy-Item -LiteralPath $_.FullName -Destination $destination -Recurse -Force\r\n" +
            "    }\r\n" +
            "  }\r\n" +
            "  foreach ($folder in Get-ChildItem -LiteralPath $target -Directory -Force) {\r\n" +
            "    $name = $folder.Name\r\n" +
            "    $proper = Join-Path $target $name\r\n" +
            "    $nested = Join-Path $proper $name\r\n" +
            "    if (Test-Path -LiteralPath $nested) {\r\n" +
            "      Get-ChildItem -LiteralPath $nested -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $proper $_.Name) -Recurse -Force }\r\n" +
            "      Remove-Item -LiteralPath $nested -Recurse -Force -ErrorAction SilentlyContinue\r\n" +
            "    }\r\n" +
            "  }\r\n" +
            "  Remove-Item -LiteralPath (Join-Path $target 'README.md') -Force -ErrorAction SilentlyContinue\r\n" +
            "  Remove-Item -LiteralPath (Join-Path $target 'nvdaControllerClient.dll') -Force -ErrorAction SilentlyContinue\r\n" +
            "  Remove-Item -LiteralPath (Join-Path $target 'nvdaControllerClient.LICENSE.txt') -Force -ErrorAction SilentlyContinue\r\n" +
            "  if (Test-Path -LiteralPath (Join-Path $target 'Docs')) { Get-ChildItem -LiteralPath (Join-Path $target 'Docs') -Filter '*.md' -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue }\r\n" +
            "  if (Test-Path -LiteralPath (Join-Path $target 'docs')) { Get-ChildItem -LiteralPath (Join-Path $target 'docs') -Filter '*.md' -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue }\r\n" +
            "  Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue\r\n" +
            "  Start-Process -FilePath $exe\r\n" +
            "} catch {\r\n" +
            "  [System.Windows.Forms.MessageBox]::Show('Sensor Readout update failed:' + [Environment]::NewLine + [Environment]::NewLine + $_.Exception.Message, 'Sensor Readout updater', 'OK', 'Error') | Out-Null\r\n" +
            "}\r\n" +
            "Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue\r\n";
    }

    private static string PowerShellQuote(string value)
    {
        return "'" + (value ?? "").Replace("'", "''") + "'";
    }
}
