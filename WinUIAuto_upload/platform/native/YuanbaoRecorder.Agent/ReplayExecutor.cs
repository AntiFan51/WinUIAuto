using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Automation;

namespace YuanbaoRecorder.Agent
{
    internal sealed class ReplayExecutionResult
    {
        internal bool Success;
        internal string Error;
        internal List<ReplayStepResult> Steps = new List<ReplayStepResult>();
    }

    internal sealed class ReplayExecutor
    {
        private readonly bool requireForeground;

        internal ReplayExecutor(bool requireForeground = true)
        {
            this.requireForeground = requireForeground;
        }

        internal ReplayExecutionResult Execute(
            ReplayTask task,
            CancellationToken cancellationToken,
            Action<ReplayStepResult> onStepCompleted)
        {
            var result = new ReplayExecutionResult();
            var processName = string.IsNullOrWhiteSpace(task.Target == null ? null : task.Target.ProcessName)
                ? "yuanbao"
                : task.Target.ProcessName;
            var restarted = task.Target != null && task.Target.RestartBeforeReplay;
            IntPtr targetWindow;
            string launchError;
            if (!ApplicationLauncher.EnsureWindow(processName, restarted, cancellationToken, out targetWindow, out launchError))
            {
                result.Error = launchError;
                return result;
            }
            var actions = task.Actions ?? new List<ReplayAction>();
            AutomationElement root;
            string rootError;
            if (!AcquireInitialAutomationRoot(
                processName,
                ref targetWindow,
                restarted,
                cancellationToken,
                requireForeground,
                out root,
                out rootError))
            {
                NativeMethods.ReleaseReplayWindow(targetWindow);
                result.Error = rootError;
                return result;
            }
            try
            {
                var assertions = task.Assertions ?? new List<ReplayAssertion>();
                var locatorEngine = new ReplayLocatorEngine();
                var outcomeVerifier = new ReplayOutcomeVerifier(locatorEngine);
                var falseBranchStartIndex = -1;
                var falseBranchStepCount = 0;
                for (var index = 0; index < actions.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (index == falseBranchStartIndex && falseBranchStepCount > 0)
                    {
                        for (var offset = 0; offset < falseBranchStepCount && index + offset < actions.Count; offset++)
                        {
                            var skipped = new ReplayStepResult
                            {
                                ActionId = actions[index + offset].Id,
                                Index = index + offset + 1,
                                Status = "skipped",
                                Error = "IF 条件成立，已跳过 ELSE 分支"
                            };
                            result.Steps.Add(skipped);
                            onStepCompleted(skipped);
                        }
                        index += falseBranchStepCount - 1;
                        falseBranchStartIndex = -1;
                        falseBranchStepCount = 0;
                        continue;
                    }
                    var action = actions[index];
                    var step = ExecuteAction(
                        root,
                        targetWindow,
                        processName,
                        task.Target,
                        action,
                        index + 1,
                        locatorEngine,
                        outcomeVerifier,
                        requireForeground,
                        cancellationToken);
                    if (string.Equals(step.Status, "succeeded", StringComparison.Ordinal))
                    {
                        foreach (var assertion in assertions.Where(item =>
                            string.Equals(item.AfterActionId, action.Id, StringComparison.Ordinal)))
                        {
                            // Chromium navigation can replace the accessibility tree while
                            // keeping the visible Yuanbao window alive. Actions already
                            // resolve against current process windows; assertions must also
                            // rebind instead of traversing the initial, potentially stale UIA root.
                            var assertionRoot = RefreshAutomationRoot(processName, ref targetWindow, root);
                            if (assertionRoot != null) root = assertionRoot;
                            var assertionResult = ExecuteAssertion(
                                root,
                                processName,
                                locatorEngine,
                                assertion,
                                cancellationToken);
                            step.AssertionResults.Add(assertionResult);
                            if (!string.Equals(assertionResult.Status, "passed", StringComparison.Ordinal))
                            {
                                step.Status = "assertion_failed";
                                step.Error = assertionResult.Error;
                                break;
                            }
                        }
                    }
                    result.Steps.Add(step);
                    onStepCompleted(step);
                    if (!string.Equals(step.Status, "succeeded", StringComparison.Ordinal))
                    {
                        result.Error = step.Error;
                        return result;
                    }

                    if (string.Equals(action.Type, "condition", StringComparison.Ordinal) && step.ConditionMatched == false)
                    {
                        var skipCount = Math.Max(1, action.TrueStepCount > 0 ? action.TrueStepCount : action.SkipIfFalse);
                        for (var offset = 1; offset <= skipCount && index + offset < actions.Count; offset++)
                        {
                            var skipped = new ReplayStepResult
                            {
                                ActionId = actions[index + offset].Id,
                                Index = index + offset + 1,
                                Status = "skipped",
                                Error = "条件不成立，已跳过"
                            };
                            result.Steps.Add(skipped);
                            onStepCompleted(skipped);
                        }
                        index += skipCount;
                    }
                    else if (string.Equals(action.Type, "condition", StringComparison.Ordinal) &&
                        step.ConditionMatched == true && action.FalseStepCount > 0)
                    {
                        var trueStepCount = Math.Max(1, action.TrueStepCount > 0 ? action.TrueStepCount : action.SkipIfFalse);
                        falseBranchStartIndex = index + trueStepCount + 1;
                        falseBranchStepCount = action.FalseStepCount;
                    }

                    var delay = action.DelayAfterMilliseconds > 0 ? action.DelayAfterMilliseconds : 300;
                    if (cancellationToken.WaitHandle.WaitOne(delay)) cancellationToken.ThrowIfCancellationRequested();
                }

                result.Success = true;
                return result;
            }
            finally
            {
                NativeMethods.ReleaseReplayWindow(targetWindow);
            }
        }

