using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using DriverPlus.Core;

namespace DriverPlus.Services
{
    public static class WindowsUpdateCatalogClient
    {
        private static readonly Regex RowRegex = new Regex(
            @"<tr\s+id=[""']([0-9a-fA-F\-]{36})_R\d+[""'][\s\S]*?<\/tr>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex CellRegex = new Regex(
            @"<td\b[^>]*>([\s\S]*?)<\/td>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex TagStripRegex = new Regex(
            @"<[^>]+>",
            RegexOptions.Compiled);

        private static readonly Regex CabUrlRegex = new Regex(
            @"https?:\/\/[A-Za-z0-9.\-_]+\/[^\s""'<>]+\.cab",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly HttpClient SharedHttp = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true
            };
            var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(7);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DriverPlus/1.0 (Windows NT 10.0; Win64; x64)");
            return client;
        }

        public static List<DriverCandidate> ParseSearchHtml(string html, int osBuildNumber, string queriedHwid)
        {
            var results = new List<DriverCandidate>();
            if (string.IsNullOrWhiteSpace(html))
            {
                return results;
            }

            bool isWin11 = osBuildNumber >= 22000;
            MatchCollection rows = RowRegex.Matches(html);
            foreach (Match row in rows)
            {
                string guid = row.Groups[1].Value.Trim();
                MatchCollection cells = CellRegex.Matches(row.Value);
                if (cells.Count < 7)
                {
                    continue;
                }

                string title = CleanCell(cells[1].Groups[1].Value);
                string products = CleanCell(cells[2].Groups[1].Value);
                string classification = CleanCell(cells[3].Groups[1].Value);
                string dateRaw = CleanCell(cells[4].Groups[1].Value);
                string versionRaw = CleanCell(cells[5].Groups[1].Value);
                string sizeRaw = CleanCell(cells[6].Groups[1].Value);

                if (classification.IndexOf("Driver", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                // Filter to host OS family so Windows 10 servicing drivers don't pollute Windows 11 and vice versa
                bool mentionsWin11 = products.IndexOf("Windows 11", StringComparison.OrdinalIgnoreCase) >= 0;
                bool mentionsWin10 = products.IndexOf("Windows 10", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isWin11 && mentionsWin10 && !mentionsWin11)
                {
                    continue;
                }
                if (!isWin11 && mentionsWin11 && !mentionsWin10)
                {
                    continue;
                }

                int[] parsedVer;
                if (!DriverVersionComparer.TryParseDriverVersion(versionRaw, out parsedVer))
                {
                    continue;
                }

                string isoDate = NormalizeCatalogDate(dateRaw);
                string sizeText;
                long sizeBytes;
                ParseCatalogSize(sizeRaw, out sizeText, out sizeBytes);

                results.Add(new DriverCandidate
                {
                    UpdateId = guid,
                    Title = title,
                    SupportedProducts = products,
                    Classification = classification,
                    TargetDate = isoDate,
                    TargetVersion = versionRaw,
                    SizeText = sizeText,
                    SizeBytes = sizeBytes,
                    MatchedHardwareId = queriedHwid ?? "",
                    MatchSpecificity = DriverVersionComparer.GetHardwareIdSpecificity(queriedHwid),
                    WhqlSigned = true,
                    RebootRequired = false,
                    Source = "Microsoft Update Catalog"
                });
            }

            return results;
        }

        public static List<string> ParseDownloadDialogHtml(string html)
        {
            var allowed = new List<string>();
            if (string.IsNullOrWhiteSpace(html))
            {
                return allowed;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            MatchCollection matches = CabUrlRegex.Matches(html);
            foreach (Match m in matches)
            {
                string rawUrl = m.Value.Trim();
                if (IsOfficialMicrosoftHttpsCabUrl(rawUrl) && seen.Add(rawUrl))
                {
                    allowed.Add(rawUrl);
                }
            }
            return allowed;
        }

        public static bool IsOfficialMicrosoftHttpsCabUrl(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
            {
                return false;
            }
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            string host = uri.Host.ToLowerInvariant();
            bool trustedDomain =
                host.Equals("windowsupdate.com", StringComparison.Ordinal) ||
                host.EndsWith(".windowsupdate.com", StringComparison.Ordinal) ||
                host.Equals("microsoft.com", StringComparison.Ordinal) ||
                host.EndsWith(".microsoft.com", StringComparison.Ordinal);

            return trustedDomain && uri.AbsolutePath.EndsWith(".cab", StringComparison.OrdinalIgnoreCase);
        }

        public static async Task<string> ResolveCabDownloadUrlAsync(string updateGuid)
        {
            if (string.IsNullOrWhiteSpace(updateGuid))
            {
                return "";
            }
            Guid parsed;
            if (!Guid.TryParse(updateGuid.Trim(), out parsed))
            {
                return "";
            }

            string payloadJson = string.Format(
                CultureInfo.InvariantCulture,
                "[{{\"size\":0,\"languages\":\"\",\"uidInfo\":\"{0}\",\"updateID\":\"{0}\"}}]",
                parsed.ToString("D"));
            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("updateIDs", payloadJson)
            });

            using (HttpResponseMessage resp = await SharedHttp.PostAsync(
                "https://www.catalog.update.microsoft.com/DownloadDialog.aspx", form).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode)
                {
                    return "";
                }
                string html = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                List<string> urls = ParseDownloadDialogHtml(html);
                return urls.Count > 0 ? urls[0] : "";
            }
        }

