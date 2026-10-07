using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace YuanbaoRecorder.Agent
{
    internal sealed class AssertionDialog : Form
    {
        private readonly AssertionCaptureContext capture;
        private readonly ComboBox typeInput;
        private readonly ComboBox scopeInput;
        private readonly Label valueLabel;
        private readonly TextBox valueInput;
        private readonly Label minimumLabel;
        private readonly NumericUpDown minimumInput;
        private readonly Label rowsLabel;
        private readonly NumericUpDown rowsInput;
        private readonly Label columnsLabel;
        private readonly NumericUpDown columnsInput;
        private readonly Label controlTypeLabel;
        private readonly ComboBox controlTypeInput;
        private readonly Label bindingHint;

        internal event Action<string> ScopeChanged;

        internal AssertionDialog(AssertionCaptureContext capture)
        {
            this.capture = capture;
            Text = "添加验证点";
            Size = new Size(540, 510);
            MinimumSize = Size;
            MaximumSize = Size;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            TopMost = true;

            Controls.Add(new Label
            {
                Text = "已选择：" + DescribeTarget(capture.Target),
                AutoEllipsis = true,
                Font = new Font(Font.FontFamily, 11, FontStyle.Bold),
                Location = new Point(22, 18),
                Size = new Size(480, 28)
            });
            Controls.Add(new Label
            {
                Text = capture.SuggestedScopeControlType == "Table"
                    ? "已识别该内容属于表格，表格类验证会自动作用于整个表格。"
                    : "定位信息由 Agent 自动采集，只需说明期望结果。",
                ForeColor = Color.DimGray,
                Location = new Point(22, 48),
                Size = new Size(480, 25)
            });

            Controls.Add(new Label { Text = "验证内容", AutoSize = true, Location = new Point(22, 88) });
            typeInput = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(102, 84),
                Size = new Size(400, 25)
            };
            typeInput.Items.Add(new AssertionOption("target_exists", "控件存在（不校验名称）"));
            typeInput.Items.Add(new AssertionOption("property_equals_name", "控件标题/占位提示精确一致"));
            typeInput.Items.Add(new AssertionOption("property_equals_value", "输入内容精确一致"));
            typeInput.Items.Add(new AssertionOption("text_contains", "此处包含指定文字（兼容类型）"));
            typeInput.Items.Add(new AssertionOption("target_not_exists", "下一次操作后这个内容应该消失"));
            typeInput.Items.Add(new AssertionOption("keywords_match_count", "多个关键词至少命中指定数量"));
            typeInput.Items.Add(new AssertionOption("table_dimensions", "表格行列数量正确"));
            typeInput.Items.Add(new AssertionOption("horizontal_bounds_within_window", "内容不应横向超出窗口"));
            typeInput.Items.Add(new AssertionOption("descendants_within_bounds", "区域内容不应超出区域边界"));
            typeInput.Items.Add(new AssertionOption("descendant_count", "区域内指定控件数量正确"));
            for (var index = typeInput.Items.Count - 1; index >= 0; index--)
            {
                var option = (AssertionOption)typeInput.Items[index];
                if (option.Value != "text_contains" && option.Value != "property_equals_name" && option.Value != "property_equals_value" && option.Value != "target_exists" &&
                    option.Value != "target_not_exists" && option.Value != "keywords_match_count")
                {
                    typeInput.Items.RemoveAt(index);
                }
            }
            typeInput.SelectedIndex = 0;
            typeInput.SelectedIndexChanged += delegate
            {
                if (AssertionType == "keywords_match_count") SelectScope("window");
                if (AssertionType == "table_dimensions") SelectScope("table");
                valueInput.Text = SuggestedExpectedForAssertion();
                UpdateForm();
            };
            Controls.Add(typeInput);

            Controls.Add(new Label { Text = "验证范围", AutoSize = true, Location = new Point(22, 119) });
            scopeInput = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(102, 115),
                Size = new Size(400, 25)
            };
            scopeInput.Items.Add(new AssertionOption("window", "整个元宝窗口"));
            scopeInput.Items.Add(new AssertionOption("target", "当前选中的内容"));
            if (capture.SuggestedScopeControlType == "Table")
            {
                scopeInput.Items.Add(new AssertionOption("table", "当前内容所属的整个表格"));
            }
            scopeInput.SelectedIndex = 1;
            scopeInput.SelectedIndexChanged += delegate
            {
                UpdateForm();
                if (ScopeChanged != null) ScopeChanged(SelectedScope);
            };
            Controls.Add(scopeInput);

            // Keep the caption on its own row. At 125%+ DPI the longer
            // "期望输入内容" caption used to grow over the text box.
            valueLabel = new Label { AutoSize = true, Location = new Point(22, 151) };
            valueInput = new TextBox
            {
                Location = new Point(22, 176),
                Size = new Size(480, 55),
                Multiline = true,
                MaxLength = 2000,
                Text = SuggestedExpected(capture.Target)
            };
            Controls.Add(valueLabel);
            Controls.Add(valueInput);

            minimumLabel = new Label { Text = "至少命中", AutoSize = true, Location = new Point(22, 229) };
            minimumInput = CreateNumberInput(102, 225, 1, 100, 3);
            Controls.Add(minimumLabel);
            Controls.Add(minimumInput);

            rowsLabel = new Label { Text = "数据行数", AutoSize = true, Location = new Point(22, 268) };
            rowsInput = CreateNumberInput(102, 264, 0, 1000, 2);
            columnsLabel = new Label { Text = "列数", AutoSize = true, Location = new Point(225, 268) };
            columnsInput = CreateNumberInput(275, 264, 1, 1000, 3);
            Controls.Add(rowsLabel);
            Controls.Add(rowsInput);
            Controls.Add(columnsLabel);
            Controls.Add(columnsInput);

            controlTypeLabel = new Label { Text = "控件类型", AutoSize = true, Location = new Point(22, 307) };
            controlTypeInput = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(102, 303),
                Size = new Size(160, 25)
            };
            controlTypeInput.Items.AddRange(new object[] { "Text", "DataItem", "Button", "Edit", "Image", "Group" });
            controlTypeInput.SelectedIndex = 0;
            Controls.Add(controlTypeLabel);
            Controls.Add(controlTypeInput);

            bindingHint = new Label
            {
                ForeColor = Color.FromArgb(45, 95, 145),
                Location = new Point(22, 377),
                Size = new Size(480, 35)
            };
            Controls.Add(bindingHint);

            var cancelButton = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                Location = new Point(332, 420),
                Size = new Size(80, 30)
            };
            Controls.Add(cancelButton);
            var confirmButton = new Button
            {
                Text = "添加验证点",
                DialogResult = DialogResult.None,
                Location = new Point(422, 420),
                Size = new Size(80, 30)
            };
            confirmButton.Click += ConfirmButtonClick;
            Controls.Add(confirmButton);
            AcceptButton = confirmButton;
            CancelButton = cancelButton;
            if (AssertionType == "keywords_match_count") SelectScope("window");
            UpdateForm();
        }

        internal AssertionDraft Draft
        {
            get
            {
                var selectedScope = ((AssertionOption)scopeInput.SelectedItem).Value;
                return new AssertionDraft
                {
                    Type = IsExactPropertyAssertion ? "property_equals" : AssertionType,
                    Expected = AssertionType == "text_contains" || IsExactPropertyAssertion ? valueInput.Text.Trim() : null,
                    AssertionMode = AssertionType == "target_exists" ? "component_exists"
                        : AssertionType == "property_equals_name" ? "component_name_equals"
                        : AssertionType == "property_equals_value" ? "input_value_equals" : null,
                    Property = AssertionType == "property_equals_name" ? "name"
                        : AssertionType == "property_equals_value" ? "value" : null,
                    Scope = selectedScope == "table" ? "nearest_ancestor" : selectedScope,
                    ScopeControlType = selectedScope == "table" ? "Table" : null,
                    Keywords = ParseKeywords(valueInput.Text),
                    MinimumMatches = Decimal.ToInt32(minimumInput.Value),
                    DescendantControlType = controlTypeInput.Text,
                    CountOperator = "equals",
                    ExpectedCount = Decimal.ToInt32(minimumInput.Value),
                    ExpectedRows = Decimal.ToInt32(rowsInput.Value),
                    ExpectedColumns = Decimal.ToInt32(columnsInput.Value),
                    BoundsTolerancePixels = 1,
                    TimeoutMilliseconds = 1000
                };
            }
        }

        private string AssertionType { get { return ((AssertionOption)typeInput.SelectedItem).Value; } }
        private bool IsExactPropertyAssertion
        {
            get { return AssertionType == "property_equals_name" || AssertionType == "property_equals_value"; }
        }

        internal string SelectedScope
        {
            get
            {
                var option = scopeInput.SelectedItem as AssertionOption;
                return option == null ? "target" : option.Value;
            }
        }

        private void UpdateForm()
        {
            var text = AssertionType == "text_contains" || IsExactPropertyAssertion;
            var keywords = AssertionType == "keywords_match_count";
            var dimensions = AssertionType == "table_dimensions";
            var count = AssertionType == "descendant_count";
            scopeInput.Enabled = AssertionType != "table_dimensions" && !IsExactPropertyAssertion;
            if (AssertionType == "table_dimensions") SelectScope("table");
            if (IsExactPropertyAssertion) SelectScope("target");
            valueLabel.Visible = text || keywords;
            valueInput.Visible = text || keywords;
            valueLabel.Text = keywords ? "候选关键词" : AssertionType == "property_equals_value" ? "期望输入内容" : "期望文字";
            minimumLabel.Visible = keywords || count;
            minimumInput.Visible = keywords || count;
            minimumLabel.Text = count ? "期望数量" : "至少命中";
            rowsLabel.Visible = dimensions;
            rowsInput.Visible = dimensions;
            columnsLabel.Visible = dimensions;
            columnsInput.Visible = dimensions;
            controlTypeLabel.Visible = count || IsExactPropertyAssertion;
            controlTypeInput.Visible = count || IsExactPropertyAssertion;
            var selectedScope = ((AssertionOption)scopeInput.SelectedItem).Value;
            bindingHint.Text = AssertionType == "target_not_exists"
                ? "验证点会自动绑定到接下来在元宝执行的操作。"
                : selectedScope == "window" ? "验证范围：整个元宝窗口。"
                    : selectedScope == "table" ? "验证范围：系统识别到的整个表格。"
                        : "验证范围：当前选中的内容。";
        }

        private void ConfirmButtonClick(object sender, EventArgs eventArgs)
        {
            if ((AssertionType == "text_contains" || IsExactPropertyAssertion || AssertionType == "keywords_match_count") &&
                string.IsNullOrWhiteSpace(valueInput.Text))
            {
                MessageBox.Show("请输入需要验证的文字或关键词", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                valueInput.Focus();
                return;
            }
            if (AssertionType == "property_equals_name" &&
                (!string.Equals(capture.Target.Name ?? string.Empty, valueInput.Text.Trim(), StringComparison.Ordinal) ||
                 !string.Equals(capture.Target.ControlType ?? string.Empty, controlTypeInput.Text, StringComparison.Ordinal)))
            {
                MessageBox.Show("精确名称断言必须直接选中同名控件，并选择与该控件一致的控件类型。请按 Ctrl 后将鼠标停在目标文字控件上重新添加。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (AssertionType == "property_equals_value" &&
                (!capture.Target.Patterns.Contains("Value") ||
                 !string.Equals(capture.Target.Value ?? string.Empty, valueInput.Text.Trim(), StringComparison.Ordinal) ||
                 !string.Equals(capture.Target.ControlType ?? string.Empty, controlTypeInput.Text, StringComparison.Ordinal)))
            {
                MessageBox.Show("输入内容断言必须直接选中实际输入控件，且其 UIA Value 必须与期望内容完全一致。请在输入完成后按 Ctrl，将鼠标停在输入框上重新添加。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (AssertionType == "table_dimensions" && capture.SuggestedScopeControlType != "Table")
            {
                MessageBox.Show("未识别到表格范围，请将鼠标停在表格单元格上重新添加验证点。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        private void SelectScope(string value)
        {
            for (var index = 0; index < scopeInput.Items.Count; index++)
            {
                if (((AssertionOption)scopeInput.Items[index]).Value != value) continue;
                scopeInput.SelectedIndex = index;
                return;
            }
        }

        private static NumericUpDown CreateNumberInput(int x, int y, int minimum, int maximum, int value)
        {
            return new NumericUpDown
            {
                Location = new Point(x, y),
                Size = new Size(80, 25),
                Minimum = minimum,
                Maximum = maximum,
                Value = value
            };
        }

        private static List<string> ParseKeywords(string value)
        {
            return (value ?? string.Empty)
                .Split(new[] { ',', '，', '\r', '\n', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => item.Length > 0)
                .Distinct()
                .ToList();
        }

        private static string SuggestedExpected(UiaNode target)
        {
            if (target == null || string.IsNullOrWhiteSpace(target.Name)) return string.Empty;
            return target.Name.Length > 100 ? target.Name.Substring(0, 100) : target.Name;
        }

        private string SuggestedExpectedForAssertion()
        {
            if (AssertionType == "property_equals_value")
            {
                return capture.Target == null ? string.Empty : capture.Target.Value ?? string.Empty;
            }
            return SuggestedExpected(capture.Target);
        }

        private static string DescribeTarget(UiaNode target)
        {
            if (target == null) return "元宝中的所选内容";
            if (!string.IsNullOrWhiteSpace(target.Name)) return target.Name.Length > 80 ? target.Name.Substring(0, 80) : target.Name;
            if (!string.IsNullOrWhiteSpace(target.AutomationId)) return target.AutomationId;
            return string.IsNullOrWhiteSpace(target.ControlType) ? "元宝中的所选内容" : target.ControlType;
        }

        private sealed class AssertionOption
        {
            internal AssertionOption(string value, string label) { Value = value; Label = label; }
            internal string Value { get; private set; }
            private string Label { get; set; }
            public override string ToString() { return Label; }
        }
    }
}
