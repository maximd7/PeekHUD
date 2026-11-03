using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using LibreHardwareMonitor.Hardware;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace PeekHUD
{
    /// <summary>
    /// Polls CPU / GPU / RAM sensors once per second. Hardware I/O runs on a worker thread; results are
    /// published on the UI dispatcher. Unsupported sensors read as <see cref="double.NaN"/>.
    /// </summary>
    public sealed class HardwareMonitorService : INotifyPropertyChanged, IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _timer;
        private readonly FpsMonitor? _fps;
        private readonly HashSet<string> _loggedFailures = new(StringComparer.OrdinalIgnoreCase);
        private Computer? _computer;
        private bool _opened, _polling, _disposed;

        private double _cpuTemp = double.NaN, _cpuLoad = double.NaN, _gpuTemp = double.NaN, _gpuLoad = double.NaN;
        private double _vramUsed = double.NaN, _vramTotal = double.NaN, _vramPercent = double.NaN;
        private double _ramUsed = double.NaN, _ramTotal = double.NaN, _ramPercent = double.NaN, _fpsValue = double.NaN;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Raised on the UI thread after each completed poll.</summary>
        public event EventHandler? Sampled;

        public double CpuTemp { get => _cpuTemp; private set => Set(ref _cpuTemp, value); }
        public double CpuLoad { get => _cpuLoad; private set => Set(ref _cpuLoad, value); }
        public double GpuTemp { get => _gpuTemp; private set => Set(ref _gpuTemp, value); }
        public double GpuLoad { get => _gpuLoad; private set => Set(ref _gpuLoad, value); }
        public double VramUsedGb { get => _vramUsed; private set => Set(ref _vramUsed, value); }
        public double VramTotalGb { get => _vramTotal; private set => Set(ref _vramTotal, value); }
        public double VramPercent { get => _vramPercent; private set => Set(ref _vramPercent, value); }
        public double RamUsedGb { get => _ramUsed; private set => Set(ref _ramUsed, value); }
        public double RamTotalGb { get => _ramTotal; private set => Set(ref _ramTotal, value); }
        public double RamPercent { get => _ramPercent; private set => Set(ref _ramPercent, value); }
        public double Fps { get => _fpsValue; private set => Set(ref _fpsValue, value); }

        public HardwareMonitorService(FpsMonitor? fps = null)
        {
            _fps = fps;
            _dispatcher = Dispatcher.CurrentDispatcher;
            _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(1000)
            };
            _timer.Tick += async (_, _) => await PollAsync();
        }

        public void Start()
        {
            _timer.Start();
            _ = PollAsync();
        }

        private async Task PollAsync()
        {
            if (_polling || _disposed) return;
            _polling = true;
            try
            {
                var snap = await Task.Run(ReadSensors);
                if (_disposed) return;

                CpuTemp = snap.CpuTemp; CpuLoad = snap.CpuLoad;
                GpuTemp = snap.GpuTemp; GpuLoad = snap.GpuLoad;
                VramUsedGb = snap.VramUsed; VramTotalGb = snap.VramTotal; VramPercent = snap.VramPercent;
                RamUsedGb = snap.RamUsed; RamTotalGb = snap.RamTotal; RamPercent = snap.RamPercent;
                Fps = snap.Fps;
                Sampled?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                // A failed poll must never take the app down; log once and retry on next tick.
                LogOnce("PollAsync", ex);
            }
            finally
            {
                _polling = false;
            }
        }

        private readonly record struct Snapshot(
            double CpuTemp, double CpuLoad, double GpuTemp, double GpuLoad,
            double VramUsed, double VramTotal, double VramPercent,
            double RamUsed, double RamTotal, double RamPercent, double Fps);

        private Snapshot ReadSensors()
        {
            EnsureOpen();

            double cpuT = double.NaN, cpuL = double.NaN, gpuT = double.NaN, gpuL = double.NaN;
            double vramU = double.NaN, vramT = double.NaN, vramP = double.NaN;
            double ramU = double.NaN, ramA = double.NaN, ramP = double.NaN;

            if (_computer != null)
            {
                IReadOnlyList<IHardware> hardwareList = Array.Empty<IHardware>();
                try
                {
                    hardwareList = _computer.Hardware.ToList();
                }
                catch (Exception ex)
                {
                    LogOnce("Hardware.Enumerate", ex);
                }

                foreach (var hw in hardwareList)
                    UpdateRecursive(hw);

                // CPU sensors (isolated so a CPU driver fault never blocks GPU / RAM / FPS).
                try
                {
                    var cpu = hardwareList.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
                    if (cpu != null)
                    {
                        var s = CollectSensorsSafe(cpu);
                        cpuT = Pick(s, SensorType.Temperature, "CPU Package", "Core (Tctl/Tdie)", "Core (Tdie)", "Tctl", "Package",
                                    "Core Max", "Core Average");
                        cpuL = Pick(s, SensorType.Load, "CPU Total");
                    }
                }
                catch (Exception ex)
                {
                    LogOnce("ReadSensors.CPU", ex);
                }

                // Target the discrete NVIDIA GPU explicitly. GpuAmd also covers Ryzen iGPUs, which on many
                // systems enumerate first and idle at 0%, so they are only a fallback.
                try
                {
                    var gpu = hardwareList.FirstOrDefault(h => h.HardwareType == HardwareType.GpuNvidia)
                              ?? hardwareList.FirstOrDefault(h => h.HardwareType == HardwareType.GpuAmd)
                              ?? hardwareList.FirstOrDefault(h => h.HardwareType == HardwareType.GpuIntel);
                    if (gpu != null)
                    {
                        var s = CollectSensorsSafe(gpu);
                        gpuT = Pick(s, SensorType.Temperature, "GPU Core", "GPU Hot Spot", "GPU Temperature");
                        gpuL = ReadGpuLoad(s);
                        (vramU, vramT, vramP) = ReadGpuVram(s);
                    }
                }
                catch (Exception ex)
                {
                    LogOnce("ReadSensors.GPU", ex);
                }

                // System memory sensors.
                try
                {
                    var mem = hardwareList.FirstOrDefault(h => h.HardwareType == HardwareType.Memory);
                    if (mem != null)
                    {
                        var s = CollectSensorsSafe(mem);
                        ramU = Exact(s, SensorType.Data, "Memory Used");
                        ramA = Exact(s, SensorType.Data, "Memory Available");
                        ramP = Exact(s, SensorType.Load, "Memory");
                    }
                }
                catch (Exception ex)
                {
                    LogOnce("ReadSensors.Memory", ex);
                }
            }

            double ramTotal = (!double.IsNaN(ramU) && !double.IsNaN(ramA)) ? ramU + ramA : double.NaN;

            if (double.IsNaN(ramTotal))
            {
                // Fallback when the Memory hardware node is unavailable.
                try
                {
                    var info = GC.GetGCMemoryInfo();
                    ramTotal = info.TotalAvailableMemoryBytes / 1073741824.0;
                    if (double.IsNaN(ramU)) ramU = info.MemoryLoadBytes / 1073741824.0;
                }
                catch (Exception ex)
                {
                    LogOnce("ReadSensors.GCMemoryFallback", ex);
                }
            }
            if (double.IsNaN(ramP) && !double.IsNaN(ramU) && ramTotal > 0) ramP = ramU / ramTotal * 100.0;

            double fpsVal = double.NaN;
            try
            {
                fpsVal = _fps?.Sample() ?? double.NaN;
            }
            catch (Exception ex)
            {
                LogOnce("ReadSensors.FPS", ex);
            }

            return new Snapshot(cpuT, cpuL, gpuT, gpuL, vramU, vramT, vramP, ramU, ramTotal, ramP, fpsVal);
        }

        private void EnsureOpen()
        {
            if (_opened) return;
            _opened = true;

            var computer = new Computer
            {
                // Keep Storage, Motherboard, Controller, and Network disabled so unused device drivers
                // cannot throw DeviceIOException / UnauthorizedAccessException during enumeration.
                IsCpuEnabled = false,
                IsGpuEnabled = false,
                IsMemoryEnabled = false,
                IsMotherboardEnabled = false,
                IsControllerEnabled = false,
                IsNetworkEnabled = false,
                IsStorageEnabled = false
            };

            bool opened = false;
            try
            {
                computer.Open();
                opened = true;
            }
            catch (Exception ex)
            {
                LogOnce("Computer.Open", ex);
            }

            if (opened)
            {
                // Enable each required sensor group independently so a failure in one group (e.g. blocked
                // kernel ring0 driver or unsupported GPU adapter) never prevents the other groups from working.
                TryEnableGroup("CPU", () => computer.IsCpuEnabled = true);
                TryEnableGroup("GPU", () => computer.IsGpuEnabled = true);
                TryEnableGroup("Memory", () => computer.IsMemoryEnabled = true);
                _computer = computer;
                return;
            }

            // Fallback if base Computer.Open() threw before marking the instance open.
            try
            {
                var fallback = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMemoryEnabled = true,
                    IsMotherboardEnabled = false,
                    IsControllerEnabled = false,
                    IsNetworkEnabled = false,
                    IsStorageEnabled = false
                };
                fallback.Open();
                _computer = fallback;
            }
            catch (Exception ex)
            {
                LogOnce("Computer.OpenFallback", ex);
                _computer = null; // degrades cleanly to NaN ("--" in the HUD) while RAM fallback + FPS continue
            }
        }

        private void TryEnableGroup(string groupName, Action enableAction)
        {
            try
            {
                enableAction();
            }
            catch (Exception ex)
            {
                LogOnce($"EnableGroup.{groupName}", ex);
            }
        }

        private void UpdateRecursive(IHardware hw)
        {
            try
            {
                hw.Update();
            }
            catch (Exception ex)
            {
                LogOnce($"Update.{hw.HardwareType}.{hw.Name}", ex);
            }

            IHardware[] subs;
            try
            {
                subs = hw.SubHardware;
            }
            catch (Exception ex)
            {
                LogOnce($"SubHardware.{hw.HardwareType}.{hw.Name}", ex);
                return;
            }

            foreach (var sub in subs)
                UpdateRecursive(sub);
        }

        private List<ISensor> CollectSensorsSafe(IHardware hw)
        {
            var result = new List<ISensor>();
            AppendSensorsSafe(hw, result);
            return result;
        }

        private void AppendSensorsSafe(IHardware hw, List<ISensor> target)
        {
            try
            {
                var sensors = hw.Sensors;
                if (sensors != null)
                {
                    foreach (var s in sensors)
                    {
                        if (s != null) target.Add(s);
                    }
                }
            }
            catch (Exception ex)
            {
                LogOnce($"Sensors.{hw.HardwareType}.{hw.Name}", ex);
            }

            try
            {
                var subs = hw.SubHardware;
                if (subs != null)
                {
                    foreach (var sub in subs)
                    {
                        if (sub != null) AppendSensorsSafe(sub, target);
                    }
                }
            }
            catch (Exception ex)
            {
                LogOnce($"SensorsSub.{hw.HardwareType}.{hw.Name}", ex);
            }
        }

        private void LogOnce(string key, Exception ex)
        {
            bool shouldLog;
            lock (_loggedFailures)
            {
                shouldLog = _loggedFailures.Add(key);
            }
            if (shouldLog)
                App.LogNonFatal($"HardwareMonitorService.{key}", ex);
        }

        private static double Pick(List<ISensor> sensors, SensorType type, params string[] preferred)
        {
            var ofType = sensors.Where(s => s.SensorType == type && s.Value.HasValue).ToList();
            foreach (var name in preferred)
            {
                var hit = ofType.FirstOrDefault(s => s.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit.Value!.Value;
            }
            // Temperatures: any value beats none. Loads: only accept the named ones.
            return type == SensorType.Temperature && ofType.Count > 0 ? ofType[0].Value!.Value : double.NaN;
        }

        /// <summary>
        /// GPU load: exact "GPU Core", then "D3D 3D", then "GPU Video Engine". If "GPU Core" reports 0 while
        /// the D3D 3D engine is busy, the 3D engine value is used. Returns NaN ("--" in the HUD) if no
        /// load sensor has a value.
        /// </summary>
        private static double ReadGpuLoad(List<ISensor> sensors)
        {
            double? Get(string name) => sensors
                .FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name == name)?.Value;

            double? core = Get("GPU Core");
            double? d3d = Get("D3D 3D");
            double? video = Get("GPU Video Engine");

            double? v = core ?? d3d ?? video;
            if (v == 0 && d3d > 0) v = d3d;
            return v.HasValue ? Math.Round(v.Value) : double.NaN;
        }

        /// <summary>
        /// Reads GPU VRAM from SensorType.SmallData (reported in MB by LibreHardwareMonitor) and converts to GB.
        /// </summary>
        private static (double usedGb, double totalGb, double percent) ReadGpuVram(List<ISensor> sensors)
        {
            double SmallMb(params string[] names)
            {
                foreach (var name in names)
                {
                    var hit = sensors.FirstOrDefault(s =>
                        s.SensorType == SensorType.SmallData
                        && s.Value.HasValue
                        && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (hit != null) return hit.Value!.Value;
                }
                return double.NaN;
            }

            double usedMb = SmallMb("GPU Memory Used", "D3D Dedicated Memory Used", "Memory Used");
            double totalMb = SmallMb("GPU Memory Total", "D3D Dedicated Memory Total", "Memory Total");
            if (double.IsNaN(totalMb))
            {
                double freeMb = SmallMb("GPU Memory Free", "D3D Dedicated Memory Free", "Memory Free");
                if (!double.IsNaN(usedMb) && !double.IsNaN(freeMb)) totalMb = usedMb + freeMb;
            }

            double usedGb = !double.IsNaN(usedMb) ? usedMb / 1024.0 : double.NaN;
            double totalGb = !double.IsNaN(totalMb) ? totalMb / 1024.0 : double.NaN;
            double pct = (!double.IsNaN(usedGb) && !double.IsNaN(totalGb) && totalGb > 0)
                ? (usedGb / totalGb * 100.0)
                : Exact(sensors, SensorType.Load, "GPU Memory");

            return (usedGb, totalGb, pct);
        }

        private static double Exact(List<ISensor> sensors, SensorType type, string name)
        {
            var hit = sensors.FirstOrDefault(s => s.SensorType == type && s.Name == name && s.Value.HasValue);
            return hit?.Value ?? double.NaN;
        }

        private void Set(ref double field, double value, [CallerMemberName] string? name = null)
        {
            // NaN != NaN, so compare bitwise-ish.
            if (field.Equals(value)) return;
            field = value;
            RaiseChanged(name!);
        }

        private void RaiseChanged(string name)
        {
            var args = new PropertyChangedEventArgs(name);
            if (_dispatcher.CheckAccess()) PropertyChanged?.Invoke(this, args);
            else _dispatcher.BeginInvoke(() => PropertyChanged?.Invoke(this, args));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
            try { _computer?.Close(); } catch { }
        }
    }

    /// <summary>
    /// High-precision FPS monitor using a sliding ring buffer of DXGI ETW QPC Present timestamps,
    /// multi-tick rolling smoothing, a 2.5s timeout grace period, and foreground focus-jitter filtering.
    /// </summary>
    public sealed class FpsMonitor : IDisposable
    {
        private static readonly Guid DxgiProvider = new("ca11c036-0102-4a2d-a6ad-f03cfed5d3c9");
        private const string SessionName = "PeekHUD-Present";

        // Microsoft-Windows-DXGI event IDs (version 0 binary schema).
        private const int PresentStartId = 42;
        private const int PresentStopId = 43;
        private const uint DXGI_PRESENT_TEST = 0x00000001;

        private const double SlidingWindowMs = 2000.0;        // 2.0s sliding window of frame timestamps
        private const int MaxTimestampCapacity = 1024;        // Ring-buffer cap (> 500 FPS across 2s)
        private const int RollingSampleTicks = 3;             // Smooth FPS across the last 3 sample ticks
        private const int MinPlausibleFps = 15;               // < 15 FPS => cursor blink / idle UI redraw, show "--"
        private const double MinFrameTimeSec = 0.001;         // < 1ms (> 1000 FPS) => DWM sync / duplicate swap event
        private const double TimeoutGraceSec = 2.500;         // Hold last valid FPS for 2.5s before dropping to "--"
        private const double DeadChainPurgeSec = 5.000;       // Purge inactive swap-chain buffers after 5.0s
        private const double CandidateSwitchDebounceMs = 350; // Ignore sub-350ms popup steals from unknown helpers

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint STILL_ACTIVE = 259;
        private const int ERROR_ACCESS_DENIED = 5;

        private enum PidKind : uint
        {
            Trackable = 0,
            IgnoredApp = 1,
            SystemOrOverlay = 2
        }

        // System shell, taskbar, tray, and focus-staging window classes that must not reset the tracked game PID.
        private static readonly HashSet<string> SystemOverlayWindowClasses = new(StringComparer.OrdinalIgnoreCase)
        {
            "Shell_TrayWnd", "SecondaryTrayWnd", "NotifyIconOverflowWindow",
            "TopLevelWindowForOverflowXamlIsland", "XamlExplorerHostIslandWindow",
            "Windows.UI.Input.InputSite.WindowClass",
            "Progman", "WorkerW", "TaskSwitcherWnd", "TaskSwitcherOverlayWnd",
            "MultitaskingViewFrame", "ForegroundStaging", "Ghost", "tooltips_class32", "#32768"
        };

        // System hosts and in-game overlays (Game Bar, Steam, NVIDIA, AMD, RTSS, PeekHUD) that transiently take focus.
        private static readonly HashSet<string> TransientOverlayProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            "peekhud", "easyfps", "shellexperiencehost", "searchhost", "startmenuexperiencehost",
            "textinputhost", "lockapp", "dwm", "sihost", "ctfmon", "widgets",
            "gamebar", "gamebarftserver", "gamebarpresencewriter", "gameoverlayui",
            "nvidia share", "nvcontainer", "nvsphelper64", "amdow", "amdrsserv",
            "radeonsoftware", "rtss", "presentmon"
        };

        // Terminal, shell, IDE, browser, and desktop utility processes never acquired as games.
        private static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            "windowsterminal", "openconsole", "conhost", "cmd", "powershell", "pwsh",
            "explorer", "devenv", "taskmgr", "peekhud", "easyfps",
            "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc",
            "discord", "spotify", "code", "notepad", "steamwebhelper",
            "applicationframehost", "shellexperiencehost", "searchhost",
            "startmenuexperiencehost", "systemsettings", "lockapp", "textinputhost"
        };

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        private readonly record struct InFlightPresent(ulong SwapChain, double TimestampMs, bool IsTest);

        private sealed class SwapChainBuffer
        {
            public double LastTimestampMs = -1;
            public double LastWallMs = -1;
            public readonly Queue<double> FrameTimestampsMs = new(128);
        }

        private readonly object _pidSync = new();
        private readonly object _sync = new();
        private readonly Dictionary<ulong, SwapChainBuffer> _chains = new();
        private readonly Queue<double> _recentTickSamples = new(RollingSampleTicks);
        // Accessed exclusively on the single ETW consumer thread.
        private readonly Dictionary<int, InFlightPresent> _inFlight = new();
        private readonly byte[] _rawBuf = new byte[16];
        private readonly Stopwatch _wallClock = Stopwatch.StartNew();

        private TraceEventSession? _session;
        private Thread? _thread;
        private int _targetPid;
        private IntPtr _targetHwnd;
        private long _pidClassificationCache; // high 32 bits: raw PID, low 32 bits: PidKind
        private long _lastPidPollTicks;
        private double _unfocusedSinceWallMs = -1;
        private int _pendingCandidatePid;
        private double _pendingCandidateSinceWallMs = -1;
        private double _lastPresentWallMs = -1;
        private double _lastSampledWallMs = -1;
        private double _lastValidFps = double.NaN;
        private double _lastValidWallMs = -1;
        private volatile bool _disposed;

        public bool IsAvailable { get; private set; }

        public void Start()
        {
            try
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "PeekHUD-ETW" };
                _thread.Start();
            }
            catch (Exception ex)
            {
                IsAvailable = false;
                App.LogNonFatal("FpsMonitor.Start", ex);
            }
        }

        private void Run()
        {
            try
            {
                try { TraceEventSession.GetActiveSession(SessionName)?.Stop(); } catch { }

                _session = new TraceEventSession(SessionName)
                {
                    StopOnDispose = true,
                    BufferQuantumKB = 16
                };
                _session.EnableProvider(DxgiProvider, TraceEventLevel.Informational);
                _session.Source.Dynamic.All += OnEvent;
                IsAvailable = true;
                _session.Source.Process(); // blocks until disposed
            }
            catch (Exception ex)
            {
                IsAvailable = false; // FPS will read "--"
                if (!_disposed)
                    App.LogNonFatal("FpsMonitor.ETW", ex);
            }
        }

        private void OnEvent(TraceEvent e)
        {
            if (_disposed) return;

            try
            {
                long wallTicks = Stopwatch.GetTimestamp();
                double eventMs = e.TimeStampRelativeMSec;

                // Periodically refresh the foreground PID (~20 Hz max) so newly focused games start buffering immediately.
                if (wallTicks - _lastPidPollTicks > Stopwatch.Frequency / 20)
                {
                    _lastPidPollTicks = wallTicks;
                    UpdateTargetPid();
                }

                int targetPid = Volatile.Read(ref _targetPid);
                if (targetPid == 0 || e.ProcessID != targetPid) return;

                int id = (int)e.ID;
                if (id == PresentStartId)
                {
                    // Microsoft-Windows-DXGI strips XML templates in OS manifests, so PayloadByName returns null.
                    // Event 42 binary layout: [0..ptrSize) = pIDXGISwapChain, [ptrSize..ptrSize+4) = Flags.
                    ulong swapChain = 0;
                    uint flags = 0;
                    int ptrSize = e.PointerSize == 4 ? 4 : 8;
                    if (e.EventDataLength >= ptrSize + 4)
                    {
                        e.EventData(_rawBuf, 0, 0, ptrSize + 4);
                        swapChain = ptrSize == 8
                            ? BitConverter.ToUInt64(_rawBuf, 0)
                            : BitConverter.ToUInt32(_rawBuf, 0);
                        flags = BitConverter.ToUInt32(_rawBuf, ptrSize);
                    }

                    if (_inFlight.Count > 512) _inFlight.Clear();
                    _inFlight[e.ThreadID] = new InFlightPresent(swapChain, eventMs, (flags & DXGI_PRESENT_TEST) != 0);
                }
                else if (id == PresentStopId)
                {
                    if (!_inFlight.Remove(e.ThreadID, out var start) || start.IsTest) return;

                    // Event 43 binary layout: [0..4) = HRESULT Result (0 == S_OK; skip DXGI_STATUS_OCCLUDED / errors).
                    if (e.EventDataLength >= 4)
                    {
                        e.EventData(_rawBuf, 0, 0, 4);
                        if (BitConverter.ToInt32(_rawBuf, 0) != 0) return;
                    }

                    RecordValidPresent(start.SwapChain, start.TimestampMs);
                }
            }
            catch
            {
                // Malformed or truncated ETW event payloads must never kill the consumer thread.
            }
        }

        private void RecordValidPresent(ulong swapChain, double currentTimestampMs)
        {
            double nowWallMs = _wallClock.Elapsed.TotalMilliseconds;

            lock (_sync)
            {
                if (!_chains.TryGetValue(swapChain, out var chain))
                {
                    if (_chains.Count >= 16) _chains.Clear();
                    chain = new SwapChainBuffer
                    {
                        LastTimestampMs = currentTimestampMs,
                        LastWallMs = nowWallMs
                    };
                    chain.FrameTimestampsMs.Enqueue(currentTimestampMs);
                    _chains[swapChain] = chain;
                    Volatile.Write(ref _lastPresentWallMs, nowWallMs);
                    return;
                }

                if (chain.LastTimestampMs < 0)
                {
                    chain.LastTimestampMs = currentTimestampMs;
                    chain.LastWallMs = nowWallMs;
                    chain.FrameTimestampsMs.Clear();
                    chain.FrameTimestampsMs.Enqueue(currentTimestampMs);
                    Volatile.Write(ref _lastPresentWallMs, nowWallMs);
                    return;
                }

                double frameTimeSec = (currentTimestampMs - chain.LastTimestampMs) * 0.001;

                // Out-of-order or sub-1ms (> 1000 FPS) DWM / duplicate composite swap event -> discard
                // without advancing LastTimestampMs so the next real frame measures from the last real frame.
                if (frameTimeSec < MinFrameTimeSec) return;

                // Only reset the ring buffer if the gap exceeds the full 2.5s timeout grace period
                // (e.g. resuming from a long pause), so brief shader-compile stutters never wipe samples.
                if (frameTimeSec > TimeoutGraceSec)
                {
                    chain.FrameTimestampsMs.Clear();
                    chain.FrameTimestampsMs.Enqueue(currentTimestampMs);
                    chain.LastTimestampMs = currentTimestampMs;
                    chain.LastWallMs = nowWallMs;
                    Volatile.Write(ref _lastPresentWallMs, nowWallMs);
                    return;
                }

                // Prune timestamps older than the 2.0s sliding window.
                while (chain.FrameTimestampsMs.Count > 0 &&
                       (currentTimestampMs - chain.FrameTimestampsMs.Peek()) > SlidingWindowMs)
                {
                    chain.FrameTimestampsMs.Dequeue();
                }

                if (chain.FrameTimestampsMs.Count >= MaxTimestampCapacity)
                {
                    chain.FrameTimestampsMs.Dequeue();
                }

                chain.FrameTimestampsMs.Enqueue(currentTimestampMs);
                chain.LastTimestampMs = currentTimestampMs;
                chain.LastWallMs = nowWallMs;
                Volatile.Write(ref _lastPresentWallMs, nowWallMs);
            }
        }

        private int UpdateTargetPid()
        {
            lock (_pidSync)
            {
                double nowMs = _wallClock.Elapsed.TotalMilliseconds;
                int currentPid = Volatile.Read(ref _targetPid);

                // If the tracked game process has exited, drop tracking immediately.
                if (currentPid != 0 && !IsProcessAlive(currentPid))
                {
                    SetTargetPid(0, IntPtr.Zero);
                    currentPid = 0;
                }

                IntPtr fg = NativeMethods.GetForegroundWindow();
                if (fg == IntPtr.Zero)
                {
                    // Transient NULL foreground window during Win32 activation / fullscreen transitions.
                    return HoldOrExpireTrackedPid(currentPid, nowMs, allowWhilePresenting: true);
                }

                NativeMethods.GetWindowThreadProcessId(fg, out uint rawPid);

                // Tracked game is actively in the foreground.
                if (currentPid != 0 && (int)rawPid == currentPid)
                {
                    _unfocusedSinceWallMs = -1;
                    _pendingCandidatePid = 0;
                    _pendingCandidateSinceWallMs = -1;
                    if (!IsIconic(fg)) _targetHwnd = fg;
                    return currentPid;
                }

                // Ignore temporary focus switches to taskbar, tray, volume flyout, or overlays (Shell_TrayWnd, Game Bar, etc.).
                if (rawPid == 0 || rawPid == (uint)Environment.ProcessId || IsSystemOverlayWindowClass(fg))
                {
                    return HoldOrExpireTrackedPid(currentPid, nowMs, allowWhilePresenting: true);
                }

                PidKind kind = ClassifyProcess((int)rawPid);
                if (kind == PidKind.SystemOrOverlay)
                {
                    return HoldOrExpireTrackedPid(currentPid, nowMs, allowWhilePresenting: true);
                }

                if (kind == PidKind.IgnoredApp)
                {
                    // Browser / Discord / Explorer / Terminal took focus: hold briefly across focus jitter,
                    // then release after TimeoutGraceSec (2.5s) if the user actually stayed outside the game.
                    return HoldOrExpireTrackedPid(currentPid, nowMs, allowWhilePresenting: false);
                }

                // Foreground window belongs to a trackable candidate process.
                if (IsIconic(fg))
                {
                    return HoldOrExpireTrackedPid(currentPid, nowMs, allowWhilePresenting: false);
                }

                int candidatePid = (int)rawPid;

                // Acquire immediately if no game with valid live FPS is currently active.
                double lastPresentMs = Volatile.Read(ref _lastPresentWallMs);
                double lastFps = Volatile.Read(ref _lastValidFps);
                bool currentActivelyPresenting = currentPid != 0
                    && !double.IsNaN(lastFps)
                    && lastPresentMs >= 0
                    && (nowMs - lastPresentMs) * 0.001 < TimeoutGraceSec;

                if (!currentActivelyPresenting)
                {
                    SetTargetPid(candidatePid, fg);
                    return candidatePid;
                }

                // Debounce switches away from an actively presenting game so transient helper popups never reset FPS.
                if (_pendingCandidatePid != candidatePid)
                {
                    _pendingCandidatePid = candidatePid;
                    _pendingCandidateSinceWallMs = nowMs;
                    return currentPid;
                }

                if (nowMs - _pendingCandidateSinceWallMs >= CandidateSwitchDebounceMs)
                {
                    SetTargetPid(candidatePid, fg);
                    return candidatePid;
                }

                return currentPid;
            }
        }

        private int HoldOrExpireTrackedPid(int currentPid, double nowMs, bool allowWhilePresenting)
        {
            _pendingCandidatePid = 0;
            _pendingCandidateSinceWallMs = -1;

            if (currentPid == 0) return 0;

            if (_unfocusedSinceWallMs < 0)
                _unfocusedSinceWallMs = nowMs;

            bool withinGrace = (nowMs - _unfocusedSinceWallMs) * 0.001 < TimeoutGraceSec;
            double lastPresentMs = Volatile.Read(ref _lastPresentWallMs);
            bool stillPresenting = allowWhilePresenting
                && lastPresentMs >= 0
                && (nowMs - lastPresentMs) * 0.001 < TimeoutGraceSec
                && (_targetHwnd == IntPtr.Zero || !IsIconic(_targetHwnd));

            if (withinGrace || stillPresenting)
                return currentPid;

            SetTargetPid(0, IntPtr.Zero);
            return 0;
        }

        private static bool IsSystemOverlayWindowClass(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return true;
            var sb = new StringBuilder(64);
            int len = GetClassName(hWnd, sb, sb.Capacity);
            return len > 0 && SystemOverlayWindowClasses.Contains(sb.ToString());
        }

        private PidKind ClassifyProcess(int pid)
        {
            long cached = Volatile.Read(ref _pidClassificationCache);
            if ((int)(cached >> 32) == pid)
            {
                return (PidKind)(uint)(cached & 0xFFFFFFFFL);
            }

            PidKind kind = PidKind.Trackable;
            try
            {
                using var proc = Process.GetProcessById(pid);
                string procName = proc.ProcessName;
                if (TransientOverlayProcesses.Contains(procName))
                    kind = PidKind.SystemOrOverlay;
                else if (IgnoredProcesses.Contains(procName))
                    kind = PidKind.IgnoredApp;
            }
            catch
            {
                kind = PidKind.Trackable;
            }

            Volatile.Write(ref _pidClassificationCache, ((long)pid << 32) | (uint)kind);
            return kind;
        }

        private static bool IsProcessAlive(int pid)
        {
            if (pid <= 0) return false;
            IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess == IntPtr.Zero)
            {
                // Protected anti-cheat processes may return ERROR_ACCESS_DENIED while still running.
                return Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED;
            }

            try
            {
                return GetExitCodeProcess(hProcess, out uint exitCode) && exitCode == STILL_ACTIVE;
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }

        private void SetTargetPid(int nextPid, IntPtr nextHwnd)
        {
            _targetHwnd = nextHwnd;
            _unfocusedSinceWallMs = -1;
            _pendingCandidatePid = 0;
            _pendingCandidateSinceWallMs = -1;

            int prev = Interlocked.Exchange(ref _targetPid, nextPid);
            if (prev != nextPid)
            {
                lock (_sync)
                {
                    _chains.Clear();
                    _recentTickSamples.Clear();
                    Volatile.Write(ref _lastPresentWallMs, -1);
                    _lastSampledWallMs = -1;
                    Volatile.Write(ref _lastValidFps, double.NaN);
                    _lastValidWallMs = -1;
                }
            }
        }

        /// <summary>
        /// Returns live smoothed FPS from the sliding timestamp ring buffer of the active game process,
        /// holding the previous valid FPS across single-tick ETW flush delays and only dropping to "--"
        /// after 2.5 consecutive seconds without Present events.
        /// </summary>
        public double Sample()
        {
            if (!IsAvailable || _disposed) return double.NaN;

            int pid = UpdateTargetPid();
            if (pid == 0) return double.NaN;

            // Request a non-blocking kernel ETW buffer flush without sleeping.
            try { _session?.Flush(); } catch { }

            double wallMs = _wallClock.Elapsed.TotalMilliseconds;

            lock (_sync)
            {
                SwapChainBuffer? primary = null;
                double primaryFps = 0;
                bool primaryEstablished = false;
                List<ulong>? deadKeys = null;

                foreach (var (key, chain) in _chains)
                {
                    if (chain.LastWallMs < 0 || chain.LastTimestampMs < 0) continue;

                    double silenceSec = Math.Max(0.0, (wallMs - chain.LastWallMs) * 0.001);
                    if (silenceSec >= TimeoutGraceSec)
                    {
                        if (silenceSec > DeadChainPurgeSec)
                        {
                            deadKeys ??= new List<ulong>();
                            deadKeys.Add(key);
                        }
                        continue;
                    }

                    int count = chain.FrameTimestampsMs.Count;
                    if (count < 2) continue;

                    double totalTimeSpanSeconds = (chain.LastTimestampMs - chain.FrameTimestampsMs.Peek()) * 0.001;
                    if (totalTimeSpanSeconds <= 0) continue;

                    // (count - 1) intervals across totalTimeSpanSeconds gives exact FPS across the sliding window.
                    double fps = (count - 1) / totalTimeSpanSeconds;
                    bool established = count >= 3;

                    // Prefer established swap chains (>= 3 frames) and, among those, the active gameplay chain
                    // over slower secondary UI / launcher swap chains in multi-swapchain titles.
                    if (primary == null
                        || (established && !primaryEstablished)
                        || (established == primaryEstablished && fps > primaryFps))
                    {
                        primary = chain;
                        primaryFps = fps;
                        primaryEstablished = established;
                    }
                }

                if (deadKeys != null)
                {
                    foreach (ulong k in deadKeys) _chains.Remove(k);
                }

                // Only push a new sample tick if fresh Present events arrived since the previous Sample() call.
                if (primary != null && primaryEstablished && primary.LastWallMs > _lastSampledWallMs)
                {
                    _lastSampledWallMs = primary.LastWallMs;

                    if (primaryFps >= MinPlausibleFps)
                    {
                        if (_recentTickSamples.Count >= RollingSampleTicks)
                            _recentTickSamples.Dequeue();
                        _recentTickSamples.Enqueue(primaryFps);

                        int smoothedFps = (int)Math.Round(_recentTickSamples.Average());
                        Volatile.Write(ref _lastValidFps, smoothedFps);
                        _lastValidWallMs = primary.LastWallMs;
                        return smoothedFps;
                    }
                }

                // Timeout grace period: if 0 frames arrived during a single 500ms-1000ms tick (or a brief hitch occurred)
                // while the tracked game process is still active, hold the last valid FPS for up to 2.5 seconds.
                double lastPresentMs = Volatile.Read(ref _lastPresentWallMs);
                double lastFps = Volatile.Read(ref _lastValidFps);
                bool withinPresentGrace = lastPresentMs >= 0 && (wallMs - lastPresentMs) * 0.001 < TimeoutGraceSec;
                bool withinValidGrace = _lastValidWallMs >= 0 && (wallMs - _lastValidWallMs) * 0.001 < TimeoutGraceSec;

                if (!double.IsNaN(lastFps) && withinPresentGrace && withinValidGrace)
                {
                    return lastFps;
                }

                Volatile.Write(ref _lastValidFps, double.NaN);
                _lastValidWallMs = -1;
                _recentTickSamples.Clear();
                return double.NaN;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _session?.Dispose(); } catch { }
        }
    }
}
