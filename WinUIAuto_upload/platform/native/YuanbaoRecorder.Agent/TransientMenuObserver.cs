using System;
using System.Threading;
using System.Threading.Tasks;

namespace YuanbaoRecorder.Agent
{
    internal sealed class TransientMenuMatch
    {
        internal UiaNode Target;
        internal LocatorBundle Locator;
        internal UiaSnapshot Snapshot;
        internal int Revision;
        internal string Decision;
        internal int CandidateCount;
    }

    internal sealed class TransientMenuObserver : IDisposable
    {
        private readonly object sync = new object();
        private readonly UiaCaptureService captureService;
        private CancellationTokenSource cancellation = new CancellationTokenSource();
        private UiaSnapshot latestSnapshot;
        private DateTime latestCapturedAtUtc;
        private int revision;
        private int requestGeneration;

        internal TransientMenuObserver(UiaCaptureService captureService)
        {
            this.captureService = captureService;
        }

        internal void ObserveAfterPointerAction(IntPtr windowHandle)
        {
            var generation = Interlocked.Increment(ref requestGeneration);
            var token = cancellation.Token;
            Task.Run(async () =>
            {
                for (var attempt = 0; attempt < 16 && !token.IsCancellationRequested; attempt++)
                {
                    try
                    {
                        await Task.Delay(attempt == 0 ? 25 : 35, token);
                        var snapshot = captureService.CaptureTransientMenus(windowHandle);
                        if (snapshot == null || snapshot.Nodes.Count == 0) continue;
                        if (generation != Volatile.Read(ref requestGeneration)) return;
                        lock (sync)
                        {
                            latestSnapshot = snapshot;
                            latestCapturedAtUtc = DateTime.UtcNow;
                            revision++;
                        }
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch
                    {
                    }
                }
            }, token);
        }

        internal TransientMenuMatch ConsumeAtPoint(ClickObservation observation)
        {
            UiaSnapshot snapshot;
            int currentRevision;
            lock (sync)
            {
                // Nested menus can require hover plus scrolling before the final
                // selection. Keep the pre-dismissal snapshot long enough for a
                // deliberate user click; point containment still prevents an
                // unrelated click from consuming a menu target.
                if (latestSnapshot == null || DateTime.UtcNow - latestCapturedAtUtc > TimeSpan.FromSeconds(12))
                {
                    latestSnapshot = null;
                    return null;
                }
                snapshot = latestSnapshot;
                currentRevision = revision;
                latestSnapshot = null;
            }
            var capture = captureService.CaptureTransientTargetFromSnapshot(snapshot, observation);
            if (capture.Target == null) return null;
            return new TransientMenuMatch
            {
                Target = capture.Target,
                Locator = capture.Locator,
                Snapshot = snapshot,
                Revision = currentRevision,
                Decision = capture.Decision,
                CandidateCount = capture.CandidateCount
            };
        }

        internal void SeedSnapshotForTest(UiaSnapshot snapshot)
        {
            lock (sync)
            {
                latestSnapshot = snapshot;
                latestCapturedAtUtc = DateTime.UtcNow;
                revision++;
            }
        }

        internal void Reset()
        {
            Interlocked.Increment(ref requestGeneration);
            lock (sync)
            {
                latestSnapshot = null;
            }
        }

        public void Dispose()
        {
            cancellation.Cancel();
            cancellation.Dispose();
            Reset();
        }
    }
}
