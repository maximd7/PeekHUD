using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace PeekHUD
{
    public partial class App : Application
    {
        private static readonly object LogSync = new();
        private static int _fatalDialogShown;

        public static string CrashLogPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PeekHUD",
            "crashlog.txt");

        private Mutex? _mutex;
        private WinForms.NotifyIcon? _notifyIcon;
        private WinForms.ContextMenuStrip? _trayMenu;
        private WinForms.ToolStripMenuItem? _toggleItem, _editItem;
        private HotkeyService? _hotkey;
        private FpsMonitor? _fps;
        private DispatcherTimer? _saveTimer;
        private SettingsWindow? _settingsWindow;

        public static new App Current => (App)Application.Current;

        public AppSettings Settings { get; private set; } = null!;
        public HardwareMonitorService Hardware { get; private set; } = null!;
        public OverlayWindow Overlay { get; private set; } = null!;
        public bool IsExiting { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                LogNonFatal("TaskScheduler.UnobservedTaskException", args.Exception);
                args.SetObserved();
            };

            try
            {
                base.OnStartup(e);

                bool first = AcquireSingleInstanceMutex();
                if (!first)
                {
                    Shutdown();
                    return;
                }

                bool settingsFileExisted = File.Exists(AppSettings.SettingsFilePath);
                Settings = AppSettings.Load();
                bool isFirstRun = !settingsFileExisted || !Settings.FirstRunCompleted;
                if (isFirstRun)
                {
                    // Ensure "Run on Windows startup" is strictly OFF by default on fresh installs.
                    Settings.RunOnStartup = false;
                    StartupService.SetEnabled(false);
                }
                else
                {
                    Settings.RunOnStartup = StartupService.IsEnabled(); // reflect reality, not stale JSON
                }
                ApplyTheme(Settings.EffectiveTheme);

                string[] cmdArgs = e.Args.Length > 0
                    ? e.Args
                    : Environment.GetCommandLineArgs().Skip(1).ToArray();

                bool isSilent = cmdArgs.Any(arg =>
                    arg.Equals("--silent", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("-s", StringComparison.OrdinalIgnoreCase));
                bool openSettingsArg = cmdArgs.Any(arg =>
                    arg.Equals("--settings", StringComparison.OrdinalIgnoreCase));

                _fps = new FpsMonitor();
                _fps.Start();
                Hardware = new HardwareMonitorService(_fps);

                Overlay = new OverlayWindow(Settings, Hardware);
                Overlay.Show();                       // creates the HWND + applies click-through styles
                if (!Settings.OverlayVisible) Overlay.Hide();

                BuildTray();

                _hotkey = new HotkeyService();
                _hotkey.Pressed += () => Settings.OverlayVisible = !Settings.OverlayVisible;
                if (!_hotkey.Register(Settings.Hotkey) && !isSilent)
                {
                    _notifyIcon?.ShowBalloonTip(
                        4000,
                        "PeekHUD",
                        $"Hotkey {Settings.Hotkey} is in use by another application. Pick another in the Overlay tab.",
                        WinForms.ToolTipIcon.Warning);
                }

                _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Settings.Save(); };
                Settings.Changed += OnSettingsChanged;

                Hardware.Start();

                if (!isSilent)
                {
                    if (isFirstRun)
                    {
                        _notifyIcon?.ShowBalloonTip(
                            3000,
                            "PeekHUD Active",
                            "PeekHUD is running in your system tray. Double-click or right-click to open settings.",
                            WinForms.ToolTipIcon.Info
                        );
                        Settings.FirstRunCompleted = true;
                        Settings.Save();
                        OpenSettings();
                    }
                    else if (openSettingsArg)
                    {
                        OpenSettings();
                    }
                }
            }
            catch (Exception ex)
            {
                ReportFatalCrash("Startup Crash", ex);
                Shutdown(1);
            }
        }

        private bool AcquireSingleInstanceMutex()
        {
            try
            {
                _mutex = new Mutex(true, "Global\\PeekHUD.SingleInstance", out bool first);
                if (!first)
                {
                    _mutex.Dispose();
                    _mutex = null;
                }
                return first;
            }
            catch (UnauthorizedAccessException)
            {
                // Another instance in a different session already owns the Global mutex.
                _mutex = null;
                return false;
            }
            catch (Exception ex)
            {
                LogNonFatal("SingleInstanceMutex", ex);
                return true;
            }
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            ReportFatalCrash("Unhandled UI Exception", e.Exception);
            e.Handled = true;
            if (Overlay == null)
                Shutdown(1);
        }

        private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception
                     ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown fatal exception.");
            ReportFatalCrash("Unhandled AppDomain Exception", ex);
        }

        public static void ReportFatalCrash(string context, Exception ex)
        {
            WriteCrashEntry("FATAL", context, ex);

            if (Interlocked.CompareExchange(ref _fatalDialogShown, 1, 0) == 0)
            {
                try
                {
                    MessageBox.Show(
                        $"PeekHUD encountered a fatal error ({context}):\n\n{ex.GetType().Name}: {ex.Message}\n\nFull stack trace saved to:\n{CrashLogPath}",
                        "PeekHUD - Crash Report",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                catch { /* ignore secondary UI failure */ }
            }
        }

        public static void LogNonFatal(string context, Exception ex)
        {
            WriteCrashEntry("WARN", context, ex);
        }

        private static void WriteCrashEntry(string level, string context, Exception ex)
        {
            try
            {
                string logPath = CrashLogPath;
                string? dir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                string entry =
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {context}{Environment.NewLine}" +
                    $"OS: {Environment.OSVersion} | .NET: {Environment.Version}{Environment.NewLine}" +
                    $"{ex}{Environment.NewLine}" +
                    new string('-', 80) + Environment.NewLine;

                lock (LogSync)
                {
                    File.AppendAllText(logPath, entry);
                }
            }
            catch { /* logging must never throw */ }
        }

        public void ApplyTheme(AppTheme theme)
        {
            static SolidColorBrush BrushOf(string hex)
            {
                var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                b.Freeze();
                return b;
            }

            if (theme == AppTheme.Light)
            {
                Resources["WindowBrush"] = BrushOf("#F4F4F5");
                Resources["PanelBrush"] = BrushOf("#FFFFFF");
                Resources["LineBrush"] = BrushOf("#E4E4E7");
                Resources["HoverBrush"] = BrushOf("#EAEAEF");
                Resources["TextBrush"] = BrushOf("#18181B");
                Resources["DimBrush"] = BrushOf("#52525B");
                Resources["WarnBrush"] = BrushOf("#D97706");
                Resources["CritBrush"] = BrushOf("#DC2626");
                Resources["MoveBtnBgBrush"] = BrushOf("#F4F4F5");
                Resources["MoveBtnBorderBrush"] = BrushOf("#E4E4E7");
                Resources["MoveBtnHoverBrush"] = BrushOf("#E4E4E7");
                Resources["MoveBtnFgBrush"] = BrushOf("#18181B");
            }
            else
            {
                Resources["WindowBrush"] = BrushOf("#0D0E12");
                Resources["PanelBrush"] = BrushOf("#14161D");
                Resources["LineBrush"] = BrushOf("#222530");
                Resources["HoverBrush"] = BrushOf("#1E222D");
                Resources["TextBrush"] = BrushOf("#EDEDED");
                Resources["DimBrush"] = BrushOf("#9CA3AF");
                Resources["WarnBrush"] = BrushOf("#F59E0B");
                Resources["CritBrush"] = BrushOf("#EF4444");
                Resources["MoveBtnBgBrush"] = BrushOf("#222530");
                Resources["MoveBtnBorderBrush"] = BrushOf("#2F3443");
                Resources["MoveBtnHoverBrush"] = BrushOf("#2C303E");
                Resources["MoveBtnFgBrush"] = BrushOf("#EDEDED");
            }

            ApplyAccent();
            ApplyTrayTheme(theme);
            _settingsWindow?.ApplyWindowChromeTheme(theme);
        }

        public void ApplyAccent()
        {
            if (!MetricSetting.TryParseColor(Settings?.AppAccentColor, out Color accent))
                accent = Color.FromRgb(0x00, 0xE5, 0xFF);

            var accentBrush = new SolidColorBrush(accent);
            accentBrush.Freeze();
            Resources["AccentBrush"] = accentBrush;

            // Compute WCAG relative luminance so segmented button text stays legible on any accent color.
            static double ToLinear(byte channel)
            {
                double c = channel / 255.0;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
            double luminance = 0.2126 * ToLinear(accent.R) + 0.7152 * ToLinear(accent.G) + 0.0722 * ToLinear(accent.B);
            Color fg = luminance > 0.28 ? Color.FromRgb(0x0D, 0x0E, 0x12) : Colors.White;

            var fgBrush = new SolidColorBrush(fg);
            fgBrush.Freeze();
            Resources["AccentFgBrush"] = fgBrush;

            if (Settings != null) ApplyTrayTheme(Settings.EffectiveTheme);
        }

        private void OnSettingsChanged(string prop)
        {
            switch (prop)
            {
                case "*":
                    ApplyTheme(Settings.EffectiveTheme);
                    ApplyHotkey(Settings.Hotkey);
                    StartupService.SetEnabled(Settings.RunOnStartup);
                    if (_toggleItem != null) _toggleItem.Checked = Settings.OverlayVisible;
                    if (_editItem != null) _editItem.Checked = Settings.EditMode;
                    return; // ResetToFactoryDefaults already persisted settings.json
                case nameof(AppSettings.Theme):
                    ApplyTheme(Settings.EffectiveTheme);
                    break;
                case nameof(AppSettings.AppAccentColor):
                    ApplyAccent();
                    break;
                case nameof(AppSettings.RunOnStartup):
                    if (!StartupService.SetEnabled(Settings.RunOnStartup))
                        _notifyIcon?.ShowBalloonTip(3000, "PeekHUD", "Could not update the startup task.", WinForms.ToolTipIcon.Warning);
                    break;
                case nameof(AppSettings.OverlayVisible):
                    if (_toggleItem != null) _toggleItem.Checked = Settings.OverlayVisible;
                    break;
                case nameof(AppSettings.EditMode):
                    if (_editItem != null) _editItem.Checked = Settings.EditMode;
                    return; // transient, not persisted
            }
            _saveTimer?.Stop();
            _saveTimer?.Start();
        }

        // ---------------------------------------------------------------- tray

        private void BuildTray()
        {
            _trayMenu = new WinForms.ContextMenuStrip
            {
                ShowImageMargin = false,
                ShowCheckMargin = true,
                Font = new Drawing.Font("Segoe UI", 9f)
            };

            var settings = new WinForms.ToolStripMenuItem("Settings");
            settings.Click += (_, _) => OpenSettings();

            _toggleItem = new WinForms.ToolStripMenuItem { Checked = Settings.OverlayVisible };
            _toggleItem.Click += (_, _) => Settings.OverlayVisible = !Settings.OverlayVisible;

            _editItem = new WinForms.ToolStripMenuItem("Edit Mode (Move HUD)");
            _editItem.Click += (_, _) => Settings.EditMode = !Settings.EditMode;

            var visitSite = new WinForms.ToolStripMenuItem("Visit Maxim DevLab");
            visitSite.Click += (_, _) => SettingsWindow.OpenExternal("https://maximdevlab.com/");

            var exit = new WinForms.ToolStripMenuItem("Exit");
            exit.Click += (_, _) => ExitApp();

            _trayMenu.Items.AddRange(new WinForms.ToolStripItem[]
            {
                settings,
                _toggleItem,
                _editItem,
                new WinForms.ToolStripSeparator(),
                visitSite,
                new WinForms.ToolStripSeparator(),
                exit
            });
            UpdateToggleText();
            ApplyTrayTheme(Settings.EffectiveTheme);

            _notifyIcon = new WinForms.NotifyIcon
            {
                Icon = LoadTrayIcon(),
                Text = "PeekHUD",
                ContextMenuStrip = _trayMenu,
                Visible = true
            };

            // Left single-click, double-click, or clicking the balloon tip opens SettingsWindow
            // (right-click continues to open ContextMenuStrip cleanly).
            _notifyIcon.MouseClick += (_, e) =>
            {
                if (e.Button == WinForms.MouseButtons.Left)
                    OpenSettings();
            };
            _notifyIcon.DoubleClick += (s, e) => OpenSettings();
            _notifyIcon.BalloonTipClicked += (_, _) => OpenSettings();
        }

        private void ApplyTrayTheme(AppTheme theme)
        {
            if (_trayMenu == null) return;
            bool light = theme == AppTheme.Light;
            Drawing.Color accent = MetricSetting.TryParseColor(Settings?.AppAccentColor, out Color c)
                ? Drawing.Color.FromArgb(c.A, c.R, c.G, c.B)
                : Drawing.ColorTranslator.FromHtml("#00E5FF");
            _trayMenu.Renderer = new TrayMenuRenderer(light, accent);
            _trayMenu.BackColor = Drawing.ColorTranslator.FromHtml(light ? "#FFFFFF" : "#14161D");
            _trayMenu.ForeColor = Drawing.ColorTranslator.FromHtml(light ? "#18181B" : "#EDEDED");
        }

        private void UpdateToggleText()
        {
            if (_toggleItem != null) _toggleItem.Text = $"Toggle Overlay ({Settings.Hotkey})";
        }

        /// <summary>Tries to bind a new global hotkey; keeps the previous one on failure.</summary>
        public bool ApplyHotkey(string gesture)
        {
            if (_hotkey == null) return false;
            if (_hotkey.Register(gesture))
            {
                Settings.Hotkey = gesture;
                UpdateToggleText();
                return true;
            }
            _hotkey.Register(Settings.Hotkey);
            return false;
        }

        public void OpenSettings()
        {
            _settingsWindow ??= new SettingsWindow(Settings)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            if (!_settingsWindow.IsVisible) _settingsWindow.Show();
            if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            _settingsWindow.Focus();
        }

        public void ShowSettings() => OpenSettings();

        private static Drawing.Icon LoadTrayIcon()
        {
            // 1. Extract directly from the executing binary (works regardless of working directory or single-file publish).
            try
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                    exePath = Process.GetCurrentProcess().MainModule?.FileName;

                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                {
                    Drawing.Icon? associated = Drawing.Icon.ExtractAssociatedIcon(exePath);
                    if (associated != null) return associated;
                }
            }
            catch (Exception ex)
            {
                LogNonFatal("LoadTrayIcon.ExtractAssociatedIcon", ex);
            }

            // 2. Fallback to embedded WPF pack resource stream.
            try
            {
                var res = GetResourceStream(new Uri("pack://application:,,,/PeekIcon.ico"));
                if (res?.Stream != null)
                {
                    using (res.Stream)
                        return new Drawing.Icon(res.Stream);
                }
            }
            catch (Exception ex)
            {
                LogNonFatal("LoadTrayIcon.PackResource", ex);
            }

            return Drawing.SystemIcons.Application;
        }

        // ---------------------------------------------------------------- exit

        private void ExitApp()
        {
            if (IsExiting) return;
            IsExiting = true;

            if (Settings != null)
            {
                Settings.Changed -= OnSettingsChanged;
                Settings.EditMode = false;
                Settings.Save();
            }

            if (_notifyIcon != null) { _notifyIcon.Visible = false; _notifyIcon.Dispose(); }
            _hotkey?.Dispose();
            Hardware?.Dispose();
            _fps?.Dispose();
            _settingsWindow?.Close();
            Overlay?.Close();

            try { _mutex?.ReleaseMutex(); } catch { }
            _mutex?.Dispose();
            Shutdown();
        }

        // Flat tray menu (hairline border, no rounded edges, follows Dark / Light theme)
        private sealed class TrayMenuRenderer : WinForms.ToolStripProfessionalRenderer
        {
            private readonly Drawing.Color _textColor;

            public TrayMenuRenderer(bool light, Drawing.Color accent) : base(new TrayColors(light, accent))
            {
                RoundedEdges = false;
                _textColor = Drawing.ColorTranslator.FromHtml(light ? "#18181B" : "#EDEDED");
            }

            protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = _textColor;
                base.OnRenderItemText(e);
            }
        }

        private sealed class TrayColors : WinForms.ProfessionalColorTable
        {
            private readonly bool _light;
            private readonly Drawing.Color _accent;
            public TrayColors(bool light, Drawing.Color accent) { _light = light; _accent = accent; }

            private static Drawing.Color C(string hex) => Drawing.ColorTranslator.FromHtml(hex);
            public override Drawing.Color ToolStripDropDownBackground => C(_light ? "#FFFFFF" : "#14161D");
            public override Drawing.Color MenuBorder => C(_light ? "#E4E4E7" : "#222530");
            public override Drawing.Color MenuItemBorder => C(_light ? "#E4E4E7" : "#222530");
            public override Drawing.Color MenuItemSelected => C(_light ? "#EAEAEF" : "#1E222D");
            public override Drawing.Color MenuItemSelectedGradientBegin => C(_light ? "#EAEAEF" : "#1E222D");
            public override Drawing.Color MenuItemSelectedGradientEnd => C(_light ? "#EAEAEF" : "#1E222D");
            public override Drawing.Color ImageMarginGradientBegin => C(_light ? "#FFFFFF" : "#14161D");
            public override Drawing.Color ImageMarginGradientMiddle => C(_light ? "#FFFFFF" : "#14161D");
            public override Drawing.Color ImageMarginGradientEnd => C(_light ? "#FFFFFF" : "#14161D");
            public override Drawing.Color SeparatorDark => C(_light ? "#E4E4E7" : "#222530");
            public override Drawing.Color SeparatorLight => C(_light ? "#E4E4E7" : "#222530");
            public override Drawing.Color CheckBackground => _accent;
            public override Drawing.Color CheckSelectedBackground => _accent;
            public override Drawing.Color CheckPressedBackground => _accent;
        }
    }
}
