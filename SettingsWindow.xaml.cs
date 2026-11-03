using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace PeekHUD
{
    public partial class SettingsWindow : Window
    {
        private const string ContactLabel = "Contact";
        private readonly AppSettings _settings;
        private DispatcherTimer? _contactCopyTimer;
        private Button? _activeSwatchButton;
        private MetricSetting? _activeColorMetric;
        private bool _editingAccentColor;

        public SettingsWindow(AppSettings settings)
        {
            InitializeComponent();
            _settings = settings;
            DataContext = settings;

            // Align the popup's right border flush with the right edge of the row's hex TextBox,
            // keeping the entire preset bar cleanly inside the row's bounding area.
            ColorPresetPopup.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            {
                DpiScale dpi = VisualTreeHelper.GetDpi(this);
                double rightShadowMarginPx = 6.0 * dpi.DpiScaleX;
                double topGapPx = 2.0 * dpi.DpiScaleY;
                double x = targetSize.Width - (popupSize.Width - rightShadowMarginPx);
                double y = targetSize.Height + topGapPx;
                return new[]
                {
                    new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.Horizontal)
                };
            };

            Deactivated += (_, _) => CloseColorPopup();
            LocationChanged += (_, _) => CloseColorPopup();
            SizeChanged += (_, _) => CloseColorPopup();
            AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => CloseColorPopup()));

            SourceInitialized += (_, _) => ApplyWindowChromeTheme(_settings.EffectiveTheme);
        }

        public void ApplyWindowChromeTheme(AppTheme theme)
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            // DWMWA_USE_IMMERSIVE_DARK_MODE (20): match Windows title bar chrome to Dark / Light theme.
            int dark = theme == AppTheme.Dark ? 1 : 0;
            NativeMethods.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        }

        /// <summary>Closing hides to the tray; the process only ends via the tray "Exit" item.</summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            CloseColorPopup();
            if (!App.Current.IsExiting)
            {
                e.Cancel = true;
                Hide();
            }
            base.OnClosing(e);
        }

        private void EditModeToggle_Click(object sender, RoutedEventArgs e)
        {
            _settings.EditMode = !_settings.EditMode;
        }

        private void MetricSwatch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not MetricSetting metric)
                return;

            if (ColorPresetPopup.IsOpen && ReferenceEquals(_activeSwatchButton, btn))
            {
                CloseColorPopup();
                return;
            }

            if (ColorPresetPopup.IsOpen)
                ColorPresetPopup.IsOpen = false;

            _activeSwatchButton = btn;
            _editingAccentColor = false;
            _activeColorMetric = metric;
            ColorPresetPopup.PlacementTarget = btn.Parent as UIElement ?? btn;
            ColorPresetPopup.IsOpen = true;
        }

        private void AccentSwatch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn)
                return;

            if (ColorPresetPopup.IsOpen && ReferenceEquals(_activeSwatchButton, btn))
            {
                CloseColorPopup();
                return;
            }

            if (ColorPresetPopup.IsOpen)
                ColorPresetPopup.IsOpen = false;

            _activeSwatchButton = btn;
            _activeColorMetric = null;
            _editingAccentColor = true;
            ColorPresetPopup.PlacementTarget = btn.Parent as UIElement ?? btn;
            ColorPresetPopup.IsOpen = true;
        }

        private void Preset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string hex })
                return;

            if (_activeColorMetric != null)
                _activeColorMetric.ColorHex = hex;
            else if (_editingAccentColor)
                _settings.AppAccentColor = hex;

            CloseColorPopup();
        }

        private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!ColorPresetPopup.IsOpen) return;

            if (e.OriginalSource is DependencyObject source)
            {
                if (ColorPresetPopup.Child != null && IsDescendantOrSelf(source, ColorPresetPopup.Child))
                    return;
                if (_activeSwatchButton != null && IsDescendantOrSelf(source, _activeSwatchButton))
                    return;
            }

            CloseColorPopup();
        }

        private void ColorPresetPopup_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CloseColorPopup();
                e.Handled = true;
            }
        }

        private void CloseColorPopup()
        {
            ColorPresetPopup.IsOpen = false;
            ColorPresetPopup.PlacementTarget = null;
            _activeSwatchButton = null;
            _activeColorMetric = null;
            _editingAccentColor = false;
        }

        private static bool IsDescendantOrSelf(DependencyObject? node, DependencyObject target)
        {
            while (node != null)
            {
                if (ReferenceEquals(node, target)) return true;
                node = node is Visual or Visual3D
                    ? VisualTreeHelper.GetParent(node)
                    : LogicalTreeHelper.GetParent(node);
            }
            return false;
        }

        private void WarningPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string hex })
                _settings.WarningColor = hex;
        }

        private void CriticalPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string hex })
                _settings.CriticalColor = hex;
        }

        private void ThresholdBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = string.IsNullOrEmpty(e.Text) || !e.Text.All(char.IsAsciiDigit);
        }

        private void ThresholdBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (!e.DataObject.GetDataPresent(DataFormats.Text) ||
                e.DataObject.GetData(DataFormats.Text) is not string text ||
                string.IsNullOrEmpty(text) ||
                !text.All(char.IsAsciiDigit))
            {
                e.CancelCommand();
            }
        }

        private void ThresholdBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb && !int.TryParse(tb.Text, out _))
                tb.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        }

        private void ResetWarningDefaults_Click(object sender, RoutedEventArgs e)
        {
            _settings.ResetWarningDefaults();
            RefreshThresholdBindings();
        }

        private void ResetFactoryDefaults_Click(object sender, RoutedEventArgs e)
        {
            _settings.ResetToFactoryDefaults();
            RefreshThresholdBindings();
            AccentColorBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            HotkeyBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();

            HotkeyStatus.Text = string.Empty;
            HotkeyStatus.Visibility = Visibility.Collapsed;

            FactoryResetStatus.Text = "Restored factory defaults and saved clean settings.json.";
            FactoryResetStatus.Visibility = Visibility.Visible;
        }

        private void Website_Click(object sender, RoutedEventArgs e)
        {
            OpenExternal("https://maximdevlab.com/");
        }

        private void BuyMeACoffee_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://buymeacoffee.com/maximd",
                    UseShellExecute = true
                });
            }
            catch { /* ignore missing default handler */ }
        }

        private void Contact_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText("maxim.devlab@gmail.com");
            }
            catch { return; }

            ContactButton.Content = "Copied to Clipboard!";

            if (_contactCopyTimer == null)
            {
                _contactCopyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _contactCopyTimer.Tick += (_, _) =>
                {
                    _contactCopyTimer.Stop();
                    ContactButton.Content = ContactLabel;
                };
            }
            else
            {
                _contactCopyTimer.Stop();
            }

            _contactCopyTimer.Start();
        }

        internal static void OpenExternal(string uri)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = uri,
                    UseShellExecute = true
                });
            }
            catch { /* ignore missing default handler */ }
        }

        private void RefreshThresholdBindings()
        {
            // Refresh any threshold TextBox that may have uncommitted/blank text at click time.
            foreach (Panel panel in ThresholdsGrid.Children.OfType<Panel>())
            {
                foreach (TextBox tb in panel.Children.OfType<TextBox>())
                    tb.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            }
        }

        private void Hotkey_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;

            // Keep plain Tab / Shift+Tab working for keyboard navigation.
            if (key == Key.Tab && (Keyboard.Modifiers & ~ModifierKeys.Shift) == ModifierKeys.None) return;

            e.Handled = true;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
                or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed or Key.None)
                return;

            ModifierKeys mods = Keyboard.Modifiers;
            if (mods == ModifierKeys.None)
            {
                SetStatus("Add at least one modifier (Ctrl, Shift, Alt or Win).", error: true);
                return;
            }

            string gesture = HotkeyService.Format(mods, key);
            if (App.Current.ApplyHotkey(gesture)) SetStatus($"Registered {gesture}.", error: false);
            else SetStatus($"{gesture} is unavailable (in use by another app). Previous hotkey kept.", error: true);
        }

        private void HotkeyReset_Click(object sender, RoutedEventArgs e)
        {
            const string def = "Ctrl+Shift+F";
            if (App.Current.ApplyHotkey(def)) SetStatus($"Registered {def}.", error: false);
            else SetStatus($"{def} is unavailable (in use by another app).", error: true);
        }

        private void SetStatus(string text, bool error)
        {
            HotkeyStatus.Text = text;
            HotkeyStatus.Visibility = Visibility.Visible;
            HotkeyStatus.SetResourceReference(
                System.Windows.Controls.TextBlock.ForegroundProperty,
                error ? "CritBrush" : "AccentBrush");
        }
    }
}
