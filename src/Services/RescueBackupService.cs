using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using DriverPlus.Core;
using DriverPlus.Native;

namespace DriverPlus.Services
{
    public static class RescueBackupService
    {
        public static string GetDefaultBackupDirectory()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"DriverPlus\Backups");
            Directory.CreateDirectory(dir);
            return dir;
        }

        public static bool ValidateSafeArchiveEntry(string entryFullName)
        {
            if (string.IsNullOrWhiteSpace(entryFullName))
            {
                return false;
            }
            if (entryFullName.IndexOf(':') >= 0)
            {
                return false;
            }
            if (entryFullName.StartsWith("/", StringComparison.Ordinal) ||
                entryFullName.StartsWith("\\", StringComparison.Ordinal))
            {
                return false;
            }

            string normalized = entryFullName.Replace('\\', '/');
            string[] segments = normalized.Split('/');
            foreach (string seg in segments)
            {
                if (seg == "..")
                {
                    return false;
                }
                foreach (char ch in seg)
                {
                    if (char.IsControl(ch))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        public static BackupRecord CreateDriverBackupZip(
            HardwareDevice device,
            string sourceDriverFolder,
            string backupRootDir)
        {
            if (device == null) throw new ArgumentNullException("device");
            if (string.IsNullOrWhiteSpace(sourceDriverFolder) || !Directory.Exists(sourceDriverFolder))
            {
                throw new DirectoryNotFoundException("Driver package folder not found: " + sourceDriverFolder);
            }

            string targetRoot = string.IsNullOrWhiteSpace(backupRootDir) ? GetDefaultBackupDirectory() : backupRootDir;
            Directory.CreateDirectory(targetRoot);

            string safeName = Regex.Replace(device.DeviceName ?? "Driver", @"[^A-Za-z0-9._\-]+", "_").Trim('_');
            if (safeName.Length > 36) safeName = safeName.Substring(0, 36);
            string safeVer = Regex.Replace(device.DriverVersion ?? "0.0", @"[^0-9.]+", "");
            if (string.IsNullOrEmpty(safeVer)) safeVer = "0.0";

            string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string id = stamp + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
            string zipPath = Path.Combine(targetRoot, string.Format("{0}_{1}_{2}.zip", stamp, safeName, safeVer));

            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }
            ZipFile.CreateFromDirectory(sourceDriverFolder, zipPath, CompressionLevel.Optimal, false);

            string sha256 = ComputeSha256Hex(zipPath);
            long sizeBytes = new FileInfo(zipPath).Length;

            var record = new BackupRecord
            {
                BackupId = id,
                CreatedAtUtc = DateTime.UtcNow,
                DeviceName = device.DeviceName ?? "",
                HardwareId = device.PrimaryHardwareId,
                InfName = device.InfName ?? "",
                DriverVersion = device.DriverVersion ?? "",
                ZipFilePath = zipPath,
                Sha256 = sha256,
                SizeBytes = sizeBytes
            };

            SaveBackupMetadataSidecar(record);
            return record;
        }

        public static void ExtractBackupZipSafely(string zipFilePath, string destinationFolder)
        {
            if (string.IsNullOrWhiteSpace(zipFilePath) || !File.Exists(zipFilePath))
            {
                throw new FileNotFoundException("Backup archive not found.", zipFilePath);
            }

            Directory.CreateDirectory(destinationFolder);
            string fullDestRoot = Path.GetFullPath(destinationFolder);
            if (!fullDestRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                fullDestRoot += Path.DirectorySeparatorChar;
            }

            using (ZipArchive archive = ZipFile.OpenRead(zipFilePath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith("/", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    if (!ValidateSafeArchiveEntry(entry.FullName))
                    {
                        throw new InvalidDataException("Blocked unsafe archive entry path: " + entry.FullName);
                    }

                    string targetPath = Path.GetFullPath(Path.Combine(fullDestRoot, entry.FullName));
                    if (!targetPath.StartsWith(fullDestRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("Blocked Zip-Slip traversal outside destination: " + entry.FullName);
                    }

                    string parentDir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(parentDir))
                    {
                        Directory.CreateDirectory(parentDir);
                    }
                    entry.ExtractToFile(targetPath, true);
                }
            }
        }

        public static List<BackupRecord> ListBackups(string backupRootDir)
        {
            var records = new List<BackupRecord>();
            string root = string.IsNullOrWhiteSpace(backupRootDir) ? GetDefaultBackupDirectory() : backupRootDir;
            if (!Directory.Exists(root))
            {
                return records;
            }

            var serializer = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
            string[] zipFiles = Directory.GetFiles(root, "*.zip", SearchOption.TopDirectoryOnly);
            foreach (string zf in zipFiles)
            {
                string metaPath = zf + ".meta.json";
                if (File.Exists(metaPath))
                {
                    try
                    {
                        BackupRecord rec = serializer.Deserialize<BackupRecord>(File.ReadAllText(metaPath, Encoding.UTF8));
                        if (rec != null)
                        {
                            rec.ZipFilePath = zf; // Pin ZipFilePath to the discovered archive inside root
                            records.Add(rec);
                            continue;
                        }
                    }
                    catch
                    {
                    }
                }

                var fi = new FileInfo(zf);
                records.Add(new BackupRecord
                {
                    BackupId = Path.GetFileNameWithoutExtension(zf),
                    CreatedAtUtc = fi.CreationTimeUtc,
                    DeviceName = Path.GetFileNameWithoutExtension(zf),
                    ZipFilePath = zf,
                    SizeBytes = fi.Length,
                    Sha256 = ComputeSha256Hex(zf)
                });
            }

            records.Sort((a, b) => b.CreatedAtUtc.CompareTo(a.CreatedAtUtc));
            return records;
        }

        public static bool RestoreDriverFromBackupZip(string zipFilePath, out bool needReboot, out string message)
        {
            needReboot = false;
            if (string.IsNullOrWhiteSpace(zipFilePath) || !File.Exists(zipFilePath))
            {
                message = "Backup ZIP archive was not found.";
                return false;
            }

            if (!DriverInstallOrchestrator.IsCurrentProcessElevated())
            {
                message = "Administrator elevation required: click 'Relaunch as Admin' before restoring DriverStore packages.";
                return false;
            }

            // Verify sidecar SHA-256 integrity if sidecar exists
            string metaPath = zipFilePath + ".meta.json";
            if (File.Exists(metaPath))
            {
                try
                {
                    var serializer = new JavaScriptSerializer();
                    BackupRecord rec = serializer.Deserialize<BackupRecord>(File.ReadAllText(metaPath, Encoding.UTF8));
                    if (rec != null && !string.IsNullOrWhiteSpace(rec.Sha256))
                    {
                        string actualSha = ComputeSha256Hex(zipFilePath);
                        if (!string.Equals(rec.Sha256, actualSha, StringComparison.OrdinalIgnoreCase))
                        {
                            message = "Blocked: backup ZIP SHA-256 digest does not match recorded sidecar metadata.";
                            return false;
                        }
                    }
                }
                catch
                {
                }
            }

            string stageParent = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                @"DriverPlus\Stage");
            Directory.CreateDirectory(stageParent);
            string stageDir = Path.Combine(stageParent, "Restore_" + Guid.NewGuid().ToString("N"));
            try
            {
                ExtractBackupZipSafely(zipFilePath, stageDir);
                string[] infs = Directory.GetFiles(stageDir, "*.inf", SearchOption.AllDirectories);
                if (infs.Length == 0)
                {
                    message = "Backup archive did not contain any .inf driver manifest.";
                    return false;
                }

                string[] cats = Directory.GetFiles(stageDir, "*.cat", SearchOption.AllDirectories);
                foreach (string cat in cats)
                {
                    SignatureCheckResult sig = DriverInstallOrchestrator.VerifyAuthenticodeFile(cat);
                    if (!sig.IsTrusted)
                    {
                        message = "Blocked: backup security catalog (.cat) failed WinVerifyTrust (" + sig.Summary + ").";
                        return false;
                    }
                }

                int installed = 0;
                int lastErr = 0;
                foreach (string inf in infs)
                {
                    bool rb;
                    if (NativeMethods.DiInstallDriverW(IntPtr.Zero, inf, NativeMethods.DIIRFLAG_FORCE_INF, out rb))
                    {
                        installed++;
                        if (rb) needReboot = true;
                    }
                    else
                    {
                        lastErr = Marshal.GetLastWin32Error();
                    }
                }

                if (installed > 0)
                {
                    SettingsService.AppendHistoryEntry(string.Format(
                        "{0:yyyy-MM-dd HH:mm} | RESTORED ZIP | {1}",
                        DateTime.Now, Path.GetFileName(zipFilePath)));
                    message = needReboot
                        ? "Restored driver package cleanly. Restart Windows to complete activation."
                        : "Restored driver package cleanly from backup archive.";
                    return true;
                }

                HResultInfo info = ProblemCodeCatalog.DescribeInstallHResult((uint)lastErr);
                message = string.Format("DriverStore restore failed ({0}): {1}", info.SymbolicName, info.Explanation);
                return false;
            }
            catch (Exception ex)
            {
                message = "Restore failed: " + ex.Message;
                return false;
            }
            finally
            {
                try { Directory.Delete(stageDir, true); } catch { }
            }
        }

        public static bool CreateSystemRestorePoint(string description, out string statusMessage)
        {
            var rp = new NativeMethods.RESTOREPOINTINFOW
            {
                dwEventType = 100u,      // BEGIN_SYSTEM_CHANGE
                dwRestorePtType = 10u,   // DEVICE_DRIVER_INSTALL
                llSequenceNumber = 0,
                szDescription = string.IsNullOrWhiteSpace(description) ? "Driver+ Pre-Update Checkpoint" : description
            };
            NativeMethods.STATEMGRSTATUS status;
            bool ok = NativeMethods.SRSetRestorePointW(ref rp, out status);
            if (ok && status.nStatus == 0)
            {
                var endRp = new NativeMethods.RESTOREPOINTINFOW
                {
                    dwEventType = 101u, // END_SYSTEM_CHANGE
                    dwRestorePtType = 10u,
                    llSequenceNumber = status.llSequenceNumber,
                    szDescription = rp.szDescription
                };
                NativeMethods.STATEMGRSTATUS endStatus;
                NativeMethods.SRSetRestorePointW(ref endRp, out endStatus);
                statusMessage = "System Restore Point #" + status.llSequenceNumber + " created.";
                return true;
            }
            statusMessage = string.Format("SRSetRestorePointW returned status {0} (requires Administrator & System Protection enabled).", status.nStatus);
            return false;
        }

        public static bool RollbackDeviceDriver(string instanceId, out bool needReboot, out string message)
        {
            needReboot = false;
            if (string.IsNullOrWhiteSpace(instanceId))
            {
                message = "No device InstanceId specified.";
                return false;
            }

            IntPtr devInfoSet = NativeMethods.SetupDiGetClassDevsW(
                IntPtr.Zero,
                null,
                IntPtr.Zero,
                NativeMethods.DIGCF_ALLCLASSES | NativeMethods.DIGCF_PRESENT);
            if (devInfoSet == IntPtr.Zero || devInfoSet == new IntPtr(-1))
            {
                message = "Failed to open SetupAPI device info set.";
                return false;
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

                    var sb = new StringBuilder(512);
                    uint req;
                    if (NativeMethods.SetupDiGetDeviceInstanceIdW(devInfoSet, ref devData, sb, (uint)sb.Capacity, out req) &&
                        string.Equals(sb.ToString(), instanceId, StringComparison.OrdinalIgnoreCase))
                    {
                        bool ok = NativeMethods.DiRollbackDriver(devInfoSet, ref devData, IntPtr.Zero, 0, out needReboot);
                        if (ok)
                        {
                            message = needReboot
                                ? "Driver rolled back cleanly. Restart Windows to finish."
                                : "Driver rolled back to previous installed version.";
                            return true;
                        }
                        int err = Marshal.GetLastWin32Error();
                        message = string.Format("Windows reported no rollback backup available or access denied (Win32 error {0}).", err);
                        return false;
                    }
                }
            }
            finally
            {
                NativeMethods.SetupDiDestroyDeviceInfoList(devInfoSet);
            }

            message = "Target present device node was not found.";
            return false;
        }

        public static void ExportOfflineProfile(IList<HardwareDevice> devices, SystemProfile sys, string outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentNullException("outputPath");
            var manifest = new OfflineHardwareManifest
            {
                SchemaVersion = "1.0",
                ExportedAtUtc = DateTime.UtcNow,
                MachineName = sys != null ? sys.ComputerName : Environment.MachineName,
                OsCaption = sys != null ? sys.OsCaption : Environment.OSVersion.VersionString,
                OsBuildNumber = sys != null && sys.OsBuildNumber > 0 ? sys.OsBuildNumber : Environment.OSVersion.Version.Build
            };

            if (devices != null)
            {
                foreach (HardwareDevice d in devices)
                {
                    if (d != null && d.IsPresent)
                    {
                        manifest.Devices.Add(d);
                    }
                }
            }

            string dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var serializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
            File.WriteAllText(outputPath, serializer.Serialize(manifest), Encoding.UTF8);
        }

        public static OfflineHardwareManifest ImportOfflineProfile(string inputPath)
        {
            if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
            {
                throw new FileNotFoundException("Offline hardware profile not found.", inputPath);
            }
            var serializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
            OfflineHardwareManifest manifest = serializer.Deserialize<OfflineHardwareManifest>(File.ReadAllText(inputPath, Encoding.UTF8));
            if (manifest != null && manifest.Devices != null)
            {
                foreach (HardwareDevice d in manifest.Devices)
                {
                    if (d == null) continue;
                    Uri parsedUri;
                    bool isSafeHttps = !string.IsNullOrWhiteSpace(d.VendorSupportUrl) &&
                        Uri.TryCreate(d.VendorSupportUrl, UriKind.Absolute, out parsedUri) &&
                        string.Equals(parsedUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
                    if (!isSafeHttps)
                    {
                        VendorSupportInfo resolved = PciVendorResolver.ResolveVendor(d.PrimaryHardwareId, d.ProviderName);
                        d.VendorSupportUrl = resolved.SupportUrl ?? "";
                    }
                }
            }
            return manifest;
        }

        public static string ComputeSha256Hex(string filePath)
        {
            using (FileStream fs = File.OpenRead(filePath))
            using (SHA256 sha = SHA256.Create())
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

        private static void SaveBackupMetadataSidecar(BackupRecord record)
        {
            try
            {
                var serializer = new JavaScriptSerializer();
                File.WriteAllText(record.ZipFilePath + ".meta.json", serializer.Serialize(record), Encoding.UTF8);
            }
            catch
            {
            }
        }
    }
}
