using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using DriverPlus.Core;
using DriverPlus.Native;

namespace DriverPlus.Services
{
    public static class PnpDeviceScanner
    {
        private const uint DN_HAS_PROBLEM = 0x00000400u;
        private const uint DN_PRIVATE_PROBLEM = 0x00008000u;

        public static List<HardwareDevice> ScanDevices(bool includeGhostDevices)
        {
            var results = new List<HardwareDevice>();

            uint flags = NativeMethods.DIGCF_ALLCLASSES | (includeGhostDevices ? 0u : NativeMethods.DIGCF_PRESENT);
            IntPtr devInfoSet = NativeMethods.SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, flags);
            if (devInfoSet == IntPtr.Zero || devInfoSet == new IntPtr(-1))
            {
                return results;
            }

            try
            {
                uint index = 0;
                while (true)
                {
                    var devData = new NativeMethods.SP_DEVINFO_DATA();
                    devData.cbSize = (uint)Marshal.SizeOf(typeof(NativeMethods.SP_DEVINFO_DATA));

                    if (!NativeMethods.SetupDiEnumDeviceInfo(devInfoSet, index, ref devData))
                    {
                        break;
                    }
                    index++;

                    string instanceId = ReadInstanceId(devInfoSet, ref devData);
                    if (string.IsNullOrWhiteSpace(instanceId))
                    {
                        continue;
                    }

                    uint status;
                    uint problem;
                    uint cr = NativeMethods.CM_Get_DevNode_Status(out status, out problem, devData.DevInst, 0);
                    bool isPresent = (cr == NativeMethods.CR_SUCCESS);
                    if (!includeGhostDevices && !isPresent)
                    {
                        continue;
                    }

                    string friendlyName = ReadRegString(devInfoSet, ref devData, NativeMethods.SPDRP_FRIENDLYNAME);
                    string deviceDesc = ReadRegString(devInfoSet, ref devData, NativeMethods.SPDRP_DEVICEDESC);
                    string displayName = !string.IsNullOrWhiteSpace(friendlyName) ? friendlyName : deviceDesc;
                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        displayName = instanceId;
                    }

                    string devClass = ReadRegString(devInfoSet, ref devData, NativeMethods.SPDRP_CLASS);
                    string classGuid = ReadRegString(devInfoSet, ref devData, NativeMethods.SPDRP_CLASSGUID);
                    string mfg = ReadRegString(devInfoSet, ref devData, NativeMethods.SPDRP_MFG);
                    string driverKeyRel = ReadRegString(devInfoSet, ref devData, NativeMethods.SPDRP_DRIVER);
                    List<string> hwIds = ReadRegMultiSz(devInfoSet, ref devData, NativeMethods.SPDRP_HARDWAREID);
                    List<string> compatIds = ReadRegMultiSz(devInfoSet, ref devData, NativeMethods.SPDRP_COMPATIBLEIDS);

                    var dev = new HardwareDevice
                    {
                        InstanceId = instanceId,
                        DevInstHandle = devData.DevInst,
                        DeviceName = displayName,
                        DeviceClass = string.IsNullOrWhiteSpace(devClass) ? "System" : devClass,
                        ClassGuid = classGuid ?? "",
                        Manufacturer = mfg ?? "",
                        IsPresent = isPresent,
                        HardwareIds = hwIds,
                        CompatibleIds = compatIds
                    };

                    PopulateDriverRegistryMetadata(dev, driverKeyRel);

                    if (isPresent)
                    {
                        if ((status & DN_HAS_PROBLEM) != 0)
                        {
                            dev.ProblemCode = (int)problem;
                        }
                        else if ((status & DN_PRIVATE_PROBLEM) != 0)
                        {
                            dev.ProblemCode = 31; // Private driver problem reported by kernel driver
                        }
                    }
                    else
                    {
                        dev.ProblemCode = 45; // CM_PROB_PHANTOM
                    }

                    ProblemCodeInfo pInfo = ProblemCodeCatalog.DescribeProblemCode(dev.ProblemCode);
                    dev.ProblemSummary = pInfo.Summary;
                    dev.ProblemRemediation = pInfo.RecommendedAction;

                    VendorSupportInfo vInfo = PciVendorResolver.ResolveVendor(dev.PrimaryHardwareId, dev.ProviderName);
                    dev.VendorSupportName = vInfo.VendorName;
                    dev.SubsystemOemName = vInfo.SubsystemOemName;
                    dev.VendorSupportUrl = vInfo.SupportUrl;

                    results.Add(dev);
                }
            }
            finally
            {
                NativeMethods.SetupDiDestroyDeviceInfoList(devInfoSet);
            }

