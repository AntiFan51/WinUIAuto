using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Threading;

namespace YuanbaoRecorder.Agent
{
    [DataContract]
    internal sealed class RawTrace
    {
        [DataMember(Name = "schema_version", Order = 1)]
        internal string SchemaVersion = "0.9-windows-causal-actions";

        [DataMember(Name = "session_id", Order = 2)]
        internal string SessionId;

        [DataMember(Name = "case_name", Order = 3)]
        internal string CaseName;

        [DataMember(Name = "target_window", Order = 4)]
        internal string TargetWindow;

        [DataMember(Name = "started_at", Order = 5)]
        internal string StartedAt;

        [DataMember(Name = "finished_at", Order = 6, EmitDefaultValue = false)]
        internal string FinishedAt;

        [DataMember(Name = "agent", Order = 7)]
        internal TraceAgentMetadata Agent;

        [DataMember(Name = "input_event_log", Order = 8)]
        internal string InputEventLog = "input-events.ndjson";

        [DataMember(Name = "actions", Order = 9)]
        internal List<TraceAction> Actions = new List<TraceAction>();

        [DataMember(Name = "assertions", Order = 10)]
        internal List<TraceAssertion> Assertions = new List<TraceAssertion>();
    }

    [DataContract]
    internal sealed class TraceAgentMetadata
    {
        [DataMember(Name = "version", Order = 1)]
        internal string Version;

        [DataMember(Name = "build_configuration", Order = 2)]
        internal string BuildConfiguration;

        [DataMember(Name = "git_commit", Order = 3)]
        internal string GitCommit;

        [DataMember(Name = "built_at", Order = 4)]
        internal string BuiltAt;

        [DataMember(Name = "executable_path", Order = 5)]
        internal string ExecutablePath;

        [DataMember(Name = "process_id", Order = 6)]
        internal int ProcessId;

        [DataMember(Name = "machine_name", Order = 7)]
        internal string MachineName;

        [DataMember(Name = "os_version", Order = 8)]
        internal string OsVersion;

        [DataMember(Name = "capture_strategy", Order = 9)]
        internal string CaptureStrategy;
    }

    [DataContract]
    internal sealed class TraceAssertion
    {
        [DataMember(Name = "id", Order = 1)]
        internal string Id;

        [DataMember(Name = "after_action_id", Order = 2)]
        internal string AfterActionId;

        [DataMember(Name = "label", Order = 3)]
        internal string Label;

        [DataMember(Name = "type", Order = 4)]
        internal string Type;

        [DataMember(Name = "assertion_mode", Order = 4, EmitDefaultValue = false)]
        internal string AssertionMode;

        [DataMember(Name = "expected", Order = 5, EmitDefaultValue = false)]
        internal string Expected;

        [DataMember(Name = "property", Order = 6, EmitDefaultValue = false)]
        internal string Property;

        [DataMember(Name = "scope", Order = 6, EmitDefaultValue = false)]
        internal string Scope;

        [DataMember(Name = "scope_control_type", Order = 7, EmitDefaultValue = false)]
        internal string ScopeControlType;

        [DataMember(Name = "keywords", Order = 8, EmitDefaultValue = false)]
        internal List<string> Keywords;

        [DataMember(Name = "minimum_matches", Order = 9, EmitDefaultValue = false)]
        internal int MinimumMatches;

        [DataMember(Name = "descendant_control_type", Order = 10, EmitDefaultValue = false)]
        internal string DescendantControlType;

        [DataMember(Name = "count_operator", Order = 11, EmitDefaultValue = false)]
        internal string CountOperator;

        [DataMember(Name = "expected_count", Order = 12, EmitDefaultValue = false)]
        internal int ExpectedCount;

        [DataMember(Name = "expected_rows", Order = 13, EmitDefaultValue = false)]
        internal int ExpectedRows;

        [DataMember(Name = "expected_columns", Order = 14, EmitDefaultValue = false)]
        internal int ExpectedColumns;

        [DataMember(Name = "bounds_tolerance_px", Order = 15, EmitDefaultValue = false)]
        internal int BoundsTolerancePixels;

        [DataMember(Name = "timeout_ms", Order = 16)]
        internal int TimeoutMilliseconds;

