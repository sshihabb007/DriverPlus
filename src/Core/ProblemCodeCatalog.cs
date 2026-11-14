using System;
using System.Collections.Generic;

namespace DriverPlus.Core
{
    public static class ProblemCodeCatalog
    {
        private static readonly Dictionary<int, ProblemCodeInfo> Codes = BuildCodes();
        private static readonly Dictionary<uint, HResultInfo> HResults = BuildHResults();

        private static Dictionary<int, ProblemCodeInfo> BuildCodes()
        {
            var d = new Dictionary<int, ProblemCodeInfo>();
            Add(d, 0, "CM_PROB_NONE", "Healthy", "Device is working properly.", "No action required.", false);
            Add(d, 1, "CM_PROB_NOT_CONFIGURED", "Not Configured", "This device is not configured correctly.", "Scan and install a matching signed INF driver.", true);
            Add(d, 3, "CM_PROB_CORRUPTED", "Corrupted Driver", "The driver for this device might be corrupted, or your system may be running low on memory.", "Back up and reinstall the signed driver package.", true);
            Add(d, 10, "CM_PROB_FAILED_START", "Failed to Start", "This device cannot start (Code 10).", "Reset the PnP device node or update to a newer WHQL driver.", true);
            Add(d, 12, "CM_PROB_NORMAL_CONFLICT", "Resource Conflict", "This device cannot find enough free system resources.", "Check BIOS PCIe/ACPI settings or disable conflicting unused devices.", false);
            Add(d, 14, "CM_PROB_NEED_RESTART", "Restart Required", "This device cannot work properly until you restart your computer.", "Save open work and restart Windows to complete driver binding.", false);
            Add(d, 18, "CM_PROB_REINSTALL", "Reinstall Needed", "Reinstall the drivers for this device.", "Run Driver+ clean driver reinstall for this hardware ID.", true);
            Add(d, 19, "CM_PROB_REGISTRY", "Registry Filter Error", "Windows cannot start this hardware device because its configuration information (UpperFilters/LowerFilters) in the registry is incomplete or damaged.", "Audit class UpperFilters/LowerFilters and re-enumerate the device node.", true);
            Add(d, 22, "CM_PROB_DISABLED", "Disabled", "This device is disabled in Device Manager.", "Re-enable the device node via SetupAPI property change.", true);
            Add(d, 24, "CM_PROB_DEVICE_NOT_THERE", "Disconnected", "This device is not present, is not working properly, or does not have all its drivers installed.", "Use Clean Unplugged Devices if hardware was permanently removed.", false);
            Add(d, 28, "CM_PROB_FAILED_INSTALL", "Missing Driver", "The drivers for this device are not installed (Code 28).", "Query Microsoft Update Catalog and install the verified WHQL driver.", true);
            Add(d, 31, "CM_PROB_FAILED_ADD", "Driver Load Failed", "This device is not working properly because Windows cannot load the drivers required for this device.", "Install an OS-compatible Windows 10/11 x64 signed driver.", true);
            Add(d, 32, "CM_PROB_DISABLED_SERVICE", "Service Disabled", "A driver (service) for this device has been disabled.", "Restore the backing kernel driver service start type.", true);
            Add(d, 37, "CM_PROB_FAILED_DRIVER_ENTRY", "DriverEntry Failed", "Windows cannot initialize the device driver for this hardware (Code 37).", "Roll back or update the kernel driver package.", true);
            Add(d, 39, "CM_PROB_DRIVER_FAILED_LOAD", "Corrupt/Missing .sys", "Windows cannot load the device driver for this hardware. The driver may be corrupted or missing.", "Reinstall the WHQL driver package from DriverStore or Microsoft Update.", true);
            Add(d, 43, "CM_PROB_FAILED_POST_START", "Device Reported Fault", "Windows has stopped this device because it has reported problems (Code 43).", "Perform a clean driver reinstall and reset the USB/PCIe port power state.", true);
            Add(d, 45, "CM_PROB_PHANTOM", "Unplugged / Ghost", "Currently, this hardware device is not connected to the computer (Code 45).", "Safe to keep or prune with Clean Unplugged Ghost Devices.", false);
            Add(d, 48, "CM_PROB_DRIVER_BLOCKED", "Blocked Incompatible", "The software for this device has been blocked from starting because it is known to have problems with Windows.", "Check HVCI Memory Integrity compatibility and update to a WHQL x64 driver.", false);
            Add(d, 52, "CM_PROB_UNSIGNED_DRIVER", "Invalid Digital Signature", "Windows cannot verify the digital signature for the drivers required for this device (Code 52).", "Replace with an Authenticode WHQL-signed driver package.", true);
            return d;
        }

