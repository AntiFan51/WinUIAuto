using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace YuanbaoRecorder.Agent
{
    internal sealed class RecorderSession : IDisposable
    {
        private readonly string targetProcessName;
        private readonly string targetDisplayName;
        private readonly string caseRoot;
        private BlockingCollection<ClickObservation> observations;
        private readonly UiaCaptureService uiaCaptureService = new UiaCaptureService();
        private readonly TransientMenuObserver transientMenuObserver;
        private readonly YuanbaoMessageDiffService messageDiffService = new YuanbaoMessageDiffService();
        private readonly object traceLock = new object();
        private NativeMethods.LowLevelMouseProc mouseCallback;
        private IntPtr mouseHook;
        private NativeMethods.LowLevelKeyboardProc keyboardCallback;
        private IntPtr keyboardHook;
        private Task consumerTask;
        private RawTrace trace;
        private string sessionDirectory;
        private int actionSequence;
        private int assertionSequence;
        private bool recording;
        private volatile bool actionCaptureSuspended;
        private volatile bool controlKeyPressed;
        private uint targetProcessId;
        private IntPtr targetWindowHandle;
        private UiaSnapshot lastSnapshot;
        private string lastSnapshotRelativePath;
        private PendingInputContext pendingInput;
        private readonly object keyboardInputOriginLock = new object();
        private FocusedEditableCapture keyboardInputOrigin;
        private readonly List<TraceAssertion> pendingAfterNextActionAssertions = new List<TraceAssertion>();
        private bool suppressNextRightButtonUp;
        private InputEventJournal inputEventJournal;
        private PendingPointerGesture pendingPointerGesture;
        private TraceAction activeBranch;
        private string activeBranchPath;

        internal RecorderSession(string targetProcessName, string targetDisplayName, string caseRoot)
        {
            this.targetProcessName = targetProcessName;
            this.targetDisplayName = targetDisplayName;
            this.caseRoot = caseRoot;
            transientMenuObserver = new TransientMenuObserver(uiaCaptureService);
        }

        internal bool IsRecording { get { return recording; } }
        internal string SessionDirectory { get { return sessionDirectory; } }
        internal event Action<string> StatusChanged;
        internal event Action AssertionRequested;

        internal void Start(string caseName)
        {
            if (recording) throw new InvalidOperationException("已经存在正在录制的用例");

            var targetWindow = NativeMethods.FindLargestVisibleWindowByProcessName(targetProcessName);
            if (targetWindow == IntPtr.Zero)
            {
                throw new InvalidOperationException("没有找到腾讯元宝窗口，请先启动元宝客户端");
            }

            var safeCaseName = SanitizeFileName(caseName);
            var sessionId = safeCaseName + "-" + DateTime.Now.ToString("yyyyMMddHHmmssfff");
            sessionDirectory = Path.Combine(caseRoot, sessionId);
            Directory.CreateDirectory(Path.Combine(sessionDirectory, "frames"));
            Directory.CreateDirectory(Path.Combine(sessionDirectory, "hierarchy"));

            trace = new RawTrace
            {
                SessionId = sessionId,
                CaseName = caseName,
                TargetWindow = targetDisplayName,
                StartedAt = DateTime.UtcNow.ToString("O"),
                Agent = new TraceAgentMetadata
                {
                    Version = AgentBuildInfo.Version,
                    BuildConfiguration = AgentBuildInfo.Configuration,
                    GitCommit = AgentBuildInfo.GitCommit,
                    BuiltAt = AgentBuildInfo.BuiltAtUtc,
                    ExecutablePath = Process.GetCurrentProcess().MainModule.FileName,
                    ProcessId = Process.GetCurrentProcess().Id,
                    MachineName = Environment.MachineName,
                    OsVersion = Environment.OSVersion.VersionString,
                    CaptureStrategy = "input_event_journal+immediate_uia_hit_test+transient_menu_observer+asynchronous_evidence_snapshot"
                }
            };
            if (inputEventJournal != null) inputEventJournal.Dispose();
            inputEventJournal = new InputEventJournal(Path.Combine(sessionDirectory, trace.InputEventLog));
            if (observations != null) observations.Dispose();
            observations = new BlockingCollection<ClickObservation>(new ConcurrentQueue<ClickObservation>());
            actionSequence = 0;
            assertionSequence = 0;
            lastSnapshot = null;
            lastSnapshotRelativePath = null;
            pendingInput = null;
            lock (keyboardInputOriginLock) keyboardInputOrigin = null;
            pendingAfterNextActionAssertions.Clear();
            suppressNextRightButtonUp = false;
            controlKeyPressed = false;
            pendingPointerGesture = null;
            activeBranch = null;
            activeBranchPath = null;
            transientMenuObserver.Reset();
            WriteTrace();

            mouseCallback = MouseHookCallback;
            mouseHook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WhMouseLowLevel,
                mouseCallback,
                NativeMethods.GetModuleHandle(null),
                0);
            if (mouseHook == IntPtr.Zero)
            {
                throw new InvalidOperationException("安装 Windows 鼠标监听器失败：" + Marshal.GetLastWin32Error());
            }

            keyboardCallback = KeyboardHookCallback;
            keyboardHook = NativeMethods.SetKeyboardHook(
                NativeMethods.WhKeyboardLowLevel,
                keyboardCallback,
                NativeMethods.GetModuleHandle(null),
                0);
            if (keyboardHook == IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(mouseHook);
                mouseHook = IntPtr.Zero;
                throw new InvalidOperationException("安装 Windows 键盘监听器失败：" + Marshal.GetLastWin32Error());
            }

            consumerTask = Task.Run((Action)ConsumeObservations);
            recording = true;
            actionCaptureSuspended = false;
            targetWindowHandle = targetWindow;
            targetProcessId = NativeMethods.ReadWindowProcessId(targetWindow);
            NativeMethods.ShowWindowAsync(targetWindow, NativeMethods.ShowMaximized);
            NativeMethods.EnsureForegroundWindow(targetWindow, targetProcessId, 3);
            PublishStatus("录制中：请直接操作元宝");
        }

        internal void SuspendActionCapture()
        {
            actionCaptureSuspended = true;
        }

        internal void ResumeActionCapture()
        {
            actionCaptureSuspended = false;
        }

        internal UiaTargetCaptureResult PreviewTargetAtCursor()
        {
            if (!recording) return null;
            var targetWindow = NativeMethods.FindLargestVisibleWindowByProcessName(targetProcessName);
            NativeMethods.Point point;
            NativeMethods.WindowRectangle rectangle;
            if (targetWindow == IntPtr.Zero || !NativeMethods.GetCursorPos(out point) ||
                !NativeMethods.GetWindowRect(targetWindow, out rectangle)) return null;
            if (point.X < rectangle.Left || point.X > rectangle.Right || point.Y < rectangle.Top || point.Y > rectangle.Bottom)
            {
                return null;
            }
            return uiaCaptureService.CaptureTargetAtPoint(new ClickObservation
            {
                TimestampUtc = DateTime.UtcNow,
                X = point.X,
                Y = point.Y,
                WindowHandle = targetWindow,
                WindowTitle = NativeMethods.ReadWindowTitle(targetWindow),
                WindowRectangle = rectangle
            }, false);
        }

        internal AssertionCaptureContext CaptureAssertionTarget()
        {
            if (!recording) throw new InvalidOperationException("请先开始录制");

            var targetWindow = NativeMethods.FindLargestVisibleWindowByProcessName(targetProcessName);
            if (targetWindow == IntPtr.Zero) throw new InvalidOperationException("没有找到腾讯元宝窗口");

            NativeMethods.Point point;
            NativeMethods.WindowRectangle rectangle;
            if (!NativeMethods.GetCursorPos(out point) || !NativeMethods.GetWindowRect(targetWindow, out rectangle))
            {
                throw new InvalidOperationException("无法读取鼠标位置或元宝窗口范围");
            }
            if (point.X < rectangle.Left || point.X > rectangle.Right || point.Y < rectangle.Top || point.Y > rectangle.Bottom)
            {
                throw new InvalidOperationException("请先把鼠标悬停在元宝中需要验证的内容上");
            }

            var sequence = Interlocked.Increment(ref assertionSequence);
            var assertionId = "assertion_" + sequence.ToString("D4");
            var screenshotRelativePath = "frames/" + assertionId + ".png";
            var hierarchyRelativePath = "hierarchy/" + assertionId + ".json";
            var capturedAt = DateTime.UtcNow.ToString("O");
            var observation = new ClickObservation
            {
                TimestampUtc = DateTime.UtcNow,
                X = point.X,
                Y = point.Y,
                WindowHandle = targetWindow,
                WindowTitle = NativeMethods.ReadWindowTitle(targetWindow),
                WindowRectangle = rectangle
            };

            var immediateCapture = uiaCaptureService.CaptureTargetAtPoint(observation);
            SaveScreenshot(rectangle, Path.Combine(sessionDirectory, screenshotRelativePath));
            UiaCaptureResult capture;
            try
            {
                capture = uiaCaptureService.Capture(observation);
            }
            catch
            {
                var fallbackSnapshot = new UiaSnapshot
                {
                    CapturedAt = DateTime.UtcNow.ToString("O"),
                    WindowTitle = observation.WindowTitle
                };
                if (immediateCapture.Target != null) fallbackSnapshot.Nodes.Add(immediateCapture.Target);
                fallbackSnapshot.NodeCount = fallbackSnapshot.Nodes.Count;
                capture = new UiaCaptureResult
                {
                    Snapshot = fallbackSnapshot,
                    Target = immediateCapture.Target,
                    Locator = immediateCapture.Locator,
                    ReadMode = "assertion_immediate_fallback"
                };
            }
            var transientCapture = uiaCaptureService.CaptureTransientTargetFromSnapshot(capture.Snapshot, observation);
            WriteJson(Path.Combine(sessionDirectory, hierarchyRelativePath), capture.Snapshot);
            var snapshotWindowBounds = capture.Snapshot == null || capture.Snapshot.Nodes.Count == 0
                ? null
                : capture.Snapshot.Nodes[0].Bounds;
            // Ctrl assertion capture is an explicit user choice.  A later full
            // snapshot can score a large generic container higher than the text
            // directly under the cursor (for example dialog title -> chat
            // content).  Preserve a directly hit named/automated control; only
            // promote when the immediate target has no stable business identity.
            var useSnapshotTarget = !HasExplicitAssertionIdentity(immediateCapture.Target) &&
                ControlResolver.IsBetter(capture.Target, immediateCapture.Target, snapshotWindowBounds);
            var assertionTarget = transientCapture.Target ??
                (useSnapshotTarget ? capture.Target : immediateCapture.Target ?? capture.Target);
            var assertionLocator = transientCapture.Locator ??
                (useSnapshotTarget ? capture.Locator : immediateCapture.Locator ?? capture.Locator);
            if (assertionTarget == null)
            {
                DeleteIfExists(Path.Combine(sessionDirectory, screenshotRelativePath));
                DeleteIfExists(Path.Combine(sessionDirectory, hierarchyRelativePath));
                throw new InvalidOperationException("该位置没有识别到可验证的 UIA 控件，请移动鼠标后重试");
            }

            string latestActionId;
            lock (traceLock)
            {
                latestActionId = trace.Actions.Count == 0 ? null : trace.Actions[trace.Actions.Count - 1].Id;
            }
            return new AssertionCaptureContext
            {
                Id = assertionId,
                SuggestedAfterActionId = latestActionId,
                HitTarget = immediateCapture.HitTarget,
                Target = assertionTarget,
                TargetDecision = transientCapture.Target != null
                    ? "current_snapshot_transient_menu"
                    : useSnapshotTarget ? "semantic_snapshot_promotion" : immediateCapture.Decision,
                Locator = assertionLocator,
                Screenshot = screenshotRelativePath,
                UiaSnapshot = hierarchyRelativePath,
                CapturedAt = capturedAt,
                ScreenPoint = new TracePoint(point.X, point.Y),
                WindowPoint = new TracePoint(point.X - rectangle.Left, point.Y - rectangle.Top),
                WindowTitle = observation.WindowTitle,
                Snapshot = capture.Snapshot,
                SuggestedScopeControlType = FindAncestorControlType(
                    capture.Snapshot,
                    assertionTarget,
                    "Table",
                    point.X,
                    point.Y),
                CaptureDiagnostics = new TraceCaptureDiagnostics
                {
                    MouseEvent = "ctrl_right_click",
                    QueueDelayMilliseconds = 0,
                    SelectionSource = transientCapture.Target != null
                        ? "current_snapshot_transient_menu"
                        : useSnapshotTarget ? "semantic_snapshot_promotion" : "semantic_immediate_hit_test",
                    TransientDecision = transientCapture.Decision,
                    TransientCandidateCount = transientCapture.CandidateCount,
                    TransientTarget = TraceTargetDiagnostic.FromNode(transientCapture.Target),
                    ImmediateTarget = TraceTargetDiagnostic.FromNode(immediateCapture.Target),
                    HitLeafTarget = TraceTargetDiagnostic.FromNode(immediateCapture.HitTarget),
                    AfterSnapshotTarget = TraceTargetDiagnostic.FromNode(capture.Target)
                }
            };
        }

        internal void AddAssertion(
            AssertionCaptureContext capture,
            AssertionDraft draft)
        {
            var type = draft == null ? null : draft.Type;
            var expected = draft == null ? string.Empty : draft.Expected;
            if (capture == null || capture.Target == null) throw new InvalidOperationException("没有可用的断言目标");
            if (draft == null) throw new InvalidOperationException("断言配置不能为空");
            if (type != "target_exists" && type != "target_not_exists" && type != "text_contains" && type != "text_equals" && type != "property_equals" &&
                type != "keywords_match_count" && type != "descendant_count" &&
                type != "table_dimensions" && type != "horizontal_bounds_within_window" &&
                type != "descendants_within_bounds")
            {
                throw new InvalidOperationException("不支持的验证方式");
            }
            expected = (expected ?? string.Empty).Trim();
            if ((type == "text_contains" || type == "text_equals" || type == "property_equals") && expected.Length == 0)
            {
                throw new InvalidOperationException("请输入期望出现的文字");
            }

            var role = DescribeAssertionTarget(capture.Target);
            var assertion = new TraceAssertion
            {
                Id = capture.Id,
                AfterActionId = capture.SuggestedAfterActionId,
                Type = type,
                AssertionMode = draft.AssertionMode,
                Expected = type == "text_contains" || type == "text_equals" || type == "property_equals" ? expected : null,
                Property = draft.Property,
                Scope = draft.Scope,
                ScopeControlType = draft.ScopeControlType,
                Keywords = draft.Keywords,
                MinimumMatches = draft.MinimumMatches,
                DescendantControlType = draft.DescendantControlType,
                CountOperator = draft.CountOperator,
                ExpectedCount = draft.ExpectedCount,
                ExpectedRows = draft.ExpectedRows,
                ExpectedColumns = draft.ExpectedColumns,
                BoundsTolerancePixels = draft.BoundsTolerancePixels,
                TimeoutMilliseconds = 1000,
                Label = draft.AssertionMode == "input_value_equals"
                    ? role + "输入内容应精确等于“" + expected + "”"
                    : type == "property_equals"
                    ? role + "控件名称应精确等于“" + expected + "”"
                    : type == "text_contains"
                    ? role + "应包含“" + expected + "”"
                    : type == "target_not_exists" ? role + "应消失" : role + "应出现",
                Target = new TraceAssertionTarget
                {
                    SemanticRole = role,
                    LocatorBundle = capture.Locator,
                    Node = capture.Target
                },
                Evidence = new TraceAssertionEvidence
                {
                    Screenshot = capture.Screenshot,
                    UiaSnapshot = capture.UiaSnapshot,
                    CapturedAt = capture.CapturedAt,
                    CaptureDiagnostics = capture.CaptureDiagnostics
                }
            };

            if (type == "keywords_match_count") assertion.Label = "至少命中 " + draft.MinimumMatches + " 个关键词";
            if (type == "descendant_count") assertion.Label = role + "中的" + draft.DescendantControlType + "控件数量应为 " + draft.ExpectedCount;
            if (type == "table_dimensions") assertion.Label = "表格应为 " + draft.ExpectedRows + " 行 " + draft.ExpectedColumns + " 列";
            if (type == "horizontal_bounds_within_window") assertion.Label = role + "的内容不应横向溢出";
            if (type == "descendants_within_bounds") assertion.Label = role + "的子控件不应超出区域边界";

            lock (traceLock)
            {
                if (type == "target_not_exists")
                {
                    assertion.AfterActionId = null;
                    pendingAfterNextActionAssertions.Add(assertion);
                    PublishStatus("验证点已暂存：请执行使“" + role + "”消失的下一次操作");
                    return;
                }
                if (string.IsNullOrWhiteSpace(assertion.AfterActionId))
                {
                    throw new InvalidOperationException("请至少完成一个操作后再添加验证点");
                }
                var precedingAction = trace.Actions.LastOrDefault(action => action.Id == assertion.AfterActionId);
                if (type == "target_exists" && precedingAction != null &&
                    SameSemanticTarget(precedingAction.Target, capture.Target))
                {
                    throw new InvalidOperationException("“控件存在”选中了刚点击的同一控件；它只能证明触发器仍在，不能证明操作后的状态。请按 Ctrl 停在操作后新出现的内容上重新添加断言。");
                }
                trace.Assertions.Add(assertion);
                WriteTraceUnsafe();
            }
            PublishStatus("已添加验证点：" + assertion.Label);
        }

        internal void AddFlowControlAction(AssertionCaptureContext capture, FlowControlDraft draft)
        {
            if (capture == null || capture.Target == null || capture.Locator == null)
                throw new InvalidOperationException("没有可用的流程控制目标");
            if (draft == null) throw new InvalidOperationException("流程控制配置不能为空");
            if (draft.Type != "wait_for_target" && draft.Type != "condition")
                throw new InvalidOperationException("不支持的流程控制类型");
            var sameKindCondition = draft.Type == "condition" &&
                (draft.ConditionType == "same_kind_exists" || draft.ConditionType == "same_kind_not_exists");
            if (draft.Type == "condition" && draft.ConditionType != "group_list_not_empty" && !sameKindCondition)
            {
                PromoteConditionTargetToNamedDescendant(capture);
                if (capture.Locator == null ||
                    (string.IsNullOrWhiteSpace(capture.Locator.AutomationId) &&
                     string.IsNullOrWhiteSpace(capture.Locator.Name)))
                {
                    throw new InvalidOperationException(
                        "IF 条件目标缺少名称或 AutomationId，无法可靠区分分组和普通会话；请把鼠标悬停在具体分组文字上后重试");
                }
            }
            lock (traceLock)
            {
                if (draft.Type == "condition" && activeBranch != null)
                    throw new InvalidOperationException("暂不支持嵌套分支，请先结束当前分支");
            }
            var conditionType = draft.ConditionType == "target_not_exists" ||
                draft.ConditionType == "same_kind_exists" || draft.ConditionType == "same_kind_not_exists" ||
                draft.ConditionType == "group_list_not_empty"
                ? draft.ConditionType
                : "target_exists";
            string conditionScopeType = null;
            string conditionScopeStartName = null;
            string conditionScopeEndName = null;
            if (sameKindCondition)
            {
                capture.Locator = BuildSameKindLocator(capture.Locator, capture.Target);
                InferConditionScope(
                    capture,
                    out conditionScopeType,
                    out conditionScopeStartName,
                    out conditionScopeEndName);
            }
            var sequence = Interlocked.Increment(ref actionSequence);
            var role = DescribeAssertionTarget(capture.Target);
            var action = new TraceAction
            {
                Id = "action_" + sequence.ToString("D4"),
                Type = draft.Type,
                Timestamp = capture.CapturedAt,
                ScreenPoint = capture.ScreenPoint,
                WindowPoint = capture.WindowPoint,
                WindowTitle = capture.WindowTitle,
                Target = capture.Target,
                Locator = capture.Locator,
                Screenshot = capture.Screenshot,
                UiaSnapshot = capture.UiaSnapshot,
                EvidenceBeforeSnapshot = lastSnapshotRelativePath,
                EvidenceAfterSnapshot = capture.UiaSnapshot,
                ConditionType = conditionType,
                ConditionMatchMode = sameKindCondition ? "same_kind" : "exact",
                ConditionScopeType = conditionScopeType,
                ConditionScopeStartName = conditionScopeStartName,
                ConditionScopeEndName = conditionScopeEndName,
                TrueStepCount = 0,
                FalseStepCount = 0,
                TimeoutMilliseconds = draft.Type == "wait_for_target"
                    ? Math.Max(1000, Math.Min(120000, draft.TimeoutMilliseconds))
                    : 0,
                BranchId = draft.Type == "condition" ? "branch_" + sequence.ToString("D4") : null,
                Derivation = "manual_during_recording",
                CaptureDiagnostics = capture.CaptureDiagnostics
            };
            lock (traceLock)
            {
                ApplyActiveBranchUnsafe(action);
                trace.Actions.Add(action);
                if (draft.Type == "condition")
                {
                    activeBranch = action;
                    activeBranchPath = "if";
                }
                WriteTraceUnsafe();
            }
            PublishStatus(draft.Type == "wait_for_target"
                ? "已添加等待：" + role
                : "已添加 IF/ELSE 分支：" + role);
        }

        internal void AddDelayAction(FlowControlDraft draft)
        {
            if (draft == null || draft.Type != "wait_time")
                throw new InvalidOperationException("固定等待配置不正确");
            var duration = Math.Max(1000, Math.Min(120000, draft.TimeoutMilliseconds));
            var sequence = Interlocked.Increment(ref actionSequence);
            var action = new TraceAction
            {
                Id = "action_" + sequence.ToString("D4"),
                Type = "wait_time",
                Timestamp = DateTime.UtcNow.ToString("O"),
                WindowTitle = NativeMethods.ReadWindowTitle(targetWindowHandle),
                EvidenceBeforeSnapshot = lastSnapshotRelativePath,
                EvidenceAfterSnapshot = lastSnapshotRelativePath,
                TimeoutMilliseconds = duration,
                Derivation = "manual_timer_during_recording"
            };
            lock (traceLock)
            {
                ApplyActiveBranchUnsafe(action);
                trace.Actions.Add(action);
                WriteTraceUnsafe();
            }
            PublishStatus("已添加固定等待：" + (duration / 1000) + " 秒");
        }

        private static LocatorBundle BuildSameKindLocator(LocatorBundle source, UiaNode target)
        {
            if (source == null) return null;
            return new LocatorBundle
            {
                ControlType = source.ControlType,
                ClassName = BaseClassName(source.ClassName ?? (target == null ? null : target.ClassName)),
                AncestorPath = source.AncestorPath == null
                    ? new List<LocatorSegment>()
                    : source.AncestorPath
                        .Where(segment => segment != null && !string.IsNullOrWhiteSpace(segment.ClassName))
                        .Select(segment => new LocatorSegment
                        {
                            ControlType = segment.ControlType,
                            ClassName = BaseClassName(segment.ClassName)
                        })
                        .ToList(),
                FallbackWindowPoint = source.FallbackWindowPoint
            };
        }

        private static void InferConditionScope(
            AssertionCaptureContext capture,
            out string scopeType,
            out string startName,
            out string endName)
        {
            scopeType = "window";
            startName = null;
            endName = null;
            if (capture == null || capture.Target == null || capture.Target.Bounds == null ||
                capture.Snapshot == null || capture.Snapshot.Nodes == null) return;
            var targetBounds = capture.Target.Bounds;
            var headers = capture.Snapshot.Nodes
                .Where(node => node != null && node.Bounds != null && !node.Offscreen &&
                    IsMeaningfulConditionName(node.Name) &&
                    (string.Equals(node.ControlType, "Text", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(node.ControlType, "Group", StringComparison.OrdinalIgnoreCase)) &&
                    node.Bounds.Width > 0 && node.Bounds.Height > 0 &&
                    node.Bounds.X <= targetBounds.X + 24 &&
                    node.Bounds.X + node.Bounds.Width >= targetBounds.X + targetBounds.Width - 24)
                .OrderBy(node => node.Bounds.Y)
                .ToList();
            var start = headers.LastOrDefault(node => node.Bounds.Y + node.Bounds.Height <= targetBounds.Y + 4);
            var end = headers.FirstOrDefault(node => node.Bounds.Y >= targetBounds.Y + targetBounds.Height - 4);
            if (start == null || end == null || string.Equals(start.Name, end.Name, StringComparison.Ordinal)) return;
            scopeType = "section";
            startName = start.Name;
            endName = end.Name;
        }

        private static string BaseClassName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var token = value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(token)) return null;
            var marker = token.IndexOf("__", StringComparison.Ordinal);
            return marker > 0 ? token.Substring(0, marker) : token;
        }

        private static void PromoteConditionTargetToNamedDescendant(AssertionCaptureContext capture)
        {
            if (capture == null || capture.Target == null || capture.Snapshot == null ||
                capture.Snapshot.Nodes == null || capture.Snapshot.Nodes.Count == 0) return;

            var nodesByIndex = capture.Snapshot.Nodes.ToDictionary(node => node.Index);
            var snapshotTarget = FindSnapshotEquivalent(capture.Target, capture.Snapshot.Nodes);
            if (snapshotTarget != null) capture.Target = snapshotTarget;
            if (!string.IsNullOrWhiteSpace(capture.Target.AutomationId) ||
                !string.IsNullOrWhiteSpace(capture.Target.Name))
            {
                capture.Locator = UiaCaptureService.BuildLocatorFromSnapshot(
                    capture.Target,
                    nodesByIndex,
                    BuildConditionObservation(capture));
                return;
            }

            var named = capture.Snapshot.Nodes
                .Where(node => IsMeaningfulConditionName(node.Name) &&
                    IsDescendantOfSnapshotNode(node, capture.Target, nodesByIndex))
                .Where(node => node.Bounds != null && !node.Offscreen &&
                    node.Bounds.Width > 0 && node.Bounds.Height > 0)
                .Where(node => !string.Equals(node.Name, "scrollable content", StringComparison.OrdinalIgnoreCase))
                .OrderBy(node => string.Equals(node.ControlType, "Text", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(node => node.Depth - capture.Target.Depth)
                .ThenBy(node => node.Bounds == null ? double.MaxValue : node.Bounds.Width * node.Bounds.Height)
                .FirstOrDefault();
            if (named == null) return;

            capture.Target = named;
            capture.Locator = UiaCaptureService.BuildLocatorFromSnapshot(
                named,
                nodesByIndex,
                BuildConditionObservation(capture));
            if (capture.CaptureDiagnostics != null)
                capture.CaptureDiagnostics.SelectionSource = "condition_named_descendant_promotion";
        }

        private static bool IsMeaningfulConditionName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var text = value.Trim();
            return text.Any(character =>
                !char.IsWhiteSpace(character) &&
                !(character >= '\uE000' && character <= '\uF8FF'));
        }

        private static UiaNode FindSnapshotEquivalent(UiaNode immediate, IEnumerable<UiaNode> nodes)
        {
            if (immediate == null || immediate.Bounds == null) return null;
            return (nodes ?? Enumerable.Empty<UiaNode>())
                .Where(node => node.Bounds != null &&
                    Math.Abs(node.Bounds.X - immediate.Bounds.X) <= 2 &&
                    Math.Abs(node.Bounds.Y - immediate.Bounds.Y) <= 2 &&
                    Math.Abs(node.Bounds.Width - immediate.Bounds.Width) <= 2 &&
                    Math.Abs(node.Bounds.Height - immediate.Bounds.Height) <= 2)
                .Where(node => string.Equals(node.ControlType, immediate.ControlType, StringComparison.OrdinalIgnoreCase))
                .Where(node => string.IsNullOrWhiteSpace(immediate.ClassName) ||
                    string.Equals(node.ClassName, immediate.ClassName, StringComparison.Ordinal))
                .OrderByDescending(node => node.Depth)
                .FirstOrDefault();
        }

        private static ClickObservation BuildConditionObservation(AssertionCaptureContext capture)
        {
            return new ClickObservation
            {
                X = capture.ScreenPoint == null ? 0 : capture.ScreenPoint.X,
                Y = capture.ScreenPoint == null ? 0 : capture.ScreenPoint.Y,
                WindowRectangle = new NativeMethods.WindowRectangle
                {
                    Left = capture.ScreenPoint == null || capture.WindowPoint == null ? 0 : capture.ScreenPoint.X - capture.WindowPoint.X,
                    Top = capture.ScreenPoint == null || capture.WindowPoint == null ? 0 : capture.ScreenPoint.Y - capture.WindowPoint.Y
                }
            };
        }

        private static bool IsDescendantOfSnapshotNode(
            UiaNode node,
            UiaNode ancestor,
            IDictionary<int, UiaNode> nodesByIndex)
        {
            if (node == null || ancestor == null) return false;
            var current = node;
            for (var depth = 0; current != null && depth < 40; depth++)
            {
                if (current.Index == ancestor.Index) return current != node;
                UiaNode parent;
                current = current.ParentIndex >= 0 && nodesByIndex.TryGetValue(current.ParentIndex, out parent)
                    ? parent
                    : null;
            }
            return false;
        }

        internal bool HasActiveBranch { get { lock (traceLock) return activeBranch != null; } }

        internal void SwitchToElseBranch()
        {
            lock (traceLock)
            {
                if (activeBranch == null) throw new InvalidOperationException("当前没有正在录制的 IF 分支");
                if (activeBranchPath == "else") throw new InvalidOperationException("当前已经在录制 ELSE 分支");
                activeBranch.TrueStepCount = CountActiveBranchActionsUnsafe("if");
                if (activeBranch.TrueStepCount == 0) throw new InvalidOperationException("IF 分支至少需要录制一个步骤");
                activeBranchPath = "else";
                WriteTraceUnsafe();
            }
            PublishStatus("已切换到 ELSE，后续操作将归入 ELSE 分支");
        }

        internal void EndActiveBranch()
        {
            lock (traceLock)
            {
                if (activeBranch == null) throw new InvalidOperationException("当前没有正在录制的 IF 分支");
                activeBranch.TrueStepCount = CountActiveBranchActionsUnsafe("if");
                activeBranch.FalseStepCount = CountActiveBranchActionsUnsafe("else");
                if (activeBranch.TrueStepCount == 0) throw new InvalidOperationException("IF 分支至少需要录制一个步骤");
                activeBranch = null;
                activeBranchPath = null;
                WriteTraceUnsafe();
            }
            PublishStatus("IF / ELSE 分支已结束，后续操作恢复为公共步骤");
        }

        private int CountActiveBranchActionsUnsafe(string path)
        {
            return trace.Actions.Count(item => item.BranchId == activeBranch.BranchId && item.BranchPath == path);
        }

        private void ApplyActiveBranchUnsafe(TraceAction action)
        {
            if (action == null || activeBranch == null || action == activeBranch) return;
            action.BranchId = activeBranch.BranchId;
            action.BranchPath = activeBranchPath;
        }

        private static string FindAncestorControlType(
            UiaSnapshot snapshot,
            UiaNode target,
            string controlType,
            double pointX,
            double pointY)
        {
            if (snapshot == null || target == null) return null;
            var nodes = snapshot.Nodes.ToDictionary(node => node.Index);
            var current = target.RuntimeId == null
                ? null
                : snapshot.Nodes.FirstOrDefault(node => node.RuntimeId != null && node.RuntimeId.SequenceEqual(target.RuntimeId));
            if (current == null) current = target;
            for (var depth = 0; current != null && depth < 30; depth++)
            {
                if (string.Equals(current.ControlType, controlType, StringComparison.OrdinalIgnoreCase)) return controlType;
                UiaNode parent;
                current = current.ParentIndex >= 0 && nodes.TryGetValue(current.ParentIndex, out parent) ? parent : null;
            }
            return snapshot.Nodes.Any(node =>
                string.Equals(node.ControlType, controlType, StringComparison.OrdinalIgnoreCase) &&
                node.Bounds != null && node.Bounds.Width > 0 && node.Bounds.Height > 0 &&
                pointX >= node.Bounds.X && pointX <= node.Bounds.X + node.Bounds.Width &&
                pointY >= node.Bounds.Y && pointY <= node.Bounds.Y + node.Bounds.Height)
                ? controlType
                : null;
        }

        internal void DiscardAssertionCapture(AssertionCaptureContext capture)
        {
            if (capture == null) return;
            DeleteIfExists(Path.Combine(sessionDirectory, capture.Screenshot ?? string.Empty));
            DeleteIfExists(Path.Combine(sessionDirectory, capture.UiaSnapshot ?? string.Empty));
        }

        internal async Task StopAsync(TimeSpan timeout)
        {
            if (!recording) return;

            recording = false;
            controlKeyPressed = false;
            if (mouseHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(mouseHook);
                mouseHook = IntPtr.Zero;
            }
            if (keyboardHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(keyboardHook);
                keyboardHook = IntPtr.Zero;
            }
            observations.CompleteAdding();
            transientMenuObserver.Reset();
            if (inputEventJournal != null)
            {
                inputEventJournal.Dispose();
                inputEventJournal = null;
            }
            PublishStatus("正在整理已采集的操作…");

            if (consumerTask != null)
            {
                var completed = await Task.WhenAny(consumerTask, Task.Delay(timeout));
                if (completed != consumerTask)
                {
                    PublishStatus("部分 UIA 采集仍在后台处理，Raw Trace 已安全保存");
                }
            }

            lock (traceLock)
            {
                // A user may finish the whole recording immediately after the
                // ELSE path instead of pressing the branch hotkey once more.
                // Persist both branch lengths here so replay never treats ELSE
                // actions as unconditional trailing actions.
                if (activeBranch != null)
                {
                    activeBranch.TrueStepCount = CountActiveBranchActionsUnsafe("if");
                    activeBranch.FalseStepCount = CountActiveBranchActionsUnsafe("else");
                    activeBranch = null;
                    activeBranchPath = null;
                }
                var abandonedAssertions = pendingAfterNextActionAssertions.Count;
                foreach (var assertion in pendingAfterNextActionAssertions)
                {
                    DeleteIfExists(Path.Combine(sessionDirectory, assertion.Evidence.Screenshot));
                    DeleteIfExists(Path.Combine(sessionDirectory, assertion.Evidence.UiaSnapshot));
                }
                pendingAfterNextActionAssertions.Clear();
                trace.FinishedAt = DateTime.UtcNow.ToString("O");
                WriteTraceUnsafe();
                if (abandonedAssertions > 0)
                {
                    PublishStatus("有 " + abandonedAssertions + " 个“下一次操作后消失”验证点未绑定，已安全忽略");
                }
            }
            PublishStatus("录制结束：" + sessionDirectory);
        }

        private IntPtr MouseHookCallback(int code, IntPtr message, IntPtr data)
        {
            var messageId = message.ToInt32();
            var controlPressed = controlKeyPressed ||
                (NativeMethods.GetAsyncKeyState(NativeMethods.VirtualKeyControl) & 0x8000) != 0;
            if (code >= 0 && recording && messageId == NativeMethods.WmRightButtonUp && suppressNextRightButtonUp)
            {
                suppressNextRightButtonUp = false;
                return new IntPtr(1);
            }
            if (code >= 0 && recording && controlPressed && messageId == NativeMethods.WmRightButtonDown)
            {
                var hookData = (NativeMethods.MouseHookData)Marshal.PtrToStructure(
                    data,
                    typeof(NativeMethods.MouseHookData));
                var targetWindow = NativeMethods.FindLargestVisibleWindowByProcessName(targetProcessName);
                NativeMethods.WindowRectangle targetRectangle;
                if (targetWindow != IntPtr.Zero && NativeMethods.GetWindowRect(targetWindow, out targetRectangle) &&
                    hookData.Position.X >= targetRectangle.Left && hookData.Position.X <= targetRectangle.Right &&
                    hookData.Position.Y >= targetRectangle.Top && hookData.Position.Y <= targetRectangle.Bottom)
                {
                    if (inputEventJournal != null)
                    {
                        inputEventJournal.Record(
                            "right_click",
                            DateTime.UtcNow,
                            hookData.Position.X,
                            hookData.Position.Y,
                            true,
                            "assertion_requested");
                    }
                    suppressNextRightButtonUp = true;
                    var assertionHandler = AssertionRequested;
                    if (assertionHandler != null) assertionHandler();
                    return new IntPtr(1);
                }
            }
            if (code >= 0 && recording && !actionCaptureSuspended && messageId == NativeMethods.WmLeftButtonDown)
            {
                BeginPointerGesture(data, controlPressed);
                return NativeMethods.CallNextHookEx(mouseHook, code, message, data);
            }
            if (code >= 0 && recording && !actionCaptureSuspended && messageId == NativeMethods.WmLeftButtonUp)
            {
                CompletePointerGesture(data);
                return NativeMethods.CallNextHookEx(mouseHook, code, message, data);
            }
            if (code >= 0 && recording && !actionCaptureSuspended && messageId == NativeMethods.WmMouseWheel)
            {
                RecordWheel(data, controlPressed);
                return NativeMethods.CallNextHookEx(mouseHook, code, message, data);
            }
            var isRecordedMouseAction = messageId == NativeMethods.WmRightButtonDown && !controlPressed;
            if (code >= 0 && isRecordedMouseAction && recording && !actionCaptureSuspended)
            {
                var hookData = (NativeMethods.MouseHookData)Marshal.PtrToStructure(
                    data,
                    typeof(NativeMethods.MouseHookData));
                NativeMethods.WindowRectangle windowRectangle;
                if (IsInsideTarget(hookData.Position, out windowRectangle))
                {
                    try
                    {
                        var observation = new ClickObservation
                        {
                            ActionType = messageId == NativeMethods.WmRightButtonDown ? "right_click" : "click",
                            TimestampUtc = DateTime.UtcNow,
                            X = hookData.Position.X,
                            Y = hookData.Position.Y,
                            WindowHandle = targetWindowHandle,
                            WindowTitle = NativeMethods.ReadWindowTitle(targetWindowHandle),
                            WindowRectangle = windowRectangle
                        };
                        if (inputEventJournal != null)
                        {
                            observation.InputEventId = inputEventJournal.Record(
                                observation.ActionType,
                                observation.TimestampUtc,
                                observation.X,
                                observation.Y,
                                controlPressed,
                                "recorded_action");
                        }
                        if (messageId == NativeMethods.WmLeftButtonDown)
                        {
                            var transientMatch = transientMenuObserver.ConsumeAtPoint(observation);
                            if (transientMatch != null)
                            {
                                observation.EventTransientTarget = transientMatch.Target;
                                observation.EventTransientLocator = transientMatch.Locator;
                                observation.EventTransientSnapshot = transientMatch.Snapshot;
                                observation.EventTransientRevision = transientMatch.Revision;
                                observation.EventTransientDecision = transientMatch.Decision;
                                observation.EventTransientCandidateCount = transientMatch.CandidateCount;
                            }
                        }
                        try
                        {
                            var immediateCapture = uiaCaptureService.CaptureTargetAtPoint(observation);
                            observation.ImmediateTarget = immediateCapture.Target;
                            observation.ImmediateLocator = immediateCapture.Locator;
                            observation.HitLeafTarget = immediateCapture.HitTarget;
                            observation.HitLeafLocator = immediateCapture.HitLocator;
                        }
                        catch (Exception error)
                        {
                            observation.ImmediateCaptureError = error.Message;
                        }
                        observations.Add(observation);
                        if (messageId == NativeMethods.WmRightButtonDown)
                        {
                            transientMenuObserver.ObserveAfterPointerAction(targetWindowHandle);
                        }
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            }
            return NativeMethods.CallNextHookEx(mouseHook, code, message, data);
        }

        private IntPtr KeyboardHookCallback(int code, IntPtr message, IntPtr data)
        {
            var messageId = message.ToInt32();
            if (code >= 0)
            {
                var stateData = (NativeMethods.KeyboardHookData)Marshal.PtrToStructure(
                    data,
                    typeof(NativeMethods.KeyboardHookData));
                var isControl = stateData.VirtualKeyCode == NativeMethods.VirtualKeyControl ||
                    stateData.VirtualKeyCode == NativeMethods.VirtualKeyLeftControl ||
                    stateData.VirtualKeyCode == NativeMethods.VirtualKeyRightControl;
                if (isControl && (messageId == NativeMethods.WmKeyDown || messageId == NativeMethods.WmSysKeyDown))
                {
                    controlKeyPressed = true;
                }
                else if (isControl && (messageId == NativeMethods.WmKeyUp || messageId == NativeMethods.WmSysKeyUp))
                {
                    controlKeyPressed = false;
                }
            }
            if (code < 0 || !recording || actionCaptureSuspended ||
                (messageId != NativeMethods.WmKeyDown && messageId != NativeMethods.WmSysKeyDown))
            {
                return NativeMethods.CallNextHookEx(keyboardHook, code, message, data);
            }
            var hookData = (NativeMethods.KeyboardHookData)Marshal.PtrToStructure(data, typeof(NativeMethods.KeyboardHookData));
            var key = hookData.VirtualKeyCode == 0x0D ? "ENTER" : hookData.VirtualKeyCode == 0x09 ? "TAB" : null;
            if (!NativeMethods.IsWindowOwnedByTargetFamily(
                NativeMethods.GetForegroundWindow(),
                targetWindowHandle,
                targetProcessName))
            {
                return NativeMethods.CallNextHookEx(keyboardHook, code, message, data);
            }
            NativeMethods.WindowRectangle rectangle;
            if (!NativeMethods.GetWindowRect(targetWindowHandle, out rectangle))
            {
                return NativeMethods.CallNextHookEx(keyboardHook, code, message, data);
            }
            var observation = new ClickObservation
            {
                ActionType = "key_press",
                Key = key,
                CommitInputByKeyboard = true,
                TimestampUtc = DateTime.UtcNow,
                X = rectangle.Left + rectangle.Width / 2,
                Y = rectangle.Top + rectangle.Height / 2,
                WindowHandle = targetWindowHandle,
                WindowTitle = NativeMethods.ReadWindowTitle(targetWindowHandle),
                WindowRectangle = rectangle
            };
            if (key == null)
            {
                lock (keyboardInputOriginLock)
                {
                    if (keyboardInputOrigin != null)
                    {
                        return NativeMethods.CallNextHookEx(keyboardHook, code, message, data);
                    }
                }
                try
                {
                    var focusedEditable = uiaCaptureService.CaptureFocusedEditable(observation);
                    if (focusedEditable != null && TrySetKeyboardInputOrigin(focusedEditable))
                    {
                        observation.ActionType = "input_start";
                        observation.StartedInput = focusedEditable;
                        observation.EvidenceOnly = true;
                        observations.Add(observation);
                    }
                }
                catch
                {
                }
                return NativeMethods.CallNextHookEx(keyboardHook, code, message, data);
            }
            if (inputEventJournal != null)
            {
                observation.InputEventId = inputEventJournal.Record("key_press_" + key.ToLowerInvariant(), observation.TimestampUtc, observation.X, observation.Y, false, "recorded_action");
            }
            FocusedEditableCapture preInteractionInput = null;
            try { preInteractionInput = uiaCaptureService.CaptureFocusedEditable(observation); }
            catch { }
            if (preInteractionInput == null && pendingInput != null && !pendingInput.IsChatEditor)
            {
                try { preInteractionInput = uiaCaptureService.CaptureEditableAtBounds(observation, pendingInput.Bounds); }
                catch { }
            }
            if (preInteractionInput != null) observation.InputCommitSource = "keyboard_pre_dispatch";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(100);
                FocusedEditableCapture afterInteractionInput = null;
                try { afterInteractionInput = uiaCaptureService.CaptureFocusedEditable(observation); }
                catch { }
                observation.CommittedInput = PreferPreInteractionInput(preInteractionInput, afterInteractionInput);
                if (observation.CommittedInput != null && string.IsNullOrWhiteSpace(observation.InputCommitSource))
                {
                    observation.InputCommitSource = "keyboard_post_dispatch";
                }
                lock (keyboardInputOriginLock) keyboardInputOrigin = null;
                try
                {
                    if (!observations.IsAddingCompleted) observations.Add(observation);
                }
                catch (InvalidOperationException)
                {
                }
            });
            return NativeMethods.CallNextHookEx(keyboardHook, code, message, data);
        }

        private void BeginPointerGesture(IntPtr data, bool controlPressed)
        {
            var hookData = (NativeMethods.MouseHookData)Marshal.PtrToStructure(data, typeof(NativeMethods.MouseHookData));
            NativeMethods.WindowRectangle rectangle;
            if (!IsInsideTarget(hookData.Position, out rectangle))
            {
                pendingPointerGesture = null;
                if (pendingInput != null && !pendingInput.IsChatEditor && NativeMethods.GetWindowRect(targetWindowHandle, out rectangle))
                {
                    var evidence = CreateObservation("input_commit", hookData.Position, rectangle, controlPressed);
                    evidence.EvidenceOnly = true;
                    try { evidence.CommittedInput = uiaCaptureService.CaptureFocusedEditable(evidence); }
                    catch { }
                    if (evidence.CommittedInput == null)
                    {
                        try { evidence.CommittedInput = uiaCaptureService.CaptureEditableAtBounds(evidence, pendingInput.Bounds); }
                        catch { }
                    }
                    if (evidence.CommittedInput != null) evidence.InputCommitSource = "pointer_pre_dispatch";
                    if (evidence.CommittedInput != null) observations.Add(evidence);
                }
                if (inputEventJournal != null)
                {
                    inputEventJournal.Record("left_click", DateTime.UtcNow, hookData.Position.X, hookData.Position.Y, controlPressed, "ignored_outside_target");
                }
                return;
            }
            var observation = CreateObservation("click", hookData.Position, rectangle, controlPressed, false);
            var transientMatch = transientMenuObserver.ConsumeAtPoint(observation);
            if (transientMatch != null)
            {
                observation.EventTransientTarget = transientMatch.Target;
                observation.EventTransientLocator = transientMatch.Locator;
                observation.EventTransientSnapshot = transientMatch.Snapshot;
                observation.EventTransientRevision = transientMatch.Revision;
                observation.EventTransientDecision = transientMatch.Decision;
                observation.EventTransientCandidateCount = transientMatch.CandidateCount;
            }
            try { observation.CommittedInput = uiaCaptureService.CaptureFocusedEditable(observation); }
            catch { }
            if (observation.CommittedInput == null && pendingInput != null && !pendingInput.IsChatEditor)
            {
                try { observation.CommittedInput = uiaCaptureService.CaptureEditableAtBounds(observation, pendingInput.Bounds); }
                catch { }
            }
            if (observation.CommittedInput != null) observation.InputCommitSource = "pointer_pre_dispatch";
            try { observation.StartedInput = uiaCaptureService.CaptureEditableAtPoint(observation); }
            catch { }
            lock (keyboardInputOriginLock) keyboardInputOrigin = observation.StartedInput;
            pendingPointerGesture = new PendingPointerGesture
            {
                Observation = observation,
                StartedAtUtc = observation.TimestampUtc,
                StartX = hookData.Position.X,
                StartY = hookData.Position.Y,
                ControlPressed = controlPressed
            };
        }

        private void CompletePointerGesture(IntPtr data)
        {
            var gesture = pendingPointerGesture;
            pendingPointerGesture = null;
            if (gesture == null) return;
            var hookData = (NativeMethods.MouseHookData)Marshal.PtrToStructure(data, typeof(NativeMethods.MouseHookData));
            var distance = Math.Sqrt(Math.Pow(hookData.Position.X - gesture.StartX, 2) + Math.Pow(hookData.Position.Y - gesture.StartY, 2));
            gesture.Observation.EndX = hookData.Position.X;
            gesture.Observation.EndY = hookData.Position.Y;
            gesture.Observation.DurationMilliseconds = Math.Max(1, (int)(DateTime.UtcNow - gesture.StartedAtUtc).TotalMilliseconds);
            if (distance >= 8)
            {
                gesture.Observation.ActionType = "drag";
            }
            if (inputEventJournal != null)
            {
                gesture.Observation.InputEventId = inputEventJournal.Record(
                    gesture.Observation.ActionType,
                    gesture.Observation.TimestampUtc,
                    gesture.StartX,
                    gesture.StartY,
                    gesture.ControlPressed,
                    "recorded_action");
            }
            observations.Add(gesture.Observation);
            if (string.Equals(gesture.Observation.ActionType, "click", StringComparison.Ordinal))
            {
                // New Yuanbao sidebar menus open with a left click. Capture the
                // popup after dispatch so a later menu-item click can be bound
                // to the pre-dismissal UIA node instead of the page underneath.
                transientMenuObserver.ObserveAfterPointerAction(targetWindowHandle);
            }
        }

        private void RecordWheel(IntPtr data, bool controlPressed)
        {
            var hookData = (NativeMethods.MouseHookData)Marshal.PtrToStructure(data, typeof(NativeMethods.MouseHookData));
            NativeMethods.WindowRectangle rectangle;
            if (!IsInsideTarget(hookData.Position, out rectangle))
            {
                if (inputEventJournal != null)
                {
                    inputEventJournal.Record("scroll", DateTime.UtcNow, hookData.Position.X, hookData.Position.Y, controlPressed, "ignored_outside_target");
                }
                return;
            }
            var observation = CreateObservation("scroll", hookData.Position, rectangle, controlPressed);
            observation.WheelDelta = unchecked((short)((hookData.MouseData >> 16) & 0xffff));
            observations.Add(observation);
            // Scrolling a nested submenu changes both its visible items and
            // their bounds. Refresh the transient snapshot after every wheel
            // event so the next click uses the current menu geometry.
            transientMenuObserver.ObserveAfterPointerAction(targetWindowHandle);
        }

        private ClickObservation CreateObservation(
            string actionType,
            NativeMethods.Point point,
            NativeMethods.WindowRectangle rectangle,
            bool controlPressed,
            bool recordInputEvent = true)
        {
            var observation = new ClickObservation
            {
                ActionType = actionType,
                TimestampUtc = DateTime.UtcNow,
                X = point.X,
                Y = point.Y,
                WindowHandle = targetWindowHandle,
                WindowTitle = NativeMethods.ReadWindowTitle(targetWindowHandle),
                WindowRectangle = rectangle
            };
            if (recordInputEvent && inputEventJournal != null)
            {
                observation.InputEventId = inputEventJournal.Record(actionType, observation.TimestampUtc, point.X, point.Y, controlPressed, "recorded_action");
            }
            try
            {
                var immediateCapture = uiaCaptureService.CaptureTargetAtPoint(observation);
                observation.ImmediateTarget = immediateCapture.Target;
                observation.ImmediateLocator = immediateCapture.Locator;
                observation.HitLeafTarget = immediateCapture.HitTarget;
                observation.HitLeafLocator = immediateCapture.HitLocator;
            }
            catch (Exception error)
            {
                observation.ImmediateCaptureError = error.Message;
            }
            return observation;
        }

        private bool IsInsideTarget(NativeMethods.Point point, out NativeMethods.WindowRectangle rectangle)
        {
            rectangle = new NativeMethods.WindowRectangle();
            var currentWindow = NativeMethods.FindLargestVisibleWindowByProcessName(targetProcessName);
            if (currentWindow == IntPtr.Zero || !NativeMethods.GetWindowRect(currentWindow, out rectangle)) return false;
            targetWindowHandle = currentWindow;
            targetProcessId = NativeMethods.ReadWindowProcessId(currentWindow);
            return NativeMethods.IsPointOwnedByWindowOrProcess(
                point.X,
                point.Y,
                currentWindow,
                targetProcessName);
        }

        private void ConsumeObservations()
        {
            ClickObservation deferred = null;
            while (deferred != null || !observations.IsCompleted)
            {
                ClickObservation observation;
                if (deferred != null)
                {
                    observation = deferred;
                    deferred = null;
                }
                else if (!observations.TryTake(out observation, Timeout.Infinite)) continue;
                if (observation.ActionType == "scroll")
                {
                    ClickObservation next;
                    while (observations.TryTake(out next, 180))
                    {
                        if (next.ActionType == "scroll" &&
                            Math.Abs(next.X - observation.X) <= 40 && Math.Abs(next.Y - observation.Y) <= 40)
                        {
                            observation.WheelDelta += next.WheelDelta;
                            continue;
                        }
                        deferred = next;
                        break;
                    }
                }
                CaptureAction(observation);
                if (observation.Processed != null) observation.Processed.Set();
            }
        }

        private void CaptureAction(ClickObservation observation)
        {
            if (observation.EvidenceOnly && string.Equals(observation.ActionType, "input_start", StringComparison.Ordinal) &&
                observation.StartedInput != null)
            {
                if (pendingInput == null || !IsSameEditable(pendingInput, observation.StartedInput))
                {
                    pendingInput = CreatePendingInputContext(observation.StartedInput, observation);
                }
                return;
            }
            var processingStartedAt = DateTime.UtcNow;
            var processingStopwatch = Stopwatch.StartNew();
            var sequence = Interlocked.Increment(ref actionSequence);
            var actionId = "action_" + sequence.ToString("D4");
            var screenshotRelativePath = "frames/" + actionId + ".png";
            var hierarchyRelativePath = "hierarchy/" + actionId + ".json";
            var action = new TraceAction
            {
                Id = actionId,
                InputEventId = observation.InputEventId,
                Type = string.IsNullOrWhiteSpace(observation.ActionType) ? "click" : observation.ActionType,
                Timestamp = observation.TimestampUtc.ToString("O"),
                ScreenPoint = new TracePoint(observation.X, observation.Y),
                WindowPoint = new TracePoint(
                    observation.X - observation.WindowRectangle.Left,
                    observation.Y - observation.WindowRectangle.Top),
                WindowTitle = observation.WindowTitle,
                Screenshot = screenshotRelativePath,
                UiaSnapshot = hierarchyRelativePath,
                EndWindowPoint = observation.ActionType == "drag"
                    ? new TracePoint(observation.EndX - observation.WindowRectangle.Left, observation.EndY - observation.WindowRectangle.Top)
                    : null,
                WheelDelta = observation.WheelDelta,
                DurationMilliseconds = observation.DurationMilliseconds
                ,Key = observation.Key
            };
            TraceAction semanticInputAction = null;

            try
            {
                var screenshotStopwatch = Stopwatch.StartNew();
                SaveScreenshot(observation.WindowRectangle, Path.Combine(sessionDirectory, screenshotRelativePath));
                screenshotStopwatch.Stop();
                var capture = uiaCaptureService.Capture(observation);
                if (string.Equals(action.Type, "click", StringComparison.Ordinal) &&
                    observation.EventTransientTarget == null)
                {
                    Thread.Sleep(220);
                    var settledCapture = uiaCaptureService.Capture(observation);
                    if (settledCapture != null && settledCapture.Snapshot != null)
                    {
                        capture = settledCapture;
                    }
                }
                action.Target = observation.EventTransientTarget ??
                    observation.ImmediateTarget ?? capture.Target;
                action.Locator = observation.EventTransientLocator ??
                    observation.ImmediateLocator ?? capture.Locator;
                var settledWindowBounds = capture.Snapshot == null || capture.Snapshot.Nodes.Count == 0
                    ? null
                    : capture.Snapshot.Nodes[0].Bounds;
                if (observation.EventTransientTarget == null &&
                    capture.Target != null &&
                    Contains(capture.Target.Bounds, observation.X, observation.Y) &&
                    ControlResolver.IsBetter(capture.Target, action.Target, settledWindowBounds))
                {
                    action.Target = capture.Target;
                    action.Locator = capture.Locator;
                }
                PromoteSharedSidebarRowToHitLeaf(action, observation, capture.Snapshot);
                EnrichNamedContainerLocator(action, capture.Snapshot);
                var usedAfterSnapshotFallback = observation.EventTransientTarget == null &&
                    observation.ImmediateTarget == null;
                action.CaptureDiagnostics = new TraceCaptureDiagnostics
                {
                    MouseEvent = action.Type,
                    QueueDelayMilliseconds = Math.Max(0, (processingStartedAt - observation.TimestampUtc).TotalMilliseconds),
                    SelectionSource = observation.EventTransientTarget != null
                        ? "event_time_transient_menu_cache"
                        : observation.ImmediateTarget != null && object.ReferenceEquals(action.Target, observation.ImmediateTarget)
                            ? "semantic_immediate_hit_test"
                            : "semantic_settled_snapshot_promotion",
                    TransientDecision = observation.EventTransientDecision,
                    TransientCandidateCount = observation.EventTransientCandidateCount,
                    ImmediateTarget = TraceTargetDiagnostic.FromNode(observation.ImmediateTarget),
                    HitLeafTarget = TraceTargetDiagnostic.FromNode(observation.HitLeafTarget),
                    TransientTarget = TraceTargetDiagnostic.FromNode(observation.EventTransientTarget),
                    AfterSnapshotTarget = TraceTargetDiagnostic.FromNode(capture.Target),
                    ImmediateCaptureError = observation.ImmediateCaptureError,
                    TransientRevision = observation.EventTransientRevision,
                    ScreenshotDurationMilliseconds = screenshotStopwatch.Elapsed.TotalMilliseconds,
                    UiaDurationMilliseconds = capture.DurationMilliseconds,
                    UiaReadMode = capture.ReadMode
                };
                if (observation.EventTransientSnapshot != null)
                {
                    var transientRelativePath = "hierarchy/" + actionId + "-transient.json";
                    WriteJson(Path.Combine(sessionDirectory, transientRelativePath), observation.EventTransientSnapshot);
                    action.CaptureDiagnostics.TransientSnapshot = transientRelativePath;
                }
                action.EvidenceBeforeSnapshot = lastSnapshotRelativePath;
                action.EvidenceAfterSnapshot = hierarchyRelativePath;
                if (!usedAfterSnapshotFallback && IsInterfaceAction(action.Type) &&
                    capture.Target != null && !SameSemanticTarget(action.Target, capture.Target) &&
                    !SnapshotContainsSemanticTarget(lastSnapshot, capture.Target) &&
                    !IsSharedSidebarRow(capture.Target, capture.Locator))
                {
                    action.Effects.Add(new TraceActionEffect
                    {
                        Type = "target_appeared",
                        Target = capture.Target,
                        Locator = capture.Locator,
                        Source = "before_after_uia_diff"
                    });
                }
                if (string.Equals(action.Type, "click", StringComparison.Ordinal) && action.Effects.Count == 0)
                {
                    var appearedInput = FindNewlyAppearedInput(lastSnapshot, capture.Snapshot);
                    if (appearedInput != null)
                    {
                        action.Effects.Add(new TraceActionEffect
                        {
                            Type = "target_appeared",
                            Target = appearedInput,
                            Locator = BuildSnapshotLocator(capture.Snapshot, appearedInput, observation.WindowRectangle),
                            Source = "before_after_new_input_diff"
                        });
                    }
                }

                if (observation.StartedInput == null && string.Equals(action.Type, "click", StringComparison.Ordinal))
                {
                    try
                    {
                        var focusedEditable = uiaCaptureService.CaptureFocusedEditable(observation);
                        if (focusedEditable != null && Contains(focusedEditable.Bounds, observation.X, observation.Y))
                        {
                            observation.StartedInput = focusedEditable;
                        }
                    }
                    catch
                    {
                    }
                }

                if (observation.CommittedInput == null && pendingInput != null && !pendingInput.IsChatEditor)
                {
                    try
                    {
                        observation.CommittedInput = uiaCaptureService.CaptureEditableAtBounds(observation, pendingInput.Bounds);
                        if (observation.CommittedInput != null) observation.InputCommitSource = "pending_bounds_at_commit";
                    }
                    catch
                    {
                    }
                }

                if (pendingInput != null && !pendingInput.IsChatEditor &&
                    ShouldCommitGenericInput(
                        pendingInput.Bounds,
                        pendingInput.InitialText,
                        observation.CommittedInput,
                        observation.StartedInput,
                        observation.CommitInputByKeyboard))
                {
                    semanticInputAction = BuildGenericInputAction(
                        actionId, observation, pendingInput, observation.CommittedInput.Text,
                        lastSnapshotRelativePath, hierarchyRelativePath, screenshotRelativePath);
                    pendingInput = null;
                }
                else if ((observation.CommitInputByKeyboard || IsSendButton(action.Target, action.Locator)) &&
                    pendingInput != null && pendingInput.IsChatEditor)
                {
                    var messageResult = messageDiffService.FindCommittedUserMessage(lastSnapshot, capture.Snapshot);
                    for (var retry = 0; retry < 3 && messageResult.NeedsReview; retry++)
                    {
                        Thread.Sleep(250);
                        capture = uiaCaptureService.Capture(observation);
                        messageResult = messageDiffService.FindCommittedUserMessage(lastSnapshot, capture.Snapshot);
                    }
                    if (!string.IsNullOrWhiteSpace(messageResult.Text))
                    {
                        semanticInputAction = BuildSemanticInputAction(
                            actionId,
                            observation,
                            pendingInput,
                            messageResult,
                            lastSnapshotRelativePath,
                            hierarchyRelativePath,
                            screenshotRelativePath);
                    }
                    else
                    {
                        action.NeedsReview = true;
                        action.ReviewReason = "输入文本未沉淀：" + (messageResult.Reason ?? "未提取到有效文本");
                    }
                    pendingInput = null;
                }

                WriteJson(Path.Combine(sessionDirectory, hierarchyRelativePath), capture.Snapshot);
                lastSnapshot = capture.Snapshot;
                lastSnapshotRelativePath = hierarchyRelativePath;
                if (observation.StartedInput != null && (pendingInput == null || !IsSameEditable(pendingInput, observation.StartedInput)))
                {
                    pendingInput = CreatePendingInputContext(observation.StartedInput, observation);
                }
                else if (IsInputEditor(action.Target, action.Locator))
                {
                    pendingInput = new PendingInputContext
                    {
                        Target = action.Target,
                        Locator = action.Locator,
                        ScreenPoint = action.ScreenPoint,
                        WindowPoint = action.WindowPoint,
                        IsChatEditor = true
                    };
                }
                PublishStatus("已记录 " + actionId + "：" + DescribeTarget(action.Target));
                if (!string.IsNullOrWhiteSpace(observation.ImmediateCaptureError))
                {
                    action.CaptureError = "点击瞬时目标降级为异步解析：" + observation.ImmediateCaptureError;
                }
            }
            catch (Exception error)
            {
                action.CaptureError = error.Message;
                PublishStatus(actionId + " 采集不完整：" + error.Message);
            }

            processingStopwatch.Stop();
            if (action.CaptureDiagnostics != null)
            {
                action.CaptureDiagnostics.ProcessingDurationMilliseconds = processingStopwatch.Elapsed.TotalMilliseconds;
            }

            lock (traceLock)
            {
                var delayedInputEffect = action.Effects.FirstOrDefault(effect =>
                    string.Equals(effect.Source, "before_after_new_input_diff", StringComparison.Ordinal) &&
                    SameSemanticTarget(effect.Target, action.Target));
                if (delayedInputEffect != null && trace.Actions.Count > 0)
                {
                    var trigger = trace.Actions[trace.Actions.Count - 1];
                    if (string.Equals(trigger.Type, "click", StringComparison.Ordinal) &&
                        !trigger.Effects.Any(effect => string.Equals(effect.Type, "target_appeared", StringComparison.Ordinal)))
                    {
                        trigger.Effects.Add(new TraceActionEffect
                        {
                            Type = delayedInputEffect.Type,
                            Target = delayedInputEffect.Target,
                            Locator = delayedInputEffect.Locator,
                            Source = "next_action_observed_input_appeared"
                        });
                        action.Effects.Remove(delayedInputEffect);
                    }
                }
                if (semanticInputAction != null)
                {
                    ApplyActiveBranchUnsafe(semanticInputAction);
                    trace.Actions.Add(semanticInputAction);
                }
                if (!observation.EvidenceOnly)
                {
                    ApplyActiveBranchUnsafe(action);
                    trace.Actions.Add(action);
                }
                foreach (var assertion in observation.EvidenceOnly
                    ? new List<TraceAssertion>()
                    : pendingAfterNextActionAssertions)
                {
                    assertion.AfterActionId = action.Id;
                    trace.Assertions.Add(assertion);
                }
                if (!observation.EvidenceOnly) pendingAfterNextActionAssertions.Clear();
                WriteTraceUnsafe();
            }
        }

        private static bool IsInterfaceAction(string actionType)
        {
            return string.Equals(actionType, "click", StringComparison.Ordinal) ||
                string.Equals(actionType, "right_click", StringComparison.Ordinal) ||
                string.Equals(actionType, "key_press", StringComparison.Ordinal);
        }

        private static bool SnapshotContainsSemanticTarget(UiaSnapshot snapshot, UiaNode target)
        {
            return snapshot != null && target != null && snapshot.Nodes.Any(node => SameSemanticTarget(node, target));
        }

        private static UiaNode FindNewlyAppearedInput(UiaSnapshot before, UiaSnapshot after)
        {
            if (after == null || after.Nodes == null) return null;
            var beforeNodes = before == null || before.Nodes == null ? new List<UiaNode>() : before.Nodes;
            return after.Nodes
                .Where(node => node != null && !node.Offscreen && node.Enabled && node.Bounds != null &&
                    node.Bounds.Width > 0 && node.Bounds.Height > 0 &&
                    string.Equals(node.ControlType, "Edit", StringComparison.OrdinalIgnoreCase) &&
                    !beforeNodes.Any(oldNode => SameSemanticTarget(oldNode, node)))
                .OrderByDescending(node => node.Focusable)
                .ThenBy(node => node.Bounds.Width * node.Bounds.Height)
                .FirstOrDefault();
        }

        private static LocatorBundle BuildSnapshotLocator(
            UiaSnapshot snapshot,
            UiaNode node,
            NativeMethods.WindowRectangle windowRectangle)
        {
            var locator = new LocatorBundle
            {
                AutomationId = node.AutomationId,
                Name = node.Name,
                ControlType = node.ControlType,
                ClassName = BaseClassName(node.ClassName),
                FallbackWindowPoint = node.Bounds == null ? null : new TracePoint(
                    (int)Math.Round(node.Bounds.X + node.Bounds.Width / 2 - windowRectangle.Left),
                    (int)Math.Round(node.Bounds.Y + node.Bounds.Height / 2 - windowRectangle.Top))
            };
            if (snapshot == null || snapshot.Nodes == null) return locator;
            var byIndex = snapshot.Nodes.ToDictionary(item => item.Index);
            var ancestors = new List<UiaNode>();
            var parentIndex = node.ParentIndex;
            for (var depth = 0; depth < 8 && parentIndex >= 0 && byIndex.ContainsKey(parentIndex); depth++)
            {
                var parent = byIndex[parentIndex];
                ancestors.Add(parent);
                parentIndex = parent.ParentIndex;
            }
            ancestors.Reverse();
            foreach (var ancestor in ancestors)
            {
                locator.AncestorPath.Add(new LocatorSegment
                {
                    AutomationId = ancestor.AutomationId,
                    Name = ancestor.Name,
                    ControlType = ancestor.ControlType,
                    ClassName = BaseClassName(ancestor.ClassName)
                });
            }
            locator.AncestorPath.Add(new LocatorSegment
            {
                AutomationId = node.AutomationId,
                Name = node.Name,
                ControlType = node.ControlType,
                ClassName = BaseClassName(node.ClassName)
            });
            return locator;
        }

        private static bool SameSemanticTarget(UiaNode left, UiaNode right)
        {
            if (left == null || right == null) return false;
            if (!string.IsNullOrWhiteSpace(left.AutomationId) || !string.IsNullOrWhiteSpace(right.AutomationId))
            {
                return string.Equals(left.AutomationId, right.AutomationId, StringComparison.Ordinal) &&
                    string.Equals(left.ControlType, right.ControlType, StringComparison.Ordinal);
            }
            if (!string.IsNullOrWhiteSpace(left.Name) || !string.IsNullOrWhiteSpace(right.Name))
            {
                return string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
                    string.Equals(left.ControlType, right.ControlType, StringComparison.Ordinal);
            }
            return string.Equals(left.ClassName, right.ClassName, StringComparison.Ordinal) &&
                string.Equals(left.ControlType, right.ControlType, StringComparison.Ordinal);
        }

        private static bool HasExplicitAssertionIdentity(UiaNode target)
        {
            if (target == null) return false;
            if (string.Equals(target.ControlType, "Document", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(target.ControlType, "Window", StringComparison.OrdinalIgnoreCase)) return false;
            return !string.IsNullOrWhiteSpace(target.AutomationId) || !string.IsNullOrWhiteSpace(target.Name);
        }

        private static void PromoteSharedSidebarRowToHitLeaf(
            TraceAction action,
            ClickObservation observation,
            UiaSnapshot snapshot)
        {
            if (action == null || observation == null ||
                !(string.Equals(action.Type, "click", StringComparison.Ordinal) ||
                  string.Equals(action.Type, "right_click", StringComparison.Ordinal)) ||
                !IsSharedSidebarRow(action.Target, action.Locator) ||
                !HasBusinessIdentity(observation.HitLeafTarget)) return;

            var originalRow = action.Target;
            action.Target = observation.HitLeafTarget;
            action.Locator = observation.HitLeafLocator;
            if (action.Locator == null) return;

            ApplySidebarSectionScope(action.Locator, originalRow, snapshot);
        }

        private static bool HasBusinessIdentity(UiaNode target)
        {
            if (target == null ||
                string.Equals(target.ControlType, "Pane", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(target.ControlType, "Window", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(target.ControlType, "Document", StringComparison.OrdinalIgnoreCase)) return false;
            return !string.IsNullOrWhiteSpace(target.AutomationId) || !string.IsNullOrWhiteSpace(target.Name);
        }

        private static bool IsSharedSidebarRow(UiaNode target, LocatorBundle locator)
        {
            var className = BaseClassName(target == null ? null : target.ClassName) ??
                BaseClassName(locator == null ? null : locator.ClassName);
            return string.Equals(className, "Item_chatOrProjectItem", StringComparison.Ordinal) ||
                string.Equals(className, "ProjectsSection_item", StringComparison.Ordinal);
        }

        private static void ApplySidebarSectionScope(LocatorBundle locator, UiaNode row, UiaSnapshot snapshot)
        {
            if (locator == null || row == null || row.Bounds == null || snapshot == null || snapshot.Nodes == null) return;
            var headerNames = new[] { "\u5206\u7ec4", "\u6700\u8fd1", "\u804a\u5929" };
            var headers = snapshot.Nodes
                .Where(node => node != null && node.Bounds != null && !node.Offscreen &&
                    headerNames.Contains((node.Name ?? string.Empty).Trim(), StringComparer.Ordinal) &&
                    node.Bounds.Width > 0 && node.Bounds.Height > 0)
                .GroupBy(node => new { Name = node.Name.Trim(), Y = Math.Round(node.Bounds.Y, 0) })
                .Select(group => group.OrderByDescending(node => node.Bounds.Width).First())
                .OrderBy(node => node.Bounds.Y)
                .ToList();
            var start = headers.LastOrDefault(node => node.Bounds.Y + node.Bounds.Height <= row.Bounds.Y + 2);
            if (start == null) return;
            var end = headers.FirstOrDefault(node => node.Bounds.Y > row.Bounds.Y + 2);
            locator.SectionStartName = start.Name.Trim();
            locator.SectionEndName = end == null ? null : end.Name.Trim();
        }

        private static void EnrichNamedContainerLocator(TraceAction action, UiaSnapshot snapshot)
        {
            if (action == null || action.Target == null || action.Locator == null || snapshot == null ||
                !string.IsNullOrWhiteSpace(action.Locator.AutomationId) ||
                !string.IsNullOrWhiteSpace(action.Locator.Name) ||
                !string.Equals(BaseClassName(action.Target.ClassName), "item_itemContainer", StringComparison.Ordinal)) return;
            var nodesByIndex = snapshot.Nodes.ToDictionary(node => node.Index);
            var container = FindSnapshotEquivalent(action.Target, snapshot.Nodes);
            if (container == null) return;
            var descendant = snapshot.Nodes
                .Where(node => node != null && node.Bounds != null && !node.Offscreen &&
                    string.Equals(node.ControlType, "Text", StringComparison.OrdinalIgnoreCase) &&
                    IsMeaningfulConditionName(node.Name) &&
                    IsDescendantOfSnapshotNode(node, container, nodesByIndex))
                .OrderBy(node => node.Depth - container.Depth)
                .ThenBy(node => node.Bounds.Y)
                .ThenBy(node => node.Bounds.X)
                .FirstOrDefault();
            if (descendant == null) return;
            action.Locator.DescendantName = descendant.Name.Trim();
            action.Locator.DescendantControlType = descendant.ControlType;
        }

        private static TraceAction BuildSemanticInputAction(
            string relatedActionId,
            ClickObservation observation,
            PendingInputContext input,
            MessageDiffResult result,
            string beforeSnapshot,
            string afterSnapshot,
            string screenshot)
        {
            return new TraceAction
            {
                Id = relatedActionId + "_input",
                Type = "input_text",
                Timestamp = observation.TimestampUtc.ToString("O"),
                ScreenPoint = input.ScreenPoint,
                WindowPoint = input.WindowPoint,
                WindowTitle = observation.WindowTitle,
                Target = input.Target,
                Locator = input.Locator,
                Screenshot = screenshot,
                UiaSnapshot = afterSnapshot,
                Text = result.Text,
                Derivation = "uia_user_message_diff",
                Confidence = result.NeedsReview ? 0 : 1,
                NeedsReview = result.NeedsReview,
                ReviewReason = result.NeedsReview ? result.Reason : null,
                EvidenceBeforeSnapshot = beforeSnapshot,
                EvidenceAfterSnapshot = afterSnapshot
            };
        }

        private static TraceAction BuildGenericInputAction(
            string relatedActionId,
            ClickObservation observation,
            PendingInputContext input,
            string finalText,
            string beforeSnapshot,
            string afterSnapshot,
            string screenshot)
        {
            var normalized = (finalText ?? string.Empty).TrimEnd('\r', '\n');
            if (string.Equals(normalized, input.InitialText ?? string.Empty, StringComparison.Ordinal)) return null;
            return new TraceAction
            {
                Id = relatedActionId + "_input",
                Type = "input_text",
                Timestamp = observation.TimestampUtc.ToString("O"),
                ScreenPoint = input.ScreenPoint,
                WindowPoint = input.WindowPoint,
                WindowTitle = observation.WindowTitle,
                Target = input.Target,
                Locator = input.Locator,
                Screenshot = screenshot,
                UiaSnapshot = afterSnapshot,
                Text = normalized,
                Derivation = "uia_edit_value_" + (string.IsNullOrWhiteSpace(observation.InputCommitSource)
                    ? "at_commit"
                    : observation.InputCommitSource),
                Confidence = 1,
                EvidenceBeforeSnapshot = beforeSnapshot,
                EvidenceAfterSnapshot = afterSnapshot
            };
        }

        private static bool IsSameEditable(PendingInputContext pending, FocusedEditableCapture started)
        {
            return pending != null && IsSameBounds(pending.Bounds, started == null ? null : started.Bounds);
        }

        internal static bool ShouldCommitGenericInput(
            NodeBounds pendingBounds,
            string initialText,
            FocusedEditableCapture committed,
            FocusedEditableCapture started,
            bool commitByKeyboard)
        {
            if (committed == null || !IsSameBounds(pendingBounds, committed.Bounds)) return false;
            if (commitByKeyboard || !IsSameBounds(pendingBounds, started == null ? null : started.Bounds)) return true;
            var normalizedInitial = (initialText ?? string.Empty).TrimEnd('\r', '\n');
            var normalizedCurrent = (committed.Text ?? string.Empty).TrimEnd('\r', '\n');
            return !string.Equals(normalizedInitial, normalizedCurrent, StringComparison.Ordinal);
        }

        internal static FocusedEditableCapture PreferPreInteractionInput(
            FocusedEditableCapture preInteraction,
            FocusedEditableCapture afterInteraction)
        {
            if (preInteraction != null && afterInteraction != null &&
                IsSameBounds(preInteraction.Bounds, afterInteraction.Bounds))
            {
                var before = (preInteraction.Text ?? string.Empty).TrimEnd('\r', '\n');
                var after = (afterInteraction.Text ?? string.Empty).TrimEnd('\r', '\n');
                if (!string.Equals(before, after, StringComparison.Ordinal) && after.Length >= before.Length)
                {
                    return afterInteraction;
                }
            }
            return preInteraction ?? afterInteraction;
        }

        private bool TrySetKeyboardInputOrigin(FocusedEditableCapture candidate)
        {
            lock (keyboardInputOriginLock)
            {
                if (IsSameEditable(keyboardInputOrigin, candidate)) return false;
                keyboardInputOrigin = candidate;
                return true;
            }
        }

        private static bool IsSameEditable(FocusedEditableCapture first, FocusedEditableCapture second)
        {
            return first != null && second != null && IsSameBounds(first.Bounds, second.Bounds);
        }

        private static bool IsSameBounds(NodeBounds first, NodeBounds second)
        {
            if (first == null || second == null) return false;
            if (Math.Abs(first.X - second.X) <= 2 &&
                Math.Abs(first.Y - second.Y) <= 2 &&
                Math.Abs(first.Width - second.Width) <= 2 &&
                Math.Abs(first.Height - second.Height) <= 2) return true;
            if (Math.Abs(first.X - second.X) > 6 || Math.Abs(first.Y - second.Y) > 6 ||
                Math.Abs(first.Height - second.Height) > 6) return false;
            var overlapLeft = Math.Max(first.X, second.X);
            var overlapRight = Math.Min(first.X + first.Width, second.X + second.Width);
            var overlap = Math.Max(0, overlapRight - overlapLeft);
            var smallerWidth = Math.Min(first.Width, second.Width);
            return smallerWidth > 0 && overlap / smallerWidth >= 0.9 &&
                Math.Abs(first.Width - second.Width) <= Math.Max(32, smallerWidth * 0.25);
        }

        private static PendingInputContext CreatePendingInputContext(
            FocusedEditableCapture started,
            ClickObservation observation)
        {
            var screenX = started.Bounds == null
                ? observation.X
                : (int)Math.Round(started.Bounds.X + started.Bounds.Width / 2);
            var screenY = started.Bounds == null
                ? observation.Y
                : (int)Math.Round(started.Bounds.Y + started.Bounds.Height / 2);
            return new PendingInputContext
            {
                Target = started.Target,
                Locator = started.Locator,
                ScreenPoint = new TracePoint(screenX, screenY),
                WindowPoint = new TracePoint(
                    screenX - observation.WindowRectangle.Left,
                    screenY - observation.WindowRectangle.Top),
                InitialText = started.Text,
                Bounds = started.Bounds,
                IsChatEditor = IsInputEditor(started.Target, started.Locator)
            };
        }

        internal static bool IsInputEditor(UiaNode target, LocatorBundle locator)
        {
            if (target != null)
            {
                if (string.Equals(target.AutomationId, "searchbar-editor", StringComparison.Ordinal)) return true;
                if (!string.IsNullOrWhiteSpace(target.ClassName) &&
                    target.ClassName.IndexOf("ql-editor", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return locator != null && string.Equals(locator.AutomationId, "searchbar-editor", StringComparison.Ordinal);
        }

        internal static bool IsSendButton(UiaNode target, LocatorBundle locator)
        {
            if (!MatchesAutomationId(target, locator, "yuanbao-send-btn")) return false;
            return !HasSemanticName(target, locator, "停止回答");
        }

        private static bool HasSemanticName(UiaNode target, LocatorBundle locator, string name)
        {
            if (target != null && string.Equals(target.Name, name, StringComparison.Ordinal)) return true;
            if (locator == null) return false;
            if (string.Equals(locator.Name, name, StringComparison.Ordinal)) return true;
            foreach (var segment in locator.AncestorPath)
            {
                if (string.Equals(segment.Name, name, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static bool MatchesAutomationId(UiaNode target, LocatorBundle locator, string automationId)
        {
            if (target != null && string.Equals(target.AutomationId, automationId, StringComparison.Ordinal)) return true;
            if (locator == null) return false;
            if (string.Equals(locator.AutomationId, automationId, StringComparison.Ordinal)) return true;
            foreach (var segment in locator.AncestorPath)
            {
                if (string.Equals(segment.AutomationId, automationId, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static void SaveScreenshot(NativeMethods.WindowRectangle rectangle, string outputPath)
        {
            if (rectangle.Width <= 0 || rectangle.Height <= 0)
            {
                throw new InvalidOperationException("元宝窗口尺寸无效");
            }
            using (var bitmap = new Bitmap(rectangle.Width, rectangle.Height, PixelFormat.Format24bppRgb))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(rectangle.Left, rectangle.Top, 0, 0, bitmap.Size);
                bitmap.Save(outputPath, ImageFormat.Png);
            }
        }

        private void WriteTrace()
        {
            lock (traceLock)
            {
                WriteTraceUnsafe();
            }
        }

        private void WriteTraceUnsafe()
        {
            WriteJson(Path.Combine(sessionDirectory, "raw-trace.json"), trace);
        }

        private static void WriteJson<T>(string outputPath, T value)
        {
            var temporaryPath = outputPath + ".tmp";
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = File.Create(temporaryPath))
            {
                serializer.WriteObject(stream, value);
            }
            if (File.Exists(outputPath)) File.Delete(outputPath);
            File.Move(temporaryPath, outputPath);
        }

        private static string DescribeTarget(UiaNode target)
        {
            if (target == null) return "未解析到控件，保留坐标兜底";
            if (!string.IsNullOrWhiteSpace(target.AutomationId)) return target.AutomationId;
            if (!string.IsNullOrWhiteSpace(target.Name)) return target.Name;
            return target.ControlType;
        }

        private static string SanitizeFileName(string value)
        {
            var result = string.IsNullOrWhiteSpace(value) ? "元宝测试用例" : value.Trim();
            foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
            {
                result = result.Replace(invalidCharacter, '_');
            }
            return result;
        }

        private void PublishStatus(string message)
        {
            var handler = StatusChanged;
            if (handler != null) handler(message);
        }

        public void Dispose()
        {
            if (mouseHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(mouseHook);
                mouseHook = IntPtr.Zero;
            }
            if (keyboardHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(keyboardHook);
                keyboardHook = IntPtr.Zero;
            }
            if (observations != null) observations.Dispose();
            if (inputEventJournal != null) inputEventJournal.Dispose();
            transientMenuObserver.Dispose();
        }

        private static bool Contains(NodeBounds bounds, int x, int y)
        {
            return bounds != null && bounds.Width > 0 && bounds.Height > 0 &&
                x >= bounds.X && x <= bounds.X + bounds.Width &&
                y >= bounds.Y && y <= bounds.Y + bounds.Height;
        }

        private static string DescribeAssertionTarget(UiaNode target)
        {
            if (target == null) return "所选内容";
            if (!string.IsNullOrWhiteSpace(target.Name)) return target.Name.Length > 60 ? target.Name.Substring(0, 60) : target.Name;
            if (!string.IsNullOrWhiteSpace(target.AutomationId)) return target.AutomationId;
            return string.IsNullOrWhiteSpace(target.ControlType) ? "所选内容" : target.ControlType;
        }

        private static void DeleteIfExists(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path);
        }

        private sealed class PendingInputContext
        {
            internal UiaNode Target;
            internal LocatorBundle Locator;
            internal TracePoint ScreenPoint;
            internal TracePoint WindowPoint;
            internal string InitialText;
            internal NodeBounds Bounds;
            internal bool IsChatEditor;
        }

        private sealed class PendingPointerGesture
        {
            internal ClickObservation Observation;
            internal DateTime StartedAtUtc;
            internal int StartX;
            internal int StartY;
            internal bool ControlPressed;
        }
    }
}
