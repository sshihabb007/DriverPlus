using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;
using DriverPlus.Core;

namespace DriverPlus.Services
{
    public static class GameRuntimeInspector
    {
        public static List<RuntimeComponent> InspectAllRuntimes()
        {
            var uninstallEntries = CollectUninstallDisplayEntries();
            var list = new List<RuntimeComponent>();

            // 1-2. VC++ 2015-2022
            list.Add(BuildVc2015Plus("vcredist-2015-2022-x64", "Microsoft Visual C++ 2015–2022 Redistributable (x64)", "x64",
                @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64", "14.42.34433.0",
                "Microsoft.VCRedist.2015+.x64", "https://aka.ms/vs/17/release/vc_redist.x64.exe"));
            list.Add(BuildVc2015Plus("vcredist-2015-2022-x86", "Microsoft Visual C++ 2015–2022 Redistributable (x86)", "x86",
                @"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x86", "14.42.34433.0",
                "Microsoft.VCRedist.2015+.x86", "https://aka.ms/vs/17/release/vc_redist.x86.exe"));

            // 3-4. VC++ 2013
            list.Add(MatchUninstallRuntime(uninstallEntries, "vcredist-2013-x64",
                "Microsoft Visual C++ 2013 Redistributable (x64)", "Visual C++", "x64",
                @"Visual C\+\+ 2013.*x64", "12.0.40664.0",
                "Microsoft.VCRedist.2013.x64", "https://aka.ms/highdpimfc2013x64enu"));
            list.Add(MatchUninstallRuntime(uninstallEntries, "vcredist-2013-x86",
                "Microsoft Visual C++ 2013 Redistributable (x86)", "Visual C++", "x86",
                @"Visual C\+\+ 2013.*x86", "12.0.40664.0",
                "Microsoft.VCRedist.2013.x86", "https://aka.ms/highdpimfc2013x86enu"));

            // 5-6. VC++ 2012
            list.Add(MatchUninstallRuntime(uninstallEntries, "vcredist-2012-x64",
                "Microsoft Visual C++ 2012 Redistributable (x64)", "Visual C++", "x64",
                @"Visual C\+\+ 2012.*x64", "11.0.61030.0",
                "Microsoft.VCRedist.2012.x64", "https://www.microsoft.com/en-us/download/details.aspx?id=30679"));
            list.Add(MatchUninstallRuntime(uninstallEntries, "vcredist-2012-x86",
                "Microsoft Visual C++ 2012 Redistributable (x86)", "Visual C++", "x86",
                @"Visual C\+\+ 2012.*x86", "11.0.61030.0",
                "Microsoft.VCRedist.2012.x86", "https://www.microsoft.com/en-us/download/details.aspx?id=30679"));

            // 7-8. VC++ 2010
            list.Add(MatchUninstallRuntime(uninstallEntries, "vcredist-2010-x64",
                "Microsoft Visual C++ 2010 SP1 Redistributable (x64)", "Visual C++", "x64",
                @"Visual C\+\+ 2010.*x64", "10.0.40219",
                "Microsoft.VCRedist.2010.x64", "https://www.microsoft.com/en-us/download/details.aspx?id=26999"));
            list.Add(MatchUninstallRuntime(uninstallEntries, "vcredist-2010-x86",
                "Microsoft Visual C++ 2010 SP1 Redistributable (x86)", "Visual C++", "x86",
                @"Visual C\+\+ 2010.*x86", "10.0.40219",
                "Microsoft.VCRedist.2010.x86", "https://www.microsoft.com/en-us/download/details.aspx?id=26999"));

            // 9. DirectX End-User Runtime (June 2010 legacy D3DX9 / XInput DLLs)
            string sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string d3dx9Path = Path.Combine(sys32, "d3dx9_43.dll");
            bool hasDx = File.Exists(d3dx9Path);
            string dxVer = hasDx ? ReadFileVersionSafe(d3dx9Path) : "";
            list.Add(new RuntimeComponent
            {
                Id = "directx-jun2010",
                Name = "Microsoft DirectX End-User Runtime (D3DX9 / XInput / XAudio2)",
                Category = "Graphics & Audio",
                Architecture = "x86/x64",
                InstalledVersion = hasDx ? (string.IsNullOrEmpty(dxVer) ? "9.29.952.3111" : dxVer) : "",
                LatestVersion = "9.29.952.3111",
                IsInstalled = hasDx,
                NeedsUpdate = !hasDx,
                WingetId = "Microsoft.DirectX",
                OfficialDownloadUrl = "https://www.microsoft.com/en-us/download/details.aspx?id=35",
                Notes = "Required by DirectX 9/10/11 titles needing d3dx9_43.dll and xinput1_3.dll."
            });

            // 10. .NET Framework 4.8+
            int releaseKey = ReadDotNet4ReleaseKey();
            bool hasNet48 = releaseKey >= 528040;
            list.Add(new RuntimeComponent
            {
                Id = "dotnet-fx-48",
                Name = "Microsoft .NET Framework 4.8 / 4.8.1 Runtime",
                Category = ".NET Runtime",
                Architecture = "x64",
                InstalledVersion = releaseKey >= 533320 ? "4.8.1" : (hasNet48 ? "4.8.0" : ""),
                LatestVersion = "4.8.1",
                IsInstalled = hasNet48,
                NeedsUpdate = !hasNet48,
                WingetId = "Microsoft.DotNet.Framework.DeveloperPack_4",
                OfficialDownloadUrl = "https://dotnet.microsoft.com/en-us/download/dotnet-framework/net481",
                Notes = "Inbox Windows desktop managed runtime."
            });

            // 11. .NET Desktop Runtime (8.0 / 9.0)
            string latestModernNet = DetectModernDotNetDesktopVersion();
            list.Add(new RuntimeComponent
            {
                Id = "dotnet-desktop-modern",
                Name = "Microsoft .NET Desktop Runtime (8.0 / 9.0 LTS/STS)",
                Category = ".NET Runtime",
                Architecture = "x64",
                InstalledVersion = latestModernNet,
                LatestVersion = "9.0.7",
                IsInstalled = !string.IsNullOrEmpty(latestModernNet),
                NeedsUpdate = string.IsNullOrEmpty(latestModernNet),
                WingetId = "Microsoft.DotNet.DesktopRuntime.9",
                OfficialDownloadUrl = "https://dotnet.microsoft.com/en-us/download/dotnet/9.0",
                Notes = "Powers modern WPF/WinForms .NET 8 and .NET 9 desktop titles."
            });

            // 12. OpenAL 3D Audio
            string openAlPath = Path.Combine(sys32, "OpenAL32.dll");
            bool hasOpenAl = File.Exists(openAlPath);
            list.Add(new RuntimeComponent
            {
                Id = "openal-runtime",
                Name = "OpenAL Cross-Platform 3D Audio Library",
                Category = "Graphics & Audio",
                Architecture = "x86/x64",
                InstalledVersion = hasOpenAl ? ReadFileVersionSafe(openAlPath) : "",
                LatestVersion = "2.1.0.0",
                IsInstalled = hasOpenAl,
                NeedsUpdate = !hasOpenAl,
                WingetId = "CreativeTechnology.OpenAL",
                OfficialDownloadUrl = "https://www.openal.org/downloads/",
                Notes = "Required by classic Unreal, id Tech, and indie OpenGL/Vulkan games."
            });

            // 13. NVIDIA PhysX System Software
            list.Add(MatchUninstallRuntime(uninstallEntries, "nvidia-physx",
                "NVIDIA PhysX System Software", "Physics & Game SDK", "x86/x64",
                @"NVIDIA PhysX", "9.23.1019",
                "Nvidia.PhysX", "https://www.nvidia.com/en-us/drivers/physx/physx-9-23-1019-driver/"));

            // 14. Java / OpenJDK x64
            list.Add(MatchUninstallRuntime(uninstallEntries, "java-openjdk-x64",
                "Java / Eclipse Temurin OpenJDK Runtime (x64)", "Java Runtime", "x64",
                @"(Java\s+\d+|Temurin|OpenJDK)", "21.0.5",
                "EclipseAdoptium.Temurin.21.JRE", "https://adoptium.net/temurin/releases/"));

            return list;
        }

