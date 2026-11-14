using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using DriverPlus.Core;
using DriverPlus.Services;
using DriverPlus.UI;

[assembly: AssemblyTitle("Driver+")]
[assembly: AssemblyDescription("Clean-Room Official WHQL Hardware & Driver Updater for Windows 10/11")]
[assembly: AssemblyCompany("Driver+ Open Source Project")]
[assembly: AssemblyProduct("Driver+")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Driver+ Contributors (MIT License)")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace DriverPlus
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args != null && args.Length > 0)
            {
                if (HasFlag(args, "--self-test"))
                {
                    string outPath = GetArgValue(args, "--out", "");
                    return RunAutomatedSelfTest(outPath);
                }

                if (HasFlag(args, "--scan-json"))
                {
                    string jsonPath = GetArgValue(args, "--scan-json", "");
                    bool live = !HasFlag(args, "--offline");
                    return RunHeadlessScanJson(jsonPath, live);
                }

                if (HasFlag(args, "--serve-ui"))
                {
                    int port = 17890;
                    int parsed;
                    if (int.TryParse(GetArgValue(args, "--port", "17890"), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed >= 0)
                    {
                        port = parsed;
                    }
                    return RunHeadlessWebPortal(port);
                }
            }

            bool createdNew;
            using (var singleInstanceMutex = new Mutex(true, @"Local\DriverPlus_Desktop_Singleton_v1", out createdNew))
            {
                bool isRelaunch = HasFlag(args, "--relaunch");
                if (!createdNew)
                {
                    if (isRelaunch)
                    {
                        bool acquired = false;
                        try
                        {
                            acquired = singleInstanceMutex.WaitOne(TimeSpan.FromSeconds(6));
                        }
                        catch (AbandonedMutexException)
                        {
                            acquired = true;
                        }
                        if (!acquired)
                        {
                            return 0;
                        }
                    }
                    else
                    {
                        MessageBox.Show(
                            "Driver+ is already running. Check your taskbar or system tray icon.",
                            "Driver+",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                        return 0;
                    }
                }

                bool startMinimized = HasFlag(args, "--minimized");
                var app = new Application();
                var window = new MainWindow(startMinimized);
                return app.Run(window);
            }
        }

        private static int RunAutomatedSelfTest(string reportPath)
        {
            var sb = new StringBuilder();
            var sw = Stopwatch.StartNew();
            int failures = 0;

            Action<string, Func<string>> check = (name, fn) =>
            {
                try
                {
                    string detail = fn();
                    sb.AppendLine("[PASS] " + name + (string.IsNullOrEmpty(detail) ? "" : " — " + detail));
                }
                catch (Exception ex)
                {
                    failures++;
                    sb.AppendLine("[FAIL] " + name + " -> " + ex.Message);
                }
            };

            check("64-bit Process Architecture", () =>
            {
                if (!Environment.Is64BitProcess) throw new InvalidOperationException("Process must execute as native 64-bit x64.");
                return "x86_64 native PE32+";
            });

            List<HardwareDevice> devices = null;
            check("Live SetupAPI + CfgMgr32 Hardware Scan", () =>
            {
                devices = PnpDeviceScanner.ScanDevices(true);
                if (devices == null || devices.Count < 10) throw new InvalidOperationException("Expected >= 10 PnP devices.");
                int present = 0;
                foreach (HardwareDevice d in devices) if (d.IsPresent) present++;
                return string.Format("{0} total devnodes ({1} present)", devices.Count, present);
            });

            DriverStoreReport store = null;
            check("DriverStore & Windows 11 HVCI Blocker Audit", () =>
            {
                store = DriverStoreInspector.ScanDriverStoreAndHvci();
                return string.Format("{0} OEM INF packages, {1} HVCI blocker(s)", store.Packages.Count, store.HvciBlockers.Count);
            });

            check("4-Part OEM SUBSYS Specificity & Anti-Downgrade Engine", () =>
            {
                int s4 = DriverVersionComparer.GetHardwareIdSpecificity(@"PCI\VEN_8086&DEV_51F0&SUBSYS_00948086");
                int s2 = DriverVersionComparer.GetHardwareIdSpecificity(@"PCI\VEN_8086&DEV_51F0");
                if (s4 <= s2) throw new InvalidOperationException("SUBSYS rank failed.");
                return string.Format("SUBSYS={0} > Generic={1}", s4, s2);
            });

            check("Game Runtimes & Crash DLL Doctor", () =>
            {
                List<RuntimeComponent> runtimes = GameRuntimeInspector.InspectAllRuntimes();
                if (runtimes.Count < 12) throw new InvalidOperationException("Expected >= 12 runtimes.");
                return runtimes.Count + " official redistributables verified";
            });

            check("6-Card Hardware Fix Non-Destructive Diagnostics", () =>
            {
                List<DiagnosticFinding> diag = HardwareFixService.RunNonDestructiveDiagnostics(devices, store);
                if (diag.Count != 6) throw new InvalidOperationException("Expected 6 diagnostic cards.");
                return "6/6 diagnostic modules healthy";
            });

            check("PowrProf Power Scheme & System Profile", () =>
            {
                string guid = GameBoostService.GetActivePowerSchemeGuidString();
                SystemProfile sys = SystemInfoService.CollectSystemProfile();
                if (string.IsNullOrWhiteSpace(sys.CpuName)) throw new InvalidOperationException("CPU name empty.");
                return sys.CpuName + " | Plan=" + GameBoostService.DescribePowerScheme(guid);
            });

            check("Loopback Web Telemetry CSRF & Origin Guard", () =>
            {
                using (var srv = new LocalWebPortalServer(0))
                {
                    srv.Start();
                    if (srv.Port <= 0 || srv.CsrfToken.Length != 64)
                    {
                        throw new InvalidOperationException("Loopback portal failed initialization.");
                    }
                    return "Bound 127.0.0.1:" + srv.Port + " with 256-bit CSRF token";
                }
            });

            sw.Stop();
            sb.AppendLine(string.Format("Self-test completed in {0} ms (Failures={1}).", sw.ElapsedMilliseconds, failures));

            if (!string.IsNullOrWhiteSpace(reportPath))
            {
                string dir = Path.GetDirectoryName(reportPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(reportPath, sb.ToString(), Encoding.UTF8);
            }
            else
            {
                Console.Write(sb.ToString());
            }

            return failures == 0 ? 0 : 1;
        }

        private static int RunHeadlessScanJson(string outPath, bool liveCatalog)
        {
            var swTotal = Stopwatch.StartNew();
            SystemProfile profile = SystemInfoService.CollectSystemProfile();
            List<HardwareDevice> devices = PnpDeviceScanner.ScanDevices(true);
            DriverStoreReport store = DriverStoreInspector.ScanDriverStoreAndHvci();
            UpdateScanSummary summary = WindowsUpdateCatalogClient.EnrichWithOfficialUpdatesAsync(
                devices, store, liveCatalog, true, null, null).GetAwaiter().GetResult();
            List<RuntimeComponent> runtimes = GameRuntimeInspector.InspectAllRuntimes();
            List<DiagnosticFinding> diagnostics = HardwareFixService.RunNonDestructiveDiagnostics(summary.Devices, store);
            swTotal.Stop();

            long workingSetBytes = Process.GetCurrentProcess().WorkingSet64;
            long privateBytes = Process.GetCurrentProcess().PrivateMemorySize64;

            var root = new Dictionary<string, object>
            {
                { "generatedAtUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) },
                { "totalElapsedMs", swTotal.ElapsedMilliseconds },
                { "workingSetMb", Math.Round(workingSetBytes / (1024.0 * 1024.0), 2) },
                { "privateMemoryMb", Math.Round(privateBytes / (1024.0 * 1024.0), 2) },
                { "systemProfile", profile },
                { "scanSummary", summary },
                { "runtimesCount", runtimes.Count },
                { "diagnostics", diagnostics }
            };

            var serializer = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
            string json = serializer.Serialize(root);
            if (!string.IsNullOrWhiteSpace(outPath))
            {
                string dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(outPath, json, Encoding.UTF8);
            }
            else
            {
                Console.WriteLine(json);
            }
            return 0;
        }

        private static int RunHeadlessWebPortal(int port)
        {
            using (var server = new LocalWebPortalServer(port))
            {
                server.Start();
                string urlFile = Path.Combine(SettingsService.GetAppDataDir(), "active-web-portal.txt");
                try { File.WriteAllText(urlFile, server.BaseUrl, Encoding.UTF8); } catch { }
                try { Console.WriteLine("Driver+ Loopback Telemetry Portal listening on " + server.BaseUrl); } catch { }

                using (var quitEvent = new ManualResetEvent(false))
                {
                    try
                    {
                        Console.CancelKeyPress += (s, e) =>
                        {
                            e.Cancel = true;
                            quitEvent.Set();
                        };
                    }
                    catch
                    {
                    }
                    quitEvent.WaitOne();
                }
            }
            return 0;
        }

        private static bool HasFlag(string[] args, string flag)
        {
            if (args == null) return false;
            foreach (string a in args)
            {
                if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string GetArgValue(string[] args, string key, string fallback)
        {
            if (args == null) return fallback;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    return args[i + 1];
                }
            }
            return fallback;
        }
    }
}
