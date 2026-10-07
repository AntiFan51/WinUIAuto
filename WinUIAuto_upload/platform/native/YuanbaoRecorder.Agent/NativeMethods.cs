using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;
using System.Linq;
using System.Collections.Generic;

namespace YuanbaoRecorder.Agent
{
    internal static class NativeMethods
    {
        internal const int WhMouseLowLevel = 14;
        internal const int WhKeyboardLowLevel = 13;
        internal const int WmLeftButtonDown = 0x0201;
        internal const int WmLeftButtonUp = 0x0202;
        internal const int WmMouseMove = 0x0200;
        internal const int WmMouseWheel = 0x020A;
        internal const int WmKeyDown = 0x0100;
        internal const int WmKeyUp = 0x0101;
        internal const int WmSysKeyDown = 0x0104;
        internal const int WmSysKeyUp = 0x0105;
        internal const int WmRightButtonDown = 0x0204;
        internal const int WmRightButtonUp = 0x0205;
        internal const int WmHotKey = 0x0312;
        internal const int ShowNormal = 1;
        internal const int ShowMaximized = 3;
        internal const int ShowMinimized = 6;
        internal const int ShowRestore = 9;
        internal const uint SetWindowPosNoMove = 0x0002;
        internal const uint SetWindowPosNoSize = 0x0001;
        internal const uint SetWindowPosShowWindow = 0x0040;
        internal const uint SetWindowPosNoActivate = 0x0010;
        private static readonly IntPtr WindowTopMost = new IntPtr(-1);
        private static readonly IntPtr WindowNotTopMost = new IntPtr(-2);
        private static IntPtr replayPreviousForegroundWindow = IntPtr.Zero;
        private static WindowRectangle replayPreviousForegroundRectangle;
        private static bool replayPreviousForegroundWasMaximized;
        private static readonly object replayPromotedWindowsLock = new object();
        private static readonly HashSet<IntPtr> replayPromotedWindows = new HashSet<IntPtr>();
        internal const uint ModShift = 0x0004;
        internal const uint ModControl = 0x0002;
        internal const uint KeyEventKeyUp = 0x0002;
        internal const uint KeyEventUnicode = 0x0004;
        internal const uint MouseEventLeftDown = 0x0002;
        internal const uint MouseEventLeftUp = 0x0004;
        internal const uint MouseEventRightDown = 0x0008;
        internal const uint MouseEventRightUp = 0x0010;
        internal const uint MouseEventWheel = 0x0800;
        internal const byte VirtualKeyControl = 0x11;
        internal const uint VirtualKeyLeftControl = 0xA2;
        internal const uint VirtualKeyRightControl = 0xA3;
        internal const byte VirtualKeyMenu = 0x12;
        internal const byte VirtualKeyA = 0x41;
        internal const byte VirtualKeyB = 0x42;
        internal const byte VirtualKeyI = 0x49;
        internal const byte VirtualKeyT = 0x54;
        internal const byte VirtualKeyW = 0x57;

