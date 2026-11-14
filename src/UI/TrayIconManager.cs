using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using DriverPlus.Services;

namespace DriverPlus.UI
{
    public sealed class TrayIconManager : IDisposable
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly Icon _ownedIcon;
        private readonly Action _onOpenWindow;
        private readonly Action _onQuickScan;
        private readonly Action _onToggleBoost;
        private readonly Action _onFixAudio;
        private readonly Action _onOpenWebPortal;
        private readonly Action _onExitApp;

        public TrayIconManager(
            Action onOpenWindow,
            Action onQuickScan,
            Action onToggleBoost,
            Action onFixAudio,
            Action onOpenWebPortal,
            Action onExitApp)
        {
            _onOpenWindow = onOpenWindow;
            _onQuickScan = onQuickScan;
            _onToggleBoost = onToggleBoost;
            _onFixAudio = onFixAudio;
            _onOpenWebPortal = onOpenWebPortal;
            _onExitApp = onExitApp;

            _ownedIcon = LoadTrayIcon();
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open Driver+ Console", null, (s, e) => SafeInvoke(_onOpenWindow));
            menu.Items.Add("Quick Hardware & WHQL Scan", null, (s, e) => SafeInvoke(_onQuickScan));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Toggle Game Boost Mode", null, (s, e) => SafeInvoke(_onToggleBoost));
            menu.Items.Add("Fix No Sound & Play Chime", null, (s, e) => SafeInvoke(_onFixAudio));
            menu.Items.Add("Open Live Web Telemetry (127.0.0.1)", null, (s, e) => SafeInvoke(_onOpenWebPortal));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit Driver+", null, (s, e) => SafeInvoke(_onExitApp));

            _notifyIcon = new NotifyIcon
            {
                Icon = _ownedIcon ?? SystemIcons.Shield,
                Text = "Driver+ — Official WHQL Hardware & Driver Updater",
                ContextMenuStrip = menu,
                Visible = true
            };
            _notifyIcon.DoubleClick += (s, e) => SafeInvoke(_onOpenWindow);
        }

        public void ShowToastIfAllowed(string title, string message, bool silentGamingGuard)
        {
            try
            {
                if (silentGamingGuard && GameBoostService.IsFullscreenGameOrD3DActive())
                {
                    // Silent Focus Gaming Guard: never interrupt fullscreen D3D / presentation apps
                    return;
                }
                _notifyIcon.ShowBalloonTip(3500, title, message, ToolTipIcon.Info);
            }
            catch
            {
            }
        }

        private static Icon LoadTrayIcon()
        {
            try
            {
                string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                {
                    Icon extracted = Icon.ExtractAssociatedIcon(exePath);
                    if (extracted != null) return extracted;
                }
            }
            catch
            {
            }
            return null;
        }

        private static void SafeInvoke(Action action)
        {
            if (action != null)
            {
                try { action(); } catch { }
            }
        }

        public void Dispose()
        {
            try
            {
                _notifyIcon.Visible = false;
                if (_notifyIcon.ContextMenuStrip != null) _notifyIcon.ContextMenuStrip.Dispose();
                _notifyIcon.Dispose();
                if (_ownedIcon != null) _ownedIcon.Dispose();
            }
            catch
            {
            }
        }
    }
}