        private static AutomationElement RefreshAutomationRoot(
            string processName,
            ref IntPtr targetWindow,
            AutomationElement fallback)
        {
            try
            {
                // Keep the HWND that was activated and used for interaction.
                // Yuanbao also owns a large untitled Chromium host window; a
                // fresh "largest window" search can incorrectly bind to it.
                if (targetWindow != IntPtr.Zero) return AutomationElement.FromHandle(targetWindow);
            }
            catch (ElementNotAvailableException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            try
            {
                var latestWindow = NativeMethods.FindLargestVisibleWindowByProcessName(processName);
                if (latestWindow != IntPtr.Zero)
                {
                    targetWindow = latestWindow;
                    return AutomationElement.FromHandle(targetWindow);
                }
            }
            catch (ElementNotAvailableException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            return fallback;
        }

        private static bool AcquireInitialAutomationRoot(
            string processName,
            ref IntPtr window,
            bool restarted,
            CancellationToken cancellationToken,
            bool requireForeground,
            out AutomationElement root,
            out string error)
        {
            root = null;
            error = null;
            var deadline = DateTime.UtcNow.AddSeconds(restarted ? 45 : 20);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var latestWindow = NativeMethods.FindLargestVisibleWindowByProcessName(processName);
                if (latestWindow != IntPtr.Zero) window = latestWindow;
                var candidateWindows = NativeMethods.FindVisibleWindowsByProcessName(processName);
                if (window != IntPtr.Zero && !candidateWindows.Contains(window)) candidateWindows.Insert(0, window);
                foreach (var candidateWindow in candidateWindows)
                {
                    try
                    {
                        var candidate = AutomationElement.FromHandle(candidateWindow);
                        if (!HasUsableApplicationContent(candidate)) continue;

                        if (requireForeground)
                        {
                            NativeMethods.ShowWindowAsync(candidateWindow, NativeMethods.ShowNormal);
                            if (!NativeMethods.EnsureForegroundWindowByProcessName(candidateWindow, processName, 3))
                            {
                                error = "元宝页面窗口已找到，但无法取得前台操作权";
                                continue;
                            }
                        }
                        window = candidateWindow;
                        root = candidate;
                        return true;
                    }
                    catch (ElementNotAvailableException)
                    {
                        error = "元宝窗口 UIA 根节点尚未就绪";
                    }
                    catch (InvalidOperationException)
                    {
                        error = "无法创建元宝 UIA 根节点";
                    }
                }
                if (candidateWindows.Count > 0) error = "元宝窗口已出现，正在等待任一窗口的 Chromium/UIA 内容树就绪";
                if (cancellationToken.WaitHandle.WaitOne(250)) cancellationToken.ThrowIfCancellationRequested();
            }
            error = string.IsNullOrWhiteSpace(error)
                ? "元宝窗口已出现，但未能获取可用 UIA 内容树"
                : error;
            return false;
        }

        private static bool HasUsableApplicationContent(AutomationElement root)
        {
            if (root == null) return false;
            try
            {
                var bounds = root.Current.BoundingRectangle;
                if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return false;

                // Yuanbao's interactive page is hosted in Chromium/WebView. Waiting for
                // a visible Chrome descendant avoids accepting only the native shell or
                // splash window while remaining independent of any particular business page.
                // Chromium providers can temporarily report the host subtree as
                // offscreen even while the composed Tauri window is visible. Presence
                // of the Chrome subtree is the stable shell-readiness signal here;
                // individual actions still enforce visibility before interaction.
                var condition = new PropertyCondition(AutomationElement.FrameworkIdProperty, "Chrome");
                return root.FindFirst(TreeScope.Descendants, condition) != null;
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static ReplayStepResult ExecuteAction(
            AutomationElement root,
            IntPtr targetWindow,
            string targetProcessName,
            ReplayTarget replayTarget,
            ReplayAction action,
            int index,
            ReplayLocatorEngine locatorEngine,
            ReplayOutcomeVerifier outcomeVerifier,
            bool enforceForeground,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var step = new ReplayStepResult
            {
                ActionId = action.Id,
                Index = index,
                Status = "failed"
            };

            try
            {
                if (string.Equals(action.Type, "wait_time", StringComparison.Ordinal))
                {
                    var duration = Math.Max(1000, Math.Min(120000, action.TimeoutMilliseconds));
                    if (cancellationToken.WaitHandle.WaitOne(duration))
                        cancellationToken.ThrowIfCancellationRequested();
                    step.Status = "succeeded";
                    step.LocatorUsed = "fixed_delay=" + duration + "ms";
                    step.ExecutionMode = "timer";
                    step.Outcome = "fixed_delay_completed";
                    return step;
                }
                if (IsPointerAction(action.Type) && IsAmbiguousSharedSidebarRowLocator(action.Locator))
                {
                    throw new InvalidOperationException(
                        "侧边栏分组/会话目标缺少名称或分区范围，已拒绝模糊回放：" + DescribeLocator(action.Locator));
                }
                if (IsPointerAction(action.Type) && IsAmbiguousConversationItemLocator(action.Locator))
                {
                    throw new InvalidOperationException(
                        "会话内容行缺少可验证的内部文字，已拒绝模糊回放：" + DescribeLocator(action.Locator));
                }
                var outcomeProbe = outcomeVerifier.CaptureBaseline(
                    root,
                    targetProcessName,
                    action,
                    cancellationToken);
                var lookupTimeout = string.Equals(action.Type, "wait_for_target", StringComparison.Ordinal)
                    ? 0
                    : string.Equals(action.Type, "condition", StringComparison.Ordinal)
                        ? 500
                        : IsPointerAction(action.Type) ? 700 : 8000;
                string collectionDiagnostics = null;
                bool? collectionConditionMatched = null;
                if (string.Equals(action.Type, "condition", StringComparison.Ordinal) &&
                    string.Equals(action.ConditionType, "group_list_not_empty", StringComparison.Ordinal))
                {
                    collectionConditionMatched = EvaluateGroupListNotEmpty(root, out collectionDiagnostics);
                }
                else if (string.Equals(action.Type, "condition", StringComparison.Ordinal) &&
                    (string.Equals(action.ConditionType, "same_kind_exists", StringComparison.Ordinal) ||
                     string.Equals(action.ConditionType, "same_kind_not_exists", StringComparison.Ordinal)))
                {
                    var sameKindExists = EvaluateSameKindExists(root, action, out collectionDiagnostics);
                    collectionConditionMatched = string.Equals(
                        action.ConditionType,
                        "same_kind_not_exists",
                        StringComparison.Ordinal)
                        ? !sameKindExists
                        : sameKindExists;
                }
                var lookup = collectionConditionMatched.HasValue
                    ? new ReplayLocatorResult
                    {
                        Diagnostics = collectionDiagnostics,
                        LocatorUsed = "collection_scope=分组"
                    }
                    : string.Equals(action.Type, "condition", StringComparison.Ordinal)
                    ? locatorEngine.FindStrictIdentity(
                        root,
                        targetProcessName,
                        action.Locator,
                        cancellationToken)
                    : locatorEngine.FindWithWait(
                        root,
                        targetProcessName,
                        action.Locator,
                        lookupTimeout,
                        cancellationToken);
                if (!lookup.Found && IsPointerAction(action.Type) && HasSemanticLocator(action.Locator))
                {
                    ReplayLocatorResult revealedLookup;
                    string hoverDiagnostics;
                    if (TryRevealTargetByHover(
                        root,
                        targetWindow,
                        targetProcessName,
                        replayTarget,
                        action,
                        locatorEngine,
                        enforceForeground,
                        cancellationToken,
                        out revealedLookup,
                        out hoverDiagnostics))
                    {
                        lookup = revealedLookup;
                    }
                    lookup.Diagnostics = hoverDiagnostics + ";" + (lookup.Diagnostics ?? string.Empty);
                }
                var element = lookup.Element;
                var interactionWindow = lookup.WindowHandle != IntPtr.Zero
                    ? lookup.WindowHandle
                    : targetWindow;
                var locatorUsed = lookup.LocatorUsed;
                step.LocatorUsed = locatorUsed;
                step.LocatorDiagnostics = lookup.Diagnostics;
                switch (action.Type)
                {
                    case "click":
                        Click(
                            element,
                            interactionWindow,
                            targetProcessName,
                            replayTarget,
                            action,
                            outcomeProbe.Effect != null,
                            false,
                            enforceForeground,
                            ref locatorUsed);
                        step.InteractionAttempts.Add(locatorUsed);
                        break;
                    case "right_click":
                        RightClick(
                            element,
                            interactionWindow,
                            targetProcessName,
                            replayTarget,
                            action,
                            enforceForeground,
                            ref locatorUsed);
                        break;
                    case "input_text":
                        InputTextWithStaleRetry(
                            root,
                            locatorEngine,
                            element,
                            interactionWindow,
                            targetProcessName,
                            replayTarget,
                            action,
                            enforceForeground,
                            cancellationToken,
                            ref locatorUsed);
                        break;
                    case "key_press":
                        if (enforceForeground) EnsureReplayForeground(interactionWindow, targetProcessName);
                        PressKey(action.Value);
                        locatorUsed = "keyboard";
                        break;
                    case "scroll":
                        Scroll(interactionWindow, targetProcessName, replayTarget, action, ref locatorUsed);
                        break;
                    case "drag":
                        Drag(interactionWindow, targetProcessName, replayTarget, action, ref locatorUsed);
                        break;
                    case "condition":
                        if (collectionConditionMatched.HasValue)
                        {
                            step.ConditionMatched = collectionConditionMatched.Value;
                            locatorUsed = "condition_match_mode=" +
                                (string.IsNullOrWhiteSpace(action.ConditionMatchMode) ? "legacy_collection" : action.ConditionMatchMode) +
                                ";scope=" + (string.IsNullOrWhiteSpace(action.ConditionScopeType) ? "section" : action.ConditionScopeType);
                            step.LocatorDiagnostics = collectionDiagnostics;
                        }
                        else
                        {
                            var exists = element != null;
                            step.ConditionMatched = string.Equals(action.ConditionType, "target_not_exists", StringComparison.Ordinal)
                                ? !exists
                                : exists;
                            locatorUsed = exists ? locatorUsed : "target_not_found";
                        }
                        break;
                    case "wait_for_target":
                        var waited = WaitForTarget(
                            root,
                            targetProcessName,
                            action,
                            locatorEngine,
                            cancellationToken);
                        element = waited.Element;
                        locatorUsed = waited.LocatorUsed;
                        step.LocatorDiagnostics = waited.Diagnostics;
                        step.ConditionMatched = true;
                        break;
                    default:
                        throw new InvalidOperationException("不支持的回放动作类型：" + action.Type);
                }
                if (string.Equals(action.Type, "wait_for_target", StringComparison.Ordinal))
                {
                    step.Outcome = "wait_condition_satisfied";
                }
                else
                {
                    var outcome = outcomeVerifier.VerifyTransition(
                        root,
                        targetProcessName,
                        outcomeProbe,
                        5000,
                        cancellationToken);
                    for (var retry = 0;
                        !outcome.Verified && retry < 2 && string.Equals(action.Type, "click", StringComparison.Ordinal);
                        retry++)
                    {
                        var retryLookup = locatorEngine.FindWithWait(
                            root,
                            targetProcessName,
                            action.Locator,
                            1000,
                            cancellationToken);
                        if (!retryLookup.Found) break;
                        var retryLocator = retryLookup.LocatorUsed;
                        var retryWindow = retryLookup.WindowHandle != IntPtr.Zero
                            ? retryLookup.WindowHandle
                            : targetWindow;
                        Click(
                            retryLookup.Element,
                            retryWindow,
                            targetProcessName,
                            replayTarget,
                            action,
                            true,
                            false,
                            enforceForeground,
                            ref retryLocator);
                        step.InteractionAttempts.Add("effect_retry_" + (retry + 1) + ":" + retryLocator);
                        outcome = outcomeVerifier.VerifyTransition(
                            root,
                            targetProcessName,
                            outcomeProbe,
                            5000,
                            cancellationToken);
                    }
                    step.Outcome = outcome.Description;
                    if (!outcome.Verified)
                    {
                        throw new InvalidOperationException(
                            "操作已执行，但预期界面未出现：" + outcome.Description + "; " + outcome.Diagnostics);
                    }
                }
                step.Status = "succeeded";
                step.LocatorUsed = locatorUsed;
                step.ExecutionMode = DescribeExecutionMode(action.Type, locatorUsed);
                step.Degraded = string.Equals(step.ExecutionMode, "coordinate_fallback", StringComparison.Ordinal);
            }
            catch (Exception error)
            {
                step.Error = error.Message;
                step.LocatorDiagnostics = (step.LocatorDiagnostics ?? string.Empty) + ";exception=" + error;
            }
            finally
            {
                stopwatch.Stop();
                step.DurationMilliseconds = stopwatch.ElapsedMilliseconds;
            }
            return step;
        }

        private static bool EvaluateGroupListNotEmpty(AutomationElement root, out string diagnostics)
        {
            diagnostics = "condition=group_list_not_empty;result=false;reason=scope_not_found";
            if (root == null) return false;
            var visible = EnumerateElements(root, false)
                .Select(element => new
                {
                    Element = element,
                    Name = Read(() => element.Current.Name),
                    ClassName = Read(() => element.Current.ClassName),
                    ControlType = Read(() => element.Current.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty)),
                    Bounds = ReadValue(() => element.Current.BoundingRectangle, System.Windows.Rect.Empty),
                    Offscreen = ReadValue(() => element.Current.IsOffscreen, true)
                })
                .Where(item => !item.Offscreen && !item.Bounds.IsEmpty && item.Bounds.Width > 0 && item.Bounds.Height > 0)
                .ToList();
            var groupHeader = visible
                .Where(item => string.Equals(item.Name, "分组", StringComparison.Ordinal))
                .OrderByDescending(item => item.Bounds.Width)
                .ThenBy(item => item.Bounds.Top)
                .FirstOrDefault();
            if (groupHeader == null) return false;
            var chatHeader = visible
                .Where(item => string.Equals(item.Name, "聊天", StringComparison.Ordinal) &&
                    item.Bounds.Top > groupHeader.Bounds.Bottom)
                .OrderBy(item => item.Bounds.Top)
                .FirstOrDefault();
            var lowerBoundary = chatHeader == null ? double.MaxValue : chatHeader.Bounds.Top;
            var items = visible
                .Where(item => !string.IsNullOrWhiteSpace(item.ClassName) &&
                    item.ClassName.IndexOf("Item_chatOrProjectItem", StringComparison.Ordinal) >= 0)
                .Where(item => item.Bounds.Top >= groupHeader.Bounds.Bottom - 2 && item.Bounds.Top < lowerBoundary)
                .Where(item => item.Bounds.Left >= groupHeader.Bounds.Left - 8 &&
                    item.Bounds.Right <= groupHeader.Bounds.Right + 8)
                .ToList();
            diagnostics = "condition=group_list_not_empty;scope_header=分组;next_header=" +
                (chatHeader == null ? "none" : "聊天") + ";item_class=Item_chatOrProjectItem;match_count=" + items.Count;
            return items.Count > 0;
        }

        private static bool EvaluateSameKindExists(
            AutomationElement root,
            ReplayAction action,
            out string diagnostics)
        {
            diagnostics = "condition=same_kind;result=false;reason=invalid_selector";
            if (root == null || action == null || action.Locator == null) return false;
            var expectedClass = BaseClassName(action.Locator.ClassName);
            var expectedControlType = action.Locator.ControlType ?? string.Empty;
            if (string.IsNullOrWhiteSpace(expectedClass) && string.IsNullOrWhiteSpace(expectedControlType)) return false;
            var visible = EnumerateElements(root, false)
                .Select(element => new
                {
                    Name = Read(() => element.Current.Name),
                    ClassName = Read(() => element.Current.ClassName),
                    ControlType = Read(() => element.Current.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty)),
                    Bounds = ReadValue(() => element.Current.BoundingRectangle, System.Windows.Rect.Empty),
                    Offscreen = ReadValue(() => element.Current.IsOffscreen, true)
                })
                .Where(item => !item.Offscreen && !item.Bounds.IsEmpty && item.Bounds.Width > 0 && item.Bounds.Height > 0)
                .ToList();
            double top = double.MinValue;
            double bottom = double.MaxValue;
            if (string.Equals(action.ConditionScopeType, "section", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(action.ConditionScopeStartName))
            {
                var start = visible.Where(item => string.Equals(item.Name, action.ConditionScopeStartName, StringComparison.Ordinal))
                    .OrderBy(item => item.Bounds.Top).FirstOrDefault();
                var end = visible.Where(item => string.Equals(item.Name, action.ConditionScopeEndName, StringComparison.Ordinal) &&
                    (start == null || item.Bounds.Top > start.Bounds.Bottom))
                    .OrderBy(item => item.Bounds.Top).FirstOrDefault();
                if (start == null)
                {
                    diagnostics = "condition=same_kind;result=false;reason=scope_start_not_found;scope_start=" +
                        action.ConditionScopeStartName;
                    return false;
                }
                top = start.Bounds.Bottom - 2;
                if (end != null) bottom = end.Bounds.Top;
            }
            var matches = visible.Where(item =>
                    (string.IsNullOrWhiteSpace(expectedClass) ||
                     string.Equals(BaseClassName(item.ClassName), expectedClass, StringComparison.Ordinal)) &&
                    (string.IsNullOrWhiteSpace(expectedControlType) ||
                     string.Equals(item.ControlType, expectedControlType, StringComparison.OrdinalIgnoreCase)) &&
                    item.Bounds.Top >= top && item.Bounds.Top < bottom)
                .ToList();
            diagnostics = "condition=same_kind;selector_class=" + (expectedClass ?? "none") +
                ";selector_control_type=" + (expectedControlType.Length == 0 ? "none" : expectedControlType) +
                ";scope=" + (action.ConditionScopeType ?? "window") +
                ";scope_start=" + (action.ConditionScopeStartName ?? "none") +
                ";scope_end=" + (action.ConditionScopeEndName ?? "none") +
                ";match_count=" + matches.Count;
            return matches.Count > 0;
        }

        private static string BaseClassName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var token = value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(token)) return null;
            var marker = token.IndexOf("__", StringComparison.Ordinal);
            return marker > 0 ? token.Substring(0, marker) : token;
        }

