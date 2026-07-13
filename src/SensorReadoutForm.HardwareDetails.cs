using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.IO.MemoryMappedFiles;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;

public sealed partial class SensorReadoutForm : Form
{
    private static Dictionary<string, string> GetCpuHardwareDetails()
    {
        lock (hardwareDetailsCacheLock)
        {
            if (cachedCpuHardwareDetails != null)
            {
                return new Dictionary<string, string>(cachedCpuHardwareDetails, StringComparer.OrdinalIgnoreCase);
            }
        }

        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Processor"))
            {
                foreach (ManagementObject cpu in ExecuteWmiQuery(searcher, "WMI"))
                {
                    AddDetail(details, "CPU name", GetWmiPropertyText(cpu, "Name"));
                    AddDetail(details, "CPU manufacturer", GetWmiPropertyText(cpu, "Manufacturer"));
                    AddDetail(details, "CPU description", GetWmiPropertyText(cpu, "Description"));
                    AddDetail(details, "CPU caption", GetWmiPropertyText(cpu, "Caption"));
                    AddDetail(details, "CPU device ID", GetWmiPropertyText(cpu, "DeviceID"));
                    AddDetail(details, "CPU processor ID", GetWmiPropertyText(cpu, "ProcessorId"));
                    AddDetail(details, "CPU socket", GetWmiPropertyText(cpu, "SocketDesignation"));
                    AddDetail(details, "CPU architecture", FormatCpuArchitecture(GetWmiPropertyValue(cpu, "Architecture")));
                    AddDetail(details, "CPU address width", FormatBits(GetWmiPropertyValue(cpu, "AddressWidth")));
                    AddDetail(details, "CPU data width", FormatBits(GetWmiPropertyValue(cpu, "DataWidth")));
                    AddDetail(details, "CPU family", GetWmiPropertyText(cpu, "Family"));
                    AddDetail(details, "CPU stepping", GetWmiPropertyText(cpu, "Stepping"));
                    AddDetail(details, "CPU revision", GetWmiPropertyText(cpu, "Revision"));
                    AddDetail(details, "CPU processor type", GetWmiPropertyText(cpu, "ProcessorType"));
                    AddDetail(details, "CPU upgrade method", GetWmiPropertyText(cpu, "UpgradeMethod"));
                    AddDetail(details, "CPU current voltage", FormatTenthsOfVolt(GetWmiPropertyValue(cpu, "CurrentVoltage")));
                    AddDetail(details, "CPU voltage caps", GetWmiPropertyText(cpu, "VoltageCaps"));
                    AddDetail(details, "CPU external clock", FormatMegahertz(GetWmiPropertyValue(cpu, "ExtClock")));
                    AddDetail(details, "CPU max clock", FormatMegahertz(GetWmiPropertyValue(cpu, "MaxClockSpeed")));
                    AddDetail(details, "CPU current clock", FormatMegahertz(GetWmiPropertyValue(cpu, "CurrentClockSpeed")));
                    AddDetail(details, "CPU cores", GetWmiPropertyText(cpu, "NumberOfCores"));
                    AddDetail(details, "CPU enabled cores", GetWmiPropertyText(cpu, "NumberOfEnabledCore"));
                    AddDetail(details, "CPU logical processors", GetWmiPropertyText(cpu, "NumberOfLogicalProcessors"));
                    AddDetail(details, "CPU thread count", GetWmiPropertyText(cpu, "ThreadCount"));
                    AddDetail(details, "CPU L2 cache size", FormatCacheWmiKilobytes(GetWmiPropertyValue(cpu, "L2CacheSize")));
                    AddDetail(details, "CPU L2 cache speed", FormatMegahertz(GetWmiPropertyValue(cpu, "L2CacheSpeed")));
                    AddDetail(details, "CPU L3 cache size", FormatCacheWmiKilobytes(GetWmiPropertyValue(cpu, "L3CacheSize")));
                    AddDetail(details, "CPU L3 cache speed", FormatMegahertz(GetWmiPropertyValue(cpu, "L3CacheSpeed")));
                    AddDetail(details, "CPU instruction sets", GetProcessorInstructionSetSummary());
                    AddDetail(details, "CPU virtualization extensions", FormatWindowsReportedCpuFeature(GetWmiPropertyValue(cpu, "VMMonitorModeExtensions")));
                    AddDetail(details, "CPU virtualization enabled in firmware", FormatYesNo(GetWmiPropertyValue(cpu, "VirtualizationFirmwareEnabled")));
                    AddDetail(details, "CPU hardware VM memory translation (SLAT/EPT/NPT)", FormatWindowsReportedCpuFeature(GetWmiPropertyValue(cpu, "SecondLevelAddressTranslationExtensions")));
                    AddDetail(details, "CPU data execution prevention", GetProcessorFeatureYesNo(12));
                    AddDetail(details, "CPU power management supported", FormatYesNo(GetWmiPropertyValue(cpu, "PowerManagementSupported")));
                    AddDetail(details, "CPU power management capabilities", FormatWmiDetailValue(GetWmiPropertyValue(cpu, "PowerManagementCapabilities")));
                    AddDetail(details, "CPU status", GetWmiPropertyText(cpu, "Status"));
                    AddDetail(details, "CPU status code", GetWmiPropertyText(cpu, "CpuStatus"));
                    AddDetail(details, "CPU availability", GetWmiPropertyText(cpu, "Availability"));
                    AddDetail(details, "CPU role", GetWmiPropertyText(cpu, "Role"));
                    AddDetail(details, "CPU system name", GetWmiPropertyText(cpu, "SystemName"));
                    AddRawWmiDetails(details, "CPU WMI", cpu);
                    break;
                }
            }
        }
        catch
        {
        }

        AddCpuCacheDetails(details);