        public static Task<UpdateScanSummary> EnrichWithOfficialUpdatesAsync(
            IList<HardwareDevice> devices,
            DriverStoreReport storeReport,
            bool queryLiveCatalog,
            bool whqlOnly,
            HashSet<string> ignoredInstanceIds,
            Action<string, int> progress)
        {
            return EnrichWithOfficialUpdatesAsync(devices, storeReport, queryLiveCatalog, whqlOnly, ignoredInstanceIds, 0, progress);
        }

        public static async Task<UpdateScanSummary> EnrichWithOfficialUpdatesAsync(
            IList<HardwareDevice> devices,
            DriverStoreReport storeReport,
            bool queryLiveCatalog,
            bool whqlOnly,
            HashSet<string> ignoredInstanceIds,
            int osBuildOverride,
            Action<string, int> progress)
        {
            var sw = Stopwatch.StartNew();
            var summary = new UpdateScanSummary
            {
                ScannedAtUtc = DateTime.UtcNow,
                HvciBlockerCount = storeReport != null ? storeReport.HvciBlockers.Count : 0
            };

            int osBuild = osBuildOverride > 0 ? osBuildOverride : Environment.OSVersion.Version.Build;
            bool isOfflineImport = osBuildOverride > 0;
            if (progress != null) progress("Checking Windows Update Agent (Type='Driver')...", 25);

            List<DriverCandidate> wuaCandidates = isOfflineImport
                ? new List<DriverCandidate>()
                : await Task.Run(() => QueryLocalWindowsUpdateAgentDrivers()).ConfigureAwait(false);
            summary.WuaCandidateCount = wuaCandidates.Count;

            var catalogByHwid = new ConcurrentDictionary<string, List<DriverCandidate>>(StringComparer.OrdinalIgnoreCase);

            if (queryLiveCatalog)
            {
                var queries = CollectPriorityCatalogQueries(devices);
                Dictionary<string, List<DriverCandidate>> diskCache = LoadCatalogCache();
                var confirmedLive = new ConcurrentDictionary<string, List<DriverCandidate>>(StringComparer.OrdinalIgnoreCase);
                var uncached = new List<string>();
                foreach (string q in queries)
                {
                    string cacheKey = osBuild + "|" + q;
                    List<DriverCandidate> cachedList;
                    if (diskCache.TryGetValue(cacheKey, out cachedList))
                    {
                        catalogByHwid[q] = cachedList;
                    }
                    else
                    {
                        uncached.Add(q);
                    }
                }

                if (uncached.Count > 0)
                {
                    int completed = 0;
                    using (var gate = new SemaphoreSlim(6))
                    {
                        var tasks = new List<Task>();
                        foreach (string hwidQuery in uncached)
                        {
                            string captured = hwidQuery;
                            tasks.Add(Task.Run(async () =>
                            {
                                await gate.WaitAsync().ConfigureAwait(false);
                                try
                                {
                                    List<DriverCandidate> found = await SearchMicrosoftCatalogForHwidAsync(captured, osBuild).ConfigureAwait(false);
                                    if (found != null)
                                    {
                                        // If 4-part SUBSYS query returned 0 entries, try generic 2-part VEN&DEV fallback
                                        if (found.Count == 0 && captured.IndexOf("&SUBSYS_", StringComparison.OrdinalIgnoreCase) >= 0)
                                        {
                                            string fallback2 = DriverVersionComparer.ExtractTwoPartHardwareId(captured);
                                            if (!string.IsNullOrEmpty(fallback2) && !string.Equals(fallback2, captured, StringComparison.OrdinalIgnoreCase))
                                            {
                                                List<DriverCandidate> found2 = await SearchMicrosoftCatalogForHwidAsync(fallback2, osBuild).ConfigureAwait(false);
                                                if (found2 != null)
                                                {
                                                    catalogByHwid[fallback2] = found2;
                                                    confirmedLive[osBuild + "|" + fallback2] = found2;
                                                }
                                            }
                                        }
                                        catalogByHwid[captured] = found;
                                        confirmedLive[osBuild + "|" + captured] = found;
                                    }
                                }
                                catch
                                {
                                }
                                finally
                                {
                                    int done = Interlocked.Increment(ref completed);
                                    int pct = 30 + (int)Math.Round((done * 60.0) / Math.Max(1, uncached.Count));
                                    if (progress != null)
                                    {
                                        progress("Querying Microsoft Update Catalog (" + done + "/" + uncached.Count + ")...", pct);
                                    }
                                    gate.Release();
                                }
                            }));
                        }
                        await Task.WhenAll(tasks).ConfigureAwait(false);
                    }

                    if (confirmedLive.Count > 0)
                    {
                        foreach (var kv in confirmedLive)
                        {
                            diskCache[kv.Key] = kv.Value;
                        }
                        SaveCatalogCache(diskCache);
                    }
                }
            }

            int catalogTotal = 0;
            foreach (var kv in catalogByHwid)
            {
                catalogTotal += kv.Value.Count;
            }
            summary.CatalogCandidateCount = catalogTotal;

            foreach (HardwareDevice d in devices)
            {
                if (ignoredInstanceIds != null && ignoredInstanceIds.Contains(d.InstanceId))
                {
                    d.IsIgnored = true;
                }

                if (!d.IsPresent)
                {
                    summary.GhostDeviceCount++;
                    summary.Devices.Add(d);
                    continue;
                }

                summary.TotalPresentDevices++;
                if (d.ProblemCode != 0)
                {
                    summary.FaultyDeviceCount++;
                }

                var combined = new List<DriverCandidate>();
                foreach (DriverCandidate wc in wuaCandidates)
                {
                    combined.Add(wc);
                }
                foreach (string hwid in d.HardwareIds)
                {
                    string norm4 = DriverVersionComparer.NormalizeHardwareId(hwid);
                    List<DriverCandidate> list4;
                    if (!string.IsNullOrEmpty(norm4) && catalogByHwid.TryGetValue(norm4, out list4))
                    {
                        combined.AddRange(list4);
                    }

                    string norm2 = DriverVersionComparer.ExtractTwoPartHardwareId(norm4);
                    List<DriverCandidate> list2;
                    if (!string.IsNullOrEmpty(norm2) && !norm2.Equals(norm4, StringComparison.OrdinalIgnoreCase) &&
                        catalogByHwid.TryGetValue(norm2, out list2))
                    {
                        combined.AddRange(list2);
                    }
                }

                d.RecommendedUpdate = DriverVersionComparer.SelectBestCandidate(d, combined, whqlOnly);
                if (d.HasUpdate)
                {
                    summary.OutdatedCount++;
                }
                else if (d.ProblemCode == 0)
                {
                    summary.UpToDateCount++;
                }

                summary.Devices.Add(d);
            }

            // Sort: Faulty first, then Outdated, then Present Up-To-Date, then Unplugged Ghost
            summary.Devices.Sort((a, b) =>
            {
                int rankA = GetSortRank(a);
                int rankB = GetSortRank(b);
                if (rankA != rankB) return rankA.CompareTo(rankB);
                return string.Compare(a.DeviceName, b.DeviceName, StringComparison.OrdinalIgnoreCase);
            });

            sw.Stop();
            summary.ElapsedMs = sw.ElapsedMilliseconds;
            if (progress != null) progress("Scan complete.", 100);
            return summary;
        }

