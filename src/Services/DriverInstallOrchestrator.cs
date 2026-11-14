using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DriverPlus.Core;
using DriverPlus.Native;

namespace DriverPlus.Services
{
    public sealed class DriverInstallResult
    {
        public bool Succeeded { get; set; }
        public bool RebootRequired { get; set; }
        public string BackupZipPath { get; set; }
        public string InstalledInfCount { get; set; }
        public string SummaryMessage { get; set; }
    }

    public static class DriverInstallOrchestrator
    {
        private const long MaxCabBytes = 1610612736L; // 1.5 GiB ceiling

        private static readonly Regex CabSha1Regex = new Regex(
            @"_([0-9a-fA-F]{40})\.cab$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly HttpClient SharedDownloadHttp = CreateDownloadClient();

        private static HttpClient CreateDownloadClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(4) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DriverPlus/1.0 (Windows NT 10.0; Win64; x64)");
            return client;
        }

        public static bool IsCurrentProcessElevated()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                {
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        public static string ExtractExpectedSha1FromCabUrl(string cabUrl)
        {
            if (string.IsNullOrWhiteSpace(cabUrl))
            {
                return "";
            }
            Uri uri;
            string target = Uri.TryCreate(cabUrl, UriKind.Absolute, out uri) ? uri.AbsolutePath : cabUrl;
            Match m = CabSha1Regex.Match(target);
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : "";
        }

        public static SignatureCheckResult VerifyAuthenticodeFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return new SignatureCheckResult
                {
                    IsTrusted = false,
                    WinTrustStatus = 0x80070002u,
                    SignerSubject = "",
                    Summary = "File does not exist."
                };
            }

            IntPtr pFilePath = Marshal.StringToCoTaskMemUni(filePath);
            IntPtr pFileInfo = IntPtr.Zero;
            var wvtData = new NativeMethods.WINTRUST_DATA();
            bool stateOpened = false;

            try
            {
                var fileInfo = new NativeMethods.WINTRUST_FILE_INFO
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(NativeMethods.WINTRUST_FILE_INFO)),
                    pcwszFilePath = pFilePath,
                    hFile = IntPtr.Zero,
                    pgKnownSubject = IntPtr.Zero
                };

