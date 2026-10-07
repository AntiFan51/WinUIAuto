using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace YuanbaoRecorder.Agent
{
    /// <summary>
    /// Visualizes the complete target decision instead of only drawing the final UIA node.
    /// The three windows remain independent so nested hit/target/scope rectangles stay visible.
    /// </summary>
    internal sealed class ControlHighlightOverlay : IDisposable
    {
        private static readonly Color HitColor = Color.FromArgb(45, 135, 255);
        private static readonly Color TargetColor = Color.FromArgb(0, 220, 160);
        private static readonly Color ScopeColor = Color.FromArgb(255, 155, 45);

        private readonly HighlightWindow hitWindow = new HighlightWindow(HitColor, 2);
        private readonly HighlightWindow targetWindow = new HighlightWindow(TargetColor, 3);
        private readonly HighlightWindow scopeWindow = new HighlightWindow(ScopeColor, 4);
        private bool disposed;

        internal void ShowPreview(UiaTargetCaptureResult preview)
        {
            if (preview == null || !HasBounds(preview.Target))
            {
                Hide();
                return;
            }

            scopeWindow.Hide();
            if (HasBounds(preview.HitTarget) && !SameBounds(preview.HitTarget.Bounds, preview.Target.Bounds))
            {
                hitWindow.ShowBounds(preview.HitTarget.Bounds, null, 1);
            }
            else
            {
                hitWindow.Hide();
            }

            targetWindow.ShowBounds(
                preview.Target.Bounds,
                BuildTargetDescription(preview.Target, preview.Decision),
                3);
        }

        internal void ShowAssertion(AssertionCaptureContext capture, string selectedScope)
        {
            if (capture == null || !HasBounds(capture.Target))
            {
                Hide();
                return;
            }

            if (HasBounds(capture.HitTarget) && !SameBounds(capture.HitTarget.Bounds, capture.Target.Bounds))
            {
                hitWindow.ShowBounds(capture.HitTarget.Bounds, null, 1);
            }
            else
            {
                hitWindow.Hide();
            }

            targetWindow.ShowBounds(
                capture.Target.Bounds,
                BuildTargetDescription(capture.Target, capture.TargetDecision),
                3);

            var scope = ResolveAssertionScopeNode(capture, selectedScope);
            if (scope == null || !HasBounds(scope) || SameBounds(scope.Bounds, capture.Target.Bounds))
            {
                scopeWindow.Hide();
            }
            else
            {
                scopeWindow.ShowBounds(scope.Bounds, BuildScopeDescription(selectedScope, scope), 7);
            }
        }

        // Kept for call sites that only have a resolved target.
        internal void ShowTarget(UiaNode target)
        {
            hitWindow.Hide();
            scopeWindow.Hide();
            if (!HasBounds(target))
            {
                targetWindow.Hide();
                return;
            }
            targetWindow.ShowBounds(target.Bounds, BuildTargetDescription(target, null), 3);
        }

        internal static UiaNode ResolveAssertionScopeNode(AssertionCaptureContext capture, string selectedScope)
        {
            if (capture == null) return null;
            if (string.Equals(selectedScope, "target", StringComparison.OrdinalIgnoreCase)) return capture.Target;

            var nodes = capture.Snapshot == null
                ? new List<UiaNode>()
                : capture.Snapshot.Nodes.Where(HasBounds).ToList();
            if (string.Equals(selectedScope, "window", StringComparison.OrdinalIgnoreCase))
            {
                return nodes
                    .Where(node => node.ParentIndex < 0)
                    .OrderByDescending(Area)
                    .FirstOrDefault() ?? nodes.OrderByDescending(Area).FirstOrDefault() ?? capture.Target;
            }

            if (string.Equals(selectedScope, "table", StringComparison.OrdinalIgnoreCase))
            {
                var x = capture.ScreenPoint == null ? double.NaN : capture.ScreenPoint.X;
                var y = capture.ScreenPoint == null ? double.NaN : capture.ScreenPoint.Y;
                return nodes
                    .Where(node => string.Equals(node.ControlType, "Table", StringComparison.OrdinalIgnoreCase))
                    .Where(node => double.IsNaN(x) || Contains(node.Bounds, x, y))
                    .OrderBy(Area)
                    .FirstOrDefault();
            }

            return capture.Target;
        }

        internal void Hide()
        {
            hitWindow.Hide();
            targetWindow.Hide();
            scopeWindow.Hide();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            hitWindow.Dispose();
            targetWindow.Dispose();
            scopeWindow.Dispose();
        }

        private static bool HasBounds(UiaNode node)
        {
            return node != null && node.Bounds != null && node.Bounds.Width > 0 && node.Bounds.Height > 0;
        }

        private static bool SameBounds(NodeBounds first, NodeBounds second)
        {
            if (first == null || second == null) return false;
            return Math.Abs(first.X - second.X) < 1 && Math.Abs(first.Y - second.Y) < 1 &&
                Math.Abs(first.Width - second.Width) < 1 && Math.Abs(first.Height - second.Height) < 1;
        }

        private static bool Contains(NodeBounds bounds, double x, double y)
        {
            return bounds != null && x >= bounds.X && x <= bounds.X + bounds.Width &&
                y >= bounds.Y && y <= bounds.Y + bounds.Height;
        }

        private static double Area(UiaNode node)
        {
            return node == null || node.Bounds == null ? double.MaxValue : node.Bounds.Width * node.Bounds.Height;
        }

        private static string BuildTargetDescription(UiaNode target, string decision)
        {
            var description = "目标 · " + ControlResolver.Describe(target);
            if (!string.IsNullOrWhiteSpace(target.AutomationId)) description += " · #" + target.AutomationId;
            if (string.Equals(decision, "promoted_semantic_ancestor", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(decision, "semantic_snapshot_promotion", StringComparison.OrdinalIgnoreCase))
            {
                description += " · 已提升到语义父控件";
            }
            return description;
        }

        private static string BuildScopeDescription(string selectedScope, UiaNode scope)
        {
            if (string.Equals(selectedScope, "window", StringComparison.OrdinalIgnoreCase)) return "断言范围 · 整个元宝窗口";
            if (string.Equals(selectedScope, "table", StringComparison.OrdinalIgnoreCase)) return "断言范围 · 所属表格";
            return "断言范围 · " + ControlResolver.Describe(scope);
        }

        // Uses the same four-edge overlay architecture as FlaUI's MIT-licensed
        // WinFormsOverlayManager. The target interior never contains an overlay HWND.
        private sealed class HighlightWindow : IDisposable
        {
            private readonly int borderThickness;
            private readonly PassiveOverlayWindow left;
            private readonly PassiveOverlayWindow top;
            private readonly PassiveOverlayWindow right;
            private readonly PassiveOverlayWindow bottom;
            private readonly HighlightLabelWindow label;

            internal HighlightWindow(Color borderColor, int borderThickness)
            {
                this.borderThickness = borderThickness;
                left = new PassiveOverlayWindow(borderColor);
                top = new PassiveOverlayWindow(borderColor);
                right = new PassiveOverlayWindow(borderColor);
                bottom = new PassiveOverlayWindow(borderColor);
                label = new HighlightLabelWindow(borderColor);
            }

            internal void ShowBounds(NodeBounds bounds, string description, int padding)
            {
                if (bounds == null || bounds.Width <= 0 || bounds.Height <= 0)
                {
                    Hide();
                    return;
                }

                var x = (int)Math.Floor(bounds.X) - padding;
                var y = (int)Math.Floor(bounds.Y) - padding;
                var width = Math.Max(8, (int)Math.Ceiling(bounds.Width) + padding * 2);
                var height = Math.Max(8, (int)Math.Ceiling(bounds.Height) + padding * 2);
                var thickness = Math.Min(borderThickness, Math.Min(width / 2, height / 2));

                left.ShowAt(new Rectangle(x, y, thickness, height));
                top.ShowAt(new Rectangle(x, y, width, thickness));
                right.ShowAt(new Rectangle(x + width - thickness, y, thickness, height));
                bottom.ShowAt(new Rectangle(x, y + height - thickness, width, thickness));

                if (string.IsNullOrWhiteSpace(description))
                {
                    label.Hide();
                }
                else
                {
                    const int labelHeight = 23;
                    var labelTop = y - labelHeight;
                    if (labelTop < SystemInformation.VirtualScreen.Top) labelTop = y + thickness;
                    label.ShowLabel(
                        new Rectangle(x, labelTop, Math.Max(180, Math.Min(620, width)), labelHeight),
                        description);
                }
            }

            internal void Hide()
            {
                left.Hide();
                top.Hide();
                right.Hide();
                bottom.Hide();
                label.Hide();
            }

            public void Dispose()
            {
                left.Dispose();
                top.Dispose();
                right.Dispose();
                bottom.Dispose();
                label.Dispose();
            }
        }

        private class PassiveOverlayWindow : Form
        {
            private const int WsExTransparent = 0x20;
            private const int WsExToolWindow = 0x80;
            private const int WsExNoActivate = 0x08000000;

            internal PassiveOverlayWindow(Color background)
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                TopMost = true;
                BackColor = background;
                // A color key makes this a layered window. Combined with
                // WS_EX_TRANSPARENT, Windows routes pointer input underneath it.
                TransparencyKey = Color.Magenta;
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    var parameters = base.CreateParams;
                    parameters.ExStyle |= WsExTransparent | WsExToolWindow | WsExNoActivate;
                    return parameters;
                }
            }

            internal void ShowAt(Rectangle bounds)
            {
                Bounds = bounds;
                if (!Visible) Show();
            }
        }

        private sealed class HighlightLabelWindow : PassiveOverlayWindow
        {
            private readonly Color accentColor;
            private string description = string.Empty;

            internal HighlightLabelWindow(Color accentColor)
                : base(Color.FromArgb(15, 35, 45))
            {
                this.accentColor = accentColor;
            }

            internal void ShowLabel(Rectangle bounds, string text)
            {
                description = text ?? string.Empty;
                ShowAt(bounds);
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs eventArgs)
            {
                base.OnPaint(eventArgs);
                using (var accent = new SolidBrush(accentColor))
                using (var textBrush = new SolidBrush(Color.White))
                {
                    eventArgs.Graphics.FillRectangle(accent, 0, Height - 3, Width, 3);
                    eventArgs.Graphics.DrawString(description, Font, textBrush, 4, 3);
                }
            }
        }
    }
}
