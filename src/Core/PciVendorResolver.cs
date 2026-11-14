using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DriverPlus.Core
{
    public static class PciVendorResolver
    {
        private static readonly Regex VenRegex = new Regex(@"(?:VEN_|VID_)([0-9A-Fa-f]{4})", RegexOptions.Compiled);
        private static readonly Regex SubsysRegex = new Regex(@"SUBSYS_[0-9A-Fa-f]{4}([0-9A-Fa-f]{4})", RegexOptions.Compiled);

        private static readonly Dictionary<string, KeyValuePair<string, string>> Vendors =
            new Dictionary<string, KeyValuePair<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "8086", new KeyValuePair<string, string>("Intel Corporation", "https://www.intel.com/content/www/us/en/download-center/home.html") },
                { "10DE", new KeyValuePair<string, string>("NVIDIA Corporation", "https://www.nvidia.com/Download/index.aspx") },
                { "1002", new KeyValuePair<string, string>("Advanced Micro Devices (AMD)", "https://www.amd.com/en/support") },
                { "1022", new KeyValuePair<string, string>("Advanced Micro Devices (AMD)", "https://www.amd.com/en/support") },
                { "10EC", new KeyValuePair<string, string>("Realtek Semiconductor", "https://www.realtek.com/Download/Index?cate_id=194") },
                { "14C3", new KeyValuePair<string, string>("MediaTek Inc.", "https://www.catalog.update.microsoft.com/") },
                { "168C", new KeyValuePair<string, string>("Qualcomm Atheros", "https://www.catalog.update.microsoft.com/") },
                { "14E4", new KeyValuePair<string, string>("Broadcom Inc.", "https://www.broadcom.com/support/download-search") },
                { "1B21", new KeyValuePair<string, string>("ASMedia Technology", "https://www.catalog.update.microsoft.com/") },
                { "1987", new KeyValuePair<string, string>("Phison Electronics", "https://www.catalog.update.microsoft.com/") },
                { "144D", new KeyValuePair<string, string>("Samsung Electronics", "https://semiconductor.samsung.com/consumer-storage/support/tools/") },
                { "15B7", new KeyValuePair<string, string>("Western Digital / SanDisk", "https://support-en.wd.com/") },
                { "2646", new KeyValuePair<string, string>("Kingston Technology", "https://www.kingston.com/en/support") },
                { "046D", new KeyValuePair<string, string>("Logitech", "https://support.logi.com/") },
                { "1532", new KeyValuePair<string, string>("Razer Inc.", "https://mysupport.razer.com/") },
                { "045E", new KeyValuePair<string, string>("Microsoft Corporation", "https://www.catalog.update.microsoft.com/") },
                { "0BDA", new KeyValuePair<string, string>("Realtek USB", "https://www.realtek.com/Download/Index?cate_id=194") },
                { "17AA", new KeyValuePair<string, string>("Lenovo", "https://pcsupport.lenovo.com/") },
                { "1043", new KeyValuePair<string, string>("ASUSTeK Computer", "https://www.asus.com/support/download-center/") },
                { "1462", new KeyValuePair<string, string>("MSI (Micro-Star International)", "https://www.msi.com/support/download") },
                { "1458", new KeyValuePair<string, string>("Gigabyte Technology", "https://www.gigabyte.com/Support") },
                { "1028", new KeyValuePair<string, string>("Dell Inc.", "https://www.dell.com/support/home/") },
                { "103C", new KeyValuePair<string, string>("HP Inc.", "https://support.hp.com/drivers") },
                { "1025", new KeyValuePair<string, string>("Acer Inc.", "https://www.acer.com/us-en/support/drivers-and-manuals") }
            };

        public static VendorSupportInfo ResolveVendor(string hwid, string fallbackProvider)
        {
            var info = new VendorSupportInfo
            {
                VendorId = "",
                VendorName = string.IsNullOrWhiteSpace(fallbackProvider) ? "Standard System Device" : fallbackProvider.Trim(),
                SubsystemOemId = "",
                SubsystemOemName = "",
                SupportUrl = "https://www.catalog.update.microsoft.com/"
            };

            if (!string.IsNullOrWhiteSpace(hwid))
            {
                Match venMatch = VenRegex.Match(hwid);
                if (venMatch.Success)
                {
                    string vid = venMatch.Groups[1].Value.ToUpperInvariant();
                    info.VendorId = vid;
                    KeyValuePair<string, string> entry;
                    if (Vendors.TryGetValue(vid, out entry))
                    {
                        info.VendorName = entry.Key;
                        info.SupportUrl = entry.Value;
                    }
                }

                Match subMatch = SubsysRegex.Match(hwid);
                if (subMatch.Success)
                {
                    string subVid = subMatch.Groups[1].Value.ToUpperInvariant();
                    info.SubsystemOemId = subVid;
                    KeyValuePair<string, string> oemEntry;
                    if (Vendors.TryGetValue(subVid, out oemEntry))
                    {
                        info.SubsystemOemName = oemEntry.Key;
                    }
                }
            }

            return info;
        }
    }
}
