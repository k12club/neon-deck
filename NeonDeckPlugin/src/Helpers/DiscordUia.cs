namespace Loupedeck.NeonDeckPlugin
{
    using System;
    using System.Diagnostics;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Windows.Automation;

    /// <summary>
    /// Drives Discord's own Mute button through Windows UI Automation - no keybinds, no focus stealing.
    /// Chromium only builds its accessibility tree once somebody asks for it, so we first poke the window with
    /// WM_GETOBJECT, then look for the toggle button named "Mute" (ToggleState.On = muted).
    /// </summary>
    internal static class DiscordUia
    {
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, UInt32 msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern Boolean SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern Boolean IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern Boolean ShowWindow(IntPtr hWnd, Int32 cmd);
        [DllImport("user32.dll")] private static extern UInt32 GetWindowThreadProcessId(IntPtr hWnd, out UInt32 pid);
        [DllImport("user32.dll")] private static extern Boolean AttachThreadInput(UInt32 idAttach, UInt32 idAttachTo, Boolean attach);
        [DllImport("user32.dll")] private static extern void SwitchToThisWindow(IntPtr hWnd, Boolean altTab);
        [DllImport("kernel32.dll")] private static extern UInt32 GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern Boolean PostMessage(IntPtr hWnd, UInt32 msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern Boolean ScreenToClient(IntPtr hWnd, ref POINT pt);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public Int32 X; public Int32 Y; }

        private const UInt32 WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;

        /// <summary>
        /// Click the button by posting mouse messages straight into Discord's window queue. Unlike a UIA Invoke this
        /// never activates the window, so the user stays where they are. Returns false when the button has no
        /// on-screen rectangle (window minimized) and the caller should fall back.
        /// </summary>
        private static Boolean PostClick(IntPtr hwnd, AutomationElement button)
        {
            var r = button.Current.BoundingRectangle;
            if (r.IsEmpty || r.Width < 2 || r.Height < 2 || Double.IsInfinity(r.X))
            {
                return false;
            }
            var pt = new POINT { X = (Int32)(r.X + r.Width / 2), Y = (Int32)(r.Y + r.Height / 2) };
            if (!ScreenToClient(hwnd, ref pt) || pt.X < 0 || pt.Y < 0)
            {
                return false;
            }
            var lParam = new IntPtr((pt.Y << 16) | (pt.X & 0xFFFF));
            PostMessage(hwnd, WM_MOUSEMOVE, IntPtr.Zero, lParam);
            System.Threading.Thread.Sleep(30);
            PostMessage(hwnd, WM_LBUTTONDOWN, new IntPtr(1), lParam);
            System.Threading.Thread.Sleep(60);
            PostMessage(hwnd, WM_LBUTTONUP, IntPtr.Zero, lParam);
            return true;
        }

        private const Int32 SW_MINIMIZE = 6;

        /// <summary>Give the foreground back to the window the user was in. Chromium activates Discord when we press its button.</summary>
        private static void RestoreForeground(IntPtr prev)
        {
            if (prev == IntPtr.Zero || GetForegroundWindow() == prev)
            {
                return;
            }
            for (var attempt = 0; attempt < 3 && GetForegroundWindow() != prev; attempt++)
            {
                switch (attempt)
                {
                    case 0:
                        SetForegroundWindow(prev);
                        break;
                    case 1:
                        // attach to the thread that currently owns the foreground so Windows lets us hand it over
                        var fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
                        var me = GetCurrentThreadId();
                        AttachThreadInput(me, fgThread, true);
                        SetForegroundWindow(prev);
                        AttachThreadInput(me, fgThread, false);
                        break;
                    default:
                        SwitchToThisWindow(prev, true);
                        break;
                }
                System.Threading.Thread.Sleep(60);
            }
        }

        private const UInt32 WM_GETOBJECT = 0x003D;
        private static readonly IntPtr OBJID_CLIENT = new IntPtr(-4);

        // Discord localises the button name; match the ones we know and fall back to anything containing "mute".
        private static readonly String[] MuteNames = { "Mute", "Unmute", "ปิดเสียง", "เปิดเสียง", "Stumm", "Muet", "Silenciar" };

        public sealed class MuteButton
        {
            public AutomationElement Element;
            public TogglePattern Toggle;
            public Boolean IsMuted => this.Toggle.Current.ToggleState == ToggleState.On;
        }

        public static IntPtr DiscordWindow()
        {
            try
            {
                return Process.GetProcessesByName("Discord").Select(p => p.MainWindowHandle).FirstOrDefault(h => h != IntPtr.Zero);
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        public static Boolean IsRunning()
        {
            try
            {
                return Process.GetProcessesByName("Discord").Length > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Find Discord's Mute toggle. Null when Discord has no window (tray) or the button is not exposed.</summary>
        public static MuteButton FindMuteButton()
        {
            var hwnd = DiscordWindow();
            if (hwnd == IntPtr.Zero)
            {
                return null;
            }
            try
            {
                SendMessage(hwnd, WM_GETOBJECT, IntPtr.Zero, OBJID_CLIENT); // wake Chromium's accessibility tree
                var root = AutomationElement.FromHandle(hwnd);
                var buttons = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                AutomationElement best = null;
                foreach (AutomationElement b in buttons)
                {
                    var name = b.Current.Name ?? "";
                    if (MuteNames.Any(n => String.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        best = b;
                        break;
                    }
                    if (best == null && name.IndexOf("mute", StringComparison.OrdinalIgnoreCase) >= 0 && name.IndexOf("deafen", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        best = b;
                    }
                }
                if (best == null)
                {
                    return null;
                }
                if (!best.TryGetCurrentPattern(TogglePattern.Pattern, out var pattern))
                {
                    return null;
                }
                return new MuteButton { Element = best, Toggle = (TogglePattern)pattern };
            }
            catch (Exception ex)
            {
                PluginLog.Warning($"discord uia: {ex.Message}");
                return null;
            }
        }

        /// <summary>Current Discord mute state, or null when unknown.</summary>
        public static Boolean? IsMuted()
        {
            var btn = FindMuteButton();
            return btn?.IsMuted;
        }

        /// <summary>Press Discord's Mute button. Returns the state read back afterwards (null = could not).</summary>
        public static Boolean? ToggleMute()
        {
            var btn = FindMuteButton();
            if (btn == null)
            {
                return null;
            }
            var hwnd = DiscordWindow();
            var prevForeground = GetForegroundWindow();
            var wasMinimized = hwnd != IntPtr.Zero && IsIconic(hwnd);
            var before = btn.IsMuted;

            // Preferred: a posted click never activates Discord, so no window flash at all.
            var method = "click";
            if (wasMinimized || !PostClick(hwnd, btn.Element))
            {
                // Fallback (window minimized / no rectangle): UIA toggle, then hand the foreground back.
                method = "uia";
                btn.Toggle.Toggle();
            }
            System.Threading.Thread.Sleep(300);
            var after = FindMuteButton()?.IsMuted;
            if (after == before && method == "click")
            {
                // click did not land (layout changed?) - try the UIA route once
                method = "uia-retry";
                btn.Toggle.Toggle();
                System.Threading.Thread.Sleep(300);
                after = FindMuteButton()?.IsMuted;
            }
            if (method != "click")
            {
                if (prevForeground != hwnd)
                {
                    RestoreForeground(prevForeground);
                }
                if (wasMinimized && hwnd != IntPtr.Zero && !IsIconic(hwnd))
                {
                    ShowWindow(hwnd, SW_MINIMIZE);
                }
            }
            PluginLog.Info($"discord {method}: mute {before} -> {after}; foreground unchanged={GetForegroundWindow() == prevForeground}");
            return after;
        }
    }
}
