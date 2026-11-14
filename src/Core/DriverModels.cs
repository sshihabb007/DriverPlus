using System;
using System.Collections.Generic;

namespace DriverPlus.Core
{
    public sealed class HardwareDevice
    {
        public string InstanceId { get; set; }
        public uint DevInstHandle { get; set; }
        public string DeviceName { get; set; }
        public string DeviceClass { get; set; }
        public string ClassGuid { get; set; }
        public string Manufacturer { get; set; }
        public string ProviderName { get; set; }
        public string DriverVersion { get; set; }
        public string DriverDate { get; set; }
        public string InfName { get; set; }
        public string MatchingDeviceId { get; set; }
        public string DigitalSigner { get; set; }
        public bool IsWhqlSigned { get; set; }
        public bool IsPresent { get; set; }
        public int ProblemCode { get; set; }
        public string ProblemSummary { get; set; }
        public string ProblemRemediation { get; set; }
        public string VendorSupportName { get; set; }
        public string SubsystemOemName { get; set; }
        public string VendorSupportUrl { get; set; }
        public bool IsIgnored { get; set; }
        public List<string> HardwareIds { get; set; }
        public List<string> CompatibleIds { get; set; }
        public DriverCandidate RecommendedUpdate { get; set; }

        public string PrimaryHardwareId
        {
            get
            {
                if (HardwareIds != null && HardwareIds.Count > 0)
                {
                    return HardwareIds[0];
                }
                return MatchingDeviceId ?? "";
            }
        }

        public bool HasUpdate
        {
            get { return RecommendedUpdate != null && !IsIgnored; }
        }

        public bool HasFault
        {
            get { return IsPresent && ProblemCode != 0; }
        }

        public string HealthBadge
        {
            get
            {
                if (!IsPresent) return "Unplugged";
                if (ProblemCode == 28) return "Missing Driver";
                if (ProblemCode != 0) return "Fault (Code " + ProblemCode + ")";
                if (HasUpdate) return "Update Available";
                return "Up to Date";
            }
        }

        public HardwareDevice()
        {
            InstanceId = "";
            DeviceName = "Unknown Device";
            DeviceClass = "Other";
            ClassGuid = "";
            Manufacturer = "";
            ProviderName = "";
            DriverVersion = "";
            DriverDate = "";
            InfName = "";
            MatchingDeviceId = "";
            DigitalSigner = "";
            ProblemSummary = "Healthy";
            ProblemRemediation = "";
            VendorSupportName = "";
            SubsystemOemName = "";
            VendorSupportUrl = "";
            IsPresent = true;
            HardwareIds = new List<string>();
            CompatibleIds = new List<string>();
        }
    }

    public sealed class DriverCandidate
    {
        public string UpdateId { get; set; }
        public string Title { get; set; }
        public string TargetVersion { get; set; }
        public string TargetDate { get; set; }
        public string Classification { get; set; }
        public string SupportedProducts { get; set; }
        public string SizeText { get; set; }
        public long SizeBytes { get; set; }
        public string MatchedHardwareId { get; set; }
        public int MatchSpecificity { get; set; }
        public bool WhqlSigned { get; set; }
        public bool RebootRequired { get; set; }
        public string Source { get; set; }
        public string DownloadUrl { get; set; }

        public DriverCandidate()
        {
            UpdateId = "";
            Title = "";
            TargetVersion = "";
            TargetDate = "";
            Classification = "Drivers";
            SupportedProducts = "";
            SizeText = "";
            MatchedHardwareId = "";
            Source = "Microsoft Update Catalog";
            DownloadUrl = "";
        }
    }

    public sealed class ProblemCodeInfo
    {
        public int Code { get; set; }
        public string SymbolicName { get; set; }
        public string ShortStatus { get; set; }
        public string Summary { get; set; }
        public string RecommendedAction { get; set; }
        public bool CanAutoResetDevNode { get; set; }
    }

    public sealed class HResultInfo
    {
        public uint HResult { get; set; }
        public string HexCode { get; set; }
        public string SymbolicName { get; set; }
        public string OutcomeCategory { get; set; }
        public string Explanation { get; set; }
        public bool RebootRequired { get; set; }
    }

    public sealed class VendorSupportInfo
    {
        public string VendorId { get; set; }
        public string VendorName { get; set; }
        public string SubsystemOemId { get; set; }
        public string SubsystemOemName { get; set; }
        public string SupportUrl { get; set; }
    }

    public sealed class DriverStorePackage
    {
        public string PublishedName { get; set; }
        public string OriginalInfName { get; set; }
        public string ProviderName { get; set; }
        public string ClassName { get; set; }
        public string DriverVersion { get; set; }
        public string DriverDate { get; set; }
        public string SignerName { get; set; }
        public string RepositoryFolder { get; set; }
        public List<string> SysFiles { get; set; }
        public bool FlagsHvciIncompatible { get; set; }
        public string HvciReason { get; set; }

        public DriverStorePackage()
        {
            PublishedName = "";
            OriginalInfName = "";
            ProviderName = "";
            ClassName = "";
            DriverVersion = "";
            DriverDate = "";
            SignerName = "";
            RepositoryFolder = "";
            SysFiles = new List<string>();
            HvciReason = "";
        }
    }

