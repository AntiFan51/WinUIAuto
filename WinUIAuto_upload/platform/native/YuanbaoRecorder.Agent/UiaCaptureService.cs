using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Automation;

namespace YuanbaoRecorder.Agent
{
    internal sealed class UiaCaptureResult
    {
        internal UiaSnapshot Snapshot;
        internal UiaNode Target;
        internal LocatorBundle Locator;
        internal double DurationMilliseconds;
        internal string ReadMode;
    }

    internal sealed class UiaTargetCaptureResult
    {
        internal UiaNode Target;
        internal LocatorBundle Locator;
        internal UiaNode HitTarget;
        internal LocatorBundle HitLocator;
        internal int CandidateCount;
        internal string Decision;
    }

    internal sealed class UiaCaptureService
    {
        private const int MaximumNodes = 1200;
        private const int MaximumDepth = 40;

        internal UiaCaptureResult Capture(ClickObservation observation)
        {
            var stopwatch = Stopwatch.StartNew();
            var root = ResolvePointRoot(observation);
            if (root == null)
            {
                throw new InvalidOperationException("无法从元宝窗口句柄创建 UIA 根节点");
            }

            var snapshot = new UiaSnapshot
            {
                CapturedAt = DateTime.UtcNow.ToString("O"),
                WindowTitle = observation.WindowTitle
            };
            var candidates = new List<UiaCandidate>();
            var readMode = "cached_raw_view";
            try
            {
                var cachedRoot = CacheSubtree(root);
                TraverseCached(cachedRoot, -1, 0, observation.X, observation.Y, snapshot, candidates);
            }
            catch (Exception error)
            {
                if (!(error is ElementNotAvailableException) &&
                    !(error is InvalidOperationException) &&
                    !(error is NotSupportedException)) throw;
                snapshot.Nodes.Clear();
                candidates.Clear();
                readMode = "legacy_raw_view_fallback";
                Traverse(root, -1, 0, observation.X, observation.Y, snapshot, candidates);
            }
            snapshot.NodeCount = snapshot.Nodes.Count;
            snapshot.Truncated = snapshot.Nodes.Count >= MaximumNodes;

            var selectedNode = ControlResolver.SelectBest(
                candidates.Select(candidate => candidate.Node),
                snapshot.Nodes.Count == 0 ? null : snapshot.Nodes[0].Bounds);
            var selected = selectedNode == null
                ? null
                : candidates.FirstOrDefault(candidate => object.ReferenceEquals(candidate.Node, selectedNode));

            var nodesByIndex = snapshot.Nodes.ToDictionary(node => node.Index);
            stopwatch.Stop();
            return new UiaCaptureResult
            {
                Snapshot = snapshot,
                Target = selected == null ? null : selected.Node,
                Locator = selected == null
                    ? new LocatorBundle
                    {
                        FallbackWindowPoint = new TracePoint(
                            observation.X - observation.WindowRectangle.Left,
                            observation.Y - observation.WindowRectangle.Top)
                    }
                    : BuildLocatorFromSnapshot(selected.Node, nodesByIndex, observation),
                DurationMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
                ReadMode = readMode
            };
        }

        private static AutomationElement CacheSubtree(AutomationElement root)
        {
            var request = new CacheRequest
            {
                AutomationElementMode = AutomationElementMode.Full,
                TreeFilter = Automation.RawViewCondition,
                TreeScope = TreeScope.Element | TreeScope.Descendants
            };
            request.Add(AutomationElement.RuntimeIdProperty);
            request.Add(AutomationElement.NameProperty);
            request.Add(AutomationElement.AutomationIdProperty);
            request.Add(AutomationElement.ClassNameProperty);
            request.Add(AutomationElement.FrameworkIdProperty);
            request.Add(AutomationElement.ControlTypeProperty);
            request.Add(AutomationElement.BoundingRectangleProperty);
            request.Add(AutomationElement.IsEnabledProperty);
            request.Add(AutomationElement.IsOffscreenProperty);
            request.Add(AutomationElement.IsKeyboardFocusableProperty);
            request.Add(InvokePattern.Pattern);
            request.Add(ValuePattern.Pattern);
            request.Add(TextPattern.Pattern);
            request.Add(TogglePattern.Pattern);
            request.Add(SelectionItemPattern.Pattern);
            request.Add(ExpandCollapsePattern.Pattern);
            request.Add(ScrollItemPattern.Pattern);
            return root.GetUpdatedCache(request);
        }

