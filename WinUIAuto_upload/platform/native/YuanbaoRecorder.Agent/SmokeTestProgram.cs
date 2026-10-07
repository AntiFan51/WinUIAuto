#if SMOKE_TEST
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace YuanbaoRecorder.Agent
{
    internal static class SmokeTestProgram
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 1 && (args[0] == "--verify-yuanbao-launcher" || args[0] == "--verify-yuanbao-restart"))
            {
                return VerifyYuanbaoLauncher(args[0] == "--verify-yuanbao-restart");
            }
            if (args.Length == 3 && args[0] == "--verify-message-diff")
            {
                return VerifyRecordedSnapshots(args[1], args[2]);
            }
            if (args.Length == 1 && args[0] == "--verify-highlight-scopes")
            {
                try
                {
                    VerifyAssertionScopeVisualization();
                    return 0;
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine(error);
                    return 1;
                }
            }

            Exception testError = null;
            UiaCaptureResult captureResult = null;
            var ready = new ManualResetEventSlim(false);
            Form fixture = null;
            Button sendButton = null;
            TextBox editableTextBox = null;
            Label replayResultLabel = null;
            Form replayPopup = null;

            var uiThread = new Thread(delegate()
            {
                try
                {
                    fixture = new Form
                    {
                        Text = "腾讯元宝 UIA 冒烟测试",
                        StartPosition = FormStartPosition.Manual,
                        Location = new Point(160, 160),
                        Size = new Size(600, 420)
                    };
                    sendButton = new Button
                    {
                        Name = "yuanbao-send-btn",
                        Text = "发送",
                        AccessibleName = "发送",
                        Location = new Point(430, 290),
                        Size = new Size(100, 45)
                    };
                    editableTextBox = new TextBox
                    {
                        Name = "group-name-editor",
                        Text = "测试分组",
                        Location = new Point(40, 80),
                        Size = new Size(240, 30)
                    };
                    replayResultLabel = new Label
                    {
                        Text = "派已创建",
                        AccessibleName = "派已创建",
                        Location = new Point(40, 140),
                        Size = new Size(160, 30),
                        Visible = false
                    };
                    replayPopup = new Form
                    {
                        Text = string.Empty,
                        FormBorderStyle = FormBorderStyle.FixedToolWindow,
                        ShowInTaskbar = false,
                        StartPosition = FormStartPosition.Manual,
                        Location = new Point(590, 470),
                        Size = new Size(180, 90)
                    };
                    var createMenuItem = new Button
                    {
                        Text = "创建元宝派",
                        AccessibleName = "创建元宝派",
                        Dock = DockStyle.Fill
                    };
                    createMenuItem.Click += delegate
                    {
                        replayResultLabel.Visible = true;
                        replayPopup.Hide();
                    };
                    replayPopup.Controls.Add(createMenuItem);
                    fixture.Controls.Add(editableTextBox);
                    fixture.Controls.Add(sendButton);
                    fixture.Controls.Add(replayResultLabel);
                    fixture.Shown += delegate { ready.Set(); };
                    Application.Run(fixture);
                }
                catch (Exception error)
                {
                    testError = error;
                    ready.Set();
                }
            });
            uiThread.SetApartmentState(ApartmentState.STA);
            uiThread.Start();

            if (!ready.Wait(TimeSpan.FromSeconds(5)))
            {
                Console.Error.WriteLine("测试窗口启动超时");
                return 1;
            }
            if (testError != null)
            {
                Console.Error.WriteLine(testError);
                return 1;
            }

            try
            {
                IntPtr windowHandle = IntPtr.Zero;
                Point buttonCenter = Point.Empty;
                Point editorCenter = Point.Empty;
                fixture.Invoke((Action)delegate
                {
                    windowHandle = fixture.Handle;
                    buttonCenter = sendButton.PointToScreen(new Point(sendButton.Width / 2, sendButton.Height / 2));
                    editorCenter = editableTextBox.PointToScreen(new Point(editableTextBox.Width / 2, editableTextBox.Height / 2));
                });

                NativeMethods.WindowRectangle windowRectangle;
                if (!NativeMethods.GetWindowRect(windowHandle, out windowRectangle))
                {
                    throw new InvalidOperationException("无法读取测试窗口坐标");
                }

                var observation = new ClickObservation
                {
                    TimestampUtc = DateTime.UtcNow,
                    X = buttonCenter.X,
                    Y = buttonCenter.Y,
                    WindowHandle = windowHandle,
                    WindowTitle = fixture.Text,
                    WindowRectangle = windowRectangle
                };
                var captureService = new UiaCaptureService();
                var editorObservation = new ClickObservation
                {
                    TimestampUtc = DateTime.UtcNow,
                    X = editorCenter.X,
                    Y = editorCenter.Y,
                    WindowHandle = windowHandle,
                    WindowTitle = fixture.Text,
                    WindowRectangle = windowRectangle
                };
                var editableCapture = captureService.CaptureEditableAtPoint(editorObservation);
                if (editableCapture == null || editableCapture.Text != "测试分组" || editableCapture.Locator == null)
                {
                    throw new InvalidOperationException("通用编辑框值采集失败");
                }
                fixture.Invoke((Action)delegate
                {
                    editableTextBox.Text = "弹层搜索词";
                    sendButton.Focus();
                });
                var unfocusedEditableCapture = captureService.CaptureEditableAtBounds(
                    observation,
                    editableCapture.Bounds);
                if (unfocusedEditableCapture == null || unfocusedEditableCapture.Text != "弹层搜索词")
                {
                    throw new InvalidOperationException("普通输入框失焦后按原范围回读失败");
                }
                fixture.Invoke((Action)delegate
                {
                    editableTextBox.Text = "测试分组";
                });
                var immediateTarget = captureService.CaptureTargetAtPoint(observation);
                captureResult = captureService.Capture(observation);

                if (immediateTarget.Target == null ||
                    !string.Equals(immediateTarget.Target.ControlType, "Button", StringComparison.OrdinalIgnoreCase) ||
                    immediateTarget.Locator == null)
                {
                    throw new InvalidOperationException("点击瞬时 UIA 命中失败");
                }

                if (captureResult.Snapshot.NodeCount < 2)
                {
                    throw new InvalidOperationException("UIA 控件树节点数量不足");
                }
                if (captureResult.Target == null)
                {
                    throw new InvalidOperationException("未解析到点击目标");
                }
                if (!string.Equals(captureResult.Target.ControlType, "Button", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("目标控件类型错误：" + captureResult.Target.ControlType);
                }
                if (captureResult.Locator == null || captureResult.Locator.AncestorPath.Count == 0)
                {
                    throw new InvalidOperationException("未生成多级 Locator");
                }

                VerifyTransientMenuTarget(captureService);
                VerifyInputEventJournal();
                VerifySemanticControlRecognition();
                VerifyAssertionScopeVisualization();
                VerifyMessageDiff();
                VerifyGenericInputCommitIsolation();
                VerifyEditableTextSelection();
                VerifyReplayTaskDeserialization();
                VerifyExplorerShortcutActivation();
                VerifyAssertions(windowHandle, fixture, replayPopup);
                VerifyReplayExecution(
                    fixture,
                    editableTextBox,
                    replayPopup,
                    editableCapture.Locator,
                    captureResult.Locator);
                Console.WriteLine("UIA_SMOKE_OK");
                Console.WriteLine("nodes=" + captureResult.Snapshot.NodeCount);
                Console.WriteLine("target_name=" + captureResult.Target.Name);
                Console.WriteLine("target_automation_id=" + captureResult.Target.AutomationId);
                Console.WriteLine("target_control_type=" + captureResult.Target.ControlType);
                Console.WriteLine("locator_depth=" + captureResult.Locator.AncestorPath.Count);
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
            finally
            {
                if (fixture != null && !fixture.IsDisposed)
                {
                    fixture.BeginInvoke((Action)delegate { fixture.Close(); });
                }
                uiThread.Join(TimeSpan.FromSeconds(3));
                ready.Dispose();
            }
        }

        private static int VerifyYuanbaoLauncher(bool restart)
        {
            var window = IntPtr.Zero;
            try
            {
                var task = new ReplayTask
                {
                    Id = "yuanbao-launcher-smoke",
                    CaseId = "yuanbao-launcher-smoke",
                    CaseName = "元宝启动与 UIA 绑定测试",
                    Target = new ReplayTarget
                    {
                        ProcessName = "yuanbao",
                        RestartBeforeReplay = restart
                    }
                };
                var result = new ReplayExecutor(false).Execute(task, CancellationToken.None, delegate { });
                if (!result.Success) throw new InvalidOperationException(result.Error);
                window = NativeMethods.FindLargestVisibleWindowByProcessName("yuanbao");
                if (window == IntPtr.Zero) throw new InvalidOperationException("元宝启动后未返回主窗口句柄");
                if (!NativeMethods.IsForegroundProcessName("yuanbao") && !NativeMethods.IsWindowExposed(window, "yuanbao"))
                {
                    throw new InvalidOperationException("元宝启动并绑定 UIA 后不可见或不可交互");
                }
                var rectangle = new NativeMethods.WindowRectangle();
                if (!NativeMethods.GetWindowRect(window, out rectangle))
                {
                    throw new InvalidOperationException("无法读取元宝主窗口边界");
                }
                ReplayExecutor.EnsureSafeMousePoint(
                    rectangle.Left + 193,
                    rectangle.Top + 199,
                    window,
                    "yuanbao");
                var executablePath = ApplicationLauncher.ResolveExecutablePath("yuanbao");
                if (string.IsNullOrWhiteSpace(executablePath)) throw new InvalidOperationException("未解析到元宝启动路径");
                Console.WriteLine("YUANBAO_LAUNCHER_OK");
                Console.WriteLine("path=" + executablePath);
                Console.WriteLine("window=" + window);
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
            finally
            {
                NativeMethods.ReleaseReplayWindow(window);
            }
        }

        private static void VerifyExplorerShortcutActivation()
        {
            var fixtureDirectory = Path.Combine(Path.GetTempPath(), "yuanbao-launcher-test");
            var shortcutPath = Path.Combine(fixtureDirectory, "Yuanbao.lnk");
            var executablePath = Path.Combine(fixtureDirectory, "yuanbao.exe");
            var shortcut = ApplicationLauncher.BuildLaunchStartInfo(shortcutPath, executablePath);
            if (!string.Equals(Path.GetFileName(shortcut.FileName), "explorer.exe", StringComparison.OrdinalIgnoreCase) ||
                shortcut.UseShellExecute || shortcut.Arguments != "\"" + shortcutPath + "\"")
            {
                throw new InvalidOperationException("Shortcut activation must be delegated to Windows Explorer");
            }
            var executable = ApplicationLauncher.BuildLaunchStartInfo(executablePath, executablePath);
            if (!executable.UseShellExecute || !string.Equals(executable.FileName, executablePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Executable activation must retain shell execution");
            }
            Console.WriteLine("EXPLORER_SHORTCUT_ACTIVATION_SMOKE_OK");
        }


        private static void VerifyReplayExecution(
            Form fixture,
            TextBox editableTextBox,
            Form replayPopup,
            LocatorBundle editorLocator,
            LocatorBundle buttonLocator)
        {
            var task = new ReplayTask
            {
                Id = "smoke-replay",
                CaseId = "smoke-replay",
                CaseName = "原生回放冒烟测试",
                Target = new ReplayTarget
                {
                    ProcessName = Process.GetCurrentProcess().ProcessName,
                    RestartBeforeReplay = false,
                    RecordedWindowSize = new ReplayWindowSize { Width = 600, Height = 420 }
                }
            };
            task.Actions.Add(new ReplayAction
            {
                Id = "input",
                Type = "input_text",
                Value = "回放输入成功",
                Locator = new LocatorBundle
                {
                    Name = "旧的输入框占位名称",
                    ClassName = editorLocator.ClassName,
                    ControlType = "Edit",
                    AncestorPath = editorLocator.AncestorPath,
                    FallbackWindowPoint = editorLocator.FallbackWindowPoint
                },
                DelayAfterMilliseconds = 50
            });
            var triggerClick = new ReplayAction
            {
                Id = "click",
                Type = "click",
                Locator = new LocatorBundle
                {
                    Name = buttonLocator.Name,
                    ClassName = "version_changed_button_class",
                    ControlType = "ListItem",
                    FallbackWindowPoint = buttonLocator.FallbackWindowPoint
                },
                DelayAfterMilliseconds = 50
            };
            triggerClick.Effects.Add(new ReplayEffect
            {
                Type = "target_appeared",
                Locator = new LocatorBundle
                {
                    Name = "\u521b\u5efa\u5143\u5b9d\u6d3e",
                    ControlType = "Button"
                },
                TimeoutMilliseconds = 5000
            });
            task.Actions.Add(triggerClick);
            task.Actions.Add(new ReplayAction
            {
                Id = "transient-menu-click",
                Type = "condition",
                ConditionType = "target_exists",
                Locator = new LocatorBundle
                {
                    Name = "创建元宝派",
                    ControlType = "MenuItem"
                },
                DelayAfterMilliseconds = 50
            });
            task.Actions.Add(new ReplayAction
            {
                Id = "uia-ancestor-anchor",
                Type = "condition",
                ConditionType = "target_exists",
                Locator = new LocatorBundle
                {
                    ControlType = "Image",
                    FallbackWindowPoint = new TracePoint(1, 1),
                    AncestorPath = new List<LocatorSegment>
                    {
                        new LocatorSegment
                        {
                            AutomationId = buttonLocator.AutomationId,
                            Name = buttonLocator.Name,
                            ControlType = "Button"
                        },
                        new LocatorSegment { ControlType = "Image" }
                    }
                },
                DelayAfterMilliseconds = 50
            });
            task.Actions.Add(new ReplayAction
            {
                Id = "safe-coordinate-fallback",
                Type = "click",
                Locator = new LocatorBundle
                {
                    ControlType = "Image",
                    FallbackWindowPoint = buttonLocator.FallbackWindowPoint
                },
                DelayAfterMilliseconds = 50
            });

            int scaledX;
            int scaledY;
            ReplayExecutor.ScaleWindowPoint(
                new NativeMethods.WindowRectangle { Left = 100, Top = 50, Right = 1300, Bottom = 890 },
                new ReplayWindowSize { Width = 600, Height = 420 },
                new TracePoint(300, 210),
                out scaledX,
                out scaledY);
            if (scaledX != 700 || scaledY != 470)
            {
                throw new InvalidOperationException("Scaled window-relative replay coordinate is incorrect");
            }

            Action schedulePopup = delegate
            {
                fixture.BeginInvoke((Action)delegate
                {
                    var popupTimer = new System.Windows.Forms.Timer { Interval = 500 };
                    popupTimer.Tick += delegate
                    {
                        popupTimer.Stop();
                        popupTimer.Dispose();
                        if (!replayPopup.Visible) replayPopup.Show(fixture);
                    };
                    popupTimer.Start();
                });
            };
            var result = new ReplayExecutor(false).Execute(task, CancellationToken.None, delegate(ReplayStepResult completedStep)
            {
                if (string.Equals(completedStep.ActionId, "input", StringComparison.Ordinal)) schedulePopup();
            });
            if (!result.Success || result.Steps.Count != 5 || result.Steps.Any(step => step.Status != "succeeded"))
            {
                var details = string.Join("; ", result.Steps.Select(step =>
                    step.ActionId + "=" + step.Status + ",locator=" + step.LocatorUsed + ",error=" + step.Error + ",attempts=" + string.Join("|", step.InteractionAttempts) + ",diagnostics=" + step.LocatorDiagnostics));
                throw new InvalidOperationException("原生回放链路失败：" + (result.Error ?? "unknown") + "; " + details);
            }
            if (result.Steps.Any(step => step.InteractionAttempts != null && step.InteractionAttempts.Count > 1))
            {
                throw new InvalidOperationException("A replay step executed more than one interaction attempt");
            }
            string replayedText = null;
            fixture.Invoke((Action)delegate { replayedText = editableTextBox.Text; });
            if (replayedText != "回放输入成功")
            {
                throw new InvalidOperationException("原生回放未写入文本框：" + replayedText);
            }
            if (result.Steps[0].LocatorUsed == null ||
                result.Steps[0].LocatorUsed.IndexOf("mutable_name_fallback", StringComparison.Ordinal) < 0)
            {
                throw new InvalidOperationException("Mutable edit locator fallback was not used");
            }
            if (result.Steps[1].Outcome == null ||
                result.Steps[1].Outcome.IndexOf("effect_satisfied:target_appeared", StringComparison.Ordinal) < 0)
            {
                throw new InvalidOperationException("Transient menu did not become the verified result of the trigger click: " + result.Steps[1].Outcome);
            }
            if (result.Steps[2].LocatorUsed == null ||
                result.Steps[2].LocatorUsed.IndexOf("创建元宝派", StringComparison.Ordinal) < 0)
            {
                throw new InvalidOperationException("Transient menu item was not located semantically");
            }
            if (result.Steps[3].LocatorUsed == null ||
                result.Steps[3].LocatorUsed.IndexOf("uia_ancestor_anchor", StringComparison.Ordinal) < 0)
            {
                throw new InvalidOperationException("UIA 定位失败后未使用安全窗口坐标兜底");
            }
            if (result.Steps[4].LocatorUsed == null ||
                result.Steps[4].LocatorUsed.IndexOf("scaled_window_relative_coordinate", StringComparison.Ordinal) < 0)
            {
                throw new InvalidOperationException("UIA failure did not use scaled window-relative fallback");
            }
            var strictTask = new ReplayTask
            {
                Id = "strict-semantic-replay",
                CaseId = "strict-semantic-replay",
                CaseName = "严格语义定位",
                Target = task.Target
            };
            strictTask.Actions.Add(new ReplayAction
            {
                Id = "missing-semantic-target",
                Type = "click",
                Locator = new LocatorBundle
                {
                    Name = "不存在的业务控件",
                    ControlType = "Button",
                    FallbackWindowPoint = buttonLocator.FallbackWindowPoint
                }
            });
            var strictResult = new ReplayExecutor(false).Execute(strictTask, CancellationToken.None, delegate { });
            if (strictResult.Success || strictResult.Steps.Count != 1 ||
                strictResult.Steps[0].Error == null ||
                strictResult.Steps[0].Error.IndexOf("拒绝坐标兜底", StringComparison.Ordinal) < 0)
            {
                throw new InvalidOperationException("语义定位失败后不应继续坐标回放");
            }
            Console.WriteLine("NATIVE_REPLAY_SMOKE_OK");
        }

        private static void VerifySemanticControlRecognition()
        {
            if (UiaCaptureService.IsEditableControl("Document", "RootWebArea", true, true, true, false))
            {
                throw new InvalidOperationException("RootWebArea Document must not be treated as editable input");
            }
            if (UiaCaptureService.IsEditableControl("Group", "status-value", true, false, true, false))
            {
                throw new InvalidOperationException("ValuePattern alone must not classify a control as editable input");
            }
            if (!UiaCaptureService.IsEditableControl("Edit", "group-name-editor", true, true, true, true))
            {
                throw new InvalidOperationException("Real Edit control was rejected as non-editable");
            }
            if (!UiaCaptureService.IsEditableControl("Group", "chat-editor", false, true, true, true))
            {
                throw new InvalidOperationException("Content-editable chat control was rejected as non-editable");
            }
            var semanticLeaf = new UiaNode
            {
                ControlType = "Image",
                Bounds = new NodeBounds { X = 100, Y = 100, Width = 16, Height = 16 },
                Enabled = true
            };
            var semanticParent = new UiaNode
            {
                AutomationId = "project-add-guide",
                Name = "New group",
                ControlType = "Group",
                Bounds = new NodeBounds { X = 96, Y = 96, Width = 24, Height = 24 },
                Enabled = true,
                Patterns = new List<string> { "Invoke" }
            };
            var selectedSemanticControl = ControlResolver.SelectBest(
                new[] { semanticLeaf, semanticParent },
                new NodeBounds { X = 0, Y = 0, Width = 1200, Height = 900 });
            if (!object.ReferenceEquals(selectedSemanticControl, semanticParent))
            {
                throw new InvalidOperationException("Generic image leaf was not promoted to its semantic parent");
            }

            var rootDocument = new UiaNode
            {
                AutomationId = "RootWebArea",
                Name = "Yuanbao Document",
                ControlType = "Document",
                Bounds = new NodeBounds { X = 0, Y = 0, Width = 1920, Height = 1040 },
                Enabled = true,
                Focusable = true,
                Patterns = new List<string> { "Value", "Text" }
            };
            var inputWrapper = new UiaNode
            {
                ClassName = "project-add-dialog_input",
                ControlType = "Group",
                Bounds = new NodeBounds { X = 700, Y = 420, Width = 420, Height = 40 },
                Enabled = true
            };
            selectedSemanticControl = ControlResolver.SelectBest(
                new[] { inputWrapper, rootDocument },
                rootDocument.Bounds);
            if (!object.ReferenceEquals(selectedSemanticControl, inputWrapper))
            {
                throw new InvalidOperationException("Root document incorrectly replaced the clicked business control");
            }

            var scrollContainer = new UiaNode
            {
                Name = "scrollable content",
                ClassName = "simplebar-content-wrapper",
                ControlType = "Group",
                Bounds = new NodeBounds { X = 0, Y = 169, Width = 216, Height = 686 },
                Enabled = true,
                Focusable = true
            };
            var plusTrigger = new UiaNode
            {
                ClassName = "group-plus_trigger__7yqy0",
                ControlType = "Group",
                Bounds = new NodeBounds { X = 172, Y = 718, Width = 24, Height = 25 },
                Enabled = true
            };
            selectedSemanticControl = ControlResolver.SelectBest(
                new[] { scrollContainer, plusTrigger },
                rootDocument.Bounds);
            if (!object.ReferenceEquals(selectedSemanticControl, plusTrigger))
            {
                throw new InvalidOperationException("小型交互触发器不应被滚动容器覆盖");
            }

            var chromeHost = new UiaNode
            {
                Name = "Chrome Legacy Window",
                AutomationId = "10249",
                ClassName = "Chrome_RenderWidgetHostHWND",
                ControlType = "Pane",
                Bounds = new NodeBounds { X = 400, Y = 200, Width = 700, Height = 700 },
                Enabled = true
            };
            var popupTabText = new UiaNode
            {
                Name = "聊天记录",
                ControlType = "Text",
                Bounds = new NodeBounds { X = 650, Y = 340, Width = 90, Height = 30 },
                Enabled = true
            };
            selectedSemanticControl = ControlResolver.SelectBest(
                new[] { popupTabText, chromeHost },
                rootDocument.Bounds);
            if (!object.ReferenceEquals(selectedSemanticControl, popupTabText))
            {
                throw new InvalidOperationException("Chromium 容器不应覆盖有名称的弹窗业务控件");
            }

            var imageTarget = new UiaNode { ControlType = "Image" };
            var sendLocator = new LocatorBundle();
            sendLocator.AncestorPath.Add(new LocatorSegment { AutomationId = "yuanbao-send-btn" });
            if (!RecorderSession.IsSendButton(imageTarget, sendLocator))
            {
                throw new InvalidOperationException("发送按钮子节点未通过 Locator 祖先识别");
            }
            sendLocator.AncestorPath.Insert(0, new LocatorSegment { AutomationId = "searchbar-editor" });
            if (RecorderSession.IsInputEditor(imageTarget, sendLocator))
            {
                throw new InvalidOperationException("发送按钮因输入框祖先被错误识别为输入框");
            }

            var stopTarget = new UiaNode
            {
                AutomationId = "yuanbao-send-btn",
                Name = "停止回答",
                ControlType = "Group"
            };
            if (RecorderSession.IsSendButton(stopTarget, sendLocator))
            {
                throw new InvalidOperationException("停止回答被错误识别为发送按钮");
            }

            var editorTarget = new UiaNode { ControlType = "Group", ClassName = "ql-editor" };
            var editorLocator = new LocatorBundle();
            editorLocator.AncestorPath.Add(new LocatorSegment { AutomationId = "searchbar-editor" });
            if (!RecorderSession.IsInputEditor(editorTarget, editorLocator))
            {
                throw new InvalidOperationException("输入框子节点未通过 Locator 祖先识别");
            }

            var unrelatedLocator = new LocatorBundle();
            unrelatedLocator.AncestorPath.Add(new LocatorSegment { AutomationId = "unrelated" });
            if (RecorderSession.IsInputEditor(imageTarget, unrelatedLocator))
            {
                throw new InvalidOperationException("非输入控件被错误识别为输入框");
            }
            Console.WriteLine("SEMANTIC_CONTROL_SMOKE_OK");
        }

        private static void VerifyAssertionScopeVisualization()
        {
            var window = new UiaNode
            {
                Index = 0,
                ParentIndex = -1,
                ControlType = "Window",
                Bounds = new NodeBounds { X = 10, Y = 20, Width = 900, Height = 700 }
            };
            var table = new UiaNode
            {
                Index = 1,
                ParentIndex = 0,
                ControlType = "Table",
                Bounds = new NodeBounds { X = 100, Y = 120, Width = 500, Height = 320 }
            };
            var target = new UiaNode
            {
                Index = 2,
                ParentIndex = 1,
                ControlType = "Text",
                Name = "scope target",
                Bounds = new NodeBounds { X = 140, Y = 150, Width = 120, Height = 30 }
            };
            var capture = new AssertionCaptureContext
            {
                Target = target,
                ScreenPoint = new TracePoint(160, 165),
                Snapshot = new UiaSnapshot { Nodes = new List<UiaNode> { window, table, target } }
            };

            if (!object.ReferenceEquals(ControlHighlightOverlay.ResolveAssertionScopeNode(capture, "target"), target))
            {
                throw new InvalidOperationException("Target assertion scope visualization resolved the wrong node");
            }
            if (!object.ReferenceEquals(ControlHighlightOverlay.ResolveAssertionScopeNode(capture, "table"), table))
            {
                throw new InvalidOperationException("Table assertion scope visualization resolved the wrong node");
            }
            if (!object.ReferenceEquals(ControlHighlightOverlay.ResolveAssertionScopeNode(capture, "window"), window))
            {
                throw new InvalidOperationException("Window assertion scope visualization resolved the wrong node");
            }
            Console.WriteLine("ASSERTION_SCOPE_VISUALIZATION_OK");
        }

        private static void VerifyReplayTaskDeserialization()
        {
            const string json = "{\"task\":{\"id\":\"task-1\",\"case_id\":\"case-1\",\"case_name\":\"case\",\"target\":{},\"actions\":[{\"id\":\"action-1\",\"type\":\"click\",\"target_relative_point\":{\"x\":0.8657407407407407,\"y\":0.8177842565597667}}],\"assertions\":[]}}";
            var serializer = new DataContractJsonSerializer(typeof(ReplayTaskEnvelope));
            ReplayTaskEnvelope envelope;
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                envelope = (ReplayTaskEnvelope)serializer.ReadObject(stream);
            }
            var point = envelope.Task.Actions[0].TargetRelativePoint;
            if (point == null || Math.Abs(point.X - 0.8657407407407407) > 0.000001 ||
                Math.Abs(point.Y - 0.8177842565597667) > 0.000001)
            {
                throw new InvalidOperationException("回放相对坐标反序列化失败");
            }
        }

        private static void VerifyTransientMenuTarget(UiaCaptureService captureService)
        {
            var snapshot = new UiaSnapshot();
            snapshot.Nodes.Add(new UiaNode
            {
                Index = 1,
                ParentIndex = -1,
                Depth = 1,
                AutomationId = "chatInputContextMenu",
                ClassName = "ContextMenu_menuWrap",
                ControlType = "Group",
                Bounds = new NodeBounds { X = 860, Y = 870, Width = 100, Height = 80 }
            });
            snapshot.Nodes.Add(new UiaNode
            {
                Index = 2,
                ParentIndex = 1,
                Depth = 2,
                Name = "复制",
                ClassName = "t-list-item",
                ControlType = "ListItem",
                Bounds = new NodeBounds { X = 868, Y = 880, Width = 86, Height = 28 }
            });
            snapshot.Nodes.Add(new UiaNode
            {
                Index = 3,
                ParentIndex = 1,
                Depth = 2,
                Name = "粘贴",
                ClassName = "t-list-item",
                ControlType = "ListItem",
                Bounds = new NodeBounds { X = 868, Y = 910, Width = 86, Height = 28 }
            });
            var result = captureService.CaptureTransientTargetFromSnapshot(
                snapshot,
                new ClickObservation
                {
                    X = 920,
                    Y = 920,
                    WindowRectangle = new NativeMethods.WindowRectangle { Left = 0, Top = 0, Right = 1200, Bottom = 900 }
                });
            if (result.Target == null || result.Target.Name != "粘贴" || result.Locator == null)
            {
                throw new InvalidOperationException("瞬态菜单项识别失败");
            }
            using (var observer = new TransientMenuObserver(captureService))
            {
                observer.SeedSnapshotForTest(snapshot);
                var eventTimeResult = observer.ConsumeAtPoint(new ClickObservation
                {
                    X = 920,
                    Y = 920,
                    WindowRectangle = new NativeMethods.WindowRectangle { Left = 0, Top = 0, Right = 1200, Bottom = 900 }
                });
                if (eventTimeResult == null || eventTimeResult.Target == null || eventTimeResult.Target.Name != "粘贴")
                {
                    throw new InvalidOperationException("事件时菜单缓存识别失败");
                }
            }

            var responseMenuSnapshot = new UiaSnapshot();
            responseMenuSnapshot.Nodes.Add(new UiaNode
            {
                Index = 5,
                ParentIndex = -1,
                Depth = 1,
                Name = "3.待办事项三",
                ClassName = "ybc-li-component ybc-li-component_ol",
                ControlType = "ListItem",
                Bounds = new NodeBounds { X = 557, Y = 590, Width = 938, Height = 25 }
            });
            responseMenuSnapshot.Nodes.Add(new UiaNode
            {
                Index = 10,
                ParentIndex = -1,
                Depth = 1,
                AutomationId = "ybContextMenu",
                ClassName = "ContextMenu_ybContextMenu",
                ControlType = "Group",
                Bounds = new NodeBounds { X = 1086, Y = 583, Width = 119, Height = 239 }
            });
            responseMenuSnapshot.Nodes.Add(new UiaNode
            {
                Index = 11,
                ParentIndex = 10,
                Depth = 2,
                ClassName = "ContextMenu_items ContextMenu_ybRow",
                ControlType = "Group",
                Bounds = new NodeBounds { X = 1093, Y = 590, Width = 104, Height = 36 }
            });
            responseMenuSnapshot.Nodes.Add(new UiaNode
            {
                Index = 12,
                ParentIndex = 11,
                Depth = 3,
                Name = "复制",
                ControlType = "Text",
                Bounds = new NodeBounds { X = 1129, Y = 598, Width = 28, Height = 19 }
            });
            var copyResult = captureService.CaptureTransientTargetFromSnapshot(
                responseMenuSnapshot,
                new ClickObservation
                {
                    X = 1164,
                    Y = 599,
                    WindowRectangle = new NativeMethods.WindowRectangle { Left = 0, Top = 0, Right = 1400, Bottom = 900 }
                });
            if (copyResult.Target == null || copyResult.Target.Name != "复制")
            {
                throw new InvalidOperationException("菜单行子文本语义识别失败");
            }
            Console.WriteLine("TRANSIENT_MENU_SMOKE_OK");
        }

        private static void VerifyInputEventJournal()
        {
            var filePath = Path.Combine(Path.GetTempPath(), "cache-agent-input-events-" + Guid.NewGuid().ToString("N") + ".ndjson");
            try
            {
                using (var journal = new InputEventJournal(filePath))
                {
                    var eventId = journal.Record("right_click", DateTime.UtcNow, 100, 200, false, "recorded_action");
                    if (eventId != "event_000001") throw new InvalidOperationException("输入事件编号错误");
                    var dragEventId = journal.Record("drag", DateTime.UtcNow, 120, 220, false, "recorded_action");
                    if (dragEventId != "event_000002") throw new InvalidOperationException("拖动事件编号错误");
                }
                var content = File.ReadAllText(filePath);
                if (!content.Contains("\"type\":\"right_click\"") ||
                    !content.Contains("\"type\":\"drag\"") ||
                    !content.Contains("\"disposition\":\"recorded_action\""))
                {
                    throw new InvalidOperationException("输入事件日志内容错误");
                }
                Console.WriteLine("INPUT_EVENT_JOURNAL_SMOKE_OK");
            }
            finally
            {
                if (File.Exists(filePath)) File.Delete(filePath);
            }
        }

        private static void VerifyMessageDiff()
        {
            var service = new YuanbaoMessageDiffService();
            var result = service.FindCommittedUserMessage(
                CreateSnapshot("之前的问题", "和元宝说点什么"),
                CreateSnapshot("之前的问题", "和元宝说点什么", "什么是VPN"));
            if (result.NeedsReview || result.Text != "什么是VPN")
            {
                throw new InvalidOperationException("中文用户消息差分失败");
            }

            var repeated = service.FindCommittedUserMessage(
                CreateSnapshot("重复问题"),
                CreateSnapshot("重复问题", "重复问题"));
            if (repeated.NeedsReview || repeated.Text != "重复问题")
            {
                throw new InvalidOperationException("重复用户消息差分失败");
            }

            var ambiguous = service.FindCommittedUserMessage(
                CreateSnapshot(),
                CreateSnapshot("问题一", "问题二"));
            if (!ambiguous.NeedsReview)
            {
                throw new InvalidOperationException("多条新增消息必须进入人工复核");
            }
            Console.WriteLine("MESSAGE_DIFF_SMOKE_OK");
        }

        private static void VerifyGenericInputCommitIsolation()
        {
            var searchBounds = new NodeBounds { X = 700, Y = 300, Width = 550, Height = 40 };
            var chatBounds = new NodeBounds { X = 600, Y = 900, Width = 900, Height = 80 };
            var searchWithText = new FocusedEditableCapture { Text = "阿凡达", Bounds = searchBounds };
            var sameSearch = new FocusedEditableCapture { Text = "阿凡达", Bounds = searchBounds };
            var chatPlaceholder = new FocusedEditableCapture { Text = "和元宝说点什么", Bounds = chatBounds };

            if (!RecorderSession.ShouldCommitGenericInput(searchBounds, string.Empty, searchWithText, sameSearch, false))
            {
                throw new InvalidOperationException("同一搜索框内容变化后未提交");
            }
            if (RecorderSession.ShouldCommitGenericInput(searchBounds, string.Empty, chatPlaceholder, null, false))
            {
                throw new InvalidOperationException("搜索框错误读取了聊天输入框文本");
            }
            var beforeEnter = new FocusedEditableCapture { Text = "new-group-name", Bounds = searchBounds };
            var afterEnter = new FocusedEditableCapture { Text = string.Empty, Bounds = chatBounds };
            if (!object.ReferenceEquals(
                RecorderSession.PreferPreInteractionInput(beforeEnter, afterEnter),
                beforeEnter))
            {
                throw new InvalidOperationException("Pre-interaction input was not preserved");
            }
            if (!object.ReferenceEquals(
                RecorderSession.PreferPreInteractionInput(null, afterEnter),
                afterEnter))
            {
                throw new InvalidOperationException("Post-interaction input fallback failed");
            }
            var imeBeforeEnter = new FocusedEditableCapture { Text = string.Empty, Bounds = searchBounds };
            var resizedSearchBounds = new NodeBounds { X = 700, Y = 300, Width = 529, Height = 40 };
            var imeAfterEnter = new FocusedEditableCapture { Text = "元宝", Bounds = resizedSearchBounds };
            if (!object.ReferenceEquals(
                RecorderSession.PreferPreInteractionInput(imeBeforeEnter, imeAfterEnter),
                imeAfterEnter))
            {
                throw new InvalidOperationException("IME 提交后的最终输入值未被优先采用");
            }
            if (!RecorderSession.ShouldCommitGenericInput(
                searchBounds,
                string.Empty,
                imeAfterEnter,
                null,
                true))
            {
                throw new InvalidOperationException("输入框出现清除按钮导致宽度变化后未提交");
            }
            Console.WriteLine("GENERIC_INPUT_COMMIT_SMOKE_OK");
        }

        private static void VerifyEditableTextSelection()
        {
            var emptyValue = UiaCaptureService.SelectEditableText(true, string.Empty, true, "和元宝说点什么");
            if (emptyValue != string.Empty)
            {
                throw new InvalidOperationException("ValuePattern 空值被 TextPattern 占位文字覆盖");
            }
            var contentEditable = UiaCaptureService.SelectEditableText(false, null, true, "中文输入\r\n");
            if (contentEditable != "中文输入")
            {
                throw new InvalidOperationException("contenteditable TextPattern 读取失败");
            }
            Console.WriteLine("EDITABLE_TEXT_SELECTION_SMOKE_OK");
        }

        private static void VerifyAssertions(IntPtr windowHandle, Form fixture, Form replayPopup)
        {
            var root = System.Windows.Automation.AutomationElement.FromHandle(windowHandle);
            var locator = new LocatorBundle { AutomationId = "yuanbao-send-btn" };
            var executorTarget = new ReplayAssertionTarget { Locator = locator, SemanticRole = "发送按钮" };
            var exists = ReplayExecutor.ExecuteAssertion(
                root,
                new ReplayAssertion
                {
                    Id = "assert_exists",
                    Label = "发送按钮存在",
                    Type = "target_exists",
                    Target = executorTarget,
                    TimeoutMilliseconds = 500
                },
                CancellationToken.None);
            if (exists.Status != "passed") throw new InvalidOperationException("控件存在断言失败");

            var property = ReplayExecutor.ExecuteAssertion(
                root,
                new ReplayAssertion
                {
                    Id = "assert_property",
                    Label = "AutomationId 正确",
                    Type = "property_equals",
                    Property = "automation_id",
                    Expected = "yuanbao-send-btn",
                    Target = executorTarget,
                    TimeoutMilliseconds = 500
                },
                CancellationToken.None);
            if (property.Status != "passed") throw new InvalidOperationException("属性断言失败");

            var text = ReplayExecutor.ExecuteAssertion(
                root,
                new ReplayAssertion
                {
                    Id = "assert_text",
                    Label = "窗口包含发送文本",
                    Type = "text_contains",
                    Expected = "发送",
                    TimeoutMilliseconds = 500
                },
                CancellationToken.None);
            if (text.Status != "passed") throw new InvalidOperationException("文本断言失败");

            var windowKeywords = ReplayExecutor.ExecuteAssertion(
                root,
                new ReplayAssertion
                {
                    Id = "assert_window_keywords",
                    Label = "window keyword assertion",
                    Type = "keywords_match_count",
                    Scope = "window",
                    Keywords = new List<string> { root.Current.Name, "__missing_keyword__" },
                    MinimumMatches = 1,
                    TimeoutMilliseconds = 500
                },
                CancellationToken.None);
            if (windowKeywords.Status != "passed")
            {
                throw new InvalidOperationException("Window-scoped keyword assertion failed: " + windowKeywords.Actual);
            }

            var missing = ReplayExecutor.ExecuteAssertion(
                root,
                new ReplayAssertion
                {
                    Id = "assert_missing",
                    Label = "不存在的文本",
                    Type = "text_contains",
                    Expected = "__missing_assertion_text__",
                    TimeoutMilliseconds = 100
                },
                CancellationToken.None);
            if (missing.Status != "failed") throw new InvalidOperationException("失败断言被误判为通过");
            var strictMissingTarget = ReplayExecutor.ExecuteAssertion(
                root,
                new ReplayAssertion
                {
                    Id = "assert_missing_target_must_not_search_window",
                    Label = "missing target must not fall back to window text",
                    Type = "text_contains",
                    Expected = root.Current.Name,
                    Target = new ReplayAssertionTarget
                    {
                        Locator = new LocatorBundle { AutomationId = "__missing_assertion_target__" }
                    },
                    TimeoutMilliseconds = 100
                },
                CancellationToken.None);
            if (strictMissingTarget.Status != "failed" || strictMissingTarget.Actual != "target_not_found")
            {
                throw new InvalidOperationException("Target-scoped text assertion incorrectly fell back to window text");
            }

            var confirmedMissingTarget = ReplayExecutor.ExecuteAssertion(
                root,
                new ReplayAssertion
                {
                    Id = "assert_missing_target_confirmed_after_timeout",
                    Label = "missing target receives a confirmation probe",
                    Type = "target_not_exists",
                    Target = new ReplayAssertionTarget
                    {
                        Locator = new LocatorBundle { AutomationId = "__confirmed_missing_target__" }
                    },
                    TimeoutMilliseconds = 1
                },
                CancellationToken.None);
            if (confirmedMissingTarget.Status != "passed" || confirmedMissingTarget.Actual != "not_exists")
            {
                throw new InvalidOperationException(
                    "Negative assertion timed out after its first absence sample: " +
                    confirmedMissingTarget.Status + "/" + confirmedMissingTarget.Actual);
            }

            string popupTargetName = null;
            fixture.Invoke((Action)delegate
            {
                replayPopup.Show(fixture);
                popupTargetName = replayPopup.Controls[0].AccessibleName;
            });
            try
            {
                var crossWindow = ReplayExecutor.ExecuteAssertion(
                    root,
                    Process.GetCurrentProcess().ProcessName,
                    new ReplayLocatorEngine(),
                    new ReplayAssertion
                    {
                        Id = "assert_cross_window_target",
                        Label = "cross-window target exists",
                        Type = "target_exists",
                        Target = new ReplayAssertionTarget
                        {
                            Locator = new LocatorBundle
                            {
                                Name = popupTargetName,
                                ControlType = "Button"
                            }
                        },
                        TimeoutMilliseconds = 1000
                    },
                    CancellationToken.None);
                if (crossWindow.Status != "passed" ||
                    string.IsNullOrWhiteSpace(crossWindow.LocatorUsed) ||
                    crossWindow.LocatorUsed.IndexOf("window=", StringComparison.Ordinal) < 0)
                {
                    throw new InvalidOperationException("Cross-window assertion did not bind to the popup window");
                }
            }
            finally
            {
                fixture.Invoke((Action)delegate { replayPopup.Hide(); });
            }
            Console.WriteLine("ASSERTION_SMOKE_OK");
        }

        private static UiaSnapshot CreateSnapshot(params string[] values)
        {
            var snapshot = new UiaSnapshot();
            snapshot.Nodes.Add(new UiaNode { Index = 0, ParentIndex = -1, ControlType = "Document" });
            var index = 1;
            foreach (var value in values)
            {
                if (value == "和元宝说点什么")
                {
                    snapshot.Nodes.Add(new UiaNode
                    {
                        Index = index++, ParentIndex = 0, ControlType = "Text", Name = value
                    });
                    continue;
                }

                var containerIndex = index++;
                snapshot.Nodes.Add(new UiaNode
                {
                    Index = containerIndex,
                    ParentIndex = 0,
                    ControlType = "Group",
                    ClassName = "chat_chatInfoItem agent-chat__list__item--human"
                });
                snapshot.Nodes.Add(new UiaNode
                {
                    Index = index++, ParentIndex = containerIndex, ControlType = "Text", Name = value
                });
            }
            snapshot.NodeCount = snapshot.Nodes.Count;
            return snapshot;
        }

        private static int VerifyRecordedSnapshots(string beforePath, string afterPath)
        {
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(UiaSnapshot));
                UiaSnapshot before;
                UiaSnapshot after;
                using (var stream = File.OpenRead(beforePath))
                {
                    before = (UiaSnapshot)serializer.ReadObject(stream);
                }
                using (var stream = File.OpenRead(afterPath))
                {
                    after = (UiaSnapshot)serializer.ReadObject(stream);
                }

                var result = new YuanbaoMessageDiffService().FindCommittedUserMessage(before, after);
                Console.WriteLine("needs_review=" + result.NeedsReview);
                Console.WriteLine("text=" + result.Text);
                Console.WriteLine("reason=" + result.Reason);
                Console.WriteLine("container_index=" + result.ContainerIndex);
                Console.WriteLine("text_node_index=" + result.TextNodeIndex);
                return result.NeedsReview ? 2 : 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }
    }
}
#endif
