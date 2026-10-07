using System.Collections.Generic;
using System.Runtime.Serialization;

namespace YuanbaoRecorder.Agent
{
    [DataContract]
    internal sealed class ReplayTaskEnvelope
    {
        [DataMember(Name = "task", Order = 1, EmitDefaultValue = false)]
        internal ReplayTask Task;
    }

    [DataContract]
    internal sealed class ReplayTask
    {
        [DataMember(Name = "id", Order = 1)]
        internal string Id;

        [DataMember(Name = "case_id", Order = 2)]
        internal string CaseId;

        [DataMember(Name = "case_name", Order = 3)]
        internal string CaseName;

        [DataMember(Name = "target", Order = 4)]
        internal ReplayTarget Target;

        [DataMember(Name = "actions", Order = 5)]
        internal List<ReplayAction> Actions = new List<ReplayAction>();

        [DataMember(Name = "assertions", Order = 6)]
        internal List<ReplayAssertion> Assertions = new List<ReplayAssertion>();
    }

    [DataContract]
    internal sealed class ReplayTarget
    {
        [DataMember(Name = "process_name", Order = 1)]
        internal string ProcessName;

        [DataMember(Name = "window_title", Order = 2)]
        internal string WindowTitle;

        [DataMember(Name = "restart_before_replay", Order = 3)]
        internal bool RestartBeforeReplay;

        [DataMember(Name = "recorded_window_size", Order = 4, EmitDefaultValue = false)]
        internal ReplayWindowSize RecordedWindowSize;
    }

    [DataContract]
    internal sealed class ReplayWindowSize
    {
        [DataMember(Name = "width", Order = 1)]
        internal int Width;

        [DataMember(Name = "height", Order = 2)]
        internal int Height;
    }

    [DataContract]
    internal sealed class ReplayAction
    {
        [DataMember(Name = "id", Order = 1)]
        internal string Id;

        [DataMember(Name = "type", Order = 2)]
        internal string Type;

        [DataMember(Name = "value", Order = 3, EmitDefaultValue = false)]
        internal string Value;

        [DataMember(Name = "coordinate", Order = 4, EmitDefaultValue = false)]
        internal TracePoint Coordinate;

        [DataMember(Name = "locator", Order = 5, EmitDefaultValue = false)]
        internal LocatorBundle Locator;

        [DataMember(Name = "delay_after_ms", Order = 6)]
        internal int DelayAfterMilliseconds;

        [DataMember(Name = "end_coordinate", Order = 7, EmitDefaultValue = false)]
        internal TracePoint EndCoordinate;

        [DataMember(Name = "wheel_delta", Order = 8, EmitDefaultValue = false)]
        internal int WheelDelta;

        [DataMember(Name = "duration_ms", Order = 9, EmitDefaultValue = false)]
        internal int DurationMilliseconds;

        [DataMember(Name = "condition_type", Order = 10, EmitDefaultValue = false)]
        internal string ConditionType;

        [DataMember(Name = "skip_if_false", Order = 11, EmitDefaultValue = false)]
        internal int SkipIfFalse;

        [DataMember(Name = "target_relative_point", Order = 12, EmitDefaultValue = false)]
        internal ReplayRelativePoint TargetRelativePoint;

        [DataMember(Name = "effects", Order = 13, EmitDefaultValue = false)]
        internal List<ReplayEffect> Effects = new List<ReplayEffect>();

        [DataMember(Name = "hover_anchor_locator", Order = 14, EmitDefaultValue = false)]
        internal LocatorBundle HoverAnchorLocator;

        [DataMember(Name = "hover_anchor_match_mode", Order = 23, EmitDefaultValue = false)]
        internal string HoverAnchorMatchMode;

        [DataMember(Name = "true_step_count", Order = 15, EmitDefaultValue = false)]
        internal int TrueStepCount;

        [DataMember(Name = "false_step_count", Order = 16, EmitDefaultValue = false)]
        internal int FalseStepCount;

        [DataMember(Name = "timeout_ms", Order = 17, EmitDefaultValue = false)]
        internal int TimeoutMilliseconds;