            return results;
        }

        private static void PopulateDriverRegistryMetadata(HardwareDevice dev, string driverKeyRel)
        {
            if (string.IsNullOrWhiteSpace(driverKeyRel))
            {
                return;
            }
            try
            {
                string subKeyPath = @"SYSTEM\CurrentControlSet\Control\Class\" + driverKeyRel.TrimStart('\\');
                using (RegistryKey rk = Registry.LocalMachine.OpenSubKey(subKeyPath, false))
                {
                    if (rk == null) return;
                    dev.DriverVersion = Convert.ToString(rk.GetValue("DriverVersion", "")) ?? "";
                    dev.DriverDate = Convert.ToString(rk.GetValue("DriverDate", "")) ?? "";
                    dev.ProviderName = Convert.ToString(rk.GetValue("ProviderName", "")) ?? "";
                    dev.InfName = Convert.ToString(rk.GetValue("InfPath", "")) ?? "";
                    dev.MatchingDeviceId = Convert.ToString(rk.GetValue("MatchingDeviceId", "")) ?? "";
                    if (!string.IsNullOrWhiteSpace(dev.InfName))
                    {
                        dev.IsWhqlSigned = true;
                        dev.DigitalSigner = dev.InfName.StartsWith("oem", StringComparison.OrdinalIgnoreCase)
                            ? (string.IsNullOrWhiteSpace(dev.ProviderName) ? "OEM WHQL Publisher" : dev.ProviderName + " (WHQL)")
                            : "Microsoft Windows Publisher";
                    }
                }
            }
            catch
            {
                // Non-fatal: individual protected class subkeys remain partial rather than failing the scan
            }
        }

        private static string ReadInstanceId(IntPtr devInfoSet, ref NativeMethods.SP_DEVINFO_DATA devData)
        {
            var sb = new StringBuilder(512);
            uint required;
            if (NativeMethods.SetupDiGetDeviceInstanceIdW(devInfoSet, ref devData, sb, (uint)sb.Capacity, out required))
            {
                return sb.ToString();
            }
            if (required > sb.Capacity && required < 4096)
            {
                sb = new StringBuilder((int)required + 2);
                if (NativeMethods.SetupDiGetDeviceInstanceIdW(devInfoSet, ref devData, sb, (uint)sb.Capacity, out required))
                {
                    return sb.ToString();
                }
            }
            return "";
        }

        private static string ReadRegString(IntPtr devInfoSet, ref NativeMethods.SP_DEVINFO_DATA devData, uint prop)
        {
            uint regType;
            uint required;
            byte[] buf = new byte[512];
            if (!NativeMethods.SetupDiGetDeviceRegistryPropertyW(devInfoSet, ref devData, prop, out regType, buf, (uint)buf.Length, out required))
            {
                if (required > buf.Length && required < 16384)
                {
                    buf = new byte[required + 4];
                    if (!NativeMethods.SetupDiGetDeviceRegistryPropertyW(devInfoSet, ref devData, prop, out regType, buf, (uint)buf.Length, out required))
                    {
                        return "";
                    }
                }
                else
                {
                    return "";
                }
            }

            if (required < 2) return "";
            int decodeLen = (int)Math.Min((uint)buf.Length, required);
            string raw = Encoding.Unicode.GetString(buf, 0, decodeLen);
            int nul = raw.IndexOf('\0');
            return nul >= 0 ? raw.Substring(0, nul) : raw;
        }

        private static List<string> ReadRegMultiSz(IntPtr devInfoSet, ref NativeMethods.SP_DEVINFO_DATA devData, uint prop)
        {
            var list = new List<string>();
            uint regType;
            uint required;
            byte[] buf = new byte[1024];
            if (!NativeMethods.SetupDiGetDeviceRegistryPropertyW(devInfoSet, ref devData, prop, out regType, buf, (uint)buf.Length, out required))
            {
                if (required > buf.Length && required < 32768)
                {
                    buf = new byte[required + 4];
                    if (!NativeMethods.SetupDiGetDeviceRegistryPropertyW(devInfoSet, ref devData, prop, out regType, buf, (uint)buf.Length, out required))
                    {
                        return list;
                    }
                }
                else
                {
                    return list;
                }
            }

            if (required < 2) return list;
            int decodeLen = (int)Math.Min((uint)buf.Length, required);
            string raw = Encoding.Unicode.GetString(buf, 0, decodeLen);
            string[] parts = raw.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string p in parts)
            {
                string trimmed = p.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    list.Add(trimmed);
                }
            }
            return list;
        }
    }
}
