using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Automation;

namespace YuanbaoRecorder.Agent
{
    internal sealed class ReplayLocatorResult
    {
        internal AutomationElement Element;
        internal IntPtr WindowHandle;
        internal string LocatorUsed;
        internal string Diagnostics;

        internal bool Found { get { return Element != null; } }
    }

    internal sealed class ReplayLocatorEngine
    {
        internal ReplayLocatorResult FindWithWait(
            AutomationElement primaryRoot,
            string processName,
            LocatorBundle locator,
            int timeoutMilliseconds,
            CancellationToken cancellationToken)
        {
            if (locator == null)
            {
                return new ReplayLocatorResult { Diagnostics = "target=none;lookup=skipped" };
            }
            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(0, timeoutMilliseconds));
            var attempts = 0;
            var searchedRoots = new List<string>();
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                attempts++;
                foreach (var root in EnumerateProcessRoots(primaryRoot, processName))
                {
                    var rootDescription = DescribeRoot(root);
                    if (!searchedRoots.Contains(rootDescription)) searchedRoots.Add(rootDescription);
                    string locatorUsed;
                    var element = ReplayExecutor.FindElement(root, locator, out locatorUsed);
                    if (element == null) continue;
                    return new ReplayLocatorResult
                    {
                        Element = element,
                        WindowHandle = new IntPtr(Read(() => root.Current.NativeWindowHandle)),
                        LocatorUsed = "window=" + rootDescription + ";" + locatorUsed,
                        Diagnostics = "attempts=" + attempts + ";scope=process_windows;roots=" + string.Join(",", searchedRoots)
                    };
                }

                if (DateTime.UtcNow >= deadline) break;
                if (cancellationToken.WaitHandle.WaitOne(200)) cancellationToken.ThrowIfCancellationRequested();
            } while (true);

            return new ReplayLocatorResult
            {
                Diagnostics = "attempts=" + attempts + ";scope=process_windows;roots=" + string.Join(",", searchedRoots) + ";target=" + ReplayExecutor.DescribeLocator(locator) + ";visible_named=" + DescribeVisibleNamedElements(primaryRoot, processName)
            };
        }

        internal ReplayLocatorResult FindStrictIdentity(
            AutomationElement primaryRoot,
            string processName,
            LocatorBundle locator,
            CancellationToken cancellationToken)
        {
            var searchedRoots = new List<string>();
            foreach (var root in EnumerateProcessRoots(primaryRoot, processName))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rootDescription = DescribeRoot(root);
                searchedRoots.Add(rootDescription);
                string locatorUsed;
                var element = ReplayExecutor.FindElementStrictIdentity(root, locator, out locatorUsed);
                if (element != null)
                {
                    return new ReplayLocatorResult
                    {
                        Element = element,
                        WindowHandle = new IntPtr(Read(() => root.Current.NativeWindowHandle)),
                        LocatorUsed = "window=" + rootDescription + ";" + locatorUsed,
                        Diagnostics = "mode=strict_identity;scope=process_windows;roots=" + string.Join(",", searchedRoots)
                    };
                }
            }
            return new ReplayLocatorResult
            {
                Diagnostics = "mode=strict_identity;result=not_found;roots=" + string.Join(",", searchedRoots) +
                    ";target=" + ReplayExecutor.DescribeLocator(locator)
            };
        }

        private static string DescribeVisibleNamedElements(AutomationElement primaryRoot, string processName)
        {
            var names = new List<string>();
            foreach (var root in EnumerateProcessRoots(primaryRoot, processName))
            {
                try
                {
                    var elements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                    foreach (AutomationElement element in elements)
                    {
                        if (!IsVisible(element)) continue;
                        var name = Read(() => element.Current.Name);
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        var controlType = Read(() => element.Current.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty));
                        var description = controlType + ":" + name;
                        if (!names.Contains(description)) names.Add(description);
                        if (names.Count >= 40) return string.Join("|", names);
                    }
                }
                catch (ElementNotAvailableException) { }
                catch (InvalidOperationException) { }
            }
            return string.Join("|", names);
        }

        private static IEnumerable<AutomationElement> EnumerateProcessRoots(AutomationElement primaryRoot, string processName)
        {
            var seen = new HashSet<long>();
            if (primaryRoot != null)
            {
                var handle = Read(() => primaryRoot.Current.NativeWindowHandle);
                if (handle != 0) seen.Add(handle);
                yield return primaryRoot;
            }

            foreach (var handle in NativeMethods.FindVisibleWindowsByProcessName(processName))
            {
                if (!seen.Add(handle.ToInt64())) continue;
                AutomationElement root;
                try { root = AutomationElement.FromHandle(handle); }
                catch (ElementNotAvailableException) { continue; }
                catch (InvalidOperationException) { continue; }
                if (root != null) yield return root;
            }
        }

        private static bool IsVisible(AutomationElement element)
        {
            try
            {
                var bounds = element.Current.BoundingRectangle;
                return !element.Current.IsOffscreen && !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0;
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }
        }

        private static string DescribeRoot(AutomationElement root)
        {
            var name = Read(() => root.Current.Name);
            var handle = Read(() => root.Current.NativeWindowHandle);
            return (string.IsNullOrWhiteSpace(name) ? "<untitled>" : name) + "#" + handle;
        }

        private static T Read<T>(Func<T> reader)
        {
            try { return reader(); }
            catch (ElementNotAvailableException) { return default(T); }
            catch (InvalidOperationException) { return default(T); }
        }
    }
}
