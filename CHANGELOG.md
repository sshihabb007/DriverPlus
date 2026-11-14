# Changelog

All notable changes to **Driver+** are documented in this file.

## [1.0.0] - 2026-11-14

### Added
- **Native 64-Bit (`PE32+`) Single-Pass Hardware & Ghost Scanner**: Direct `SetupAPI` (`SetupDiGetClassDevsW`) + `CfgMgr32` (`CM_Locate_DevNodeW`, `CM_Get_DevNode_Status`) inventory of present devices, unplugged ghost devnodes, `CM_PROB_*` fault codes (`1–52`), and `SetupAPI` `HRESULT` diagnostics without 32-bit `WOW64` thunking.
- **4-Part OEM `SUBSYS` Specificity & Anti-Downgrade Engine**: Prioritizes board/laptop-specific `PCI\VEN_xxxx&DEV_yyyy&SUBSYS_zzzzzzzz` WHQL drivers over generic 2-part silicon matches, isolates foreign-OEM `SUBSYS` packages, prevents version downgrades, and resolves official `https://` vendor support portals.
- **100% Official Dual-Source WHQL Discovery**: Queries local Windows Update Agent COM (`Microsoft.Update.Session`) alongside concurrent TLS 1.2+ searches against `https://www.catalog.update.microsoft.com` filtered to the host OS family (Windows 11 vs. Windows 10).
- **14 Game Runtimes & Event ID 1000 Crash DLL Doctor**: Audits VC++ `2010–2022` (`x86`/`x64`), DirectX End-User Runtimes (`d3dx9_43.dll` / `XInput`), `.NET Framework 4.8+`, `.NET Desktop Runtime 8/9`, OpenAL, NVIDIA PhysX, and OpenJDK x64, plus maps Windows Application Event Log `Event ID 1000` faulting DLL crashes to official `winget` manifests.
- **Rescue Center & Air-Gapped Offline Hardware Profiles**: Pre-update Windows System Restore checkpoints (`SRSetRestorePointW`), DriverStore `.zip` backups with `.sha256` sidecars, one-click `DiRollbackDriver` rollback, and JSON offline hardware profile export/import.
- **6-Card Hardware Fix Toolbox & Windows 11 HVCI Blocker Auditor**: Includes CoreAudio `IAudioEndpointVolume` unmute & `Audiosrv` verification, Device Error `1–52` remediation, unplugged USB/HID ghost cleanup, DNS flush + NIC reset, native display mode reset, and Windows 11 Memory Integrity (HVCI) incompatible `.sys` driver detection.
- **Crash-Safe Game Boost & Direct3D Fullscreen Focus Guard**: Persists pre-boost power plan and service state prior to switching `PowrProf` to `High Performance`, and suppresses tray notifications during fullscreen Direct3D sessions (`SHQueryUserNotificationState`).
- **60fps DirectX WPF Desktop UI, Native x64 Setup Wizard & CLI Modes**: Virtualized dark glassmorphic WPF interface (`266 KB` portable EXE), `158 KB` native x64 Setup Wizard & clean owned-file Uninstaller (`--silent`, `--install-dir`, `--uninstall`), headless `--self-test` and `--scan-json` automation flags, and a loopback `127.0.0.1` web telemetry portal.

### Security
- **Cryptographic Driver Supply-Chain Gate**: Enforces `*.windowsupdate.com` / `*.microsoft.com` `https://` allowlists across redirects, caps CAB downloads at `1.5 GiB`, verifies 40-hex SHA-1 CAB digests prior to extraction, and enforces `WinVerifyTrust` (`WINTRUST_ACTION_GENERIC_VERIFY_V2`) Authenticode signature validation on `.cat`/`.sys` files before `newdev.dll!DiInstallDriverW`.
- **Archive, Uninstaller & Loopback Portal Hardening**: Rejects Zip-Slip traversal entries during backup/installer extraction, restricts uninstallation strictly to owned `Driver+` files, enforces `Host`/`Origin` DNS-rebinding guards + per-response CSP nonces + 256-bit CSRF tokens on the loopback portal, sanitizes imported offline profile URLs to `https://`, and compiles all binaries with `DYNAMIC_BASE`, `HIGH_ENTROPY_VA`, `NX_COMPAT`, and `NO_SEH`.
