using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using DriverPlus.Core;

namespace DriverPlus.Services
{
    public sealed class LocalWebPortalServer : IDisposable
    {
        private const int MaxHeaderBytes = 16384;
        private const int MaxBodyBytes = 262144;

        private readonly int _requestedPort;
        private readonly string _csrfToken;
        private readonly object _cacheLock = new object();
        private readonly SemaphoreSlim _clientGate = new SemaphoreSlim(16);
        private TcpListener _listener;
        private volatile bool _running;

        private SystemProfile _cachedProfile;
        private DriverStoreReport _cachedStore;
        private UpdateScanSummary _cachedScan;
        private List<RuntimeComponent> _cachedRuntimes;
        private List<DiagnosticFinding> _cachedDiagnostics;

        public int Port { get; private set; }
        public string CsrfToken { get { return _csrfToken; } }
        public string BaseUrl { get { return "http://127.0.0.1:" + Port; } }

        public LocalWebPortalServer(int port)
        {
            _requestedPort = port;
            _csrfToken = GenerateSecureHexToken(32);
        }

        public void SeedSnapshot(
            SystemProfile profile,
            DriverStoreReport store,
            UpdateScanSummary scan,
            List<RuntimeComponent> runtimes,
            List<DiagnosticFinding> diagnostics)
        {
            lock (_cacheLock)
            {
                if (profile != null) _cachedProfile = profile;
                if (store != null) _cachedStore = store;
                if (scan != null) _cachedScan = scan;
                if (runtimes != null) _cachedRuntimes = runtimes;
                if (diagnostics != null) _cachedDiagnostics = diagnostics;
            }
        }

        public void Start()
        {
            if (_running) return;
            _listener = new TcpListener(IPAddress.Loopback, _requestedPort);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _running = true;
            Task.Run(() => AcceptLoop());
        }

        public void Stop()
        {
            _running = false;
            try
            {
                if (_listener != null)
                {
                    _listener.Stop();
                }
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            Stop();
            try { _clientGate.Dispose(); } catch { }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    if (!_clientGate.Wait(2000))
                    {
                        try { client.Close(); } catch { }
                        continue;
                    }
                    Task.Run(() =>
                    {
                        try
                        {
                            HandleClient(client);
                        }
                        finally
                        {
                            try { _clientGate.Release(); } catch { }
                        }
                    });
                }
                catch
                {
                    if (!_running) break;
                }
            }
        }

