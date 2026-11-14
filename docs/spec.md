# Driver+ — Clean-Room Open-Source Windows Driver Updater Specification

## 1. Objective & Clean-Room Mandate
**Driver+** (`v1.0.0`) is a native 64-bit, open-source Windows Driver & Hardware Maintenance suite engineered from clean-room OS API findings. It provides full functional parity with commercial driver updaters while eliminating legacy 32-bit `WOW64` proxy executables, disabled exploit mitigations, disk-based `.ini` IPC, adware popups, and bundled third-party installers.

### Strict Clean-Room & Security Constraints
- **100% Legitimate Public Update Sources Only**:
  1. **Windows Update Agent COM (`Microsoft.Update.Session`)**: `IsInstalled=0 and Type='Driver'`
  2. **Microsoft Update Catalog (`https://www.catalog.update.microsoft.com`)**: Live HTTPS Hardware ID (`PCI\VEN_...`, `USB\VID_...`, `HDAUDIO\...`) search and `DownloadDialog.aspx` resolution to official Microsoft-hosted WHQL `.cab` packages (`*.download.windowsupdate.com`).
  3. **Official Runtime Manifests & `winget`**: Microsoft Visual C++ (`2008–2022` x86/x64), DirectX End-User Runtime, `.NET Framework 4.8`, `.NET Desktop Runtime 8/9`, OpenAL, NVIDIA PhysX, XNA 4.0, and OpenJDK via `winget` and official vendor HTTPS CDNs.
  4. **Never** query proprietary IObit endpoints (`driverboosterscan.com`), decrypt vendor containers, or copy proprietary assets.
- **Native x64 Hardening**: Compiled with `/platform:x64 /optimize+ /highentropyva+` so `DYNAMIC_BASE` (ASLR), `HIGH_ENTROPY_VA` (64-bit ASLR), and `NX_COMPAT` (Hardware DEP) are all enabled.
- **Fail-Closed Driver Mutation**: Every driver install or removal requires explicit user action, creates a verified DriverStore `.zip` backup (and optional Windows System Restore point) before staging, validates Authenticode signatures via `WinVerifyTrust`, and uses supported `newdev.dll!DiInstallDriverW` / `DiRollbackDriver` APIs rather than deprecated `DIFxAPI`.

## 2. Core Functional Capabilities
1. **PnP & DriverStore Scanner (`PnpDeviceScanner` + `DriverStoreInspector`)**:
   - Enumerates present and disconnected/ghost PnP devnodes via `SetupAPI.dll` & `CfgMgr32.dll`.
   - Reads Hardware IDs, Compatible IDs, Device Class, Manufacturer, Installed `DriverVersion`, `DriverDate`, `InfPath` (`oemXX.inf`), `MatchingDeviceId`, WHQL/Signer metadata, and `CM_PROB_*` status codes (`0` healthy, `1..52` fault).
   - Cross-references DriverStore packages (`pnputil` / `FileRepository`) and flags `.sys` drivers incompatible with Windows 11 HVCI Memory Integrity.
2. **Dual-Server Official Update Engine (`WindowsUpdateCatalogClient` + `DriverVersionComparer`)**:
   - Queries local/online `Microsoft.Update.Session` (`Type='Driver'`) and live `catalog.update.microsoft.com` with bounded concurrency, OS-version filtering (Windows 11 vs Windows 10), exact 4-part `SUBSYS` Hardware ID priority over 2-part generic IDs, strict version/date comparison (preventing accidental downgrades), and local JSON caching.
3. **Safe Installer & Rescue Center (`DriverInstallOrchestrator` + `RescueBackupService`)**:
   - Pre-install Windows System Restore checkpoint + DriverStore OEM `.zip` export.
   - Official `.cab` download from `*.windowsupdate.com`, SHA-1 filename digest check + `WinVerifyTrust` signature verification, `expand.exe` extraction, and `DiInstallDriverW` installation.
   - Driver Restore from `.zip`, PnP `DiRollbackDriver`, and **Offline Driver Profile Export / Bundle Import** for air-gapped PCs.
4. **Game Runtimes & Crash DLL Doctor (`GameRuntimeInspector`)**:
   - Audits 14 redistributables (VC++ 2008–2022 x86/x64, DirectX, `.NET`, OpenAL, PhysX, XNA, Java) + scans Windows Application Event Log (`Event ID 1000`) for recent missing/faulting runtime DLL crashes, offering 1-click official `winget` repair.
5. **6-in-1 Hardware & OS Fix Toolbox (`HardwareFixService`)**:
   - *Fix No Sound* (CoreAudio `IMMDeviceEnumerator` COM endpoint check/unmute + Audio service check + test tone)
   - *Fix Device Error* (`CM_PROB_*` `1..52` root-cause & devnode re-enumerate/re-enable)
   - *Clean Unplugged Ghost Devices* (Disconnected PnP devnode listing & selective `DIF_REMOVE` cleanup)
   - *Fix Network Failure* (NIC status + DNS flush / Winsock check)
   - *Fix Bad Resolution* (`EnumDisplaySettingsExW` current vs native display mode check & apply)
   - *HVCI Memory Integrity Auditor* (Core Isolation registry state + incompatible DriverStore `.sys` backup & cleanup)
6. **Game Boost & System Profiler (`GameBoostService` + `SystemInfoService`)**:
   - Switches active power plan (`PowrProf.dll`) to High/Ultimate Performance and optionally stops curated non-essential background services, persisting prior state to `boost-restore.json` for 1-click crash-safe restoration.
   - Fullscreen Focus detection (`SHQueryUserNotificationState`) + detailed CPU/GPU/RAM/Board/BIOS/Disk/OS hardware profile export (`.json` / `.md`).
7. **Dual High-Performance Interfaces**:
   - **Primary**: Native 64-bit DirectX-accelerated WPF Desktop App (`DriverPlus.exe`, `< 45 MB` idle RAM, `< 500 KB` binary, 60fps virtualized lists, System Tray quick menu) + Setup Wizard (`DriverPlus-Setup-1.0.0-x64.exe`).
   - **Companion**: Loopback-only (`127.0.0.1`) CSRF-protected Web Telemetry Dashboard (`DriverPlus.exe --serve-ui`) for browser inspection and automated E2E verification.
