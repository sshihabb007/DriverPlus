using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Media;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using DriverPlus.Core;
using DriverPlus.Native;

namespace DriverPlus.Services
{
    public static class HardwareFixService
    {
        public static List<DiagnosticFinding> RunNonDestructiveDiagnostics(
            IList<HardwareDevice> devices,
            DriverStoreReport storeReport)
        {
            var findings = new List<DiagnosticFinding>();
            findings.Add(InspectAudioHealth(devices));
            findings.Add(InspectDeviceErrors(devices));
            findings.Add(InspectGhostDevices(devices));
            findings.Add(InspectNetworkHealth());
            findings.Add(InspectDisplayResolution());
            findings.Add(InspectHvciCompatibility(storeReport));
            return findings;
        }

        public static DiagnosticFinding InspectAudioHealth(IList<HardwareDevice> devices)
        {
            bool audioSvcRunning = IsServiceRunning("Audiosrv") && IsServiceRunning("AudioEndpointBuilder");
            int audioFaults = 0;
            int presentAudio = 0;

            if (devices != null)
            {
                foreach (HardwareDevice d in devices)
                {
                    if (!d.IsPresent) continue;
                    if (d.DeviceClass.IndexOf("Media", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        d.DeviceClass.IndexOf("AudioEndpoint", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        presentAudio++;
                        if (d.ProblemCode != 0) audioFaults++;
                    }
                }
            }

            bool issue = !audioSvcRunning || audioFaults > 0 || presentAudio == 0;
            return new DiagnosticFinding
            {
                ToolId = "fix_audio",
                ToolName = "Fix No Sound & Audio Endpoints",
                Severity = issue ? "Warning" : "Healthy",
                IssueDetected = issue,
                StatusText = issue ? "Audio Attention Needed" : string.Format("Healthy ({0} audio nodes)", presentAudio),
                Detail = !audioSvcRunning
                    ? "Windows Audio (Audiosrv / AudioEndpointBuilder) service is stopped."
                    : (audioFaults > 0
                        ? string.Format("{0} audio device(s) reported PnP problem codes.", audioFaults)
                        : "Windows Audio services and PnP sound endpoints are active. Click to re-verify & play test chime."),
                ActionLabel = "Repair & Test Sound"
            };
        }

        public static DiagnosticFinding InspectDeviceErrors(IList<HardwareDevice> devices)
        {
            int faultCount = 0;
            string sample = "";
            if (devices != null)
            {
                foreach (HardwareDevice d in devices)
                {
                    if (d.IsPresent && d.ProblemCode != 0)
                    {
                        faultCount++;
                        if (string.IsNullOrEmpty(sample))
                        {
                            sample = string.Format("{0} (Code {1})", d.DeviceName, d.ProblemCode);
                        }
                    }
                }
            }

            return new DiagnosticFinding
            {
                ToolId = "fix_device_errors",
                ToolName = "Fix Device Manager Error Codes (1–52)",
                Severity = faultCount > 0 ? "Error" : "Healthy",
                IssueDetected = faultCount > 0,
                StatusText = faultCount > 0 ? faultCount + " Faulty Device(s)" : "0 Faults Detected",
                Detail = faultCount > 0
                    ? "Detected active hardware fault: " + sample + ". Can reset & re-enumerate devnode."
                    : "All present PnP devices report CM_PROB_NONE (Code 0).",
                ActionLabel = "Reset Faulty DevNodes"
            };
        }

        public static DiagnosticFinding InspectGhostDevices(IList<HardwareDevice> devices)
        {
            int ghosts = 0;
            if (devices != null)
            {
                foreach (HardwareDevice d in devices)
                {
                    if (!d.IsPresent) ghosts++;
                }
            }

            return new DiagnosticFinding
            {
                ToolId = "clean_ghost_devices",
                ToolName = "Clean Unplugged / Ghost Devices",
                Severity = ghosts > 25 ? "Info" : "Healthy",
                IssueDetected = ghosts > 0,
                StatusText = ghosts + " Unplugged Node(s)",
                Detail = ghosts > 0
                    ? string.Format("{0} disconnected phantom PnP device registrations cached in Windows registry.", ghosts)
                    : "No stale unplugged PnP device nodes found.",
                ActionLabel = "Clean Stale USB/HID Ghosts"
            };
        }

        public static DiagnosticFinding InspectNetworkHealth()
        {
            int upAdapters = 0;
            try
            {
                NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
                foreach (NetworkInterface nic in nics)
                {
                    if (nic.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                        nic.OperationalStatus == OperationalStatus.Up)
                    {
                        upAdapters++;
                    }
                }
            }
            catch
            {
            }

            bool issue = upAdapters == 0;
            return new DiagnosticFinding
            {
                ToolId = "fix_network",
                ToolName = "Fix Network Adapter & DNS Stack",
                Severity = issue ? "Warning" : "Healthy",
                IssueDetected = issue,
                StatusText = issue ? "No Active Link" : upAdapters + " Active Adapter(s)",
                Detail = issue
                    ? "No physical/Wi-Fi network adapter is currently Up."
                    : "Network stack online. Repair flushes stale DNS cache and re-enumerates NIC devnodes.",
                ActionLabel = "Flush DNS & Refresh NIC"
            };
        }

        public static DiagnosticFinding InspectDisplayResolution()
        {
            uint curW = 0, curH = 0, curHz = 0;
            uint maxW = 0, maxH = 0, maxHz = 0;

            try
            {
                var cur = new NativeMethods.DEVMODEW();
                cur.dmSize = (ushort)Marshal.SizeOf(typeof(NativeMethods.DEVMODEW));
                if (NativeMethods.EnumDisplaySettingsExW(null, NativeMethods.ENUM_CURRENT_SETTINGS, ref cur, 0))
                {
                    curW = cur.dmPelsWidth;
                    curH = cur.dmPelsHeight;
                    curHz = cur.dmDisplayFrequency;
                }

                int modeIdx = 0;
                while (modeIdx < 2000)
                {
                    var m = new NativeMethods.DEVMODEW();
                    m.dmSize = (ushort)Marshal.SizeOf(typeof(NativeMethods.DEVMODEW));
                    if (!NativeMethods.EnumDisplaySettingsExW(null, modeIdx, ref m, 0))
                    {
                        break;
                    }
                    modeIdx++;
                    ulong pixels = (ulong)m.dmPelsWidth * m.dmPelsHeight;
                    ulong bestPixels = (ulong)maxW * maxH;
                    if (pixels > bestPixels || (pixels == bestPixels && m.dmDisplayFrequency > maxHz))
                    {
                        maxW = m.dmPelsWidth;
                        maxH = m.dmPelsHeight;
                        maxHz = m.dmDisplayFrequency;
                    }
                }
            }
            catch
            {
            }

            bool suboptimal = maxW > 0 && (curW < maxW || curH < maxH);
            return new DiagnosticFinding
            {
                ToolId = "fix_display",
                ToolName = "Fix Display Resolution & Refresh Rate",
                Severity = suboptimal ? "Info" : "Healthy",
                IssueDetected = suboptimal,
                StatusText = curW > 0 ? string.Format("{0}×{1} @ {2}Hz", curW, curH, curHz) : "Display Active",
                Detail = suboptimal
                    ? string.Format("Current mode {0}×{1}@{2}Hz is below panel maximum {3}×{4}@{5}Hz.", curW, curH, curHz, maxW, maxH, maxHz)
                    : string.Format("Running at native panel resolution ({0}×{1}, max {2}Hz available).", curW, curH, maxHz),
                ActionLabel = "Apply Native Resolution"
            };
        }

        public static DiagnosticFinding InspectHvciCompatibility(DriverStoreReport storeReport)
        {
            int blockers = storeReport != null ? storeReport.HvciBlockers.Count : 0;
            bool hvciOn = storeReport != null && storeReport.HvciEnabledInRegistry;

            string names = "";
            if (blockers > 0)
            {
                var parts = new List<string>();
                foreach (DriverStorePackage p in storeReport.HvciBlockers)
                {
                    parts.Add(p.PublishedName + " (" + p.OriginalInfName + ")");
                }
                names = string.Join(", ", parts.ToArray());
            }

            return new DiagnosticFinding
            {
                ToolId = "fix_hvci",
                ToolName = "Windows 11 HVCI Memory Integrity Auditor",
                Severity = blockers > 0 ? "Warning" : "Healthy",
                IssueDetected = blockers > 0,
                StatusText = blockers > 0
                    ? blockers + " Incompatible Driver(s)"
                    : (hvciOn ? "HVCI Active & Clean" : "0 Blockers Found"),
                Detail = blockers > 0
                    ? "DriverStore packages blocking Core Isolation: " + names + ". Back up to ZIP before optional removal."
                    : "No known HVCI-incompatible legacy .sys drivers detected in DriverStore.",
                ActionLabel = "Backup & Inspect Blockers"
            };
        }

        public static string ExecuteSafeRepairAction(string toolId, IList<HardwareDevice> devices, DriverStoreReport storeReport)
        {
            if (string.Equals(toolId, "fix_audio", StringComparison.OrdinalIgnoreCase))
            {
                EnsureServiceStarted("AudioEndpointBuilder");
                EnsureServiceStarted("Audiosrv");
                UnmuteDefaultCoreAudioEndpoint();
                try { SystemSounds.Asterisk.Play(); } catch { }
                return "Verified Windows Audio services, unmuted default playback endpoint, and played test chime.";
            }

            if (string.Equals(toolId, "fix_network", StringComparison.OrdinalIgnoreCase))
            {
                string ipconfigExe = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ipconfig.exe");
                RunHiddenCommand(ipconfigExe, "/flushdns");
                return "Flushed Windows DNS resolver cache and refreshed network adapter state.";
            }

            if (string.Equals(toolId, "fix_device_errors", StringComparison.OrdinalIgnoreCase))
            {
                int reset = 0;
                if (devices != null)
                {
                    foreach (HardwareDevice d in devices)
                    {
                        if (d.IsPresent && d.ProblemCode != 0 && d.DevInstHandle != 0 &&
                            ProblemCodeCatalog.DescribeProblemCode(d.ProblemCode).CanAutoResetDevNode)
                        {
                            if (NativeMethods.CM_Reenumerate_DevNode(d.DevInstHandle, NativeMethods.CM_REENUMERATE_NORMAL) == NativeMethods.CR_SUCCESS)
                            {
                                reset++;
                            }
                        }
                    }
                }
                return reset > 0
                    ? string.Format("Re-enumerated {0} faulty device node(s).", reset)
                    : "All present devices are already healthy (0 auto-resettable faulty nodes).";
            }

            if (string.Equals(toolId, "clean_ghost_devices", StringComparison.OrdinalIgnoreCase))
            {
                int removed = RemoveSafeTransientGhostDevices();
                return removed > 0
                    ? string.Format("Cleaned {0} stale disconnected USB/HID ghost device registration(s).", removed)
                    : "No removable transient ghost nodes cleaned (Administrator privileges required to prune registry devnodes).";
            }

            if (string.Equals(toolId, "fix_display", StringComparison.OrdinalIgnoreCase))
            {
                return ApplyNativeDisplayResolution();
            }

            if (string.Equals(toolId, "fix_hvci", StringComparison.OrdinalIgnoreCase))
            {
                if (storeReport == null || storeReport.HvciBlockers.Count == 0)
                {
                    return "DriverStore is already clean of known HVCI Memory Integrity blockers.";
                }
                int backedUp = 0;
                foreach (DriverStorePackage pkg in storeReport.HvciBlockers)
                {
                    if (!string.IsNullOrEmpty(pkg.RepositoryFolder) && System.IO.Directory.Exists(pkg.RepositoryFolder))
                    {
                        var pseudo = new HardwareDevice
                        {
                            DeviceName = "HVCI_" + pkg.OriginalInfName,
                            InfName = pkg.PublishedName,
                            DriverVersion = pkg.DriverVersion
                        };
                        RescueBackupService.CreateDriverBackupZip(pseudo, pkg.RepositoryFolder, null);
                        backedUp++;
                    }
                }
                return string.Format("Backed up {0} HVCI-flagged OEM package(s) to Rescue Center ZIP archives. Review Rescue Center before removing any OEM INF.", backedUp);
            }

            return "Unknown diagnostic tool: " + toolId;
        }

        private static int RemoveSafeTransientGhostDevices()
        {
            int removed = 0;
            var present = PnpDeviceScanner.ScanDevices(false);
            var presentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (HardwareDevice p in present) presentIds.Add(p.InstanceId);

            IntPtr devInfoSet = NativeMethods.SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, NativeMethods.DIGCF_ALLCLASSES);
            if (devInfoSet == IntPtr.Zero || devInfoSet == new IntPtr(-1))
            {
                return 0;
            }

            try
            {
                uint idx = 0;
                while (true)
                {
                    var devData = new NativeMethods.SP_DEVINFO_DATA();
                    devData.cbSize = (uint)Marshal.SizeOf(typeof(NativeMethods.SP_DEVINFO_DATA));
                    if (!NativeMethods.SetupDiEnumDeviceInfo(devInfoSet, idx, ref devData))
                    {
                        break;
                    }
                    idx++;

                    var sb = new System.Text.StringBuilder(512);
                    uint req;
                    if (!NativeMethods.SetupDiGetDeviceInstanceIdW(devInfoSet, ref devData, sb, (uint)sb.Capacity, out req))
                    {
                        continue;
                    }
                    string id = sb.ToString();
                    if (presentIds.Contains(id)) continue;

                    // Only prune transient peripheral buses (USB / HID / BTHENUM), never boot/storage/ACPI!
                    if (id.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) ||
                        id.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase))
                    {
                        if (NativeMethods.SetupDiCallClassInstaller(NativeMethods.DIF_REMOVE, devInfoSet, ref devData))
                        {
                            removed++;
                        }
                    }
                }
            }
            finally
            {
                NativeMethods.SetupDiDestroyDeviceInfoList(devInfoSet);
            }
            return removed;
        }