        private static void TraverseCached(
            AutomationElement element,
            int parentIndex,
            int depth,
            int clickX,
            int clickY,
            UiaSnapshot snapshot,
            List<UiaCandidate> candidates)
        {
            if (element == null || depth > MaximumDepth || snapshot.Nodes.Count >= MaximumNodes) return;

            UiaNode node;
            try
            {
                node = ReadCachedNode(element, snapshot.Nodes.Count, parentIndex, depth);
            }
            catch (ElementNotAvailableException)
            {
                return;
            }

            snapshot.Nodes.Add(node);
            if (Contains(node.Bounds, clickX, clickY))
            {
                node.CandidateScore = ControlResolver.ScoreNode(node, snapshot.Nodes[0].Bounds);
                candidates.Add(new UiaCandidate { Node = node });
            }

            AutomationElementCollection children;
            try
            {
                children = element.CachedChildren;
            }
            catch (InvalidOperationException)
            {
                return;
            }
            for (var index = 0; index < children.Count && snapshot.Nodes.Count < MaximumNodes; index++)
            {
                TraverseCached(children[index], node.Index, depth + 1, clickX, clickY, snapshot, candidates);
            }
        }

        private static UiaNode ReadCachedNode(AutomationElement element, int index, int parentIndex, int depth)
        {
            var rectangle = Cached(element, AutomationElement.BoundingRectangleProperty, Rect.Empty);
            var controlType = Cached<ControlType>(element, AutomationElement.ControlTypeProperty, null);
            var node = new UiaNode
            {
                Index = index,
                ParentIndex = parentIndex,
                Depth = depth,
                RuntimeId = Cached<int[]>(element, AutomationElement.RuntimeIdProperty, null),
                Name = Cached(element, AutomationElement.NameProperty, string.Empty),
                Value = ReadCachedValue(element),
                AutomationId = Cached(element, AutomationElement.AutomationIdProperty, string.Empty),
                ClassName = Cached(element, AutomationElement.ClassNameProperty, string.Empty),
                FrameworkId = Cached(element, AutomationElement.FrameworkIdProperty, string.Empty),
                ControlType = controlType == null
                    ? string.Empty
                    : controlType.ProgrammaticName.Replace("ControlType.", string.Empty),
                Bounds = new NodeBounds
                {
                    X = FiniteOrZero(rectangle.X),
                    Y = FiniteOrZero(rectangle.Y),
                    Width = PositiveFiniteOrZero(rectangle.Width),
                    Height = PositiveFiniteOrZero(rectangle.Height)
                },
                Enabled = Cached(element, AutomationElement.IsEnabledProperty, false),
                Offscreen = Cached(element, AutomationElement.IsOffscreenProperty, true),
                Focusable = Cached(element, AutomationElement.IsKeyboardFocusableProperty, false)
            };
            AddCachedPattern(element, InvokePattern.Pattern, "Invoke", node.Patterns);
            AddCachedPattern(element, ValuePattern.Pattern, "Value", node.Patterns);
            AddCachedPattern(element, TextPattern.Pattern, "Text", node.Patterns);
            AddCachedPattern(element, TogglePattern.Pattern, "Toggle", node.Patterns);
            AddCachedPattern(element, SelectionItemPattern.Pattern, "SelectionItem", node.Patterns);
            AddCachedPattern(element, ExpandCollapsePattern.Pattern, "ExpandCollapse", node.Patterns);
            AddCachedPattern(element, ScrollItemPattern.Pattern, "ScrollItem", node.Patterns);
            return node;
        }

        private static T Cached<T>(AutomationElement element, AutomationProperty property, T fallback)
        {
            try
            {
                var value = element.GetCachedPropertyValue(property, true);
                return value == null || value == AutomationElement.NotSupported ? fallback : (T)value;
            }
            catch (InvalidOperationException)
            {
                return fallback;
            }
        }

        private static string ReadCachedValue(AutomationElement element)
        {
            try
            {
                var pattern = element.GetCachedPattern(ValuePattern.Pattern) as ValuePattern;
                return pattern == null ? string.Empty : pattern.Cached.Value ?? string.Empty;
            }
            catch (InvalidOperationException)
            {
                return string.Empty;
            }
        }

        private static void AddCachedPattern(
            AutomationElement element,
            AutomationPattern pattern,
            string name,
            ICollection<string> patterns)
        {
            object patternObject;
            try
            {
                if (element.TryGetCachedPattern(pattern, out patternObject)) patterns.Add(name);
            }
            catch (InvalidOperationException)
            {
            }
        }

