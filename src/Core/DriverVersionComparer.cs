using System;
using System.Collections.Generic;
using System.Globalization;

namespace DriverPlus.Core
{
    public static class DriverVersionComparer
    {
        public static bool TryParseDriverVersion(string raw, out int[] parts)
        {
            parts = new int[4];
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            string trimmed = raw.Trim();
            int spaceIdx = trimmed.IndexOf(' ');
            if (spaceIdx > 0)
            {
                trimmed = trimmed.Substring(0, spaceIdx).Trim();
            }

            string[] tokens = trimmed.Split('.');
            if (tokens.Length < 1 || tokens.Length > 4)
            {
                return false;
            }

            bool hasAnyNumber = false;
            for (int i = 0; i < tokens.Length; i++)
            {
                int val;
                if (!int.TryParse(tokens[i].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out val) || val < 0)
                {
                    return false;
                }
                parts[i] = val;
                hasAnyNumber = true;
            }
            return hasAnyNumber;
        }

        public static int CompareVersions(string installed, string candidate)
        {
            int[] a;
            int[] b;
            bool okA = TryParseDriverVersion(installed, out a);
            bool okB = TryParseDriverVersion(candidate, out b);

            if (!okA && !okB) return 0;
            // Never let a malformed/unparseable candidate beat a valid installed version
            if (okA && !okB) return 1;
            if (!okA && okB) return -1;

            for (int i = 0; i < 4; i++)
            {
                if (a[i] != b[i])
                {
                    return a[i] < b[i] ? -1 : 1;
                }
            }
            return 0;
        }

        public static string NormalizeHardwareId(string hwid)
        {
            if (string.IsNullOrWhiteSpace(hwid))
            {
                return "";
            }
            string upper = hwid.Trim().ToUpperInvariant();
            // Strip trailing instance path segment if present (e.g., PCI\VEN_8086&DEV_51F0&SUBSYS_00948086&REV_01\3&11583659&0&A3)
            int firstSlash = upper.IndexOf('\\');
            if (firstSlash > 0)
            {
                int secondSlash = upper.IndexOf('\\', firstSlash + 1);
                if (secondSlash > firstSlash)
                {
                    upper = upper.Substring(0, secondSlash);
                }
            }
            // Strip &REV_XX suffix for canonical catalog search while keeping SUBSYS intact
            int revIdx = upper.IndexOf("&REV_", StringComparison.Ordinal);
            if (revIdx > 0)
            {
                int nextAmp = upper.IndexOf('&', revIdx + 5);
                upper = nextAmp > 0 ? upper.Substring(0, revIdx) + upper.Substring(nextAmp) : upper.Substring(0, revIdx);
            }
            return upper;
        }

        public static string ExtractTwoPartHardwareId(string hwid)
        {
            string norm = NormalizeHardwareId(hwid);
            int subsysIdx = norm.IndexOf("&SUBSYS_", StringComparison.Ordinal);
            if (subsysIdx > 0)
            {
                return norm.Substring(0, subsysIdx);
            }
            int ccIdx = norm.IndexOf("&CC_", StringComparison.Ordinal);
            if (ccIdx > 0)
            {
                return norm.Substring(0, ccIdx);
            }
            return norm;
        }

        public static int GetHardwareIdSpecificity(string hwid)
        {
            if (string.IsNullOrWhiteSpace(hwid))
            {
                return 0;
            }
            string u = hwid.Trim().ToUpperInvariant();
            int score = 10;
            if (u.IndexOf("&SUBSYS_", StringComparison.Ordinal) >= 0)
            {
                score += 40;
            }
            if (u.IndexOf("&REV_", StringComparison.Ordinal) >= 0)
            {
                score += 10;
            }
            if (u.StartsWith("PCI\\VEN_", StringComparison.Ordinal) ||
                u.StartsWith("USB\\VID_", StringComparison.Ordinal) ||
                u.StartsWith("HDAUDIO\\", StringComparison.Ordinal))
            {
                score += 20;
            }
            if (u.IndexOf("&CC_", StringComparison.Ordinal) >= 0 && u.IndexOf("DEV_", StringComparison.Ordinal) < 0)
            {
                score = 5;
            }
            return score;
        }

        public static DriverCandidate SelectBestCandidate(
            HardwareDevice device,
            IEnumerable<DriverCandidate> candidates,
            bool whqlOnly)
        {
            if (device == null || candidates == null)
            {
                return null;
            }

            var normalizedDeviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var twoPartDeviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (device.HardwareIds != null)
            {
                foreach (string id in device.HardwareIds)
                {
                    string n = NormalizeHardwareId(id);
                    if (!string.IsNullOrEmpty(n))
                    {
                        normalizedDeviceIds.Add(n);
                        twoPartDeviceIds.Add(ExtractTwoPartHardwareId(n));
                    }
                }
            }
            if (!string.IsNullOrWhiteSpace(device.MatchingDeviceId))
            {
                string m = NormalizeHardwareId(device.MatchingDeviceId);
                if (!string.IsNullOrEmpty(m))
                {
                    normalizedDeviceIds.Add(m);
                    twoPartDeviceIds.Add(ExtractTwoPartHardwareId(m));
                }
            }

            if (normalizedDeviceIds.Count == 0)
            {
                return null;
            }

            DriverCandidate best = null;
            int bestSpec = -1;

            foreach (DriverCandidate c in candidates)
            {
                if (c == null) continue;
                if (whqlOnly && !c.WhqlSigned) continue;

                int[] candParts;
                if (!TryParseDriverVersion(c.TargetVersion, out candParts))
                {
                    continue;
                }

                // Strictly reject downgrades or equal versions when device already has a valid driver
                if (!string.IsNullOrWhiteSpace(device.DriverVersion) && device.ProblemCode != 28)
                {
                    if (CompareVersions(device.DriverVersion, c.TargetVersion) >= 0)
                    {
                        continue;
                    }
                }

                string candNorm = NormalizeHardwareId(c.MatchedHardwareId);
                if (string.IsNullOrEmpty(candNorm))
                {
                    continue;
                }
                bool matchesExact = normalizedDeviceIds.Contains(candNorm);
                bool candHasSubsys = candNorm.IndexOf("&SUBSYS_", StringComparison.Ordinal) >= 0;
                bool matchesTwoPart = !candHasSubsys && twoPartDeviceIds.Contains(ExtractTwoPartHardwareId(candNorm));
                if (!matchesExact && !matchesTwoPart)
                {
                    continue;
                }

                int spec = c.MatchSpecificity > 0 ? c.MatchSpecificity : GetHardwareIdSpecificity(candNorm);
                if (matchesExact && candHasSubsys)
                {
                    spec += 50;
                }

                if (best == null)
                {
                    best = c;
                    bestSpec = spec;
                }
                else
                {
                    // Exact 4-part OEM SUBSYS match outranks generic 2-part match; within same specificity tier, newer version (then newer ISO date) wins
                    if (spec > bestSpec)
                    {
                        best = c;
                        bestSpec = spec;
                    }
                    else if (spec == bestSpec)
                    {
                        int verCmp = CompareVersions(best.TargetVersion, c.TargetVersion);
                        if (verCmp < 0 || (verCmp == 0 && string.CompareOrdinal(best.TargetDate ?? "", c.TargetDate ?? "") < 0))
                        {
                            best = c;
                        }
                    }
                }
            }

            return best;
        }
    }
}
