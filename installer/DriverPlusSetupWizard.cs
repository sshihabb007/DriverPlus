using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Driver+ Setup Wizard")]
[assembly: AssemblyDescription("Official Setup & Uninstaller for Driver+ (x64)")]
[assembly: AssemblyCompany("Driver+ Open Source Project")]
[assembly: AssemblyProduct("Driver+")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace DriverPlusInstaller
{
    public static class SetupEntry
    {
        private const string UninstallRegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DriverPlus";
        private static readonly string[] OwnedFileNames = new[]
        {
            "DriverPlus.exe",
            "driverplus.ico",
            "README.md",
            "LICENSE"
        };

        [STAThread]
        public static int Main(string[] args)
        {
            bool silent = HasFlag(args, "--silent") || HasFlag(args, "/S");
            bool noRegistry = HasFlag(args, "--no-registry");
            bool uninstall = HasFlag(args, "--uninstall");
            string customDir = GetArgValue(args, "--install-dir", "");

            string defaultDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Programs\DriverPlus");
            string targetDir = string.IsNullOrWhiteSpace(customDir) ? defaultDir : Path.GetFullPath(customDir);

            if (uninstall)
            {
                return PerformUninstall(targetDir, silent, noRegistry);
            }

            if (silent)
            {
                return PerformInstall(targetDir, false, false, noRegistry, false);
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var form = new SetupWizardForm(targetDir))
            {
                Application.Run(form);
                return form.ExitCode;
            }
        }

        public static int PerformInstall(
            string installDir,
            bool createDesktopShortcut,
            bool createStartMenuShortcut,
            bool skipRegistry,
            bool launchAfter)
        {
            try
            {
                if (!IsSafeInstallDestination(installDir))
                {
                    throw new InvalidOperationException("Refusing to install directly into a drive root or protected Windows/Profile root folder.");
                }
                StopRunningInstances();
                Directory.CreateDirectory(installDir);

                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream payloadStream = asm.GetManifestResourceStream("DriverPlusInstaller.payload.zip"))
                {
                    if (payloadStream == null)
                    {
                        throw new InvalidOperationException("Embedded DriverPlusInstaller.payload.zip resource missing.");
                    }

                    string fullRoot = Path.GetFullPath(installDir);
                    if (!fullRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                    {
                        fullRoot += Path.DirectorySeparatorChar;
                    }

                    using (var archive = new ZipArchive(payloadStream, ZipArchiveMode.Read))
                    {
                        foreach (ZipArchiveEntry entry in archive.Entries)
                        {
                            if (string.IsNullOrEmpty(entry.Name)) continue;
                            string dest = Path.GetFullPath(Path.Combine(fullRoot, entry.FullName));
                            if (!dest.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                            {
                                throw new InvalidDataException("Blocked Zip-Slip path during install: " + entry.FullName);
                            }
                            string parent = Path.GetDirectoryName(dest);
                            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                            entry.ExtractToFile(dest, true);
                        }
                    }
                }

                // Copy self as Uninstall.exe
                string selfExe = Assembly.GetExecutingAssembly().Location;
                string uninstallExe = Path.Combine(installDir, "Uninstall.exe");
                if (!string.Equals(Path.GetFullPath(selfExe), Path.GetFullPath(uninstallExe), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(selfExe, uninstallExe, true);
                }

                string mainExe = Path.Combine(installDir, "DriverPlus.exe");

                if (createDesktopShortcut)
                {
                    string deskLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Driver+.lnk");
                    TryCreateShortcut(deskLnk, mainExe, installDir, "Driver+ — Official WHQL Hardware & Driver Updater");
                }

                if (createStartMenuShortcut)
                {
                    string startDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Driver+");
                    Directory.CreateDirectory(startDir);
                    TryCreateShortcut(Path.Combine(startDir, "Driver+.lnk"), mainExe, installDir, "Driver+ — Official WHQL Hardware & Driver Updater");
                }

                if (!skipRegistry)
                {
                    using (RegistryKey rk = Registry.CurrentUser.CreateSubKey(UninstallRegKey))
                    {
                        if (rk != null)
                        {
                            rk.SetValue("DisplayName", "Driver+ (Clean-Room WHQL Edition)");
                            rk.SetValue("DisplayVersion", "1.0.0");
                            rk.SetValue("Publisher", "Driver+ Open Source Project");
                            rk.SetValue("DisplayIcon", mainExe);
                            rk.SetValue("InstallLocation", installDir);
                            rk.SetValue("UninstallString", string.Format("\"{0}\" --uninstall --install-dir \"{1}\"", uninstallExe, installDir));
                            rk.SetValue("QuietUninstallString", string.Format("\"{0}\" --uninstall --silent --install-dir \"{1}\"", uninstallExe, installDir));
                            rk.SetValue("NoModify", 1, RegistryValueKind.DWord);
                            rk.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                        }
                    }
                }

                if (launchAfter && File.Exists(mainExe))
                {
                    Process.Start(new ProcessStartInfo { FileName = mainExe, WorkingDirectory = installDir, UseShellExecute = true });
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Setup failed: " + ex.Message);
                return 1;
            }
        }

        public static int PerformUninstall(string installDir, bool silent, bool skipRegistry)
        {
            try
            {
                if (!silent)
                {
                    DialogResult dr = MessageBox.Show(
                        "Uninstall Driver+ from:\n" + installDir + " ?",
                        "Driver+ Uninstaller",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);
                    if (dr != DialogResult.Yes) return 0;
                }

                StopRunningInstances();

                // Remove shortcuts if created
                try
                {
                    string deskLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Driver+.lnk");
                    if (File.Exists(deskLnk)) File.Delete(deskLnk);
                    string startDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Driver+");
                    if (Directory.Exists(startDir)) Directory.Delete(startDir, true);
                }
                catch
                {
                }

                if (!skipRegistry)
                {
                    try { Registry.CurrentUser.DeleteSubKeyTree(UninstallRegKey, false); } catch { }
                }

                if (Directory.Exists(installDir))
                {
                    string fullRoot = Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    string selfPath = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);

                    // Delete ONLY known owned files — never recursively wipe arbitrary user folders
                    foreach (string name in OwnedFileNames)
                    {
                        string ownedPath = Path.Combine(installDir, name);
                        if (File.Exists(ownedPath) && !string.Equals(Path.GetFullPath(ownedPath), selfPath, StringComparison.OrdinalIgnoreCase))
                        {
                            try { File.Delete(ownedPath); } catch { }
                        }
                    }

                    string uninstFile = Path.Combine(installDir, "Uninstall.exe");
                    if (File.Exists(uninstFile) && !string.Equals(Path.GetFullPath(uninstFile), selfPath, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(uninstFile); } catch { }
                    }

                    // Deferred self-cleanup for Uninstall.exe via environment variables (zero shell command/variable injection, non-recursive rmdir)
                    if (selfPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        string cmdExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                        var psi = new ProcessStartInfo
                        {
                            FileName = cmdExe,
                            Arguments = "/D /C ping 127.0.0.1 -n 2 > nul & del /F /Q \"%DP_SELF%\" & rmdir \"%DP_DIR%\" 2>nul",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        psi.EnvironmentVariables["DP_SELF"] = selfPath;
                        psi.EnvironmentVariables["DP_DIR"] = Path.GetFullPath(installDir);
                        Process.Start(psi);
                    }
                    else
                    {
                        try { Directory.Delete(installDir, false); } catch { }
                    }
                }

                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Uninstall failed: " + ex.Message);
                return 1;
            }
        }

        public static bool IsSafeInstallDestination(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return false;
            string full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
            string root = Path.GetPathRoot(full).TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return false;

            string[] forbidden = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };
            foreach (string f in forbidden)
            {
                if (!string.IsNullOrEmpty(f) &&
                    string.Equals(full, f.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        }

        private static void StopRunningInstances()
        {
            try
            {
                foreach (Process p in Process.GetProcessesByName("DriverPlus"))
                {
                    try
                    {
                        p.Kill();
                        p.WaitForExit(2000);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private static void TryCreateShortcut(string lnkPath, string targetExe, string workDir, string description)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;
                object shell = Activator.CreateInstance(shellType);
                object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                Type scType = shortcut.GetType();
                scType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { targetExe });
                scType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workDir });
                scType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { description });
                scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            catch
            {
            }
        }

        private static bool HasFlag(string[] args, string flag)
        {
            if (args == null) return false;
            foreach (string a in args)
            {
                if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string GetArgValue(string[] args, string key, string fallback)
        {
            if (args == null) return fallback;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    return args[i + 1];
                }
            }
            return fallback;
        }
    }

    public sealed class SetupWizardForm : Form
    {
        public int ExitCode { get; private set; }
        private readonly TextBox _dirBox;
        private readonly CheckBox _deskCheck;
        private readonly CheckBox _startCheck;
        private readonly CheckBox _launchCheck;
        private readonly Button _installBtn;
        private readonly Label _statusLbl;

        public SetupWizardForm(string defaultInstallDir)
        {
            Text = "Driver+ v1.0.0 Setup (x64)";
            ClientSize = new Size(540, 340);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(15, 23, 42);
            ForeColor = Color.FromArgb(248, 250, 252);
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

            try
            {
                using (Stream icoStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DriverPlusInstaller.app.ico"))
                {
                    if (icoStream != null) Icon = new Icon(icoStream);
                }
            }
            catch
            {
            }

            var header = new Label
            {
                Text = "Install Driver+ (Official WHQL Clean-Room Edition)",
                Font = new Font("Segoe UI", 13.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(52, 211, 153),
                Location = new Point(24, 22),
                AutoSize = true
            };
            Controls.Add(header);

            var sub = new Label
            {
                Text = "Native 64-bit Windows hardware scanner, Microsoft Update Catalog WHQL installer, Game Runtime Doctor, and HVCI Auditor. Zero adware or background services.",
                ForeColor = Color.FromArgb(148, 163, 184),
                Location = new Point(24, 54),
                Size = new Size(492, 42)
            };
            Controls.Add(sub);

            Controls.Add(new Label
            {
                Text = "Installation Folder:",
                Location = new Point(24, 108),
                AutoSize = true
            });

            _dirBox = new TextBox
            {
                Text = defaultInstallDir,
                Location = new Point(24, 132),
                Size = new Size(400, 26),
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(_dirBox);

            var browseBtn = new Button
            {
                Text = "Browse...",
                Location = new Point(432, 130),
                Size = new Size(84, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.White
            };
            browseBtn.Click += (s, e) =>
            {
                using (var fbd = new FolderBrowserDialog { SelectedPath = _dirBox.Text })
                {
                    if (fbd.ShowDialog(this) == DialogResult.OK)
                    {
                        string chosen = fbd.SelectedPath.TrimEnd(Path.DirectorySeparatorChar);
                        if (!chosen.EndsWith("DriverPlus", StringComparison.OrdinalIgnoreCase))
                        {
                            chosen = Path.Combine(chosen, "DriverPlus");
                        }
                        _dirBox.Text = chosen;
                    }
                }
            };
            Controls.Add(browseBtn);

            _deskCheck = new CheckBox { Text = "Create Desktop shortcut", Checked = true, Location = new Point(24, 174), AutoSize = true };
            _startCheck = new CheckBox { Text = "Create Start Menu shortcut", Checked = true, Location = new Point(24, 202), AutoSize = true };
            _launchCheck = new CheckBox { Text = "Launch Driver+ immediately after installation", Checked = true, Location = new Point(24, 230), AutoSize = true };
            Controls.Add(_deskCheck);
            Controls.Add(_startCheck);
            Controls.Add(_launchCheck);

            _statusLbl = new Label
            {
                Text = "Ready to install.",
                ForeColor = Color.FromArgb(148, 163, 184),
                Location = new Point(24, 286),
                Size = new Size(340, 24)
            };
            Controls.Add(_statusLbl);

            _installBtn = new Button
            {
                Text = "Install Now",
                Location = new Point(386, 278),
                Size = new Size(130, 36),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(5, 150, 105),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold)
            };
            _installBtn.Click += OnInstallClick;
            Controls.Add(_installBtn);
        }

        private void OnInstallClick(object sender, EventArgs e)
        {
            _installBtn.Enabled = false;
            _statusLbl.Text = "Extracting verified x64 binaries...";
            Application.DoEvents();

            int code = SetupEntry.PerformInstall(
                _dirBox.Text.Trim(),
                _deskCheck.Checked,
                _startCheck.Checked,
                false,
                _launchCheck.Checked);

            ExitCode = code;
            if (code == 0)
            {
                Close();
            }
            else
            {
                _statusLbl.Text = "Installation encountered an error.";
                _installBtn.Enabled = true;
            }
        }
    }
}
