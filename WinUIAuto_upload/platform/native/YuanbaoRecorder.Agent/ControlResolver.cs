using System;
using System.Collections.Generic;
using System.Linq;

namespace YuanbaoRecorder.Agent
{
    internal static class ControlResolver
    {
        private static readonly HashSet<string> InteractiveControlTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Button", "Edit", "CheckBox", "RadioButton", "ComboBox", "ListItem", "MenuItem",
            "TabItem", "TreeItem", "Hyperlink", "Slider", "Spinner", "DataItem"
        };

        internal static double ScoreNode(UiaNode node, NodeBounds windowBounds)
        {
            if (node == null || node.Offscreen || node.Bounds == null || node.Bounds.Width <= 0 || node.Bounds.Height <= 0)
            {
                return double.MinValue;
            }

            if (string.Equals(node.ControlType, "Document", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(node.ControlType, "Window", StringComparison.OrdinalIgnoreCase))
            {
                return -10000;
            }

            var score = 0.0;
            if (!string.IsNullOrWhiteSpace(node.AutomationId)) score += 500;
            if (!string.IsNullOrWhiteSpace(node.Name)) score += 260;
            if (!string.IsNullOrWhiteSpace(node.ClassName)) score += 90;
            if (LooksLikeInteractiveClass(node.ClassName)) score += 360;
            if (InteractiveControlTypes.Contains(node.ControlType ?? string.Empty)) score += 220;
            if (node.Patterns.Contains("Invoke") || node.Patterns.Contains("Toggle") ||
                node.Patterns.Contains("SelectionItem") || node.Patterns.Contains("ExpandCollapse")) score += 240;
            if (node.Patterns.Contains("Value")) score += 220;
            if (node.Patterns.Contains("Text") && node.Focusable) score += 100;
            if (node.Enabled) score += 30;
            if (node.Focusable) score += 20;

            var hasOwnIdentity = !string.IsNullOrWhiteSpace(node.AutomationId) ||
                !string.IsNullOrWhiteSpace(node.Name) || !string.IsNullOrWhiteSpace(node.ClassName);
            if (!hasOwnIdentity && string.Equals(node.ControlType, "Image", StringComparison.OrdinalIgnoreCase)) score -= 320;
            if (!hasOwnIdentity && string.Equals(node.ControlType, "Group", StringComparison.OrdinalIgnoreCase)) score -= 160;
            if (!hasOwnIdentity && string.Equals(node.ControlType, "Pane", StringComparison.OrdinalIgnoreCase)) score -= 180;
            if (LooksLikeGenericChromeHost(node)) score -= 1600;
            else if (LooksLikeGenericContainer(node)) score -= 420;

            var area = Math.Max(1, node.Bounds.Width * node.Bounds.Height);
            var windowArea = windowBounds == null ? area : Math.Max(1, windowBounds.Width * windowBounds.Height);
            var areaRatio = area / windowArea;
            if (areaRatio > 0.75) score -= 1200;
            else if (areaRatio > 0.40) score -= 700;
            else score += Math.Max(0, 45 - Math.Log10(area) * 7);
            return score;
        }

        private static bool LooksLikeInteractiveClass(string className)
        {
            if (string.IsNullOrWhiteSpace(className)) return false;
            return className.IndexOf("trigger", StringComparison.OrdinalIgnoreCase) >= 0 ||
                className.IndexOf("button", StringComparison.OrdinalIgnoreCase) >= 0 ||
                className.IndexOf("action", StringComparison.OrdinalIgnoreCase) >= 0 ||
                className.IndexOf("menuitem", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool LooksLikeGenericContainer(UiaNode node)
        {
            if (node == null) return false;
            return string.Equals(node.Name, "scrollable content", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(node.ClassName) &&
                    (node.ClassName.IndexOf("simplebar-content", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     node.ClassName.IndexOf("scrollcontainer", StringComparison.OrdinalIgnoreCase) >= 0));
        }

        private static bool LooksLikeGenericChromeHost(UiaNode node)
        {
            return node != null &&
                (string.Equals(node.Name, "Chrome Legacy Window", StringComparison.OrdinalIgnoreCase) ||
                 (!string.IsNullOrWhiteSpace(node.ClassName) &&
                  node.ClassName.IndexOf("Chrome_RenderWidgetHostHWND", StringComparison.OrdinalIgnoreCase) >= 0));
        }

        internal static UiaNode SelectBest(IEnumerable<UiaNode> candidates, NodeBounds windowBounds)
        {
            return (candidates ?? Enumerable.Empty<UiaNode>())
                .OrderByDescending(node => ScoreNode(node, windowBounds))
                .ThenBy(node => node.Bounds == null ? double.MaxValue : node.Bounds.Width * node.Bounds.Height)
                .FirstOrDefault(node => ScoreNode(node, windowBounds) > double.MinValue);
        }

        internal static bool IsBetter(UiaNode candidate, UiaNode current, NodeBounds windowBounds)
        {
            if (candidate == null) return false;
            if (current == null) return true;
            return ScoreNode(candidate, windowBounds) > ScoreNode(current, windowBounds) + 40;
        }

        internal static string Describe(UiaNode node)
        {
            if (node == null) return "未识别控件";
            var role = !string.IsNullOrWhiteSpace(node.Name)
                ? node.Name
                : !string.IsNullOrWhiteSpace(node.AutomationId) ? node.AutomationId : node.ControlType;
            return role + " · " + (node.ControlType ?? "Control");
        }
    }
}