        [DataMember(Name = "target", Order = 17)]
        internal TraceAssertionTarget Target;

        [DataMember(Name = "source", Order = 18)]
        internal string Source = "manual_during_recording";

        [DataMember(Name = "evidence", Order = 19)]
        internal TraceAssertionEvidence Evidence;
    }

    [DataContract]
    internal sealed class TraceAssertionTarget
    {
        [DataMember(Name = "semantic_role", Order = 1)]
        internal string SemanticRole;

        [DataMember(Name = "locator_bundle", Order = 2)]
        internal LocatorBundle LocatorBundle;

        [DataMember(Name = "node", Order = 3)]
        internal UiaNode Node;
    }

    [DataContract]
    internal sealed class TraceAssertionEvidence
    {
        [DataMember(Name = "screenshot", Order = 1)]
        internal string Screenshot;

        [DataMember(Name = "uia_snapshot", Order = 2)]
        internal string UiaSnapshot;

        [DataMember(Name = "captured_at", Order = 3)]
        internal string CapturedAt;

        [DataMember(Name = "capture_diagnostics", Order = 4, EmitDefaultValue = false)]
        internal TraceCaptureDiagnostics CaptureDiagnostics;
    }

    internal sealed class AssertionCaptureContext
    {
        internal string Id;
        internal string SuggestedAfterActionId;
        internal UiaNode HitTarget;
        internal UiaNode Target;
        internal string TargetDecision;
        internal LocatorBundle Locator;
        internal string Screenshot;
        internal string UiaSnapshot;
        internal string CapturedAt;
        internal TraceCaptureDiagnostics CaptureDiagnostics;
        internal string SuggestedScopeControlType;
        internal TracePoint ScreenPoint;
        internal TracePoint WindowPoint;
        internal string WindowTitle;
        internal UiaSnapshot Snapshot;
    }

    internal sealed class AssertionDraft
    {
        internal string Type;
        internal string AssertionMode;
        internal string Expected;
        internal string Property;
        internal string Scope;
        internal string ScopeControlType;
        internal List<string> Keywords = new List<string>();
        internal int MinimumMatches;
        internal string DescendantControlType;
        internal string CountOperator;
        internal int ExpectedCount;
        internal int ExpectedRows;
        internal int ExpectedColumns;
        internal int BoundsTolerancePixels;
        internal int TimeoutMilliseconds;
    }

    internal sealed class FlowControlDraft
    {
        internal string Type;
        internal string ConditionType;
        internal int TimeoutMilliseconds;
    }

    [DataContract]
    internal sealed class TraceAction
    {
        [DataMember(Name = "id", Order = 1)]
        internal string Id;

        [DataMember(Name = "input_event_id", Order = 2, EmitDefaultValue = false)]
        internal string InputEventId;

        [DataMember(Name = "type", Order = 3)]
        internal string Type;

        [DataMember(Name = "timestamp", Order = 4)]
        internal string Timestamp;

        [DataMember(Name = "screen_point", Order = 5)]
        internal TracePoint ScreenPoint;

        [DataMember(Name = "window_point", Order = 6)]
        internal TracePoint WindowPoint;

        [DataMember(Name = "window_title", Order = 7)]
        internal string WindowTitle;

        [DataMember(Name = "target", Order = 8, EmitDefaultValue = false)]
        internal UiaNode Target;

        [DataMember(Name = "locator", Order = 9, EmitDefaultValue = false)]
        internal LocatorBundle Locator;

        [DataMember(Name = "screenshot", Order = 10)]
        internal string Screenshot;

        [DataMember(Name = "uia_snapshot", Order = 11)]
        internal string UiaSnapshot;

        [DataMember(Name = "capture_error", Order = 12, EmitDefaultValue = false)]
        internal string CaptureError;

        [DataMember(Name = "text", Order = 13, EmitDefaultValue = false)]
        internal string Text;

        [DataMember(Name = "derivation", Order = 14, EmitDefaultValue = false)]
        internal string Derivation;

        [DataMember(Name = "confidence", Order = 15, EmitDefaultValue = false)]
        internal double Confidence;

        [DataMember(Name = "needs_review", Order = 16, EmitDefaultValue = false)]
        internal bool NeedsReview;

