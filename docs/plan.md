# Driver+ Clean-Room Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans with superpowers:test-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build **Driver+ (`v1.0.0`)**, a native 64-bit open-source Windows Driver Updater, Rescue Center, Game Runtime Inspector, 6-in-1 Hardware/HVCI Fixer, and Game Boost desktop suite with `< 45 MB` idle RAM, full x64 ASLR/DEP hardening, dual official Microsoft update servers, and a Setup Installer Wizard.

**Architecture:** Modular native 64-bit C# (`csc.exe` `/platform:x64 /optimize+ /highentropyva+ /langversion:5`) core library compiled into a DirectX-accelerated WPF desktop executable (`DriverPlus.exe`), a standalone test binary (`DriverPlusTests.exe`), an optional loopback (`127.0.0.1`) CSRF-guarded Web Telemetry Portal (`--serve-ui`), and a self-contained Windows Setup/Uninstall Wizard (`DriverPlus-Setup-1.0.0-x64.exe`).

**Tech Stack:** Native x64 C# 5.0 (`.NET Framework 4.8` runtime built into Windows 10/11), Win32 `SetupAPI` / `CfgMgr32` / `newdev` / `WinTrust` / `PowrProf` / CoreAudio COM / WUA COM, HTTPS `catalog.update.microsoft.com` parser, WPF DirectX UI (`PresentationFramework.dll`), Playwright & Chrome DevTools MCP for web portal verification, and REA MCP for PE hardening verification.

**Spec:** `d:\xampp\htdocs\REA\Driver+\docs\spec.md`