        internal static ReplayAssertionResult ExecuteAssertion(
            AutomationElement root,
            ReplayAssertion assertion,
            CancellationToken cancellationToken)
        {
            return ExecuteAssertion(root, null, null, assertion, cancellationToken);
        }

        internal static ReplayAssertionResult ExecuteAssertion(
            AutomationElement root,
            string processName,
            ReplayLocatorEngine locatorEngine,
            ReplayAssertion assertion,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = new ReplayAssertionResult
            {
                AssertionId = assertion.Id,
                Label = assertion.Label,
                Type = assertion.Type,
                Expected = assertion.Expected,
                Status = "failed"
            };
            var timeout = assertion.TimeoutMilliseconds > 0 ? assertion.TimeoutMilliseconds : 5000;
            var consecutiveAbsenceCount = 0;
            var absenceConfirmationPending = false;
            try
            {
                // A negative assertion needs two consecutive absence samples to avoid
                // passing on a transient UIA refresh. A single cross-window lookup can,
                // however, take longer than the user-facing assertion timeout. Once the
                // first absence has been observed, always grant exactly one confirmation
                // probe instead of timing out with actual=not_exists but status=failed.
                while (stopwatch.ElapsedMilliseconds <= timeout || absenceConfirmationPending)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var assertionRoot = root;
                    AutomationElement target = null;
                    var locator = assertion.Target == null ? null : assertion.Target.Locator;
                    if (locator != null)
                    {
                        if (locatorEngine != null && !string.IsNullOrWhiteSpace(processName))
                        {
                            var lookup = locatorEngine.FindWithWait(
                                root,
                                processName,
                                locator,
                                0,
                                cancellationToken);
                            target = lookup.Element;
                            result.LocatorUsed = lookup.LocatorUsed;
                            result.LocatorDiagnostics = lookup.Diagnostics;
                            if (lookup.WindowHandle != IntPtr.Zero)
                            {
                                try { assertionRoot = AutomationElement.FromHandle(lookup.WindowHandle); }
                                catch (ElementNotAvailableException) { target = null; }
                                catch (InvalidOperationException) { target = null; }
                            }
                        }
                        else
                        {
                            string locatorUsed;
                            target = FindElement(root, locator, out locatorUsed);
                            result.LocatorUsed = locatorUsed;
                            result.LocatorDiagnostics = "scope=primary_window";
                        }
                    }
                    string actual;
                    var matched = EvaluateAssertion(
                        assertionRoot,
                        target,
                        locator != null,
                        assertion,
                        out actual);
                    if (matched && string.Equals(assertion.Type, "target_not_exists", StringComparison.Ordinal))
                    {
                        consecutiveAbsenceCount++;
                        matched = consecutiveAbsenceCount >= 2;
                        absenceConfirmationPending = !matched;
                    }
                    else if (!matched)
                    {
                        consecutiveAbsenceCount = 0;
                        absenceConfirmationPending = false;
                    }
                    if (matched)
                    {
                        result.Status = "passed";
                        result.Actual = actual;
                        return result;
                    }
                    result.Actual = actual;
                    if (cancellationToken.WaitHandle.WaitOne(200)) cancellationToken.ThrowIfCancellationRequested();
                }
                result.Error = "断言超时：" + assertion.Label;
                return result;
            }
            catch (Exception error)
            {
                result.Error = error.Message;
                return result;
            }
            finally
            {
                stopwatch.Stop();
                result.DurationMilliseconds = stopwatch.ElapsedMilliseconds;
            }
        }