        public static string MapFaultingModuleToRuntime(string moduleName)
        {
            if (string.IsNullOrWhiteSpace(moduleName))
            {
                return "";
            }
            string m = Path.GetFileName(moduleName.Trim()).ToLowerInvariant();
            if (m.StartsWith("vcruntime140", StringComparison.Ordinal) ||
                m.StartsWith("msvcp140", StringComparison.Ordinal) ||
                m.StartsWith("concrt140", StringComparison.Ordinal) ||
                m.StartsWith("vcomp140", StringComparison.Ordinal))
            {
                return "vcredist-2015-2022-x64";
            }
            if (m.StartsWith("msvcr120", StringComparison.Ordinal) || m.StartsWith("msvcp120", StringComparison.Ordinal))
            {
                return "vcredist-2013-x64";
            }
            if (m.StartsWith("msvcr110", StringComparison.Ordinal) || m.StartsWith("msvcp110", StringComparison.Ordinal))
            {
                return "vcredist-2012-x64";
            }
            if (m.StartsWith("msvcr100", StringComparison.Ordinal) || m.StartsWith("msvcp100", StringComparison.Ordinal))
            {
                return "vcredist-2010-x64";
            }
            if (m.StartsWith("d3dx9_", StringComparison.Ordinal) ||
                m.StartsWith("d3dx10_", StringComparison.Ordinal) ||
                m.StartsWith("d3dx11_", StringComparison.Ordinal) ||
                m.StartsWith("xinput1_", StringComparison.Ordinal) ||
                m.StartsWith("x3daudio", StringComparison.Ordinal) ||
                m.StartsWith("xapofx", StringComparison.Ordinal))
            {
                return "directx-jun2010";
            }
            if (m.Equals("openal32.dll", StringComparison.Ordinal) || m.Equals("wrap_oal.dll", StringComparison.Ordinal))
            {
                return "openal-runtime";
            }
            if (m.StartsWith("physx", StringComparison.Ordinal))
            {
                return "nvidia-physx";
            }
            return "";
        }