        internal UiaTargetCaptureResult CaptureTargetAtPoint(ClickObservation observation, bool includeLocators = true)
        {
            var root = ResolvePointRoot(observation);
            if (root == null) throw new InvalidOperationException("无法从元宝窗口句柄创建 UIA 根节点");

            var hitElement = AutomationElement.FromPoint(new Point(observation.X, observation.Y));
            if (hitElement == null) return new UiaTargetCaptureResult();
            // Hover preview is called repeatedly while Ctrl is held. Batch each node's
            // properties/patterns into one UIA provider request instead of making many
            // cross-process Current/TryGetCurrentPattern calls per ancestor.
            var hitNode = includeLocators
                ? ReadNode(hitElement, 0, -1, 0)
                : ReadPreviewNode(hitElement, 0);
            var candidates = new List<Tuple<AutomationElement, UiaNode>>();
            var current = hitElement;
            for (var depth = 0; current != null && depth < 14; depth++)
            {
                var node = depth == 0
                    ? hitNode
                    : includeLocators ? ReadNode(current, depth, -1, depth) : ReadPreviewNode(current, depth);
                candidates.Add(Tuple.Create(current, node));
                if (Automation.Compare(current, root)) break;
                try { current = TreeWalker.RawViewWalker.GetParent(current); }
                catch (ElementNotAvailableException) { break; }
            }
            var rootCandidate = candidates.LastOrDefault(item => Automation.Compare(item.Item1, root));
            var windowBounds = rootCandidate == null
                ? (includeLocators ? ReadNode(root, -1, -1, 0) : ReadPreviewNode(root, -1)).Bounds
                : rootCandidate.Item2.Bounds;
            var selectedNode = ControlResolver.SelectBest(candidates.Select(item => item.Item2), windowBounds);
            var selected = candidates.FirstOrDefault(item => object.ReferenceEquals(item.Item2, selectedNode)) ?? candidates[0];
            return new UiaTargetCaptureResult
            {
                Target = selected.Item2,
                Locator = includeLocators ? BuildLocator(selected.Item1, selected.Item2, root, observation) : null,
                HitTarget = hitNode,
                HitLocator = includeLocators ? BuildLocator(hitElement, hitNode, root, observation) : null,
                CandidateCount = candidates.Count,
                Decision = object.ReferenceEquals(selected.Item2, hitNode) ? "hit_leaf" : "promoted_semantic_ancestor"
            };
        }

        private static UiaNode ReadPreviewNode(AutomationElement element, int index)
        {
            var request = new CacheRequest
            {
                AutomationElementMode = AutomationElementMode.None,
                TreeFilter = Automation.RawViewCondition,
                TreeScope = TreeScope.Element
            };
            request.Add(AutomationElement.NameProperty);
            request.Add(AutomationElement.AutomationIdProperty);
            request.Add(AutomationElement.ClassNameProperty);
            request.Add(AutomationElement.ControlTypeProperty);
            request.Add(AutomationElement.BoundingRectangleProperty);
            request.Add(AutomationElement.IsEnabledProperty);
            request.Add(AutomationElement.IsOffscreenProperty);
            request.Add(AutomationElement.IsKeyboardFocusableProperty);
            request.Add(InvokePattern.Pattern);
            request.Add(ValuePattern.Pattern);
            request.Add(TextPattern.Pattern);
            request.Add(TogglePattern.Pattern);
            request.Add(SelectionItemPattern.Pattern);
            request.Add(ExpandCollapsePattern.Pattern);
            return ReadCachedNode(element.GetUpdatedCache(request), index, -1, index);
        }

        internal FocusedEditableCapture CaptureEditableAtPoint(ClickObservation observation)
        {
            var root = ResolvePointRoot(observation);
            if (root == null) return null;
            AutomationElement element;
            try
            {
                element = AutomationElement.FromPoint(new Point(observation.X, observation.Y));
            }
            catch (ElementNotAvailableException)
            {
                return null;
            }
            var direct = CaptureEditable(element, root, observation);
            if (direct != null) return direct;

            var current = element;
            for (var depth = 0; current != null && depth < 8; depth++)
            {
                try { current = TreeWalker.RawViewWalker.GetParent(current); }
                catch (ElementNotAvailableException) { break; }
                var ancestorEditable = CaptureEditable(current, root, observation);
                if (ancestorEditable != null) return ancestorEditable;
            }

            current = element;
            for (var depth = 0; current != null && depth < 5; depth++)
            {
                var descendantEditable = CaptureEditableDescendant(
                    current,
                    root,
                    observation,
                    true,
                    80);
                if (descendantEditable != null) return descendantEditable;
                try { current = TreeWalker.RawViewWalker.GetParent(current); }
                catch (ElementNotAvailableException) { break; }
            }
            return null;
        }

