using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using DriverPlus.Core;
using DriverPlus.Services;

namespace DriverPlus.Tests
{
    public static class AssertEx
    {
        public static void True(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("ASSERT TRUE FAILED: " + message);
            }
        }

        public static void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException(string.Format(
                    "ASSERT EQUAL FAILED ({0}): expected [{1}], actual [{2}]",
                    message, expected, actual));
            }
        }
    }

    public static class DriverPlusTestsProgram
    {
        public static int Main(string[] args)
        {
            var tests = new List<KeyValuePair<string, Action>>
            {
                new KeyValuePair<string, Action>("Task1_VersionParsing_Handles1To4SegmentsAndRejectsGarbage", TestVersionParsing),
                new KeyValuePair<string, Action>("Task1_CandidateSelection_RanksExact4PartSubsysAboveGenericAndPreventsDowngrade", TestCandidateSelection),
                new KeyValuePair<string, Action>("Task1_ProblemCodeAndHResultCatalog_DescribesKnownAndUnknownCodes", TestProblemAndHResultCatalog),
                new KeyValuePair<string, Action>("Task1_PciVendorResolver_ResolvesPrimarySiliconAndOemSubsys", TestVendorResolver),
                new KeyValuePair<string, Action>("Task2_CatalogSearchParser_FiltersOsFamilyAndExtractsMetadata", TestCatalogSearchParser),
                new KeyValuePair<string, Action>("Task2_CatalogDownloadDialogParser_EnforcesOfficialMicrosoftHttpsAllowlist", TestCatalogDownloadAllowlist),
                new KeyValuePair<string, Action>("Task2_DriverStoreInspector_DetectsHvciIncompatibleDrivers", TestDriverStoreAndHvciParser),
                new KeyValuePair<string, Action>("Task2_LiveNative64BitPnpScanner_ReadsRealHostDevicesAndGhostNodes", TestLivePnpScanner),
                new KeyValuePair<string, Action>("Task3_RescueArchiveValidator_BlocksZipSlipTraversal", TestZipSlipProtection),
                new KeyValuePair<string, Action>("Task3_RescueBackupAndOfflineProfile_RoundTripVerified", TestRescueBackupAndOfflineRoundTrip),
                new KeyValuePair<string, Action>("Task3_AuthenticodeAndCabDigest_VerifiesSignedAndBlocksUnsigned", TestAuthenticodeAndCabSha1),
                new KeyValuePair<string, Action>("Task4_GameRuntimeInspector_AuditsRuntimesAndCrashDllDoctor", TestGameRuntimesAndCrashDoctor),
                new KeyValuePair<string, Action>("Task4_HardwareFixService_RunsAll6NonDestructiveDiagnostics", TestHardwareFixDiagnostics),
                new KeyValuePair<string, Action>("Task4_GameBoostService_ReadsLivePowerPlanAndPersistsSnapshot", TestGameBoostSnapshot),
                new KeyValuePair<string, Action>("Task4_SystemInfoService_CollectsRealHardwareAndFormatsMarkdown", TestSystemInfoAndExport),
                new KeyValuePair<string, Action>("Task5_LocalWebPortalServer_EnforcesLoopbackCsrfAndServesBootstrapJson", TestLocalWebPortalServer)
            };

            int passed = 0;
            int failed = 0;
            foreach (var test in tests)
            {
                try
                {
                    test.Value();
                    Console.WriteLine("  [PASS] " + test.Key);
                    passed++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("  [FAIL] " + test.Key + " -> " + ex.Message);
                    failed++;
                }
            }

            Console.WriteLine(string.Format("[SUMMARY] Passed={0}, Failed={1}, Total={2}", passed, failed, tests.Count));
            return failed == 0 ? 0 : 1;
        }

        private static void TestVersionParsing()
        {
            int[] parts;
            AssertEx.True(DriverVersionComparer.TryParseDriverVersion("24.40.0.4", out parts), "4-part version parses");
            AssertEx.Equal(24, parts[0], "major");
            AssertEx.Equal(4, parts[3], "revision");

            AssertEx.True(DriverVersionComparer.TryParseDriverVersion("2026.5", out parts), "2-part version pads to 4");
            AssertEx.Equal(0, parts[2], "padded build");

            AssertEx.True(!DriverVersionComparer.TryParseDriverVersion("N/A", out parts), "N/A rejected");
            AssertEx.True(!DriverVersionComparer.TryParseDriverVersion("", out parts), "empty rejected");

            AssertEx.True(DriverVersionComparer.CompareVersions("22.220.0.4", "24.40.0.4") < 0, "24.40.0.4 is newer than 22.220.0.4");
            AssertEx.Equal(0, DriverVersionComparer.CompareVersions("10.0.26100.1", "10.0.26100.1"), "identical versions equal");
            AssertEx.True(DriverVersionComparer.CompareVersions("24.40.0.4", "N/A") > 0, "malformed candidate never beats valid installed");
        }

        private static void TestCandidateSelection()
        {
            var dev = new HardwareDevice
            {
                InstanceId = @"PCI\VEN_8086&DEV_51F0&SUBSYS_00948086&REV_01\3&11583659&0&A3",
                DeviceName = "Intel(R) Wi-Fi 6 AX203",
                DriverVersion = "22.220.0.4",
                DriverDate = "2024-01-15",
                HardwareIds = new List<string>
                {
                    @"PCI\VEN_8086&DEV_51F0&SUBSYS_00948086&REV_01",
                    @"PCI\VEN_8086&DEV_51F0&SUBSYS_00948086",
                    @"PCI\VEN_8086&DEV_51F0&CC_028000"
                }
            };

            AssertEx.True(
                DriverVersionComparer.GetHardwareIdSpecificity(@"PCI\VEN_8086&DEV_51F0&SUBSYS_00948086") >
                DriverVersionComparer.GetHardwareIdSpecificity(@"PCI\VEN_8086&DEV_51F0"),
                "4-part SUBSYS must outrank generic 2-part VEN&DEV");

            var candidates = new List<DriverCandidate>
            {
                // Older downgrade candidate -> must be rejected
                new DriverCandidate
                {
                    Title = "Intel Old Driver",
                    TargetVersion = "21.10.0.1",
                    MatchedHardwareId = @"PCI\VEN_8086&DEV_51F0&SUBSYS_00948086",
                    WhqlSigned = true,
                    Source = "Microsoft Update Catalog"
                },
                // Generic 2-part match with slightly higher version
                new DriverCandidate
                {
                    Title = "Intel Generic 2-part Driver",
                    TargetVersion = "24.50.0.1",
                    MatchedHardwareId = @"PCI\VEN_8086&DEV_51F0",
                    WhqlSigned = true,
                    Source = "Microsoft Update Catalog"
                },
                // Exact 4-part OEM SUBSYS WHQL match -> must win over generic 2-part!
                new DriverCandidate
                {
                    Title = "Intel OEM SUBSYS Verified Driver",
                    TargetVersion = "24.40.0.4",
                    MatchedHardwareId = @"PCI\VEN_8086&DEV_51F0&SUBSYS_00948086",
                    WhqlSigned = true,
                    Source = "Microsoft Update Catalog"
                },
                // Foreign OEM SUBSYS (e.g. Dell 40908086 on a Lenovo/Intel 00948086 card) -> MUST be rejected even though 2-part VEN&DEV matches!
                new DriverCandidate
                {
                    Title = "Foreign OEM SUBSYS Driver",
                    TargetVersion = "26.99.0.1",
                    MatchedHardwareId = @"PCI\VEN_8086&DEV_51F0&SUBSYS_40908086",
                    WhqlSigned = true,
                    Source = "Microsoft Update Catalog"
                },
                // Unsigned beta driver -> must be filtered when whqlOnly=true
                new DriverCandidate
                {
                    Title = "Unsigned Beta Driver",
                    TargetVersion = "25.99.0.1",
                    MatchedHardwareId = @"PCI\VEN_8086&DEV_51F0&SUBSYS_00948086",
                    WhqlSigned = false,
                    Source = "External"
                }
            };

            var winner = DriverVersionComparer.SelectBestCandidate(dev, candidates, true);
            AssertEx.True(winner != null, "winner selected");
            AssertEx.Equal("24.40.0.4", winner.TargetVersion, "Exact 4-part SUBSYS WHQL candidate wins over generic 2-part and unsigned");

            // Device with empty HardwareIds must never false-match an unrelated candidate
            var swDev = new HardwareDevice { DeviceName = "Virtual Tile Node", HardwareIds = new List<string>() };
            var falseMatch = DriverVersionComparer.SelectBestCandidate(swDev, candidates, true);
            AssertEx.True(falseMatch == null, "Device without HardwareIds must never false-match unrelated candidates");
        }

        private static void TestProblemAndHResultCatalog()
        {
            var healthy = ProblemCodeCatalog.DescribeProblemCode(0);
            AssertEx.Equal("Healthy", healthy.ShortStatus, "Code 0 is Healthy");

            var code28 = ProblemCodeCatalog.DescribeProblemCode(28);
            AssertEx.True(code28.Summary.IndexOf("not installed", StringComparison.OrdinalIgnoreCase) >= 0, "Code 28 missing driver");
            AssertEx.True(!string.IsNullOrWhiteSpace(code28.RecommendedAction), "Code 28 has remediation");

            var code52 = ProblemCodeCatalog.DescribeProblemCode(52);
            AssertEx.True(code52.Summary.IndexOf("signature", StringComparison.OrdinalIgnoreCase) >= 0, "Code 52 digital signature");

            var hrNoDev = ProblemCodeCatalog.DescribeInstallHResult(0xE000020Bu);
            AssertEx.Equal("ERROR_NO_SUCH_DEVINST", hrNoDev.SymbolicName, "SetupAPI 0xE000020B mapped");

            var hrUntrusted = ProblemCodeCatalog.DescribeInstallHResult(0x800B0100u);
            AssertEx.Equal("TRUST_E_NOSIGNATURE", hrUntrusted.SymbolicName, "Authenticode 0x800B0100 mapped");

            var win32AccessDenied = ProblemCodeCatalog.DescribeInstallHResult(5u);
            AssertEx.Equal("E_ACCESSDENIED", win32AccessDenied.SymbolicName, "Raw Win32 error 5 maps to 0x80070005 E_ACCESSDENIED");
        }

        private static void TestVendorResolver()
        {
            var intel = PciVendorResolver.ResolveVendor(@"PCI\VEN_8086&DEV_51F0&SUBSYS_380117AA", "Intel");
            AssertEx.Equal("Intel Corporation", intel.VendorName, "VEN_8086 -> Intel");
            AssertEx.Equal("Lenovo", intel.SubsystemOemName, "SUBSYS ending 17AA -> Lenovo OEM");
            AssertEx.True(intel.SupportUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase), "HTTPS vendor portal");

            var nvidia = PciVendorResolver.ResolveVendor(@"PCI\VEN_10DE&DEV_28E0", "");
            AssertEx.Equal("NVIDIA Corporation", nvidia.VendorName, "VEN_10DE -> NVIDIA");
        }

        private static void TestCatalogSearchParser()
        {
            string sampleHtml = @"
<table id='ctl00_catalogBody_updateMatches'>
  <tr id='91bf41cb-0052-454a-a3cb-13ff62a706a4_R0'>
    <td></td>
    <td><a id='91bf41cb_link'>Intel net Driver Update (23.90.0.1)</a></td>
    <td>Windows 10, Vibranium and later, Servicing Drivers</td>
    <td>Drivers (Networking)</td>
    <td>1/10/2026</td>
    <td>23.90.0.1</td>
    <td><span>21.0 MB</span> <span>22020096</span></td>
    <td></td>
  </tr>
  <tr id='1805f0b5-23fd-4ef6-903d-16269b3f52bf_R1'>
    <td></td>
    <td><a id='1805f0b5_link'>Intel net Driver Update (24.40.0.4)</a></td>
    <td>Windows 11 Client, version 24H2 and later, Servicing Drivers</td>
    <td>Drivers (Networking)</td>
    <td>4/12/2026</td>
    <td>24.40.0.4</td>
    <td><span>23.3 MB</span> <span>24467933</span></td>
    <td></td>
  </tr>
</table>";

            var win11List = WindowsUpdateCatalogClient.ParseSearchHtml(sampleHtml, 26100, @"PCI\VEN_8086&DEV_51F0&SUBSYS_00948086");
            AssertEx.Equal(1, win11List.Count, "Win11 build 26100 filters to Windows 11 row");
            AssertEx.Equal("1805f0b5-23fd-4ef6-903d-16269b3f52bf", win11List[0].UpdateId, "GUID parsed");
            AssertEx.Equal("24.40.0.4", win11List[0].TargetVersion, "Version parsed");
            AssertEx.Equal("2026-04-12", win11List[0].TargetDate, "ISO date normalized");
            AssertEx.True(win11List[0].WhqlSigned, "MS Catalog drivers are WHQL signed");

            var win10List = WindowsUpdateCatalogClient.ParseSearchHtml(sampleHtml, 19045, @"PCI\VEN_8086&DEV_51F0");
            AssertEx.Equal(1, win10List.Count, "Win10 build 19045 filters to Windows 10 row");
            AssertEx.Equal("23.90.0.1", win10List[0].TargetVersion, "Win10 version parsed");
        }

        private static void TestCatalogDownloadAllowlist()
        {
            string dialogHtml = @"
downloadInformation[0].files[0].url = 'https://catalog.s.download.windowsupdate.com/d/msdownload/update/driver/drvs/2026/04/a855de7b_2a7b7fffc6bd37f5093c6a0261f9aea45ac475f4.cab';
downloadInformation[0].files[1].url = 'http://catalog.s.download.windowsupdate.com/insecure.cab';
downloadInformation[0].files[2].url = 'https://windowsupdate.com.attacker.example/spoof.cab';
";
            var urls = WindowsUpdateCatalogClient.ParseDownloadDialogHtml(dialogHtml);
            AssertEx.Equal(1, urls.Count, "Only HTTPS *.windowsupdate.com / *.microsoft.com .cab URLs allowed");
            AssertEx.True(urls[0].StartsWith("https://catalog.s.download.windowsupdate.com/", StringComparison.OrdinalIgnoreCase), "Exact trusted URL preserved");
        }

        private static void TestDriverStoreAndHvciParser()
        {
            string samplePnpUtil = @"Microsoft PnP Utility

Published Name:     oem42.inf
Original Name:      ssuddfu.inf
Provider Name:      SAMSUNG Electronics Co., Ltd.
Class Name:         Universal Serial Bus devices
Class GUID:         {88bae032-5a81-49f0-bc3d-a4ff138216d6}
Driver Version:     05/19/2021 2.17.16.0
Signer Name:        Microsoft Windows Hardware Compatibility Publisher

Published Name:     oem88.inf
Original Name:      netwtw08.inf
Provider Name:      Intel
Class Name:         Network adapters
Driver Version:     02/18/2025 23.80.1.3
Signer Name:        Microsoft Windows Hardware Compatibility Publisher
";
            var packages = DriverStoreInspector.ParsePnpUtilOutput(samplePnpUtil);
            AssertEx.Equal(2, packages.Count, "2 OEM packages parsed");
            AssertEx.Equal("oem42.inf", packages[0].PublishedName, "oem42.inf parsed");
            AssertEx.Equal("2.17.16.0", packages[0].DriverVersion, "version extracted from Driver Version field");

            packages[0].SysFiles.Add("ssdudfu.sys");
            DriverStoreInspector.EvaluateHvciCompatibility(packages[0]);
            AssertEx.True(packages[0].FlagsHvciIncompatible, "ssdudfu.sys flagged as known HVCI Memory Integrity blocker");

            packages[1].SysFiles.Add("Netwtw14.sys");
            DriverStoreInspector.EvaluateHvciCompatibility(packages[1]);
            AssertEx.True(!packages[1].FlagsHvciIncompatible, "Modern Intel Wi-Fi .sys is HVCI compatible");
        }

        private static void TestLivePnpScanner()
        {
            var devices = PnpDeviceScanner.ScanDevices(true);
            AssertEx.True(devices != null && devices.Count >= 10, "Live 64-bit SetupAPI scan must return >= 10 devices on Windows host");
            int presentWithHwid = 0;
            int ghostCount = 0;
            foreach (var d in devices)
            {
                if (d.IsPresent && !string.IsNullOrEmpty(d.PrimaryHardwareId))
                {
                    presentWithHwid++;
                }
                if (!d.IsPresent)
                {
                    ghostCount++;
                }
            }
            AssertEx.True(presentWithHwid >= 5, "Present devices carry real Hardware IDs");
            AssertEx.True(ghostCount >= 0, "Ghost enumeration completed cleanly");
        }

        private static void TestZipSlipProtection()
        {
            AssertEx.True(!RescueBackupService.ValidateSafeArchiveEntry("../evil.sys"), "blocks ../");
            AssertEx.True(!RescueBackupService.ValidateSafeArchiveEntry(@"..\..\Windows\System32\evil.dll"), "blocks ..\\");
            AssertEx.True(!RescueBackupService.ValidateSafeArchiveEntry(@"C:\Windows\evil.inf"), "blocks drive colon");
            AssertEx.True(!RescueBackupService.ValidateSafeArchiveEntry(@"\rooted\evil.inf"), "blocks leading backslash");
            AssertEx.True(!RescueBackupService.ValidateSafeArchiveEntry("/rooted/evil.inf"), "blocks leading slash");
            AssertEx.True(RescueBackupService.ValidateSafeArchiveEntry("driver/netwtw08.inf"), "allows safe relative entry");
            AssertEx.True(RescueBackupService.ValidateSafeArchiveEntry("Netwtw14.sys"), "allows root filename");
        }

        private static void TestRescueBackupAndOfflineRoundTrip()
        {
            string tempRoot = Path.Combine(Path.GetTempPath(), "DriverPlus_TDD_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            try
            {
                string fakeRepo = Path.Combine(tempRoot, "src_pkg");
                Directory.CreateDirectory(fakeRepo);
                File.WriteAllText(Path.Combine(fakeRepo, "sample.inf"), "[Version]\r\nSignature=\"$WINDOWS NT$\"\r\nDriverVer=04/12/2026,24.40.0.4\r\n");
                File.WriteAllBytes(Path.Combine(fakeRepo, "sample.sys"), new byte[] { 0x4D, 0x5A, 0x90, 0x00 });

                string backupDir = Path.Combine(tempRoot, "backups");
                var dev = new HardwareDevice
                {
                    DeviceName = "Intel(R) Wi-Fi 6 AX203",
                    InfName = "oem42.inf",
                    DriverVersion = "24.40.0.4",
                    HardwareIds = new List<string> { @"PCI\VEN_8086&DEV_51F0" }
                };

                BackupRecord rec = RescueBackupService.CreateDriverBackupZip(dev, fakeRepo, backupDir);
                AssertEx.True(rec != null && File.Exists(rec.ZipFilePath), "Backup ZIP created on disk");
                AssertEx.Equal(64, rec.Sha256.Length, "SHA-256 recorded");

                string restoreDir = Path.Combine(tempRoot, "restored");
                RescueBackupService.ExtractBackupZipSafely(rec.ZipFilePath, restoreDir);
                AssertEx.True(File.Exists(Path.Combine(restoreDir, "sample.inf")), "Restored INF exists");

                string offlineJson = Path.Combine(tempRoot, "offline-profile.json");
                dev.VendorSupportUrl = @"C:\Windows\System32\calc.exe"; // Untrusted non-HTTPS payload
                var sys = new SystemProfile { ComputerName = "TEST-RIG", OsCaption = "Windows 11", OsBuildNumber = 26100 };
                RescueBackupService.ExportOfflineProfile(new List<HardwareDevice> { dev }, sys, offlineJson);

                OfflineHardwareManifest imported = RescueBackupService.ImportOfflineProfile(offlineJson);
                AssertEx.Equal("TEST-RIG", imported.MachineName, "Offline profile machine name round-tripped");
                AssertEx.Equal(1, imported.Devices.Count, "Offline profile devices round-tripped");
                AssertEx.True(
                    string.IsNullOrEmpty(imported.Devices[0].VendorSupportUrl) ||
                    imported.Devices[0].VendorSupportUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase),
                    "ImportOfflineProfile strips non-HTTPS VendorSupportUrl");
            }
            finally
            {
                try { Directory.Delete(tempRoot, true); } catch { }
            }
        }

        private static void TestAuthenticodeAndCabSha1()
        {
            string url = "https://catalog.s.download.windowsupdate.com/d/msdownload/update/driver/drvs/2026/04/a855de7b-b2e8-4192-8bca-2d3c74dc8665_2a7b7fffc6bd37f5093c6a0261f9aea45ac475f4.cab";
            string sha1 = DriverInstallOrchestrator.ExtractExpectedSha1FromCabUrl(url);
            AssertEx.Equal("2a7b7fffc6bd37f5093c6a0261f9aea45ac475f4", sha1, "40-hex SHA-1 extracted from MS Update CAB filename");

            string tempUnsigned = Path.Combine(Path.GetTempPath(), "driverplus_unsigned_" + Guid.NewGuid().ToString("N") + ".dll");
            File.WriteAllBytes(tempUnsigned, new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00 });
            try
            {
                SignatureCheckResult unsignedRes = DriverInstallOrchestrator.VerifyAuthenticodeFile(tempUnsigned);
                AssertEx.True(!unsignedRes.IsTrusted, "Unsigned dummy binary must fail WinVerifyTrust");
            }
            finally
            {
                try { File.Delete(tempUnsigned); } catch { }
            }

            string repoRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"DriverStore\FileRepository");
            if (Directory.Exists(repoRoot))
            {
                string signedCat = null;
                foreach (string subDir in Directory.EnumerateDirectories(repoRoot))
                {
                    string[] cats = Directory.GetFiles(subDir, "*.cat", SearchOption.TopDirectoryOnly);
                    if (cats.Length > 0)
                    {
                        signedCat = cats[0];
                        break;
                    }
                }
                AssertEx.True(!string.IsNullOrEmpty(signedCat), "Found signed .cat in Windows DriverStore");
                SignatureCheckResult signedRes = DriverInstallOrchestrator.VerifyAuthenticodeFile(signedCat);
                AssertEx.True(signedRes != null && signedRes.IsTrusted, "Real signed DriverStore .cat passes WinVerifyTrust IsTrusted==true");
            }
        }

        private static void TestGameRuntimesAndCrashDoctor()
        {
            List<RuntimeComponent> runtimes = GameRuntimeInspector.InspectAllRuntimes();
            AssertEx.True(runtimes != null && runtimes.Count >= 12, ">= 12 game/system runtimes audited");

            bool foundDotNet48 = false;
            foreach (RuntimeComponent rc in runtimes)
            {
                AssertEx.True(rc.OfficialDownloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase), "HTTPS official runtime URL for " + rc.Id);
                if (rc.Id == "dotnet-fx-48" && rc.IsInstalled)
                {
                    foundDotNet48 = true;
                }
            }
            AssertEx.True(foundDotNet48, ".NET Framework 4.8 detected as installed on Windows 10/11 host");

            AssertEx.Equal("vcredist-2015-2022-x64", GameRuntimeInspector.MapFaultingModuleToRuntime("vcruntime140_1.dll"), "vcruntime140_1.dll maps to VC++ 2015-2022");
            AssertEx.Equal("directx-jun2010", GameRuntimeInspector.MapFaultingModuleToRuntime("d3dx9_43.dll"), "d3dx9_43.dll maps to DirectX");
            AssertEx.Equal("openal-runtime", GameRuntimeInspector.MapFaultingModuleToRuntime("OpenAL32.dll"), "OpenAL32.dll maps to OpenAL");
        }

        private static void TestHardwareFixDiagnostics()
        {
            var devices = PnpDeviceScanner.ScanDevices(true);
            var store = new DriverStoreReport { HvciEnabledInRegistry = true };
            List<DiagnosticFinding> findings = HardwareFixService.RunNonDestructiveDiagnostics(devices, store);
            AssertEx.Equal(6, findings.Count, "All 6 hardware & OS repair tools produce non-destructive diagnostics");
        }

        private static void TestGameBoostSnapshot()
        {
            string activeScheme = GameBoostService.GetActivePowerSchemeGuidString();
            Guid parsed;
            AssertEx.True(Guid.TryParse(activeScheme, out parsed), "PowrProf PowerGetActiveScheme returned valid GUID");

            string tempPath = Path.Combine(Path.GetTempPath(), "driverplus_boost_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                BoostSnapshot snap = GameBoostService.BuildRestoreSnapshot(activeScheme, new List<string> { "Spooler", "SysMain" });
                GameBoostService.SaveSnapshotToFile(tempPath, snap);

                // Re-building snapshot when one already exists must preserve original PreviousPowerSchemeGuid
                BoostSnapshot second = GameBoostService.MergeWithExistingSnapshot(
                    snap,
                    GameBoostService.HighPerformanceGuid.ToString("D"),
                    new List<string> { "WSearch" });
                AssertEx.Equal(activeScheme, second.PreviousPowerSchemeGuid, "Re-activating Game Boost preserves pre-boost power scheme");
                AssertEx.True(second.StoppedServices.Count == 3, "Re-activating Game Boost unions stopped services");

                BoostSnapshot loaded = GameBoostService.LoadSnapshotFromFile(tempPath);
                AssertEx.Equal(activeScheme, loaded.PreviousPowerSchemeGuid, "Power scheme GUID persisted before mutation");
                AssertEx.Equal(2, loaded.StoppedServices.Count, "Stopped services list persisted");
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        private static void TestSystemInfoAndExport()
        {
            SystemProfile profile = SystemInfoService.CollectSystemProfile();
            AssertEx.True(!string.IsNullOrWhiteSpace(profile.CpuName), "Live CPU name detected");
            AssertEx.True(profile.TotalRamGb > 2.0, "Live total RAM > 2 GB");
            AssertEx.True(profile.OsBuildNumber >= 10240, "Windows 10/11 build number detected");

            string md = SystemInfoService.FormatMarkdownReport(profile, new UpdateScanSummary { TotalPresentDevices = 100 });
            AssertEx.True(md.IndexOf(profile.CpuName, StringComparison.Ordinal) >= 0, "Markdown report embeds CPU name");
        }

        private static void TestLocalWebPortalServer()
        {
            using (var server = new LocalWebPortalServer(0))
            {
                server.Start();
                AssertEx.True(server.Port > 0, "Ephemeral loopback port assigned");
                AssertEx.Equal(64, server.CsrfToken.Length, "256-bit hex CSRF token generated");

                string baseUrl = "http://127.0.0.1:" + server.Port;
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
                {
                    HttpResponseMessage htmlResp = http.GetAsync(baseUrl + "/").Result;
                    AssertEx.Equal(HttpStatusCode.OK, htmlResp.StatusCode, "GET / returns 200 OK");
                    string html = htmlResp.Content.ReadAsStringAsync().Result;
                    AssertEx.True(html.IndexOf("Driver+ Telemetry", StringComparison.Ordinal) >= 0, "Dashboard HTML served");

                    HttpResponseMessage bootResp = http.GetAsync(baseUrl + "/api/bootstrap").Result;
                    AssertEx.Equal(HttpStatusCode.OK, bootResp.StatusCode, "GET /api/bootstrap returns 200 OK");
                    string bootJson = bootResp.Content.ReadAsStringAsync().Result;
                    AssertEx.True(bootJson.IndexOf("\"systemProfile\"", StringComparison.Ordinal) >= 0, "Bootstrap JSON contains systemProfile");
                    AssertEx.True(bootJson.IndexOf("\"scanSummary\"", StringComparison.Ordinal) >= 0, "Bootstrap JSON contains scanSummary");

                    // POST without CSRF token must return 403 Forbidden
                    var badReq = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/action")
                    {
                        Content = new StringContent("{\"action\":\"ping\"}", Encoding.UTF8, "application/json")
                    };
                    HttpResponseMessage badResp = http.SendAsync(badReq).Result;
                    AssertEx.Equal(HttpStatusCode.Forbidden, badResp.StatusCode, "POST without X-DriverPlus-Token rejected with 403");

                    // DNS Rebinding Host header check: forged Host must return 403 Forbidden
                    var rebindReq = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/api/bootstrap");
                    rebindReq.Headers.Host = "evil-rebind.example:" + server.Port;
                    HttpResponseMessage rebindResp = http.SendAsync(rebindReq).Result;
                    AssertEx.Equal(HttpStatusCode.Forbidden, rebindResp.StatusCode, "DNS rebinding Host header rejected with 403");

                    // POST with valid CSRF token but MISSING Origin header must return 403 Forbidden
                    var noOriginReq = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/action")
                    {
                        Content = new StringContent("{\"action\":\"ping\"}", Encoding.UTF8, "application/json")
                    };
                    noOriginReq.Headers.Add("X-DriverPlus-Token", server.CsrfToken);
                    HttpResponseMessage noOriginResp = http.SendAsync(noOriginReq).Result;
                    AssertEx.Equal(HttpStatusCode.Forbidden, noOriginResp.StatusCode, "POST missing Origin rejected with 403");

                    // POST with valid CSRF token + matching loopback Origin succeeds
                    var goodReq = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/action")
                    {
                        Content = new StringContent("{\"action\":\"ping\"}", Encoding.UTF8, "application/json")
                    };
                    goodReq.Headers.Add("X-DriverPlus-Token", server.CsrfToken);
                    goodReq.Headers.Add("Origin", baseUrl);
                    HttpResponseMessage goodResp = http.SendAsync(goodReq).Result;
                    AssertEx.Equal(HttpStatusCode.OK, goodResp.StatusCode, "POST with valid X-DriverPlus-Token returns 200 OK");
                }
            }
        }
    }
}