        lock (hardwareDetailsCacheLock)
        {
            cachedCpuHardwareDetails = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase);
        }

        return details;
    }

    private static void AddCpuCacheDetails(Dictionary<string, string> details)
    {
        try
        {
            var index = 1;
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_CacheMemory"))
            {
                foreach (ManagementObject cache in ExecuteWmiQuery(searcher, "WMI"))
                {
                    var label = "CPU cache " + index;
                    var level = CpuCacheLevelName(GetWmiPropertyValue(cache, "Level"), GetWmiPropertyValue(cache, "Purpose"));
                    AddDetail(details, label + " level", level);
                    AddDetail(details, label + " purpose", GetWmiPropertyText(cache, "Purpose"));
                    AddDetail(details, label + " installed size", FormatCacheWmiKilobytes(GetWmiPropertyValue(cache, "InstalledSize")));
                    AddDetail(details, label + " maximum size", FormatCacheWmiKilobytes(GetWmiPropertyValue(cache, "MaxCacheSize")));
                    AddDetail(details, label + " associativity", FormatCacheAssociativity(GetWmiPropertyValue(cache, "Associativity")));
                    AddDetail(details, label + " availability", FormatAvailability(GetWmiPropertyValue(cache, "Availability")));
                    AddDetail(details, label + " block size", GetWmiPropertyText(cache, "BlockSize"));
                    AddDetail(details, label + " cache speed", FormatMegahertz(GetWmiPropertyValue(cache, "CacheSpeed")));
                    AddDetail(details, label + " cache type", FormatCacheType(GetWmiPropertyValue(cache, "CacheType")));
                    AddDetail(details, label + " error method", FormatCacheErrorCorrectType(GetWmiPropertyValue(cache, "ErrorCorrectType")));
                    AddDetail(details, label + " SRAM type", FormatWmiDetailValue(GetWmiPropertyValue(cache, "SRAMType")));
                    AddDetail(details, label + " write policy", FormatCacheWritePolicy(GetWmiPropertyValue(cache, "WritePolicy")));
                    AddRawWmiDetails(details, label + " WMI", cache);
                    index++;
                }
            }
        }
        catch
        {
        }
    }

    private static Dictionary<string, string> GetMemoryHardwareDetails()
    {
        lock (hardwareDetailsCacheLock)
        {
            if (cachedMemoryHardwareDetails != null)
            {
                return new Dictionary<string, string>(cachedMemoryHardwareDetails, StringComparer.OrdinalIgnoreCase);
            }
        }

        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddMemoryArrayDetails(details);
        AddPhysicalMemoryDetails(details);

        lock (hardwareDetailsCacheLock)
        {
            cachedMemoryHardwareDetails = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase);
        }

        return details;
    }

    private static void AddMemoryArrayDetails(Dictionary<string, string> details)
    {
        try
        {
            var index = 1;
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PhysicalMemoryArray"))
            {
                foreach (ManagementObject array in ExecuteWmiQuery(searcher, "WMI"))
                {
                    var label = "Memory array " + index;
                    AddDetail(details, label + " location", GetWmiPropertyText(array, "Location"));
                    AddDetail(details, label + " use", GetWmiPropertyText(array, "Use"));
                    AddDetail(details, label + " memory error correction", GetWmiPropertyText(array, "MemoryErrorCorrection"));
                    AddDetail(details, label + " maximum capacity", FormatMemoryArrayCapacity(array));
                    AddDetail(details, label + " memory devices", GetWmiPropertyText(array, "MemoryDevices"));
                    AddDetail(details, label + " status", GetWmiPropertyText(array, "Status"));
                    AddRawWmiDetails(details, label + " WMI", array);
                    index++;
                }
            }
        }
        catch
        {
        }
    }

    private static void AddPhysicalMemoryDetails(Dictionary<string, string> details)
    {
        try
        {
            var index = 1;
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PhysicalMemory"))
            {
                foreach (ManagementObject module in ExecuteWmiQuery(searcher, "WMI"))
                {
                    var locator = FirstNonEmpty(GetWmiPropertyText(module, "DeviceLocator"), GetWmiPropertyText(module, "BankLabel"), "Module " + index);
                    var label = "Memory " + locator;
                    AddDetail(details, label + " capacity", FormatBytes(GetWmiPropertyValue(module, "Capacity")));
                    AddDetail(details, label + " manufacturer", GetWmiPropertyText(module, "Manufacturer"));
                    AddDetail(details, label + " part number", GetWmiPropertyText(module, "PartNumber"));
                    AddDetail(details, label + " serial number", GetWmiPropertyText(module, "SerialNumber"));
                    AddDetail(details, label + " bank", GetWmiPropertyText(module, "BankLabel"));
                    AddDetail(details, label + " device locator", GetWmiPropertyText(module, "DeviceLocator"));
                    AddDetail(details, label + " form factor", FormatMemoryFormFactor(GetWmiPropertyValue(module, "FormFactor")));
                    AddDetail(details, label + " memory type", FormatMemoryType(GetWmiPropertyValue(module, "MemoryType")));
                    AddDetail(details, label + " SMBIOS memory type", FormatSmbiosMemoryType(GetWmiPropertyValue(module, "SMBIOSMemoryType")));
                    AddDetail(details, label + " type detail", FormatMemoryTypeDetail(GetWmiPropertyValue(module, "TypeDetail")));
                    AddDetail(details, label + " speed", FormatMegahertz(GetWmiPropertyValue(module, "Speed")));
                    AddDetail(details, label + " configured speed", FormatMegahertz(GetWmiPropertyValue(module, "ConfiguredClockSpeed")));
                    AddDetail(details, label + " data width", FormatBits(GetWmiPropertyValue(module, "DataWidth")));
                    AddDetail(details, label + " total width", FormatBits(GetWmiPropertyValue(module, "TotalWidth")));
                    AddDetail(details, label + " configured voltage", FormatMillivolts(GetWmiPropertyValue(module, "ConfiguredVoltage")));
                    AddDetail(details, label + " minimum voltage", FormatMillivolts(GetWmiPropertyValue(module, "MinVoltage")));
                    AddDetail(details, label + " maximum voltage", FormatMillivolts(GetWmiPropertyValue(module, "MaxVoltage")));
                    AddDetail(details, label + " interleave data depth", GetWmiPropertyText(module, "InterleaveDataDepth"));
                    AddDetail(details, label + " interleave position", GetWmiPropertyText(module, "InterleavePosition"));
                    AddDetail(details, label + " position in row", GetWmiPropertyText(module, "PositionInRow"));
                    AddDetail(details, label + " tag", GetWmiPropertyText(module, "Tag"));
                    AddDetail(details, label + " status", GetWmiPropertyText(module, "Status"));
                    AddRawWmiDetails(details, label + " WMI", module);
                    index++;
                }
            }
        }
        catch
        {
        }
    }

    private static Dictionary<string, string> GetWindowsHardwareDetails()
    {
        lock (hardwareDetailsCacheLock)
        {
            if (cachedWindowsHardwareDetails != null)
            {
                return new Dictionary<string, string>(cachedWindowsHardwareDetails, StringComparer.OrdinalIgnoreCase);
            }
        }

        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddWindowsOperatingSystemDetails(details);
        AddWindowsRegistryDetails(details);
        AddWindowsLicensingDetails(details);
        AddComputerSystemProductDetails(details);

        lock (hardwareDetailsCacheLock)
        {
            cachedWindowsHardwareDetails = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase);
        }

        return details;
    }


    private static void AddWindowsOperatingSystemDetails(Dictionary<string, string> details)
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_OperatingSystem"))
            {
                foreach (ManagementObject os in ExecuteWmiQuery(searcher, "WMI"))
                {
                    AddDetail(details, "Windows edition", GetWmiPropertyText(os, "Caption"));
                    AddDetail(details, "Windows version", GetWmiPropertyText(os, "Version"));
                    AddDetail(details, "Windows build", GetWmiPropertyText(os, "BuildNumber"));
                    AddDetail(details, "Windows build type", GetWmiPropertyText(os, "BuildType"));
                    AddDetail(details, "Windows architecture", GetWmiPropertyText(os, "OSArchitecture"));
                    AddDetail(details, "Windows install date", FormatWindowsInstallDate(GetWmiPropertyValue(os, "InstallDate")));
                    AddDetail(details, "Windows last boot time", FormatWmiDateWithAge(GetWmiPropertyValue(os, "LastBootUpTime")));
                    AddDetail(details, "Windows directory", GetWmiPropertyText(os, "WindowsDirectory"));
                    AddDetail(details, "Windows system directory", GetWmiPropertyText(os, "SystemDirectory"));
                    AddDetail(details, "Windows system drive", GetWmiPropertyText(os, "SystemDrive"));
                    AddDetail(details, "Windows boot device", GetWmiPropertyText(os, "BootDevice"));
                    AddDetail(details, "Windows system device", GetWmiPropertyText(os, "SystemDevice"));
                    AddDetail(details, "Windows locale", GetWmiPropertyText(os, "Locale"));
                    AddDetail(details, "Windows country code", GetWmiPropertyText(os, "CountryCode"));
                    AddDetail(details, "Windows code set", GetWmiPropertyText(os, "CodeSet"));
                    AddDetail(details, "Windows language", GetWmiPropertyText(os, "OSLanguage"));
                    AddDetail(details, "Windows MUI languages", FormatWmiDetailValue(GetWmiPropertyValue(os, "MUILanguages")));
                    AddDetail(details, "Windows encryption level", GetWmiPropertyText(os, "EncryptionLevel"));
                    AddDetail(details, "Windows portable OS", FormatYesNo(GetWmiPropertyValue(os, "PortableOperatingSystem")));
                    AddDetail(details, "Windows product type", FormatWindowsProductType(GetWmiPropertyValue(os, "ProductType")));
                    AddDetail(details, "Windows operating system SKU", GetWmiPropertyText(os, "OperatingSystemSKU"));
                    AddDetail(details, "Windows product suite", GetWmiPropertyText(os, "OSProductSuite"));
                    AddDetail(details, "Windows suite mask", GetWmiPropertyText(os, "SuiteMask"));
                    AddDetail(details, "Windows DEP available", FormatYesNo(GetWmiPropertyValue(os, "DataExecutionPrevention_Available")));
                    AddDetail(details, "Windows DEP for drivers", FormatYesNo(GetWmiPropertyValue(os, "DataExecutionPrevention_Drivers")));
                    AddDetail(details, "Windows DEP for 32-bit apps", FormatYesNo(GetWmiPropertyValue(os, "DataExecutionPrevention_32BitApplications")));
                    AddDetail(details, "Windows DEP support policy", GetWmiPropertyText(os, "DataExecutionPrevention_SupportPolicy"));
                    AddDetail(details, "Windows number of users", GetWmiPropertyText(os, "NumberOfUsers"));
                    AddDetail(details, "Windows number of processes", GetWmiPropertyText(os, "NumberOfProcesses"));
                    AddDetail(details, "Windows maximum process memory", FormatBytesFromKilobytes(GetWmiPropertyValue(os, "MaxProcessMemorySize")));
                    AddDetail(details, "Windows total visible memory", FormatBytesFromKilobytes(GetWmiPropertyValue(os, "TotalVisibleMemorySize")));
                    AddDetail(details, "Windows free physical memory", FormatBytesFromKilobytes(GetWmiPropertyValue(os, "FreePhysicalMemory")));
                    AddDetail(details, "Windows total virtual memory", FormatBytesFromKilobytes(GetWmiPropertyValue(os, "TotalVirtualMemorySize")));
                    AddDetail(details, "Windows free virtual memory", FormatBytesFromKilobytes(GetWmiPropertyValue(os, "FreeVirtualMemory")));
                    AddDetail(details, "Windows page file size", FormatBytesFromKilobytes(GetWmiPropertyValue(os, "SizeStoredInPagingFiles")));
                    AddDetail(details, "Windows page file free space", FormatBytesFromKilobytes(GetWmiPropertyValue(os, "FreeSpaceInPagingFiles")));
                    AddDetail(details, "Windows product ID", GetWmiPropertyText(os, "SerialNumber"));
                    break;
                }
            }
        }
        catch
        {
        }
    }

    private static void AddWindowsRegistryDetails(Dictionary<string, string> details)
    {
        try
        {
            using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                if (key == null)
                {
                    return;
                }

                AddDetail(details, "Windows registry product name", Convert.ToString(key.GetValue("ProductName")));
                AddDetail(details, "Windows registry display version", Convert.ToString(key.GetValue("DisplayVersion")));
                AddDetail(details, "Windows registry release ID", Convert.ToString(key.GetValue("ReleaseId")));
                AddDetail(details, "Windows registry edition ID", Convert.ToString(key.GetValue("EditionID")));
                AddDetail(details, "Windows registry installation type", Convert.ToString(key.GetValue("InstallationType")));
                AddDetail(details, "Windows registry current build", Convert.ToString(key.GetValue("CurrentBuild")));
                AddDetail(details, "Windows registry current build number", Convert.ToString(key.GetValue("CurrentBuildNumber")));
                AddDetail(details, "Windows registry update build revision", Convert.ToString(key.GetValue("UBR")));
                AddDetail(details, "Windows registry build branch", Convert.ToString(key.GetValue("BuildBranch")));
                AddDetail(details, "Windows registry build lab", Convert.ToString(key.GetValue("BuildLab")));
                AddDetail(details, "Windows registry build lab ex", Convert.ToString(key.GetValue("BuildLabEx")));
                AddDetail(details, "Windows registry product ID", Convert.ToString(key.GetValue("ProductId")));
                AddDetail(details, "Windows registry composition edition ID", Convert.ToString(key.GetValue("CompositionEditionID")));
                AddDetail(details, "Windows registry edition build lab", Convert.ToString(key.GetValue("EditionSubManufacturer")));
            }
        }
        catch
        {
        }
    }

    private static void AddWindowsLicensingDetails(Dictionary<string, string> details)
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Version, OA3xOriginalProductKey, ClientMachineID FROM SoftwareLicensingService"))
            {
                foreach (ManagementObject service in ExecuteWmiQuery(searcher, "WMI"))
                {
                    AddDetail(details, "Windows licensing service version", GetWmiPropertyText(service, "Version"));
                    AddDetail(details, "Windows client machine ID", GetWmiPropertyText(service, "ClientMachineID"));
                    AddDetail(details, "Windows OEM embedded product key ending", MaskProductKey(GetWmiPropertyText(service, "OA3xOriginalProductKey")));
                    break;
                }
            }
        }
        catch
        {
        }

        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT Name, Description, LicenseStatus, PartialProductKey, ProductKeyChannel, GracePeriodRemaining FROM SoftwareLicensingProduct WHERE PartialProductKey IS NOT NULL"))
            {
                var index = 1;
                foreach (ManagementObject product in ExecuteWmiQuery(searcher, "WMI"))
                {
                    var label = "Windows license " + index;
                    AddDetail(details, label + " name", GetWmiPropertyText(product, "Name"));
                    AddDetail(details, label + " description", GetWmiPropertyText(product, "Description"));
                    AddDetail(details, label + " status", FormatLicenseStatus(GetWmiPropertyValue(product, "LicenseStatus")));
                    AddDetail(details, label + " product key ending", MaskPartialProductKey(GetWmiPropertyText(product, "PartialProductKey")));
                    AddDetail(details, label + " product key channel", GetWmiPropertyText(product, "ProductKeyChannel"));
                    AddDetail(details, label + " grace period remaining", FormatLicenseGracePeriod(GetWmiPropertyValue(product, "GracePeriodRemaining")));
                    index++;
                }
            }
        }
        catch
        {
        }
    }

    private static void AddComputerSystemProductDetails(Dictionary<string, string> details)
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_ComputerSystemProduct"))
            {
                foreach (ManagementObject product in ExecuteWmiQuery(searcher, "WMI"))
                {
                    AddDetail(details, "System product vendor", GetWmiPropertyText(product, "Vendor"));
                    AddDetail(details, "System product name", GetWmiPropertyText(product, "Name"));
                    AddDetail(details, "System product version", GetWmiPropertyText(product, "Version"));
                    AddDetail(details, "System product SKU", GetWmiPropertyText(product, "SKUNumber"));
                    AddDetail(details, "System product UUID", GetWmiPropertyText(product, "UUID"));
                    AddDetail(details, "System product identifying number", GetWmiPropertyText(product, "IdentifyingNumber"));
                    AddRawWmiDetails(details, "System product WMI", product);
                    break;
                }
            }
        }
        catch
        {
        }
    }

    private static Dictionary<string, string> GetFirmwareHardwareDetails()
    {
        lock (hardwareDetailsCacheLock)
        {
            if (cachedFirmwareHardwareDetails != null)
            {
                return new Dictionary<string, string>(cachedFirmwareHardwareDetails, StringComparer.OrdinalIgnoreCase);
            }
        }

        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddFirmwareBiosDetails(details);
        AddTpmDetails(details);
        AddDetail(details, "BIOS mode", GetFirmwareMode());
        AddDetail(details, "Secure Boot", GetSecureBootState());

        lock (hardwareDetailsCacheLock)
        {
            cachedFirmwareHardwareDetails = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase);
        }

        return details;
    }

    private static void AddFirmwareBiosDetails(Dictionary<string, string> details)
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_BIOS"))
            {
                foreach (ManagementObject bios in ExecuteWmiQuery(searcher, "WMI"))
                {
                    AddDetail(details, "BIOS vendor", GetWmiPropertyText(bios, "Manufacturer"));
                    AddDetail(details, "BIOS name", GetWmiPropertyText(bios, "Name"));
                    AddDetail(details, "BIOS caption", GetWmiPropertyText(bios, "Caption"));
                    AddDetail(details, "BIOS description", GetWmiPropertyText(bios, "Description"));
                    AddDetail(details, "BIOS version", GetWmiPropertyText(bios, "SMBIOSBIOSVersion"));
                    AddDetail(details, "BIOS release date", FormatWmiDate(GetWmiPropertyValue(bios, "ReleaseDate")));
                    AddDetail(details, "BIOS serial number", GetWmiPropertyText(bios, "SerialNumber"));
                    AddDetail(details, "BIOS status", GetWmiPropertyText(bios, "Status"));
                    AddDetail(details, "BIOS characteristics", FormatWmiDetailValue(GetWmiPropertyValue(bios, "BiosCharacteristics")));
                    AddDetail(details, "BIOS language edition", GetWmiPropertyText(bios, "LanguageEdition"));
                    AddDetail(details, "BIOS list of languages", FormatWmiDetailValue(GetWmiPropertyValue(bios, "ListOfLanguages")));
                    AddDetail(details, "BIOS current language", GetWmiPropertyText(bios, "CurrentLanguage"));
                    AddDetail(details, "SMBIOS present", FormatYesNo(GetWmiPropertyValue(bios, "SMBIOSPresent")));
                    AddDetail(details, "SMBIOS major version", GetWmiPropertyText(bios, "SMBIOSMajorVersion"));
                    AddDetail(details, "SMBIOS minor version", GetWmiPropertyText(bios, "SMBIOSMinorVersion"));
                    AddDetail(details, "SMBIOS version", FormatMajorMinor(GetWmiPropertyValue(bios, "SMBIOSMajorVersion"), GetWmiPropertyValue(bios, "SMBIOSMinorVersion"), true));
                    AddDetail(details, "System BIOS major version", GetWmiPropertyText(bios, "SystemBiosMajorVersion"));
                    AddDetail(details, "System BIOS minor version", GetWmiPropertyText(bios, "SystemBiosMinorVersion"));
                    AddDetail(details, "Embedded controller major version", GetWmiPropertyText(bios, "EmbeddedControllerMajorVersion"));
                    AddDetail(details, "Embedded controller minor version", GetWmiPropertyText(bios, "EmbeddedControllerMinorVersion"));
                    AddRawWmiDetails(details, "BIOS WMI", bios);
                    break;
                }
            }
        }
        catch
        {
        }
    }

    private static void AddTpmDetails(Dictionary<string, string> details)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftTpm");
            scope.Connect();
            using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM Win32_Tpm")))
            {
                foreach (ManagementObject tpm in ExecuteWmiQuery(searcher, "WMI"))
                {
                    AddDetail(details, "TPM enabled", FormatYesNo(GetWmiPropertyValue(tpm, "IsEnabled_InitialValue")));
                    AddDetail(details, "TPM activated", FormatYesNo(GetWmiPropertyValue(tpm, "IsActivated_InitialValue")));
                    AddDetail(details, "TPM owned", FormatYesNo(GetWmiPropertyValue(tpm, "IsOwned_InitialValue")));
                    AddDetail(details, "TPM manufacturer ID", GetWmiPropertyText(tpm, "ManufacturerId"));
                    AddDetail(details, "TPM manufacturer version", GetWmiPropertyText(tpm, "ManufacturerVersion"));
                    AddDetail(details, "TPM spec version", GetWmiPropertyText(tpm, "SpecVersion"));
                    AddRawWmiDetails(details, "TPM WMI", tpm);
                    break;
                }
            }
        }
        catch
        {
        }
    }

    private static Dictionary<string, string> GetBoardHardwareDetails()
    {
        lock (hardwareDetailsCacheLock)
        {
            if (cachedBoardHardwareDetails != null)
            {
                return new Dictionary<string, string>(cachedBoardHardwareDetails, StringComparer.OrdinalIgnoreCase);
            }
        }

        var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddBaseBoardDetails(details);
        AddSystemEnclosureDetails(details);
        AddBoardMemorySlotDetails(details);
        AddSystemSlotDetails(details);

        lock (hardwareDetailsCacheLock)
        {
            cachedBoardHardwareDetails = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase);
        }

        return details;
    }

    private static void AddBaseBoardDetails(Dictionary<string, string> details)
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_BaseBoard"))
            {
                foreach (ManagementObject board in ExecuteWmiQuery(searcher, "WMI"))
                {
                    AddDetail(details, "Baseboard manufacturer", GetWmiPropertyText(board, "Manufacturer"));
                    AddDetail(details, "Baseboard product", GetWmiPropertyText(board, "Product"));
                    AddDetail(details, "Baseboard version", GetWmiPropertyText(board, "Version"));
                    AddDetail(details, "Baseboard serial number", GetWmiPropertyText(board, "SerialNumber"));
                    AddDetail(details, "Baseboard part number", GetWmiPropertyText(board, "PartNumber"));
                    AddDetail(details, "Baseboard SKU", GetWmiPropertyText(board, "SKU"));
                    AddDetail(details, "Baseboard model", GetWmiPropertyText(board, "Model"));
                    AddDetail(details, "Baseboard tag", GetWmiPropertyText(board, "Tag"));
                    AddDetail(details, "Baseboard hosting board", FormatYesNo(GetWmiPropertyValue(board, "HostingBoard")));
                    AddDetail(details, "Baseboard hot swappable", FormatYesNo(GetWmiPropertyValue(board, "HotSwappable")));
                    AddDetail(details, "Baseboard removable", FormatYesNo(GetWmiPropertyValue(board, "Removable")));
                    AddDetail(details, "Baseboard replaceable", FormatYesNo(GetWmiPropertyValue(board, "Replaceable")));
                    AddDetail(details, "Baseboard requires daughter board", FormatYesNo(GetWmiPropertyValue(board, "RequiresDaughterBoard")));
                    AddDetail(details, "Baseboard slot layout", GetWmiPropertyText(board, "SlotLayout"));
                    AddDetail(details, "Baseboard configuration options", FormatWmiDetailValue(GetWmiPropertyValue(board, "ConfigOptions")));
                    AddDetail(details, "Baseboard special requirements", FormatYesNo(GetWmiPropertyValue(board, "SpecialRequirements")));
                    AddDetail(details, "Baseboard requirements description", GetWmiPropertyText(board, "RequirementsDescription"));
                    AddDetail(details, "Baseboard status", GetWmiPropertyText(board, "Status"));
                    AddRawWmiDetails(details, "Baseboard WMI", board);
                    break;
                }
            }
        }
        catch
        {
        }
    }

    private static void AddSystemEnclosureDetails(Dictionary<string, string> details)
    {
        try
        {
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_SystemEnclosure"))
            {
                foreach (ManagementObject enclosure in ExecuteWmiQuery(searcher, "WMI"))
                {
                    AddDetail(details, "Chassis manufacturer", GetWmiPropertyText(enclosure, "Manufacturer"));
                    AddDetail(details, "Chassis version", GetWmiPropertyText(enclosure, "Version"));
                    AddDetail(details, "Chassis serial number", GetWmiPropertyText(enclosure, "SerialNumber"));
                    AddDetail(details, "Chassis asset tag", GetWmiPropertyText(enclosure, "SMBIOSAssetTag"));
                    AddDetail(details, "Chassis type", FormatChassisTypes(GetWmiPropertyValue(enclosure, "ChassisTypes")));
                    AddDetail(details, "Chassis type descriptions", FormatWmiDetailValue(GetWmiPropertyValue(enclosure, "TypeDescriptions")));
                    AddDetail(details, "Chassis lock present", FormatYesNo(GetWmiPropertyValue(enclosure, "LockPresent")));
                    AddDetail(details, "Chassis security status", FormatChassisSecurityStatus(GetWmiPropertyValue(enclosure, "SecurityStatus")));
                    AddDetail(details, "Chassis security breach", FormatWmiDetailValue(GetWmiPropertyValue(enclosure, "SecurityBreach")));
                    AddDetail(details, "Chassis number of power cords", GetWmiPropertyText(enclosure, "NumberOfPowerCords"));
                    AddDetail(details, "Chassis status", GetWmiPropertyText(enclosure, "Status"));
                    AddRawWmiDetails(details, "Chassis WMI", enclosure);
                    break;
                }
            }
        }
        catch
        {
        }
    }

    private static void AddBoardMemorySlotDetails(Dictionary<string, string> details)
    {
        try
        {
            var index = 1;
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PhysicalMemoryArray"))
            {
                foreach (ManagementObject array in ExecuteWmiQuery(searcher, "WMI"))
                {
                    var label = "Board memory array " + index;
                    AddDetail(details, label + " slots", GetWmiPropertyText(array, "MemoryDevices"));
                    AddDetail(details, label + " maximum capacity", FormatMemoryArrayCapacity(array));
                    AddDetail(details, label + " use", FormatMemoryArrayUse(GetWmiPropertyValue(array, "Use")));
                    AddDetail(details, label + " location", FormatMemoryArrayLocation(GetWmiPropertyValue(array, "Location")));
                    AddDetail(details, label + " error correction", FormatMemoryErrorCorrection(GetWmiPropertyValue(array, "MemoryErrorCorrection")));
                    index++;
                }
            }
        }
        catch
        {
        }
    }

    private static void AddSystemSlotDetails(Dictionary<string, string> details)
    {
        try
        {
            var slots = new List<ManagementObject>();
            using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_SystemSlot"))
            {
                foreach (ManagementObject slot in ExecuteWmiQuery(searcher, "WMI"))
                {
                    slots.Add(slot);
                }
            }

            AddDetail(details, "Expansion slot count", slots.Count.ToString());
            for (var index = 0; index < slots.Count; index++)
            {
                using (var slot = slots[index])
                {
                    var label = "Expansion slot " + (index + 1);
                    AddDetail(details, label + " designation", GetWmiPropertyText(slot, "SlotDesignation"));
                    AddDetail(details, label + " current usage", FormatSystemSlotCurrentUsage(GetWmiPropertyValue(slot, "CurrentUsage")));
                    AddDetail(details, label + " connector type", FormatSystemSlotConnectorTypes(GetWmiPropertyValue(slot, "ConnectorType")));
                    AddDetail(details, label + " maximum data width", FormatSystemSlotDataWidth(GetWmiPropertyValue(slot, "MaxDataWidth")));
                    AddDetail(details, label + " hot plug supported", FormatYesNo(GetWmiPropertyValue(slot, "SupportsHotPlug")));
                    AddDetail(details, label + " status", GetWmiPropertyText(slot, "Status"));
                    AddRawWmiDetails(details, label + " WMI", slot);
                }
            }
        }
        catch
        {
        }
    }

    private static void AddRawWmiDetails(Dictionary<string, string> details, string prefix, ManagementBaseObject obj)
    {
        if (details == null || obj == null || string.IsNullOrWhiteSpace(prefix))
        {
            return;
        }

        try
        {
            foreach (PropertyData property in obj.Properties)
            {
                if (property == null || string.IsNullOrWhiteSpace(property.Name))
                {
                    continue;
                }

                AddDetail(details, prefix + " " + SplitPascalCase(property.Name), FormatWmiDetailValue(property.Value));
            }
        }
        catch
        {
        }
    }

    private static Dictionary<string, string> CloneDetails(Dictionary<string, string> details)
    {
        return details == null || details.Count == 0
            ? null
            : new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase);
    }

    private static string FormatWmiDetailValue(object value)
    {
        if (value == null)
        {
            return "";
        }

        var array = value as Array;
        if (array != null && !(value is byte[]))
        {
            var parts = new List<string>();
            foreach (var item in array)
            {
                var text = Convert.ToString(item);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    parts.Add(text.Trim());
                }
            }

            return string.Join(", ", parts.ToArray());
        }

        var bytes = value as byte[];
        if (bytes != null)
        {
            if (bytes.Length == 0)
            {
                return "";
            }

            return BitConverter.ToString(bytes);
        }

        if (value is DateTime)
        {
            return FormatDateTime((DateTime)value);
        }

        var textValue = Convert.ToString(value);
        var looksLikeDmtf = !string.IsNullOrWhiteSpace(textValue) && textValue.Trim().Length >= 14 && textValue.Trim().Take(14).All(char.IsDigit);
        DateTime date;
        if (looksLikeDmtf && TryParseWmiDate(textValue, out date))
        {
            return FormatDateTime(date);
        }

        return CleanWmiText(textValue);
    }

    private static string SplitPascalCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        return Regex.Replace(value.Trim(), "([a-z0-9])([A-Z])", "$1 $2");
    }

    private static string FormatBits(object value)
    {
        var text = Convert.ToString(value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        return text.Trim() + "-bit";
    }

    private static string FormatBytesFromKilobytes(object value)
    {
        long kb;
        return TryConvertToInt64(value, out kb) && kb > 0 ? FormatBytes(kb * 1024.0) : "";
    }

    private static string FormatWindowsProductType(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 1: return "Workstation";
            case 2: return "Domain controller";
            case 3: return "Server";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatLicenseStatus(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 0: return "Unlicensed";
            case 1: return "Licensed";
            case 2: return "Out-of-box grace period";
            case 3: return "Out-of-tolerance grace period";
            case 4: return "Non-genuine grace period";
            case 5: return "Notification";
            case 6: return "Extended grace period";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatLicenseGracePeriod(object value)
    {
        long minutes;
        if (!TryConvertToInt64(value, out minutes) || minutes <= 0)
        {
            return "";
        }

        return FormatUptime(TimeSpan.FromMinutes(minutes));
    }

    private static string MaskPartialProductKey(string partial)
    {
        partial = (partial ?? "").Trim();
        return string.IsNullOrWhiteSpace(partial) ? "" : "ends with " + partial;
    }

    private static string MaskProductKey(string key)
    {
        key = (key ?? "").Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            return "";
        }

        var tail = key.Length <= 5 ? key : key.Substring(key.Length - 5);
        return "ends with " + tail;
    }

    private static string FormatMillivolts(object value)
    {
        long millivolts;
        if (!TryConvertToInt64(value, out millivolts) || millivolts <= 0)
        {
            return "";
        }

        return FormatNumber(Math.Round(millivolts / 1000.0, 3), "0.###") + " V";
    }

    private static string FormatTenthsOfVolt(object value)
    {
        long raw;
        if (!TryConvertToInt64(value, out raw) || raw <= 0)
        {
            return "";
        }

        return FormatNumber(Math.Round(raw / 10.0, 1), "0.0") + " V";
    }

    private static string FormatCacheWmiKilobytes(object value)
    {
        long sizeKb;
        return TryConvertToInt64(value, out sizeKb) && sizeKb > 0 ? FormatCacheKilobytes(sizeKb) : "";
    }

    private static string FormatCacheAssociativity(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 1: return "Other";
            case 2: return "Unknown";
            case 3: return "Direct mapped";
            case 4: return "2-way set associative";
            case 5: return "4-way set associative";
            case 6: return "Fully associative";
            case 7: return "8-way set associative";
            case 8: return "16-way set associative";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatCacheType(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 1: return "Other";
            case 2: return "Unknown";
            case 3: return "Instruction";
            case 4: return "Data";
            case 5: return "Unified";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatCacheErrorCorrectType(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 1: return "Other";
            case 2: return "Unknown";
            case 3: return "None";
            case 4: return "Parity";
            case 5: return "Single-bit ECC";
            case 6: return "Multi-bit ECC";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatCacheWritePolicy(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 1: return "Other";
            case 2: return "Unknown";
            case 3: return "Write back";
            case 4: return "Write through";
            case 5: return "Varies by memory address";
            case 6: return "Determined per I/O";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatAvailability(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 1: return "Other";
            case 2: return "Unknown";
            case 3: return "Running or full power";
            case 4: return "Warning";
            case 5: return "In test";
            case 6: return "Not applicable";
            case 7: return "Power off";
            case 8: return "Off line";
            case 9: return "Off duty";
            case 10: return "Degraded";
            case 11: return "Not installed";
            case 12: return "Install error";
            case 13: return "Power save unknown";
            case 14: return "Power save low power";
            case 15: return "Power save standby";
            case 16: return "Power cycle";
            case 17: return "Power save warning";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatChassisTypes(object value)
    {
        var array = value as Array;
        if (array == null)
        {
            return FormatChassisType(value);
        }

        var parts = new List<string>();
        foreach (var item in array)
        {
            var text = FormatChassisType(item);
            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text);
            }
        }

        return string.Join(", ", parts.ToArray());
    }

    private static string FormatChassisType(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 1: return "Other";
            case 2: return "Unknown";
            case 3: return "Desktop";
            case 4: return "Low-profile desktop";
            case 5: return "Pizza box";
            case 6: return "Mini tower";
            case 7: return "Tower";
            case 8: return "Portable";
            case 9: return "Laptop";
            case 10: return "Notebook";
            case 11: return "Handheld";
            case 12: return "Docking station";
            case 13: return "All-in-one";
            case 14: return "Sub-notebook";
            case 15: return "Space-saving";
            case 16: return "Lunch box";
            case 17: return "Main system chassis";
            case 18: return "Expansion chassis";
            case 19: return "Sub-chassis";
            case 20: return "Bus expansion chassis";
            case 21: return "Peripheral chassis";
            case 22: return "Storage chassis";
            case 23: return "Rack mount chassis";
            case 24: return "Sealed-case PC";
            case 30: return "Tablet";
            case 31: return "Convertible";
            case 32: return "Detachable";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatChassisSecurityStatus(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 1: return "Other";
            case 2: return "Unknown";
            case 3: return "None";
            case 4: return "External interface locked out";
            case 5: return "External interface enabled";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatMemoryArrayLocation(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 1: return "Other";
            case 2: return "Unknown";
            case 3: return "System board or motherboard";
            case 4: return "ISA add-on card";
            case 5: return "EISA add-on card";
            case 6: return "PCI add-on card";
            case 7: return "MCA add-on card";
            case 8: return "PCMCIA add-on card";
            case 9: return "Proprietary add-on card";
            case 10: return "NuBus";
            case 11: return "PC-98/C20 add-on card";
            case 12: return "PC-98/C24 add-on card";
            case 13: return "PC-98/E add-on card";
            case 14: return "PC-98/local bus add-on card";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatMemoryArrayUse(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 1: return "Other";
            case 2: return "Unknown";
            case 3: return "System memory";
            case 4: return "Video memory";
            case 5: return "Flash memory";
            case 6: return "Non-volatile RAM";
            case 7: return "Cache memory";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatMemoryErrorCorrection(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 1: return "Other";
            case 2: return "Unknown";
            case 3: return "None";
            case 4: return "Parity";
            case 5: return "Single-bit ECC";
            case 6: return "Multi-bit ECC";
            case 7: return "CRC";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatSystemSlotCurrentUsage(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 1: return "Other";
            case 2: return "Unknown";
            case 3: return "Available";
            case 4: return "In use";
            case 5: return "Unavailable";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatSystemSlotConnectorTypes(object value)
    {
        var array = value as Array;
        if (array == null)
        {
            return FormatSystemSlotConnectorType(value);
        }

        var parts = new List<string>();
        foreach (var item in array)
        {
            var text = FormatSystemSlotConnectorType(item);
            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text);
            }
        }

        return string.Join(", ", parts.ToArray());
    }

    private static string FormatSystemSlotConnectorType(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 0: return "Unknown";
            case 1: return "Other";
            case 2: return "ISA";
            case 3: return "MCA";
            case 4: return "EISA";
            case 5: return "PCI";
            case 6: return "PCMCIA";
            case 7: return "VL-VESA";
            case 8: return "Proprietary";
            case 9: return "Processor card slot";
            case 10: return "Proprietary memory card slot";
            case 11: return "I/O riser card slot";
            case 12: return "NuBus";
            case 13: return "PCI-66MHZ";
            case 14: return "AGP";
            case 15: return "AGP 2X";
            case 16: return "AGP 4X";
            case 17: return "PCI-X";
            case 18: return "AGP 8X";
            case 19: return "M.2 socket 1-DP";
            case 20: return "M.2 socket 1-SD";
            case 21: return "M.2 socket 2";
            case 22: return "M.2 socket 3";
            case 23: return "MXM type I";
            case 24: return "MXM type II";
            case 25: return "MXM type III";
            case 26: return "MXM type III-HE";
            case 27: return "MXM type IV";
            case 28: return "MXM 3.0 type A";
            case 29: return "MXM 3.0 type B";
            case 30: return "PCI Express Gen 2 SFF-8639";
            case 31: return "PCI Express Gen 3 SFF-8639";
            case 32: return "PCI Express Mini 52-pin";
            case 33: return "PCI Express Mini 52-pin with bottom-side keep-outs";
            case 34: return "PCI Express Mini 76-pin";
            case 35: return "PCI Express Gen 4 SFF-8639";
            case 36: return "PCI Express Gen 5 SFF-8639";
            case 37: return "OCP NIC 3.0 small form factor";
            case 38: return "OCP NIC 3.0 large form factor";
            case 39: return "OCP NIC prior to 3.0";
            case 40: return "CXL Flexbus 1.0";
            case 41: return "PC-98/C20";
            case 42: return "PC-98/C24";
            case 43: return "PC-98/E";
            case 44: return "PC-98/local bus";
            case 45: return "PCI Express";
            case 46: return "PCI Express x1";
            case 47: return "PCI Express x2";
            case 48: return "PCI Express x4";
            case 49: return "PCI Express x8";
            case 50: return "PCI Express x16";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatSystemSlotDataWidth(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 1: return "Other";
            case 2: return "Unknown";
            case 3: return "8-bit";
            case 4: return "16-bit";
            case 5: return "32-bit";
            case 6: return "64-bit";
            case 7: return "1x or x1";
            case 8: return "2x or x2";
            case 9: return "4x or x4";
            case 10: return "8x or x8";
            case 11: return "12x or x12";
            case 12: return "16x or x16";
            case 13: return "32x or x32";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatMemoryArrayCapacity(ManagementObject array)
    {
        if (array == null)
        {
            return "";
        }

        long kbEx;
        if (TryConvertToInt64(GetWmiPropertyValue(array, "MaxCapacityEx"), out kbEx) && kbEx > 0)
        {
            return FormatBytes(kbEx * 1024.0);
        }

        long kb;
        return TryConvertToInt64(GetWmiPropertyValue(array, "MaxCapacity"), out kb) && kb > 0 ? FormatBytes(kb * 1024.0) : "";
    }

    private static string FormatMemoryFormFactor(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 0: return "Unknown";
            case 1: return "Other";
            case 2: return "SIP";
            case 3: return "DIP";
            case 4: return "ZIP";
            case 5: return "SOJ";
            case 6: return "Proprietary";
            case 7: return "SIMM";
            case 8: return "DIMM";
            case 9: return "TSOP";
            case 10: return "PGA";
            case 11: return "RIMM";
            case 12: return "SODIMM";
            case 13: return "SRIMM";
            case 14: return "SMD";
            case 15: return "SSMP";
            case 16: return "QFP";
            case 17: return "TQFP";
            case 18: return "SOIC";
            case 19: return "LCC";
            case 20: return "PLCC";
            case 21: return "BGA";
            case 22: return "FPBGA";
            case 23: return "LGA";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatMemoryType(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 0: return "Unknown";
            case 1: return "Other";
            case 2: return "DRAM";
            case 3: return "Synchronous DRAM";
            case 4: return "Cache DRAM";
            case 5: return "EDO";
            case 6: return "EDRAM";
            case 7: return "VRAM";
            case 8: return "SRAM";
            case 9: return "RAM";
            case 10: return "ROM";
            case 11: return "Flash";
            case 12: return "EEPROM";
            case 13: return "FEPROM";
            case 14: return "EPROM";
            case 15: return "CDRAM";
            case 16: return "3DRAM";
            case 17: return "SDRAM";
            case 18: return "SGRAM";
            case 19: return "RDRAM";
            case 20: return "DDR";
            case 21: return "DDR2";
            case 22: return "DDR2 FB-DIMM";
            case 24: return "DDR3";
            case 25: return "FBD2";
            case 26: return "DDR4";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatSmbiosMemoryType(object value)
    {
        int code;
        if (!TryConvertToInt32(value, out code))
        {
            return Convert.ToString(value);
        }

        switch (code)
        {
            case 0: return "Unknown";
            case 1: return "Other";
            case 2: return "DRAM";
            case 3: return "Synchronous DRAM";
            case 4: return "Cache DRAM";
            case 5: return "EDO";
            case 6: return "EDRAM";
            case 7: return "VRAM";
            case 8: return "SRAM";
            case 9: return "RAM";
            case 10: return "ROM";
            case 11: return "Flash";
            case 12: return "EEPROM";
            case 13: return "FEPROM";
            case 14: return "EPROM";
            case 15: return "CDRAM";
            case 16: return "3DRAM";
            case 17: return "SDRAM";
            case 18: return "SGRAM";
            case 19: return "RDRAM";
            case 20: return "DDR";
            case 21: return "DDR2";
            case 22: return "DDR2 FB-DIMM";
            case 24: return "DDR3";
            case 25: return "FBD2";
            case 26: return "DDR4";
            case 27: return "LPDDR";
            case 28: return "LPDDR2";
            case 29: return "LPDDR3";
            case 30: return "LPDDR4";
            case 31: return "Logical non-volatile device";
            case 32: return "HBM";
            case 33: return "HBM2";
            case 34: return "DDR5";
            case 35: return "LPDDR5";
            default: return Convert.ToString(value);
        }
    }

    private static string FormatMemoryTypeDetail(object value)
    {
        int flags;
        if (!TryConvertToInt32(value, out flags) || flags == 0)
        {
            return Convert.ToString(value);
        }

        var parts = new List<string>();
        AddMemoryTypeDetailFlag(parts, flags, 1, "Reserved");
        AddMemoryTypeDetailFlag(parts, flags, 2, "Other");
        AddMemoryTypeDetailFlag(parts, flags, 4, "Unknown");
        AddMemoryTypeDetailFlag(parts, flags, 8, "Fast-paged");
        AddMemoryTypeDetailFlag(parts, flags, 16, "Static column");
        AddMemoryTypeDetailFlag(parts, flags, 32, "Pseudo-static");
        AddMemoryTypeDetailFlag(parts, flags, 64, "RAMBUS");
        AddMemoryTypeDetailFlag(parts, flags, 128, "Synchronous");
        AddMemoryTypeDetailFlag(parts, flags, 256, "CMOS");
        AddMemoryTypeDetailFlag(parts, flags, 512, "EDO");
        AddMemoryTypeDetailFlag(parts, flags, 1024, "Window DRAM");
        AddMemoryTypeDetailFlag(parts, flags, 2048, "Cache DRAM");
        AddMemoryTypeDetailFlag(parts, flags, 4096, "Non-volatile");
        return parts.Count == 0 ? Convert.ToString(value) : string.Join(", ", parts.ToArray());
    }

    private static void AddMemoryTypeDetailFlag(List<string> parts, int flags, int flag, string text)
    {
        if ((flags & flag) != 0)
        {
            parts.Add(text);
        }
    }
}