        [DataMember(Name = "review_reason", Order = 17, EmitDefaultValue = false)]
        internal string ReviewReason;

        [DataMember(Name = "evidence_before_snapshot", Order = 18, EmitDefaultValue = false)]
        internal string EvidenceBeforeSnapshot;

        [DataMember(Name = "evidence_after_snapshot", Order = 19, EmitDefaultValue = false)]
        internal string EvidenceAfterSnapshot;

        [DataMember(Name = "capture_diagnostics", Order = 20, EmitDefaultValue = false)]
        internal TraceCaptureDiagnostics CaptureDiagnostics;

        [DataMember(Name = "end_window_point", Order = 21, EmitDefaultValue = false)]
        internal TracePoint EndWindowPoint;

        [DataMember(Name = "wheel_delta", Order = 22, EmitDefaultValue = false)]
        internal int WheelDelta;

        [DataMember(Name = "duration_ms", Order = 23, EmitDefaultValue = false)]
        internal int DurationMilliseconds;

        [DataMember(Name = "key", Order = 24, EmitDefaultValue = false)]
        internal string Key;

        [DataMember(Name = "effects", Order = 25, EmitDefaultValue = false)]
        internal List<TraceActionEffect> Effects = new List<TraceActionEffect>();

        [DataMember(Name = "condition_type", Order = 26, EmitDefaultValue = false)]
        internal string ConditionType;

        [DataMember(Name = "true_step_count", Order = 27, EmitDefaultValue = false)]
        internal int TrueStepCount;

        [DataMember(Name = "false_step_count", Order = 28, EmitDefaultValue = false)]
        internal int FalseStepCount;

        [DataMember(Name = "timeout_ms", Order = 29, EmitDefaultValue = false)]
        internal int TimeoutMilliseconds;

        [DataMember(Name = "branch_id", Order = 30, EmitDefaultValue = false)]
        internal string BranchId;

        [DataMember(Name = "branch_path", Order = 31, EmitDefaultValue = false)]
        internal string BranchPath;

        [DataMember(Name = "condition_match_mode", Order = 32, EmitDefaultValue = false)]
        internal string ConditionMatchMode;

        [DataMember(Name = "condition_scope_type", Order = 33, EmitDefaultValue = false)]
        internal string ConditionScopeType;

        [DataMember(Name = "condition_scope_start_name", Order = 34, EmitDefaultValue = false)]
        internal string ConditionScopeStartName;

        [DataMember(Name = "condition_scope_end_name", Order = 35, EmitDefaultValue = false)]
        internal string ConditionScopeEndName;

    }

    [DataContract]
    internal sealed class TraceActionEffect
    {
        [DataMember(Name = "type", Order = 1)]
        internal string Type;

        [DataMember(Name = "target", Order = 2, EmitDefaultValue = false)]
        internal UiaNode Target;

        [DataMember(Name = "locator", Order = 3, EmitDefaultValue = false)]
        internal LocatorBundle Locator;

        [DataMember(Name = "source", Order = 4, EmitDefaultValue = false)]
        internal string Source;
    }

    [DataContract]
    internal sealed class TraceCaptureDiagnostics
    {
        [DataMember(Name = "mouse_event", Order = 1)]
        internal string MouseEvent;

        [DataMember(Name = "queue_delay_ms", Order = 2)]
        internal double QueueDelayMilliseconds;

        [DataMember(Name = "selection_source", Order = 3)]
        internal string SelectionSource;

        [DataMember(Name = "transient_decision", Order = 4, EmitDefaultValue = false)]
        internal string TransientDecision;

        [DataMember(Name = "transient_candidate_count", Order = 5)]
        internal int TransientCandidateCount;

        [DataMember(Name = "immediate_target", Order = 6, EmitDefaultValue = false)]
        internal TraceTargetDiagnostic ImmediateTarget;

        [DataMember(Name = "hit_leaf_target", Order = 16, EmitDefaultValue = false)]
        internal TraceTargetDiagnostic HitLeafTarget;

        [DataMember(Name = "transient_target", Order = 7, EmitDefaultValue = false)]
        internal TraceTargetDiagnostic TransientTarget;