        internal delegate IntPtr LowLevelMouseProc(int code, IntPtr message, IntPtr data);
        internal delegate IntPtr LowLevelKeyboardProc(int code, IntPtr message, IntPtr data);
        internal delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point
        {
            internal int X;
            internal int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MouseHookData
        {
            internal Point Position;
            internal uint MouseData;
            internal uint Flags;
            internal uint Time;
            internal UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct KeyboardHookData
        {
            internal uint VirtualKeyCode;
            internal uint ScanCode;
            internal uint Flags;
            internal uint Time;
            internal UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WindowRectangle
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;

            internal int Width { get { return Right - Left; } }
            internal int Height { get { return Bottom - Top; } }
        }

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetWindowsHookEx(
            int hookId,
            LowLevelMouseProc callback,
            IntPtr module,
            uint threadId);

        [DllImport("user32.dll", EntryPoint = "SetWindowsHookEx", SetLastError = true)]
        internal static extern IntPtr SetKeyboardHook(
            int hookId,
            LowLevelKeyboardProc callback,
            IntPtr module,
            uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        internal static extern IntPtr CallNextHookEx(
            IntPtr hook,
            int code,
            IntPtr message,
            IntPtr data);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsZoomed(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern IntPtr SetActiveWindow(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern void SwitchToThisWindow(IntPtr window, bool altTab);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool BringWindowToTop(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AttachThreadInput(uint sourceThreadId, uint targetThreadId, bool attach);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        internal static extern IntPtr WindowFromPoint(Point point);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetAncestor(IntPtr window, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsChild(IntPtr parentWindow, IntPtr window);

        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindowAsync(IntPtr window, int command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(IntPtr window, StringBuilder text, int count);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr window, out WindowRectangle rectangle);

        [DllImport("user32.dll", EntryPoint = "SetProcessDpiAwarenessContext")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("user32.dll", EntryPoint = "SetProcessDPIAware")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessDpiAware();

        internal static void InitializeDpiAwareness()
        {
            // Mouse hooks, UIA and CopyFromScreen all use physical screen pixels.
            // Fix the process DPI mode before WinForms creates any handle so
            // GetWindowRect cannot alternate between 150%-virtualized logical
            // coordinates and physical coordinates across capture callbacks.
            try
            {
                if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; // Per-monitor aware V2
            }
            catch (EntryPointNotFoundException)
            {
            }

            try { SetProcessDpiAware(); }
            catch (EntryPointNotFoundException) { }
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterHotKey(IntPtr window, int id);

        [DllImport("user32.dll")]
        internal static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extraInfo);

        [DllImport("user32.dll")]
        internal static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint SendInput(uint inputCount, Input[] inputs, int size);

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            internal uint Type;
            internal InputUnion Union;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)]
            internal KeyboardInput Keyboard;

            [FieldOffset(0)]
            internal MouseInput Mouse;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            internal int X;
            internal int Y;
            internal uint MouseData;
            internal uint Flags;
            internal uint Time;
            internal UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            internal ushort VirtualKey;
            internal ushort ScanCode;
            internal uint Flags;
            internal uint Time;
            internal UIntPtr ExtraInfo;
        }

        internal static string ReadWindowTitle(IntPtr window)
        {
            var text = new StringBuilder(1024);
            GetWindowText(window, text, text.Capacity);
            return text.ToString();
        }

        internal static IntPtr FindVisibleWindowByTitle(string titlePart)
        {
            var result = IntPtr.Zero;
            EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                if (!IsWindowVisible(window)) return true;
                var title = ReadWindowTitle(window);
                if (title.IndexOf(titlePart, StringComparison.OrdinalIgnoreCase) < 0) return true;
                result = window;
                return false;
            }, IntPtr.Zero);
            return result;
        }

        internal static IntPtr FindLargestVisibleWindowByProcessName(string processName)
        {
            var result = IntPtr.Zero;
            var largestArea = 0L;
            var selectedHasTitle = false;
            EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                if (!IsWindowVisible(window)) return true;
                uint processId;
                GetWindowThreadProcessId(window, out processId);
                if (processId == 0) return true;
                try
                {
                    using (var process = Process.GetProcessById((int)processId))
                    {
                        if (!string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
                catch
                {
                    return true;
                }

                WindowRectangle rectangle;
                if (!GetWindowRect(window, out rectangle)) return true;
                var area = (long)Math.Max(0, rectangle.Width) * Math.Max(0, rectangle.Height);
                var hasTitle = !string.IsNullOrWhiteSpace(ReadWindowTitle(window));
                if (selectedHasTitle && !hasTitle) return true;
                if (selectedHasTitle == hasTitle && area <= largestArea) return true;
                largestArea = area;
                selectedHasTitle = hasTitle;
                result = window;
                return true;
            }, IntPtr.Zero);
            return result;
        }

        internal static List<IntPtr> FindVisibleWindowsByProcessName(string processName)
        {
            var results = new List<Tuple<IntPtr, long>>();
            EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                if (!IsWindowVisible(window) || !IsWindowOwnedByProcessName(window, processName)) return true;
                WindowRectangle rectangle;
                if (!GetWindowRect(window, out rectangle)) return true;
                var area = (long)Math.Max(0, rectangle.Width) * Math.Max(0, rectangle.Height);
                if (area > 0) results.Add(Tuple.Create(window, area));
                return true;
            }, IntPtr.Zero);
            return results.OrderByDescending(item => item.Item2).Select(item => item.Item1).ToList();
        }

        internal static uint ReadWindowProcessId(IntPtr window)
        {
            uint processId;
            GetWindowThreadProcessId(window, out processId);
            return processId;
        }

        internal static bool EnsureForegroundWindow(IntPtr window, uint expectedProcessId, int attempts)
        {
            if (window == IntPtr.Zero) return false;
            for (var attempt = 0; attempt < Math.Max(1, attempts); attempt++)
            {
                ShowWindowAsync(window, ShowMaximized);
                var foreground = GetForegroundWindow();
                uint foregroundProcessId;
                var foregroundThreadId = GetWindowThreadProcessId(foreground, out foregroundProcessId);
                uint targetProcessId;
                var targetThreadId = GetWindowThreadProcessId(window, out targetProcessId);
                var currentThreadId = GetCurrentThreadId();
                var attachedForeground = false;
                var attachedTarget = false;
                try
                {
                    if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
                    {
                        attachedForeground = AttachThreadInput(currentThreadId, foregroundThreadId, true);
                    }
                    if (targetThreadId != 0 && targetThreadId != currentThreadId)
                    {
                        attachedTarget = AttachThreadInput(currentThreadId, targetThreadId, true);
                    }
                    BringWindowToTop(window);
                    SetActiveWindow(window);
                    SetForegroundWindow(window);
                }
                finally
                {
                    if (attachedTarget) AttachThreadInput(currentThreadId, targetThreadId, false);
                    if (attachedForeground) AttachThreadInput(currentThreadId, foregroundThreadId, false);
                }
                System.Threading.Thread.Sleep(120);
                foreground = GetForegroundWindow();
                var actualProcessId = ReadWindowProcessId(foreground);
                if (foreground == window || actualProcessId == expectedProcessId) return true;
            }
            return false;
        }

        internal static bool EnsureForegroundWindowByProcessName(IntPtr window, string expectedProcessName, int attempts)
        {
            if (window == IntPtr.Zero) return false;
            for (var attempt = 0; attempt < Math.Max(1, attempts); attempt++)
            {
                RestoreWindowForActivation(window);
                var foreground = GetForegroundWindow();
                uint foregroundProcessId;
                var foregroundThreadId = GetWindowThreadProcessId(foreground, out foregroundProcessId);
                uint targetProcessId;
                var targetThreadId = GetWindowThreadProcessId(window, out targetProcessId);
                var currentThreadId = GetCurrentThreadId();
                var attachedForeground = false;
                var attachedTarget = false;
                try
                {
                    if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
                    {
                        attachedForeground = AttachThreadInput(currentThreadId, foregroundThreadId, true);
                    }
                    if (targetThreadId != 0 && targetThreadId != currentThreadId)
                    {
                        attachedTarget = AttachThreadInput(currentThreadId, targetThreadId, true);
                    }
                    SetWindowPos(window, WindowTopMost, 0, 0, 0, 0, SetWindowPosNoMove | SetWindowPosNoSize | SetWindowPosShowWindow);
                    TrackReplayPromotedWindow(window);
                    SwitchToThisWindow(window, true);
                    BringWindowToTop(window);
                    SetActiveWindow(window);
                    keybd_event(VirtualKeyMenu, 0, 0, UIntPtr.Zero);
                    SetForegroundWindow(window);
                    keybd_event(VirtualKeyMenu, 0, KeyEventKeyUp, UIntPtr.Zero);
                }
                finally
                {
                    if (attachedTarget) AttachThreadInput(currentThreadId, targetThreadId, false);
                    if (attachedForeground) AttachThreadInput(currentThreadId, foregroundThreadId, false);
                }
                System.Threading.Thread.Sleep(180);
                var acquired = IsForegroundWindowRoot(window);
                SetWindowPos(window, WindowNotTopMost, 0, 0, 0, 0, SetWindowPosNoMove | SetWindowPosNoSize | SetWindowPosShowWindow);
                if (acquired) return true;
            }
            return AcquireReplayForegroundLease(window, expectedProcessName);
        }

        private static bool AcquireReplayForegroundLease(IntPtr window, string expectedProcessName)
        {
            RestoreWindowForActivation(window);
            SetWindowPos(window, WindowTopMost, 0, 0, 0, 0, SetWindowPosNoMove | SetWindowPosNoSize | SetWindowPosShowWindow);
            TrackReplayPromotedWindow(window);
            SwitchToThisWindow(window, true);
            BringWindowToTop(window);
            SetForegroundWindow(window);
            System.Threading.Thread.Sleep(300);
            var acquired = IsForegroundWindowRoot(window);
            SetWindowPos(window, WindowNotTopMost, 0, 0, 0, 0, SetWindowPosNoMove | SetWindowPosNoSize | SetWindowPosShowWindow);
            if (!acquired) RestoreReplayForegroundWindow();
            return acquired;
        }

        internal static void ReleaseReplayWindow(IntPtr window)
        {
            var promotedWindows = new List<IntPtr>();
            lock (replayPromotedWindowsLock)
            {
                promotedWindows.AddRange(replayPromotedWindows);
                replayPromotedWindows.Clear();
            }
            if (window != IntPtr.Zero && !promotedWindows.Contains(window)) promotedWindows.Add(window);
            foreach (var promotedWindow in promotedWindows.Where(IsWindow))
            {
                SetWindowPos(promotedWindow, WindowNotTopMost, 0, 0, 0, 0, SetWindowPosNoMove | SetWindowPosNoSize | SetWindowPosShowWindow);
            }
            RestoreReplayForegroundWindow();
        }

        private static void RestoreReplayForegroundWindow()
        {
            var previous = replayPreviousForegroundWindow;
            replayPreviousForegroundWindow = IntPtr.Zero;
            if (previous == IntPtr.Zero || !IsWindow(previous)) return;
            SetWindowPos(
                previous,
                IntPtr.Zero,
                replayPreviousForegroundRectangle.Left,
                replayPreviousForegroundRectangle.Top,
                Math.Max(1, replayPreviousForegroundRectangle.Width),
                Math.Max(1, replayPreviousForegroundRectangle.Height),
                SetWindowPosNoActivate | SetWindowPosShowWindow);
            ShowWindowAsync(previous, replayPreviousForegroundWasMaximized ? ShowMaximized : ShowNormal);
            BringWindowToTop(previous);
            SetForegroundWindow(previous);
        }

        internal static bool IsWindowExposed(IntPtr window, string expectedProcessName)
        {
            WindowRectangle rectangle;
            if (!GetWindowRect(window, out rectangle) || rectangle.Width <= 0 || rectangle.Height <= 0) return false;
            var points = new[]
            {
                new Point { X = rectangle.Left + rectangle.Width / 2, Y = rectangle.Top + rectangle.Height / 2 },
                new Point { X = rectangle.Left + Math.Min(80, rectangle.Width / 4), Y = rectangle.Top + Math.Min(80, rectangle.Height / 4) },
                new Point { X = rectangle.Right - Math.Min(80, rectangle.Width / 4), Y = rectangle.Bottom - Math.Min(80, rectangle.Height / 4) }
            };
            return points.Any(point => IsPointOwnedByProcessName(point.X, point.Y, expectedProcessName));
        }

        internal static bool IsForegroundProcessName(string expectedProcessName)
        {
            var foreground = GetForegroundWindow();
            var processId = foreground == IntPtr.Zero ? 0 : ReadWindowProcessId(foreground);
            if (processId == 0) return false;
            try
            {
                using (var process = Process.GetProcessById((int)processId))
                {
                    return string.Equals(process.ProcessName, expectedProcessName, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsForegroundWindowRoot(IntPtr expectedRootWindow)
        {
            if (expectedRootWindow == IntPtr.Zero || !IsWindow(expectedRootWindow)) return false;
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;
            return foreground == expectedRootWindow ||
                GetAncestor(foreground, 2) == expectedRootWindow ||
                IsChild(expectedRootWindow, foreground);
        }

        internal static void PromoteWindow(IntPtr window)
        {
            if (window == IntPtr.Zero) return;
            RestoreWindowForActivation(window);
            SetWindowPos(window, WindowTopMost, 0, 0, 0, 0, SetWindowPosNoMove | SetWindowPosNoSize | SetWindowPosShowWindow);
            TrackReplayPromotedWindow(window);
            SwitchToThisWindow(window, true);
            BringWindowToTop(window);
            SetForegroundWindow(window);
        }

        private static void RestoreWindowForActivation(IntPtr window)
        {
            if (IsIconic(window)) ShowWindowAsync(window, ShowRestore);
        }

        private static void TrackReplayPromotedWindow(IntPtr window)
        {
            if (window == IntPtr.Zero) return;
            lock (replayPromotedWindowsLock) replayPromotedWindows.Add(window);
        }

        internal static bool IsPointOwnedByProcessName(int x, int y, string expectedProcessName)
        {
            var window = WindowFromPoint(new Point { X = x, Y = y });
            var processId = window == IntPtr.Zero ? 0 : ReadWindowProcessId(window);
            if (processId == 0) return false;
            try
            {
                using (var process = Process.GetProcessById((int)processId))
                {
                    return string.Equals(process.ProcessName, expectedProcessName, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsPointOwnedByWindowOrProcess(
            int x,
            int y,
            IntPtr expectedRootWindow,
            string expectedProcessName)
        {
            var hitWindow = WindowFromPoint(new Point { X = x, Y = y });
            if (hitWindow == IntPtr.Zero) return false;
            var rootWindow = GetAncestor(hitWindow, 2);
            if (rootWindow == expectedRootWindow || hitWindow == expectedRootWindow || IsChild(expectedRootWindow, hitWindow))
            {
                return true;
            }
            if (IsWindowOwnedByProcessName(rootWindow, expectedProcessName) ||
                IsWindowOwnedByProcessName(hitWindow, expectedProcessName)) return true;

            WindowRectangle expectedRectangle;
            return GetWindowRect(expectedRootWindow, out expectedRectangle) &&
                x >= expectedRectangle.Left && x <= expectedRectangle.Right &&
                y >= expectedRectangle.Top && y <= expectedRectangle.Bottom &&
                (IsWindowOwnedByProcessName(hitWindow, "msedgewebview2") ||
                 IsWindowOwnedByProcessName(rootWindow, "msedgewebview2"));
        }

        internal static bool IsWindowOwnedByTargetFamily(
            IntPtr window,
            IntPtr expectedRootWindow,
            string expectedProcessName)
        {
            if (window == IntPtr.Zero) return false;
            if (window == expectedRootWindow || IsChild(expectedRootWindow, window) ||
                IsWindowOwnedByProcessName(window, expectedProcessName)) return true;
            var rootWindow = GetAncestor(window, 2);
            return IsWindowOwnedByProcessName(window, "msedgewebview2") ||
                IsWindowOwnedByProcessName(rootWindow, "msedgewebview2");
        }

        internal static string DescribePointOwner(int x, int y)
        {
            var hitWindow = WindowFromPoint(new Point { X = x, Y = y });
            var rootWindow = hitWindow == IntPtr.Zero ? IntPtr.Zero : GetAncestor(hitWindow, 2);
            return string.Format(
                "point={0},{1}; hit={2}/{3}; root={4}/{5}; foreground={6}/{7}",
                x,
                y,
                hitWindow,
                ReadWindowProcessName(hitWindow),
                rootWindow,
                ReadWindowProcessName(rootWindow),
                GetForegroundWindow(),
                ReadWindowProcessName(GetForegroundWindow()));
        }

        private static string ReadWindowProcessName(IntPtr window)
        {
            var processId = window == IntPtr.Zero ? 0 : ReadWindowProcessId(window);
            if (processId == 0) return "none";
            try
            {
                using (var process = Process.GetProcessById((int)processId)) return process.ProcessName;
            }
            catch
            {
                return "unknown";
            }
        }

        private static bool IsWindowOwnedByProcessName(IntPtr window, string expectedProcessName)
        {
            var processId = window == IntPtr.Zero ? 0 : ReadWindowProcessId(window);
            if (processId == 0) return false;
            try
            {
                using (var process = Process.GetProcessById((int)processId))
                {
                    return string.Equals(process.ProcessName, expectedProcessName, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        internal static void SendMouseClick(int x, int y)
        {
            SetCursorPos(x, y);
            System.Threading.Thread.Sleep(40);
            mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
            System.Threading.Thread.Sleep(30);
            mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
        }

        internal static void SendMouseRightClick(int x, int y)
        {
            SetCursorPos(x, y);
            System.Threading.Thread.Sleep(40);
            mouse_event(MouseEventRightDown, 0, 0, 0, UIntPtr.Zero);
            System.Threading.Thread.Sleep(30);
            mouse_event(MouseEventRightUp, 0, 0, 0, UIntPtr.Zero);
        }

        internal static void SendMouseWheel(int x, int y, int delta)
        {
            SetCursorPos(x, y);
            mouse_event(MouseEventWheel, 0, 0, unchecked((uint)delta), UIntPtr.Zero);
        }

        internal static void SendMouseDrag(int startX, int startY, int endX, int endY, int durationMilliseconds)
        {
            SetCursorPos(startX, startY);
            mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
            var steps = Math.Max(4, Math.Min(30, durationMilliseconds / 20));
            for (var index = 1; index <= steps; index++)
            {
                var ratio = (double)index / steps;
                SetCursorPos(
                    (int)Math.Round(startX + (endX - startX) * ratio),
                    (int)Math.Round(startY + (endY - startY) * ratio));
                System.Threading.Thread.Sleep(Math.Max(1, durationMilliseconds / steps));
            }
            mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
        }

        internal static void SendSelectAll()
        {
            keybd_event(VirtualKeyControl, 0, 0, UIntPtr.Zero);
            keybd_event(VirtualKeyA, 0, 0, UIntPtr.Zero);
            keybd_event(VirtualKeyA, 0, KeyEventKeyUp, UIntPtr.Zero);
            keybd_event(VirtualKeyControl, 0, KeyEventKeyUp, UIntPtr.Zero);
        }

        internal static void SendVirtualKey(byte virtualKey)
        {
            keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
            keybd_event(virtualKey, 0, KeyEventKeyUp, UIntPtr.Zero);
        }

        internal static void SendUnicodeText(string text)
        {
            foreach (var character in text ?? string.Empty)
            {
                var inputs = new[]
                {
                    new Input
                    {
                        Type = 1,
                        Union = new InputUnion
                        {
                            Keyboard = new KeyboardInput
                            {
                                ScanCode = character,
                                Flags = KeyEventUnicode
                            }
                        }
                    },
                    new Input
                    {
                        Type = 1,
                        Union = new InputUnion
                        {
                            Keyboard = new KeyboardInput
                            {
                                ScanCode = character,
                                Flags = KeyEventUnicode | KeyEventKeyUp
                            }
                        }
                    }
                };
                if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Input))) != inputs.Length)
                {
                    throw new InvalidOperationException("Unicode 文本输入失败");
                }
            }
        }
    }
}
