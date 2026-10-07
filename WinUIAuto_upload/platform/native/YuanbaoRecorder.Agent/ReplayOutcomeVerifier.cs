using System;
using System.Linq;
using System.Threading;
using System.Windows.Automation;

namespace YuanbaoRecorder.Agent
{
    internal sealed class ReplayOutcomeProbe
    {
        internal ReplayEffect Effect;
        internal string BaselineDiagnostics;
    }

    internal sealed class ReplayOutcomeResult
    {
        internal bool Verified;
        internal string Description;
        internal string Diagnostics;
    }

    internal sealed class ReplayOutcomeVerifier
    {
        private readonly ReplayLocatorEngine locatorEngine;

        internal ReplayOutcomeVerifier(ReplayLocatorEngine locatorEngine)
        {
            this.locatorEngine = locatorEngine;
        }

        internal ReplayOutcomeProbe CaptureBaseline(
            AutomationElement root,
            string processName,
            ReplayAction currentAction,
            CancellationToken cancellationToken)
        {
            var effect = currentAction == null || currentAction.Effects == null
                ? null
                : currentAction.Effects.FirstOrDefault(item =>
                    string.Equals(item.Type, "target_appeared", StringComparison.Ordinal) && item.Locator != null);
            var probe = new ReplayOutcomeProbe { Effect = effect };
            if (effect == null) return probe;
            var baseline = locatorEngine.FindWithWait(root, processName, effect.Locator, 0, cancellationToken);
            probe.BaselineDiagnostics = baseline.Diagnostics;
            return probe;
        }

        internal ReplayOutcomeResult VerifyTransition(
            AutomationElement root,
            string processName,
            ReplayOutcomeProbe probe,
            int timeoutMilliseconds,
            CancellationToken cancellationToken)
        {
            if (probe == null || probe.Effect == null)
            {
                return new ReplayOutcomeResult
                {
                    Verified = true,
                    Description = "no_explicit_effect",
                    Diagnostics = probe == null ? null : probe.BaselineDiagnostics
                };
            }

            var result = locatorEngine.FindWithWait(
                root,
                processName,
                probe.Effect.Locator,
                probe.Effect.TimeoutMilliseconds > 0 ? probe.Effect.TimeoutMilliseconds : timeoutMilliseconds,
                cancellationToken);
            return new ReplayOutcomeResult
            {
                Verified = result.Found,
                Description = result.Found
                    ? "effect_satisfied:target_appeared"
                    : "effect_missing:target_appeared",
                Diagnostics = result.Diagnostics
            };
        }
    }
}
