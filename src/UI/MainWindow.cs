using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using DriverPlus.Core;
using DriverPlus.Native;
using DriverPlus.Services;

namespace DriverPlus.UI
{
    public sealed class MainWindow : Window
    {
        private DriverPlusSettings _settings;
        private SystemProfile _systemProfile;
        private DriverStoreReport _storeReport;
        private UpdateScanSummary _scanSummary;
        private List<RuntimeComponent> _runtimes;
        private List<CrashDllEvent> _crashEvents;
        private List<DiagnosticFinding> _diagnostics;
        private LocalWebPortalServer _webServer;
        private TrayIconManager _trayManager;

        private string _activeTab = "drivers";
        private string _deviceModeFilter = "all"; // all, updates, faults, ghosts
        private bool _isScanning;
        private bool _isBusy;
        private bool _allowRealExit;

        // Chrome & Navigation
        private StackPanel _navStack;
        private TextBlock _statusText;
        private ProgressBar _progressBar;
        private Grid _workspaceHost;

        // Drivers tab controls
        private TextBlock _kpiPresentVal;
        private TextBlock _kpiUpdatesVal;
        private TextBlock _kpiHealthyVal;
        private TextBlock _kpiFaultsVal;
        private TextBlock _kpiElapsedVal;
        private TextBox _searchBox;
        private CheckBox _liveCatalogCheck;
        private Button _btnScanNow;
        private Button _btnUpdateAll;
        private ListBox _deviceListBox;
        private StackPanel _inspectorPanel;

        // Other tab dynamic hosts
        private StackPanel _runtimesListHost;
        private Border _crashDoctorBanner;
        private ListBox _backupsListBox;
        private WrapPanel _toolboxWrapHost;
        private TextBlock _boostStatusHeadline;
        private TextBlock _boostDetailText;
        private CheckBox _boostStopServicesCheck;
        private StackPanel _sysInfoHost;
        private ListBox _historyListBox;

        public MainWindow(bool startMinimized)
        {
            _settings = SettingsService.Load();
            _scanSummary = new UpdateScanSummary();
            _runtimes = new List<RuntimeComponent>();
            _crashEvents = new List<CrashDllEvent>();
            _diagnostics = new List<DiagnosticFinding>();

            Title = "Driver+ — Official WHQL Hardware & Driver Updater";
            Width = 1280;
            Height = 800;
            MinWidth = 1020;
            MinHeight = 660;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = ThemeTokens.BgBase;
            Foreground = ThemeTokens.TextPrimary;
            FontFamily = ThemeTokens.UiFont;

            TryApplyWindowIcon();
            BuildLayout();

            SourceInitialized += OnWindowSourceInitialized;
            Loaded += async (s, e) =>
            {
                InitTrayIcon();
                if (startMinimized)
                {
                    Hide();
                }
                await RunFullScanWorkflowAsync(_settings.QueryLiveMicrosoftCatalog);
            };
            Closing += OnWindowClosing;
        }

        private void TryApplyWindowIcon()
        {
            try
            {
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                string icoCandidate = Path.Combine(exeDir, "driverplus.ico");
                if (!File.Exists(icoCandidate))
                {
                    icoCandidate = Path.Combine(exeDir, @"..\assets\driverplus.ico");
                }
                if (File.Exists(icoCandidate))
                {
                    Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(Path.GetFullPath(icoCandidate)));
                }
            }
            catch
            {
            }
        }