        [DataMember(Name = "branch_id", Order = 18, EmitDefaultValue = false)]
        internal string BranchId;

        [DataMember(Name = "condition_match_mode", Order = 19, EmitDefaultValue = false)]
        internal string ConditionMatchMode;

        [DataMember(Name = "condition_scope_type", Order = 20, EmitDefaultValue = false)]
        internal string ConditionScopeType;

        [DataMember(Name = "condition_scope_start_name", Order = 21, EmitDefaultValue = false)]
        internal string ConditionScopeStartName;

        [DataMember(Name = "condition_scope_end_name", Order = 22, EmitDefaultValue = false)]
        internal string ConditionScopeEndName;

    }

    [DataContract]
    internal sealed class ReplayEffect
    {
        [DataMember(Name = "type", Order = 1)]
        internal string Type;

        [DataMember(Name = "locator", Order = 2, EmitDefaultValue = false)]
        internal LocatorBundle Locator;

        [DataMember(Name = "timeout_ms", Order = 3)]
        internal int TimeoutMilliseconds;
    }

    [DataContract]
    internal sealed class ReplayRelativePoint
    {
        [DataMember(Name = "x", Order = 1)]
        internal double X;

        [DataMember(Name = "y", Order = 2)]
        internal double Y;
    }

    [DataContract]
    internal sealed class ReplayStepResult
    {
        [DataMember(Name = "actionId", Order = 1)]
        internal string ActionId;

        [DataMember(Name = "index", Order = 2)]
        internal int Index;

        [DataMember(Name = "status", Order = 3)]
        internal string Status;

        [DataMember(Name = "locatorUsed", Order = 4, EmitDefaultValue = false)]
        internal string LocatorUsed;

        [DataMember(Name = "durationMs", Order = 5)]
        internal long DurationMilliseconds;

        [DataMember(Name = "error", Order = 6, EmitDefaultValue = false)]
        internal string Error;

        [DataMember(Name = "conditionMatched", Order = 7, EmitDefaultValue = false)]
        internal bool? ConditionMatched;

        [DataMember(Name = "assertionResults", Order = 7)]
        internal List<ReplayAssertionResult> AssertionResults = new List<ReplayAssertionResult>();

        [DataMember(Name = "executionMode", Order = 8, EmitDefaultValue = false)]
        internal string ExecutionMode;

        [DataMember(Name = "degraded", Order = 9, EmitDefaultValue = false)]
        internal bool Degraded;

        [DataMember(Name = "locatorDiagnostics", Order = 10, EmitDefaultValue = false)]
        internal string LocatorDiagnostics;

        [DataMember(Name = "outcome", Order = 11, EmitDefaultValue = false)]
        internal string Outcome;

        [DataMember(Name = "interactionAttempts", Order = 12, EmitDefaultValue = false)]
        internal List<string> InteractionAttempts = new List<string>();
    }

    [DataContract]
    internal sealed class ReplayAssertion
    {
        [DataMember(Name = "id", Order = 1)]
        internal string Id;

        [DataMember(Name = "after_action_id", Order = 2)]
        internal string AfterActionId;

        [DataMember(Name = "label", Order = 3)]
        internal string Label;

        [DataMember(Name = "type", Order = 4)]
        internal string Type;

        [DataMember(Name = "expected", Order = 5, EmitDefaultValue = false)]
        internal string Expected;

        [DataMember(Name = "property", Order = 6, EmitDefaultValue = false)]
        internal string Property;

        [DataMember(Name = "scope", Order = 7, EmitDefaultValue = false)]
        internal string Scope;

        [DataMember(Name = "scope_control_type", Order = 8, EmitDefaultValue = false)]
        internal string ScopeControlType;

        [DataMember(Name = "keywords", Order = 9, EmitDefaultValue = false)]
        internal List<string> Keywords;

        [DataMember(Name = "minimum_matches", Order = 10, EmitDefaultValue = false)]
        internal int MinimumMatches;

        [DataMember(Name = "descendant_control_type", Order = 11, EmitDefaultValue = false)]
        internal string DescendantControlType;

