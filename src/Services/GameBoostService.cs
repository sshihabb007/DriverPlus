using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Web.Script.Serialization;
using DriverPlus.Core;
using DriverPlus.Native;

namespace DriverPlus.Services
{
    public static class GameBoostService
    {
        // Standard Windows High Performance scheme GUID
        public static readonly Guid HighPerformanceGuid = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        public static readonly Guid BalancedGuid = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");

        // Strictly curated, safe-to-pause optional background indexing/telemetry/spool services (never security/audio/network/WUA)
        private static readonly string[] SafeOptionalServices = new[]
        {
            "WSearch",      // Windows Search Indexer
            "MapsBroker",   // Downloaded Maps Manager
            "Spooler",      // Print Spooler
            "Fax",          // Windows Fax
            "RetailDemo"    // Retail Demo Service
        };

        public static string GetDefaultSnapshotFilePath()
        {
            return Path.Combine(SettingsService.GetAppDataDir(), "boost-restore-snapshot.json");
        }

        public static string GetActivePowerSchemeGuidString()
        {
            IntPtr ptr = IntPtr.Zero;
            try
            {
                uint res = NativeMethods.PowerGetActiveScheme(IntPtr.Zero, out ptr);
                if (res == 0 && ptr != IntPtr.Zero)
                {
                    Guid g = (Guid)Marshal.PtrToStructure(ptr, typeof(Guid));
                    return g.ToString("D");
                }
            }
            catch
            {
            }
            finally
            {
                if (ptr != IntPtr.Zero)
                {
                    NativeMethods.LocalFree(ptr);
                }
            }
            return BalancedGuid.ToString("D");
        }

        public static string DescribePowerScheme(string guidStr)
        {
            if (string.IsNullOrWhiteSpace(guidStr)) return "Balanced";
            if (guidStr.IndexOf("8c5e7fda", StringComparison.OrdinalIgnoreCase) >= 0) return "High Performance";
            if (guidStr.IndexOf("e9a42b02", StringComparison.OrdinalIgnoreCase) >= 0) return "Ultimate Performance";
            if (guidStr.IndexOf("a1841308", StringComparison.OrdinalIgnoreCase) >= 0) return "Power Saver";
            return "Balanced (" + guidStr.Substring(0, Math.Min(8, guidStr.Length)) + ")";
        }

        public static bool IsFullscreenGameOrD3DActive()
        {
            try
            {
                int state;
                if (NativeMethods.SHQueryUserNotificationState(out state) == 0)
                {
                    // QUNS_BUSY = 2, QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4
                    return state == 2 || state == 3 || state == 4;
                }
            }
            catch
            {
            }
            return false;
        }

        public static BoostSnapshot BuildRestoreSnapshot(string activeSchemeGuid, IList<string> stoppedServices)
        {
            var snap = new BoostSnapshot
            {
                ActivatedAtUtc = DateTime.UtcNow,
                PreviousPowerSchemeGuid = activeSchemeGuid ?? BalancedGuid.ToString("D"),
                ActiveBoostSchemeGuid = HighPerformanceGuid.ToString("D"),
                SilentFocusActive = true
            };
            if (stoppedServices != null)
            {
                foreach (string s in stoppedServices)
                {
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        snap.StoppedServices.Add(s);
                    }
                }
            }
            return snap;
        }

        public static BoostSnapshot MergeWithExistingSnapshot(
            BoostSnapshot existing,
            string currentPowerGuid,
            IList<string> newlyStoppedServices)
        {
            string basePower = (existing != null && !string.IsNullOrWhiteSpace(existing.PreviousPowerSchemeGuid))
                ? existing.PreviousPowerSchemeGuid
                : (currentPowerGuid ?? BalancedGuid.ToString("D"));

            var union = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (existing != null && existing.StoppedServices != null)
            {
                foreach (string s in existing.StoppedServices)
                {
                    if (!string.IsNullOrWhiteSpace(s) && seen.Add(s)) union.Add(s);
                }
            }
            if (newlyStoppedServices != null)
            {
                foreach (string s in newlyStoppedServices)
                {
                    if (!string.IsNullOrWhiteSpace(s) && seen.Add(s)) union.Add(s);
                }
            }

            return BuildRestoreSnapshot(basePower, union);
        }

