using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace YuanbaoRecorder.Agent
{
    internal sealed class ReplayAgentService : IDisposable
    {
        private readonly string serverBaseUrl;
        private readonly string agentId;
        private readonly Func<bool> canClaimTask;
        private readonly ReplayExecutor executor = new ReplayExecutor();
        private CancellationTokenSource cancellation;
        private Task worker;
        private Task executingTask;
        private string currentTaskId;
        private string lastStatus;

        internal ReplayAgentService(string serverBaseUrl, Func<bool> canClaimTask)
        {
            this.serverBaseUrl = serverBaseUrl.TrimEnd('/');
            this.canClaimTask = canClaimTask;
            agentId = Environment.MachineName + "-" + Process.GetCurrentProcess().Id;
        }

        internal event Action<string> StatusChanged;
        internal event Action<bool, string, string> ReplayCompleted;

        internal void Start()
        {
            if (worker != null) return;
            cancellation = new CancellationTokenSource();
            worker = Task.Run(() => RunAsync(cancellation.Token));
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var status = currentTaskId != null ? "busy" : canClaimTask() ? "idle" : "recording";
                    PostWithoutResult(
                        "/api/agents/" + Uri.EscapeDataString(agentId) + "/heartbeat",
                        new AgentHeartbeatRequest
                        {
                            Status = status,
                            Version = AgentBuildInfo.Version,
                            BuildConfiguration = AgentBuildInfo.Configuration,
                            GitCommit = AgentBuildInfo.GitCommit,
                            ExecutablePath = Process.GetCurrentProcess().MainModule.FileName,
                            CurrentTaskId = currentTaskId,
                            Capabilities = new System.Collections.Generic.List<string>
                            {
                                "assertions_v1",
                                "recording_assertions_v1",
                                "aggregate_assertions_v1",
                                "scoped_assertions_v1",
                                "interaction_transactions_v1",
                                "control_flow_v1"
                            }
                        });

                    if (currentTaskId == null && canClaimTask())
                    {
                        var envelope = Post<EmptyRequest, ReplayTaskEnvelope>(
                            "/api/agents/" + Uri.EscapeDataString(agentId) + "/tasks/claim",
                            new EmptyRequest());
                        if (envelope != null && envelope.Task != null)
                        {
                            executingTask = ExecuteTaskAsync(envelope.Task, cancellationToken);
                        }
                        else
                        {
                            PublishStatus("管理平台已连接，等待回放任务");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception error)
                {
                    LogServiceError(error);
                    PublishStatus("管理平台未连接：" + error.Message);
                }

                try
                {
                    await Task.Delay(1200, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private Task ExecuteTaskAsync(ReplayTask task, CancellationToken cancellationToken)
        {
            currentTaskId = task.Id;
            PublishStatus("正在回放：" + task.CaseName);
            return Task.Run(() =>
            {
                ReplayExecutionResult execution = null;
                try
                {
                    execution = executor.Execute(
                        task,
                        cancellationToken,
                        step =>
                        {
                            PublishStatus(string.Format(
                                "正在回放：第 {0}/{1} 步 {2}",
                                step.Index,
                                task.Actions.Count,
                                step.Status == "succeeded" ? "成功" : "失败"));
                            TryPostProgress(task.Id, step);
                        });

                    PostWithoutResult(
                        "/api/replay-tasks/" + Uri.EscapeDataString(task.Id) + "/complete",
                        new ReplayCompleteRequest
                        {
                            AgentId = agentId,
                            Success = execution.Success,
                            CompletedSteps = execution.Steps.Count,
                            Error = execution.Error,
                            Results = execution.Steps
                        });
                    PublishStatus(execution.Success
                        ? "回放完成：" + task.CaseName
                        : "回放失败：" + execution.Error);
                    PublishReplayCompleted(execution.Success, task.CaseName, execution.Error);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception error)
                {
                    LogReplayError(task, error);
                    var reported = TryCompleteAsFailed(task, execution, error);
                    PublishStatus(reported
                        ? "回放失败：" + error.Message
                        : "回放失败且上报失败，请查看日志：" + error.Message);
                    PublishReplayCompleted(false, task.CaseName, error.Message);
                }
                finally
                {
                    currentTaskId = null;
                    executingTask = null;
                }
            }, cancellationToken);
        }

        private void PublishReplayCompleted(bool success, string caseName, string error)
        {
            var handler = ReplayCompleted;
            if (handler != null) handler(success, caseName, error);
        }

        private void TryPostProgress(string taskId, ReplayStepResult step)
        {
            try
            {
                PostWithoutResult(
                    "/api/replay-tasks/" + Uri.EscapeDataString(taskId) + "/progress",
                    new ReplayProgressRequest
                    {
                        AgentId = agentId,
                        CurrentStep = step.Index,
                        Result = step
                    });
            }
            catch
            {
            }
        }

        private bool TryCompleteAsFailed(ReplayTask task, ReplayExecutionResult execution, Exception error)
        {
            Exception reportError = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    PostWithoutResult(
                        "/api/replay-tasks/" + Uri.EscapeDataString(task.Id) + "/complete",
                        new ReplayCompleteRequest
                        {
                            AgentId = agentId,
                            Success = false,
                            CompletedSteps = execution == null ? 0 : execution.Steps.Count,
                            Error = error.Message,
                            Results = execution == null ? new System.Collections.Generic.List<ReplayStepResult>() : execution.Steps
                        });
                    return true;
                }
                catch (Exception currentError)
                {
                    reportError = currentError;
                    Thread.Sleep(300 * attempt);
                }
            }
            LogReplayError(task, new InvalidOperationException("失败结果上报失败", reportError));
            return false;
        }

        private static void LogReplayError(ReplayTask task, Exception error)
        {
            try
            {
                var line = DateTime.UtcNow.ToString("O") + " task=" +
                    (task == null ? "unknown" : task.Id) + Environment.NewLine +
                    error + Environment.NewLine;
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "YuanbaoRecorder.Agent-replay.log"),
                    line,
                    Encoding.UTF8);
            }
            catch { }
        }

        private static void LogServiceError(Exception error)
        {
            LogReplayError(null, error);
        }

        private TResponse Post<TRequest, TResponse>(string relativeUrl, TRequest body)
        {
            var request = CreateRequest(relativeUrl);
            WriteBody(request, body);
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            {
                var serializer = new DataContractJsonSerializer(typeof(TResponse));
                return (TResponse)serializer.ReadObject(stream);
            }
        }

        private void PostWithoutResult<TRequest>(string relativeUrl, TRequest body)
        {
            var request = CreateRequest(relativeUrl);
            WriteBody(request, body);
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            {
                if (stream != null) stream.CopyTo(Stream.Null);
            }
        }

        private HttpWebRequest CreateRequest(string relativeUrl)
        {
            var request = (HttpWebRequest)WebRequest.Create(serverBaseUrl + relativeUrl);
            request.Method = "POST";
            request.ContentType = "application/json; charset=utf-8";
            request.Timeout = 10000;
            request.ReadWriteTimeout = 10000;
            return request;
        }

        private static void WriteBody<TRequest>(HttpWebRequest request, TRequest body)
        {
            var serializer = new DataContractJsonSerializer(typeof(TRequest));
            using (var stream = request.GetRequestStream())
            {
                serializer.WriteObject(stream, body);
            }
        }

        private void PublishStatus(string message)
        {
            if (string.Equals(lastStatus, message, StringComparison.Ordinal)) return;
            lastStatus = message;
            var handler = StatusChanged;
            if (handler != null) handler(message);
        }

        public void Dispose()
        {
            if (cancellation != null) cancellation.Cancel();
            cancellation = null;
            worker = null;
        }
    }
}
