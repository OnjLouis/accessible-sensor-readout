using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using SensorReadout.PluginSdk;

public sealed partial class SensorReadoutForm : Form
{
    private PlugInManager plugInManager;

    private IEnumerable<SensorRow> GetPlugInRows()
    {
        return GetPlugInRows(false);
    }

    private IEnumerable<SensorRow> GetPlugInRows(bool diagnosticsMode)
    {
        EnsurePlugInManager();
        return plugInManager.GetRows(diagnosticsMode);
    }

    private bool TryPlugInFanControl(string identifier, int percent, bool manual)
    {
        EnsurePlugInManager();
        return plugInManager.TrySetFanControl(identifier, percent, manual);
    }

    public static List<PlugInPreferenceInfo> LoadPlugInPreferenceInfos(AppSettings settings)
    {
        return PlugInManager.LoadPreferenceInfos(settings, GetPlugInsFolderPath());
    }

    private static List<PlugInHelpLink> LoadEnabledPlugInHelpLinks(AppSettings settings)
    {
        return PlugInManager.LoadEnabledHelpLinks(settings, GetPlugInsFolderPath());
    }

    private List<PlugInHelpLink> LoadEnabledPlugInHelpLinks()
    {
        return LoadEnabledPlugInHelpLinks(settings);
    }

    private void EnsurePlugInManager()
    {
        if (plugInManager != null)
        {
            return;
        }

        plugInManager = new PlugInManager(settings, GetPlugInsFolderPath(), LogMessage);
    }