        public static void SaveSnapshotToFile(string path, BoostSnapshot snapshot)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var serializer = new JavaScriptSerializer();
            string tempPath = path + ".tmp";
            File.WriteAllText(tempPath, serializer.Serialize(snapshot), Encoding.UTF8);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(tempPath, path);
        }

        public static BoostSnapshot LoadSnapshotFromFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }
            var serializer = new JavaScriptSerializer();
            return serializer.Deserialize<BoostSnapshot>(File.ReadAllText(path, Encoding.UTF8));
        }

        public static bool IsBoostCurrentlyActive()
        {
            return File.Exists(GetDefaultSnapshotFilePath());
        }

        public static string ActivateGameBoost(bool suspendOptionalServices)
        {
            string snapshotPath = GetDefaultSnapshotFilePath();
            string previousPower = GetActivePowerSchemeGuidString();
            var candidateStops = new List<string>();

            if (suspendOptionalServices)
            {
                foreach (string svcName in SafeOptionalServices)
                {
                    try
                    {
                        using (var sc = new ServiceController(svcName))
                        {
                            if (sc.Status == ServiceControllerStatus.Running && sc.CanStop)
                            {
                                candidateStops.Add(svcName);
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }

            // Persist restore state BEFORE mutating power plan or stopping any service (crash-safe & idempotent invariant)
            BoostSnapshot existing = LoadSnapshotFromFile(snapshotPath);
            BoostSnapshot snap = MergeWithExistingSnapshot(existing, previousPower, candidateStops);
            SaveSnapshotToFile(snapshotPath, snap);

            Guid hp = HighPerformanceGuid;
            uint pwrRes = NativeMethods.PowerSetActiveScheme(IntPtr.Zero, ref hp);

            int actuallyStopped = 0;
            foreach (string svcName in candidateStops)
            {
                try
                {
                    using (var sc = new ServiceController(svcName))
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(3));
                        actuallyStopped++;
                    }
                }
                catch
                {
                }
            }

            return string.Format(
                "Game Boost active: Power scheme -> {0}, quiesced {1} optional background service(s). Snapshot saved for 1-click restore.",
                pwrRes == 0 ? "High Performance" : DescribePowerScheme(GetActivePowerSchemeGuidString()),
                actuallyStopped);
        }

        public static string RestoreNormalMode()
        {
            string snapshotPath = GetDefaultSnapshotFilePath();
            BoostSnapshot snap = LoadSnapshotFromFile(snapshotPath);
            if (snap == null)
            {
                return "System is already in normal mode (no active Game Boost snapshot).";
            }

            Guid prevGuid;
            if (Guid.TryParse(snap.PreviousPowerSchemeGuid, out prevGuid))
            {
                NativeMethods.PowerSetActiveScheme(IntPtr.Zero, ref prevGuid);
            }

            var allowedSet = new HashSet<string>(SafeOptionalServices, StringComparer.OrdinalIgnoreCase);
            var stillStopped = new List<string>();
            int restarted = 0;
            if (snap.StoppedServices != null)
            {
                foreach (string svcName in snap.StoppedServices)
                {
                    if (!allowedSet.Contains(svcName)) continue;
                    try
                    {
                        using (var sc = new ServiceController(svcName))
                        {
                            if (sc.Status == ServiceControllerStatus.Stopped)
                            {
                                sc.Start();
                                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(3));
                                restarted++;
                            }
                        }
                    }
                    catch
                    {
                        stillStopped.Add(svcName);
                    }
                }
            }

            if (stillStopped.Count == 0)
            {
                try { File.Delete(snapshotPath); } catch { }
            }
            else
            {
                snap.StoppedServices = stillStopped;
                try { SaveSnapshotToFile(snapshotPath, snap); } catch { }
            }

            return string.Format(
                "Restored normal mode: Power scheme reverted to {0}, restarted {1} background service(s).",
                DescribePowerScheme(snap.PreviousPowerSchemeGuid),
                restarted);
        }
    }
}
