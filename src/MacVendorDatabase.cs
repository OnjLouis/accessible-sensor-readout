using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

internal sealed class MacVendorDatabase
{
    private readonly Dictionary<string, string> vendors;
    private static MacVendorDatabase cached;

    private MacVendorDatabase(Dictionary<string, string> vendors)
    {
        this.vendors = vendors ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public static MacVendorDatabase Load(string dataFolder)
    {
        if (cached != null)
        {
            return cached;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        TryLoadCsv(Path.Combine(dataFolder ?? "", "oui.csv"), values);
        TryLoadText(Path.Combine(dataFolder ?? "", "oui.txt"), values);
        cached = new MacVendorDatabase(values);
        return cached;
    }

    public string Lookup(string macAddress)
    {
        var prefix = NormalizePrefix(macAddress);
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return "";
        }

        string vendor;
        return vendors.TryGetValue(prefix, out vendor) ? vendor : "";
    }

    private static void TryLoadCsv(string path, Dictionary<string, string> values)
    {
        if (!File.Exists(path))
        {
            return;
        }

        foreach (var line in File.ReadLines(path, Encoding.UTF8).Skip(1))
        {
            var fields = ParseCsvLine(line);
            if (fields.Count < 3)
            {
                continue;
            }

            AddVendor(values, fields[1], fields[2]);
        }
    }

    private static void TryLoadText(string path, Dictionary<string, string> values)
    {
        if (!File.Exists(path))
        {
            return;
        }

        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal) || trimmed.StartsWith(";", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = trimmed.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                AddVendor(values, parts[0], string.Join(" ", parts.Skip(1).ToArray()));
            }
        }
    }

    private static void AddVendor(Dictionary<string, string> values, string prefix, string vendor)
    {
        var normalized = NormalizePrefix(prefix);
        vendor = (vendor ?? "").Trim();
        if (string.IsNullOrWhiteSpace(normalized) || string.IsNullOrWhiteSpace(vendor) || values.ContainsKey(normalized))
        {
            return;
        }

        values[normalized] = vendor;
    }

    private static string NormalizePrefix(string value)
    {
        var hex = new string((value ?? "").Where(Uri.IsHexDigit).Take(6).Select(char.ToUpperInvariant).ToArray());
        return hex.Length == 6 ? hex : "";
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < (line ?? "").Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (ch == ',' && !inQuotes)
            {
                fields.Add(current.ToString());
                current.Length = 0;
            }
            else
            {
                current.Append(ch);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }
}