        internal FocusedEditableCapture CaptureFocusedEditable(ClickObservation observation)
        {
            AutomationElement element;
            try
            {
                element = AutomationElement.FocusedElement;
            }
            catch (ElementNotAvailableException)
            {
                return null;
            }
            var root = ResolveFocusedRoot(observation, element);
            if (root == null) return null;
            if (!IsDescendantOf(element, root)) return null;
            var direct = CaptureEditable(element, root, observation);
            if (direct != null) return direct;

            var current = element;
            for (var depth = 0; current != null && depth < 5; depth++)
            {
                var descendantEditable = CaptureEditableDescendant(
                    current,
                    root,
                    observation,
                    false,
                    120);
                if (descendantEditable != null) return descendantEditable;
                try { current = TreeWalker.RawViewWalker.GetParent(current); }
                catch (ElementNotAvailableException) { break; }
            }
            return null;
        }

        internal FocusedEditableCapture CaptureEditableAtBounds(
            ClickObservation observation,
            NodeBounds bounds)
        {
            if (observation == null || bounds == null || bounds.Width <= 0 || bounds.Height <= 0) return null;
            var probe = new ClickObservation
            {
                X = (int)Math.Round(bounds.X + bounds.Width / 2d),
                Y = (int)Math.Round(bounds.Y + bounds.Height / 2d),
                WindowHandle = observation.WindowHandle,
                WindowTitle = observation.WindowTitle,
                WindowRectangle = observation.WindowRectangle
            };
            return CaptureEditableAtPoint(probe);
        }

        private static AutomationElement ResolvePointRoot(ClickObservation observation)
        {
            var hitWindow = NativeMethods.WindowFromPoint(new NativeMethods.Point
            {
                X = observation.X,
                Y = observation.Y
            });
            var rootWindow = hitWindow == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetAncestor(hitWindow, 2);
            var resolved = TryFromHandle(rootWindow);
            return resolved ?? TryFromHandle(observation.WindowHandle);
        }

        private static AutomationElement ResolveFocusedRoot(
            ClickObservation observation,
            AutomationElement focusedElement)
        {
            var targetRoot = TryFromHandle(observation.WindowHandle);
            var foregroundWindow = NativeMethods.GetForegroundWindow();
            var foregroundRoot = TryFromHandle(foregroundWindow);

            // Global keyboard hooks also see typing in the recorder's own WinForms
            // controls. Never let an unrelated foreground process redefine the UIA
            // root for a target-app observation, otherwise fields such as "用例名称"
            // can be emitted as input_text actions in the recorded Yuanbao case.
            var targetProcessId = NativeMethods.ReadWindowProcessId(observation.WindowHandle);
            var foregroundProcessId = NativeMethods.ReadWindowProcessId(foregroundWindow);
            if (targetProcessId != 0 && targetProcessId == foregroundProcessId &&
                foregroundRoot != null && IsDescendantOf(focusedElement, foregroundRoot))
            {
                return foregroundRoot;
            }

            return targetRoot;
        }