        private static void Add(
            Dictionary<int, ProblemCodeInfo> dict,
            int code,
            string sym,
            string shortStatus,
            string summary,
            string action,
            bool autoReset)
        {
            dict[code] = new ProblemCodeInfo
            {
                Code = code,
                SymbolicName = sym,
                ShortStatus = shortStatus,
                Summary = summary,
                RecommendedAction = action,
                CanAutoResetDevNode = autoReset
            };
        }

        private static Dictionary<uint, HResultInfo> BuildHResults()
        {
            var d = new Dictionary<uint, HResultInfo>();
            AddHr(d, 0x00000000u, "S_OK", "Success", "Driver package staged and installed cleanly.", false);
            AddHr(d, 0x00000BC2u, "ERROR_SUCCESS_REBOOT_REQUIRED", "RebootRequired", "Driver installed cleanly; Windows requires a system restart to activate it.", true);
            AddHr(d, 0x00240005u, "WU_S_REBOOT_REQUIRED", "RebootRequired", "Windows Update driver installation succeeded; restart required.", true);
            AddHr(d, 0x00000103u, "ERROR_NO_MORE_ITEMS", "AlreadyCurrent", "Windows confirmed the currently installed driver is already the best match.", false);
            AddHr(d, 0xE000020Bu, "ERROR_NO_SUCH_DEVINST", "StageOnly", "Driver staged into DriverStore; target hardware instance is currently unplugged.", false);
            AddHr(d, 0xE000022Fu, "ERROR_NO_CATALOG_FOR_OEM_INF", "SignatureBlocked", "INF package lacks a valid WHQL security catalog (.cat); blocked by Driver+ fail-closed policy.", false);
            AddHr(d, 0xE0000247u, "ERROR_DRIVER_STORE_ADD_FAILED", "StoreRejected", "Windows DriverStore rejected adding the INF package (check architecture and OS build).", false);
            AddHr(d, 0x80240017u, "WU_E_NOT_APPLICABLE", "NotApplicable", "Update is not applicable to any present hardware revision.", false);
            AddHr(d, 0x800B0100u, "TRUST_E_NOSIGNATURE", "SignatureBlocked", "No Authenticode digital signature was present in the driver package.", false);
            AddHr(d, 0x800B0109u, "CERT_E_UNTRUSTEDROOT", "SignatureBlocked", "Driver certificate chain terminates in an untrusted root authority.", false);
            AddHr(d, 0x80070005u, "E_ACCESSDENIED", "ElevationRequired", "Administrator privileges are required to modify the Windows DriverStore.", false);
            return d;
        }

        private static void AddHr(
            Dictionary<uint, HResultInfo> dict,
            uint hr,
            string sym,
            string cat,
            string explanation,
            bool reboot)
        {
            dict[hr] = new HResultInfo
            {
                HResult = hr,
                HexCode = string.Format("0x{0:X8}", hr),
                SymbolicName = sym,
                OutcomeCategory = cat,
                Explanation = explanation,
                RebootRequired = reboot
            };
        }

        public static ProblemCodeInfo DescribeProblemCode(int code)
        {
            ProblemCodeInfo info;
            if (Codes.TryGetValue(code, out info))
            {
                return info;
            }
            return new ProblemCodeInfo
            {
                Code = code,
                SymbolicName = "CM_PROB_" + code,
                ShortStatus = "Code " + code,
                Summary = string.Format("Windows Configuration Manager reported problem code {0}.", code),
                RecommendedAction = "Re-enumerate the device node or install a fresh WHQL driver.",
                CanAutoResetDevNode = true
            };
        }

        public static HResultInfo DescribeInstallHResult(uint hr)
        {
            HResultInfo info;
            if (HResults.TryGetValue(hr, out info))
            {
                return info;
            }
            if (hr != 0u && hr < 0x00010000u && HResults.TryGetValue(0x80070000u | hr, out info))
            {
                return info;
            }
            return new HResultInfo
            {
                HResult = hr,
                HexCode = string.Format("0x{0:X8}", hr),
                SymbolicName = "HRESULT_UNKNOWN",
                OutcomeCategory = "Failed",
                Explanation = string.Format("Installer returned status 0x{0:X8}.", hr),
                RebootRequired = false
            };
        }
    }
}
