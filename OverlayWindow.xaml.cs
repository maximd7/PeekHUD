using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PeekHUD
{
    /// <summary>Win32 declarations shared across the app.</summary>
    internal static class NativeMethods
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        public static readonly IntPtr HWND_TOPMOST = new(-1);
        public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    }

    public partial class OverlayWindow : Window
    {
        private const int HistoryLen = 60;
        private const double GraphW = 80, GraphH = 16;

        private sealed class RowView
        {
            public MetricSetting Setting = null!;
            public Run Number = null!;
            public Run Unit = null!;
            public Polyline? Line;
        }

        private readonly AppSettings _settings;
        private readonly HardwareMonitorService _hw;
        private readonly Dictionary<MetricKind, Queue<double>> _history =
            Enum.GetValues<MetricKind>().ToDictionary(k => k, _ => new Queue<double>(HistoryLen));
        private readonly List<RowView> _rows = new();
        private IntPtr _hwnd;
        private bool _isEditMode;

        // Right-edge anchoring: when the HUD sits on the right half of its monitor, width changes
        // (Number <-> Graph, metric toggles, Scale) grow/shrink leftward so the right edge stays put.
        private bool _isDragging;
        private bool _isAnchoredRight;
        private bool _anchorInitialized;

        private static readonly Brush DimBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)));
        private static readonly Brush LineBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0x25, 0x30)));
        private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

        public OverlayWindow(AppSettings settings, HardwareMonitorService hw)
        {
            InitializeComponent();
            _settings = settings;
            _hw = hw;
            _isEditMode = settings.EditMode;

            if (_settings.OverlayLeft <= 0)
            {
                double defaultMargin = 20.0;
                double defaultWidth = 220.0;
                Top = defaultMargin;
                Left = Math.Max(20.0, SystemParameters.WorkArea.Right - defaultWidth - defaultMargin);
                _settings.OverlayLeft = Left;
                _settings.OverlayTop = Top;
                _settings.Save();
            }
            else
            {
                Left = settings.OverlayLeft;
                Top = settings.OverlayTop;
            }
            ClampToScreen();

            Loaded += (_, _) =>
            {
                if (_settings.OverlayLeft <= 0)
                {
                    double defaultMargin = 20.0;
                    double defaultWidth = 220.0;
                    Top = defaultMargin;
                    Left = Math.Max(20.0, SystemParameters.WorkArea.Right - defaultWidth - defaultMargin);
                    _settings.OverlayLeft = Left;
                    _settings.OverlayTop = Top;
                    _settings.Save();
                }
                UpdateHorizontalAnchor();
            };

            SizeChanged += Window_SizeChanged; // Number <-> Graph / Scale changes the width
            LocationChanged += (_, _) =>
            {
                // While DragMove is active, the final position is persisted once on release.
                if (!_isEditMode || _isDragging) return;
                _settings.OverlayLeft = Left;
                _settings.OverlayTop = Top;
            };

            _settings.Changed += OnSettingsChanged;
            _hw.Sampled += (_, _) => Refresh();

            Rebuild();
            SetEditMode(_settings.EditMode);
        }

        // ------------------------------------------------------------ dragging & anchoring

        private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed && _isEditMode)
            {
                _isDragging = true;
                try
                {
                    DragMove(); // blocks until the mouse button is released
                }
                catch (InvalidOperationException) { }
                finally
                {
                    _isDragging = false;
                }
                SaveOverlayPosition();
                e.Handled = true;
            }
        }

        private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // Always release the drag flag so SizeChanged anchoring resumes after manual positioning.
            _isDragging = false;
            if (_isEditMode)
            {
                SaveOverlayPosition();
            }
        }

        /// <summary>Clamps on-screen, re-evaluates the left/right anchor, and persists the final position.</summary>
        private void SaveOverlayPosition()
        {
            ClampToWorkArea();
            UpdateHorizontalAnchor();
            _settings.OverlayLeft = Left;
            _settings.OverlayTop = Top;
            _settings.Save();
        }

        /// <summary>
        /// Anchors to the right edge when the HUD's center lies on the right half of the monitor it is on
        /// (per-monitor work area, falling back to the primary screen before the HWND exists).
        /// </summary>
        private void UpdateHorizontalAnchor()
        {
            if (ActualWidth <= 0) return;

            Rect wa = GetWorkAreaDip();
            double screenMidX = wa.Width > 0
                ? wa.Left + wa.Width / 2.0
                : SystemParameters.PrimaryScreenWidth / 2.0;

            _isAnchoredRight = (Left + ActualWidth / 2.0) > screenMidX;
            _anchorInitialized = true;
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_anchorInitialized && ActualWidth > 0)
            {
                UpdateHorizontalAnchor();
            }

            if (_isDragging) return; // never fight a manual drag

            bool widthChanged = e.WidthChanged
                && e.PreviousSize.Width > 0
                && Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 1.0;

            if (widthChanged && _isAnchoredRight)
            {
                // Keep the right edge fixed when the content width shrinks or expands.
                double previousRight = Left + e.PreviousSize.Width;
                Left = previousRight - e.NewSize.Width;
            }

            // Ensure the HUD can never be pushed off-screen by a width/height change.
            ClampToWorkArea();

            if (widthChanged)
            {
                _settings.OverlayLeft = Left;
                _settings.OverlayTop = Top;
                _settings.Save();
            }
        }

        // ------------------------------------------------------------ Win32 click-through

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;
            ApplyExtendedStyles(clickThrough: !_settings.EditMode);
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        }

        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int MA_NOACTIVATE = 3;

        /// <summary>Even in edit mode (when clicks hit the HUD) the window never takes activation from the game.</summary>
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_MOUSEACTIVATE)
            {
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// LAYERED + TOOLWINDOW + NOACTIVATE are always set (no taskbar/Alt-Tab entry, never steals focus).
        /// TRANSPARENT is what makes the window invisible to mouse hit-testing, so input reaches the game.
        /// </summary>
        private void ApplyExtendedStyles(bool clickThrough)
        {
            if (_hwnd == IntPtr.Zero) return;
            int ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
            ex |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
            ex = clickThrough ? ex | NativeMethods.WS_EX_TRANSPARENT : ex & ~NativeMethods.WS_EX_TRANSPARENT;
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, ex);
        }

        private void ReassertTopmost()
        {
            if (_hwnd == IntPtr.Zero || !IsVisible) return;
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }

        // ------------------------------------------------------------ settings

        private void OnSettingsChanged(string prop)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnSettingsChanged(prop)); return; }

            switch (prop)
            {
                case "*":
                    SetEditMode(_settings.EditMode);
                    Left = _settings.OverlayLeft;
                    Top = _settings.OverlayTop;
                    ClampToScreen();
                    UpdateHorizontalAnchor(); // factory reset / reload may move the HUD to the other half
                    Rebuild();
                    ApplyAppearance();
                    if (_settings.OverlayVisible) { if (!IsVisible) Show(); ReassertTopmost(); }
                    else Hide();
                    break;
                case nameof(AppSettings.OverlayLeft):
                    if (!_settings.EditMode && Math.Abs(Left - _settings.OverlayLeft) > 0.5) Left = _settings.OverlayLeft;
                    break;
                case nameof(AppSettings.OverlayTop):
                    if (!_settings.EditMode && Math.Abs(Top - _settings.OverlayTop) > 0.5) Top = _settings.OverlayTop;
                    break;
                case nameof(AppSettings.Scale):
                case nameof(AppSettings.BgTransparency):
                    ApplyAppearance();
                    break;
                case nameof(AppSettings.EnableWarningColors):
                case nameof(AppSettings.WarningColor):
                case nameof(AppSettings.CriticalColor):
                case nameof(AppSettings.TempWarning):
                case nameof(AppSettings.TempCritical):
                case nameof(AppSettings.LoadWarning):
                case nameof(AppSettings.LoadCritical):
                case nameof(AppSettings.FpsWarning):
                case nameof(AppSettings.FpsCritical):
                    Refresh(pushHistory: false);
                    break;
                case nameof(MetricSetting.Enabled):
                case nameof(MetricSetting.Mode):
                case nameof(MetricSetting.ColorHex):
                    Rebuild();
                    break;
                case nameof(AppSettings.OverlayVisible):
                    if (_settings.OverlayVisible) { if (!IsVisible) Show(); ReassertTopmost(); }
                    else
                    {
                        if (_settings.EditMode) _settings.EditMode = false; // hiding ends edit mode
                        Hide();
                    }
                    break;
                case nameof(AppSettings.EditMode):
                    SetEditMode(_settings.EditMode);
                    break;
            }
        }

        private void SetEditMode(bool edit)
        {
            _isEditMode = edit;
            if (edit && !_settings.OverlayVisible) _settings.OverlayVisible = true;
            if (!edit) _isDragging = false;

            Cursor = edit ? Cursors.SizeAll : Cursors.Arrow;
            Root.Cursor = edit ? Cursors.SizeAll : Cursors.Arrow;
            EditBorder.Visibility = edit ? Visibility.Visible : Visibility.Collapsed;
            ApplyExtendedStyles(clickThrough: !edit);
            ApplyAppearance();

            ClampToWorkArea(); // also persists the final position when locking
            if (!edit) UpdateHorizontalAnchor();
        }

        private void ApplyAppearance()
        {
            ScaleXf.ScaleX = ScaleXf.ScaleY = _settings.Scale;

            // BgTransparency: 0.0 = Solid (100% alpha), 1.0 = Clear/Transparent (0% alpha).
            double opacity = Math.Clamp(1.0 - _settings.BgTransparency, 0.0, 1.0);
            if (_settings.EditMode) opacity = Math.Max(opacity, 0.35); // keep the frame visible while positioning
            var tint = new SolidColorBrush(Color.FromArgb((byte)Math.Round(opacity * 255), 0x0D, 0x0E, 0x12));
            tint.Freeze();
            Hud.Background = tint;
        }

        private const double EdgeMargin = 10;
        private bool _clamping;

        /// <summary>Working area (excludes taskbar) of the monitor the HUD is on, in WPF device-independent units.</summary>
        private Rect GetWorkAreaDip()
        {
            var target = (PresentationSource.FromVisual(this) as HwndSource)?.CompositionTarget;
            if (_hwnd == IntPtr.Zero || target == null) return SystemParameters.WorkArea;

            var wa = System.Windows.Forms.Screen.FromHandle(_hwnd).WorkingArea; // physical pixels
            var m = target.TransformFromDevice;
            return new Rect(m.Transform(new Point(wa.Left, wa.Top)), m.Transform(new Point(wa.Right, wa.Bottom)));
        }

        /// <summary>Keeps the whole HUD inside the current monitor's work area and persists any correction.</summary>
        private void ClampToWorkArea()
        {
            if (_clamping || _isDragging || !IsVisible || ActualWidth <= 0 || ActualHeight <= 0) return;
            _clamping = true;
            try
            {
                var wa = GetWorkAreaDip();
                double left = Left, top = Top;

                if (left + ActualWidth > wa.Right) left = wa.Right - ActualWidth - EdgeMargin;
                if (top + ActualHeight > wa.Bottom) top = wa.Bottom - ActualHeight - EdgeMargin;
                if (left < wa.Left) left = wa.Left + EdgeMargin;   // left/top win if the HUD is larger than the area
                if (top < wa.Top) top = wa.Top + EdgeMargin;

                if (Math.Abs(left - Left) > 0.5 || Math.Abs(top - Top) > 0.5)
                {
                    Left = left;
                    Top = top;
                }
                if (_settings.OverlayLeft != Left) _settings.OverlayLeft = Left;
                if (_settings.OverlayTop != Top) _settings.OverlayTop = Top;
            }
            finally { _clamping = false; }
        }

        private void ClampToScreen()
        {
            double maxL = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40;
            double maxT = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40;
            Left = Math.Clamp(Left, SystemParameters.VirtualScreenLeft, Math.Max(SystemParameters.VirtualScreenLeft, maxL));
            Top = Math.Clamp(Top, SystemParameters.VirtualScreenTop, Math.Max(SystemParameters.VirtualScreenTop, maxT));
        }

        // ------------------------------------------------------------ rendering

        private void Rebuild()
        {
            Rows.Children.Clear();
            _rows.Clear();
            // Reset WPF's shared-size cache so unchecking wider rows (e.g. RAM) lets the HUD shrink immediately.
            Grid.SetIsSharedSizeScope(Rows, false);
            Grid.SetIsSharedSizeScope(Rows, true);

            var enabledMetrics = _settings.Metrics.Where(m => m.Enabled).ToList();
            bool anyGraph = enabledMetrics.Any(m => m.Mode == MetricMode.Graph);
            bool hasMemoryReadout = enabledMetrics.Any(m => m.Kind is MetricKind.GpuVram or MetricKind.Ram);

            foreach (var m in enabledMetrics)
            {
                bool graph = m.Mode == MetricMode.Graph;
                bool isMemory = m.Kind is MetricKind.GpuVram or MetricKind.Ram;
                var grid = new Grid
                {
                    Margin = new Thickness(0, 2, 0, 2),
                    // Isolate graph width to graph rows so standard [Tag] -> [Value] rows stay compact without a middle void.
                    HorizontalAlignment = (!graph && anyGraph) ? HorizontalAlignment.Left : HorizontalAlignment.Stretch
                };

                grid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = GridLength.Auto,
                    MinWidth = 28,
                    SharedSizeGroup = "Tag"
                });

                var tag = new TextBlock
                {
                    Text = m.HudTag,
                    FontFamily = Mono,
                    FontSize = 10,
                    Foreground = DimBrush,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                grid.Children.Add(tag);

                var row = new RowView { Setting = m, Number = new Run("--"), Unit = new Run { Foreground = DimBrush, FontSize = 10 } };

                var value = new TextBlock
                {
                    FontFamily = Mono,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    TextAlignment = TextAlignment.Right,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
                value.Inlines.Add(row.Number);
                value.Inlines.Add(row.Unit);

                if (graph)
                {
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    grid.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width = new GridLength(1, GridUnitType.Star),
                        MinWidth = isMemory ? 96 : 42,
                        SharedSizeGroup = "GraphValue"
                    });

                    row.Line = new Polyline
                    {
                        Stroke = m.ColorBrush, StrokeThickness = 1, StrokeLineJoin = PenLineJoin.Round,
                        SnapsToDevicePixels = false
                    };
                    var canvas = new Canvas { Width = GraphW, Height = GraphH, ClipToBounds = true };
                    canvas.Children.Add(row.Line);
                    var frame = new Border
                    {
                        BorderBrush = LineBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2),
                        Margin = new Thickness(0, 0, 8, 0), Child = canvas, VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetColumn(frame, 1);
                    grid.Children.Add(frame);

                    Grid.SetColumn(value, 2);
                }
                else if (anyGraph)
                {
                    // Compact 2-column [Tag] -> [Value] row when another row has a graph enabled.
                    grid.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width = GridLength.Auto,
                        MinWidth = isMemory ? 96 : 42,
                        SharedSizeGroup = isMemory ? "CompactMemoryValue" : "CompactScalarValue"
                    });
                    Grid.SetColumn(value, 1);
                }
                else
                {
                    // Pure 2-column tabular HUD (no graphs enabled).
                    grid.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width = new GridLength(1, GridUnitType.Star),
                        MinWidth = hasMemoryReadout ? 96 : 42,
                        SharedSizeGroup = "Value"
                    });
                    Grid.SetColumn(value, 1);
                }

                grid.Children.Add(value);

                Rows.Children.Add(grid);
                _rows.Add(row);
            }

            Refresh(pushHistory: false);
        }

        private void Refresh(bool pushHistory = true)
        {
            if (pushHistory)
            {
                foreach (var kind in _history.Keys)
                {
                    double v = Read(kind);
                    var q = _history[kind];

                    if (double.IsNaN(v))
                    {
                        // When FPS returns to "--" (no active game), reset its buffer so the next game starts cleanly.
                        if (kind == MetricKind.Fps)
                            q.Clear();
                        continue;
                    }

                    // Pre-fill empty history with the first valid telemetry reading so the graph never drops to 0 at the left edge.
                    if (q.Count == 0)
                    {
                        for (int k = 0; k < HistoryLen; k++)
                            q.Enqueue(v);
                    }
                    else
                    {
                        if (q.Count == HistoryLen) q.Dequeue();
                        q.Enqueue(v);
                    }
                }
                ReassertTopmost();
            }

            foreach (var r in _rows)
            {
                double v = Read(r.Setting.Kind);
                var (num, unit) = Format(r.Setting.Kind, v);
                r.Number.Text = num;
                r.Unit.Text = unit;

                Brush activeTone = Tone(r.Setting, v);
                r.Number.Foreground = double.IsNaN(v) ? DimBrush : activeTone;
                if (r.Line != null)
                {
                    r.Line.Stroke = double.IsNaN(v) ? r.Setting.ColorBrush : activeTone;
                    DrawLine(r);
                }
            }
        }

        private double Read(MetricKind kind) => kind switch
        {
            MetricKind.Fps => _hw.Fps,
            MetricKind.CpuTemp => _hw.CpuTemp,
            MetricKind.CpuLoad => _hw.CpuLoad,
            MetricKind.GpuTemp => _hw.GpuTemp,
            MetricKind.GpuLoad => _hw.GpuLoad,
            MetricKind.GpuVram => _hw.VramPercent,
            MetricKind.Ram => _hw.RamPercent,
            _ => double.NaN
        };

        private (string num, string unit) Format(MetricKind kind, double v)
        {
            if (kind == MetricKind.GpuVram)
            {
                if (double.IsNaN(_hw.VramUsedGb) || double.IsNaN(_hw.VramTotalGb)) return ("--", " GB");
                return ($"{_hw.VramUsedGb:0.0}/{_hw.VramTotalGb:0.0}", " GB");
            }
            if (kind == MetricKind.Ram)
            {
                if (double.IsNaN(_hw.RamUsedGb) || double.IsNaN(_hw.RamTotalGb)) return ("--", " GB");
                return ($"{_hw.RamUsedGb:0.0}/{_hw.RamTotalGb:0.0}", " GB");
            }
            if (double.IsNaN(v)) return ("--", kind switch
            {
                MetricKind.Fps => string.Empty,
                MetricKind.CpuTemp or MetricKind.GpuTemp => "\u00B0C",
                _ => "%"
            });

            string n = Math.Round(v).ToString("F0");
            return kind switch
            {
                MetricKind.Fps => (n, string.Empty),
                MetricKind.CpuTemp or MetricKind.GpuTemp => (n, "\u00B0C"),
                _ => (n, "%")
            };
        }

        /// <summary>
        /// User colour by default; when EnableWarningColors is true, uses customizable WarningColor / CriticalColor and inclusive thresholds.
        /// </summary>
        private Brush Tone(MetricSetting m, double v)
        {
            if (!_settings.EnableWarningColors || double.IsNaN(v)) return m.ColorBrush;

            Brush warn = _settings.WarningColorBrush;
            Brush crit = _settings.CriticalColorBrush;

            switch (m.Kind)
            {
                case MetricKind.CpuTemp:
                case MetricKind.GpuTemp:
                    if (v >= _settings.TempCritical) return crit;
                    if (v >= _settings.TempWarning) return warn;
                    return m.ColorBrush;
                case MetricKind.CpuLoad:
                case MetricKind.GpuLoad:
                case MetricKind.GpuVram:
                case MetricKind.Ram:
                    if (v >= _settings.LoadCritical) return crit;
                    if (v >= _settings.LoadWarning) return warn;
                    return m.ColorBrush;
                case MetricKind.Fps:
                    if (v <= _settings.FpsCritical) return crit;
                    if (v <= _settings.FpsWarning) return warn;
                    return m.ColorBrush;
                default:
                    return m.ColorBrush;
            }
        }

        private void DrawLine(RowView r)
        {
            var h = _history[r.Setting.Kind];
            int n = h.Count;
            if (n == 0)
            {
                r.Line!.Points = new PointCollection();
                return;
            }

            double max = r.Setting.Kind switch
            {
                MetricKind.Fps => Math.Max(60, h.Max() * 1.1),
                MetricKind.CpuTemp or MetricKind.GpuTemp => 110,
                _ => 100
            };

            double step = GraphW / (HistoryLen - 1);
            int i = 0;
            var pts = new PointCollection(n);
            foreach (double v in h)
            {
                double x = GraphW - (n - 1 - i) * step;           // newest sample pinned to the right edge
                double y = GraphH - 1 - Math.Clamp(v / max, 0, 1) * (GraphH - 2);
                pts.Add(new Point(x, y));
                i++;
            }
            r.Line!.Points = pts;
        }

        private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }
    }
}