        private static AutomationElement TryFromHandle(IntPtr window)
        {
            if (window == IntPtr.Zero) return null;
            try { return AutomationElement.FromHandle(window); }
            catch (ElementNotAvailableException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        private static FocusedEditableCapture CaptureEditableDescendant(
            AutomationElement container,
            AutomationElement root,
            ClickObservation observation,
            bool requirePointContainment,
            int maximumNodes)
        {
            try
            {
                var descendants = container.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                FocusedEditableCapture best = null;
                for (var index = 0; index < descendants.Count && index < maximumNodes; index++)
                {
                    var candidate = CaptureEditable(descendants[index], root, observation);
                    if (candidate == null || candidate.Bounds == null) continue;
                    if (requirePointContainment && !Contains(candidate.Bounds, observation.X, observation.Y)) continue;
                    if (best == null || candidate.Bounds.Width * candidate.Bounds.Height < best.Bounds.Width * best.Bounds.Height)
                    {
                        best = candidate;
                    }
                }
                return best;
            }
            catch (ElementNotAvailableException)
            {
                return null;
            }
        }

        private static FocusedEditableCapture CaptureEditable(
            AutomationElement element,
            AutomationElement root,
            ClickObservation observation)
        {
            if (element == null) return null;
            object valuePatternObject;
            object textPatternObject;
            var hasValue = TryPattern(element, ValuePattern.Pattern, out valuePatternObject);
            var hasText = TryPattern(element, TextPattern.Pattern, out textPatternObject);
            var controlType = Read(() => element.Current.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty), string.Empty);
            var automationId = Read(() => element.Current.AutomationId, string.Empty);
            var focusable = Read(() => element.Current.IsKeyboardFocusable, false);
            var className = Read(() => element.Current.ClassName, string.Empty);
            var contentEditable = className.IndexOf("ql-editor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                className.IndexOf("input", StringComparison.OrdinalIgnoreCase) >= 0 ||
                className.IndexOf("textarea", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!IsEditableControl(controlType, automationId, hasValue, hasText, focusable, contentEditable)) return null;

            var text = string.Empty;
            try
            {
                var valueText = hasValue ? ((ValuePattern)valuePatternObject).Current.Value : null;
                var documentText = !hasValue && hasText
                    ? ((TextPattern)textPatternObject).DocumentRange.GetText(-1)
                    : null;
                text = SelectEditableText(hasValue, valueText, hasText, documentText);
            }
            catch (ElementNotAvailableException)
            {
                return null;
            }
            var node = ReadNode(element, 0, -1, 0);
            return new FocusedEditableCapture
            {
                Text = text,
                Target = node,
                Locator = BuildLocator(element, node, root, observation),
                Bounds = node.Bounds
            };
        }

        internal static string SelectEditableText(bool hasValue, string valueText, bool hasText, string documentText)
        {
            if (hasValue) return valueText ?? string.Empty;
            if (hasText) return (documentText ?? string.Empty).TrimEnd('\r', '\n');
            return string.Empty;
        }

        internal static bool IsEditableControl(
            string controlType,
            string automationId,
            bool hasValuePattern,
            bool hasTextPattern,
            bool focusable,
            bool contentEditable)
        {
            if (string.Equals(controlType, "Document", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(controlType, "Window", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(automationId, "RootWebArea", StringComparison.OrdinalIgnoreCase)) return false;
            var isEdit = string.Equals(controlType, "Edit", StringComparison.OrdinalIgnoreCase);
            var isContentEditable = hasTextPattern && focusable && contentEditable;
            return isEdit || isContentEditable;
        }

        private static bool TryPattern(AutomationElement element, AutomationPattern pattern, out object value)
        {
            value = null;
            try
            {
                return element.TryGetCurrentPattern(pattern, out value);
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }
        }

        private static bool IsDescendantOf(AutomationElement element, AutomationElement root)
        {
            for (var current = element; current != null;)
            {
                if (current.Equals(root)) return true;
                try { current = TreeWalker.RawViewWalker.GetParent(current); }
                catch (ElementNotAvailableException) { return false; }
            }
            return false;
        }

        internal UiaSnapshot CaptureTransientMenus(IntPtr windowHandle)
        {
            var root = AutomationElement.FromHandle(windowHandle);
            if (root == null) return null;
            var condition = new OrCondition(
                new PropertyCondition(AutomationElement.AutomationIdProperty, "chatInputContextMenu"),
                new PropertyCondition(AutomationElement.AutomationIdProperty, "ybContextMenu"));
            AutomationElementCollection menuRoots;
            try
            {
                menuRoots = root.FindAll(TreeScope.Descendants, condition);
            }
            catch (ElementNotAvailableException)
            {
                return null;
            }
            if (menuRoots == null || menuRoots.Count == 0) return null;

            var snapshot = new UiaSnapshot
            {
                CapturedAt = DateTime.UtcNow.ToString("O"),
                WindowTitle = Read(() => root.Current.Name, string.Empty)
            };
            var candidates = new List<UiaCandidate>();
            for (var index = 0; index < menuRoots.Count && snapshot.Nodes.Count < MaximumNodes; index++)
            {
                Traverse(menuRoots[index], -1, 0, int.MinValue, int.MinValue, snapshot, candidates);
            }
            snapshot.NodeCount = snapshot.Nodes.Count;
            snapshot.Truncated = snapshot.Nodes.Count >= MaximumNodes;
            return snapshot.Nodes.Count == 0 ? null : snapshot;
        }

        internal UiaTargetCaptureResult CaptureTransientTargetFromSnapshot(
            UiaSnapshot snapshot,
            ClickObservation observation)
        {
            if (snapshot == null || snapshot.Nodes == null || snapshot.Nodes.Count == 0)
            {
                return new UiaTargetCaptureResult { Decision = "snapshot_unavailable" };
            }

            var nodesByIndex = snapshot.Nodes.ToDictionary(node => node.Index);
            var containingNodes = snapshot.Nodes
                .Where(node => Contains(node.Bounds, observation.X, observation.Y))
                .Where(node => IsInsideTransientMenu(node, nodesByIndex))
                .ToList();
            var decision = "no_transient_candidate";
            var selected = containingNodes
                .Where(IsDirectMenuTarget)
                .OrderBy(node => node.Bounds.Width * node.Bounds.Height)
                .ThenByDescending(TransientTargetScore)
                .FirstOrDefault();
            if (selected != null) decision = "direct_menu_target";
            if (selected == null)
            {
                var menuRow = containingNodes
                    .Where(IsMenuRow)
                    .OrderBy(node => node.Bounds.Width * node.Bounds.Height)
                    .FirstOrDefault();
                var namedDescendant = FindNamedDescendant(menuRow, snapshot.Nodes, nodesByIndex);
                selected = namedDescendant ?? menuRow;
                if (selected != null) decision = namedDescendant == null ? "menu_row" : "menu_row_named_descendant";
            }
            if (selected == null)
            {
                selected = containingNodes
                    .OrderByDescending(TransientTargetScore)
                    .ThenBy(node => node.Bounds.Width * node.Bounds.Height)
                    .FirstOrDefault();
                if (selected != null) decision = "scored_menu_candidate";
            }
            if (selected == null)
            {
                return new UiaTargetCaptureResult
                {
                    CandidateCount = containingNodes.Count,
                    Decision = decision
                };
            }

            return new UiaTargetCaptureResult
            {
                Target = selected,
                Locator = BuildLocatorFromSnapshot(selected, nodesByIndex, observation),
                CandidateCount = containingNodes.Count,
                Decision = decision
            };
        }

        private static void Traverse(
            AutomationElement element,
            int parentIndex,
            int depth,
            int clickX,
            int clickY,
            UiaSnapshot snapshot,
            List<UiaCandidate> candidates)
        {
            if (element == null || depth > MaximumDepth || snapshot.Nodes.Count >= MaximumNodes)
            {
                return;
            }

            UiaNode node;
            try
            {
                node = ReadNode(element, snapshot.Nodes.Count, parentIndex, depth);
            }
            catch (ElementNotAvailableException)
            {
                return;
            }

            snapshot.Nodes.Add(node);
            if (Contains(node.Bounds, clickX, clickY))
            {
                node.CandidateScore = ControlResolver.ScoreNode(node, snapshot.Nodes[0].Bounds);
                candidates.Add(new UiaCandidate { Node = node });
            }

            AutomationElement child;
            try
            {
                child = TreeWalker.RawViewWalker.GetFirstChild(element);
            }
            catch (ElementNotAvailableException)
            {
                return;
            }

            while (child != null && snapshot.Nodes.Count < MaximumNodes)
            {
                Traverse(child, node.Index, depth + 1, clickX, clickY, snapshot, candidates);
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

        private static UiaNode ReadNode(AutomationElement element, int index, int parentIndex, int depth)
        {
            var rectangle = Read(() => element.Current.BoundingRectangle, Rect.Empty);
            var node = new UiaNode
            {
                Index = index,
                ParentIndex = parentIndex,
                Depth = depth,
                RuntimeId = Read(element.GetRuntimeId, null),
                Name = Read(() => element.Current.Name, string.Empty),
                Value = ReadCurrentValue(element),
                AutomationId = Read(() => element.Current.AutomationId, string.Empty),
                ClassName = Read(() => element.Current.ClassName, string.Empty),
                FrameworkId = Read(() => element.Current.FrameworkId, string.Empty),
                ControlType = Read(() => element.Current.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty), string.Empty),
                Bounds = new NodeBounds
                {
                    X = FiniteOrZero(rectangle.X),
                    Y = FiniteOrZero(rectangle.Y),
                    Width = PositiveFiniteOrZero(rectangle.Width),
                    Height = PositiveFiniteOrZero(rectangle.Height)
                },
                Enabled = Read(() => element.Current.IsEnabled, false),
                Offscreen = Read(() => element.Current.IsOffscreen, true),
                Focusable = Read(() => element.Current.IsKeyboardFocusable, false)
            };

            AddPattern(element, InvokePattern.Pattern, "Invoke", node.Patterns);
            AddPattern(element, ValuePattern.Pattern, "Value", node.Patterns);
            AddPattern(element, TextPattern.Pattern, "Text", node.Patterns);
            AddPattern(element, TogglePattern.Pattern, "Toggle", node.Patterns);
            AddPattern(element, SelectionItemPattern.Pattern, "SelectionItem", node.Patterns);
            AddPattern(element, ExpandCollapsePattern.Pattern, "ExpandCollapse", node.Patterns);
            AddPattern(element, ScrollItemPattern.Pattern, "ScrollItem", node.Patterns);
            return node;
        }

        private static void AddPattern(
            AutomationElement element,
            AutomationPattern pattern,
            string name,
            ICollection<string> patterns)
        {
            object patternObject;
            try
            {
                if (element.TryGetCurrentPattern(pattern, out patternObject))
                {
                    patterns.Add(name);
                }
            }
            catch (ElementNotAvailableException)
            {
            }
        }

        private static double ScoreCandidate(UiaNode node, NodeBounds windowBounds)
        {
            return ControlResolver.ScoreNode(node, windowBounds);
        }

        private static string ReadCurrentValue(AutomationElement element)
        {
            object patternObject;
            try
            {
                return element.TryGetCurrentPattern(ValuePattern.Pattern, out patternObject)
                    ? ((ValuePattern)patternObject).Current.Value ?? string.Empty
                    : string.Empty;
            }
            catch (ElementNotAvailableException)
            {
                return string.Empty;
            }
        }

        private static LocatorBundle BuildLocator(
            AutomationElement selected,
            UiaNode target,
            AutomationElement root,
            ClickObservation observation)
        {
            var locator = new LocatorBundle
            {
                AutomationId = EmptyToNull(target.AutomationId),
                Name = EmptyToNull(target.Name),
                ControlType = EmptyToNull(target.ControlType),
                ClassName = StableClassToken(target.ClassName),
                FallbackWindowPoint = new TracePoint(
                    observation.X - observation.WindowRectangle.Left,
                    observation.Y - observation.WindowRectangle.Top)
            };

            var current = selected;
            for (var depth = 0; current != null && depth < 7; depth++)
            {
                var segmentNode = ReadNode(current, 0, -1, depth);
                locator.AncestorPath.Add(new LocatorSegment
                {
                    Name = EmptyToNull(segmentNode.Name),
                    AutomationId = EmptyToNull(segmentNode.AutomationId),
                    ControlType = EmptyToNull(segmentNode.ControlType),
                    ClassName = StableClassToken(segmentNode.ClassName)
                });
                if (Automation.Compare(current, root)) break;
                try
                {
                    current = TreeWalker.RawViewWalker.GetParent(current);
                }
                catch (ElementNotAvailableException)
                {
                    break;
                }
            }
            locator.AncestorPath.Reverse();
            return locator;
        }

        private static bool Contains(NodeBounds bounds, int x, int y)
        {
            return bounds.Width > 0 && bounds.Height > 0 &&
                   x >= bounds.X && x <= bounds.X + bounds.Width &&
                   y >= bounds.Y && y <= bounds.Y + bounds.Height;
        }

        private static bool IsInsideTransientMenu(UiaNode node, IDictionary<int, UiaNode> nodesByIndex)
        {
            var current = node;
            for (var depth = 0; current != null && depth < 12; depth++)
            {
                if (ContainsMenuToken(current.AutomationId) || ContainsMenuToken(current.ClassName) ||
                    string.Equals(current.ControlType, "MenuItem", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                UiaNode parent;
                current = current.ParentIndex >= 0 && nodesByIndex.TryGetValue(current.ParentIndex, out parent)
                    ? parent
                    : null;
            }
            return false;
        }

        private static bool ContainsMenuToken(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                value.IndexOf("menu", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsDirectMenuTarget(UiaNode node)
        {
            if (string.Equals(node.ControlType, "Button", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(node.ControlType, "MenuItem", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(node.ControlType, "ListItem", StringComparison.OrdinalIgnoreCase)) return true;
            return !string.IsNullOrWhiteSpace(node.Name) &&
                !string.Equals(node.ControlType, "Group", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsMenuRow(UiaNode node)
        {
            if (string.Equals(node.ControlType, "ListItem", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(node.ControlType, "MenuItem", StringComparison.OrdinalIgnoreCase)) return true;
            var className = node.ClassName ?? string.Empty;
            return className.IndexOf("menu", StringComparison.OrdinalIgnoreCase) >= 0 &&
                (className.IndexOf("item", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 className.IndexOf("row", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static UiaNode FindNamedDescendant(
            UiaNode ancestor,
            IEnumerable<UiaNode> nodes,
            IDictionary<int, UiaNode> nodesByIndex)
        {
            if (ancestor == null) return null;
            return nodes
                .Where(node => !string.IsNullOrWhiteSpace(node.Name) && IsDescendantOf(node, ancestor, nodesByIndex))
                .OrderBy(node => node.Depth - ancestor.Depth)
                .ThenBy(node => node.Bounds.Width * node.Bounds.Height)
                .FirstOrDefault();
        }

        private static bool IsDescendantOf(
            UiaNode node,
            UiaNode ancestor,
            IDictionary<int, UiaNode> nodesByIndex)
        {
            var current = node;
            for (var depth = 0; current != null && depth < 12; depth++)
            {
                if (current.ParentIndex == ancestor.Index) return true;
                UiaNode parent;
                current = current.ParentIndex >= 0 && nodesByIndex.TryGetValue(current.ParentIndex, out parent)
                    ? parent
                    : null;
            }
            return false;
        }

        private static double TransientTargetScore(UiaNode node)
        {
            var score = 0.0;
            if (!string.IsNullOrWhiteSpace(node.Name)) score += 220;
            if (!string.IsNullOrWhiteSpace(node.AutomationId)) score += 140;
            if (string.Equals(node.ControlType, "MenuItem", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(node.ControlType, "ListItem", StringComparison.OrdinalIgnoreCase)) score += 120;
            if (node.Patterns != null && node.Patterns.Contains("Invoke")) score += 80;
            if (string.Equals(node.ControlType, "Text", StringComparison.OrdinalIgnoreCase)) score += 30;
            score += Math.Min(60, node.Depth * 2);
            return score;
        }

        internal static LocatorBundle BuildLocatorFromSnapshot(
            UiaNode target,
            IDictionary<int, UiaNode> nodesByIndex,
            ClickObservation observation)
        {
            var locator = new LocatorBundle
            {
                AutomationId = EmptyToNull(target.AutomationId),
                Name = EmptyToNull(target.Name),
                ControlType = EmptyToNull(target.ControlType),
                ClassName = StableClassToken(target.ClassName),
                FallbackWindowPoint = new TracePoint(
                    observation.X - observation.WindowRectangle.Left,
                    observation.Y - observation.WindowRectangle.Top)
            };
            var path = new List<UiaNode>();
            var current = target;
            for (var depth = 0; current != null && depth < 12; depth++)
            {
                path.Add(current);
                UiaNode parent;
                current = current.ParentIndex >= 0 && nodesByIndex.TryGetValue(current.ParentIndex, out parent)
                    ? parent
                    : null;
            }
            path.Reverse();
            foreach (var node in path)
            {
                locator.AncestorPath.Add(new LocatorSegment
                {
                    Name = EmptyToNull(node.Name),
                    AutomationId = EmptyToNull(node.AutomationId),
                    ControlType = EmptyToNull(node.ControlType),
                    ClassName = StableClassToken(node.ClassName)
                });
            }
            return locator;
        }

        private static string StableClassToken(string className)
        {
            if (string.IsNullOrWhiteSpace(className)) return null;
            var token = className.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(token)) return null;
            var moduleSuffix = token.LastIndexOf("__", StringComparison.Ordinal);
            if (moduleSuffix <= 0) return token;
            var suffix = token.Substring(moduleSuffix + 2);
            // CSS-module hashes contain digits/uppercase characters (for
            // example __kxzx2 or __2yYvS). Lowercase words such as __header,
            // __think and __content are semantic BEM structure and must remain.
            var looksHashed = suffix.Any(character => char.IsDigit(character) || char.IsUpper(character));
            return looksHashed ? token.Substring(0, moduleSuffix) : token;
        }

        private static string EmptyToNull(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static double FiniteOrZero(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) ? 0 : value;
        }

        private static double PositiveFiniteOrZero(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) || value < 0 ? 0 : value;
        }

        private static T Read<T>(Func<T> reader, T fallback)
        {
            try
            {
                return reader();
            }
            catch (Exception error)
            {
                if (error is ElementNotAvailableException || error is InvalidOperationException)
                {
                    return fallback;
                }
                throw;
            }
        }

        private sealed class UiaCandidate
        {
            internal UiaNode Node;
        }
    }
}