        private static int GetSortRank(HardwareDevice d)
        {
            if (d.IsPresent && d.ProblemCode != 0) return 0;
            if (d.HasUpdate) return 1;
            if (d.IsPresent) return 2;
            return 3;
        }

        private static List<string> CollectPriorityCatalogQueries(IList<HardwareDevice> devices)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ordered = new List<string>();
            if (devices == null) return ordered;

            var ranked = new List<HardwareDevice>();
            foreach (HardwareDevice d in devices)
            {
                if (d != null && d.IsPresent && d.HardwareIds != null && d.HardwareIds.Count > 0)
                {
                    ranked.Add(d);
                }
            }

            // Prioritize faulty devices (Code 28 etc.), then Display/Net/Media/Audio/Bluetooth/USB, and chipset System bridges last
            ranked.Sort((a, b) => GetCatalogQueryPriority(a).CompareTo(GetCatalogQueryPriority(b)));

            foreach (HardwareDevice d in ranked)
            {
                string primary = DriverVersionComparer.NormalizeHardwareId(d.PrimaryHardwareId);
                if (!IsQueryableBusHardwareId(primary))
                {
                    continue;
                }
                if (set.Add(primary))
                {
                    ordered.Add(primary);
                }
                if (ordered.Count >= 32)
                {
                    break;
                }
            }
            return ordered;
        }

        private static int GetCatalogQueryPriority(HardwareDevice d)
        {
            if (d.ProblemCode != 0) return 0;
            string c = d.DeviceClass ?? "";
            if (c.IndexOf("Display", StringComparison.OrdinalIgnoreCase) >= 0 ||
                c.IndexOf("Net", StringComparison.OrdinalIgnoreCase) >= 0 ||
                c.IndexOf("Media", StringComparison.OrdinalIgnoreCase) >= 0 ||
                c.IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0 ||
                c.IndexOf("Audio", StringComparison.OrdinalIgnoreCase) >= 0 ||
                c.IndexOf("SCSIAdapter", StringComparison.OrdinalIgnoreCase) >= 0 ||
                c.IndexOf("HDC", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 1;
            }
            if (c.IndexOf("System", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 3;
            }
            return 2;
        }

        private static bool IsQueryableBusHardwareId(string normHwid)
        {
            if (string.IsNullOrWhiteSpace(normHwid)) return false;
            return normHwid.StartsWith("PCI\\VEN_", StringComparison.OrdinalIgnoreCase) ||
                   normHwid.StartsWith("USB\\VID_", StringComparison.OrdinalIgnoreCase) ||
                   normHwid.StartsWith("HDAUDIO\\", StringComparison.OrdinalIgnoreCase) ||
                   normHwid.StartsWith("INTELAUDIO\\", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<List<DriverCandidate>> SearchMicrosoftCatalogForHwidAsync(string hwid, int osBuild)
        {
            string url = "https://www.catalog.update.microsoft.com/Search.aspx?q=" + Uri.EscapeDataString(hwid);
            using (HttpResponseMessage resp = await SharedHttp.GetAsync(url).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode)
                {
                    return null;
                }
                string html = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ParseSearchHtml(html, osBuild, hwid);
            }
        }

        private static List<DriverCandidate> QueryLocalWindowsUpdateAgentDrivers()
        {
            var list = new List<DriverCandidate>();
            try
            {
                // Run WUA search on a worker with strict 3.5s bound so offline/busy WUA never stalls UI
                var wuaTask = Task.Run(() =>
                {
                    var inner = new List<DriverCandidate>();
                    Type sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
                    if (sessionType == null) return inner;
                    dynamic session = Activator.CreateInstance(sessionType);
                    dynamic searcher = session.CreateUpdateSearcher();
                    searcher.Online = false; // Fast local Windows Update metadata cache first
                    dynamic searchResult = searcher.Search("IsInstalled=0 and Type='Driver'");
                    int count = (int)searchResult.Updates.Count;
                    for (int i = 0; i < count; i++)
                    {
                        dynamic u = searchResult.Updates.Item(i);
                        string hwid = "";
                        try { hwid = Convert.ToString(u.DriverHardwareID) ?? ""; } catch { }
                        string title = Convert.ToString(u.Title) ?? "Windows Update Driver";
                        string ver = ExtractVersionFromTitle(title);
                        DateTime dt = DateTime.MinValue;
                        try { dt = Convert.ToDateTime(u.DriverVerDate); } catch { }

                        inner.Add(new DriverCandidate
                        {
                            UpdateId = Convert.ToString(u.Identity.UpdateID) ?? "",
                            Title = title,
                            TargetVersion = ver,
                            TargetDate = dt > DateTime.MinValue ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "",
                            MatchedHardwareId = hwid,
                            MatchSpecificity = DriverVersionComparer.GetHardwareIdSpecificity(hwid),
                            WhqlSigned = true,
                            Source = "Windows Update Agent"
                        });
                    }
                    return inner;
                });

                if (wuaTask.Wait(TimeSpan.FromMilliseconds(3500)))
                {
                    return wuaTask.Result;
                }
            }
            catch
            {
            }
            return list;
        }

        private static string ExtractVersionFromTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return "";
            Match m = Regex.Match(title, @"\b(\d+\.\d+\.\d+\.\d+)\b");
            return m.Success ? m.Groups[1].Value : "";
        }

        private static string CleanCell(string rawHtml)
        {
            if (string.IsNullOrEmpty(rawHtml)) return "";
            string noTags = TagStripRegex.Replace(rawHtml, " ");
            string decoded = WebUtility.HtmlDecode(noTags);
            return Regex.Replace(decoded, @"\s+", " ").Trim();
        }

        private static string NormalizeCatalogDate(string rawDate)
        {
            DateTime dt;
            if (DateTime.TryParse(rawDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
            {
                return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            return rawDate ?? "";
        }

        private static void ParseCatalogSize(string rawSize, out string sizeText, out long sizeBytes)
        {
            sizeText = rawSize ?? "";
            sizeBytes = 0;
            if (string.IsNullOrWhiteSpace(rawSize)) return;

            string[] tokens = rawSize.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length >= 2)
            {
                sizeText = tokens[0] + " " + tokens[1];
            }
            if (tokens.Length >= 3)
            {
                long b;
                if (long.TryParse(tokens[tokens.Length - 1], NumberStyles.None, CultureInfo.InvariantCulture, out b))
                {
                    sizeBytes = b;
                }
            }
        }

        private static string GetCacheFilePath()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DriverPlus");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "catalog-cache-v1.json");
        }

        private sealed class CatalogCacheEnvelope
        {
            public DateTime SavedAtUtc { get; set; }
            public Dictionary<string, List<DriverCandidate>> Entries { get; set; }
        }

        private static Dictionary<string, List<DriverCandidate>> LoadCatalogCache()
        {
            try
            {
                string path = GetCacheFilePath();
                if (!File.Exists(path)) return new Dictionary<string, List<DriverCandidate>>(StringComparer.OrdinalIgnoreCase);
                var serializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
                var env = serializer.Deserialize<CatalogCacheEnvelope>(File.ReadAllText(path, Encoding.UTF8));
                if (env != null && env.Entries != null && (DateTime.UtcNow - env.SavedAtUtc).TotalHours < 6.0)
                {
                    return new Dictionary<string, List<DriverCandidate>>(env.Entries, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch
            {
            }
            return new Dictionary<string, List<DriverCandidate>>(StringComparer.OrdinalIgnoreCase);
        }

        private static void SaveCatalogCache(Dictionary<string, List<DriverCandidate>> entries)
        {
            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
                var env = new CatalogCacheEnvelope
                {
                    SavedAtUtc = DateTime.UtcNow,
                    Entries = entries
                };
                File.WriteAllText(GetCacheFilePath(), serializer.Serialize(env), Encoding.UTF8);
            }
            catch
            {
            }
        }
    }
}
