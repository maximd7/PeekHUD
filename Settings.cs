using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace PeekHUD
{
    public abstract class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }

        protected void Raise([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        internal void NotifyAllChanged() => Raise(string.Empty);
    }

    public enum AppTheme { Dark, Light }

    public enum MetricKind { Fps, CpuTemp, CpuLoad, GpuTemp, GpuLoad, GpuVram, Ram }

    public enum MetricMode { Number, Graph }

    public sealed class MetricSetting : Observable
    {
        private bool _enabled = true;
        private MetricMode _mode = MetricMode.Number;
        private string _colorHex = "#FFFFFF";

        public MetricKind Kind { get; set; }

        [JsonIgnore]
        public string Label => Kind switch
        {
            MetricKind.Fps => "FPS",
            MetricKind.CpuTemp => "CPU Temp",
            MetricKind.CpuLoad => "CPU Load",
            MetricKind.GpuTemp => "GPU Temp",
            MetricKind.GpuLoad => "GPU Load",
            MetricKind.GpuVram => "GPU VRAM",
            MetricKind.Ram => "RAM",
            _ => Kind.ToString()
        };

        /// <summary>Short tag rendered in the HUD.</summary>
        [JsonIgnore]
        public string HudTag => Kind switch
        {
            MetricKind.Fps => "FPS",
            MetricKind.CpuTemp => "CPU TEMP",
            MetricKind.CpuLoad => "CPU LOAD",
            MetricKind.GpuTemp => "GPU TEMP",
            MetricKind.GpuLoad => "GPU LOAD",
            MetricKind.GpuVram => "VRAM",
            MetricKind.Ram => "RAM",
            _ => Kind.ToString()
        };

        public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

        public MetricMode Mode
        {
            get => _mode;
            set
            {
                if (!Set(ref _mode, value)) return;
                Raise(nameof(IsNumber));
                Raise(nameof(IsGraph));
            }
        }

        public string ColorHex
        {
            get => _colorHex;
            set
            {
                if (!Set(ref _colorHex, value ?? string.Empty)) return;
                Raise(nameof(ColorBrush));
                Raise(nameof(IsColorValid));
            }
        }

        // Radio-button friendly projections of Mode (a "false" write is ignored on purpose).
        [JsonIgnore]
        public bool IsNumber { get => Mode == MetricMode.Number; set { if (value) Mode = MetricMode.Number; } }

        [JsonIgnore]
        public bool IsGraph { get => Mode == MetricMode.Graph; set { if (value) Mode = MetricMode.Graph; } }

        [JsonIgnore]
        public bool IsColorValid => TryParseColor(ColorHex, out _);

        [JsonIgnore]
        public Brush ColorBrush => BrushOrFallback(ColorHex, Color.FromRgb(0xFF, 0xFF, 0xFF));

        public static Brush BrushOrFallback(string? hex, Color fallback)
        {
            if (!TryParseColor(hex, out var c)) c = fallback;
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public static bool TryParseColor(string? s, out Color color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (!s.StartsWith('#')) s = "#" + s;
            if (!Regex.IsMatch(s, "^#([0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")) return false;
            color = (Color)ColorConverter.ConvertFromString(s);
            return true;
        }
    }

    public sealed class AppSettings : Observable
    {
        public const string DefaultMetricColor = "#FFFFFF";
        public const string DefaultAppAccentColor = "#00E5FF";
        public const string DefaultWarningColor = "#F59E0B";
        public const string DefaultCriticalColor = "#EF4444";
        public const int DefaultTempWarning = 80;
        public const int DefaultTempCritical = 90;
        public const int DefaultLoadWarning = 85;
        public const int DefaultLoadCritical = 95;
        public const int DefaultFpsWarning = 45;
        public const int DefaultFpsCritical = 30;
        public const string DefaultHotkey = "Ctrl+Shift+F";
        public const double DefaultScale = 1.0;
        public const double DefaultBgTransparency = 0.65;
        public const double DefaultOverlayMargin = 20.0;
        public const double DefaultOverlayWidth = 220.0;
        public const double DefaultOverlayTop = 20.0;

        public static double GetDefaultOverlayLeft()
        {
            try
            {
                double right = SystemParameters.WorkArea.Right;
                if (right > 0)
                {
                    return Math.Max(DefaultOverlayMargin, right - DefaultOverlayWidth - DefaultOverlayMargin);
                }
            }
            catch { }
            return Math.Max(DefaultOverlayMargin, 1920.0 - DefaultOverlayWidth - DefaultOverlayMargin);
        }

        public static double GetDefaultOverlayTop() => DefaultOverlayTop;

        public static double DefaultOverlayLeft => GetDefaultOverlayLeft();

        private AppTheme? _theme;
        private string _appAccentColor = DefaultAppAccentColor;
        private double _scale = DefaultScale;
        private double? _bgTransparency;
        private double? _legacyBgOpacity;
        private bool _enableWarningColors = false;
        private string _warningColor = DefaultWarningColor;
        private string _criticalColor = DefaultCriticalColor;
        private int _tempWarning = DefaultTempWarning;
        private int _tempCritical = DefaultTempCritical;
        private int _loadWarning = DefaultLoadWarning;
        private int _loadCritical = DefaultLoadCritical;
        private int _fpsWarning = DefaultFpsWarning;
        private int _fpsCritical = DefaultFpsCritical;
        private string _hotkey = DefaultHotkey;
        private bool _firstRunCompleted;
        private bool _runOnStartup = false;
        private bool _overlayVisible = true;
        private bool _editMode;
        private double _left = GetDefaultOverlayLeft(), _top = GetDefaultOverlayTop();

        private static readonly string LegacyFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EasyFPS", "settings.json");

        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PeekHUD", "settings.json");

        public static string SettingsFilePath => FilePath;

        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };

        /// <summary>Raised with the property name whenever this object or any metric changes.</summary>
        [JsonIgnore]
        public Action<string>? Changed;

        public List<MetricSetting> Metrics { get; set; } = new();

        public AppTheme? Theme
        {
            get => _theme;
            set
            {
                if (!Set(ref _theme, value)) return;
                Raise(nameof(IsDarkTheme));
                Raise(nameof(IsLightTheme));
            }
        }

        [JsonIgnore]
        public AppTheme EffectiveTheme => _theme ?? DetectWindowsTheme();

        // Segmented header radio projections: [ Dark | Light ]
        [JsonIgnore]
        public bool IsDarkTheme
        {
            get => EffectiveTheme == AppTheme.Dark;
            set { if (value) Theme = AppTheme.Dark; }
        }

        [JsonIgnore]
        public bool IsLightTheme
        {
            get => EffectiveTheme == AppTheme.Light;
            set { if (value) Theme = AppTheme.Light; }
        }

        public string AppAccentColor
        {
            get => _appAccentColor;
            set
            {
                if (!Set(ref _appAccentColor, value ?? string.Empty)) return;
                Raise(nameof(AppAccentColorBrush));
                Raise(nameof(IsAppAccentColorValid));
            }
        }

        [JsonIgnore]
        public bool IsAppAccentColorValid => MetricSetting.TryParseColor(AppAccentColor, out _);

        [JsonIgnore]
        public Brush AppAccentColorBrush => MetricSetting.BrushOrFallback(AppAccentColor, Color.FromRgb(0x00, 0xE5, 0xFF));

        public double Scale { get => _scale; set => Set(ref _scale, Math.Clamp(value, 0.8, 1.5)); }

        /// <summary>0.0 = Solid (0% transparent), 1.0 = Clear (100% transparent).</summary>
        public double BgTransparency
        {
            get => _bgTransparency ?? 0.65;
            set => Set(ref _bgTransparency, Math.Clamp(value, 0.0, 1.0));
        }

        /// <summary>Legacy migration property for older settings.json files; omitted once saved.</summary>
        public double? BgOpacity
        {
            get => null;
            set => _legacyBgOpacity = value;
        }

        public bool EnableWarningColors
        {
            get => _enableWarningColors;
            set => Set(ref _enableWarningColors, value);
        }

        public string WarningColor
        {
            get => _warningColor;
            set
            {
                if (!Set(ref _warningColor, value ?? string.Empty)) return;
                Raise(nameof(WarningColorBrush));
                Raise(nameof(IsWarningColorValid));
            }
        }

        public string CriticalColor
        {
            get => _criticalColor;
            set
            {
                if (!Set(ref _criticalColor, value ?? string.Empty)) return;
                Raise(nameof(CriticalColorBrush));
                Raise(nameof(IsCriticalColorValid));
            }
        }

        public int TempWarning
        {
            get => _tempWarning;
            set => Set(ref _tempWarning, Math.Clamp(value, 0, 150));
        }

        public int TempCritical
        {
            get => _tempCritical;
            set => Set(ref _tempCritical, Math.Clamp(value, 0, 150));
        }

        public int LoadWarning
        {
            get => _loadWarning;
            set => Set(ref _loadWarning, Math.Clamp(value, 0, 100));
        }

        public int LoadCritical
        {
            get => _loadCritical;
            set => Set(ref _loadCritical, Math.Clamp(value, 0, 100));
        }

        public int FpsWarning
        {
            get => _fpsWarning;
            set => Set(ref _fpsWarning, Math.Clamp(value, 0, 999));
        }

        public int FpsCritical
        {
            get => _fpsCritical;
            set => Set(ref _fpsCritical, Math.Clamp(value, 0, 999));
        }

        [JsonIgnore]
        public bool IsWarningColorValid => MetricSetting.TryParseColor(WarningColor, out _);

        [JsonIgnore]
        public bool IsCriticalColorValid => MetricSetting.TryParseColor(CriticalColor, out _);

        [JsonIgnore]
        public Brush WarningColorBrush => MetricSetting.BrushOrFallback(WarningColor, Color.FromRgb(0xF5, 0x9E, 0x0B));

        [JsonIgnore]
        public Brush CriticalColorBrush => MetricSetting.BrushOrFallback(CriticalColor, Color.FromRgb(0xEF, 0x44, 0x44));

        public void ResetWarningDefaults()
        {
            WarningColor = DefaultWarningColor;
            CriticalColor = DefaultCriticalColor;
            TempWarning = DefaultTempWarning;
            TempCritical = DefaultTempCritical;
            LoadWarning = DefaultLoadWarning;
            LoadCritical = DefaultLoadCritical;
            FpsWarning = DefaultFpsWarning;
            FpsCritical = DefaultFpsCritical;

            // Force UI re-read even if a field was already at its default value (e.g. partially edited box).
            Raise(nameof(WarningColor));
            Raise(nameof(WarningColorBrush));
            Raise(nameof(IsWarningColorValid));
            Raise(nameof(CriticalColor));
            Raise(nameof(CriticalColorBrush));
            Raise(nameof(IsCriticalColorValid));
            Raise(nameof(TempWarning));
            Raise(nameof(TempCritical));
            Raise(nameof(LoadWarning));
            Raise(nameof(LoadCritical));
            Raise(nameof(FpsWarning));
            Raise(nameof(FpsCritical));

            Save();
        }

        public void ResetToFactoryDefaults()
        {
            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
            catch { /* non-fatal; Save() overwrites below */ }

            foreach (var m in Metrics)
            {
                m.Enabled = true;
                m.Mode = MetricMode.Number;
                m.ColorHex = DefaultMetricColor;
                m.NotifyAllChanged();
            }

            Theme = DetectWindowsTheme();
            AppAccentColor = DefaultAppAccentColor;
            Scale = DefaultScale;
            BgTransparency = DefaultBgTransparency;
            EnableWarningColors = false;
            WarningColor = DefaultWarningColor;
            CriticalColor = DefaultCriticalColor;
            TempWarning = DefaultTempWarning;
            TempCritical = DefaultTempCritical;
            LoadWarning = DefaultLoadWarning;
            LoadCritical = DefaultLoadCritical;
            FpsWarning = DefaultFpsWarning;
            FpsCritical = DefaultFpsCritical;
            Hotkey = DefaultHotkey;
            RunOnStartup = false;
            OverlayVisible = true;
            EditMode = false;
            double defaultMargin = 20.0;
            double defaultWidth = 220.0;
            OverlayTop = defaultMargin;
            OverlayLeft = Math.Max(20.0, SystemParameters.WorkArea.Right - defaultWidth - defaultMargin);
            FirstRunCompleted = true;

            // Force UI re-read across all bound controls (including partially edited TextBoxes).
            Raise(string.Empty);
            Save();
            Changed?.Invoke("*");
        }

        public bool FirstRunCompleted { get => _firstRunCompleted; set => Set(ref _firstRunCompleted, value); }
        public string Hotkey { get => _hotkey; set => Set(ref _hotkey, value); }
        public bool RunOnStartup { get => _runOnStartup; set => Set(ref _runOnStartup, value); }
        public bool OverlayVisible { get => _overlayVisible; set => Set(ref _overlayVisible, value); }
        public double OverlayLeft { get => _left; set => Set(ref _left, value); }
        public double OverlayTop { get => _top; set => Set(ref _top, value); }

        /// <summary>Not persisted: the HUD always starts locked (click-through).</summary>
        [JsonIgnore]
        public bool EditMode { get => _editMode; set => Set(ref _editMode, value); }

        public MetricSetting Get(MetricKind kind) => Metrics.First(m => m.Kind == kind);

        public static AppTheme DetectWindowsTheme()
        {
            try
            {
                object? val = Registry.GetValue(
                    @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme",
                    0);
                if (val is int i && i == 1) return AppTheme.Light;
            }
            catch { }
            return AppTheme.Dark;
        }

        public static AppSettings Load()
        {
            AppSettings? s = null;
            try
            {
                if (File.Exists(FilePath))
                    s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json);
                else if (File.Exists(LegacyFilePath))
                    s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(LegacyFilePath), Json);
            }
            catch { /* corrupt file -> defaults */ }

            s ??= new AppSettings();
            s.Normalize();
            s.Attach();
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
            }
            catch { /* read-only profile etc. - non fatal */ }
        }

        private void Normalize()
        {
            foreach (MetricKind k in Enum.GetValues<MetricKind>())
                if (Metrics.All(m => m.Kind != k))
                    Metrics.Add(new MetricSetting { Kind = k, ColorHex = DefaultMetricColor });

            Metrics = Metrics.GroupBy(m => m.Kind).Select(g => g.First()).OrderBy(m => (int)m.Kind).ToList();

            foreach (var m in Metrics)
            {
                if (string.IsNullOrWhiteSpace(m.ColorHex) || string.Equals(m.ColorHex, "#00E5A3", StringComparison.OrdinalIgnoreCase))
                    m.ColorHex = DefaultMetricColor;
            }

            _theme ??= DetectWindowsTheme();
            if (_bgTransparency == null)
            {
                _bgTransparency = _legacyBgOpacity.HasValue
                    ? Math.Clamp(1.0 - _legacyBgOpacity.Value, 0.0, 1.0)
                    : 0.65;
            }
            else
            {
                _bgTransparency = Math.Clamp(_bgTransparency.Value, 0.0, 1.0);
            }

            if (string.IsNullOrWhiteSpace(_appAccentColor) || string.Equals(_appAccentColor, "#00E5A3", StringComparison.OrdinalIgnoreCase))
                _appAccentColor = DefaultAppAccentColor;
            if (string.IsNullOrWhiteSpace(_warningColor)) _warningColor = DefaultWarningColor;
            if (string.IsNullOrWhiteSpace(_criticalColor)) _criticalColor = DefaultCriticalColor;
            _tempWarning = Math.Clamp(_tempWarning, 0, 150);
            _tempCritical = Math.Clamp(_tempCritical, 0, 150);
            _loadWarning = Math.Clamp(_loadWarning, 0, 100);
            _loadCritical = Math.Clamp(_loadCritical, 0, 100);
            _fpsWarning = Math.Clamp(_fpsWarning, 0, 999);
            _fpsCritical = Math.Clamp(_fpsCritical, 0, 999);

            Scale = _scale;
            if (string.IsNullOrWhiteSpace(_hotkey)) _hotkey = DefaultHotkey;

            if (_left <= 0)
            {
                _top = DefaultOverlayTop;
                _left = GetDefaultOverlayLeft();
            }
        }

        private void Attach()
        {
            PropertyChanged += (_, e) => Changed?.Invoke(e.PropertyName ?? string.Empty);
            foreach (var m in Metrics)
                m.PropertyChanged += (_, e) => Changed?.Invoke(e.PropertyName ?? string.Empty);
        }
    }
}