        public static List<CrashDllEvent> ScanRecentCrashDllEvents(int maxEntriesToInspect)
        {
            var matches = new List<CrashDllEvent>();
            try
            {
                using (var appLog = new EventLog("Application"))
                {
                    int total = appLog.Entries.Count;
                    int floor = Math.Max(0, total - Math.Max(20, maxEntriesToInspect));
                    for (int i = total - 1; i >= floor; i--)
                    {
                        EventLogEntry entry = appLog.Entries[i];
                        if (entry == null || entry.InstanceId != 1000) continue;
                        if (entry.ReplacementStrings == null || entry.ReplacementStrings.Length < 4) continue;

                        string appName = entry.ReplacementStrings[0] ?? "";
                        string faultMod = entry.ReplacementStrings[3] ?? "";
                        string runtimeId = MapFaultingModuleToRuntime(faultMod);
                        if (!string.IsNullOrEmpty(runtimeId))
                        {
                            matches.Add(new CrashDllEvent
                            {
                                TimeGenerated = entry.TimeGenerated,
                                ApplicationName = appName,
                                FaultingModule = faultMod,
                                RecommendedRuntimeId = runtimeId,
                                RecommendedRuntimeName = runtimeId
                            });
                            if (matches.Count >= 10) break;
                        }
                    }
                }
            }
            catch
            {
            }
            return matches;
        }

        private static readonly Regex SafeWingetIdRegex = new Regex(
            @"^[A-Za-z0-9._+\-]+$",
            RegexOptions.Compiled);