    public static string GetPlugInsFolderPath()
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plug-Ins");
    }

    private sealed class PlugInManager
    {
        private readonly AppSettings settings;
        private readonly string folder;
        private readonly Action<string, string> log;
        private readonly List<LoadedPlugIn> loaded = new List<LoadedPlugIn>();
        private readonly MachineInfo machine;
        private bool loadedOnce;

        public PlugInManager(AppSettings settings, string folder, Action<string, string> log)
        {
            this.settings = settings ?? new AppSettings();
            this.folder = folder ?? "";
            this.log = log ?? delegate { };
            machine = ReadMachineInfo(log);
        }

        public IEnumerable<SensorRow> GetRows()
        {
            return GetRows(false);
        }

        public IEnumerable<SensorRow> GetRows(bool diagnosticsMode)
        {
            EnsureLoaded();
            var rows = new List<SensorRow>();
            foreach (var plugIn in loaded.Where(p => p.Enabled && p.Instance != null))
            {
                try
                {
                    var context = new PlugInContext(machine, plugIn.Directory, diagnosticsMode, log);
                    var before = rows.Count;
                    rows.AddRange((plugIn.Instance.GetReadings(context) ?? Enumerable.Empty<SensorReading>())
                        .Where(r => r != null)
                        .Select(r => ToSensorRow(r, plugIn)));
                    log("Debug", "Plug-In " + plugIn.Id + " returned " + (rows.Count - before).ToString(CultureInfo.InvariantCulture) + " rows.");
                }
                catch (Exception ex)
                {
                    plugIn.Status = "Failed: " + ex.Message;
                    log("Error", "Plug-In " + plugIn.Id + " failed while collecting rows: " + ex.Message);
                }
            }

            foreach (var plugIn in loaded.Where(p => p.Enabled && p.Instance == null))
            {
                log("Debug", "Plug-In " + plugIn.Id + " is enabled but has no loaded instance. Status: " + plugIn.Status + ".");
            }

            return rows;
        }

        public bool TrySetFanControl(string identifier, int percent, bool manual)
        {
            EnsureLoaded();
            foreach (var plugIn in loaded.Where(p => p.Enabled && p.Instance is IFanControllablePlugin))
            {
                var controllable = (IFanControllablePlugin)plugIn.Instance;
                try
                {
                    var success = manual
                        ? controllable.TrySetFanPercent(identifier, percent)
                        : controllable.TryResetFan(identifier);
                    log("Debug", "Plug-In " + plugIn.Id + " fan control " + (manual ? "manual" : "automatic") + " for " + identifier + " returned " + success + ".");
                    if (success)
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    log("Error", "Plug-In " + plugIn.Id + " fan control failed: " + ex.Message);
                }
            }

            return false;
        }

        public static List<PlugInPreferenceInfo> LoadPreferenceInfos(AppSettings settings, string folder)
        {
            settings = settings ?? new AppSettings();
            var infos = new List<PlugInPreferenceInfo>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var manifest in FindManifestPaths(folder))
            {
                var descriptor = ReadDescriptor(manifest, null);
                if (descriptor == null || string.IsNullOrWhiteSpace(descriptor.Id))
                {
                    continue;
                }
                if (!seenIds.Add(descriptor.Id))
                {
                    continue;
                }

                infos.Add(new PlugInPreferenceInfo
                {
                    Id = descriptor.Id,
                    Name = descriptor.Name,
                    Version = descriptor.Version,
                    Author = descriptor.Author,
                    Description = descriptor.Description,
                    Enabled = IsEnabled(settings, descriptor),
                    Status = "Available"
                });
            }

            return infos
                .OrderBy(p => p.Name)
                .ThenBy(p => p.Id)
                .ToList();
        }

        public static List<PlugInHelpLink> LoadEnabledHelpLinks(AppSettings settings, string folder)
        {
            settings = settings ?? new AppSettings();
            var links = new List<PlugInHelpLink>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var manifest in FindManifestPaths(folder))
            {
                var descriptor = ReadDescriptor(manifest, null);
                if (descriptor == null || string.IsNullOrWhiteSpace(descriptor.Id) || !IsEnabled(settings, descriptor))
                {
                    continue;
                }
                if (!seenIds.Add(descriptor.Id))
                {
                    continue;
                }

                links.AddRange(descriptor.HelpLinks ?? new List<PlugInHelpLink>());
            }

            return links
                .OrderBy(l => l.PlugInName)
                .ThenBy(l => l.Label)
                .ToList();
        }

        private void EnsureLoaded()
        {
            if (loadedOnce)
            {
                return;
            }

            loadedOnce = true;
            var manifestCount = 0;
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var manifest in FindManifestPaths(folder))
            {
                manifestCount++;
                var descriptor = ReadDescriptor(manifest, log);
                if (descriptor == null || string.IsNullOrWhiteSpace(descriptor.Id))
                {
                    continue;
                }
                if (!seenIds.Add(descriptor.Id))
                {
                    log("Warning", "Ignoring duplicate Plug-In manifest for " + descriptor.Id + ": " + manifest);
                    continue;
                }

                var plugIn = new LoadedPlugIn
                {
                    Id = descriptor.Id,
                    Directory = Path.GetDirectoryName(manifest),
                    Enabled = IsEnabled(settings, descriptor),
                    Descriptor = descriptor,
                    Status = "Disabled"
                };
                loaded.Add(plugIn);
                if (!plugIn.Enabled)
                {
                    log("Debug", "Plug-In " + descriptor.Id + ": disabled.");
                    continue;
                }

                try
                {
                    var assemblyPath = Path.Combine(plugIn.Directory, descriptor.Assembly);
                    if (!File.Exists(assemblyPath))
                    {
                        plugIn.Status = "Missing assembly";
                        log("Error", "Plug-In " + descriptor.Id + " assembly missing: " + assemblyPath);
                        continue;
                    }

                    var assembly = Assembly.LoadFrom(assemblyPath);
                    var type = assembly.GetType(descriptor.Type, false);
                    if (type == null)
                    {
                        plugIn.Status = "Missing type";
                        log("Error", "Plug-In " + descriptor.Id + " type missing: " + descriptor.Type);
                        continue;
                    }

                    plugIn.Instance = Activator.CreateInstance(type) as ISensorReadoutPlugin;
                    plugIn.Status = plugIn.Instance == null ? "Type does not implement ISensorReadoutPlugin" : "Loaded";
                    log("Debug", "Plug-In " + descriptor.Id + ": " + plugIn.Status + ".");
                }
                catch (Exception ex)
                {
                    plugIn.Status = "Failed: " + ex.Message;
                    log("Error", "Plug-In " + descriptor.Id + " failed to load: " + ex.Message);
                }
            }

            if (manifestCount == 0)
            {
                log("Debug", "No Plug-In manifests found in " + folder + ".");
            }
        }

        private static IEnumerable<string> FindManifestPaths(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                return Enumerable.Empty<string>();
            }

            var manifests = new List<string>();
            var rootManifest = Path.Combine(folder, "plugin.json");
            if (File.Exists(rootManifest))
            {
                manifests.Add(rootManifest);
            }

            foreach (var child in Directory.GetDirectories(folder, "*", SearchOption.TopDirectoryOnly))
            {
                var manifest = Path.Combine(child, "plugin.json");
                if (File.Exists(manifest))
                {
                    manifests.Add(manifest);
                }
            }

            return manifests.OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        }

        private static PlugInDescriptor ReadDescriptor(string manifest, Action<string, string> log)
        {
            try
            {
                var obj = JObject.Parse(File.ReadAllText(manifest));
                var descriptor = new PlugInDescriptor
                {
                    Id = SafeManifestValue(obj, "id"),
                    Name = SafeManifestValue(obj, "name"),
                    Version = SafeManifestValue(obj, "version"),
                    Author = SafeManifestValue(obj, "author"),
                    Description = SafeManifestValue(obj, "description"),
                    Assembly = SafeManifestValue(obj, "assembly"),
                    Type = SafeManifestValue(obj, "type")
                };
                descriptor.HelpLinks = ReadHelpLinks(obj, descriptor);
                return descriptor;
            }
            catch (Exception ex)
            {
                if (log != null)
                {
                    log("Error", "Could not read Plug-In manifest " + manifest + ": " + ex.Message);
                }

                return null;
            }
        }

        private static List<PlugInHelpLink> ReadHelpLinks(JObject obj, PlugInDescriptor descriptor)
        {
            var links = new List<PlugInHelpLink>();
            var array = obj == null ? null : obj["helpLinks"] as JArray;
            if (array == null)
            {
                return links;
            }

            foreach (var item in array.OfType<JObject>())
            {
                var label = SafeManifestValue(item, "label");
                var url = SafeManifestValue(item, "url");
                if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(url) || !IsSafeHelpUrl(url))
                {
                    continue;
                }

                links.Add(new PlugInHelpLink
                {
                    PlugInId = descriptor == null ? "" : descriptor.Id,
                    PlugInName = descriptor == null ? "" : descriptor.Name,
                    Label = label,
                    Url = url
                });
            }

            return links;
        }

        private static bool IsSafeHelpUrl(string url)
        {
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri) &&
                (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase));
        }

        private static string SafeManifestValue(JObject obj, string key)
        {
            return obj == null || obj[key] == null ? "" : Convert.ToString(obj[key]).Trim();
        }

        private static bool IsEnabled(AppSettings settings, PlugInDescriptor descriptor)
        {
            bool enabled;
            if (settings != null &&
                settings.PlugInsEnabled != null &&
                settings.PlugInsEnabled.TryGetValue(descriptor.Id, out enabled))
            {
                return enabled;
            }

            return false;
        }

        private static SensorRow ToSensorRow(SensorReading reading, LoadedPlugIn plugIn)
        {
            return new SensorRow
            {
                Type = reading.Type,
                Hardware = reading.Hardware,
                Name = reading.Name,
                Identifier = reading.Identifier,
                Value = reading.Value,
                DisplayValue = reading.DisplayValue,
                Source = string.IsNullOrWhiteSpace(reading.Source) ? plugIn.Descriptor.Name : reading.Source,
                Details = reading.Details == null || reading.Details.Count == 0
                    ? null
                    : new Dictionary<string, string>(reading.Details, StringComparer.OrdinalIgnoreCase)
            };
        }

        private static MachineInfo ReadMachineInfo(Action<string, string> log)
        {
            var machine = new MachineInfo();
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
                {
                    foreach (ManagementObject system in ExecuteWmiQuery(searcher, "WMI"))
                    {
                        machine.Manufacturer = Convert.ToString(system["Manufacturer"]) ?? "";
                        machine.Model = Convert.ToString(system["Model"]) ?? "";
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                if (log != null)
                {
                    log("Debug", "Machine identity probe failed: " + ex.Message);
                }
            }

            return machine;
        }
    }

    private sealed class PlugInContext : IPluginContext
    {
        private readonly Action<string, string> log;

        public PlugInContext(MachineInfo machine, string pluginDirectory, bool diagnosticsMode, Action<string, string> log)
        {
            Machine = machine ?? new MachineInfo();
            PluginDirectory = pluginDirectory ?? "";
            DiagnosticsMode = diagnosticsMode;
            this.log = log ?? delegate { };
        }

        public MachineInfo Machine { get; private set; }
        public string PluginDirectory { get; private set; }
        public bool DiagnosticsMode { get; private set; }

        public void Log(string level, string message)
        {
            log(level, message);
        }
    }

    private sealed class LoadedPlugIn
    {
        public string Id = "";
        public string Directory = "";
        public bool Enabled;
        public string Status = "";
        public PlugInDescriptor Descriptor;
        public ISensorReadoutPlugin Instance;
    }

    private sealed class PlugInDescriptor
    {
        public string Id = "";
        public string Name = "";
        public string Version = "";
        public string Author = "";
        public string Description = "";
        public string Assembly = "";
        public string Type = "";
        public List<PlugInHelpLink> HelpLinks = new List<PlugInHelpLink>();
    }
}
