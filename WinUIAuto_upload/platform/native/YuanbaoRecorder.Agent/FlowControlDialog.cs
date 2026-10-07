using System;
using System.Drawing;
using System.Windows.Forms;

namespace YuanbaoRecorder.Agent
{
    internal sealed class FlowControlDialog : Form
    {
        private readonly ComboBox conditionInput;
        private readonly NumericUpDown timeoutInput;
        private readonly Label timeoutLabel;
        private readonly string flowType;

        internal FlowControlDialog(AssertionCaptureContext capture, string type)
        {
            flowType = type == "condition" ? "condition" : "wait_time";
            Text = flowType == "condition" ? "开始 IF / ELSE 分支" : "添加固定等待时间";
            Size = new Size(480, 270);
            MinimumSize = Size;
            MaximumSize = Size;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            Controls.Add(new Label
            {
                Text = flowType == "condition"
                    ? "目标：" + DescribeTarget(capture == null ? null : capture.Target)
                    : "该步骤不绑定控件，回放时会真实暂停指定时长。",
                AutoEllipsis = true,
                Location = new Point(20, 18),
                Size = new Size(420, 38)
            });

            var conditionLabel = new Label { Text = "IF 条件", Location = new Point(20, 75), AutoSize = true };
            Controls.Add(conditionLabel);
            conditionInput = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(130, 71),
                Size = new Size(300, 25)
            };
            conditionInput.Items.Add(new Option("target_exists", "目标出现 / 存在"));
            conditionInput.Items.Add(new Option("target_not_exists", "目标消失 / 不存在"));
            if (flowType == "condition")
            {
                conditionInput.Items.Add(new Option("same_kind_exists", "存在任意同类控件（忽略名称）"));
                conditionInput.Items.Add(new Option("same_kind_not_exists", "不存在同类控件（忽略名称）"));
            }
            conditionInput.SelectedIndex = 0;
            Controls.Add(conditionInput);
            conditionLabel.Visible = flowType == "condition";
            conditionInput.Visible = flowType == "condition";

            timeoutLabel = new Label { Text = "最长等待（秒）", Location = new Point(20, 113), AutoSize = true };
            Controls.Add(timeoutLabel);
            timeoutInput = CreateNumberInput(130, 109, 1, 120, 10);
            Controls.Add(timeoutInput);
            timeoutLabel.Text = "固定等待（秒）";
            timeoutLabel.Visible = flowType == "wait_time";
            timeoutInput.Visible = flowType == "wait_time";

            Controls.Add(new Label
            {
                Text = flowType == "condition"
                    ? "确认后开始录制 IF 路径；随后使用分支快捷键切换 ELSE、结束分支。"
                    : "该计时器是独立操作步骤；后续可继续录制动作或添加断言。",
                ForeColor = Color.FromArgb(80, 80, 80),
                Location = new Point(20, 150),
                Size = new Size(420, 34)
            });

            var cancelButton = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(270, 200), Size = new Size(75, 28) };
            var okButton = new Button { Text = flowType == "condition" ? "开始 IF" : "添加等待", DialogResult = DialogResult.OK, Location = new Point(355, 200), Size = new Size(75, 28) };
            Controls.Add(cancelButton);
            Controls.Add(okButton);
            CancelButton = cancelButton;
            AcceptButton = okButton;
        }

        internal FlowControlDraft Draft
        {
            get
            {
                return new FlowControlDraft
                {
                    Type = flowType,
                    ConditionType = flowType == "condition" ? ((Option)conditionInput.SelectedItem).Value : null,
                    TimeoutMilliseconds = flowType == "wait_time" ? (int)timeoutInput.Value * 1000 : 0
                };
            }
        }

        private static NumericUpDown CreateNumberInput(int x, int y, int minimum, int maximum, int value)
        {
            return new NumericUpDown
            {
                Location = new Point(x, y),
                Size = new Size(100, 25),
                Minimum = minimum,
                Maximum = maximum,
                Value = value
            };
        }

        private static string DescribeTarget(UiaNode target)
        {
            if (target == null) return "未识别";
            if (!string.IsNullOrWhiteSpace(target.Name)) return target.Name;
            if (!string.IsNullOrWhiteSpace(target.AutomationId)) return target.AutomationId;
            return string.IsNullOrWhiteSpace(target.ControlType) ? "当前控件" : target.ControlType;
        }

        private sealed class Option
        {
            internal readonly string Value;
            private readonly string label;
            internal Option(string value, string labelText) { Value = value; label = labelText; }
            public override string ToString() { return label; }
        }
    }
}
