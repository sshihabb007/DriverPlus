using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using DriverPlus.Core;

namespace DriverPlus.Services
{
    public static class DriverStoreInspector
    {
        // Known Windows 11 HVCI / Core Isolation Memory Integrity incompatible legacy .sys drivers
        private static readonly Dictionary<string, string> KnownHvciIncompatibleSys =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "ssdudfu.sys", "Legacy Samsung USB DFU kernel driver lacks HVCI page-section alignment / NX compliance." },
                { "ssudbus.sys", "Legacy Samsung USB Composite bus driver incompatible with Windows 11 Memory Integrity." },
                { "via_usb_ets.sys", "Legacy VIA Telecom USB ETS driver blocks Hypervisor-Protected Code Integrity." },
                { "mbtusbser.sys", "Legacy USB-to-Serial kernel driver lacks HVCI non-executable pool compliance." },
                { "ene.sys", "Legacy ENE RGB/IO kernel driver blocked by Windows Vulnerable Driver Blocklist / HVCI." },
                { "inpoutx64.sys", "Direct port I/O driver blocked under Core Isolation Memory Integrity." },
                { "winring0x64.sys", "Legacy Ring-0 hardware monitor driver incompatible with HVCI." },
                { "winring0.sys", "Legacy Ring-0 hardware monitor driver incompatible with HVCI." },
                { "rtcore64.sys", "Legacy low-level hardware access driver blocked under HVCI." },
                { "asio.sys", "Legacy ASUS IO driver incompatible with Windows 11 Core Isolation." },
                { "asio64.sys", "Legacy ASUS IO 64-bit driver incompatible with Windows 11 Core Isolation." },
                { "gdrv.sys", "Legacy Gigabyte utility driver blocked under HVCI." },
                { "alsysio64.sys", "Legacy CoreTemp I/O driver incompatible with HVCI." },
                { "ftdibus.sys", "Check FTDI CDM driver version: pre-2.12.36.4 builds block HVCI Memory Integrity." }
            };

        public static DriverStoreReport ScanDriverStoreAndHvci()
        {
            var report = new DriverStoreReport
            {
                HvciEnabledInRegistry = ReadHvciRegistryState()
            };

            string pnpUtilOutput = RunPnpUtilEnumDrivers();
            List<DriverStorePackage> packages = ParsePnpUtilOutput(pnpUtilOutput);
            MapFileRepositoryFolders(packages);

            foreach (DriverStorePackage pkg in packages)
            {
                EvaluateHvciCompatibility(pkg);
                report.Packages.Add(pkg);
                if (pkg.FlagsHvciIncompatible)
                {
                    report.HvciBlockers.Add(pkg);
                }
            }

            return report;
        }

        public static List<DriverStorePackage> ParsePnpUtilOutput(string output)
        {
            var list = new List<DriverStorePackage>();
            if (string.IsNullOrWhiteSpace(output))
            {
                return list;
            }

            DriverStorePackage current = null;
            string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string rawLine in lines)
            {
                int colon = rawLine.IndexOf(':');
                if (colon <= 0) continue;

                string key = rawLine.Substring(0, colon).Trim();
                string val = rawLine.Substring(colon + 1).Trim();
                if (string.IsNullOrEmpty(val)) continue;

                if (key.Equals("Published Name", StringComparison.OrdinalIgnoreCase))
                {
                    if (current != null && !string.IsNullOrEmpty(current.PublishedName))
                    {
                        list.Add(current);
                    }
                    current = new DriverStorePackage { PublishedName = val };
                }
                else if (current != null)
                {
                    if (key.Equals("Original Name", StringComparison.OrdinalIgnoreCase))
                    {
                        current.OriginalInfName = val;
                    }
                    else if (key.Equals("Provider Name", StringComparison.OrdinalIgnoreCase))
                    {
                        current.ProviderName = val;
                    }
                    else if (key.Equals("Class Name", StringComparison.OrdinalIgnoreCase))
                    {
                        current.ClassName = val;
                    }
                    else if (key.Equals("Signer Name", StringComparison.OrdinalIgnoreCase))
                    {
                        current.SignerName = val;
                    }
                    else if (key.Equals("Driver Version", StringComparison.OrdinalIgnoreCase))
                    {
                        // Format: "05/19/2021 2.17.16.0"
                        string[] parts = val.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2)
                        {
                            current.DriverDate = parts[0];
                            current.DriverVersion = parts[1];
                        }
                        else
                        {
                            current.DriverVersion = val;
                        }
                    }
                }
            }

            if (current != null && !string.IsNullOrEmpty(current.PublishedName))
            {
                list.Add(current);
            }

            return list;
        }

        public static void EvaluateHvciCompatibility(DriverStorePackage pkg)
        {
            if (pkg == null) return;
            pkg.FlagsHvciIncompatible = false;
            pkg.HvciReason = "";

            foreach (string sys in pkg.SysFiles)
            {
                string fileName = Path.GetFileName(sys);
                if (string.IsNullOrEmpty(fileName)) continue;

                string reason;
                if (KnownHvciIncompatibleSys.TryGetValue(fileName, out reason))
                {
                    // Special version check for FTDI: 2.12.36.4+ fixed HVCI compatibility
                    if (fileName.Equals("ftdibus.sys", StringComparison.OrdinalIgnoreCase) &&
                        DriverVersionComparer.CompareVersions("2.12.36.4", pkg.DriverVersion) <= 0)
                    {
                        continue;
                    }

                    pkg.FlagsHvciIncompatible = true;
                    pkg.HvciReason = fileName + ": " + reason;
                    return;
                }
            }
        }

        private static bool ReadHvciRegistryState()
        {
            try
            {
                using (RegistryKey rk = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", false))
                {
                    if (rk != null)
                    {
                        object v = rk.GetValue("Enabled");
                        if (v != null && Convert.ToInt32(v) == 1)
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        private static string RunPnpUtilEnumDrivers()
        {
            try
            {
                string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string pnpUtil = Path.Combine(sysDir, "pnputil.exe");
                if (!File.Exists(pnpUtil))
                {
                    return "";
                }

                var psi = new ProcessStartInfo
                {
                    FileName = pnpUtil,
                    Arguments = "/enum-drivers",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (Process p = Process.Start(psi))
                {
                    if (p == null) return "";
                    System.Threading.Tasks.Task<string> outTask = p.StandardOutput.ReadToEndAsync();
                    System.Threading.Tasks.Task<string> errTask = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(8000))
                    {
                        try { p.Kill(); } catch { }
                        return "";
                    }
                    System.Threading.Tasks.Task.WaitAll(new System.Threading.Tasks.Task[] { outTask, errTask }, 2000);
                    return outTask.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? outTask.Result : "";
                }
            }
            catch
            {
                return "";
            }
        }

        private static void MapFileRepositoryFolders(List<DriverStorePackage> packages)
        {
            try
            {
                string repoDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    @"DriverStore\FileRepository");
                if (!Directory.Exists(repoDir))
                {
                    return;
                }

                string[] dirs = Directory.GetDirectories(repoDir);
                var byPrefix = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (string d in dirs)
                {
                    string folderName = Path.GetFileName(d);
                    int idx = folderName.IndexOf(".inf_", StringComparison.OrdinalIgnoreCase);
                    if (idx > 0)
                    {
                        string infName = folderName.Substring(0, idx + 4);
                        List<string> bucket;
                        if (!byPrefix.TryGetValue(infName, out bucket))
                        {
                            bucket = new List<string>();
                            byPrefix[infName] = bucket;
                        }
                        bucket.Add(d);
                    }
                }

                foreach (DriverStorePackage pkg in packages)
                {
                    if (string.IsNullOrEmpty(pkg.OriginalInfName)) continue;
                    List<string> matches;
                    if (byPrefix.TryGetValue(pkg.OriginalInfName, out matches) && matches.Count > 0)
                    {
                        string chosen = matches[matches.Count - 1];
                        pkg.RepositoryFolder = chosen;
                        try
                        {
                            string[] sysFiles = Directory.GetFiles(chosen, "*.sys", SearchOption.AllDirectories);
                            foreach (string sf in sysFiles)
                            {
                                pkg.SysFiles.Add(Path.GetFileName(sf));
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }
        }
    }
}