        private void OnWindowSourceInitialized(object sender, EventArgs e)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    int dark = 1;
                    NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
                    int round = NativeMethods.DWMWCP_ROUND;
                    NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
                }
            }
            catch
            {
            }
        }

        private void InitTrayIcon()
        {
            if (_trayManager != null) return;
            _trayManager = new TrayIconManager(
                RestoreFromTray,
                () =>
                {
                    RestoreFromTray();
                    var unused = RunFullScanWorkflowAsync(_settings.QueryLiveMicrosoftCatalog);
                },
                () =>
                {
                    ToggleGameBoostAction();
                },
                () =>
                {
                    string msg = HardwareFixService.ExecuteSafeRepairAction("fix_audio", _scanSummary.Devices, _storeReport);
                    SetStatus(msg, 100);
                },
                LaunchOrOpenWebPortal,
                ExitApplicationCleanly);
        }

        private void RestoreFromTray()
        {
            Dispatcher.Invoke(() =>
            {
                Show();
                if (WindowState == WindowState.Minimized)
                {
                    WindowState = WindowState.Normal;
                }
                Activate();
            });
        }

        private void ExitApplicationCleanly()
        {
            _allowRealExit = true;
            if (_webServer != null)
            {
                _webServer.Dispose();
                _webServer = null;
            }
            if (_trayManager != null)
            {
                _trayManager.Dispose();
                _trayManager = null;
            }
            Application.Current.Shutdown();
        }

        private void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_allowRealExit)
            {
                // Clean exit when user clicks window X unless tray-Resident is explicitly desired; dispose tray cleanly so zero zombie processes linger
                if (_webServer != null)
                {
                    _webServer.Dispose();
                    _webServer = null;
                }
                if (_trayManager != null)
                {
                    _trayManager.Dispose();
                    _trayManager = null;
                }
            }
        }

        private void BuildLayout()
        {
            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(236) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Left Navigation Rail
            var railBorder = new Border
            {
                Background = ThemeTokens.BgRail,
                BorderBrush = ThemeTokens.BorderSubtle,
                BorderThickness = new Thickness(0, 0, 1, 0),
                Padding = new Thickness(14, 18, 14, 16)
            };

            var railDock = new DockPanel { LastChildFill = true };

            // Brand Header
            var brandStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 20) };
            brandStack.Children.Add(new Border
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(9),
                Background = ThemeTokens.EmeraldStrong,
                BorderBrush = ThemeTokens.Emerald,
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 10, 0),
                Child = new TextBlock
                {
                    Text = "D+",
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.ExtraBold,
                    FontSize = 15,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
            var titleCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titleCol.Children.Add(new TextBlock
            {
                Text = "Driver+",
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                Foreground = ThemeTokens.TextPrimary
            });
            titleCol.Children.Add(new TextBlock
            {
                Text = "WHQL Clean-Room Edition",
                FontSize = 11,
                Foreground = ThemeTokens.TextMuted
            });
            brandStack.Children.Add(titleCol);
            DockPanel.SetDock(brandStack, Dock.Top);
            railDock.Children.Add(brandStack);

            // Privilege & Safety Footer in Rail
            var footerCard = ThemeTokens.CreateCard(new Thickness(11, 10, 11, 10));
            var footerStack = new StackPanel();
            bool isAdmin = IsRunningAsAdministrator();
            footerStack.Children.Add(new TextBlock
            {
                Text = isAdmin ? "● Administrator Active" : "● Standard User Mode",
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = isAdmin ? ThemeTokens.EmeraldText : ThemeTokens.Sky
            });
            footerStack.Children.Add(new TextBlock
            {
                Text = isAdmin
                    ? "Kernel DriverStore staging & System Restore enabled."
                    : "Zero-UAC scan mode. Elevate only when installing INF packages.",
                FontSize = 11,
                Foreground = ThemeTokens.TextMuted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, isAdmin ? 0 : 8)
            });
            if (!isAdmin)
            {
                Button btnElevate = ThemeTokens.CreateButton("Relaunch as Admin", false);
                btnElevate.Click += (s, e) => TryRelaunchElevated();
                footerStack.Children.Add(btnElevate);
            }
            footerCard.Child = footerStack;
            DockPanel.SetDock(footerCard, Dock.Bottom);
            railDock.Children.Add(footerCard);

            _navStack = new StackPanel();
            RebuildNavButtons();
            railDock.Children.Add(_navStack);
            railBorder.Child = railDock;
            Grid.SetColumn(railBorder, 0);
            root.Children.Add(railBorder);

            // Right Main Content Area
            var rightGrid = new Grid();
            rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rightGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rightGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Top Action Bar
            var topBar = new Border
            {
                Background = ThemeTokens.BgSurface,
                BorderBrush = ThemeTokens.BorderSubtle,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(22, 12, 22, 12)
            };
            var topDock = new DockPanel();

            var rightBtns = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(rightBtns, Dock.Right);

            Button btnWeb = ThemeTokens.CreateButton("Live Web Console", false);
            btnWeb.Margin = new Thickness(0, 0, 10, 0);
            btnWeb.Click += (s, e) => LaunchOrOpenWebPortal();
            rightBtns.Children.Add(btnWeb);

            _btnScanNow = ThemeTokens.CreateButton("Scan Hardware Now", true);
            _btnScanNow.Click += async (s, e) => await RunFullScanWorkflowAsync(_liveCatalogCheck != null && _liveCatalogCheck.IsChecked == true);
            rightBtns.Children.Add(_btnScanNow);
            topDock.Children.Add(rightBtns);

            var topTitleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            topTitleStack.Children.Add(new TextBlock
            {
                Text = "Official Microsoft Update Catalog + Native 64-bit SetupAPI Engine",
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeTokens.TextPrimary
            });
            topTitleStack.Children.Add(new TextBlock
            {
                Text = "Zero proprietary cloud telemetry • Strict HTTPS *.windowsupdate.com + WinVerifyTrust Authenticode gate",
                FontSize = 11.5,
                Foreground = ThemeTokens.TextMuted
            });
            topDock.Children.Add(topTitleStack);
            topBar.Child = topDock;
            Grid.SetRow(topBar, 0);
            rightGrid.Children.Add(topBar);

            // Workspace Host
            _workspaceHost = new Grid { Margin = new Thickness(22, 18, 22, 14) };
            BuildAllWorkspaces();
            Grid.SetRow(_workspaceHost, 1);
            rightGrid.Children.Add(_workspaceHost);

            // Bottom Status & Smooth Progress Bar
            var statusBorder = new Border
            {
                Background = ThemeTokens.BgSurface,
                BorderBrush = ThemeTokens.BorderSubtle,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(22, 9, 22, 9)
            };
            var statusGrid = new Grid();
            statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });

            _statusText = new TextBlock
            {
                Text = "Initializing 64-bit SetupAPI hardware inventory...",
                FontSize = 12,
                Foreground = ThemeTokens.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_statusText, 0);
            statusGrid.Children.Add(_statusText);

            _progressBar = new ProgressBar
            {
                Height = 6,
                Minimum = 0,
                Maximum = 100,
                Value = 8,
                Background = ThemeTokens.BgElevated,
                Foreground = ThemeTokens.Emerald,
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_progressBar, 1);
            statusGrid.Children.Add(_progressBar);
            statusBorder.Child = statusGrid;
            Grid.SetRow(statusBorder, 2);
            rightGrid.Children.Add(statusBorder);

            Grid.SetColumn(rightGrid, 1);
            root.Children.Add(rightGrid);
            Content = root;
        }

        private void RebuildNavButtons()
        {
            _navStack.Children.Clear();
            var items = new[]
            {
                new KeyValuePair<string, string>("drivers", "⚡|Driver Scan & Updates"),
                new KeyValuePair<string, string>("runtimes", "🎮|Game Runtimes (14)"),
                new KeyValuePair<string, string>("rescue", "🛡|Rescue & Offline Center"),
                new KeyValuePair<string, string>("toolbox", "🔧|Fix Toolbox & HVCI"),
                new KeyValuePair<string, string>("boost", "🚀|Game Boost"),
                new KeyValuePair<string, string>("sysinfo", "💻|System Info & Export"),
                new KeyValuePair<string, string>("settings", "⚙|Settings & History")
            };

            foreach (var item in items)
            {
                string key = item.Key;
                string[] parts = item.Value.Split('|');
                Button btn = ThemeTokens.CreateNavButton(parts[0], parts[1], _activeTab == key);
                btn.Click += (s, e) => SwitchTab(key);
                _navStack.Children.Add(btn);
            }
        }

        private void SwitchTab(string tabKey)
        {
            _activeTab = tabKey;
            RebuildNavButtons();
            foreach (UIElement child in _workspaceHost.Children)
            {
                FrameworkElement fe = child as FrameworkElement;
                if (fe != null)
                {
                    fe.Visibility = string.Equals(Convert.ToString(fe.Tag), tabKey, StringComparison.Ordinal)
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }
            }

            if (tabKey == "rescue") RefreshBackupsView();
            if (tabKey == "settings") RefreshHistoryView();
        }

        private void BuildAllWorkspaces()
        {
            _workspaceHost.Children.Clear();
            _workspaceHost.Children.Add(BuildDriversWorkspace());
            _workspaceHost.Children.Add(BuildRuntimesWorkspace());
            _workspaceHost.Children.Add(BuildRescueWorkspace());
            _workspaceHost.Children.Add(BuildToolboxWorkspace());
            _workspaceHost.Children.Add(BuildBoostWorkspace());
            _workspaceHost.Children.Add(BuildSysInfoWorkspace());
            _workspaceHost.Children.Add(BuildSettingsWorkspace());
            SwitchTab("drivers");
        }

        private UIElement BuildDriversWorkspace()
        {
            var grid = new Grid { Tag = "drivers" };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            // Row 0: 5 KPI Metric Cards
            var kpiGrid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 5, Margin = new Thickness(0, 0, 0, 14) };
            kpiGrid.Children.Add(CreateKpiCard("Present Devices", out _kpiPresentVal, ThemeTokens.Sky));
            kpiGrid.Children.Add(CreateKpiCard("WHQL Updates", out _kpiUpdatesVal, ThemeTokens.Amber));
            kpiGrid.Children.Add(CreateKpiCard("Up to Date", out _kpiHealthyVal, ThemeTokens.EmeraldText));
            kpiGrid.Children.Add(CreateKpiCard("Faults / HVCI", out _kpiFaultsVal, ThemeTokens.Rose));
            kpiGrid.Children.Add(CreateKpiCard("Scan Latency", out _kpiElapsedVal, ThemeTokens.TextPrimary));
            Grid.SetRow(kpiGrid, 0);
            grid.Children.Add(kpiGrid);

            // Row 1: Search + Filter Pills + Update All
            var filterDock = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };

            var rightTools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(rightTools, Dock.Right);

            _liveCatalogCheck = new CheckBox
            {
                Content = "Live Microsoft Catalog",
                IsChecked = _settings.QueryLiveMicrosoftCatalog,
                Foreground = ThemeTokens.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 14, 0)
            };
            _liveCatalogCheck.Checked += (s, e) => { _settings.QueryLiveMicrosoftCatalog = true; SettingsService.Save(_settings); };
            _liveCatalogCheck.Unchecked += (s, e) => { _settings.QueryLiveMicrosoftCatalog = false; SettingsService.Save(_settings); };
            rightTools.Children.Add(_liveCatalogCheck);

            _btnUpdateAll = ThemeTokens.CreateButton("Update All WHQL", true);
            _btnUpdateAll.Click += async (s, e) => await InstallAllAvailableUpdatesAsync();
            rightTools.Children.Add(_btnUpdateAll);
            filterDock.Children.Add(rightTools);

            var leftTools = new StackPanel { Orientation = Orientation.Horizontal };
            _searchBox = ThemeTokens.CreateStyledTextBox("Filter by Device Name, Class, or PCI\\VEN_xxxx&DEV_yyyy...");
            _searchBox.Width = 280;
            _searchBox.Margin = new Thickness(0, 0, 10, 0);
            _searchBox.TextChanged += (s, e) => RefreshDeviceListFilter();
            leftTools.Children.Add(_searchBox);

            AddModeFilterButton(leftTools, "All Present", "all");
            AddModeFilterButton(leftTools, "Updates", "updates");
            AddModeFilterButton(leftTools, "Faults", "faults");
            AddModeFilterButton(leftTools, "Unplugged Ghosts", "ghosts");
            filterDock.Children.Add(leftTools);

            Grid.SetRow(filterDock, 1);
            grid.Children.Add(filterDock);

            // Row 2: Split Virtualized Device List + Side-by-Side Diff Inspector
            var split = new Grid();
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(375) });

            var listCard = ThemeTokens.CreateCard(new Thickness(4));
            _deviceListBox = new ListBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = ThemeTokens.TextPrimary,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(_deviceListBox, ScrollBarVisibility.Disabled);
            VirtualizingPanel.SetIsVirtualizing(_deviceListBox, true);
            VirtualizingPanel.SetVirtualizationMode(_deviceListBox, VirtualizationMode.Recycling);
            ScrollViewer.SetCanContentScroll(_deviceListBox, true);
            _deviceListBox.ItemTemplate = BuildVirtualizedDeviceTemplate();
            _deviceListBox.SelectionChanged += (s, e) => RenderSelectedDeviceInspector(_deviceListBox.SelectedItem as HardwareDevice);
            listCard.Child = _deviceListBox;
            Grid.SetColumn(listCard, 0);
            split.Children.Add(listCard);

            var inspectorCard = ThemeTokens.CreateCard(new Thickness(16));
            var inspectorScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _inspectorPanel = new StackPanel();
            inspectorScroll.Content = _inspectorPanel;
            inspectorCard.Child = inspectorScroll;
            Grid.SetColumn(inspectorCard, 2);
            split.Children.Add(inspectorCard);

            RenderSelectedDeviceInspector(null);

            Grid.SetRow(split, 2);
            grid.Children.Add(split);
            return grid;
        }

        private DataTemplate BuildVirtualizedDeviceTemplate()
        {
            var template = new DataTemplate(typeof(HardwareDevice));

            var outer = new FrameworkElementFactory(typeof(Border));
            outer.SetValue(Border.PaddingProperty, new Thickness(12, 9, 12, 9));
            outer.SetValue(Border.BorderBrushProperty, ThemeTokens.BorderSubtle);
            outer.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 1));

            var rowGrid = new FrameworkElementFactory(typeof(Grid));
            var col0 = new FrameworkElementFactory(typeof(ColumnDefinition));
            col0.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
            var col1 = new FrameworkElementFactory(typeof(ColumnDefinition));
            col1.SetValue(ColumnDefinition.WidthProperty, new GridLength(128));
            var col2 = new FrameworkElementFactory(typeof(ColumnDefinition));
            col2.SetValue(ColumnDefinition.WidthProperty, new GridLength(132));
            rowGrid.AppendChild(col0);
            rowGrid.AppendChild(col1);
            rowGrid.AppendChild(col2);

            var nameStack = new FrameworkElementFactory(typeof(StackPanel));
            nameStack.SetValue(Grid.ColumnProperty, 0);

            var titleTb = new FrameworkElementFactory(typeof(TextBlock));
            titleTb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("DeviceName"));
            titleTb.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            titleTb.SetValue(TextBlock.FontSizeProperty, 13.0);
            titleTb.SetValue(TextBlock.ForegroundProperty, ThemeTokens.TextPrimary);
            titleTb.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            nameStack.AppendChild(titleTb);

            var subTb = new FrameworkElementFactory(typeof(TextBlock));
            subTb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("PrimaryHardwareId"));
            subTb.SetValue(TextBlock.FontFamilyProperty, ThemeTokens.MonoFont);
            subTb.SetValue(TextBlock.FontSizeProperty, 11.0);
            subTb.SetValue(TextBlock.ForegroundProperty, ThemeTokens.TextMuted);
            subTb.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            nameStack.AppendChild(subTb);
            rowGrid.AppendChild(nameStack);

            var verStack = new FrameworkElementFactory(typeof(StackPanel));
            verStack.SetValue(Grid.ColumnProperty, 1);
            verStack.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            var verTb = new FrameworkElementFactory(typeof(TextBlock));
            verTb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("DriverVersion"));
            verTb.SetValue(TextBlock.FontFamilyProperty, ThemeTokens.MonoFont);
            verTb.SetValue(TextBlock.FontSizeProperty, 12.0);
            verTb.SetValue(TextBlock.ForegroundProperty, ThemeTokens.TextSecondary);
            verStack.AppendChild(verTb);

            var clsTb = new FrameworkElementFactory(typeof(TextBlock));
            clsTb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("DeviceClass"));
            clsTb.SetValue(TextBlock.FontSizeProperty, 11.0);
            clsTb.SetValue(TextBlock.ForegroundProperty, ThemeTokens.TextMuted);
            clsTb.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            verStack.AppendChild(clsTb);
            rowGrid.AppendChild(verStack);

            var badgeTb = new FrameworkElementFactory(typeof(TextBlock));
            badgeTb.SetValue(Grid.ColumnProperty, 2);
            badgeTb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("HealthBadge"));
            badgeTb.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            badgeTb.SetValue(TextBlock.FontSizeProperty, 11.5);
            badgeTb.SetValue(TextBlock.ForegroundProperty, ThemeTokens.EmeraldText);
            badgeTb.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Right);
            badgeTb.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            rowGrid.AppendChild(badgeTb);

            outer.AppendChild(rowGrid);
            template.VisualTree = outer;
            return template;
        }

        private void AddModeFilterButton(StackPanel host, string label, string mode)
        {
            Button btn = ThemeTokens.CreateButton(label, false);
            btn.Margin = new Thickness(0, 0, 6, 0);
            btn.Click += (s, e) =>
            {
                _deviceModeFilter = mode;
                RefreshDeviceListFilter();
            };
            host.Children.Add(btn);
        }

        private Border CreateKpiCard(string label, out TextBlock valueBlock, SolidColorBrush accent)
        {
            Border card = ThemeTokens.CreateCard(new Thickness(14, 11, 14, 11));
            card.Margin = new Thickness(0, 0, 10, 0);
            var st = new StackPanel();
            st.Children.Add(new TextBlock
            {
                Text = label.ToUpperInvariant(),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeTokens.TextMuted
            });
            valueBlock = new TextBlock
            {
                Text = "--",
                FontSize = 23,
                FontWeight = FontWeights.Bold,
                Foreground = accent,
                Margin = new Thickness(0, 3, 0, 0)
            };
            st.Children.Add(valueBlock);
            card.Child = st;
            return card;
        }

        private void RenderSelectedDeviceInspector(HardwareDevice dev)
        {
            _inspectorPanel.Children.Clear();
            if (dev == null)
            {
                _inspectorPanel.Children.Add(new TextBlock
                {
                    Text = "Hardware & SUBSYS Diff Inspector",
                    FontSize = 15,
                    FontWeight = FontWeights.Bold,
                    Foreground = ThemeTokens.TextPrimary
                });
                _inspectorPanel.Children.Add(new TextBlock
                {
                    Text = "Select any PnP hardware device on the left to inspect its exact 4-part OEM SUBSYS Hardware ID, installed INF/Signer, CM_PROB status code, and official Microsoft Update WHQL candidate.",
                    FontSize = 12.5,
                    Foreground = ThemeTokens.TextMuted,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0)
                });
                return;
            }

            _inspectorPanel.Children.Add(new TextBlock
            {
                Text = dev.DeviceName,
                FontSize = 15.5,
                FontWeight = FontWeights.Bold,
                Foreground = ThemeTokens.TextPrimary,
                TextWrapping = TextWrapping.Wrap
            });
            _inspectorPanel.Children.Add(new TextBlock
            {
                Text = dev.DeviceClass + " • " + (string.IsNullOrEmpty(dev.VendorSupportName) ? dev.Manufacturer : dev.VendorSupportName),
                FontSize = 12,
                Foreground = ThemeTokens.Sky,
                Margin = new Thickness(0, 3, 0, 12)
            });

            AddInspectorKv("Status", dev.HealthBadge + (dev.ProblemCode != 0 ? " — " + dev.ProblemSummary : ""));
            AddInspectorKv("Primary HWID", dev.PrimaryHardwareId);
            if (!string.IsNullOrEmpty(dev.SubsystemOemName))
            {
                AddInspectorKv("Subsystem OEM", dev.SubsystemOemName + " (4-part SUBSYS preserved)");
            }
            AddInspectorKv("Current Driver", (string.IsNullOrEmpty(dev.DriverVersion) ? "Inbox / None" : dev.DriverVersion) +
                (string.IsNullOrEmpty(dev.DriverDate) ? "" : " (" + dev.DriverDate + ")"));
            AddInspectorKv("Published INF", string.IsNullOrEmpty(dev.InfName) ? "Windows Inbox" : dev.InfName);
            AddInspectorKv("Digital Signer", string.IsNullOrEmpty(dev.DigitalSigner) ? "Microsoft Windows" : dev.DigitalSigner);

            if (dev.RecommendedUpdate != null)
            {
                DriverCandidate c = dev.RecommendedUpdate;
                var diffBox = new Border
                {
                    Background = ThemeTokens.EmeraldDim,
                    BorderBrush = ThemeTokens.BorderAccent,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 10, 12, 10),
                    Margin = new Thickness(0, 12, 0, 12)
                };
                var diffStack = new StackPanel();
                diffStack.Children.Add(new TextBlock
                {
                    Text = "RECOMMENDED OFFICIAL WHQL UPDATE",
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = ThemeTokens.EmeraldText
                });
                diffStack.Children.Add(new TextBlock
                {
                    Text = string.Format("{0}  →  {1}", string.IsNullOrEmpty(dev.DriverVersion) ? "None" : dev.DriverVersion, c.TargetVersion),
                    FontFamily = ThemeTokens.MonoFont,
                    FontSize = 13.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = ThemeTokens.TextPrimary,
                    Margin = new Thickness(0, 4, 0, 3)
                });
                diffStack.Children.Add(new TextBlock
                {
                    Text = string.Format("{0}\nDate: {1} • Size: {2}\nSource: {3}",
                        c.Title,
                        string.IsNullOrEmpty(c.TargetDate) ? "Verified" : c.TargetDate,
                        string.IsNullOrEmpty(c.SizeText) ? "CAB" : c.SizeText,
                        c.Source),
                    FontSize = 11.5,
                    Foreground = ThemeTokens.TextSecondary,
                    TextWrapping = TextWrapping.Wrap
                });
                diffBox.Child = diffStack;
                _inspectorPanel.Children.Add(diffBox);

                Button btnInstall = ThemeTokens.CreateButton("Install Official WHQL CAB", true);
                btnInstall.Margin = new Thickness(0, 0, 0, 8);
                btnInstall.Click += async (s, e) => await InstallSingleDeviceUpdateAsync(dev);
                _inspectorPanel.Children.Add(btnInstall);
            }

            if (!string.IsNullOrEmpty(dev.ProblemRemediation) && dev.ProblemCode != 0)
            {
                AddInspectorKv("Remediation", dev.ProblemRemediation);
            }

            // Secondary actions
            Button btnBackup = ThemeTokens.CreateButton("Backup Current Driver (.zip)", false);
            btnBackup.Margin = new Thickness(0, 6, 0, 8);
            btnBackup.Click += async (s, e) => await BackupSingleDeviceToZipAsync(dev);
            _inspectorPanel.Children.Add(btnBackup);

            Button btnIgnore = ThemeTokens.CreateButton(dev.IsIgnored ? "Unignore Device" : "Ignore Updates for Device", false);
            btnIgnore.Margin = new Thickness(0, 0, 0, 8);
            btnIgnore.Click += (s, e) => ToggleIgnoreDevice(dev);
            _inspectorPanel.Children.Add(btnIgnore);

            Uri safeVendorUri;
            if (!string.IsNullOrEmpty(dev.VendorSupportUrl) &&
                Uri.TryCreate(dev.VendorSupportUrl, UriKind.Absolute, out safeVendorUri) &&
                string.Equals(safeVendorUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                string safeTargetUrl = safeVendorUri.AbsoluteUri;
                Button btnVendor = ThemeTokens.CreateButton("Open Official Vendor Support Portal", false);
                btnVendor.Click += (s, e) =>
                {
                    try { Process.Start(new ProcessStartInfo { FileName = safeTargetUrl, UseShellExecute = true }); } catch { }
                };
                _inspectorPanel.Children.Add(btnVendor);
            }
        }

        private void AddInspectorKv(string key, string val)
        {
            var st = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            st.Children.Add(new TextBlock
            {
                Text = key.ToUpperInvariant(),
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeTokens.TextMuted
            });
            st.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(val) ? "—" : val,
                FontSize = 12,
                FontFamily = key.IndexOf("HWID", StringComparison.Ordinal) >= 0 ? ThemeTokens.MonoFont : ThemeTokens.UiFont,
                Foreground = ThemeTokens.TextSecondary,
                TextWrapping = TextWrapping.Wrap
            });
            _inspectorPanel.Children.Add(st);
        }

        private UIElement BuildRuntimesWorkspace()
        {
            var scroll = new ScrollViewer { Tag = "runtimes", VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var stack = new StackPanel();

            _crashDoctorBanner = ThemeTokens.CreateCard(new Thickness(16, 12, 16, 12));
            _crashDoctorBanner.Margin = new Thickness(0, 0, 0, 14);
            stack.Children.Add(_crashDoctorBanner);

            _runtimesListHost = new StackPanel();
            stack.Children.Add(_runtimesListHost);
            scroll.Content = stack;
            return scroll;
        }

        private UIElement BuildRescueWorkspace()
        {
            var grid = new Grid { Tag = "rescue" };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var actionCard = ThemeTokens.CreateCard(new Thickness(16));
            actionCard.Margin = new Thickness(0, 0, 0, 14);
            var actionWrap = new WrapPanel();

            Button btnRp = ThemeTokens.CreateButton("Create System Restore Point", true);
            btnRp.Margin = new Thickness(0, 0, 10, 8);
            btnRp.Click += async (s, e) =>
            {
                btnRp.IsEnabled = false;
                SetStatus("Creating Windows System Restore checkpoint on background worker...", 45);
                string msg = await Task.Run(() =>
                {
                    string m;
                    RescueBackupService.CreateSystemRestorePoint("Driver+ Manual Checkpoint", out m);
                    return m;
                });
                SetStatus(msg, 100);
                btnRp.IsEnabled = true;
            };
            actionWrap.Children.Add(btnRp);

            Button btnExportOffline = ThemeTokens.CreateButton("Export Offline Hardware Profile (.json)", false);
            btnExportOffline.Margin = new Thickness(0, 0, 10, 8);
            btnExportOffline.Click += (s, e) => ExportOfflineProfileDialog();
            actionWrap.Children.Add(btnExportOffline);

            Button btnImportOffline = ThemeTokens.CreateButton("Import & Audit Offline Profile (.json)", false);
            btnImportOffline.Margin = new Thickness(0, 0, 10, 8);
            btnImportOffline.Click += async (s, e) => await ImportAndAuditOfflineProfileDialogAsync();
            actionWrap.Children.Add(btnImportOffline);

            Button btnRestoreZip = ThemeTokens.CreateButton("Restore Selected Backup ZIP", false);
            btnRestoreZip.Margin = new Thickness(0, 0, 10, 8);
            btnRestoreZip.Click += async (s, e) => await RestoreSelectedBackupActionAsync();
            actionWrap.Children.Add(btnRestoreZip);

            Button btnOpenDir = ThemeTokens.CreateButton("Open Backups Folder", false);
            btnOpenDir.Margin = new Thickness(0, 0, 0, 8);
            btnOpenDir.Click += (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo { FileName = RescueBackupService.GetDefaultBackupDirectory(), UseShellExecute = true }); } catch { }
            };
            actionWrap.Children.Add(btnOpenDir);

            actionCard.Child = actionWrap;
            Grid.SetRow(actionCard, 0);
            grid.Children.Add(actionCard);

            var listCard = ThemeTokens.CreateCard(new Thickness(8));
            _backupsListBox = new ListBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = ThemeTokens.TextPrimary
            };
            listCard.Child = _backupsListBox;
            Grid.SetRow(listCard, 1);
            grid.Children.Add(listCard);
            return grid;
        }

        private UIElement BuildToolboxWorkspace()
        {
            var scroll = new ScrollViewer { Tag = "toolbox", VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _toolboxWrapHost = new WrapPanel();
            scroll.Content = _toolboxWrapHost;
            return scroll;
        }

        private UIElement BuildBoostWorkspace()
        {
            var stack = new StackPanel { Tag = "boost", MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
            var card = ThemeTokens.CreateCard(new Thickness(24));
            var inner = new StackPanel();

            _boostStatusHeadline = new TextBlock
            {
                Text = "Game Boost & Power Profile Controller",
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = ThemeTokens.TextPrimary
            };
            inner.Children.Add(_boostStatusHeadline);

            _boostDetailText = new TextBlock
            {
                Text = "Inspecting active PowrProf scheme and Direct3D fullscreen notification state...",
                FontSize = 13,
                Foreground = ThemeTokens.TextSecondary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 16)
            };
            inner.Children.Add(_boostDetailText);

            _boostStopServicesCheck = new CheckBox
            {
                Content = "Also quiesce safe optional non-essential background services (WSearch, Spooler, MapsBroker) with crash-safe snapshot",
                IsChecked = false,
                Foreground = ThemeTokens.TextSecondary,
                Margin = new Thickness(0, 0, 0, 18)
            };
            inner.Children.Add(_boostStopServicesCheck);

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal };
            Button btnActivate = ThemeTokens.CreateButton("Activate High-Performance Game Boost", true);
            btnActivate.Margin = new Thickness(0, 0, 12, 0);
            btnActivate.Click += async (s, e) =>
            {
                bool stopSvcs = _boostStopServicesCheck.IsChecked == true;
                btnActivate.IsEnabled = false;
                string msg = await Task.Run(() => GameBoostService.ActivateGameBoost(stopSvcs));
                RefreshBoostCard();
                SetStatus(msg, 100);
                btnActivate.IsEnabled = true;
            };
            btnRow.Children.Add(btnActivate);

            Button btnRestore = ThemeTokens.CreateButton("Restore Normal Mode", false);
            btnRestore.Click += async (s, e) =>
            {
                btnRestore.IsEnabled = false;
                string msg = await Task.Run(() => GameBoostService.RestoreNormalMode());
                RefreshBoostCard();
                SetStatus(msg, 100);
                btnRestore.IsEnabled = true;
            };
            btnRow.Children.Add(btnRestore);

            inner.Children.Add(btnRow);
            card.Child = inner;
            stack.Children.Add(card);
            return stack;
        }

        private UIElement BuildSysInfoWorkspace()
        {
            var scroll = new ScrollViewer { Tag = "sysinfo", VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            _sysInfoHost = new StackPanel();
            scroll.Content = _sysInfoHost;
            return scroll;
        }

        private UIElement BuildSettingsWorkspace()
        {
            var grid = new Grid { Tag = "settings" };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(420) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var leftCard = ThemeTokens.CreateCard(new Thickness(18));
            var st = new StackPanel();
            st.Children.Add(new TextBlock
            {
                Text = "Safety & Verification Policy",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 14)
            });

            st.Children.Add(CreateSettingToggle("Strict WHQL-Signed Drivers Only (Recommended)", _settings.WhqlOnly, v => _settings.WhqlOnly = v));
            st.Children.Add(CreateSettingToggle("Create Windows Restore Point Before Driver Install", _settings.CreateRestorePointBeforeInstall, v => _settings.CreateRestorePointBeforeInstall = v));
            st.Children.Add(CreateSettingToggle("Auto-Backup OEM DriverStore Package to .zip", _settings.AutoBackupDriverZip, v => _settings.AutoBackupDriverZip = v));
            st.Children.Add(CreateSettingToggle("Query Live Microsoft Update Catalog Over HTTPS", _settings.QueryLiveMicrosoftCatalog, v => _settings.QueryLiveMicrosoftCatalog = v));
            st.Children.Add(CreateSettingToggle("Silent Focus Gaming Guard (Suppress Fullscreen Toasts)", _settings.SilentFocusGamingGuard, v => _settings.SilentFocusGamingGuard = v));

            Button btnResetIgnored = ThemeTokens.CreateButton("Clear Ignored Devices List", false);
            btnResetIgnored.Margin = new Thickness(0, 16, 0, 0);
            btnResetIgnored.Click += (s, e) =>
            {
                _settings.IgnoredInstanceIds.Clear();
                SettingsService.Save(_settings);
                SetStatus("Cleared ignored devices list.", 100);
            };
            st.Children.Add(btnResetIgnored);
            leftCard.Child = st;
            Grid.SetColumn(leftCard, 0);
            grid.Children.Add(leftCard);

            var rightCard = ThemeTokens.CreateCard(new Thickness(18));
            var histDock = new DockPanel();
            var histTitle = new TextBlock
            {
                Text = "Audit & Update History",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 12)
            };
            DockPanel.SetDock(histTitle, Dock.Top);
            histDock.Children.Add(histTitle);

            _historyListBox = new ListBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = ThemeTokens.TextSecondary,
                FontFamily = ThemeTokens.MonoFont,
                FontSize = 12
            };
            histDock.Children.Add(_historyListBox);
            rightCard.Child = histDock;
            Grid.SetColumn(rightCard, 2);
            grid.Children.Add(rightCard);
            return grid;
        }

        private CheckBox CreateSettingToggle(string text, bool initial, Action<bool> setter)
        {
            var cb = new CheckBox
            {
                Content = text,
                IsChecked = initial,
                Foreground = ThemeTokens.TextSecondary,
                Margin = new Thickness(0, 0, 0, 12)
            };
            cb.Checked += (s, e) => { setter(true); SettingsService.Save(_settings); };
            cb.Unchecked += (s, e) => { setter(false); SettingsService.Save(_settings); };
            return cb;
        }

        private async Task RunFullScanWorkflowAsync(bool queryLiveCatalog)
        {
            if (_isScanning) return;
            _isScanning = true;
            _btnScanNow.IsEnabled = false;

            try
            {
                SetStatus("Enumerating 64-bit SetupAPI / CfgMgr32 hardware & unplugged devnodes...", 12);

                var ignoredSet = new HashSet<string>(_settings.IgnoredInstanceIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                bool whqlOnly = _settings.WhqlOnly;

                await Task.Run(async () =>
                {
                    if (_systemProfile == null)
                    {
                        _systemProfile = SystemInfoService.CollectSystemProfile();
                    }

                    List<HardwareDevice> rawDevices = PnpDeviceScanner.ScanDevices(true);
                    _storeReport = DriverStoreInspector.ScanDriverStoreAndHvci();

                    _scanSummary = await WindowsUpdateCatalogClient.EnrichWithOfficialUpdatesAsync(
                        rawDevices,
                        _storeReport,
                        queryLiveCatalog,
                        whqlOnly,
                        ignoredSet,
                        (msg, pct) => Dispatcher.BeginInvoke(new Action(() => SetStatus(msg, pct)))).ConfigureAwait(false);

                    _runtimes = GameRuntimeInspector.InspectAllRuntimes();
                    _crashEvents = GameRuntimeInspector.ScanRecentCrashDllEvents(80);
                    _diagnostics = HardwareFixService.RunNonDestructiveDiagnostics(_scanSummary.Devices, _storeReport);
                });

                if (_webServer != null)
                {
                    _webServer.SeedSnapshot(_systemProfile, _storeReport, _scanSummary, _runtimes, _diagnostics);
                }

                UpdateAllViewsAfterScan();
                SetStatus(string.Format(
                    "Scan complete in {0} ms — {1} present devices, {2} official WHQL update(s), {3} unplugged ghost node(s).",
                    _scanSummary.ElapsedMs,
                    _scanSummary.TotalPresentDevices,
                    _scanSummary.OutdatedCount,
                    _scanSummary.GhostDeviceCount), 100);
            }
            catch (Exception ex)
            {
                SetStatus("Scan encountered an error: " + ex.Message, 100);
            }
            finally
            {
                _isScanning = false;
                _btnScanNow.IsEnabled = true;
            }
        }

        private void UpdateAllViewsAfterScan()
        {
            _kpiPresentVal.Text = _scanSummary.TotalPresentDevices.ToString(CultureInfo.InvariantCulture);
            _kpiUpdatesVal.Text = _scanSummary.OutdatedCount.ToString(CultureInfo.InvariantCulture);
            _kpiHealthyVal.Text = _scanSummary.UpToDateCount.ToString(CultureInfo.InvariantCulture);
            _kpiFaultsVal.Text = string.Format("{0} / {1}", _scanSummary.FaultyDeviceCount, _scanSummary.HvciBlockerCount);
            _kpiElapsedVal.Text = _scanSummary.ElapsedMs + " ms";

            RefreshDeviceListFilter();
            RenderRuntimesView();
            RenderToolboxView();
            RefreshBoostCard();
            RenderSysInfoView();
        }

        private void RefreshDeviceListFilter()
        {
            if (_deviceListBox == null || _scanSummary == null) return;
            string q = (_searchBox != null ? _searchBox.Text : "").Trim();
            var filtered = new List<HardwareDevice>();

            foreach (HardwareDevice d in _scanSummary.Devices)
            {
                if (_deviceModeFilter == "all" && !d.IsPresent) continue;
                if (_deviceModeFilter == "updates" && !d.HasUpdate) continue;
                if (_deviceModeFilter == "faults" && (!d.IsPresent || d.ProblemCode == 0)) continue;
                if (_deviceModeFilter == "ghosts" && d.IsPresent) continue;

                if (!string.IsNullOrEmpty(q))
                {
                    bool match =
                        d.DeviceName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        d.DeviceClass.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        d.PrimaryHardwareId.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        d.HealthBadge.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!match) continue;
                }
                filtered.Add(d);
            }

            _deviceListBox.ItemsSource = filtered;
            if (filtered.Count > 0 && _deviceListBox.SelectedItem == null)
            {
                _deviceListBox.SelectedIndex = 0;
            }
        }

        private void RenderRuntimesView()
        {
            if (_crashEvents != null && _crashEvents.Count > 0)
            {
                CrashDllEvent ev = _crashEvents[0];
                _crashDoctorBanner.Child = new TextBlock
                {
                    Text = string.Format("Crash DLL Doctor: Recent application fault in '{0}' caused by '{1}' → Repair '{2}' below.",
                        ev.ApplicationName, ev.FaultingModule, ev.RecommendedRuntimeId),
                    Foreground = ThemeTokens.Amber,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                };
            }
            else
            {
                _crashDoctorBanner.Child = new TextBlock
                {
                    Text = "Crash DLL Doctor: 0 recent missing VC++ / DirectX / OpenAL / PhysX DLL application crashes found in Windows Event Log.",
                    Foreground = ThemeTokens.EmeraldText,
                    FontSize = 12.5
                };
            }

            _runtimesListHost.Children.Clear();
            foreach (RuntimeComponent rc in _runtimes)
            {
                RuntimeComponent captured = rc;
                Border card = ThemeTokens.CreateCard(new Thickness(14, 10, 14, 10));
                card.Margin = new Thickness(0, 0, 0, 8);
                var dock = new DockPanel();

                Button btnAct = ThemeTokens.CreateButton(rc.IsInstalled && !rc.NeedsUpdate ? "Verify / Repair" : "Install Official", !rc.IsInstalled || rc.NeedsUpdate);
                DockPanel.SetDock(btnAct, Dock.Right);
                btnAct.VerticalAlignment = VerticalAlignment.Center;
                btnAct.Click += async (s, e) =>
                {
                    btnAct.IsEnabled = false;
                    SetStatus("Running official Winget / vendor runtime installer for " + captured.Name + "...", 45);
                    string msg = await GameRuntimeInspector.InstallOrRepairRuntimeAsync(captured);
                    SetStatus(msg, 100);
                    btnAct.IsEnabled = true;
                };
                dock.Children.Add(btnAct);

                var st = new StackPanel();
                st.Children.Add(new TextBlock
                {
                    Text = rc.Name + "  [" + rc.Architecture + "]",
                    FontWeight = FontWeights.SemiBold,
                    FontSize = 13.5,
                    Foreground = ThemeTokens.TextPrimary
                });
                st.Children.Add(new TextBlock
                {
                    Text = string.Format("Status: {0}  •  Installed: {1}  •  Reference: {2}  •  Winget: {3}",
                        rc.IsInstalled ? (rc.NeedsUpdate ? "Update Available" : "Healthy") : "Not Installed",
                        string.IsNullOrEmpty(rc.InstalledVersion) ? "—" : rc.InstalledVersion,
                        rc.LatestVersion,
                        rc.WingetId),
                    FontFamily = ThemeTokens.MonoFont,
                    FontSize = 11.5,
                    Foreground = rc.IsInstalled && !rc.NeedsUpdate ? ThemeTokens.EmeraldText : ThemeTokens.Amber,
                    Margin = new Thickness(0, 3, 0, 0)
                });
                dock.Children.Add(st);
                card.Child = dock;
                _runtimesListHost.Children.Add(card);
            }
        }

        private void RenderToolboxView()
        {
            _toolboxWrapHost.Children.Clear();
            foreach (DiagnosticFinding f in _diagnostics)
            {
                DiagnosticFinding captured = f;
                Border card = ThemeTokens.CreateCard(new Thickness(18));
                card.Width = 450;
                card.Margin = new Thickness(0, 0, 14, 14);
                var st = new StackPanel();

                var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
                Border badge = ThemeTokens.CreateBadge(
                    f.StatusText,
                    f.IssueDetected ? ThemeTokens.AmberDim : ThemeTokens.EmeraldDim,
                    f.IssueDetected ? ThemeTokens.Amber : ThemeTokens.EmeraldText);
                DockPanel.SetDock(badge, Dock.Right);
                head.Children.Add(badge);
                head.Children.Add(new TextBlock
                {
                    Text = f.ToolName,
                    FontWeight = FontWeights.Bold,
                    FontSize = 14,
                    Foreground = ThemeTokens.TextPrimary,
                    TextWrapping = TextWrapping.Wrap
                });
                st.Children.Add(head);

                st.Children.Add(new TextBlock
                {
                    Text = f.Detail,
                    FontSize = 12.5,
                    Foreground = ThemeTokens.TextSecondary,
                    TextWrapping = TextWrapping.Wrap,
                    MinHeight = 42,
                    Margin = new Thickness(0, 0, 0, 12)
                });

                Button btnFix = ThemeTokens.CreateButton(f.ActionLabel, f.IssueDetected);
                btnFix.HorizontalAlignment = HorizontalAlignment.Left;
                btnFix.Click += async (s, e) =>
                {
                    btnFix.IsEnabled = false;
                    SetStatus("Running safe repair action (" + captured.ToolName + ")...", 45);
                    string res = await Task.Run(() => HardwareFixService.ExecuteSafeRepairAction(captured.ToolId, _scanSummary.Devices, _storeReport));
                    _diagnostics = await Task.Run(() => HardwareFixService.RunNonDestructiveDiagnostics(_scanSummary.Devices, _storeReport));
                    RenderToolboxView();
                    SetStatus(res, 100);
                };
                st.Children.Add(btnFix);
                card.Child = st;
                _toolboxWrapHost.Children.Add(card);
            }
        }

        private void RefreshBoostCard()
        {
            bool active = GameBoostService.IsBoostCurrentlyActive();
            string scheme = GameBoostService.DescribePowerScheme(GameBoostService.GetActivePowerSchemeGuidString());
            bool fullscreen = GameBoostService.IsFullscreenGameOrD3DActive();

            _boostStatusHeadline.Text = active
                ? "🚀 Game Boost ACTIVE — " + scheme
                : "Game Boost Standby — Current Plan: " + scheme;
            _boostStatusHeadline.Foreground = active ? ThemeTokens.EmeraldText : ThemeTokens.TextPrimary;
            _boostDetailText.Text = string.Format(
                "Active Windows Power Scheme: {0}\nDirect3D / Presentation Fullscreen Focus State: {1}\nRestore Snapshot File: {2}",
                scheme,
                fullscreen ? "Fullscreen App Active (Toasts Muted)" : "Desktop Mode",
                active ? "Persisted on disk (crash-safe rollback ready)" : "None (Normal Mode)");
        }

        private void RenderSysInfoView()
        {
            _sysInfoHost.Children.Clear();
            if (_systemProfile == null) return;

            var topActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            Button btnExportMd = ThemeTokens.CreateButton("Export Markdown Hardware Report (.md)", true);
            btnExportMd.Margin = new Thickness(0, 0, 10, 0);
            btnExportMd.Click += (s, e) =>
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    FileName = "DriverPlus-Report-" + _systemProfile.ComputerName + ".md",
                    Filter = "Markdown Report (*.md)|*.md|All Files (*.*)|*.*"
                };
                if (dlg.ShowDialog(this) == true)
                {
                    File.WriteAllText(dlg.FileName, SystemInfoService.FormatMarkdownReport(_systemProfile, _scanSummary));
                    SetStatus("Exported hardware & driver Markdown report to " + dlg.FileName, 100);
                }
            };
            topActions.Children.Add(btnExportMd);

            Button btnCopy = ThemeTokens.CreateButton("Copy Markdown to Clipboard", false);
            btnCopy.Click += (s, e) =>
            {
                Clipboard.SetText(SystemInfoService.FormatMarkdownReport(_systemProfile, _scanSummary));
                SetStatus("Copied full system & driver summary to clipboard.", 100);
            };
            topActions.Children.Add(btnCopy);
            _sysInfoHost.Children.Add(topActions);

            Border card = ThemeTokens.CreateCard(new Thickness(18));
            card.Child = new TextBlock
            {
                Text = SystemInfoService.FormatMarkdownReport(_systemProfile, _scanSummary),
                FontFamily = ThemeTokens.MonoFont,
                FontSize = 12.5,
                Foreground = ThemeTokens.TextSecondary,
                TextWrapping = TextWrapping.Wrap
            };
            _sysInfoHost.Children.Add(card);
        }

        private void RefreshBackupsView()
        {
            if (_backupsListBox == null) return;
            _backupsListBox.Items.Clear();
            List<BackupRecord> backups = RescueBackupService.ListBackups(null);
            foreach (BackupRecord b in backups)
            {
                _backupsListBox.Items.Add(new ListBoxItem
                {
                    Tag = b,
                    Content = string.Format("{0:yyyy-MM-dd HH:mm} UTC  |  {1} ({2} v{3})  |  SHA-256: {4}...",
                        b.CreatedAtUtc, b.DeviceName, b.InfName, b.DriverVersion,
                        string.IsNullOrEmpty(b.Sha256) ? "" : b.Sha256.Substring(0, Math.Min(12, b.Sha256.Length))),
                    Foreground = ThemeTokens.TextSecondary,
                    FontFamily = ThemeTokens.MonoFont,
                    Padding = new Thickness(8, 6, 8, 6)
                });
            }
        }

        private void RefreshHistoryView()
        {
            if (_historyListBox == null) return;
            _settings = SettingsService.Load();
            _historyListBox.ItemsSource = _settings.UpdateHistory;
        }

        private async Task InstallSingleDeviceUpdateAsync(HardwareDevice dev)
        {
            if (_isBusy || _isScanning || dev == null || dev.RecommendedUpdate == null) return;
            _isBusy = true;
            try
            {
                SetStatus("Installing official WHQL update for " + dev.DeviceName + "...", 30);
                DriverInstallResult res = await DriverInstallOrchestrator.DownloadBackupAndInstallAsync(
                    dev,
                    _storeReport,
                    _settings.CreateRestorePointBeforeInstall,
                    _settings.AutoBackupDriverZip,
                    msg => Dispatcher.BeginInvoke(new Action(() => SetStatus(msg, 65))));
                SetStatus(res.SummaryMessage, 100);
                if (res.Succeeded)
                {
                    await RunFullScanWorkflowAsync(false);
                }
            }
            finally
            {
                _isBusy = false;
            }
        }

        private async Task InstallAllAvailableUpdatesAsync()
        {
            if (_isBusy || _isScanning || _scanSummary == null) return;
            var targets = new List<HardwareDevice>();
            foreach (HardwareDevice d in _scanSummary.Devices)
            {
                if (d.HasUpdate) targets.Add(d);
            }
            if (targets.Count == 0)
            {
                SetStatus("All scanned hardware drivers are already up to date!", 100);
                return;
            }

            _isBusy = true;
            try
            {
                int okCount = 0;
                for (int i = 0; i < targets.Count; i++)
                {
                    HardwareDevice d = targets[i];
                    SetStatus(string.Format("Updating ({0}/{1}): {2}...", i + 1, targets.Count, d.DeviceName),
                        15 + (int)((i * 80.0) / targets.Count));
                    DriverInstallResult r = await DriverInstallOrchestrator.DownloadBackupAndInstallAsync(
                        d, _storeReport, i == 0 && _settings.CreateRestorePointBeforeInstall, _settings.AutoBackupDriverZip, null);
                    if (r.Succeeded) okCount++;
                }
                SetStatus(string.Format("Batch WHQL update finished: {0}/{1} package(s) installed.", okCount, targets.Count), 100);
                await RunFullScanWorkflowAsync(false);
            }
            finally
            {
                _isBusy = false;
            }
        }

        private async Task BackupSingleDeviceToZipAsync(HardwareDevice dev)
        {
            if (dev == null || _storeReport == null) return;
            foreach (DriverStorePackage pkg in _storeReport.Packages)
            {
                if (string.Equals(pkg.PublishedName, dev.InfName, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(pkg.RepositoryFolder) &&
                    Directory.Exists(pkg.RepositoryFolder))
                {
                    string folder = pkg.RepositoryFolder;
                    SetStatus("Archiving OEM DriverStore package to ZIP...", 45);
                    BackupRecord rec = await Task.Run(() => RescueBackupService.CreateDriverBackupZip(dev, folder, null));
                    SettingsService.AppendHistoryEntry(string.Format("{0:yyyy-MM-dd HH:mm} | BACKUP ZIP | {1} -> {2}",
                        DateTime.Now, dev.DeviceName, Path.GetFileName(rec.ZipFilePath)));
                    SetStatus("Created verified DriverStore ZIP backup: " + rec.ZipFilePath, 100);
                    return;
                }
            }
            SetStatus("Device uses an inbox Microsoft Windows driver (only third-party OEM DriverStore packages need separate ZIP archiving).", 100);
        }

        private void ToggleIgnoreDevice(HardwareDevice dev)
        {
            if (dev == null) return;
            if (dev.IsIgnored)
            {
                _settings.IgnoredInstanceIds.RemoveAll(x => string.Equals(x, dev.InstanceId, StringComparison.OrdinalIgnoreCase));
                dev.IsIgnored = false;
            }
            else
            {
                _settings.IgnoredInstanceIds.Add(dev.InstanceId);
                dev.IsIgnored = true;
            }
            SettingsService.Save(_settings);
            RenderSelectedDeviceInspector(dev);
            RefreshDeviceListFilter();
        }

        private void ExportOfflineProfileDialog()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = "OfflineHardwareProfile-" + Environment.MachineName + ".json",
                Filter = "Driver+ Hardware Manifest (*.json)|*.json"
            };
            if (dlg.ShowDialog(this) == true)
            {
                RescueBackupService.ExportOfflineProfile(_scanSummary.Devices, _systemProfile, dlg.FileName);
                SetStatus("Exported offline hardware profile to " + dlg.FileName, 100);
            }
        }

        private async Task ImportAndAuditOfflineProfileDialogAsync()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Driver+ Hardware Manifest (*.json)|*.json"
            };
            if (dlg.ShowDialog(this) == true)
            {
                OfflineHardwareManifest manifest = RescueBackupService.ImportOfflineProfile(dlg.FileName);
                SetStatus("Auditing imported offline PC '" + manifest.MachineName + "' against live Microsoft Update Catalog...", 35);
                _scanSummary = await WindowsUpdateCatalogClient.EnrichWithOfficialUpdatesAsync(
                    manifest.Devices, null, true, _settings.WhqlOnly, null, manifest.OsBuildNumber, null);
                SwitchTab("drivers");
                UpdateAllViewsAfterScan();
                SetStatus(string.Format("Offline audit for '{0}' complete: {1} official WHQL updates found.", manifest.MachineName, _scanSummary.OutdatedCount), 100);
            }
        }

        private async Task RestoreSelectedBackupActionAsync()
        {
            var item = _backupsListBox.SelectedItem as ListBoxItem;
            var rec = item != null ? item.Tag as BackupRecord : null;
            if (rec == null)
            {
                SetStatus("Select a backup archive in the Rescue list first.", 100);
                return;
            }
            SetStatus("Verifying backup catalog & restoring DriverStore package...", 45);
            string zipPath = rec.ZipFilePath;
            string msg = await Task.Run(() =>
            {
                bool reboot;
                string m;
                RescueBackupService.RestoreDriverFromBackupZip(zipPath, out reboot, out m);
                return m;
            });
            SetStatus(msg, 100);
        }

        private void ToggleGameBoostAction()
        {
            Task.Run(() =>
            {
                string msg = GameBoostService.IsBoostCurrentlyActive()
                    ? GameBoostService.RestoreNormalMode()
                    : GameBoostService.ActivateGameBoost(false);
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    RefreshBoostCard();
                    SetStatus(msg, 100);
                }));
            });
        }

        private void LaunchOrOpenWebPortal()
        {
            try
            {
                if (_webServer == null)
                {
                    _webServer = new LocalWebPortalServer(0);
                    _webServer.SeedSnapshot(_systemProfile, _storeReport, _scanSummary, _runtimes, _diagnostics);
                    _webServer.Start();
                }
                Process.Start(new ProcessStartInfo
                {
                    FileName = _webServer.BaseUrl,
                    UseShellExecute = true
                });
                SetStatus("Opened Live Loopback Web Telemetry Console at " + _webServer.BaseUrl, 100);
            }
            catch (Exception ex)
            {
                SetStatus("Failed starting local web portal: " + ex.Message, 100);
            }
        }

        private void SetStatus(string message, int progressPercent)
        {
            if (_statusText != null) _statusText.Text = message ?? "";
            if (_progressBar != null) _progressBar.Value = Math.Max(0, Math.Min(100, progressPercent));
        }

        private static bool IsRunningAsAdministrator()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                {
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        private void TryRelaunchElevated()
        {
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "--relaunch",
                    UseShellExecute = true,
                    Verb = "runas"
                });
                ExitApplicationCleanly();
            }
            catch
            {
                SetStatus("Administrator elevation was cancelled by user.", 100);
            }
        }
    }
}