        [DataMember(Name = "after_snapshot_target", Order = 8, EmitDefaultValue = false)]
        internal TraceTargetDiagnostic AfterSnapshotTarget;

        [DataMember(Name = "immediate_capture_error", Order = 9, EmitDefaultValue = false)]
        internal string ImmediateCaptureError;

        [DataMember(Name = "transient_snapshot", Order = 10, EmitDefaultValue = false)]
        internal string TransientSnapshot;

        [DataMember(Name = "transient_revision", Order = 11, EmitDefaultValue = false)]
        internal int TransientRevision;

        [DataMember(Name = "processing_duration_ms", Order = 12, EmitDefaultValue = false)]
        internal double ProcessingDurationMilliseconds;

        [DataMember(Name = "screenshot_duration_ms", Order = 13, EmitDefaultValue = false)]
        internal double ScreenshotDurationMilliseconds;

        [DataMember(Name = "uia_duration_ms", Order = 14, EmitDefaultValue = false)]
        internal double UiaDurationMilliseconds;

        [DataMember(Name = "uia_read_mode", Order = 15, EmitDefaultValue = false)]
        internal string UiaReadMode;
    }

    [DataContract]
    internal sealed class TraceTargetDiagnostic
    {
        [DataMember(Name = "name", Order = 1, EmitDefaultValue = false)]
        internal string Name;

        [DataMember(Name = "automation_id", Order = 2, EmitDefaultValue = false)]
        internal string AutomationId;

        [DataMember(Name = "control_type", Order = 3, EmitDefaultValue = false)]
        internal string ControlType;

        [DataMember(Name = "class_name", Order = 4, EmitDefaultValue = false)]
        internal string ClassName;

        [DataMember(Name = "bounds", Order = 5, EmitDefaultValue = false)]
        internal NodeBounds Bounds;

        internal static TraceTargetDiagnostic FromNode(UiaNode node)
        {
            if (node == null) return null;
            return new TraceTargetDiagnostic
            {
                Name = node.Name,
                AutomationId = node.AutomationId,
                ControlType = node.ControlType,
                ClassName = node.ClassName,
                Bounds = node.Bounds
            };
        }
    }

    [DataContract]
    internal sealed class TracePoint
    {
        internal TracePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        [DataMember(Name = "x", Order = 1)]
        internal int X;

        [DataMember(Name = "y", Order = 2)]
        internal int Y;
    }

    [DataContract]
    internal sealed class UiaSnapshot
    {
        [DataMember(Name = "captured_at", Order = 1)]
        internal string CapturedAt;

        [DataMember(Name = "window_title", Order = 2)]
        internal string WindowTitle;

        [DataMember(Name = "node_count", Order = 3)]
        internal int NodeCount;

        [DataMember(Name = "truncated", Order = 4)]
        internal bool Truncated;

        [DataMember(Name = "nodes", Order = 5)]
        internal List<UiaNode> Nodes = new List<UiaNode>();
    }

    [DataContract]
    internal sealed class UiaNode
    {
        [DataMember(Name = "index", Order = 1)]
        internal int Index;

        [DataMember(Name = "parent_index", Order = 2)]
        internal int ParentIndex;

        [DataMember(Name = "depth", Order = 3)]
        internal int Depth;

        [DataMember(Name = "runtime_id", Order = 4, EmitDefaultValue = false)]
        internal int[] RuntimeId;

        [DataMember(Name = "name", Order = 5)]
        internal string Name;

        // UIA Name is the accessible title/placeholder. It is deliberately
        // separate from Value, which is the current content of an input.
        [DataMember(Name = "value", Order = 6, EmitDefaultValue = false)]
        internal string Value;

        [DataMember(Name = "automation_id", Order = 7)]
        internal string AutomationId;

        [DataMember(Name = "class_name", Order = 7)]
        internal string ClassName;

        [DataMember(Name = "framework_id", Order = 8)]
        internal string FrameworkId;

        [DataMember(Name = "control_type", Order = 9)]
        internal string ControlType;

        [DataMember(Name = "bounds", Order = 10)]
        internal NodeBounds Bounds;

        [DataMember(Name = "enabled", Order = 11)]
        internal bool Enabled;