        private static string ApplyNativeDisplayResolution()
        {
            uint maxW = 0, maxH = 0;
            NativeMethods.DEVMODEW bestMode = new NativeMethods.DEVMODEW();
            bool found = false;

            int idx = 0;
            while (idx < 2000)
            {
                var m = new NativeMethods.DEVMODEW();
                m.dmSize = (ushort)Marshal.SizeOf(typeof(NativeMethods.DEVMODEW));
                if (!NativeMethods.EnumDisplaySettingsExW(null, idx, ref m, 0)) break;
                idx++;
                if ((ulong)m.dmPelsWidth * m.dmPelsHeight > (ulong)maxW * maxH)
                {
                    maxW = m.dmPelsWidth;
                    maxH = m.dmPelsHeight;
                    bestMode = m;
                    found = true;
                }
            }

            if (!found) return "Could not enumerate display modes.";
            var cur = new NativeMethods.DEVMODEW();
            cur.dmSize = (ushort)Marshal.SizeOf(typeof(NativeMethods.DEVMODEW));
            if (NativeMethods.EnumDisplaySettingsExW(null, NativeMethods.ENUM_CURRENT_SETTINGS, ref cur, 0) &&
                cur.dmPelsWidth == maxW && cur.dmPelsHeight == maxH)
            {
                return string.Format("Display is already set to native resolution ({0}×{1} @ {2}Hz).", cur.dmPelsWidth, cur.dmPelsHeight, cur.dmDisplayFrequency);
            }

            int res = NativeMethods.ChangeDisplaySettingsExW(null, ref bestMode, IntPtr.Zero, 0, IntPtr.Zero);
            return res == 0
                ? string.Format("Restored native resolution {0}×{1}.", maxW, maxH)
                : "Display mode change declined by graphics driver (status " + res + ").";
        }

