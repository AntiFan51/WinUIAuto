using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace YuanbaoRecorder.Agent
{
    internal sealed class MessageDiffResult
    {
        internal string Text;
        internal bool NeedsReview;
        internal string Reason;
        internal int ContainerIndex = -1;
        internal int TextNodeIndex = -1;
    }

    internal sealed class YuanbaoMessageDiffService
    {
        private const string HumanMessageClassMarker = "agent-chat__list__item--human";

        internal MessageDiffResult FindCommittedUserMessage(UiaSnapshot before, UiaSnapshot after)
        {
            if (before == null || after == null)
            {
                return Review("发送前后控件树不完整");
            }

            var previousMessages = CountMessages(ReadHumanMessages(before));
            var newMessages = new List<MessageCandidate>();
            foreach (var message in ReadHumanMessages(after))
            {
                int count;
                if (previousMessages.TryGetValue(message.Text, out count) && count > 0)
                {
                    previousMessages[message.Text] = count - 1;
                }
                else
                {
                    newMessages.Add(message);
                }
            }

            if (newMessages.Count == 0) return Review("未发现新增用户消息");
            if (newMessages.Count > 1) return Review("发现多条新增用户消息，无法唯一确定本次输入");

            var selected = newMessages[0];
            return new MessageDiffResult
            {
                Text = selected.Text,
                NeedsReview = false,
                Reason = "由发送前后用户消息节点差分获得",
                ContainerIndex = selected.ContainerIndex,
                TextNodeIndex = selected.TextNodeIndex
            };
        }

        private static IList<MessageCandidate> ReadHumanMessages(UiaSnapshot snapshot)
        {
            var result = new List<MessageCandidate>();
            if (snapshot.Nodes == null || snapshot.Nodes.Count == 0) return result;

            var children = BuildChildren(snapshot.Nodes);
            foreach (var node in snapshot.Nodes)
            {
                if (!ContainsClassMarker(node.ClassName, HumanMessageClassMarker)) continue;

                var textNodes = new List<UiaNode>();
                CollectLeafTextNodes(node.Index, snapshot.Nodes, children, textNodes);
                var text = JoinText(textNodes);
                if (string.IsNullOrWhiteSpace(text)) continue;

                result.Add(new MessageCandidate
                {
                    Text = text,
                    ContainerIndex = node.Index,
                    TextNodeIndex = textNodes.Count == 0 ? -1 : textNodes[0].Index
                });
            }
            return result;
        }

        private static IDictionary<int, IList<int>> BuildChildren(IList<UiaNode> nodes)
        {
            var children = new Dictionary<int, IList<int>>();
            foreach (var node in nodes)
            {
                IList<int> indexes;
                if (!children.TryGetValue(node.ParentIndex, out indexes))
                {
                    indexes = new List<int>();
                    children[node.ParentIndex] = indexes;
                }
                indexes.Add(node.Index);
            }
            return children;
        }

        private static void CollectLeafTextNodes(
            int nodeIndex,
            IList<UiaNode> nodes,
            IDictionary<int, IList<int>> children,
            ICollection<UiaNode> result)
        {
            IList<int> childIndexes;
            if (!children.TryGetValue(nodeIndex, out childIndexes)) return;

            foreach (var childIndex in childIndexes)
            {
                if (childIndex < 0 || childIndex >= nodes.Count) continue;
                var child = nodes[childIndex];
                IList<int> grandchildIndexes;
                var hasNamedTextDescendant = children.TryGetValue(child.Index, out grandchildIndexes) &&
                    HasNamedTextDescendant(grandchildIndexes, nodes, children);

                if (IsTextNode(child) && !string.IsNullOrWhiteSpace(child.Name) && !hasNamedTextDescendant)
                {
                    result.Add(child);
                }
                CollectLeafTextNodes(child.Index, nodes, children, result);
            }
        }

        private static bool HasNamedTextDescendant(
            IEnumerable<int> indexes,
            IList<UiaNode> nodes,
            IDictionary<int, IList<int>> children)
        {
            foreach (var index in indexes)
            {
                if (index < 0 || index >= nodes.Count) continue;
                var node = nodes[index];
                if (IsTextNode(node) && !string.IsNullOrWhiteSpace(node.Name)) return true;

                IList<int> childIndexes;
                if (children.TryGetValue(index, out childIndexes) &&
                    HasNamedTextDescendant(childIndexes, nodes, children)) return true;
            }
            return false;
        }

        private static string JoinText(IEnumerable<UiaNode> nodes)
        {
            var builder = new StringBuilder();
            foreach (var node in nodes.OrderBy(item => item.Index))
            {
                var value = Normalize(node.Name);
                if (value.Length > 0) builder.Append(value);
            }
            return builder.ToString();
        }

        private static IDictionary<string, int> CountMessages(IEnumerable<MessageCandidate> messages)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var message in messages)
            {
                int count;
                counts.TryGetValue(message.Text, out count);
                counts[message.Text] = count + 1;
            }
            return counts;
        }

        private static bool IsTextNode(UiaNode node)
        {
            return string.Equals(node.ControlType, "Text", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsClassMarker(string className, string marker)
        {
            return !string.IsNullOrWhiteSpace(className) &&
                className.IndexOf(marker, StringComparison.Ordinal) >= 0;
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        private static MessageDiffResult Review(string reason)
        {
            return new MessageDiffResult { NeedsReview = true, Reason = reason };
        }

        private sealed class MessageCandidate
        {
            internal string Text;
            internal int ContainerIndex;
            internal int TextNodeIndex;
        }
    }
}
