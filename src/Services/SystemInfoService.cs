using System;
using System.Collections.Generic;
using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Text;
using Microsoft.Win32;
using DriverPlus.Core;

namespace DriverPlus.Services
{
    public static class SystemInfoService
    {
        public static SystemProfile CollectSystemProfile()
        {
            var p = new SystemProfile
            {
                ComputerName = Environment.MachineName,
                OsBuildNumber = Environment.OSVersion.Version.Build,
                OsVersion = Environment.OSVersion.Version.ToString(),
                CpuLogicalProcessors = Environment.ProcessorCount
            };

            // Fast Registry reads first (<1 ms)
            try
            {
                using (RegistryKey rk = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", false))
                {
                    if (rk != null)
                    {
                        p.CpuName = (Convert.ToString(rk.GetValue("ProcessorNameString", "")) ?? "").Trim();
                    }
                }
                using (RegistryKey rk = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS", false))
                {
                    if (rk != null)
                    {
                        p.Manufacturer = (Convert.ToString(rk.GetValue("SystemManufacturer", "")) ?? "").Trim();
                        p.Model = (Convert.ToString(rk.GetValue("SystemProductName", "")) ?? "").Trim();
                        p.BiosVersion = (Convert.ToString(rk.GetValue("BIOSVersion", "")) ?? "").Trim();
                    }
                }
                using (RegistryKey rk = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", false))
                {
                    if (rk != null)
                    {
                        string prod = Convert.ToString(rk.GetValue("ProductName", "Windows")) ?? "Windows";
                        string dispVer = Convert.ToString(rk.GetValue("DisplayVersion", "")) ?? "";
                        int build = 0;
                        int.TryParse(Convert.ToString(rk.GetValue("CurrentBuildNumber", "0")), out build);
                        if (build > 0) p.OsBuildNumber = build;
                        if (p.OsBuildNumber >= 22000 && prod.IndexOf("Windows 10", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            prod = prod.Replace("Windows 10", "Windows 11");
                        }
                        p.OsCaption = string.IsNullOrEmpty(dispVer)
                            ? string.Format("{0} (Build {1})", prod, p.OsBuildNumber)
                            : string.Format("{0} {1} (Build {2})", prod, dispVer, p.OsBuildNumber);
                    }
                }
            }
            catch
            {
            }

            // Enrich RAM, Cores, GPUs & Storage via WMI
            QueryWmiSafe("SELECT NumberOfCores, Name FROM Win32_Processor", mo =>
            {
                if (string.IsNullOrEmpty(p.CpuName)) p.CpuName = Convert.ToString(mo["Name"]) ?? "";
                p.CpuCores += Convert.ToInt32(mo["NumberOfCores"] ?? 0);
            });

            QueryWmiSafe("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem", mo =>
            {
                double totalKb = Convert.ToDouble(mo["TotalVisibleMemorySize"] ?? 0, CultureInfo.InvariantCulture);
                double freeKb = Convert.ToDouble(mo["FreePhysicalMemory"] ?? 0, CultureInfo.InvariantCulture);
                p.TotalRamGb = Math.Round(totalKb / (1024.0 * 1024.0), 2);
                p.AvailableRamGb = Math.Round(freeKb / (1024.0 * 1024.0), 2);
            });

            QueryWmiSafe("SELECT Name, DriverVersion FROM Win32_VideoController", mo =>
            {
                string name = (Convert.ToString(mo["Name"]) ?? "").Trim();
                string ver = (Convert.ToString(mo["DriverVersion"]) ?? "").Trim();
                if (!string.IsNullOrEmpty(name))
                {
                    p.Gpus.Add(string.IsNullOrEmpty(ver) ? name : string.Format("{0} (Driver {1})", name, ver));
                }
            });

            QueryWmiSafe("SELECT Model, Size FROM Win32_DiskDrive", mo =>
            {
                string model = (Convert.ToString(mo["Model"]) ?? "").Trim();
                double bytes = Convert.ToDouble(mo["Size"] ?? 0, CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(model))
                {
                    double gb = Math.Round(bytes / (1024.0 * 1024.0 * 1024.0), 1);
                    p.StorageDrives.Add(gb > 0 ? string.Format("{0} ({1:0.0} GB)", model, gb) : model);
                }
            });

            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    {
                        p.NetworkAdapters.Add(string.Format("{0} [{1}]", nic.Description, nic.OperationalStatus));
                    }
                }
            }
            catch
            {
            }

            return p;
        }

        public static string FormatMarkdownReport(SystemProfile profile, UpdateScanSummary summary)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Driver+ Hardware & Driver Telemetry Report");
            sb.AppendLine();
            sb.AppendLine("- **Generated (UTC)**: " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            if (profile != null)
            {
                sb.AppendLine("- **Machine**: " + profile.ComputerName + " (" + profile.Manufacturer + " " + profile.Model + ")");
                sb.AppendLine("- **Operating System**: " + profile.OsCaption + " · " + profile.Architecture);
                sb.AppendLine("- **Processor**: " + profile.CpuName + string.Format(" ({0} Cores / {1} Threads)", profile.CpuCores, profile.CpuLogicalProcessors));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "- **Memory**: {0:0.00} GB Total ({1:0.00} GB Available)", profile.TotalRamGb, profile.AvailableRamGb));
                sb.AppendLine("- **BIOS**: " + profile.BiosVersion);
                if (profile.Gpus.Count > 0)
                {
                    sb.AppendLine("- **Graphics**: " + string.Join(" | ", profile.Gpus.ToArray()));
                }
                if (profile.StorageDrives.Count > 0)
                {
                    sb.AppendLine("- **Storage**: " + string.Join(" | ", profile.StorageDrives.ToArray()));
                }
            }

            if (summary != null)
            {
                sb.AppendLine();
                sb.AppendLine("## Scan Summary");
                sb.AppendLine(string.Format("- **Present Devices**: {0}", summary.TotalPresentDevices));
                sb.AppendLine(string.Format("- **Updates Available (WHQL)**: {0}", summary.OutdatedCount));
                sb.AppendLine(string.Format("- **Faulty Devices**: {0}", summary.FaultyDeviceCount));
                sb.AppendLine(string.Format("- **Unplugged Ghost Devices**: {0}", summary.GhostDeviceCount));
                sb.AppendLine(string.Format("- **HVCI Memory Integrity Blockers**: {0}", summary.HvciBlockerCount));

                if (summary.Devices != null && summary.OutdatedCount > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("## Recommended Official WHQL Updates");
                    foreach (HardwareDevice d in summary.Devices)
                    {
                        if (d.HasUpdate && d.RecommendedUpdate != null)
                        {
                            sb.AppendLine(string.Format(
                                "- **{0}** (`{1}`): `{2}` -> **`{3}`** ({4}, {5})",
                                d.DeviceName,
                                d.PrimaryHardwareId,
                                string.IsNullOrEmpty(d.DriverVersion) ? "None" : d.DriverVersion,
                                d.RecommendedUpdate.TargetVersion,
                                d.RecommendedUpdate.TargetDate,
                                d.RecommendedUpdate.Source));
                        }
                    }
                }
            }
            return sb.ToString();
        }

        private static void QueryWmiSafe(string query, Action<ManagementObject> onRow)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(query))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementBaseObject baseObj in results)
                    {
                        var mo = baseObj as ManagementObject;
                        if (mo != null) onRow(mo);
                    }
                }
            }
            catch
            {
            }
        }
    }
}