        [DataMember(Name = "offscreen", Order = 12)]
        internal bool Offscreen;

        [DataMember(Name = "focusable", Order = 13)]
        internal bool Focusable;

        [DataMember(Name = "patterns", Order = 14)]
        internal List<string> Patterns = new List<string>();

        [IgnoreDataMember]
        internal double CandidateScore;
    }

    [DataContract]
    internal sealed class NodeBounds
    {
        [DataMember(Name = "x", Order = 1)]
        internal double X;

        [DataMember(Name = "y", Order = 2)]
        internal double Y;

        [DataMember(Name = "width", Order = 3)]
        internal double Width;

        [DataMember(Name = "height", Order = 4)]
        internal double Height;
    }

    [DataContract]
    internal sealed class LocatorBundle
    {
        [DataMember(Name = "automation_id", Order = 1, EmitDefaultValue = false)]
        internal string AutomationId;

        [DataMember(Name = "name", Order = 2, EmitDefaultValue = false)]
        internal string Name;

        [DataMember(Name = "control_type", Order = 3, EmitDefaultValue = false)]
        internal string ControlType;

        [DataMember(Name = "class_name", Order = 4, EmitDefaultValue = false)]
        internal string ClassName;

        [DataMember(Name = "ancestor_path", Order = 5)]
        internal List<LocatorSegment> AncestorPath = new List<LocatorSegment>();

        [DataMember(Name = "fallback_window_point", Order = 6)]
        internal TracePoint FallbackWindowPoint;

        // Optional visual section boundaries for controls that share the same
        // UIA class in different sidebar collections (for example project
        // groups and recent chats).  Replay treats a supplied scope as strict.
        [DataMember(Name = "section_start_name", Order = 7, EmitDefaultValue = false)]
        internal string SectionStartName;

        [DataMember(Name = "section_end_name", Order = 8, EmitDefaultValue = false)]
        internal string SectionEndName;

        // Identifies an otherwise unnamed clickable container by stable text
        // inside it, while keeping the container as the physical click target.
        [DataMember(Name = "descendant_name", Order = 9, EmitDefaultValue = false)]
        internal string DescendantName;

        [DataMember(Name = "descendant_control_type", Order = 10, EmitDefaultValue = false)]
        internal string DescendantControlType;
    }

    [DataContract]
    internal sealed class LocatorSegment
    {
        [DataMember(Name = "name", Order = 1, EmitDefaultValue = false)]
        internal string Name;

        [DataMember(Name = "automation_id", Order = 2, EmitDefaultValue = false)]
        internal string AutomationId;

        [DataMember(Name = "control_type", Order = 3, EmitDefaultValue = false)]
        internal string ControlType;

        [DataMember(Name = "class_name", Order = 4, EmitDefaultValue = false)]
        internal string ClassName;
    }

    internal sealed class ClickObservation
    {
        internal string InputEventId;
        internal string ActionType;
        internal UiaNode ImmediateTarget;
        internal LocatorBundle ImmediateLocator;
        internal UiaNode HitLeafTarget;
        internal LocatorBundle HitLeafLocator;
        internal UiaNode EventTransientTarget;
        internal LocatorBundle EventTransientLocator;
        internal UiaSnapshot EventTransientSnapshot;
        internal int EventTransientRevision;
        internal string EventTransientDecision;
        internal int EventTransientCandidateCount;
        internal string ImmediateCaptureError;
        internal DateTime TimestampUtc;
        internal int X;
        internal int Y;
        internal IntPtr WindowHandle;
        internal string WindowTitle;
        internal NativeMethods.WindowRectangle WindowRectangle;
        internal int EndX;
        internal int EndY;
        internal int WheelDelta;
        internal int DurationMilliseconds;
        internal FocusedEditableCapture CommittedInput;
        internal string InputCommitSource;
        internal FocusedEditableCapture StartedInput;
        internal bool EvidenceOnly;
        internal bool CommitInputByKeyboard;
        internal ManualResetEventSlim Processed;
        internal string Key;
    }

    internal sealed class FocusedEditableCapture
    {
        internal string Text;
        internal UiaNode Target;
        internal LocatorBundle Locator;
        internal NodeBounds Bounds;
    }
}