        private void HandleClient(TcpClient client)
        {
            using (client)
            {
                client.ReceiveTimeout = 8000;
                client.SendTimeout = 8000;
                try
                {
                    using (NetworkStream ns = client.GetStream())
                    {
                        string method;
                        string rawPath;
                        Dictionary<string, string> headers;
                        string body;

                        if (!TryReadHttpRequest(ns, out method, out rawPath, out headers, out body))
                        {
                            WriteResponse(ns, 400, "Bad Request", "text/plain; charset=utf-8", "Malformed HTTP request.", null);
                            return;
                        }

                        if (!HasValidLoopbackHost(headers))
                        {
                            WriteResponse(ns, 403, "Forbidden", "application/json; charset=utf-8",
                                "{\"ok\":false,\"error\":\"Blocked by loopback Host header guard.\"}", null);
                            return;
                        }

                        string path = rawPath;
                        int qIdx = path.IndexOf('?');
                        if (qIdx >= 0) path = path.Substring(0, qIdx);

                        if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                        {
                            if (path == "/" || path == "/index.html")
                            {
                                string scriptNonce = GenerateSecureHexToken(16);
                                WriteResponse(ns, 200, "OK", "text/html; charset=utf-8", BuildDashboardHtml(scriptNonce), scriptNonce);
                                return;
                            }
                            if (path == "/favicon.ico")
                            {
                                WriteResponseBytes(ns, 204, "No Content", "image/x-icon", new byte[0], null);
                                return;
                            }
                            if (path == "/api/bootstrap")
                            {
                                string json = BuildBootstrapJson(false);
                                WriteResponse(ns, 200, "OK", "application/json; charset=utf-8", json, null);
                                return;
                            }
                            WriteResponse(ns, 404, "Not Found", "application/json; charset=utf-8", "{\"error\":\"Not found\"}", null);
                            return;
                        }

                        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!ValidateCsrfAndOrigin(headers))
                            {
                                WriteResponse(ns, 403, "Forbidden", "application/json; charset=utf-8",
                                    "{\"ok\":false,\"error\":\"Blocked by loopback CSRF / Origin guard.\"}", null);
                                return;
                            }

                            if (path == "/api/action")
                            {
                                string respJson = ExecuteActionJson(body);
                                WriteResponse(ns, 200, "OK", "application/json; charset=utf-8", respJson, null);
                                return;
                            }

                            WriteResponse(ns, 404, "Not Found", "application/json; charset=utf-8", "{\"ok\":false,\"error\":\"Unknown endpoint\"}", null);
                            return;
                        }

                        WriteResponse(ns, 405, "Method Not Allowed", "text/plain; charset=utf-8", "Method Not Allowed", null);
                    }
                }
                catch
                {
                }
            }
        }

        private bool HasValidLoopbackHost(Dictionary<string, string> headers)
        {
            string host;
            if (!headers.TryGetValue("host", out host) || string.IsNullOrWhiteSpace(host))
            {
                return false;
            }
            string h = host.Trim();
            return string.Equals(h, "127.0.0.1:" + Port, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(h, "localhost:" + Port, StringComparison.OrdinalIgnoreCase);
        }

        private bool ValidateCsrfAndOrigin(Dictionary<string, string> headers)
        {
            string suppliedToken;
            if (!headers.TryGetValue("x-driverplus-token", out suppliedToken) ||
                !ConstantTimeEquals(suppliedToken, _csrfToken))
            {
                return false;
            }

            string origin;
            if (!headers.TryGetValue("origin", out origin) || string.IsNullOrWhiteSpace(origin))
            {
                return false;
            }

            string expectedLoopback = "http://127.0.0.1:" + Port;
            string expectedLocalhost = "http://localhost:" + Port;
            if (!string.Equals(origin.Trim(), expectedLoopback, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(origin.Trim(), expectedLocalhost, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining |
            System.Runtime.CompilerServices.MethodImplOptions.NoOptimization)]
        private static bool ConstantTimeEquals(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }
            return diff == 0;
        }

        private string BuildBootstrapJson(bool forceRefresh)
        {
            EnsureSnapshotLoaded(forceRefresh);

            SystemProfile profile;
            UpdateScanSummary scan;
            List<RuntimeComponent> runtimes;
            List<DiagnosticFinding> diagnostics;

            lock (_cacheLock)
            {
                profile = _cachedProfile;
                scan = _cachedScan;
                runtimes = _cachedRuntimes;
                diagnostics = _cachedDiagnostics;
            }

            var backups = RescueBackupService.ListBackups(null);
            string activeScheme = GameBoostService.GetActivePowerSchemeGuidString();

            var payload = new Dictionary<string, object>
            {
                { "ok", true },
                { "systemProfile", profile },
                { "scanSummary", scan },
                { "runtimes", runtimes },
                { "diagnostics", diagnostics },
                { "backups", backups },
                { "boostActive", GameBoostService.IsBoostCurrentlyActive() },
                { "activePowerPlan", GameBoostService.DescribePowerScheme(activeScheme) }
            };

            var serializer = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
            return serializer.Serialize(payload);
        }

        private void EnsureSnapshotLoaded(bool forceRefresh)
        {
            lock (_cacheLock)
            {
                if (!forceRefresh && _cachedProfile != null && _cachedScan != null)
                {
                    return;
                }
            }

            DriverPlusSettings settings = SettingsService.Load();
            var ignored = new HashSet<string>(settings.IgnoredInstanceIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

            SystemProfile profile = SystemInfoService.CollectSystemProfile();
            List<HardwareDevice> devices = PnpDeviceScanner.ScanDevices(true);
            DriverStoreReport store = DriverStoreInspector.ScanDriverStoreAndHvci();

            // Fast local WUA + cached Catalog scan so initial bootstrap responds < 1.2s; explicit rescan_live queries network
            UpdateScanSummary scan = WindowsUpdateCatalogClient.EnrichWithOfficialUpdatesAsync(
                devices, store, false, settings.WhqlOnly, ignored, null).GetAwaiter().GetResult();

            List<RuntimeComponent> runtimes = GameRuntimeInspector.InspectAllRuntimes();
            List<DiagnosticFinding> diagnostics = HardwareFixService.RunNonDestructiveDiagnostics(scan.Devices, store);

            lock (_cacheLock)
            {
                _cachedProfile = profile;
                _cachedStore = store;
                _cachedScan = scan;
                _cachedRuntimes = runtimes;
                _cachedDiagnostics = diagnostics;
            }
        }

        private string ExecuteActionJson(string rawBody)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
            Dictionary<string, object> req = null;
            try
            {
                req = serializer.Deserialize<Dictionary<string, object>>(rawBody ?? "{}");
            }
            catch
            {
            }
            if (req == null) req = new Dictionary<string, object>();

            string action = req.ContainsKey("action") ? Convert.ToString(req["action"]) : "";
            if (string.Equals(action, "ping", StringComparison.OrdinalIgnoreCase))
            {
                return "{\"ok\":true,\"message\":\"pong\"}";
            }

            if (string.Equals(action, "rescan", StringComparison.OrdinalIgnoreCase))
            {
                bool liveCatalog = req.ContainsKey("liveCatalog") && Convert.ToBoolean(req["liveCatalog"]);
                DriverPlusSettings settings = SettingsService.Load();
                var ignored = new HashSet<string>(settings.IgnoredInstanceIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

                List<HardwareDevice> devices = PnpDeviceScanner.ScanDevices(true);
                DriverStoreReport store = DriverStoreInspector.ScanDriverStoreAndHvci();
                UpdateScanSummary scan = WindowsUpdateCatalogClient.EnrichWithOfficialUpdatesAsync(
                    devices, store, liveCatalog, settings.WhqlOnly, ignored, null).GetAwaiter().GetResult();
                List<DiagnosticFinding> diag = HardwareFixService.RunNonDestructiveDiagnostics(scan.Devices, store);

                lock (_cacheLock)
                {
                    _cachedStore = store;
                    _cachedScan = scan;
                    _cachedDiagnostics = diag;
                }

                return serializer.Serialize(new Dictionary<string, object>
                {
                    { "ok", true },
                    { "message", string.Format("Scanned {0} present devices ({1} updates, {2}ms).", scan.TotalPresentDevices, scan.OutdatedCount, scan.ElapsedMs) },
                    { "scanSummary", scan },
                    { "diagnostics", diag }
                });
            }

            if (string.Equals(action, "fix_tool", StringComparison.OrdinalIgnoreCase))
            {
                string toolId = req.ContainsKey("toolId") ? Convert.ToString(req["toolId"]) : "";
                EnsureSnapshotLoaded(false);
                UpdateScanSummary scan;
                DriverStoreReport store;
                lock (_cacheLock)
                {
                    scan = _cachedScan;
                    store = _cachedStore;
                }
                string msg = HardwareFixService.ExecuteSafeRepairAction(toolId, scan != null ? scan.Devices : null, store);
                List<DiagnosticFinding> refreshed = HardwareFixService.RunNonDestructiveDiagnostics(scan != null ? scan.Devices : null, store);
                lock (_cacheLock)
                {
                    _cachedDiagnostics = refreshed;
                }
                return serializer.Serialize(new Dictionary<string, object>
                {
                    { "ok", true },
                    { "message", msg },
                    { "diagnostics", refreshed }
                });
            }

            if (string.Equals(action, "toggle_boost", StringComparison.OrdinalIgnoreCase))
            {
                bool active = GameBoostService.IsBoostCurrentlyActive();
                string msg = active
                    ? GameBoostService.RestoreNormalMode()
                    : GameBoostService.ActivateGameBoost(false);
                return serializer.Serialize(new Dictionary<string, object>
                {
                    { "ok", true },
                    { "message", msg },
                    { "boostActive", GameBoostService.IsBoostCurrentlyActive() },
                    { "activePowerPlan", GameBoostService.DescribePowerScheme(GameBoostService.GetActivePowerSchemeGuidString()) }
                });
            }

            return "{\"ok\":false,\"error\":\"Unsupported action\"}";
        }

        private static bool TryReadHttpRequest(
            NetworkStream stream,
            out string method,
            out string rawPath,
            out Dictionary<string, string> headers,
            out string body)
        {
            method = "";
            rawPath = "/";
            headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            body = "";

            var headerBuf = new MemoryStream();
            int matched = 0;
            byte[] single = new byte[1];

            while (headerBuf.Length < MaxHeaderBytes)
            {
                int read = stream.Read(single, 0, 1);
                if (read <= 0) break;
                byte b = single[0];
                headerBuf.WriteByte(b);

                if ((matched == 0 && b == '\r') ||
                    (matched == 1 && b == '\n') ||
                    (matched == 2 && b == '\r') ||
                    (matched == 3 && b == '\n'))
                {
                    matched++;
                    if (matched == 4) break;
                }
                else
                {
                    matched = (b == '\r') ? 1 : 0;
                }
            }

            if (matched < 4) return false;

            string headerText = Encoding.ASCII.GetString(headerBuf.ToArray());
            string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return false;

            string[] reqParts = lines[0].Split(' ');
            if (reqParts.Length < 2) return false;
            method = reqParts[0].Trim();
            rawPath = reqParts[1].Trim();

            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon > 0)
                {
                    string k = lines[i].Substring(0, colon).Trim().ToLowerInvariant();
                    string v = lines[i].Substring(colon + 1).Trim();
                    headers[k] = v;
                }
            }

            string lenStr;
            int contentLength = 0;
            if (headers.TryGetValue("content-length", out lenStr))
            {
                if (!int.TryParse(lenStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength))
                {
                    return false;
                }
            }
            if (contentLength < 0 || contentLength > MaxBodyBytes)
            {
                return false;
            }
            if (contentLength > 0)
            {
                byte[] bodyBytes = new byte[contentLength];
                int offset = 0;
                while (offset < contentLength)
                {
                    int n = stream.Read(bodyBytes, offset, contentLength - offset);
                    if (n <= 0) break;
                    offset += n;
                }
                if (offset != contentLength) return false;
                body = Encoding.UTF8.GetString(bodyBytes, 0, offset);
            }

            return true;
        }

        private static void WriteResponse(NetworkStream ns, int statusCode, string statusText, string contentType, string content, string scriptNonce)
        {
            byte[] payload = Encoding.UTF8.GetBytes(content ?? "");
            WriteResponseBytes(ns, statusCode, statusText, contentType, payload, scriptNonce);
        }

        private static void WriteResponseBytes(NetworkStream ns, int statusCode, string statusText, string contentType, byte[] payload, string scriptNonce)
        {
            string scriptPolicy = string.IsNullOrEmpty(scriptNonce) ? "'none'" : "'nonce-" + scriptNonce + "'";
            var sb = new StringBuilder();
            sb.AppendFormat(CultureInfo.InvariantCulture, "HTTP/1.1 {0} {1}\r\n", statusCode, statusText);
            sb.AppendFormat(CultureInfo.InvariantCulture, "Content-Type: {0}\r\n", contentType);
            sb.AppendFormat(CultureInfo.InvariantCulture, "Content-Length: {0}\r\n", payload.Length);
            sb.Append("Cache-Control: no-store\r\n");
            sb.Append("X-Content-Type-Options: nosniff\r\n");
            sb.Append("X-Frame-Options: DENY\r\n");
            sb.Append("Content-Security-Policy: default-src 'self'; style-src 'unsafe-inline'; script-src " + scriptPolicy + "; connect-src 'self'\r\n");
            sb.Append("Connection: close\r\n\r\n");

            byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
            ns.Write(head, 0, head.Length);
            if (payload.Length > 0)
            {
                ns.Write(payload, 0, payload.Length);
            }
            ns.Flush();
        }

        private static string GenerateSecureHexToken(int numBytes)
        {
            byte[] buf = new byte[numBytes];
            using (var rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(buf);
            }
            var sb = new StringBuilder(numBytes * 2);
            foreach (byte b in buf)
            {
                sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private string BuildDashboardHtml(string scriptNonce)
        {
            return @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<meta name=""driverplus-csrf"" content=""" + _csrfToken + @""">
<title>Driver+ Telemetry &amp; Hardware Audit Console</title>
<style>
:root {
  --bg-base: #0B0F17;
  --bg-surface: #111827;
  --bg-elevated: #1E293B;
  --border: #263244;
  --text-primary: #F8FAFC;
  --text-secondary: #CBD5E1;
  --text-muted: #94A3B8;
  --emerald: #10B981;
  --emerald-dim: rgba(16, 185, 129, 0.14);
  --sky: #38BDF8;
  --amber: #F59E0B;
  --rose: #F43F5E;
}
* { box-sizing: border-box; }
body {
  margin: 0;
  font-family: 'Segoe UI Variable Text', 'Segoe UI', system-ui, sans-serif;
  background: var(--bg-base);
  color: var(--text-primary);
  line-height: 1.5;
}
header.topbar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  padding: 16px 28px;
  background: var(--bg-surface);
  border-bottom: 1px solid var(--border);
}
.brand { display: flex; align-items: center; gap: 12px; }
.brand-badge {
  width: 36px; height: 36px; border-radius: 9px;
  background: linear-gradient(135deg, #059669, #10B981);
  display: inline-flex; align-items: center; justify-content: center;
  font-weight: 800; color: #051B14; font-size: 16px;
}
.brand h1 { margin: 0; font-size: 18px; font-weight: 700; letter-spacing: -0.01em; }
.brand small { display: block; color: var(--text-muted); font-size: 12px; }
.actions { display: flex; flex-wrap: wrap; gap: 10px; align-items: center; }
button.btn {
  background: var(--bg-elevated);
  color: var(--text-primary);
  border: 1px solid var(--border);
  border-radius: 8px;
  padding: 8px 14px;
  font-size: 13px;
  font-weight: 600;
  cursor: pointer;
  transition: background 140ms ease, border-color 140ms ease;
}
button.btn:hover { border-color: var(--sky); background: #243247; }
button.btn:focus-visible { outline: 2px solid var(--sky); outline-offset: 2px; }
button.btn-primary {
  background: #059669;
  border-color: #10B981;
  color: #FFFFFF;
}
button.btn-primary:hover { background: #047857; }
nav.tabs {
  display: flex; gap: 8px; padding: 12px 28px;
  background: var(--bg-surface);
  border-bottom: 1px solid var(--border);
  overflow-x: auto;
}
nav.tabs button[role='tab'] {
  background: transparent;
  color: var(--text-secondary);
  border: 1px solid transparent;
  border-radius: 7px;
  padding: 7px 14px;
  font-size: 13px;
  font-weight: 600;
  cursor: pointer;
}
nav.tabs button[role='tab'][aria-selected='true'] {
  background: var(--emerald-dim);
  color: #34D399;
  border-color: rgba(16, 185, 129, 0.4);
}
main { max-width: 1380px; margin: 0 auto; padding: 24px 28px 48px; }
.kpi-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(195px, 1fr));
  gap: 14px;
  margin-bottom: 22px;
}
.kpi-card {
  background: var(--bg-surface);
  border: 1px solid var(--border);
  border-radius: 10px;
  padding: 15px 18px;
}
.kpi-label { color: var(--text-muted); font-size: 12px; text-transform: uppercase; letter-spacing: 0.04em; }
.kpi-val { font-size: 26px; font-weight: 700; margin-top: 4px; font-variant-numeric: tabular-nums; }
.val-emerald { color: #34D399; }
.val-amber { color: #FBBF24; }
.val-rose { color: #FB7185; }
.val-sky { color: #38BDF8; }
.toolbar {
  display: flex; flex-wrap: wrap; gap: 12px; align-items: center; justify-content: space-between;
  margin-bottom: 14px;
}
input.search-box {
  background: var(--bg-surface);
  border: 1px solid var(--border);
  color: var(--text-primary);
  border-radius: 8px;
  padding: 9px 14px;
  min-width: 280px;
  font-size: 13px;
}
input.search-box:focus { outline: 2px solid var(--sky); border-color: transparent; }
.panel { display: none; }
.panel.active { display: block; }
.table-wrap {
  background: var(--bg-surface);
  border: 1px solid var(--border);
  border-radius: 10px;
  overflow-x: auto;
}
table { width: 100%; border-collapse: collapse; font-size: 13px; }
th, td { text-align: left; padding: 11px 14px; border-bottom: 1px solid var(--border); }
th { color: var(--text-muted); font-weight: 600; font-size: 12px; text-transform: uppercase; background: #0E1522; }
tr:hover td { background: rgba(30, 41, 59, 0.45); }
.mono { font-family: 'Cascadia Mono', Consolas, monospace; font-size: 12px; color: var(--text-secondary); }
.badge {
  display: inline-block; padding: 3px 9px; border-radius: 999px;
  font-size: 11px; font-weight: 700;
}
.b-ok { background: rgba(16, 185, 129, 0.16); color: #34D399; }
.b-upd { background: rgba(245, 158, 11, 0.18); color: #FBBF24; }
.b-err { background: rgba(244, 63, 94, 0.20); color: #FB7185; }
.b-ghost { background: rgba(148, 163, 184, 0.16); color: #CBD5E1; }
.cards-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(320px, 1fr));
  gap: 16px;
}
.diag-card {
  background: var(--bg-surface);
  border: 1px solid var(--border);
  border-radius: 10px;
  padding: 18px;
  display: flex;
  flex-direction: column;
  justify-content: space-between;
  gap: 12px;
}
.diag-card h3 { margin: 0 0 4px 0; font-size: 15px; }
.diag-card p { margin: 0; color: var(--text-secondary); font-size: 13px; }
.toast {
  margin-bottom: 16px;
  padding: 11px 16px;
  border-radius: 8px;
  background: rgba(56, 189, 248, 0.14);
  border: 1px solid rgba(56, 189, 248, 0.4);
  color: #E0F2FE;
  font-size: 13px;
  display: none;
}
</style>
</head>
<body>
<header class=""topbar"">
  <div class=""brand"">
    <span class=""brand-badge"" aria-hidden=""true"">D+</span>
    <div>
      <h1>Driver+ Telemetry Console</h1>
      <small id=""subtitle"">100% Official Microsoft Update Catalog &amp; SetupAPI Hardware Inspector</small>
    </div>
  </div>
  <div class=""actions"">
    <button type=""button"" class=""btn"" id=""btnFastScan"">Quick Local Scan</button>
    <button type=""button"" class=""btn btn-primary"" id=""btnLiveScan"">Live MS Catalog Scan</button>
    <button type=""button"" class=""btn"" id=""btnToggleBoost"">Toggle Game Boost</button>
  </div>
</header>

<nav class=""tabs"" role=""tablist"" aria-label=""Driver+ Telemetry Views"">
  <button type=""button"" role=""tab"" aria-selected=""true"" data-tab=""drivers"">Hardware &amp; Drivers</button>
  <button type=""button"" role=""tab"" aria-selected=""false"" data-tab=""runtimes"">Game Runtimes (14)</button>
  <button type=""button"" role=""tab"" aria-selected=""false"" data-tab=""toolbox"">Fix Toolbox &amp; HVCI</button>
  <button type=""button"" role=""tab"" aria-selected=""false"" data-tab=""backups"">Rescue Backups</button>
  <button type=""button"" role=""tab"" aria-selected=""false"" data-tab=""sysinfo"">System Profile</button>
</nav>

<main>
  <div id=""statusBanner"" class=""toast"" role=""status"" aria-live=""polite""></div>

  <section class=""kpi-grid"" aria-label=""Hardware Summary Metrics"">
    <div class=""kpi-card""><div class=""kpi-label"">Present Hardware</div><div class=""kpi-val val-sky"" id=""kpiPresent"">--</div></div>
    <div class=""kpi-card""><div class=""kpi-label"">WHQL Updates Found</div><div class=""kpi-val val-amber"" id=""kpiOutdated"">--</div></div>
    <div class=""kpi-card""><div class=""kpi-label"">Healthy Up-to-Date</div><div class=""kpi-val val-emerald"" id=""kpiHealthy"">--</div></div>
    <div class=""kpi-card""><div class=""kpi-label"">Device Faults / HVCI</div><div class=""kpi-val val-rose"" id=""kpiFaults"">--</div></div>
    <div class=""kpi-card""><div class=""kpi-label"">Power &amp; Game Boost</div><div class=""kpi-val"" style=""font-size:17px;margin-top:9px"" id=""kpiPower"">--</div></div>
  </section>

  <section id=""panel-drivers"" class=""panel active"" aria-label=""Hardware Devices Table"">
    <div class=""toolbar"">
      <label for=""deviceFilter"" style=""font-size:13px;color:var(--text-secondary)"">Filter by device name, class, or VEN/DEV Hardware ID:</label>
      <input id=""deviceFilter"" class=""search-box"" type=""search"" placeholder=""e.g. PCI\VEN_8086, Display, Audio, Unplugged..."">
    </div>
    <div class=""table-wrap"">
      <table>
        <thead>
          <tr>
            <th>Status</th>
            <th>Device Name &amp; Class</th>
            <th>Installed Version</th>
            <th>Target WHQL Update</th>
            <th>Primary Hardware ID (SUBSYS)</th>
          </tr>
        </thead>
        <tbody id=""driversTbody""></tbody>
      </table>
    </div>
  </section>

  <section id=""panel-runtimes"" class=""panel"" aria-label=""Game Runtimes and Redistributables"">
    <div class=""table-wrap"">
      <table>
        <thead>
          <tr>
            <th>State</th>
            <th>Redistributable Package</th>
            <th>Arch</th>
            <th>Installed</th>
            <th>Official Reference</th>
            <th>Winget ID</th>
          </tr>
        </thead>
        <tbody id=""runtimesTbody""></tbody>
      </table>
    </div>
  </section>

  <section id=""panel-toolbox"" class=""panel"" aria-label=""Hardware Fix Toolbox"">
    <div class=""cards-grid"" id=""diagnosticsGrid""></div>
  </section>

  <section id=""panel-backups"" class=""panel"" aria-label=""DriverStore ZIP Backups"">
    <div class=""table-wrap"">
      <table>
        <thead>
          <tr>
            <th>Created (UTC)</th>
            <th>Device</th>
            <th>INF / Version</th>
            <th>SHA-256 Digest</th>
            <th>Archive Path</th>
          </tr>
        </thead>
        <tbody id=""backupsTbody""></tbody>
      </table>
    </div>
  </section>

  <section id=""panel-sysinfo"" class=""panel"" aria-label=""Host System Specifications"">
    <div class=""cards-grid"" id=""sysInfoGrid""></div>
  </section>
</main>

<script nonce=""" + scriptNonce + @""">
(function() {
  var csrf = document.querySelector('meta[name=""driverplus-csrf""]').getAttribute('content');
  var state = { devices: [], runtimes: [], diagnostics: [], backups: [], systemProfile: null };

  function esc(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/""/g, '&quot;').replace(/'/g, '&#39;');
  }

  function notify(msg) {
    var el = document.getElementById('statusBanner');
    el.textContent = msg;
    el.style.display = 'block';
  }

  function postAction(payload) {
    return fetch('/api/action', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-DriverPlus-Token': csrf },
      body: JSON.stringify(payload)
    }).then(function(r) { return r.json(); });
  }

  function renderDrivers() {
    var q = (document.getElementById('deviceFilter').value || '').toLowerCase();
    var rows = [];
    var list = state.devices || [];
    for (var i = 0; i < list.length; i++) {
      var d = list[i];
      var hay = (d.DeviceName + ' ' + d.DeviceClass + ' ' + d.PrimaryHardwareId + ' ' + d.HealthBadge).toLowerCase();
      if (q && hay.indexOf(q) < 0) continue;
      var bCls = !d.IsPresent ? 'b-ghost' : (d.ProblemCode !== 0 ? 'b-err' : (d.HasUpdate ? 'b-upd' : 'b-ok'));
      var upd = d.RecommendedUpdate
        ? ('<strong>' + esc(d.RecommendedUpdate.TargetVersion) + '</strong> <span class=""mono"">(' + esc(d.RecommendedUpdate.Source) + ')</span>')
        : '<span style=""color:var(--text-muted)"">Current</span>';
      rows.push('<tr>' +
        '<td><span class=""badge ' + bCls + '"">' + esc(d.HealthBadge) + '</span></td>' +
        '<td><strong>' + esc(d.DeviceName) + '</strong><br><span style=""color:var(--text-muted);font-size:12px"">' + esc(d.DeviceClass) + ' &bull; ' + esc(d.ProviderName || d.Manufacturer) + '</span></td>' +
        '<td class=""mono"">' + esc(d.DriverVersion || 'Inbox') + '<br><span style=""color:var(--text-muted)"">' + esc(d.DriverDate) + '</span></td>' +
        '<td>' + upd + '</td>' +
        '<td class=""mono"">' + esc(d.PrimaryHardwareId) + '</td>' +
      '</tr>');
      if (rows.length >= 250) break;
    }
    document.getElementById('driversTbody').innerHTML = rows.join('') || '<tr><td colspan=""5"">No matching devices.</td></tr>';
  }

  function renderAll(data) {
    var scan = data.scanSummary || {};
    state.devices = scan.Devices || [];
    state.runtimes = data.runtimes || [];
    state.diagnostics = data.diagnostics || [];
    state.backups = data.backups || [];
    state.systemProfile = data.systemProfile || {};

    document.getElementById('kpiPresent').textContent = scan.TotalPresentDevices != null ? scan.TotalPresentDevices : 0;
    document.getElementById('kpiOutdated').textContent = scan.OutdatedCount != null ? scan.OutdatedCount : 0;
    document.getElementById('kpiHealthy').textContent = scan.UpToDateCount != null ? scan.UpToDateCount : 0;
    document.getElementById('kpiFaults').textContent = (scan.FaultyDeviceCount || 0) + ' / ' + (scan.HvciBlockerCount || 0);
    document.getElementById('kpiPower').textContent = (data.boostActive ? 'BOOST ON • ' : '') + (data.activePowerPlan || 'Balanced');
    if (state.systemProfile && state.systemProfile.ComputerName) {
      document.getElementById('subtitle').textContent =
        state.systemProfile.ComputerName + ' • ' + (state.systemProfile.OsCaption || 'Windows') + ' • ' + (state.systemProfile.CpuName || '');
    }

    renderDrivers();

    document.getElementById('runtimesTbody').innerHTML = state.runtimes.map(function(r) {
      var cls = r.IsInstalled && !r.NeedsUpdate ? 'b-ok' : 'b-upd';
      var lbl = r.IsInstalled ? (r.NeedsUpdate ? 'Update Available' : 'Installed') : 'Missing';
      return '<tr><td><span class=""badge ' + cls + '"">' + lbl + '</span></td>' +
        '<td><strong>' + esc(r.Name) + '</strong></td>' +
        '<td class=""mono"">' + esc(r.Architecture) + '</td>' +
        '<td class=""mono"">' + esc(r.InstalledVersion || '—') + '</td>' +
        '<td class=""mono"">' + esc(r.LatestVersion) + '</td>' +
        '<td class=""mono"">' + esc(r.WingetId) + '</td></tr>';
    }).join('');

    document.getElementById('diagnosticsGrid').innerHTML = state.diagnostics.map(function(f) {
      var cls = f.IssueDetected ? 'b-upd' : 'b-ok';
      return '<article class=""diag-card""><div>' +
        '<div style=""display:flex;justify-content:space-between;align-items:center;gap:8px;margin-bottom:8px"">' +
        '<h3>' + esc(f.ToolName) + '</h3><span class=""badge ' + cls + '"">' + esc(f.StatusText) + '</span></div>' +
        '<p>' + esc(f.Detail) + '</p></div>' +
        '<div><button type=""button"" class=""btn"" data-fix=""' + esc(f.ToolId) + '"">' + esc(f.ActionLabel) + '</button></div></article>';
    }).join('');

    document.getElementById('backupsTbody').innerHTML = (state.backups.length ? state.backups.map(function(b) {
      return '<tr><td class=""mono"">' + esc(b.CreatedAtUtc || '') + '</td><td><strong>' + esc(b.DeviceName) + '</strong></td><td class=""mono"">' + esc((b.InfName || '') + ' ' + (b.DriverVersion || '')) + '</td><td class=""mono"">' + esc((b.Sha256 || '').slice(0, 16)) + '…</td><td class=""mono"">' + esc(b.ZipFilePath) + '</td></tr>';
    }).join('') : '<tr><td colspan=""5"" style=""color:var(--text-muted)"">No DriverStore ZIP backups recorded yet. Backups are created automatically before every driver update.</td></tr>');

    var sp = state.systemProfile || {};
    document.getElementById('sysInfoGrid').innerHTML =
      '<div class=""diag-card""><h3>Processor &amp; Memory</h3><p><strong>CPU:</strong> ' + esc(sp.CpuName) + ' (' + esc(sp.CpuCores) + 'C / ' + esc(sp.CpuLogicalProcessors) + 'T)<br><strong>RAM:</strong> ' + esc((sp.TotalRamGb || 0).toFixed ? sp.TotalRamGb.toFixed(1) : sp.TotalRamGb) + ' GB total</p></div>' +
      '<div class=""diag-card""><h3>Motherboard &amp; OS</h3><p><strong>Board:</strong> ' + esc(sp.Manufacturer) + ' ' + esc(sp.Model) + '<br><strong>BIOS:</strong> ' + esc(sp.BiosVersion) + '<br><strong>OS:</strong> ' + esc(sp.OsCaption) + ' (Build ' + esc(sp.OsBuildNumber) + ')</p></div>' +
      '<div class=""diag-card""><h3>Graphics &amp; Storage</h3><p><strong>GPUs:</strong> ' + esc((sp.Gpus || []).join('; ') || 'Detected via PnP') + '<br><strong>Storage:</strong> ' + esc((sp.StorageDrives || []).join('; ')) + '</p></div>';
  }

  document.querySelectorAll('nav.tabs button[role=""tab""]').forEach(function(btn) {
    btn.addEventListener('click', function() {
      var target = btn.getAttribute('data-tab');
      document.querySelectorAll('nav.tabs button[role=""tab""]').forEach(function(b) {
        b.setAttribute('aria-selected', b === btn ? 'true' : 'false');
      });
      document.querySelectorAll('.panel').forEach(function(p) {
        p.classList.toggle('active', p.id === 'panel-' + target);
      });
    });
  });

  document.getElementById('deviceFilter').addEventListener('input', renderDrivers);

  document.getElementById('btnFastScan').addEventListener('click', function() {
    notify('Running local 64-bit SetupAPI + WUA hardware scan...');
    postAction({ action: 'rescan', liveCatalog: false }).then(function(res) {
      if (res.scanSummary) {
        renderAll({
          scanSummary: res.scanSummary,
          runtimes: state.runtimes,
          diagnostics: res.diagnostics || state.diagnostics,
          backups: state.backups,
          systemProfile: state.systemProfile
        });
      }
      notify(res.message || 'Local scan complete.');
    });
  });

  document.getElementById('btnLiveScan').addEventListener('click', function() {
    notify('Querying live Microsoft Update Catalog over HTTPS for PCI/USB/HDAUDIO Hardware IDs...');
    postAction({ action: 'rescan', liveCatalog: true }).then(function(res) {
      if (res.scanSummary) {
        renderAll({
          scanSummary: res.scanSummary,
          runtimes: state.runtimes,
          diagnostics: res.diagnostics || state.diagnostics,
          backups: state.backups,
          systemProfile: state.systemProfile
        });
      }
      notify(res.message || 'Live Microsoft Update Catalog scan complete.');
    });
  });

  document.getElementById('btnToggleBoost').addEventListener('click', function() {
    postAction({ action: 'toggle_boost' }).then(function(res) {
      document.getElementById('kpiPower').textContent = (res.boostActive ? 'BOOST ON • ' : '') + (res.activePowerPlan || 'Balanced');
      notify(res.message || 'Updated Game Boost state.');
    });
  });

  document.getElementById('diagnosticsGrid').addEventListener('click', function(ev) {
    var btn = ev.target.closest('button[data-fix]');
    if (!btn) return;
    var toolId = btn.getAttribute('data-fix');
    notify('Running safe repair tool: ' + toolId + '...');
    postAction({ action: 'fix_tool', toolId: toolId }).then(function(res) {
      notify(res.message || 'Repair tool completed.');
    });
  });

  fetch('/api/bootstrap').then(function(r) { return r.json(); }).then(renderAll);
})();
</script>
</body>
</html>";
        }
    }
}