    public sealed class DriverStoreReport
    {
        public bool HvciEnabledInRegistry { get; set; }
        public List<DriverStorePackage> Packages { get; set; }
        public List<DriverStorePackage> HvciBlockers { get; set; }

        public DriverStoreReport()
        {
            Packages = new List<DriverStorePackage>();
            HvciBlockers = new List<DriverStorePackage>();
        }
    }

    public sealed class UpdateScanSummary
    {
        public DateTime ScannedAtUtc { get; set; }
        public long ElapsedMs { get; set; }
        public int TotalPresentDevices { get; set; }
        public int GhostDeviceCount { get; set; }
        public int UpToDateCount { get; set; }
        public int OutdatedCount { get; set; }
        public int FaultyDeviceCount { get; set; }
        public int WuaCandidateCount { get; set; }
        public int CatalogCandidateCount { get; set; }
        public int HvciBlockerCount { get; set; }
        public List<HardwareDevice> Devices { get; set; }

        public UpdateScanSummary()
        {
            ScannedAtUtc = DateTime.UtcNow;
            Devices = new List<HardwareDevice>();
        }
    }

    public sealed class RuntimeComponent
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public string Architecture { get; set; }
        public string InstalledVersion { get; set; }
        public string LatestVersion { get; set; }
        public bool IsInstalled { get; set; }
        public bool NeedsUpdate { get; set; }
        public string WingetId { get; set; }
        public string OfficialDownloadUrl { get; set; }
        public string Notes { get; set; }

        public RuntimeComponent()
        {
            Id = "";
            Name = "";
            Category = "Visual C++";
            Architecture = "x64";
            InstalledVersion = "";
            LatestVersion = "";
            WingetId = "";
            OfficialDownloadUrl = "";
            Notes = "";
        }
    }

    public sealed class CrashDllEvent
    {
        public DateTime TimeGenerated { get; set; }
        public string ApplicationName { get; set; }
        public string FaultingModule { get; set; }
        public string RecommendedRuntimeId { get; set; }
        public string RecommendedRuntimeName { get; set; }
    }

    public sealed class DiagnosticFinding
    {
        public string ToolId { get; set; }
        public string ToolName { get; set; }
        public string Severity { get; set; }
        public string StatusText { get; set; }
        public string Detail { get; set; }
        public string ActionLabel { get; set; }
        public bool IssueDetected { get; set; }
    }

    public sealed class BackupRecord
    {
        public string BackupId { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public string DeviceName { get; set; }
        public string HardwareId { get; set; }
        public string InfName { get; set; }
        public string DriverVersion { get; set; }
        public string ZipFilePath { get; set; }
        public string Sha256 { get; set; }
        public long SizeBytes { get; set; }
    }

    public sealed class OfflineHardwareManifest
    {
        public string SchemaVersion { get; set; }
        public DateTime ExportedAtUtc { get; set; }
        public string MachineName { get; set; }
        public string OsCaption { get; set; }
        public int OsBuildNumber { get; set; }
        public List<HardwareDevice> Devices { get; set; }

        public OfflineHardwareManifest()
        {
            SchemaVersion = "1.0";
            ExportedAtUtc = DateTime.UtcNow;
            MachineName = "";
            OsCaption = "";
            Devices = new List<HardwareDevice>();
        }
    }

    public sealed class SignatureCheckResult
    {
        public bool IsTrusted { get; set; }
        public uint WinTrustStatus { get; set; }
        public string SignerSubject { get; set; }
        public string Summary { get; set; }
    }

    public sealed class BoostSnapshot
    {
        public DateTime ActivatedAtUtc { get; set; }
        public string PreviousPowerSchemeGuid { get; set; }
        public string ActiveBoostSchemeGuid { get; set; }
        public List<string> StoppedServices { get; set; }
        public bool SilentFocusActive { get; set; }

        public BoostSnapshot()
        {
            ActivatedAtUtc = DateTime.UtcNow;
            PreviousPowerSchemeGuid = "";
            ActiveBoostSchemeGuid = "";
            StoppedServices = new List<string>();
        }
    }

    public sealed class SystemProfile
    {
        public string ComputerName { get; set; }
        public string Manufacturer { get; set; }
        public string Model { get; set; }
        public string BiosVersion { get; set; }
        public string OsCaption { get; set; }
        public string OsVersion { get; set; }
        public int OsBuildNumber { get; set; }
        public string Architecture { get; set; }
        public string CpuName { get; set; }
        public int CpuCores { get; set; }
        public int CpuLogicalProcessors { get; set; }
        public double TotalRamGb { get; set; }
        public double AvailableRamGb { get; set; }
        public List<string> Gpus { get; set; }
        public List<string> StorageDrives { get; set; }
        public List<string> NetworkAdapters { get; set; }

        public SystemProfile()
        {
            ComputerName = Environment.MachineName;
            Manufacturer = "";
            Model = "";
            BiosVersion = "";
            OsCaption = "";
            OsVersion = "";
            Architecture = Environment.Is64BitOperatingSystem ? "64-bit (x64)" : "32-bit (x86)";
            CpuName = "";
            Gpus = new List<string>();
            StorageDrives = new List<string>();
            NetworkAdapters = new List<string>();
        }
    }
}