## Global Constraints
- Compiler must be `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` with `/platform:x64 /optimize+ /highentropyva+ /langversion:5` (strict C# 5.0 syntax: no `$"..."`, `?.`, `nameof`, or expression-bodied members).
- Update sources must be 100% official public Microsoft / vendor endpoints (`Microsoft.Update.Session`, `https://www.catalog.update.microsoft.com`, `*.download.windowsupdate.com`, `winget`) — zero proprietary third-party commercial endpoints.
- Every `.cab` download URL extracted from `DownloadDialog.aspx` must be validated against an allowlist of `*.windowsupdate.com` / `*.microsoft.com` over HTTPS before download.
- Local web portal (`--serve-ui`) must bind strictly to `127.0.0.1`, enforce `Origin`/`Host` validation, and require a session CSRF token on `POST` mutations.
- No live hardware mutation (driver install, ghost removal, power plan switch, service stop) may occur during automated tests (`test.ps1` / `--self-test`).

## Review Focus
1. **Malformed or non-4-part Windows driver versions (`"24.40.0.4"`, `"10.0.26100.1"`, `"2026.1"`, `""`, `"N/A"`)** — `DriverVersionComparer.CompareVersions` must normalize 1–4 numeric segments without throwing and treat unparseable strings as unknown rather than newer. (Pinned in Task 1)
2. **Generic 2-part Hardware ID (`PCI\VEN_8086&DEV_51F0`) accidentally overriding a newer OEM-customized 4-part `SUBSYS` driver or proposing an older date/version downgrade** — `DriverVersionComparer.SelectBestCandidate` must reject downgrades and rank 4-part `SUBSYS` matches strictly above generic 2-part matches unless explicitly overridden. (Pinned in Task 1)
3. **Microsoft Update Catalog HTML containing Windows 10 vs Windows 11 rows or untrusted URLs** — `WindowsUpdateCatalogClient.ParseSearchHtml` and `ParseDownloadDialogHtml` must filter rows to match the host OS family (`Windows 11` on build >= 22000, `Windows 10` otherwise) and reject any `.cab` URL outside `*.windowsupdate.com` / `*.microsoft.com`. (Pinned in Task 2)
4. **Zip-Slip or path traversal when exporting/restoring DriverStore backups** — `RescueBackupService.ValidateSafeArchiveEntry` must reject entries containing `..`, rooted paths, or drive colons before extraction. (Pinned in Task 3)
5. **Interrupted Game Boost session leaving Windows power scheme or optional services altered after reboot/crash** — `GameBoostService` must persist `boost-restore.json` *before* mutating any power scheme or service and recover cleanly on startup/restore. (Pinned in Task 4)

---

### Task 1: Domain Models, Version/HWID Ranker, Problem Code Catalog & Vendor Resolver (TDD)

**Files:**
- Create: `src/Core/DriverModels.cs`
- Create: `src/Core/DriverVersionComparer.cs`
- Create: `src/Core/ProblemCodeCatalog.cs`
- Create: `src/Core/PciVendorResolver.cs`
- Test: `tests/DriverPlusTests.cs`
- Create: `test.ps1`

**Interfaces:**
- Produces:
  - `DriverVersionComparer.TryParseDriverVersion(string raw, out int[] parts) -> bool`
  - `DriverVersionComparer.CompareVersions(string installed, string candidate) -> int`
  - `DriverVersionComparer.NormalizeHardwareId(string hwid) -> string`
  - `DriverVersionComparer.GetHardwareIdSpecificity(string hwid) -> int`
  - `DriverVersionComparer.SelectBestCandidate(HardwareDevice device, IEnumerable<DriverCandidate> candidates, bool whqlOnly) -> DriverCandidate`
  - `ProblemCodeCatalog.DescribeProblemCode(int code) -> ProblemCodeInfo`
  - `ProblemCodeCatalog.DescribeInstallHResult(uint hr) -> HResultInfo`
  - `PciVendorResolver.ResolveVendor(string hwid, string provider) -> VendorSupportInfo`

- [ ] **Step 1: Write failing tests in `tests/DriverPlusTests.cs` + `test.ps1`** asserting version parsing (`1–4` segments + malformed `"N/A"`), downgrade rejection, 4-part `SUBSYS` vs 2-part specificity ranking, `CM_PROB_*` (`0, 1, 10, 19, 22, 28, 43, 45, 52`) lookup, HRESULT (`0x00000000`, `0xE000020B`, `0x80240017`, `0x800B0100`) lookup, and PCI/USB vendor resolution (`VEN_8086`, `VEN_10DE`, `VEN_10EC`).
- [ ] **Step 2: Run `powershell -ExecutionPolicy Bypass -File test.ps1` and verify RED failure.**
- [ ] **Step 3: Implement `DriverModels.cs`, `DriverVersionComparer.cs`, `ProblemCodeCatalog.cs`, and `PciVendorResolver.cs`.**
- [ ] **Step 4: Re-run `test.ps1` and verify GREEN pass.**

---

### Task 2: Dual Official Microsoft Update Catalog Parsers & Live PnP / DriverStore / HVCI Scanners (TDD)

**Files:**
- Create: `src/Native/NativeMethods.cs`
- Create: `src/Services/PnpDeviceScanner.cs`
- Create: `src/Services/DriverStoreInspector.cs`
- Create: `src/Services/WindowsUpdateCatalogClient.cs`
- Modify: `tests/DriverPlusTests.cs`

**Interfaces:**
- Consumes: Task 1 models & `DriverVersionComparer` / `ProblemCodeCatalog` / `PciVendorResolver`
- Produces:
  - `PnpDeviceScanner.ScanDevices(bool includeGhostDevices) -> List<HardwareDevice>`
  - `DriverStoreInspector.ScanDriverStoreAndHvci() -> DriverStoreReport`
  - `WindowsUpdateCatalogClient.ParseSearchHtml(string html, int osBuildNumber, string queriedHwid) -> List<DriverCandidate>`
  - `WindowsUpdateCatalogClient.ParseDownloadDialogHtml(string html) -> List<string>`
  - `WindowsUpdateCatalogClient.EnrichWithOfficialUpdatesAsync(IList<HardwareDevice> devices, bool queryLiveCatalog, Action<string, int> progress) -> Task<UpdateScanSummary>`

- [ ] **Step 1: Write failing tests in `tests/DriverPlusTests.cs`** covering:
  - `ParseSearchHtml` filtering `Windows 11` vs `Windows 10` rows on real Microsoft Update Catalog HTML structure (`<tr id="<guid>_R0">`), parsing version `24.40.0.4`, date `4/12/2026`, size `23.3 MB`, and skipping non-driver rows.
  - `ParseDownloadDialogHtml` accepting `https://catalog.s.download.windowsupdate.com/.../*.cab` and rejecting `http://` or non-Microsoft domains (`https://evil.example.com/drv.cab`).
  - `DriverStoreInspector.ParsePnpUtilOutput` + HVCI incompatible `.sys` matching (`ssdudfu.sys`, `via_usb_ets.sys`, `mbtusbser.sys`, etc.).
  - Read-only live Windows 64-bit `PnpDeviceScanner.ScanDevices(true)` returning `> 20` present devices with populated Hardware IDs and driver versions on this host.
- [ ] **Step 2: Run `test.ps1` and verify RED failure.**
- [ ] **Step 3: Implement `NativeMethods.cs`, `PnpDeviceScanner.cs`, `DriverStoreInspector.cs`, and `WindowsUpdateCatalogClient.cs`.**
- [ ] **Step 4: Run `test.ps1` and verify GREEN pass.**

---

### Task 3: Rescue Center, Safe Driver Installer, Offline Profile & Zip-Slip Protection (TDD)

**Files:**
- Create: `src/Services/RescueBackupService.cs`
- Create: `src/Services/DriverInstallOrchestrator.cs`
- Modify: `tests/DriverPlusTests.cs`

**Interfaces:**
- Produces:
  - `RescueBackupService.ValidateSafeArchiveEntry(string entryFullName) -> bool`
  - `RescueBackupService.ExportOfflineProfile(IList<HardwareDevice> devices, SystemProfile sys, string outputPath) -> void`
  - `RescueBackupService.ImportOfflineProfile(string inputPath) -> OfflineHardwareManifest`
  - `RescueBackupService.CreateDriverBackupZip(HardwareDevice device, string sourceDir, string backupRootDir) -> BackupRecord`
  - `DriverInstallOrchestrator.ExtractExpectedSha1FromCabUrl(string cabUrl) -> string`
  - `DriverInstallOrchestrator.VerifyAuthenticodeFile(string filePath) -> SignatureCheckResult`

- [ ] **Step 1: Write failing tests in `tests/DriverPlusTests.cs`** for Zip-Slip path rejection (`../evil.sys`, `C:\Windows\evil.sys`, `\abs\file.inf`), round-trip `.zip` driver backup creation/manifest verification in a temp sandbox, Offline Hardware Profile JSON round-trip export/import, SHA-1 extraction from Microsoft `.cab` URLs (`..._2a7b7fffc6bd37f5093c6a0261f9aea45ac475f4.cab`), and `WinVerifyTrust` check on `C:\Windows\System32\kernel32.dll`.
- [ ] **Step 2: Run `test.ps1` and verify RED failure.**
- [ ] **Step 3: Implement `RescueBackupService.cs` and `DriverInstallOrchestrator.cs`.**
- [ ] **Step 4: Run `test.ps1` and verify GREEN pass.**

---

### Task 4: Game Runtimes + Crash DLL Doctor, 6-in-1 Hardware Fixer, Game Boost & System Profiler (TDD)

**Files:**
- Create: `src/Services/GameRuntimeInspector.cs`
- Create: `src/Services/HardwareFixService.cs`
- Create: `src/Services/GameBoostService.cs`
- Create: `src/Services/SystemInfoService.cs`
- Create: `src/Services/SettingsService.cs`
- Modify: `tests/DriverPlusTests.cs`

**Interfaces:**
- Produces:
  - `GameRuntimeInspector.InspectAllRuntimes() -> List<RuntimeComponent>`
  - `GameRuntimeInspector.MapFaultingModuleToRuntime(string moduleName) -> string`
  - `HardwareFixService.RunNonDestructiveDiagnostics(IList<HardwareDevice> devices, DriverStoreReport storeReport) -> List<DiagnosticFinding>`
  - `GameBoostService.BuildRestoreSnapshot(string activeSchemeGuid, IList<string> stoppedServices) -> BoostSnapshot`
  - `GameBoostService.SaveAndLoadSnapshot(string path, BoostSnapshot snapshot) -> BoostSnapshot`
  - `SystemInfoService.CollectSystemProfile() -> SystemProfile`
  - `SystemInfoService.FormatMarkdownReport(SystemProfile profile, UpdateScanSummary summary) -> string`

- [ ] **Step 1: Write failing tests in `tests/DriverPlusTests.cs`** for runtime catalog inspection (`>= 12` runtime definitions, accurate detection of installed `.NET` / `VC++` on host), faulting-DLL crash mapping (`vcruntime140.dll`, `d3dx9_43.dll`, `openal32.dll`), non-destructive hardware diagnostics (audio, display modes, network, ghost count, HVCI status), crash-safe `BoostSnapshot` persistence before mutation, and `SystemInfoService` live hardware profile + Markdown export.
- [ ] **Step 2: Run `test.ps1` and verify RED failure.**
- [ ] **Step 3: Implement `GameRuntimeInspector.cs`, `HardwareFixService.cs`, `GameBoostService.cs`, `SystemInfoService.cs`, and `SettingsService.cs`.**
- [ ] **Step 4: Run `test.ps1` and verify GREEN pass.**

---

### Task 5: Loopback Web Portal (`--serve-ui`), 60fps DirectX WPF Desktop UI, Setup Wizard & End-to-End Verification

**Files:**
- Create: `scripts/generate_icon.py`
- Create: `src/app.manifest`
- Create: `src/Services/LocalWebPortalServer.cs`
- Create: `src/UI/ThemeTokens.cs`
- Create: `src/UI/TrayIconManager.cs`
- Create: `src/UI/MainWindow.cs`
- Create: `src/Program.cs`
- Create: `installer/DriverPlusSetupWizard.cs`
- Create: `build.ps1`
- Create: `README.md`
- Modify: `tests/DriverPlusTests.cs`

**Interfaces:**
- Produces:
  - `dist\DriverPlus.exe`, `dist\DriverPlus-Portable-1.0.0-x64.exe`, `dist\DriverPlus-Setup-1.0.0-x64.exe`
  - CLI modes: `--self-test`, `--scan-json <path>`, `--serve-ui --port <port>`, `--minimized`

- [ ] **Step 1: Write failing tests in `tests/DriverPlusTests.cs`** verifying `LocalWebPortalServer` loopback binding, CSRF rejection (`403` without `X-DriverPlus-Token`), Origin validation, and `GET /api/bootstrap` live JSON schema.
- [ ] **Step 2: Run `test.ps1` and verify RED failure.**
- [ ] **Step 3: Generate crisp multi-size `assets/driverplus.ico` + `driverplus.png`, implement `LocalWebPortalServer.cs`, `ThemeTokens.cs`, `TrayIconManager.cs`, `MainWindow.cs`, `Program.cs`, `DriverPlusSetupWizard.cs`, `build.ps1`, and `README.md`.**
- [ ] **Step 4: Run `powershell -ExecutionPolicy Bypass -File build.ps1 -SkipDesktopCopy`** to verify x64 compilation, full TDD self-test suite, Setup Wizard compilation, and sandbox silent install + SHA-256 verification + clean uninstall!
- [ ] **Step 5: Verify with MCP servers & subagents:**
  - Run **REA MCP** (`inspect_binary_hardening`, `audit_release_exposure`) on `dist\DriverPlus.exe` and `dist\DriverPlus-Setup-1.0.0-x64.exe` to prove `HIGH_ENTROPY_VA` ASLR + `NX_COMPAT` DEP enabled.
  - Benchmark cold start, full hardware + live Microsoft Update Catalog scan latency, binary size, and idle RAM of `DriverPlus.exe` vs `DriverBooster.exe` (`198.94 MB`).
  - Launch `DriverPlus.exe --serve-ui` and verify accessibility, zero console/network errors, and live hardware telemetry in real Chrome via **Playwright / Chrome DevTools MCP**.
  - Dispatch **`staff-code-reviewer`** & **`rea-security-auditor`** subagents to review the final clean-room codebase and fix any actionable findings.