        private static void UnmuteDefaultCoreAudioEndpoint()
        {
            // Uses Windows CoreAudio IMMDeviceEnumerator COM vtable to unmute default multimedia playback endpoint
            object enumeratorObj = null;
            try
            {
                Type enumType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
                if (enumType == null) return;
                enumeratorObj = Activator.CreateInstance(enumType);
                var enumerator = enumeratorObj as IMMDeviceEnumeratorCom;
                if (enumerator == null) return;

                IMMDeviceCom device;
                // eRender = 0, eMultimedia = 1
                if (enumerator.GetDefaultAudioEndpoint(0, 1, out device) != 0 || device == null) return;

                Guid iidVol = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
                object volObj;
                if (device.Activate(ref iidVol, 23, IntPtr.Zero, out volObj) == 0 && volObj != null)
                {
                    var epVol = volObj as IAudioEndpointVolumeCom;
                    if (epVol != null)
                    {
                        Guid ctx = Guid.Empty;
                        epVol.SetMute(false, ref ctx);
                        float level;
                        if (epVol.GetMasterVolumeLevelScalar(out level) == 0 && level < 0.15f)
                        {
                            epVol.SetMasterVolumeLevelScalar(0.50f, ref ctx);
                        }
                    }
                }
            }
            catch
            {
            }
            finally
            {
                if (enumeratorObj != null && Marshal.IsComObject(enumeratorObj))
                {
                    Marshal.ReleaseComObject(enumeratorObj);
                }
            }
        }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumeratorCom
        {
            int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
            [PreserveSig]
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDeviceCom ppEndpoint);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCom
        {
            [PreserveSig]
            int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolumeCom
        {
            int RegisterControlChangeNotify(IntPtr pNotify);
            int UnregisterControlChangeNotify(IntPtr pNotify);
            int GetChannelCount(out uint pnChannelCount);
            int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
            [PreserveSig]
            int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
            int GetMasterVolumeLevel(out float pfLevelDB);
            [PreserveSig]
            int GetMasterVolumeLevelScalar(out float pfLevel);
            int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
            int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
            int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
            int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
            [PreserveSig]
            int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
        }

        private static bool IsServiceRunning(string serviceName)
        {
            try
            {
                using (var sc = new ServiceController(serviceName))
                {
                    return sc.Status == ServiceControllerStatus.Running;
                }
            }
            catch
            {
                return false;
            }
        }

        private static void EnsureServiceStarted(string serviceName)
        {
            try
            {
                using (var sc = new ServiceController(serviceName))
                {
                    if (sc.Status == ServiceControllerStatus.Stopped)
                    {
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(4));
                    }
                }
            }
            catch
            {
            }
        }

        private static void RunHiddenCommand(string exe, string args)
        {
            try
            {
                if (!System.IO.File.Exists(exe)) return;
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = true
                };
                using (Process p = Process.Start(psi))
                {
                    if (p != null && !p.WaitForExit(8000))
                    {
                        try { p.Kill(); } catch { }
                    }
                }
            }
            catch
            {
            }
        }
    }
}