                pFileInfo = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(NativeMethods.WINTRUST_FILE_INFO)));
                Marshal.StructureToPtr(fileInfo, pFileInfo, false);

                wvtData.cbStruct = (uint)Marshal.SizeOf(typeof(NativeMethods.WINTRUST_DATA));
                wvtData.dwUIChoice = 2u;          // WTD_UI_NONE
                wvtData.fdwRevocationChecks = 0u; // WTD_REVOKE_NONE (fast local chain check)
                wvtData.dwUnionChoice = 1u;       // WTD_CHOICE_FILE
                wvtData.pFile = pFileInfo;
                wvtData.dwStateAction = 1u;       // WTD_STATEACTION_VERIFY
                wvtData.dwProvFlags = 0x00000010u; // WTD_CACHE_ONLY_URL_RETRIEVAL

                uint status = NativeMethods.WinVerifyTrust(
                    new IntPtr(-1),
                    NativeMethods.WINTRUST_ACTION_GENERIC_VERIFY_V2,
                    ref wvtData);
                stateOpened = true;

                string subject = "";
                try
                {
                    X509Certificate cert = X509Certificate.CreateFromSignedFile(filePath);
                    if (cert != null)
                    {
                        subject = cert.Subject ?? "";
                        cert.Reset();
                    }
                }
                catch
                {
                }

                HResultInfo hrInfo = ProblemCodeCatalog.DescribeInstallHResult(status);
                return new SignatureCheckResult
                {
                    IsTrusted = (status == 0u),
                    WinTrustStatus = status,
                    SignerSubject = subject,
                    Summary = status == 0u ? "Trusted Authenticode Signature (" + subject + ")" : hrInfo.Explanation
                };
            }
            finally
            {
                if (stateOpened)
                {
                    wvtData.dwStateAction = 2u; // WTD_STATEACTION_CLOSE
                    NativeMethods.WinVerifyTrust(
                        new IntPtr(-1),
                        NativeMethods.WINTRUST_ACTION_GENERIC_VERIFY_V2,
                        ref wvtData);
                }
                if (pFileInfo != IntPtr.Zero) Marshal.FreeCoTaskMem(pFileInfo);
                if (pFilePath != IntPtr.Zero) Marshal.FreeCoTaskMem(pFilePath);
            }
        }

        public static Task<DriverInstallResult> DownloadBackupAndInstallAsync(
            HardwareDevice device,
            DriverStoreReport storeReport,
            bool createRestorePoint,
            Action<string> statusCallback)
        {
            return DownloadBackupAndInstallAsync(device, storeReport, createRestorePoint, true, statusCallback);
        }

        public static async Task<DriverInstallResult> DownloadBackupAndInstallAsync(
            HardwareDevice device,
            DriverStoreReport storeReport,
            bool createRestorePoint,
            bool autoBackupZip,
            Action<string> statusCallback)
        {
            if (device == null || device.RecommendedUpdate == null)
            {
                return new DriverInstallResult
                {
                    Succeeded = false,
                    SummaryMessage = "No recommended update is attached to this device."
                };
            }

            if (!IsCurrentProcessElevated())
            {
                return new DriverInstallResult
                {
                    Succeeded = false,
                    SummaryMessage = "Administrator elevation required: click 'Relaunch as Admin' in the left rail before installing DriverStore packages."
                };
            }

            DriverCandidate cand = device.RecommendedUpdate;
            string cabUrl = cand.DownloadUrl;
            if (string.IsNullOrWhiteSpace(cabUrl) && !string.IsNullOrWhiteSpace(cand.UpdateId))
            {
                if (statusCallback != null) statusCallback("Resolving official Microsoft Update .cab URL...");
                cabUrl = await WindowsUpdateCatalogClient.ResolveCabDownloadUrlAsync(cand.UpdateId).ConfigureAwait(false);
                cand.DownloadUrl = cabUrl;
            }

            if (!WindowsUpdateCatalogClient.IsOfficialMicrosoftHttpsCabUrl(cabUrl))
            {
                return new DriverInstallResult
                {
                    Succeeded = false,
                    SummaryMessage = "Blocked: package URL did not resolve to an official HTTPS *.windowsupdate.com/.microsoft.com CAB."
                };
            }

            // 1. Pre-install safety checkpoints
            if (createRestorePoint)
            {
                if (statusCallback != null) statusCallback("Creating Windows System Restore checkpoint...");
                string rpMsg;
                RescueBackupService.CreateSystemRestorePoint("Driver+ Update: " + device.DeviceName, out rpMsg);
            }

            string backupZip = "";
            if (autoBackupZip && storeReport != null && !string.IsNullOrWhiteSpace(device.InfName))
            {
                foreach (DriverStorePackage pkg in storeReport.Packages)
                {
                    if (string.Equals(pkg.PublishedName, device.InfName, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(pkg.RepositoryFolder) &&
                        Directory.Exists(pkg.RepositoryFolder))
                    {
                        if (statusCallback != null) statusCallback("Backing up current OEM DriverStore package to ZIP...");
                        BackupRecord rec = RescueBackupService.CreateDriverBackupZip(device, pkg.RepositoryFolder, null);
                        backupZip = rec.ZipFilePath;
                        break;
                    }
                }
            }

            string stageParent = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                @"DriverPlus\Stage");
            Directory.CreateDirectory(stageParent);
            string stageRoot = Path.Combine(stageParent, "Stage_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stageRoot);
            string cabPath = Path.Combine(stageRoot, "driver_package.cab");
            string extractDir = Path.Combine(stageRoot, "unpacked");
            Directory.CreateDirectory(extractDir);

            try
            {
                if (statusCallback != null) statusCallback("Downloading WHQL package from Microsoft Windows Update CDN...");
                string finalUri = cabUrl;
                using (HttpResponseMessage resp = await SharedDownloadHttp.GetAsync(cabUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    if (resp.RequestMessage != null && resp.RequestMessage.RequestUri != null)
                    {
                        finalUri = resp.RequestMessage.RequestUri.AbsoluteUri;
                    }
                    if (!WindowsUpdateCatalogClient.IsOfficialMicrosoftHttpsCabUrl(finalUri))
                    {
                        return new DriverInstallResult
                        {
                            Succeeded = false,
                            BackupZipPath = backupZip,
                            SummaryMessage = "Blocked: HTTP redirect left official Microsoft Update domain."
                        };
                    }

                    using (Stream net = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (FileStream fs = File.Create(cabPath))
                    {
                        byte[] buffer = new byte[65536];
                        long totalRead = 0;
                        while (true)
                        {
                            int n = await net.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                            if (n <= 0) break;
                            totalRead += n;
                            if (totalRead > MaxCabBytes)
                            {
                                throw new InvalidDataException("CAB download exceeded 1.5 GiB safety ceiling.");
                            }
                            await fs.WriteAsync(buffer, 0, n).ConfigureAwait(false);
                        }
                    }
                }

                // 2. Fail-closed verification of 40-hex SHA-1 embedded in Microsoft Update CAB URL
                string expectedSha1 = ExtractExpectedSha1FromCabUrl(finalUri);
                if (string.IsNullOrEmpty(expectedSha1))
                {
                    expectedSha1 = ExtractExpectedSha1FromCabUrl(cabUrl);
                }
                if (expectedSha1.Length != 40)
                {
                    return new DriverInstallResult
                    {
                        Succeeded = false,
                        BackupZipPath = backupZip,
                        SummaryMessage = "Blocked: Microsoft Update CAB URL did not carry a verifiable 40-hex SHA-1 manifest digest."
                    };
                }
                string actualSha1 = ComputeSha1Hex(cabPath);
                if (!string.Equals(expectedSha1, actualSha1, StringComparison.OrdinalIgnoreCase))
                {
                    return new DriverInstallResult
                    {
                        Succeeded = false,
                        BackupZipPath = backupZip,
                        SummaryMessage = "Blocked: downloaded CAB SHA-1 digest mismatch against Microsoft Update manifest."
                    };
                }

                // 3. Verify Authenticode signature on downloaded .cab
                if (statusCallback != null) statusCallback("Verifying Microsoft Authenticode signature...");
                SignatureCheckResult sig = VerifyAuthenticodeFile(cabPath);
                if (!sig.IsTrusted)
                {
                    return new DriverInstallResult
                    {
                        Succeeded = false,
                        BackupZipPath = backupZip,
                        SummaryMessage = "Blocked by WinVerifyTrust: " + sig.Summary
                    };
                }

                // 4. Expand CAB with Windows inbox expand.exe (no redirected stdout pipe to avoid 4KB buffer deadlock)
                if (statusCallback != null) statusCallback("Extracting signed INF & kernel binaries...");
                string expandExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "expand.exe");
                var psi = new ProcessStartInfo
                {
                    FileName = expandExe,
                    Arguments = string.Format("\"{0}\" -F:* \"{1}\"", cabPath, extractDir),
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = true
                };
                using (Process proc = Process.Start(psi))
                {
                    if (proc == null)
                    {
                        return new DriverInstallResult { Succeeded = false, BackupZipPath = backupZip, SummaryMessage = "Failed starting expand.exe." };
                    }
                    if (!proc.WaitForExit(120000))
                    {
                        try { proc.Kill(); } catch { }
                        return new DriverInstallResult { Succeeded = false, BackupZipPath = backupZip, SummaryMessage = "CAB extraction timed out." };
                    }
                    if (proc.ExitCode != 0)
                    {
                        return new DriverInstallResult { Succeeded = false, BackupZipPath = backupZip, SummaryMessage = "expand.exe failed with exit code " + proc.ExitCode + "." };
                    }
                }

                string[] infFiles = Directory.GetFiles(extractDir, "*.inf", SearchOption.AllDirectories);
                if (infFiles.Length == 0)
                {
                    return new DriverInstallResult
                    {
                        Succeeded = false,
                        BackupZipPath = backupZip,
                        SummaryMessage = "Expanded CAB contained no .inf files."
                    };
                }

                // 5. Install via native 64-bit newdev.dll!DiInstallDriverW
                if (statusCallback != null) statusCallback("Installing via native 64-bit DiInstallDriverW...");
                int installedCount = 0;
                bool anyReboot = false;
                uint lastErr = 0;

                foreach (string inf in infFiles)
                {
                    bool reboot;
                    if (NativeMethods.DiInstallDriverW(IntPtr.Zero, inf, NativeMethods.DIIRFLAG_FORCE_INF, out reboot))
                    {
                        installedCount++;
                        if (reboot) anyReboot = true;
                    }
                    else
                    {
                        lastErr = (uint)Marshal.GetLastWin32Error();
                    }
                }

                if (installedCount > 0)
                {
                    if (device.DevInstHandle != 0)
                    {
                        NativeMethods.CM_Reenumerate_DevNode(device.DevInstHandle, NativeMethods.CM_REENUMERATE_NORMAL);
                    }
                    SettingsService.AppendHistoryEntry(string.Format(
                        "{0:yyyy-MM-dd HH:mm} | UPDATED | {1} | {2} -> {3}",
                        DateTime.Now, device.DeviceName, device.DriverVersion, cand.TargetVersion));

                    return new DriverInstallResult
                    {
                        Succeeded = true,
                        RebootRequired = anyReboot,
                        BackupZipPath = backupZip,
                        InstalledInfCount = installedCount.ToString(CultureInfo.InvariantCulture),
                        SummaryMessage = anyReboot
                            ? "Installed " + cand.TargetVersion + " cleanly. Please restart Windows to complete activation."
                            : "Installed " + cand.TargetVersion + " cleanly and re-enumerated hardware."
                    };
                }

                HResultInfo errInfo = ProblemCodeCatalog.DescribeInstallHResult(lastErr);
                return new DriverInstallResult
                {
                    Succeeded = false,
                    BackupZipPath = backupZip,
                    SummaryMessage = string.Format("Install did not complete ({0}): {1}", errInfo.SymbolicName, errInfo.Explanation)
                };
            }
            catch (Exception ex)
            {
                return new DriverInstallResult
                {
                    Succeeded = false,
                    BackupZipPath = backupZip,
                    SummaryMessage = "Install failed: " + ex.Message
                };
            }
            finally
            {
                try { Directory.Delete(stageRoot, true); } catch { }
            }
        }

        private static string ComputeSha1Hex(string filePath)
        {
            using (FileStream fs = File.OpenRead(filePath))
            using (SHA1 sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(fs);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                {
                    sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                }
                return sb.ToString();
            }
        }
    }
}
