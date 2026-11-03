using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;
using System.Windows.Input;
using System.Windows.Interop;

namespace PeekHUD
{
    /// <summary>Global hotkey via RegisterHotKey on a message-only window.</summary>
    public sealed class HotkeyService : IDisposable
    {
        private const int HotkeyId = 0xEF05;
        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

        private readonly HwndSource _source;
        private bool _registered;

        public event Action? Pressed;

        public HotkeyService()
        {
            var p = new HwndSourceParameters("PeekHUD.Hotkey")
            {
                ParentWindow = new IntPtr(-3), // HWND_MESSAGE
                Width = 0,
                Height = 0,
                WindowStyle = 0
            };
            _source = new HwndSource(p);
            _source.AddHook(Hook);
        }

        public bool Register(string gesture)
        {
            Unregister();
            if (!TryParse(gesture, out uint mods, out uint vk)) return false;
            _registered = NativeMethods.RegisterHotKey(_source.Handle, HotkeyId, mods | MOD_NOREPEAT, vk);
            return _registered;
        }

        private void Unregister()
        {
            if (!_registered) return;
            NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
        }

        private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                Pressed?.Invoke();
                handled = true;
            }
            return IntPtr.Zero;
        }

        public static bool TryParse(string gesture, out uint mods, out uint vk)
        {
            mods = 0; vk = 0;
            if (string.IsNullOrWhiteSpace(gesture)) return false;
            var parts = gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return false;

            for (int i = 0; i < parts.Length - 1; i++)
            {
                switch (parts[i].ToLowerInvariant())
                {
                    case "ctrl": case "control": mods |= MOD_CONTROL; break;
                    case "shift": mods |= MOD_SHIFT; break;
                    case "alt": mods |= MOD_ALT; break;
                    case "win": mods |= MOD_WIN; break;
                    default: return false;
                }
            }
            if (!Enum.TryParse(parts[^1], true, out Key key) || key == Key.None) return false;
            vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            return vk != 0;
        }

        public static string Format(ModifierKeys mods, Key key)
        {
            var sb = new StringBuilder();
            if (mods.HasFlag(ModifierKeys.Control)) sb.Append("Ctrl+");
            if (mods.HasFlag(ModifierKeys.Shift)) sb.Append("Shift+");
            if (mods.HasFlag(ModifierKeys.Alt)) sb.Append("Alt+");
            if (mods.HasFlag(ModifierKeys.Windows)) sb.Append("Win+");
            sb.Append(key);
            return sb.ToString();
        }

        public void Dispose()
        {
            Unregister();
            _source.RemoveHook(Hook);
            _source.Dispose();
        }
    }

    /// <summary>
    /// Run-at-logon for an elevated app. A HKCU\Run entry would be silently skipped by Windows for a
    /// requireAdministrator binary, so a Task Scheduler logon task (highest privileges) is used instead.
    /// </summary>
    public static class StartupService
    {
        private const string TaskName = "PeekHUD_AutoStart";
        private const string LegacyTaskName = "EasyFPS";
        private const string SilentArg = "--silent";

        public static bool IsEnabled()
        {
            if (Run($"/Query /TN \"{TaskName}\" /XML", out string stdOut) == 0)
            {
                // Automatically upgrade startup tasks that were registered without --silent.
                if (!stdOut.Contains(SilentArg, StringComparison.OrdinalIgnoreCase))
                    SetEnabled(true);

                return true;
            }

            // Backwards-compatible check if an old "EasyFPS" task exists
            if (Run($"/Query /TN \"{LegacyTaskName}\" /XML", out _) == 0)
            {
                // Migrate legacy task to PeekHUD_AutoStart
                SetEnabled(true);
                return true;
            }

            return false;
        }

        public static bool SetEnabled(bool enable)
        {
            if (!enable)
            {
                bool deletedLegacy = Run($"/Query /TN \"{LegacyTaskName}\"", out _) != 0 || Run($"/Delete /TN \"{LegacyTaskName}\" /F", out _) == 0;
                bool deletedCurrent = Run($"/Query /TN \"{TaskName}\"", out _) != 0 || Run($"/Delete /TN \"{TaskName}\" /F", out _) == 0;
                return deletedLegacy && deletedCurrent;
            }

            // Remove any legacy task during enablement
            Run($"/Delete /TN \"{LegacyTaskName}\" /F", out _);

            string exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;
            string xml = $@"<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers>
  <Principals><Principal id=""Author""><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <StartWhenAvailable>true</StartWhenAvailable>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context=""Author""><Exec><Command>{SecurityElement.Escape(exe)}</Command><Arguments>{SilentArg}</Arguments></Exec></Actions>
</Task>";
            string tmp = Path.Combine(Path.GetTempPath(), "peekhud-task.xml");
            try
            {
                File.WriteAllText(tmp, xml, Encoding.Unicode);
                if (Run($"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F", out _) == 0)
                    return true;
            }
            catch { }
            finally
            {
                try { File.Delete(tmp); } catch { }
            }

            // Fallback CLI registration with --silent argument and highest privileges.
            return Run($"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" {SilentArg}\" /SC ONLOGON /RL HIGHEST /F", out _) == 0;
        }

        private static int Run(string args, out string stdOut)
        {
            stdOut = string.Empty;
            try
            {
                using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                })!;
                stdOut = p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit(10000);
                return p.ExitCode;
            }
            catch { return -1; }
        }
    }
}