        [DataMember(Name = "count_operator", Order = 12, EmitDefaultValue = false)]
        internal string CountOperator;

        [DataMember(Name = "expected_count", Order = 13, EmitDefaultValue = false)]
        internal int ExpectedCount;

        [DataMember(Name = "expected_rows", Order = 14, EmitDefaultValue = false)]
        internal int ExpectedRows;

        [DataMember(Name = "expected_columns", Order = 15, EmitDefaultValue = false)]
        internal int ExpectedColumns;

        [DataMember(Name = "bounds_tolerance_px", Order = 16, EmitDefaultValue = false)]
        internal int BoundsTolerancePixels;

        [DataMember(Name = "timeout_ms", Order = 17)]
        internal int TimeoutMilliseconds;

        [DataMember(Name = "target", Order = 18, EmitDefaultValue = false)]
        internal ReplayAssertionTarget Target;
    }

    [DataContract]
    internal sealed class ReplayAssertionTarget
    {
        [DataMember(Name = "semantic_role", Order = 1, EmitDefaultValue = false)]
        internal string SemanticRole;

        [DataMember(Name = "locator", Order = 2, EmitDefaultValue = false)]
        internal LocatorBundle Locator;
    }

    [DataContract]
    internal sealed class ReplayAssertionResult
    {
        [DataMember(Name = "assertionId", Order = 1)]
        internal string AssertionId;

        [DataMember(Name = "label", Order = 2)]
        internal string Label;

        [DataMember(Name = "type", Order = 3)]
        internal string Type;

        [DataMember(Name = "status", Order = 4)]
        internal string Status;

        [DataMember(Name = "expected", Order = 5, EmitDefaultValue = false)]
        internal string Expected;

        [DataMember(Name = "actual", Order = 6, EmitDefaultValue = false)]
        internal string Actual;

        [DataMember(Name = "durationMs", Order = 7)]
        internal long DurationMilliseconds;

        [DataMember(Name = "error", Order = 8, EmitDefaultValue = false)]
        internal string Error;

        [DataMember(Name = "locatorUsed", Order = 9, EmitDefaultValue = false)]
        internal string LocatorUsed;

        [DataMember(Name = "locatorDiagnostics", Order = 10, EmitDefaultValue = false)]
        internal string LocatorDiagnostics;
    }

    [DataContract]
    internal sealed class ReplayProgressRequest
    {
        [DataMember(Name = "agentId", Order = 1)]
        internal string AgentId;

        [DataMember(Name = "currentStep", Order = 2)]
        internal int CurrentStep;

        [DataMember(Name = "result", Order = 3)]
        internal ReplayStepResult Result;
    }

    [DataContract]
    internal sealed class ReplayCompleteRequest
    {
        [DataMember(Name = "agentId", Order = 1)]
        internal string AgentId;

        [DataMember(Name = "success", Order = 2)]
        internal bool Success;

        [DataMember(Name = "completedSteps", Order = 3)]
        internal int CompletedSteps;

        [DataMember(Name = "error", Order = 4, EmitDefaultValue = false)]
        internal string Error;

        [DataMember(Name = "results", Order = 5)]
        internal List<ReplayStepResult> Results = new List<ReplayStepResult>();
    }

    [DataContract]
    internal sealed class AgentHeartbeatRequest
    {
        [DataMember(Name = "status", Order = 1)]
        internal string Status;

        [DataMember(Name = "version", Order = 2)]
        internal string Version;

        [DataMember(Name = "currentTaskId", Order = 3, EmitDefaultValue = false)]
        internal string CurrentTaskId;

        [DataMember(Name = "capabilities", Order = 4)]
        internal List<string> Capabilities = new List<string>();

        [DataMember(Name = "buildConfiguration", Order = 5)]
        internal string BuildConfiguration;

        [DataMember(Name = "gitCommit", Order = 6)]
        internal string GitCommit;

        [DataMember(Name = "executablePath", Order = 7)]
        internal string ExecutablePath;
    }

    [DataContract]
    internal sealed class EmptyRequest
    {
    }
}
