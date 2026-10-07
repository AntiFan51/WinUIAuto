using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace YuanbaoRecorder.Agent
{
    internal sealed class RecorderForm : Form
    {
        private const int AssertionHotKeyId = 0x4341;
        private const int FlowControlHotKeyId = 0x4342;
        private const int BranchControlHotKeyId = 0x4343;
        private readonly TextBox caseNameInput;
        private readonly Button startButton;
        private readonly Button stopButton;
        private readonly Button openOutputButton;
        private readonly Label statusLabel;
        private readonly Label replayStatusLabel;
        private readonly RecorderSession recorder;
        private readonly ReplayAgentService replayAgent;
        private readonly ControlHighlightOverlay controlHighlightOverlay;
        private readonly System.Windows.Forms.Timer controlPreviewTimer;
        private bool capturingAssertion;
        private int assertionRequestPending;
        private int previewRequestPending;
        private int previewGeneration;
        private int lastPreviewX = int.MinValue;
        private int lastPreviewY = int.MinValue;
        private DateTime lastPreviewAtUtc = DateTime.MinValue;

        internal RecorderForm()
        {
            Text = "元宝 Windows 人工测试录制器 · " + AgentBuildInfo.Version + " · " + AgentBuildInfo.Configuration;
            AutoScaleMode = AutoScaleMode.Dpi;
            // Keep enough horizontal and vertical room for the shortcut/status text
            // when Windows applies a larger font or display scale.  The previous
            // 680x292 client area allowed the labels to wrap into their neighbors
            // (and made the rightmost words appear clipped).
            ClientSize = new Size(760, 340);
            MinimumSize = new Size(760, 370);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;

            var titleLabel = new Label
            {
                Text = "人工操作、UIA 证据与录制期验证点采集",
                AutoSize = true,
                Font = new Font(Font.FontFamily, 12, FontStyle.Bold),
                Location = new Point(22, 20),
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            Controls.Add(titleLabel);

            var caseLabel = new Label
            {
                Text = "用例名称",
                AutoSize = true,
                Location = new Point(22, 64)
            };
            Controls.Add(caseLabel);

            caseNameInput = new TextBox
            {
                Text = "元宝人工测试",
                // Leave a stable gap after the label; at higher DPI the label's
                // glyphs extend slightly beyond their nominal measured width.
                Location = new Point(110, 60),
                Size = new Size(426, 25),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(caseNameInput);

            startButton = new Button
            {
                Text = "开始录制",
                Location = new Point(548, 58),
                Size = new Size(88, 30),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            startButton.Click += StartButtonClick;
            Controls.Add(startButton);

            stopButton = new Button
            {
                Text = "结束录制",
                Enabled = false,
                Location = new Point(648, 58),
                Size = new Size(88, 30),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            stopButton.Click += StopButtonClick;
            Controls.Add(stopButton);

            openOutputButton = new Button
            {
                Text = "打开结果目录",
                Enabled = false,
                Location = new Point(22, 105),
                Size = new Size(118, 32),
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            openOutputButton.Click += OpenOutputButtonClick;
            Controls.Add(openOutputButton);

            statusLabel = new Label
            {
                Text = "请先打开元宝并将其置于前台",
                AutoEllipsis = false,
                Location = new Point(154, 106),
                Size = new Size(582, 44),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(statusLabel);

            var assertionHelpLabel = new Label
            {
                Text = "快捷键：验证点 Ctrl+右键   ·   等待 Ctrl+Shift+W   ·   IF/ELSE Ctrl+Shift+B",
                ForeColor = Color.FromArgb(35, 115, 90),
                Font = new Font(Font.FontFamily, 9, FontStyle.Bold),
                Location = new Point(22, 160),
                Size = new Size(714, 58),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(assertionHelpLabel);

            replayStatusLabel = new Label
            {
                Text = "管理平台连接中…",
                AutoEllipsis = true,
                ForeColor = Color.FromArgb(55, 100, 150),
                Location = new Point(22, 222),
                Size = new Size(714, 82),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(replayStatusLabel);

            var caseRoot = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "cases-native"));
            Directory.CreateDirectory(caseRoot);
            recorder = new RecorderSession("yuanbao", "腾讯元宝", caseRoot);
            recorder.StatusChanged += RecorderStatusChanged;
            recorder.AssertionRequested += RecorderAssertionRequested;
            controlHighlightOverlay = new ControlHighlightOverlay();
            // Poll often enough that pressing Ctrl feels immediate. The actual UIA work is
            // serialized below, so this does not create overlapping provider calls.
            controlPreviewTimer = new System.Windows.Forms.Timer { Interval = 50 };
            controlPreviewTimer.Tick += PreviewAssertionTarget;
            controlPreviewTimer.Start();
            var managementServerUrl = Environment.GetEnvironmentVariable("CACHE_AGENT_SERVER_URL");
            if (string.IsNullOrWhiteSpace(managementServerUrl))
                throw new InvalidOperationException("Management server URL is not configured. Start the agent using start-agent.ps1.");
            replayAgent = new ReplayAgentService(managementServerUrl, () => !recorder.IsRecording);
            replayAgent.StatusChanged += ReplayAgentStatusChanged;
            replayAgent.ReplayCompleted += ReplayAgentCompleted;
            Shown += delegate { replayAgent.Start(); };
            FormClosing += RecorderFormClosing;

            if (!string.Equals(AgentBuildInfo.Configuration, "Current", StringComparison.Ordinal))
            {
                startButton.Enabled = false;
                statusLabel.Text = "当前不是正式 Current 构建，已禁止录制；请运行 start-agent.ps1";
            }
        }

        protected override void OnHandleCreated(EventArgs eventArgs)
        {
            base.OnHandleCreated(eventArgs);
            if (!NativeMethods.RegisterHotKey(
                Handle,
                AssertionHotKeyId,
                NativeMethods.ModControl | NativeMethods.ModShift,
                NativeMethods.VirtualKeyA))
            {
                statusLabel.Text = "验证点快捷键注册失败，请关闭占用 Ctrl+Shift+A 的程序后重启 Agent";
            }
            if (!NativeMethods.RegisterHotKey(
                Handle,
                FlowControlHotKeyId,
                NativeMethods.ModControl | NativeMethods.ModShift,
                NativeMethods.VirtualKeyW))
            {
                statusLabel.Text = "等待快捷键注册失败，请关闭占用 Ctrl+Shift+W 的程序后重启 Agent";
            }
            if (!NativeMethods.RegisterHotKey(
                Handle,
                BranchControlHotKeyId,
                NativeMethods.ModControl | NativeMethods.ModShift,
                NativeMethods.VirtualKeyB))
            {
                statusLabel.Text = "分支快捷键注册失败，请关闭占用 Ctrl+Shift+B 的程序后重启 Agent";
            }
        }

        protected override void OnHandleDestroyed(EventArgs eventArgs)
        {
            NativeMethods.UnregisterHotKey(Handle, AssertionHotKeyId);
            NativeMethods.UnregisterHotKey(Handle, FlowControlHotKeyId);
            NativeMethods.UnregisterHotKey(Handle, BranchControlHotKeyId);
            base.OnHandleDestroyed(eventArgs);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == NativeMethods.WmHotKey && message.WParam.ToInt32() == AssertionHotKeyId)
            {
                CaptureAssertionAtCursor();
                return;
            }
            if (message.Msg == NativeMethods.WmHotKey && message.WParam.ToInt32() == FlowControlHotKeyId)
            {
                CaptureFlowControlAtCursor();
                return;
            }
            if (message.Msg == NativeMethods.WmHotKey && message.WParam.ToInt32() == BranchControlHotKeyId)
            {
                CaptureOrAdvanceBranchAtCursor();
                return;
            }
            base.WndProc(ref message);
        }

        private void StartButtonClick(object sender, EventArgs eventArgs)
        {
            try
            {
                recorder.Start(caseNameInput.Text);
                startButton.Enabled = false;
                stopButton.Enabled = true;
                caseNameInput.Enabled = false;
                WindowState = FormWindowState.Minimized;
                statusLabel.Text = "录制中：按住 Ctrl 并右键目标可添加验证点";
            }
            catch (Exception error)
            {
                statusLabel.Text = error.Message;
                MessageBox.Show(error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void CaptureAssertionAtCursor()
        {
            if (!recorder.IsRecording || capturingAssertion) return;
            capturingAssertion = true;
            controlHighlightOverlay.Hide();
            AssertionCaptureContext capture = null;
            recorder.SuspendActionCapture();
            try
            {
                capture = recorder.CaptureAssertionTarget();
                using (var dialog = new AssertionDialog(capture))
                {
                    dialog.ScopeChanged += delegate(string scope)
                    {
                        controlHighlightOverlay.ShowAssertion(capture, scope);
                    };
                    controlHighlightOverlay.ShowAssertion(capture, dialog.SelectedScope);
                    ShowRecorderForModal();
                    dialog.TopMost = true;
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        recorder.AddAssertion(
                            capture,
                            dialog.Draft);
                        capture = null;
                    }
                }
                if (capture != null) recorder.DiscardAssertionCapture(capture);
            }
            catch (Exception error)
            {
                if (capture != null) recorder.DiscardAssertionCapture(capture);
                statusLabel.Text = error.Message;
                MessageBox.Show(error.Message, "添加验证点", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                controlHighlightOverlay.Hide();
                recorder.ResumeActionCapture();
                capturingAssertion = false;
                ReturnToTargetAfterModal();
            }
        }

        private void CaptureFlowControlAtCursor()
        {
            if (!recorder.IsRecording || capturingAssertion) return;
            capturingAssertion = true;
            controlHighlightOverlay.Hide();
            recorder.SuspendActionCapture();
            try
            {
                using (var dialog = new FlowControlDialog(null, "wait_time"))
                {
                    ShowRecorderForModal();
                    dialog.TopMost = true;
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        recorder.AddDelayAction(dialog.Draft);
                    }
                }
            }
            catch (Exception error)
            {
                statusLabel.Text = error.Message;
                MessageBox.Show(error.Message, "添加固定等待", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                recorder.ResumeActionCapture();
                capturingAssertion = false;
                ReturnToTargetAfterModal();
            }
        }

        private void CaptureOrAdvanceBranchAtCursor()
        {
            if (!recorder.IsRecording || capturingAssertion) return;
            if (recorder.HasActiveBranch)
            {
                ShowRecorderForModal();
                var choice = MessageBox.Show(
                    this,
                    "选择“是”切换到 ELSE；选择“否”结束当前分支；选择“取消”继续录制当前路径。",
                    "IF / ELSE 录制",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question);
                try
                {
                    if (choice == DialogResult.Yes)
                    {
                        recorder.SuspendActionCapture();
                        try
                        {
                            MessageBox.Show(
                                this,
                                "现在进入场景恢复阶段，操作不会被录制。请把元宝恢复到 ELSE 路径的起点，完成后点击确定；确定之后的操作才会归入 ELSE。",
                                "准备 ELSE 场景",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information);
                            recorder.SwitchToElseBranch();
                        }
                        finally
                        {
                            recorder.ResumeActionCapture();
                        }
                    }
                    else if (choice == DialogResult.No) recorder.EndActiveBranch();
                }
                catch (Exception error)
                {
                    statusLabel.Text = error.Message;
                    MessageBox.Show(error.Message, "IF / ELSE 录制", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                ReturnToTargetAfterModal();
                return;
            }

            capturingAssertion = true;
            controlHighlightOverlay.Hide();
            AssertionCaptureContext capture = null;
            recorder.SuspendActionCapture();
            try
            {
                capture = recorder.CaptureAssertionTarget();
                using (var dialog = new FlowControlDialog(capture, "condition"))
                {
                    ShowRecorderForModal();
                    dialog.TopMost = true;
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        recorder.AddFlowControlAction(capture, dialog.Draft);
                        capture = null;
                    }
                }
                if (capture != null) recorder.DiscardAssertionCapture(capture);
            }
            catch (Exception error)
            {
                if (capture != null) recorder.DiscardAssertionCapture(capture);
                statusLabel.Text = error.Message;
                MessageBox.Show(error.Message, "开始 IF / ELSE", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                recorder.ResumeActionCapture();
                capturingAssertion = false;
                ReturnToTargetAfterModal();
            }
        }

        private void ShowRecorderForModal()
        {
            Show();
            WindowState = FormWindowState.Normal;
            NativeMethods.SetForegroundWindow(Handle);
            Activate();
            BringToFront();
        }

        private void ReturnToTargetAfterModal()
        {
            WindowState = FormWindowState.Minimized;
            var targetWindow = NativeMethods.FindLargestVisibleWindowByProcessName("yuanbao");
            if (targetWindow != IntPtr.Zero) NativeMethods.SetForegroundWindow(targetWindow);
        }

        private void RecorderAssertionRequested()
        {
            if (IsDisposed || Interlocked.Exchange(ref assertionRequestPending, 1) != 0) return;
            BeginInvoke(new Action(delegate
            {
                try { CaptureAssertionAtCursor(); }
                finally { Interlocked.Exchange(ref assertionRequestPending, 0); }
            }));
        }

        private void PreviewAssertionTarget(object sender, EventArgs eventArgs)
        {
            if (!recorder.IsRecording || capturingAssertion ||
                (NativeMethods.GetAsyncKeyState(NativeMethods.VirtualKeyControl) & 0x8000) == 0)
            {
                Interlocked.Increment(ref previewGeneration);
                controlHighlightOverlay.Hide();
                lastPreviewX = int.MinValue;
                lastPreviewY = int.MinValue;
                lastPreviewAtUtc = DateTime.MinValue;
                return;
            }
            try
            {
                NativeMethods.Point point;
                if (!NativeMethods.GetCursorPos(out point)) return;
                var stationary = Math.Abs(point.X - lastPreviewX) < 3 && Math.Abs(point.Y - lastPreviewY) < 3;
                if (stationary && DateTime.UtcNow - lastPreviewAtUtc < TimeSpan.FromMilliseconds(600)) return;
                // Invalidate an in-flight result as soon as the pointer has moved.  Do not
                // mark this position as captured until we own the worker slot: the old code
                // did so before the pending check, which could suppress the newest request
                // for up to 600 ms and then paint a stale rectangle.
                var generation = Interlocked.Increment(ref previewGeneration);
                if (Interlocked.Exchange(ref previewRequestPending, 1) != 0)
                {
                    // Ensure the newest position is captured immediately after the worker
                    // becomes free, even if the pointer later returns to the old rectangle.
                    lastPreviewAtUtc = DateTime.MinValue;
                    return;
                }
                lastPreviewX = point.X;
                lastPreviewY = point.Y;
                lastPreviewAtUtc = DateTime.UtcNow;
                Task.Factory.StartNew(
                    () => recorder.PreviewTargetAtCursor(),
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default)
                    .ContinueWith(task =>
                    {
                        if (IsDisposed || Disposing || generation != Volatile.Read(ref previewGeneration)) return;
                        if (task.IsFaulted || task.IsCanceled || task.Result == null || task.Result.Target == null)
                        {
                            controlHighlightOverlay.Hide();
                        }
                        else
                        {
                            controlHighlightOverlay.ShowPreview(task.Result);
                        }
                    }, TaskScheduler.FromCurrentSynchronizationContext())
                    .ContinueWith(task => Interlocked.Exchange(ref previewRequestPending, 0), TaskScheduler.Default);
            }
            catch
            {
                Interlocked.Exchange(ref previewRequestPending, 0);
                controlHighlightOverlay.Hide();
            }
        }

        private async void StopButtonClick(object sender, EventArgs eventArgs)
        {
            stopButton.Enabled = false;
            statusLabel.Text = "正在停止录制…";
            await recorder.StopAsync(TimeSpan.FromSeconds(5));
            controlHighlightOverlay.Hide();
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            startButton.Enabled = true;
            caseNameInput.Enabled = true;
            openOutputButton.Enabled = Directory.Exists(recorder.SessionDirectory);
        }

        private void OpenOutputButtonClick(object sender, EventArgs eventArgs)
        {
            if (Directory.Exists(recorder.SessionDirectory))
            {
                Process.Start("explorer.exe", recorder.SessionDirectory);
            }
        }

        private void RecorderStatusChanged(string message)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(RecorderStatusChanged), message);
                return;
            }
            statusLabel.Text = message;
        }

        private void ReplayAgentStatusChanged(string message)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(ReplayAgentStatusChanged), message);
                return;
            }
            replayStatusLabel.Text = "回放 Agent：" + message;
        }

        private void ReplayAgentCompleted(bool success, string caseName, string error)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<bool, string, string>(ReplayAgentCompleted), success, caseName, error);
                return;
            }
            ShowRecorderForModal();
            MessageBox.Show(
                this,
                success ? "用例“" + caseName + "”回放完成。" : "用例“" + caseName + "”回放失败：\r\n" + error,
                success ? "回放完成" : "回放失败",
                MessageBoxButtons.OK,
                success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            ReturnToTargetAfterModal();
        }

        private async void RecorderFormClosing(object sender, FormClosingEventArgs eventArgs)
        {
            if (!recorder.IsRecording)
            {
                controlPreviewTimer.Stop();
                controlHighlightOverlay.Dispose();
                replayAgent.Dispose();
                recorder.Dispose();
                return;
            }
            eventArgs.Cancel = true;
            stopButton.Enabled = false;
            await recorder.StopAsync(TimeSpan.FromSeconds(5));
            controlPreviewTimer.Stop();
            controlHighlightOverlay.Dispose();
            replayAgent.Dispose();
            recorder.Dispose();
            FormClosing -= RecorderFormClosing;
            Close();
        }
    }
}
