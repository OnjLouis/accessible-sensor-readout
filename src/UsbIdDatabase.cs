using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

internal sealed class UsbIdEntry
{
    public string VendorId;
    public string ProductId;
    public string VendorName;
    public string ProductName;
}

internal static class UsbIdDatabase
{
    private static readonly object Sync = new object();
    private static bool loadAttempted;
    private static Dictionary<string, string> vendors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, UsbIdEntry> products = new Dictionary<string, UsbIdEntry>(StringComparer.OrdinalIgnoreCase);

    public static UsbIdEntry Lookup(string vidPidText)
    {
        string vendorId;
        string productId;
        if (!TryParseVidPid(vidPidText, out vendorId, out productId))
        {
            return null;
        }

        EnsureLoaded();

        UsbIdEntry entry;
        if (products.TryGetValue(vendorId + ":" + productId, out entry))
        {
            return entry;
        }

        string vendorName;
        if (vendors.TryGetValue(vendorId, out vendorName))
        {
            return new UsbIdEntry
            {
                VendorId = vendorId,
                ProductId = productId,
                VendorName = vendorName
            };
        }

        return null;
    }

    private static void EnsureLoaded()
    {
        if (loadAttempted)
        {
            return;
        }

        lock (Sync)
        {
            if (loadAttempted)
            {
                return;
            }

            loadAttempted = true;
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "usb.ids");
            if (!File.Exists(path))
            {
                return;
            }

            Load(path);
        }
    }

    private static void Load(string path)
    {
        string currentVendorId = "";
        string currentVendorName = "";
        foreach (var rawLine in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(rawLine) || rawLine.TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var vendorMatch = Regex.Match(rawLine, @"^([0-9a-fA-F]{4})\s+(.+)$");
            if (vendorMatch.Success)
            {
                currentVendorId = vendorMatch.Groups[1].Value.ToUpperInvariant();
                currentVendorName = vendorMatch.Groups[2].Value.Trim();
                vendors[currentVendorId] = currentVendorName;
                continue;
            }

            var productMatch = Regex.Match(rawLine, @"^\t([0-9a-fA-F]{4})\s+(.+)$");
            if (productMatch.Success && currentVendorId.Length != 0)
            {
                var productId = productMatch.Groups[1].Value.ToUpperInvariant();
                products[currentVendorId + ":" + productId] = new UsbIdEntry
                {
                    VendorId = currentVendorId,
                    ProductId = productId,
                    VendorName = currentVendorName,
                    ProductName = productMatch.Groups[2].Value.Trim()
                };
            }
        }
    }

    private static bool TryParseVidPid(string text, out string vendorId, out string productId)
    {
        vendorId = "";
        productId = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = Regex.Match(text, @"VID[_\s]?([0-9A-F]{4}).*PID[_\s]?([0-9A-F]{4})", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return false;
        }

        vendorId = match.Groups[1].Value.ToUpperInvariant();
        productId = match.Groups[2].Value.ToUpperInvariant();
        return true;
    }
}