        public static Task<string> InstallOrRepairRuntimeAsync(RuntimeComponent runtime)
        {
            return Task.Run(() =>
            {
                if (runtime == null) return "No runtime selected.";
                try
                {
                    string wingetExe = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        @"Microsoft\WindowsApps\winget.exe");

                    if (!string.IsNullOrWhiteSpace(runtime.WingetId) &&
                        SafeWingetIdRegex.IsMatch(runtime.WingetId) &&
                        File.Exists(wingetExe))
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = wingetExe,
                            Arguments = string.Format(
                                "install --id \"{0}\" --exact --silent --accept-package-agreements --accept-source-agreements",
                                runtime.WingetId),
                            UseShellExecute = false,
                            RedirectStandardOutput = false,
                            RedirectStandardError = false,
                            CreateNoWindow = true
                        };
                        using (Process p = Process.Start(psi))
                        {
                            if (p != null)
                            {
                                bool exited = p.WaitForExit(180000);
                                if (!exited)
                                {
                                    try { p.Kill(); } catch { }
                                }
                                else if (p.ExitCode == 0)
                                {
                                    return "Installed / verified " + runtime.Name + " via official Winget manifest.";
                                }
                            }
                        }
                    }

                    Uri safeUrl;
                    if (!string.IsNullOrWhiteSpace(runtime.OfficialDownloadUrl) &&
                        Uri.TryCreate(runtime.OfficialDownloadUrl, UriKind.Absolute, out safeUrl) &&
                        string.Equals(safeUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = safeUrl.AbsoluteUri,
                            UseShellExecute = true
                        });
                        return "Opened official vendor HTTPS installer page for " + runtime.Name + ".";
                    }
                }
                catch (Exception ex)
                {
                    return "Failed launching runtime installer: " + ex.Message;
                }
                return "Could not invoke installer.";
            });
        }

        private static RuntimeComponent BuildVc2015Plus(
            string id,
            string name,
            string arch,
            string regPath,
            string latestVersion,
            string wingetId,
            string url)
        {
            bool installed = false;
            string ver = "";
            try
            {
                using (RegistryKey rk = Registry.LocalMachine.OpenSubKey(regPath, false))
                {
                    if (rk != null)
                    {
                        installed = Convert.ToInt32(rk.GetValue("Installed", 0)) == 1;
                        int maj = Convert.ToInt32(rk.GetValue("Major", 0));
                        int min = Convert.ToInt32(rk.GetValue("Minor", 0));
                        int bld = Convert.ToInt32(rk.GetValue("Bld", 0));
                        int rbld = Convert.ToInt32(rk.GetValue("Rbld", 0));
                        if (maj > 0)
                        {
                            ver = string.Format("{0}.{1}.{2}.{3}", maj, min, bld, rbld);
                        }
                    }
                }
            }
            catch
            {
            }

            bool needsUpdate = !installed ||
                (!string.IsNullOrEmpty(ver) && DriverVersionComparer.CompareVersions(ver, "14.38.0.0") < 0);

            return new RuntimeComponent
            {
                Id = id,
                Name = name,
                Category = "Visual C++",
                Architecture = arch,
                InstalledVersion = ver,
                LatestVersion = latestVersion,
                IsInstalled = installed,
                NeedsUpdate = needsUpdate,
                WingetId = wingetId,
                OfficialDownloadUrl = url,
                Notes = "Unified Visual Studio 2015/2017/2019/2022 C/C++ runtime."
            };
        }

        private static RuntimeComponent MatchUninstallRuntime(
            List<KeyValuePair<string, string>> entries,
            string id,
            string name,
            string category,
            string arch,
            string regexPattern,
            string latestVer,
            string wingetId,
            string url)
        {
            var rx = new Regex(regexPattern, RegexOptions.IgnoreCase);
            string bestVer = "";
            bool found = false;

            foreach (var kv in entries)
            {
                if (rx.IsMatch(kv.Key))
                {
                    found = true;
                    if (string.IsNullOrEmpty(bestVer) ||
                        DriverVersionComparer.CompareVersions(bestVer, kv.Value) < 0)
                    {
                        bestVer = kv.Value;
                    }
                }
            }

            return new RuntimeComponent
            {
                Id = id,
                Name = name,
                Category = category,
                Architecture = arch,
                InstalledVersion = bestVer,
                LatestVersion = latestVer,
                IsInstalled = found,
                NeedsUpdate = !found,
                WingetId = wingetId,
                OfficialDownloadUrl = url
            };
        }

        private static List<KeyValuePair<string, string>> CollectUninstallDisplayEntries()
        {
            var list = new List<KeyValuePair<string, string>>();
            string[] roots = new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };

            foreach (string root in roots)
            {
                try
                {
                    using (RegistryKey rk = Registry.LocalMachine.OpenSubKey(root, false))
                    {
                        if (rk == null) continue;
                        foreach (string sub in rk.GetSubKeyNames())
                        {
                            try
                            {
                                using (RegistryKey item = rk.OpenSubKey(sub, false))
                                {
                                    if (item == null) continue;
                                    string dn = Convert.ToString(item.GetValue("DisplayName", "")) ?? "";
                                    if (!string.IsNullOrWhiteSpace(dn))
                                    {
                                        string dv = Convert.ToString(item.GetValue("DisplayVersion", "")) ?? "";
                                        list.Add(new KeyValuePair<string, string>(dn, dv));
                                    }
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
            return list;
        }

        private static int ReadDotNet4ReleaseKey()
        {
            try
            {
                using (RegistryKey rk = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", false))
                {
                    if (rk != null)
                    {
                        return Convert.ToInt32(rk.GetValue("Release", 0));
                    }
                }
            }
            catch
            {
            }
            return 0;
        }

        private static string DetectModernDotNetDesktopVersion()
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    @"dotnet\shared\Microsoft.WindowsDesktop.App");
                if (!Directory.Exists(dir)) return "";
                string[] vers = Directory.GetDirectories(dir);
                string best = "";
                foreach (string v in vers)
                {
                    string name = Path.GetFileName(v);
                    if (string.IsNullOrEmpty(best) || DriverVersionComparer.CompareVersions(best, name) < 0)
                    {
                        best = name;
                    }
                }
                return best;
            }
            catch
            {
                return "";
            }
        }

        private static string ReadFileVersionSafe(string path)
        {
            try
            {
                FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(path);
                return fvi.FileVersion ?? "";
            }
            catch
            {
                return "";
            }
        }
    }
}
