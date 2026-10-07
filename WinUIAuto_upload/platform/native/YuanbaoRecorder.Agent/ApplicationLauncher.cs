using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace YuanbaoRecorder.Agent
{
    internal static class ApplicationLauncher
    {
        private static readonly string CacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YuanbaoRecorder.Agent");
        private static readonly string CachedPathFile = Path.Combine(CacheDirectory, "yuanbao-executable.txt");
        private static readonly string CachedLaunchFile = Path.Combine(CacheDirectory, "yuanbao-launch-entry.txt");

        internal static bool EnsureWindow(
            string processName,
            bool restart,
            System.Threading.CancellationToken cancellationToken,
            out IntPtr window,
            out string error)
        {
            error = null;
            window = NativeMethods.FindLargestVisibleWindowByProcessName(processName);
            var executablePath = ResolveExecutablePath(processName);
            var launchPath = ResolveLaunchPath(executablePath);

            if (restart)
            {
                if (string.IsNullOrWhiteSpace(launchPath))
                {
                    error = "无法确定元宝启动路径。请先手动启动一次元宝，让 Agent 记录安装路径。";
                    return false;
                }
                StopProcesses(processName);
                window = IntPtr.Zero;
            }
            else if (window != IntPtr.Zero)
            {
                RememberExecutablePath(executablePath);
                return true;
            }
            else if (Process.GetProcessesByName(processName).Length > 0)
            {
                // Keep the tray/background process alive. Explorer-mediated shortcut activation below
                // reproduces a user's double-click and asks the existing single instance to show itself.
                window = IntPtr.Zero;
            }
            if (string.IsNullOrWhiteSpace(launchPath))
            {
                error = "元宝未运行，且未找到其安装路径。请先手动启动一次元宝。";
                return false;
            }

            string startError;
            if (!TryStart(launchPath, executablePath, out startError))
            {
                error = startError;
                return false;
            }

            var deadline = DateTime.UtcNow.AddSeconds(40);
            var nextWakeup = DateTime.UtcNow.AddSeconds(10);
            var wakeupAttempts = 0;
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                window = NativeMethods.FindLargestVisibleWindowByProcessName(processName);
                if (window != IntPtr.Zero)
                {
                    RememberExecutablePath(executablePath);
                    return true;
                }
                if (DateTime.UtcNow >= nextWakeup && wakeupAttempts < 2)
                {
                    wakeupAttempts++;
                    TryStart(launchPath, executablePath, out startError);
                    nextWakeup = DateTime.UtcNow.AddSeconds(10);
                }
                if (cancellationToken.WaitHandle.WaitOne(500)) cancellationToken.ThrowIfCancellationRequested();
            }
            error = "已通过启动入口唤醒元宝，但等待主窗口出现超时。启动入口：" + launchPath;
            return false;
        }

        private static bool TryStart(string launchPath, string executablePath, out string error)
        {
            error = null;
            try
            {
                Process.Start(BuildLaunchStartInfo(launchPath, executablePath));
                return true;
            }
            catch (Exception exception)
            {
                error = "启动元宝失败：" + exception.Message;
                return false;
            }
        }

        internal static ProcessStartInfo BuildLaunchStartInfo(string launchPath, string executablePath)
        {
            if (string.Equals(Path.GetExtension(launchPath), ".lnk", StringComparison.OrdinalIgnoreCase))
            {
                return new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                    Arguments = "\"" + launchPath + "\"",
                    WorkingDirectory = Path.GetDirectoryName(executablePath ?? launchPath),
                    UseShellExecute = false
                };
            }
            return new ProcessStartInfo
            {
                FileName = launchPath,
                WorkingDirectory = Path.GetDirectoryName(executablePath ?? launchPath),
                UseShellExecute = true
            };
        }

        internal static string ResolveExecutablePath(string processName)
        {
            var runningPath = Process.GetProcessesByName(processName)
                .Select(ReadExecutablePath)
                .FirstOrDefault(IsUsableExecutable);
            if (IsUsableExecutable(runningPath))
            {
                RememberExecutablePath(runningPath);
                return runningPath;
            }

            var cachedPath = ReadCachedPath();
            if (IsUsableExecutable(cachedPath)) return cachedPath;

            foreach (var candidate in EnumerateRegistryCandidates().Concat(EnumerateCommonCandidates()))
            {
                if (!IsUsableExecutable(candidate)) continue;
                RememberExecutablePath(candidate);
                return candidate;
            }
            return null;
        }

        private static string ResolveLaunchPath(string executablePath)
        {
            var cachedLaunchPath = ReadTextFile(CachedLaunchFile);
            if (IsUsableLaunchEntry(cachedLaunchPath)) return cachedLaunchPath;
            foreach (var shortcut in EnumerateShortcutCandidates())
            {
                if (!IsUsableLaunchEntry(shortcut)) continue;
                TryRememberPath(CachedLaunchFile, shortcut);
                return shortcut;
            }
            return IsUsableExecutable(executablePath) ? executablePath : null;
        }

        private static void StopProcesses(string processName)
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    if (!process.CloseMainWindow() || !process.WaitForExit(3000))
                    {
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                }
                catch (InvalidOperationException) { }
                finally { process.Dispose(); }
            }
        }


        private static IEnumerable<string> EnumerateRegistryCandidates()
        {
            var keyPaths = new[]
            {
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
                @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                foreach (var keyPath in keyPaths)
                {
                    using (var root = hive.OpenSubKey(keyPath))
                    {
                        if (root == null) continue;
                        foreach (var subKeyName in root.GetSubKeyNames())
                        {
                            using (var subKey = root.OpenSubKey(subKeyName))
                            {
                                if (subKey == null) continue;
                                var displayName = Convert.ToString(subKey.GetValue("DisplayName"));
                                if (displayName.IndexOf("元宝", StringComparison.OrdinalIgnoreCase) < 0 &&
                                    displayName.IndexOf("Yuanbao", StringComparison.OrdinalIgnoreCase) < 0) continue;
                                var displayIcon = NormalizeExecutablePath(Convert.ToString(subKey.GetValue("DisplayIcon")));
                                if (!string.IsNullOrWhiteSpace(displayIcon)) yield return displayIcon;
                                var installLocation = Convert.ToString(subKey.GetValue("InstallLocation"));
                                if (!string.IsNullOrWhiteSpace(installLocation)) yield return Path.Combine(installLocation, "yuanbao.exe");
                            }
                        }
                    }
                }
            }
        }

        private static IEnumerable<string> EnumerateCommonCandidates()
        {
            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };
            foreach (var root in roots.Where(item => !string.IsNullOrWhiteSpace(item)))
            {
                yield return Path.Combine(root, "Tencent", "Yuanbao", "yuanbao.exe");
                yield return Path.Combine(root, "Yuanbao", "yuanbao.exe");
            }
        }

        private static IEnumerable<string> EnumerateShortcutCandidates()
        {
            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
            };
            foreach (var root in roots.Where(item => !string.IsNullOrWhiteSpace(item) && Directory.Exists(item)))
            {
                string[] files;
                try { files = Directory.GetFiles(root, "*.lnk", SearchOption.AllDirectories); }
                catch { continue; }
                foreach (var file in files)
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (name.IndexOf("元宝", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Yuanbao", StringComparison.OrdinalIgnoreCase) >= 0) yield return file;
                }
            }
        }

        private static string ReadCachedPath()
        {
            return ReadTextFile(CachedPathFile);
        }

        private static void RememberExecutablePath(string path)
        {
            if (!IsUsableExecutable(path)) return;
            try
            {
                TryRememberPath(CachedPathFile, path);
            }
            catch { }
        }

        private static string ReadTextFile(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
            catch { return null; }
        }

        private static void RememberPath(string cacheFile, string value)
        {
            Directory.CreateDirectory(CacheDirectory);
            File.WriteAllText(cacheFile, value);
        }

        private static void TryRememberPath(string cacheFile, string value)
        {
            try { RememberPath(cacheFile, value); }
            catch { }
        }

        private static string ReadExecutablePath(Process process)
        {
            try { return process.MainModule.FileName; }
            catch { return null; }
            finally { process.Dispose(); }
        }

        private static string NormalizeExecutablePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var comma = value.IndexOf(',');
            if (comma >= 0) value = value.Substring(0, comma);
            return value.Trim().Trim('"');
        }

        private static bool IsUsableExecutable(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) &&
                string.Equals(Path.GetFileName(path), "yuanbao.exe", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsUsableLaunchEntry(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) &&
                (string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase) || IsUsableExecutable(path));
        }
    }
}