        private static ReplayLocatorResult WaitForTarget(
            AutomationElement root,
            string targetProcessName,
            ReplayAction action,
            ReplayLocatorEngine locatorEngine,
            CancellationToken cancellationToken)
        {
            if (action.Locator == null) throw new InvalidOperationException("等待步骤缺少目标控件");
            var timeout = action.TimeoutMilliseconds > 0 ? action.TimeoutMilliseconds : 10000;
            var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
            var waitForAbsence = string.Equals(action.ConditionType, "target_not_exists", StringComparison.Ordinal);
            var consecutiveAbsenceCount = 0;
            ReplayLocatorResult latest = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                latest = locatorEngine.FindWithWait(root, targetProcessName, action.Locator, 0, cancellationToken);
                if (!waitForAbsence && latest.Found)
                {
                    latest.Diagnostics = "wait=target_exists;timeout_ms=" + timeout + ";" + latest.Diagnostics;
                    return latest;
                }
                if (waitForAbsence && !latest.Found)
                {
                    consecutiveAbsenceCount++;
                    if (consecutiveAbsenceCount >= 2)
                    {
                        latest.LocatorUsed = "target_not_found";
                        latest.Diagnostics = "wait=target_not_exists;timeout_ms=" + timeout + ";" + latest.Diagnostics;
                        return latest;
                    }
                }
                else
                {
                    consecutiveAbsenceCount = 0;
                }
                if (DateTime.UtcNow >= deadline) break;
                if (cancellationToken.WaitHandle.WaitOne(200)) cancellationToken.ThrowIfCancellationRequested();
            } while (true);
            throw new InvalidOperationException(
                "等待超时：目标控件未在 " + timeout + "ms 内" + (waitForAbsence ? "消失" : "出现") +
                "；" + (latest == null ? string.Empty : latest.Diagnostics));
        }

        private static AutomationElement FindElementWithWait(
            AutomationElement root,
            LocatorBundle locator,
            int timeoutMilliseconds,
            CancellationToken cancellationToken,
            out string locatorUsed)
        {
            locatorUsed = null;
            if (locator == null) return null;
            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(0, timeoutMilliseconds));
            do
            {
                var element = FindElement(root, locator, out locatorUsed);
                if (element != null) return element;
                if (DateTime.UtcNow >= deadline) break;
                if (cancellationToken.WaitHandle.WaitOne(200)) cancellationToken.ThrowIfCancellationRequested();
            } while (true);
            return null;
        }

        private static AutomationElement FindElementWithWaitAcrossProcessWindows(
            AutomationElement primaryRoot,
            string processName,
            LocatorBundle locator,
            int timeoutMilliseconds,
            CancellationToken cancellationToken,
            out string locatorUsed)
        {
            locatorUsed = null;
            if (locator == null) return null;
            var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(0, timeoutMilliseconds));
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var candidate in EnumerateProcessRoots(primaryRoot, processName))
                {
                    string candidateLocator;
                    var element = FindElement(candidate, locator, out candidateLocator);
                    if (element == null) continue;
                    var title = Read(() => candidate.Current.Name);
                    locatorUsed = "window=" + (string.IsNullOrWhiteSpace(title) ? "<untitled>" : title) + ";" + candidateLocator;
                    return element;
                }
                if (DateTime.UtcNow >= deadline) break;
                if (cancellationToken.WaitHandle.WaitOne(200)) cancellationToken.ThrowIfCancellationRequested();
            } while (true);
            return null;
        }

        private static IEnumerable<AutomationElement> EnumerateProcessRoots(AutomationElement primaryRoot, string processName)
        {
            var seen = new HashSet<long>();
            if (primaryRoot != null)
            {
                var primaryHandle = 0;
                try { primaryHandle = primaryRoot.Current.NativeWindowHandle; }
                catch (ElementNotAvailableException) { }
                if (primaryHandle != 0) seen.Add(primaryHandle);
                yield return primaryRoot;
            }
            foreach (var handle in NativeMethods.FindVisibleWindowsByProcessName(processName))
            {
                var numericHandle = handle.ToInt64();
                if (!seen.Add(numericHandle)) continue;
                AutomationElement root;
                try { root = AutomationElement.FromHandle(handle); }
                catch (ElementNotAvailableException) { continue; }
                if (root != null) yield return root;
            }
        }

        private static string DescribeExecutionMode(string actionType, string locatorUsed)
        {
            if (string.Equals(actionType, "key_press", StringComparison.Ordinal)) return "keyboard";
            if (!string.IsNullOrWhiteSpace(locatorUsed) &&
                locatorUsed.IndexOf("coordinate_safe_fallback", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "coordinate_fallback";
            }
            return "uia";
        }

        private static bool IsPointerAction(string actionType)
        {
            return string.Equals(actionType, "click", StringComparison.Ordinal) ||
                string.Equals(actionType, "right_click", StringComparison.Ordinal);
        }

        private static bool TryRevealTargetByHover(
            AutomationElement root,
            IntPtr targetWindow,
            string targetProcessName,
            ReplayTarget replayTarget,
            ReplayAction action,
            ReplayLocatorEngine locatorEngine,
            bool enforceForeground,
            CancellationToken cancellationToken,
            out ReplayLocatorResult lookup,
            out string diagnostics)
        {
            lookup = new ReplayLocatorResult();
            diagnostics = "hover_reveal=unavailable";
            int x;
            int y;
            if (action.HoverAnchorLocator != null)
            {
                var anchorLookup = string.Equals(action.HoverAnchorMatchMode, "first_chat_row", StringComparison.Ordinal)
                    ? FindFirstChatRow(root)
                    : locatorEngine.FindWithWait(root, targetProcessName, action.HoverAnchorLocator, 2000, cancellationToken);
                if (anchorLookup.Found && TryGetElementCenter(anchorLookup.Element, out x, out y))
                {
                    var anchorWindow = anchorLookup.WindowHandle != IntPtr.Zero
                        ? anchorLookup.WindowHandle
                        : targetWindow;
                    EnsureSafeMousePoint(x, y, anchorWindow, targetProcessName, enforceForeground);
                    var nudgeX = Math.Max(0, x - 6);
                    NativeMethods.SetCursorPos(nudgeX, y);
                    if (cancellationToken.WaitHandle.WaitOne(80)) cancellationToken.ThrowIfCancellationRequested();
                    NativeMethods.SetCursorPos(x, y);
                    diagnostics = "hover_reveal=semantic_anchor(mode=" +
                        (action.HoverAnchorMatchMode ?? "locator") + ";" +
                        DescribeLocator(action.HoverAnchorLocator) + ")";
                    if (cancellationToken.WaitHandle.WaitOne(800)) cancellationToken.ThrowIfCancellationRequested();
                    lookup = locatorEngine.FindWithWait(root, targetProcessName, action.Locator, 7300, cancellationToken);
                    return lookup.Found;
                }
            }
            if (enforceForeground) EnsureReplayForeground(targetWindow, targetProcessName);
            if (!TryGetFallbackPoint(targetWindow, replayTarget, action, out x, out y)) return false;
            EnsureSafeMousePoint(x, y, targetWindow, targetProcessName, enforceForeground);
            NativeMethods.SetCursorPos(x, y);
            diagnostics = "hover_reveal=window_relative(" + x + "," + y + ")";
            if (cancellationToken.WaitHandle.WaitOne(500)) cancellationToken.ThrowIfCancellationRequested();
            lookup = locatorEngine.FindWithWait(root, targetProcessName, action.Locator, 7300, cancellationToken);
            return lookup.Found;
        }

        private static ReplayLocatorResult FindFirstChatRow(AutomationElement root)
        {
            if (root == null) return new ReplayLocatorResult { Diagnostics = "mode=first_chat_row;root=none" };
            var visible = EnumerateElements(root, false)
                .Select(element => new
                {
                    Element = element,
                    Name = Read(() => element.Current.Name),
                    ClassName = Read(() => element.Current.ClassName),
                    Bounds = ReadValue(() => element.Current.BoundingRectangle, System.Windows.Rect.Empty),
                    Offscreen = ReadValue(() => element.Current.IsOffscreen, true)
                })
                .Where(item => !item.Offscreen && !item.Bounds.IsEmpty && item.Bounds.Width > 0 && item.Bounds.Height > 0)
                .ToList();
            var chatHeader = visible
                .Where(item => string.Equals(item.Name, "\u804a\u5929", StringComparison.Ordinal))
                .OrderByDescending(item => item.Bounds.Width)
                .ThenBy(item => item.Bounds.Top)
                .FirstOrDefault();
            if (chatHeader == null)
            {
                return new ReplayLocatorResult { Diagnostics = "mode=first_chat_row;header=not_found" };
            }
            var row = visible
                .Where(item => BaseClassName(item.ClassName) == "Item_chatOrProjectItem")
                .Where(item => item.Bounds.Top >= chatHeader.Bounds.Bottom - 2)
                .Where(item => item.Bounds.Left >= chatHeader.Bounds.Left - 8 &&
                    item.Bounds.Right <= chatHeader.Bounds.Right + 8)
                .OrderBy(item => item.Bounds.Top)
                .FirstOrDefault();
            if (row == null)
            {
                return new ReplayLocatorResult { Diagnostics = "mode=first_chat_row;row=not_found" };
            }
            return new ReplayLocatorResult
            {
                Element = row.Element,
                WindowHandle = new IntPtr(ReadValue(() => root.Current.NativeWindowHandle, 0)),
                LocatorUsed = "first_chat_row",
                Diagnostics = "mode=first_chat_row;header_top=" + chatHeader.Bounds.Top + ";row_top=" + row.Bounds.Top
            };
        }

        private static bool EvaluateAssertion(
            AutomationElement root,
            AutomationElement element,
            bool hasTargetLocator,
            ReplayAssertion assertion,
            out string actual)
        {
            actual = null;
            var scope = ResolveAssertionScope(root, element, assertion);
            switch (assertion.Type)
            {
                case "target_exists":
                    actual = element == null ? "not_exists" : "exists";
                    return element != null;
                case "target_not_exists":
                    actual = element == null ? "not_exists" : "exists";
                    return element == null;
                case "text_contains":
                    if (element != null)
                    {
                        actual = ReadElementText(element);
                        if (actual.IndexOf(assertion.Expected ?? string.Empty, StringComparison.Ordinal) >= 0) return true;
                        // Chromium commonly exposes clickable containers as an
                        // unnamed Group and their visible label as a child Text.
                        // Keep the assertion scoped to the selected target, but
                        // search its descendants before declaring failure.
                        return FindText(element, assertion.Expected, false, out actual);
                    }
                    if (hasTargetLocator)
                    {
                        actual = "target_not_found";
                        return false;
                    }
                    return FindText(root, assertion.Expected, false, out actual);
                case "text_equals":
                    if (element != null)
                    {
                        actual = ReadElementText(element);
                        if (string.Equals(actual, assertion.Expected ?? string.Empty, StringComparison.Ordinal)) return true;
                        return FindText(element, assertion.Expected, true, out actual);
                    }
                    if (hasTargetLocator)
                    {
                        actual = "target_not_found";
                        return false;
                    }
                    return FindText(root, assertion.Expected, true, out actual);
                case "property_equals":
                    if (element == null)
                    {
                        actual = "target_not_found";
                        return false;
                    }
                    actual = ReadElementProperty(element, assertion.Property);
                    return string.Equals(actual, assertion.Expected ?? string.Empty, StringComparison.Ordinal);
                case "keywords_match_count":
                    if (scope == null)
                    {
                        actual = "scope_not_found";
                        return false;
                    }
                    return EvaluateKeywordMatches(scope, assertion, out actual);
                case "descendant_count":
                    if (scope == null)
                    {
                        actual = "scope_not_found";
                        return false;
                    }
                    return EvaluateDescendantCount(scope, assertion, out actual);
                case "table_dimensions":
                    if (scope == null)
                    {
                        actual = "table_not_found";
                        return false;
                    }
                    return EvaluateTableDimensions(scope, assertion, out actual);
                case "horizontal_bounds_within_window":
                    if (scope == null)
                    {
                        actual = "scope_not_found";
                        return false;
                    }
                    return EvaluateHorizontalBounds(root, scope, assertion, out actual);
                case "descendants_within_bounds":
                    if (scope == null)
                    {
                        actual = "scope_not_found";
                        return false;
                    }
                    return EvaluateDescendantBounds(scope, assertion, out actual);
                default:
                    throw new InvalidOperationException("不支持的断言类型：" + assertion.Type);
            }
        }

        private static AutomationElement ResolveAssertionScope(
            AutomationElement root,
            AutomationElement target,
            ReplayAssertion assertion)
        {
            if (string.Equals(assertion.Scope, "window", StringComparison.OrdinalIgnoreCase)) return root;
            if (!string.Equals(assertion.Scope, "nearest_ancestor", StringComparison.OrdinalIgnoreCase)) return target;
            var current = target;
            for (var depth = 0; current != null && depth < 30; depth++)
            {
                if (MatchesControlType(current, assertion.ScopeControlType)) return current;
                try
                {
                    current = TreeWalker.RawViewWalker.GetParent(current);
                }
                catch (ElementNotAvailableException)
                {
                    return null;
                }
            }
            return null;
        }

        private static bool EvaluateKeywordMatches(
            AutomationElement scope,
            ReplayAssertion assertion,
            out string actual)
        {
            var keywords = (assertion.Keywords ?? new List<string>())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (keywords.Count == 0 || assertion.MinimumMatches <= 0)
            {
                actual = "invalid_configuration: keywords and minimum_matches are required";
                return false;
            }
            if (assertion.MinimumMatches > keywords.Count)
            {
                actual = "invalid_configuration: minimum_matches exceeds keyword count";
                return false;
            }
            var text = string.Join("\n", EnumerateElements(scope, true).Select(ReadElementText));
            var matched = keywords.Where(item => text.IndexOf(item, StringComparison.Ordinal) >= 0).ToList();
            var minimum = assertion.MinimumMatches > 0 ? assertion.MinimumMatches : keywords.Count;
            actual = "matched=" + matched.Count + "/" + keywords.Count + "; minimum=" + minimum +
                "; keywords=" + string.Join(",", matched);
            return matched.Count >= minimum;
        }

        private static bool EvaluateDescendantCount(
            AutomationElement scope,
            ReplayAssertion assertion,
            out string actual)
        {
            if (string.IsNullOrWhiteSpace(assertion.DescendantControlType) || assertion.ExpectedCount < 0)
            {
                actual = "invalid_configuration: descendant_control_type and expected_count are required";
                return false;
            }
            var count = EnumerateElements(scope, false)
                .Count(item => MatchesControlType(item, assertion.DescendantControlType));
            var operation = string.IsNullOrWhiteSpace(assertion.CountOperator) ? "equals" : assertion.CountOperator;
            actual = "count=" + count + "; expected=" + operation + " " + assertion.ExpectedCount;
            switch (operation)
            {
                case "at_least": return count >= assertion.ExpectedCount;
                case "at_most": return count <= assertion.ExpectedCount;
                default: return count == assertion.ExpectedCount;
            }
        }

        private static bool EvaluateTableDimensions(
            AutomationElement table,
            ReplayAssertion assertion,
            out string actual)
        {
            if (assertion.ExpectedRows < 0 || assertion.ExpectedColumns <= 0)
            {
                actual = "invalid_configuration: expected_rows and expected_columns are required";
                return false;
            }
            var rows = DirectChildren(table).ToList();
            var columns = rows.Count == 0 ? 0 : rows.Max(row => DirectChildren(row).Count());
            var dataRows = rows.Count > 0 ? rows.Count - 1 : 0;
            actual = "data_rows=" + dataRows + "; columns=" + columns + "; total_rows=" + rows.Count;
            return dataRows == assertion.ExpectedRows && columns == assertion.ExpectedColumns;
        }

        private static bool EvaluateHorizontalBounds(
            AutomationElement root,
            AutomationElement scope,
            ReplayAssertion assertion,
            out string actual)
        {
            var windowBounds = ReadBounds(root);
            if (windowBounds.IsEmpty)
            {
                actual = "window_bounds_unavailable";
                return false;
            }
            var tolerance = Math.Max(0, assertion.BoundsTolerancePixels);
            var overflow = EnumerateElements(scope, true)
                .Select(ReadBounds)
                .Where(bounds => !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
                .Count(bounds => bounds.Left < windowBounds.Left - tolerance || bounds.Right > windowBounds.Right + tolerance);
            actual = "horizontal_overflow_count=" + overflow + "; tolerance_px=" + tolerance;
            return overflow == 0;
        }

        private static bool EvaluateDescendantBounds(
            AutomationElement scope,
            ReplayAssertion assertion,
            out string actual)
        {
            var containerBounds = ReadBounds(scope);
            if (containerBounds.IsEmpty)
            {
                actual = "container_bounds_unavailable";
                return false;
            }
            var tolerance = Math.Max(0, assertion.BoundsTolerancePixels);
            var descendants = EnumerateElements(scope, false)
                .Select(ReadBounds)
                .Where(bounds => !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
                .ToList();
            if (descendants.Count == 0)
            {
                actual = "no_measurable_descendants";
                return false;
            }
            var overflow = descendants.Count(bounds => bounds.Left < containerBounds.Left - tolerance ||
                    bounds.Top < containerBounds.Top - tolerance ||
                    bounds.Right > containerBounds.Right + tolerance ||
                    bounds.Bottom > containerBounds.Bottom + tolerance);
            actual = "descendant_count=" + descendants.Count + "; overflow_count=" + overflow + "; tolerance_px=" + tolerance;
            return overflow == 0;
        }

        private static IEnumerable<AutomationElement> EnumerateElements(AutomationElement root, bool includeRoot)
        {
            var queue = new Queue<AutomationElement>();
            if (includeRoot) queue.Enqueue(root);
            else foreach (var child in DirectChildren(root)) queue.Enqueue(child);
            var visited = 0;
            while (queue.Count > 0 && visited < 6000)
            {
                var current = queue.Dequeue();
                visited++;
                yield return current;
                foreach (var child in DirectChildren(current)) queue.Enqueue(child);
            }
        }

        private static IEnumerable<AutomationElement> DirectChildren(AutomationElement element)
        {
            AutomationElement child;
            try
            {
                child = TreeWalker.RawViewWalker.GetFirstChild(element);
            }
            catch (ElementNotAvailableException)
            {
                yield break;
            }
            while (child != null)
            {
                yield return child;
                try
                {
                    child = TreeWalker.RawViewWalker.GetNextSibling(child);
                }
                catch (ElementNotAvailableException)
                {
                    yield break;
                }
            }
        }

        private static System.Windows.Rect ReadBounds(AutomationElement element)
        {
            try
            {
                return element.Current.BoundingRectangle;
            }
            catch (ElementNotAvailableException)
            {
                return System.Windows.Rect.Empty;
            }
        }

        private static bool FindText(
            AutomationElement root,
            string expected,
            bool equals,
            out string actual)
        {
            actual = null;
            var queue = new Queue<AutomationElement>();
            queue.Enqueue(root);
            var visited = 0;
            while (queue.Count > 0 && visited < 6000)
            {
                var current = queue.Dequeue();
                visited++;
                var text = ReadElementText(current);
                var matched = equals
                    ? string.Equals(text, expected ?? string.Empty, StringComparison.Ordinal)
                    : text.IndexOf(expected ?? string.Empty, StringComparison.Ordinal) >= 0;
                if (matched)
                {
                    actual = text;
                    return true;
                }
                AutomationElement child;
                try
                {
                    child = TreeWalker.RawViewWalker.GetFirstChild(current);
                }
                catch (ElementNotAvailableException)
                {
                    continue;
                }
                while (child != null)
                {
                    queue.Enqueue(child);
                    try
                    {
                        child = TreeWalker.RawViewWalker.GetNextSibling(child);
                    }
                    catch (ElementNotAvailableException)
                    {
                        break;
                    }
                }
            }
            actual = "text_not_found";
            return false;
        }

        private static string ReadElementText(AutomationElement element)
        {
            if (element == null) return string.Empty;
            object pattern;
            try
            {
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern))
                {
                    var value = ((ValuePattern)pattern).Current.Value;
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
                if (element.TryGetCurrentPattern(TextPattern.Pattern, out pattern))
                {
                    var value = ((TextPattern)pattern).DocumentRange.GetText(-1);
                    if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
                }
                return element.Current.Name ?? string.Empty;
            }
            catch (ElementNotAvailableException)
            {
                return string.Empty;
            }
        }

        private static string ReadElementProperty(AutomationElement element, string property)
        {
            switch (property)
            {
                case "name": return Read(() => element.Current.Name);
                case "automation_id": return Read(() => element.Current.AutomationId);
                case "class_name": return Read(() => element.Current.ClassName);
                case "control_type": return Read(() => element.Current.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty));
                case "enabled": return element.Current.IsEnabled ? "true" : "false";
                case "value":
                    object valuePatternObject;
                    return element.TryGetCurrentPattern(ValuePattern.Pattern, out valuePatternObject)
                        ? ((ValuePattern)valuePatternObject).Current.Value ?? string.Empty
                        : "<UIA Value不可用>";
                default: throw new InvalidOperationException("不支持的断言属性：" + property);
            }
        }

        internal static AutomationElement FindElement(
            AutomationElement root,
            LocatorBundle locator,
            out string locatorUsed)
        {
            locatorUsed = null;
            if (root == null || locator == null) return null;

            if (!string.IsNullOrWhiteSpace(locator.SectionStartName))
            {
                var scoped = FindSectionScopedMatch(root, locator);
                if (scoped != null)
                {
                    locatorUsed = "section=" + locator.SectionStartName + ".." +
                        (locator.SectionEndName ?? "end") + ";identity=" + DescribeLocator(locator);
                }
                return scoped;
            }

            if (!string.IsNullOrWhiteSpace(locator.DescendantName))
            {
                var container = FindContainerByNamedDescendant(root, locator);
                if (container != null)
                {
                    locatorUsed = "descendant_name=" + locator.DescendantName + ";container=" +
                        (locator.ClassName ?? locator.ControlType);
                }
                return container;
            }

            if (!string.IsNullOrWhiteSpace(locator.AutomationId))
            {
                var element = FindFirst(root, AutomationElement.AutomationIdProperty, locator.AutomationId, null);
                if (element != null && MatchesProvidedIdentity(element, locator))
                {
                    locatorUsed = "automation_id=" + locator.AutomationId;
                    return element;
                }
            }

            if (!string.IsNullOrWhiteSpace(locator.Name))
            {
                // A Chromium page may keep multiple visible UIA nodes with the
                // same accessible name (for example "派设置" in stale/right
                // panels). When recording supplied an ancestor path, select by
                // the full structural score instead of taking the first name
                // match and silently ignoring its owning panel.
                var element = locator.AncestorPath != null && locator.AncestorPath.Count > 1
                    ? FindBestStructuralMatch(root, locator)
                    : FindFirst(root, AutomationElement.NameProperty, locator.Name, null);
                if (element != null && MatchesProvidedIdentity(element, locator))
                {
                    locatorUsed = locator.AncestorPath != null && locator.AncestorPath.Count > 1
                        ? "name=" + locator.Name + "+ancestor_path"
                        : "name=" + locator.Name;
                    return element;
                }
            }

            string anchorDescription;
            var anchored = FindAnchoredMatch(root, locator, out anchorDescription);
            if (anchored != null)
            {
                locatorUsed = "uia_ancestor_anchor=" + anchorDescription;
                return anchored;
            }

            if (CanUseMutableNameFallback(locator))
            {
                var mutableNameLocator = new LocatorBundle
                {
                    ClassName = locator.ClassName,
                    ControlType = locator.ControlType,
                    AncestorPath = locator.AncestorPath
                };
                var mutableNameMatch = FindAnchoredMatch(root, mutableNameLocator, out anchorDescription);
                if (mutableNameMatch != null)
                {
                    locatorUsed = "mutable_name_fallback=" + locator.Name + ";uia_ancestor_anchor=" + anchorDescription;
                    return mutableNameMatch;
                }
            }

            var best = FindBestStructuralMatch(root, locator);
            if (best != null)
            {
                locatorUsed = "uia_structural";
                return best;
            }
            return null;
        }

        internal static AutomationElement FindElementStrictIdentity(
            AutomationElement root,
            LocatorBundle locator,
            out string locatorUsed)
        {
            locatorUsed = null;
            if (root == null || locator == null) return null;
            if (!string.IsNullOrWhiteSpace(locator.AutomationId))
            {
                var byId = FindFirst(root, AutomationElement.AutomationIdProperty, locator.AutomationId, locator.ControlType);
                if (byId != null)
                {
                    locatorUsed = "strict_automation_id=" + locator.AutomationId;
                    return byId;
                }
            }
            if (!string.IsNullOrWhiteSpace(locator.Name))
            {
                var byName = FindFirst(root, AutomationElement.NameProperty, locator.Name, locator.ControlType);
                if (byName != null)
                {
                    locatorUsed = "strict_name=" + locator.Name;
                    return byName;
                }
            }
            return null;
        }

        private static bool CanUseMutableNameFallback(LocatorBundle locator)
        {
            return locator != null &&
                string.IsNullOrWhiteSpace(locator.AutomationId) &&
                !string.IsNullOrWhiteSpace(locator.Name) &&
                !string.IsNullOrWhiteSpace(locator.ClassName) &&
                locator.AncestorPath != null &&
                locator.AncestorPath.Count > 1 &&
                string.Equals(locator.ControlType, "Edit", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAmbiguousSharedSidebarRowLocator(LocatorBundle locator)
        {
            if (locator == null || !string.IsNullOrWhiteSpace(locator.AutomationId) ||
                !string.IsNullOrWhiteSpace(locator.Name) || !string.IsNullOrWhiteSpace(locator.SectionStartName)) return false;
            var className = BaseClassName(locator.ClassName);
            return string.Equals(className, "Item_chatOrProjectItem", StringComparison.Ordinal) ||
                string.Equals(className, "ProjectsSection_item", StringComparison.Ordinal);
        }

        private static bool IsAmbiguousConversationItemLocator(LocatorBundle locator)
        {
            if (locator == null || !string.IsNullOrWhiteSpace(locator.AutomationId) ||
                !string.IsNullOrWhiteSpace(locator.Name) || !string.IsNullOrWhiteSpace(locator.DescendantName)) return false;
            return string.Equals(BaseClassName(locator.ClassName), "item_itemContainer", StringComparison.Ordinal);
        }

        private static AutomationElement FindContainerByNamedDescendant(AutomationElement root, LocatorBundle locator)
        {
            AutomationElement descendant = null;
            try
            {
                var matches = root.FindAll(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, locator.DescendantName));
                foreach (AutomationElement candidate in matches)
                {
                    if (!string.IsNullOrWhiteSpace(locator.DescendantControlType) &&
                        !MatchesControlType(candidate, locator.DescendantControlType)) continue;
                    var current = candidate;
                    for (var depth = 0; current != null && depth < 12; depth++)
                    {
                        if (MatchesProvidedIdentity(current, new LocatorBundle
                        {
                            AutomationId = locator.AutomationId,
                            Name = locator.Name,
                            ClassName = locator.ClassName,
                            ControlType = locator.ControlType
                        })) return current;
                        try { current = TreeWalker.RawViewWalker.GetParent(current); }
                        catch (ElementNotAvailableException) { break; }
                    }
                    descendant = candidate;
                }
            }
            catch (ElementNotAvailableException)
            {
                return null;
            }
            return null;
        }

        private static AutomationElement FindSectionScopedMatch(AutomationElement root, LocatorBundle locator)
        {
            var visible = EnumerateElements(root, false)
                .Select(element => new
                {
                    Element = element,
                    Name = Read(() => element.Current.Name),
                    Bounds = ReadValue(() => element.Current.BoundingRectangle, System.Windows.Rect.Empty),
                    Offscreen = ReadValue(() => element.Current.IsOffscreen, true)
                })
                .Where(item => !item.Offscreen && !item.Bounds.IsEmpty && item.Bounds.Width > 0 && item.Bounds.Height > 0)
                .ToList();
            var start = visible
                .Where(item => string.Equals(item.Name, locator.SectionStartName, StringComparison.Ordinal))
                .OrderByDescending(item => item.Bounds.Width)
                .ThenBy(item => item.Bounds.Top)
                .FirstOrDefault();
            if (start == null) return null;
            var end = string.IsNullOrWhiteSpace(locator.SectionEndName)
                ? null
                : visible
                    .Where(item => string.Equals(item.Name, locator.SectionEndName, StringComparison.Ordinal) &&
                        item.Bounds.Top > start.Bounds.Bottom)
                    .OrderByDescending(item => item.Bounds.Width)
                    .ThenBy(item => item.Bounds.Top)
                    .FirstOrDefault();
            var bottom = end == null ? double.MaxValue : end.Bounds.Top;
            return visible
                .Where(item => item.Bounds.Top >= start.Bounds.Bottom - 2 && item.Bounds.Top < bottom)
                .Where(item => MatchesProvidedIdentity(item.Element, locator))
                .OrderByDescending(item => ReplayElementScore(item.Element))
                .ThenBy(item => item.Bounds.Top)
                .Select(item => item.Element)
                .FirstOrDefault();
        }

        private static AutomationElement FindAnchoredMatch(
            AutomationElement root,
            LocatorBundle locator,
            out string anchorDescription)
        {
            anchorDescription = null;
            if (locator.AncestorPath == null || locator.AncestorPath.Count < 2) return null;

            var candidates = locator.AncestorPath
                .Take(locator.AncestorPath.Count - 1)
                .Where(HasStableIdentity)
                .OrderByDescending(AnchorStrength)
                .ToList();
            foreach (var segment in candidates)
            {
                AutomationElement anchor = null;
                if (!string.IsNullOrWhiteSpace(segment.AutomationId))
                {
                    anchor = FindFirst(root, AutomationElement.AutomationIdProperty, segment.AutomationId, segment.ControlType);
                    anchorDescription = "automation_id=" + segment.AutomationId;
                }
                if (anchor == null && !string.IsNullOrWhiteSpace(segment.Name))
                {
                    var named = FindFirst(root, AutomationElement.NameProperty, segment.Name, segment.ControlType);
                    if (named != null && MatchesControlType(named, segment.ControlType)) anchor = named;
                    anchorDescription = "name=" + segment.Name;
                }
                if (anchor == null && !string.IsNullOrWhiteSpace(segment.ClassName))
                {
                    anchor = FindBestStructuralMatch(root, new LocatorBundle
                    {
                        ClassName = segment.ClassName,
                        ControlType = segment.ControlType
                    });
                    anchorDescription = "class_name=" + segment.ClassName;
                }
                if (anchor == null) continue;

                if (IsGenericLeaf(locator)) return anchor;
                var descendant = FindBestStructuralMatch(anchor, new LocatorBundle
                {
                    AutomationId = locator.AutomationId,
                    Name = locator.Name,
                    ClassName = locator.ClassName,
                    ControlType = locator.ControlType
                });
                if (descendant != null) return descendant;
            }
            anchorDescription = null;
            return null;
        }

        private static bool HasStableIdentity(LocatorSegment segment)
        {
            return segment != null && (!string.IsNullOrWhiteSpace(segment.AutomationId) ||
                !string.IsNullOrWhiteSpace(segment.Name) || !string.IsNullOrWhiteSpace(segment.ClassName));
        }

        private static int AnchorStrength(LocatorSegment segment)
        {
            if (!string.IsNullOrWhiteSpace(segment.AutomationId)) return 300;
            if (!string.IsNullOrWhiteSpace(segment.Name)) return 200;
            return 100;
        }

        private static bool IsGenericLeaf(LocatorBundle locator)
        {
            if (locator == null || !string.IsNullOrWhiteSpace(locator.AutomationId) ||
                !string.IsNullOrWhiteSpace(locator.Name) || !string.IsNullOrWhiteSpace(locator.ClassName)) return false;
            return string.Equals(locator.ControlType, "Image", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(locator.ControlType, "Group", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(locator.ControlType, "Pane", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(locator.ControlType, "Custom", StringComparison.OrdinalIgnoreCase);
        }

        private static AutomationElement FindFirst(
            AutomationElement root,
            AutomationProperty property,
            string value,
            string expectedControlType = null)
        {
            try
            {
                var candidates = root.FindAll(
                    TreeScope.Descendants,
                    new PropertyCondition(property, value));
                return candidates.Cast<AutomationElement>()
                    .Where(element => IsUsableReplayElement(root, element, expectedControlType))
                    .OrderByDescending(ReplayElementScore)
                    .FirstOrDefault();
            }
            catch (ElementNotAvailableException)
            {
                return null;
            }
        }

        private static bool IsUsableReplayElement(
            AutomationElement root,
            AutomationElement element,
            string expectedControlType)
        {
            if (element == null || !MatchesControlType(element, expectedControlType)) return false;
            try
            {
                if (element.Current.IsOffscreen) return false;
                var bounds = element.Current.BoundingRectangle;
                var rootBounds = root.Current.BoundingRectangle;
                if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0 || rootBounds.IsEmpty) return false;
                var centerX = bounds.Left + bounds.Width / 2;
                var centerY = bounds.Top + bounds.Height / 2;
                return centerX >= rootBounds.Left && centerX <= rootBounds.Right &&
                    centerY >= rootBounds.Top && centerY <= rootBounds.Bottom;
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static int ReplayElementScore(AutomationElement element)
        {
            try
            {
                var score = element.Current.IsEnabled ? 40 : 0;
                object pattern;
                if (element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) score += 100;
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern)) score += 80;
                if (element.Current.IsKeyboardFocusable) score += 20;
                return score;
            }
            catch (ElementNotAvailableException)
            {
                return int.MinValue;
            }
        }

        private static AutomationElement FindBestStructuralMatch(
            AutomationElement root,
            LocatorBundle locator)
        {
            AutomationElement best = null;
            var bestScore = 0;
            var queue = new Queue<AutomationElement>();
            queue.Enqueue(root);
            var visited = 0;
            while (queue.Count > 0 && visited < 6000)
            {
                var current = queue.Dequeue();
                visited++;
                var score = Score(root, current, locator);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = current;
                }

                AutomationElement child;
                try
                {
                    child = TreeWalker.RawViewWalker.GetFirstChild(current);
                }
                catch (ElementNotAvailableException)
                {
                    continue;
                }
                while (child != null)
                {
                    queue.Enqueue(child);
                    try
                    {
                        child = TreeWalker.RawViewWalker.GetNextSibling(child);
                    }
                    catch (ElementNotAvailableException)
                    {
                        break;
                    }
                }
            }
            return bestScore >= 40 ? best : null;
        }

        private static int Score(AutomationElement scopeRoot, AutomationElement element, LocatorBundle locator)
        {
            try
            {
                var bounds = element.Current.BoundingRectangle;
                var scopeBounds = scopeRoot.Current.BoundingRectangle;
                if (element.Current.IsOffscreen || bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0 || scopeBounds.IsEmpty) return int.MinValue;
                var centerX = bounds.Left + bounds.Width / 2;
                var centerY = bounds.Top + bounds.Height / 2;
                if (centerX < scopeBounds.Left || centerX > scopeBounds.Right ||
                    centerY < scopeBounds.Top || centerY > scopeBounds.Bottom) return int.MinValue;
            }
            catch (ElementNotAvailableException)
            {
                return int.MinValue;
            }
            var score = 0;
            var automationId = Read(() => element.Current.AutomationId);
            var name = Read(() => element.Current.Name);
            var className = Read(() => element.Current.ClassName);
            var controlType = Read(() => element.Current.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty));
            if (!MatchesProvidedIdentity(automationId, name, className, controlType, locator)) return int.MinValue;
            if (!string.IsNullOrWhiteSpace(locator.AutomationId)) score += 120;
            if (!string.IsNullOrWhiteSpace(locator.Name)) score += 70;
            if (!string.IsNullOrWhiteSpace(locator.ClassName) &&
                !string.IsNullOrWhiteSpace(className) &&
                className.IndexOf(locator.ClassName, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 45;
                // Older recordings trimmed semantic BEM suffixes such as
                // __header and therefore matched both the outer COT container
                // and its clickable header. Prefer the more specific descendant
                // class over the broad container for those legacy locators.
                var firstClass = className.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
                if (firstClass.StartsWith(locator.ClassName + "__", StringComparison.OrdinalIgnoreCase)) score += 25;
            }
            if (!string.IsNullOrWhiteSpace(locator.ControlType) &&
                string.Equals(locator.ControlType, controlType, StringComparison.OrdinalIgnoreCase)) score += 30;
            score += ScoreAncestorPath(element, locator.AncestorPath);
            return score;
        }

        internal static bool MatchesProvidedIdentity(AutomationElement element, LocatorBundle locator)
        {
            if (element == null || locator == null) return false;
            return MatchesProvidedIdentity(
                Read(() => element.Current.AutomationId),
                Read(() => element.Current.Name),
                Read(() => element.Current.ClassName),
                Read(() => element.Current.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty)),
                locator);
        }

        private static bool MatchesProvidedIdentity(
            string automationId,
            string name,
            string className,
            string controlType,
            LocatorBundle locator)
        {
            var hasStrongIdentity = !string.IsNullOrWhiteSpace(locator.AutomationId) ||
                !string.IsNullOrWhiteSpace(locator.Name);
            if (!string.IsNullOrWhiteSpace(locator.AutomationId) &&
                !string.Equals(locator.AutomationId, automationId, StringComparison.Ordinal)) return false;
            if (!string.IsNullOrWhiteSpace(locator.Name) &&
                !string.Equals(locator.Name, name, StringComparison.Ordinal)) return false;
            if (!hasStrongIdentity && !string.IsNullOrWhiteSpace(locator.ClassName) &&
                (string.IsNullOrWhiteSpace(className) ||
                 className.IndexOf(locator.ClassName, StringComparison.OrdinalIgnoreCase) < 0)) return false;
            if (!string.IsNullOrWhiteSpace(locator.ControlType) &&
                !string.Equals(locator.ControlType, controlType, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        private static bool IsVisuallyHittable(AutomationElement element, double x, double y)
        {
            try
            {
                var hit = AutomationElement.FromPoint(new System.Windows.Point(x, y));
                if (hit == null) return false;
                return IsSameOrDescendant(hit, element);
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static bool IsSameOrDescendant(AutomationElement element, AutomationElement ancestor)
        {
            var current = element;
            for (var depth = 0; current != null && depth < 80; depth++)
            {
                if (Automation.Compare(current, ancestor)) return true;
                try { current = TreeWalker.RawViewWalker.GetParent(current); }
                catch (ElementNotAvailableException) { return false; }
                catch (InvalidOperationException) { return false; }
            }
            return false;
        }

        private static int ScoreAncestorPath(AutomationElement element, IList<LocatorSegment> expectedPath)
        {
            if (element == null || expectedPath == null || expectedPath.Count == 0) return 0;
            var actual = new List<AutomationElement>();
            var current = element;
            for (var depth = 0; current != null && depth < 12; depth++)
            {
                actual.Add(current);
                try { current = TreeWalker.RawViewWalker.GetParent(current); }
                catch (ElementNotAvailableException) { break; }
            }
            actual.Reverse();
            var score = 0;
            var actualStart = 0;
            foreach (var expected in expectedPath)
            {
                var bestIndex = -1;
                var bestSegmentScore = 0;
                for (var actualIndex = actualStart; actualIndex < actual.Count; actualIndex++)
                {
                    var segmentScore = ScoreSegment(actual[actualIndex], expected);
                    if (segmentScore <= bestSegmentScore) continue;
                    bestSegmentScore = segmentScore;
                    bestIndex = actualIndex;
                }
                if (bestIndex < 0) continue;
                score += bestSegmentScore;
                actualStart = bestIndex + 1;
            }
            return score;
        }

        private static int ScoreSegment(AutomationElement candidate, LocatorSegment expected)
        {
            if (candidate == null || expected == null) return 0;
            var automationId = Read(() => candidate.Current.AutomationId);
            var name = Read(() => candidate.Current.Name);
            var className = Read(() => candidate.Current.ClassName);
            var controlType = Read(() => candidate.Current.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty));
            var score = 0;
            var hasIdentity = HasStableIdentity(expected);
            var identityMatched = false;
            if (!string.IsNullOrWhiteSpace(expected.AutomationId) && expected.AutomationId == automationId)
            {
                score += 55;
                identityMatched = true;
            }
            if (!string.IsNullOrWhiteSpace(expected.Name) && expected.Name == name)
            {
                score += 30;
                identityMatched = true;
            }
            if (!string.IsNullOrWhiteSpace(expected.ClassName) && !string.IsNullOrWhiteSpace(className) &&
                className.IndexOf(expected.ClassName, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 20;
                identityMatched = true;
            }
            if (hasIdentity && !identityMatched) return 0;
            if (!string.IsNullOrWhiteSpace(expected.ControlType) &&
                string.Equals(expected.ControlType, controlType, StringComparison.OrdinalIgnoreCase)) score += 8;
            return score;
        }

        private static bool MatchesControlType(AutomationElement element, string expected)
        {
            if (string.IsNullOrWhiteSpace(expected)) return true;
            var actual = Read(() => element.Current.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty));
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        private static void Click(
            AutomationElement element,
            IntPtr window,
            string targetProcessName,
            ReplayTarget replayTarget,
            ReplayAction action,
            bool preferInvoke,
            bool forcePhysical,
            bool enforceForeground,
            ref string locatorUsed)
        {
            int x;
            int y;
            object pattern;
            var preferPhysical = forcePhysical || ShouldUsePhysicalClick(element);
            if (enforceForeground) EnsureReplayForeground(window, targetProcessName);
            if (preferInvoke && !preferPhysical && element != null &&
                element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
            {
                try
                {
                    ((InvokePattern)pattern).Invoke();
                    locatorUsed += "+InvokePattern";
                    return;
                }
                catch (ElementNotEnabledException)
                {
                    locatorUsed += "+InvokePatternDisabled";
                }
                catch (InvalidOperationException)
                {
                    locatorUsed += "+InvokePatternUnavailable";
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    locatorUsed += "+InvokePatternWin32Failure";
                }
            }
            if (preferPhysical && TryGetElementActionPoint(element, action, out x, out y))
            {
                EnsureSafeMousePoint(x, y, window, targetProcessName, enforceForeground);
                NativeMethods.SendMouseClick(x, y);
                locatorUsed += action.TargetRelativePoint == null
                    ? "+semantic_bounds_click"
                    : "+semantic_relative_bounds_click";
                return;
            }

            if (!forcePhysical && element != null && element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
            {
                try
                {
                    ((InvokePattern)pattern).Invoke();
                    locatorUsed += "+InvokePattern";
                    return;
                }
                catch (ElementNotEnabledException)
                {
                    locatorUsed += "+InvokePatternDisabled";
                }
                catch (InvalidOperationException)
                {
                    locatorUsed += "+InvokePatternUnavailable";
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    locatorUsed += "+InvokePatternWin32Failure";
                }
            }

            if (TryGetElementActionPoint(element, action, out x, out y))
            {
                EnsureSafeMousePoint(x, y, window, targetProcessName, enforceForeground);
                NativeMethods.SendMouseClick(x, y);
                locatorUsed += action.TargetRelativePoint == null ? "+bounds" : "+relative_bounds";
                return;
            }

            if (TryGetFallbackPoint(window, replayTarget, action, out x, out y))
            {
                if (HasSemanticLocator(action.Locator))
                {
                    throw new InvalidOperationException("未找到语义点击目标，已拒绝坐标兜底：" + DescribeLocator(action.Locator));
                }
                NativeMethods.SendMouseClick(x, y);
                locatorUsed = DescribeCoordinateFallback(replayTarget) + "_after=" + DescribeLocator(action.Locator);
                return;
            }
            throw new InvalidOperationException("无法定位点击目标，且没有可用的元宝窗口内坐标：" + DescribeLocator(action.Locator));
        }

        private static bool ShouldUsePhysicalClick(AutomationElement element)
        {
            if (element == null) return false;
            var frameworkId = Read(() => element.Current.FrameworkId);
            return string.Equals(frameworkId, "Chrome", StringComparison.OrdinalIgnoreCase);
        }

        private static void InputText(
            AutomationElement element,
            IntPtr window,
            string targetProcessName,
            ReplayTarget replayTarget,
            ReplayAction action,
            bool enforceForeground,
            ref string locatorUsed)
        {
            if (enforceForeground) EnsureReplayForeground(window, targetProcessName);
            if (element == null)
            {
                if (HasSemanticLocator(action.Locator))
                {
                    throw new InvalidOperationException("未找到语义文本输入目标，已拒绝坐标兜底：" + DescribeLocator(action.Locator));
                }
                int x;
                int y;
                if (!TryGetFallbackPoint(window, replayTarget, action, out x, out y))
                {
                    throw new InvalidOperationException("无法定位文本输入目标");
                }
                NativeMethods.SendMouseClick(x, y);
                locatorUsed = DescribeCoordinateFallback(replayTarget) + "_after=" + DescribeLocator(action.Locator);
            }
            else
            {
                if (ShouldUsePhysicalClick(element))
                {
                    int chromeX;
                    int chromeY;
                    if (!TryGetElementActionPoint(element, action, out chromeX, out chromeY))
                    {
                        throw new ElementNotAvailableException("Chrome 文本输入控件的可点击区域已经失效");
                    }
                    EnsureSafeMousePoint(chromeX, chromeY, window, targetProcessName, enforceForeground);
                    NativeMethods.SendMouseClick(chromeX, chromeY);
                    Thread.Sleep(180);
                    NativeMethods.SendSelectAll();
                    NativeMethods.SendUnicodeText(action.Value ?? string.Empty);
                    locatorUsed += "+ChromePhysicalUnicodeInput";
                    return;
                }
                try
                {
                    element.SetFocus();
                }
                catch (InvalidOperationException)
                {
                }
                int x;
                int y;
                if (TryGetElementActionPoint(element, action, out x, out y))
                {
                    EnsureSafeMousePoint(x, y, window, targetProcessName, enforceForeground);
                    NativeMethods.SendMouseClick(x, y);
                    Thread.Sleep(120);
                }
            }

            object pattern;
            if (element != null && element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern))
            {
                var valuePattern = (ValuePattern)pattern;
                if (!valuePattern.Current.IsReadOnly)
                {
                    valuePattern.SetValue(action.Value ?? string.Empty);
                    locatorUsed += "+ValuePattern";
                    return;
                }
            }

            NativeMethods.SendSelectAll();
            NativeMethods.SendUnicodeText(action.Value ?? string.Empty);
            locatorUsed += "+UnicodeInput";
        }

        private static void InputTextWithStaleRetry(
            AutomationElement root,
            ReplayLocatorEngine locatorEngine,
            AutomationElement initialElement,
            IntPtr window,
            string targetProcessName,
            ReplayTarget replayTarget,
            ReplayAction action,
            bool enforceForeground,
            CancellationToken cancellationToken,
            ref string locatorUsed)
        {
            var element = initialElement;
            var currentWindow = window;
            Exception staleError = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    InputText(
                        element,
                        currentWindow,
                        targetProcessName,
                        replayTarget,
                        action,
                        enforceForeground,
                        ref locatorUsed);
                    if (attempt > 0) locatorUsed += "+stale_retry=" + attempt;
                    return;
                }
                catch (ElementNotAvailableException error)
                {
                    staleError = error;
                }

                var refreshed = locatorEngine.FindWithWait(
                    root,
                    targetProcessName,
                    action.Locator,
                    2500,
                    cancellationToken);
                if (!refreshed.Found)
                {
                    throw new InvalidOperationException(
                        "文本输入控件在界面更新后失效，重新定位失败：" + refreshed.Diagnostics,
                        staleError);
                }
                element = refreshed.Element;
                if (refreshed.WindowHandle != IntPtr.Zero) currentWindow = refreshed.WindowHandle;
                locatorUsed = refreshed.LocatorUsed;
                if (cancellationToken.WaitHandle.WaitOne(120)) cancellationToken.ThrowIfCancellationRequested();
            }
            throw new InvalidOperationException("文本输入控件连续失效，重试 3 次后仍无法写入", staleError);
        }

        private static void RightClick(
            AutomationElement element,
            IntPtr window,
            string targetProcessName,
            ReplayTarget replayTarget,
            ReplayAction action,
            bool enforceForeground,
            ref string locatorUsed)
        {
            if (enforceForeground) EnsureReplayForeground(window, targetProcessName);
            int x;
            int y;
            if (TryGetElementActionPoint(element, action, out x, out y))
            {
                EnsureSafeMousePoint(x, y, window, targetProcessName, enforceForeground);
                NativeMethods.SendMouseRightClick(x, y);
                locatorUsed += action.TargetRelativePoint == null ? "+right_bounds" : "+right_relative_bounds";
                return;
            }
            if (TryGetFallbackPoint(window, replayTarget, action, out x, out y))
            {
                if (HasSemanticLocator(action.Locator))
                {
                    throw new InvalidOperationException("未找到语义右键目标，已拒绝坐标兜底：" + DescribeLocator(action.Locator));
                }
                NativeMethods.SendMouseRightClick(x, y);
                locatorUsed = DescribeCoordinateFallback(replayTarget) + "_right_after=" + DescribeLocator(action.Locator);
                return;
            }
            throw new InvalidOperationException("无法定位右键目标，且没有可用的元宝窗口内坐标：" + DescribeLocator(action.Locator));
        }

        private static void PressKey(string key)
        {
            switch ((key ?? "ENTER").Trim().ToUpperInvariant())
            {
                case "ENTER": NativeMethods.SendVirtualKey(0x0D); break;
                case "TAB": NativeMethods.SendVirtualKey(0x09); break;
                case "ESC":
                case "ESCAPE": NativeMethods.SendVirtualKey(0x1B); break;
                case "BACKSPACE": NativeMethods.SendVirtualKey(0x08); break;
                case "SPACE": NativeMethods.SendVirtualKey(0x20); break;
                default: throw new InvalidOperationException("暂不支持按键：" + key);
            }
        }

        private static void Scroll(IntPtr window, string targetProcessName, ReplayTarget replayTarget, ReplayAction action, ref string locatorUsed)
        {
            EnsureReplayForeground(window, targetProcessName);
            int x;
            int y;
            if (!TryGetFallbackPoint(window, replayTarget, action, out x, out y))
            {
                throw new InvalidOperationException("滚动操作缺少起始坐标");
            }
            EnsureSafeMousePoint(x, y, window, targetProcessName);
            NativeMethods.SendMouseWheel(x, y, action.WheelDelta == 0 ? -120 : action.WheelDelta);
            locatorUsed = DescribeCoordinateFallback(replayTarget) + "_wheel";
        }

        private static void Drag(IntPtr window, string targetProcessName, ReplayTarget replayTarget, ReplayAction action, ref string locatorUsed)
        {
            EnsureReplayForeground(window, targetProcessName);
            int startX;
            int startY;
            if (!TryGetFallbackPoint(window, replayTarget, action, out startX, out startY) || action.EndCoordinate == null)
            {
                throw new InvalidOperationException("拖动操作缺少起止坐标");
            }
            NativeMethods.WindowRectangle rectangle;
            if (!NativeMethods.GetWindowRect(window, out rectangle)) throw new InvalidOperationException("无法读取元宝窗口位置");
            int endX;
            int endY;
            ScaleWindowPoint(rectangle, replayTarget == null ? null : replayTarget.RecordedWindowSize, action.EndCoordinate, out endX, out endY);
            EnsureSafeMousePoint(startX, startY, window, targetProcessName);
            EnsureSafeMousePoint(endX, endY, window, targetProcessName);
            NativeMethods.SendMouseDrag(
                startX,
                startY,
                endX,
                endY,
                action.DurationMilliseconds > 0 ? action.DurationMilliseconds : 500);
            locatorUsed = DescribeCoordinateFallback(replayTarget) + "_drag";
        }

        private static bool TryGetElementCenter(AutomationElement element, out int x, out int y)
        {
            x = 0;
            y = 0;
            if (element == null) return false;
            try
            {
                if (element.Current.IsOffscreen) return false;
                var rectangle = element.Current.BoundingRectangle;
                if (rectangle.IsEmpty || rectangle.Width <= 0 || rectangle.Height <= 0) return false;
                x = (int)Math.Round(rectangle.Left + rectangle.Width / 2);
                y = (int)Math.Round(rectangle.Top + rectangle.Height / 2);
                return true;
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }
        }

        private static bool TryGetElementActionPoint(AutomationElement element, ReplayAction action, out int x, out int y)
        {
            x = 0;
            y = 0;
            if (element == null) return false;
            try
            {
                if (element.Current.IsOffscreen) return false;
                var rectangle = element.Current.BoundingRectangle;
                if (rectangle.IsEmpty || rectangle.Width <= 0 || rectangle.Height <= 0) return false;
                var relative = action == null ? null : action.TargetRelativePoint;
                var relativeX = relative == null ? 0.5 : Math.Max(0, Math.Min(1, relative.X));
                var relativeY = relative == null ? 0.5 : Math.Max(0, Math.Min(1, relative.Y));
                x = (int)Math.Round(rectangle.Left + rectangle.Width * relativeX);
                y = (int)Math.Round(rectangle.Top + rectangle.Height * relativeY);
                if (!ShouldUsePhysicalClick(element)) return true;
                if (IsVisuallyHittable(element, x, y)) return true;

                x = (int)Math.Round(rectangle.Left + rectangle.Width / 2);
                y = (int)Math.Round(rectangle.Top + rectangle.Height / 2);
                if (IsVisuallyHittable(element, x, y)) return true;

                System.Windows.Point clickablePoint;
                if (element.TryGetClickablePoint(out clickablePoint) &&
                    IsVisuallyHittable(element, clickablePoint.X, clickablePoint.Y))
                {
                    x = (int)Math.Round(clickablePoint.X);
                    y = (int)Math.Round(clickablePoint.Y);
                    return true;
                }
                x = 0;
                y = 0;
                return false;
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }
        }

        private static bool TryGetFallbackPoint(IntPtr window, ReplayTarget replayTarget, ReplayAction action, out int x, out int y)
        {
            x = 0;
            y = 0;
            NativeMethods.WindowRectangle rectangle;
            if (!NativeMethods.GetWindowRect(window, out rectangle)) return false;
            var point = action.Locator == null ? null : action.Locator.FallbackWindowPoint;
            if (point == null) point = action.Coordinate;
            if (point == null) return false;
            ScaleWindowPoint(rectangle, replayTarget == null ? null : replayTarget.RecordedWindowSize, point, out x, out y);
            return true;
        }

        internal static void ScaleWindowPoint(
            NativeMethods.WindowRectangle currentRectangle,
            ReplayWindowSize recordedSize,
            TracePoint point,
            out int x,
            out int y)
        {
            var scaleX = recordedSize != null && recordedSize.Width > 0
                ? (double)(currentRectangle.Right - currentRectangle.Left) / recordedSize.Width
                : 1.0;
            var scaleY = recordedSize != null && recordedSize.Height > 0
                ? (double)(currentRectangle.Bottom - currentRectangle.Top) / recordedSize.Height
                : 1.0;
            x = currentRectangle.Left + (int)Math.Round(point.X * scaleX);
            y = currentRectangle.Top + (int)Math.Round(point.Y * scaleY);
        }

        private static string DescribeCoordinateFallback(ReplayTarget replayTarget)
        {
            var size = replayTarget == null ? null : replayTarget.RecordedWindowSize;
            return size != null && size.Width > 0 && size.Height > 0
                ? "scaled_window_relative_coordinate_safe_fallback"
                : "legacy_unscaled_window_relative_coordinate_safe_fallback";
        }

        private static bool HasSemanticLocator(LocatorBundle locator)
        {
            return locator != null && (!string.IsNullOrWhiteSpace(locator.AutomationId) ||
                !string.IsNullOrWhiteSpace(locator.Name) || !string.IsNullOrWhiteSpace(locator.ClassName));
        }

        internal static string DescribeLocator(LocatorBundle locator)
        {
            if (locator == null) return "none";
            if (!string.IsNullOrWhiteSpace(locator.AutomationId)) return "automation_id=" + locator.AutomationId;
            if (!string.IsNullOrWhiteSpace(locator.Name)) return "name=" + locator.Name;
            if (!string.IsNullOrWhiteSpace(locator.ClassName)) return "class_name=" + locator.ClassName;
            return "control_type=" + locator.ControlType;
        }

        internal static void EnsureSafeMousePoint(
            int x,
            int y,
            IntPtr targetWindow,
            string targetProcessName,
            bool enforceForeground = true)
        {
            var deadline = DateTime.UtcNow.AddSeconds(12);
            while (DateTime.UtcNow < deadline)
            {
                if (targetWindow == IntPtr.Zero || !NativeMethods.IsWindow(targetWindow))
                {
                    targetWindow = NativeMethods.FindLargestVisibleWindowByProcessName(targetProcessName);
                }
                var pointOwned = NativeMethods.IsPointOwnedByWindowOrProcess(x, y, targetWindow, targetProcessName);
                if (pointOwned && (!enforceForeground || NativeMethods.IsForegroundWindowRoot(targetWindow))) return;
                NativeMethods.PromoteWindow(targetWindow);
                NativeMethods.EnsureForegroundWindowByProcessName(targetWindow, targetProcessName, 1);
                if (NativeMethods.IsPointOwnedByWindowOrProcess(x, y, targetWindow, targetProcessName) &&
                    (!enforceForeground || NativeMethods.IsForegroundWindowRoot(targetWindow))) return;
                Thread.Sleep(40);
            }
            throw new InvalidOperationException(
                "等待元宝 WebView 点击区域就绪超时，未操作其他窗口；" +
                NativeMethods.DescribePointOwner(x, y));
        }

        private static void EnsureReplayForeground(IntPtr targetWindow, string targetProcessName)
        {
            if (targetWindow != IntPtr.Zero && NativeMethods.IsForegroundWindowRoot(targetWindow)) return;
            if (targetWindow == IntPtr.Zero || !NativeMethods.IsWindow(targetWindow))
            {
                targetWindow = NativeMethods.FindLargestVisibleWindowByProcessName(targetProcessName);
            }
            if (targetWindow != IntPtr.Zero &&
                NativeMethods.EnsureForegroundWindowByProcessName(targetWindow, targetProcessName, 3)) return;
            throw new InvalidOperationException("执行交互前无法取得元宝前台操作权，已停止回放");
        }

        private static string Read(Func<string> reader)
        {
            try
            {
                return reader() ?? string.Empty;
            }
            catch (ElementNotAvailableException)
            {
                return string.Empty;
            }
        }

        private static T ReadValue<T>(Func<T> reader, T fallback)
        {
            try
            {
                return reader();
            }
            catch (ElementNotAvailableException)
            {
                return fallback;
            }
            catch (InvalidOperationException)
            {
                return fallback;
            }
        }
    }
}
