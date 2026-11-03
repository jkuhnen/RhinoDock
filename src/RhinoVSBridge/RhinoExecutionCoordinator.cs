using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RhinoVSBridge
{
    internal enum RhinoExecutionState
    {
        Running,
        StartupNotAcknowledged,
        Uncertain
    }

    internal enum ExecutionReconciliationStatus
    {
        Pending,
        Completed,
        NoLongerActive
    }

    internal enum CancellationRequestStatus
    {
        NoExecution,
        CannotCancel,
        Requested,
        AlreadyCompleted
    }

    internal sealed class ExecutionReconciliation
    {
        public ExecutionReconciliationStatus Status { get; set; }
        public CompletionArtifactStatus ArtifactStatus { get; set; }
        public ActiveRhinoExecution? Execution { get; set; }
        public ScriptCaptureResult? Result { get; set; }
        public string? Workspace { get; set; }
    }

    internal sealed class CancellationRequestResult
    {
        public CancellationRequestStatus Status { get; set; }
        public ActiveRhinoExecution? Execution { get; set; }
        public ExecutionReconciliation? Reconciliation { get; set; }
    }

    internal sealed class ActiveRhinoExecution : IDisposable
    {
        private int _monitorStarted;

        public string PipeId { get; }
        public string ScriptPath { get; }
        public DateTime StartedUtc { get; }
        public bool CanCancel { get; }
        public RhinoExecutionState State { get; private set; } = RhinoExecutionState.Running;
        public string? PreservedWorkspace { get; private set; }
        public string? SessionId { get; private set; }
        public CancellationTokenSource Cancellation { get; } = new CancellationTokenSource();

        public ActiveRhinoExecution(string pipeId, string scriptPath, bool canCancel)
        {
            PipeId = pipeId;
            ScriptPath = scriptPath;
            StartedUtc = DateTime.UtcNow;
            CanCancel = canCancel;
        }

        internal void MarkUncertain(string workspace, string sessionId, bool startupNotAcknowledged)
        {
            PreservedWorkspace = Path.GetFullPath(workspace);
            SessionId = sessionId;
            State = startupNotAcknowledged
                ? RhinoExecutionState.StartupNotAcknowledged
                : RhinoExecutionState.Uncertain;
        }

        internal bool TryBeginMonitor() => Interlocked.CompareExchange(ref _monitorStarted, 1, 0) == 0;

        public void Dispose() => Cancellation.Dispose();
    }

    internal sealed class RhinoExecutionCoordinator : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, ActiveRhinoExecution> _active =
            new Dictionary<string, ActiveRhinoExecution>(StringComparer.Ordinal);

        public bool TryStart(string pipeId, string scriptPath, bool canCancel, out ActiveRhinoExecution? execution)
        {
            lock (_gate)
            {
                if (_active.ContainsKey(pipeId))
                {
                    execution = null;
                    return false;
                }
                execution = new ActiveRhinoExecution(pipeId, scriptPath, canCancel);
                _active.Add(pipeId, execution);
                return true;
            }
        }

        public ActiveRhinoExecution? Find(string? pipeId)
        {
            lock (_gate)
            {
                if (pipeId != null && pipeId.Length > 0 && _active.TryGetValue(pipeId, out var selected))
                    return selected;
                return _active.Count == 1 ? _active.Values.Single() : null;
            }
        }

        public bool MarkUncertain(ActiveRhinoExecution execution, string? workspace, string? sessionId,
            bool startupNotAcknowledged)
        {
            if (string.IsNullOrWhiteSpace(workspace) || string.IsNullOrWhiteSpace(sessionId)) return false;
            lock (_gate)
            {
                if (!_active.TryGetValue(execution.PipeId, out var current) ||
                    !ReferenceEquals(current, execution)) return false;
                execution.MarkUncertain(workspace!, sessionId!, startupNotAcknowledged);
                return true;
            }
        }

        public ExecutionReconciliation TryReconcile(ActiveRhinoExecution execution)
        {
            string workspace;
            string sessionId;
            lock (_gate)
            {
                if (!_active.TryGetValue(execution.PipeId, out var current) ||
                    !ReferenceEquals(current, execution))
                    return Outcome(ExecutionReconciliationStatus.NoLongerActive, execution);
                if (execution.State == RhinoExecutionState.Running ||
                    string.IsNullOrWhiteSpace(execution.PreservedWorkspace) ||
                    string.IsNullOrWhiteSpace(execution.SessionId))
                    return Outcome(ExecutionReconciliationStatus.Pending, execution);
                workspace = execution.PreservedWorkspace!;
                sessionId = execution.SessionId!;
            }

            var resultPath = Path.Combine(workspace, "result.json");
            var artifact = RhinoExecutionRunner.TryReadResult(resultPath, sessionId, execution.PipeId,
                out var result);
            if (artifact != CompletionArtifactStatus.Valid || result == null)
            {
                var pending = Outcome(ExecutionReconciliationStatus.Pending, execution);
                pending.ArtifactStatus = artifact;
                pending.Workspace = workspace;
                return pending;
            }

            lock (_gate)
            {
                if (!_active.TryGetValue(execution.PipeId, out var current) ||
                    !ReferenceEquals(current, execution) ||
                    !string.Equals(execution.PreservedWorkspace, workspace, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(execution.SessionId, sessionId, StringComparison.Ordinal))
                    return Outcome(ExecutionReconciliationStatus.NoLongerActive, execution);
                _active.Remove(execution.PipeId);
            }
            execution.Dispose();
            return new ExecutionReconciliation
            {
                Status = ExecutionReconciliationStatus.Completed,
                ArtifactStatus = CompletionArtifactStatus.Valid,
                Execution = execution,
                Result = result,
                Workspace = workspace
            };
        }

        public async Task<ExecutionReconciliation> MonitorUncertainAsync(ActiveRhinoExecution execution,
            TimeSpan pollInterval, TimeSpan maximumDuration, CancellationToken cancellationToken)
        {
            if (pollInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
            if (maximumDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumDuration));
            if (!execution.TryBeginMonitor())
                return Outcome(ExecutionReconciliationStatus.NoLongerActive, execution);

            var deadline = DateTime.UtcNow + maximumDuration;
            ExecutionReconciliation last;
            do
            {
                last = TryReconcile(execution);
                if (last.Status != ExecutionReconciliationStatus.Pending) return last;
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) return last;
                var delay = remaining < pollInterval ? remaining : pollInterval;
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            } while (true);
        }

        public CancellationRequestResult RequestCancellation(string? pipeId)
        {
            var execution = Find(pipeId);
            if (execution == null)
                return new CancellationRequestResult { Status = CancellationRequestStatus.NoExecution };

            var before = TryReconcile(execution);
            if (before.Status == ExecutionReconciliationStatus.Completed)
                return CompletedCancellation(execution, before);
            if (before.Status == ExecutionReconciliationStatus.NoLongerActive)
                return new CancellationRequestResult
                {
                    Status = CancellationRequestStatus.AlreadyCompleted,
                    Execution = execution,
                    Reconciliation = before
                };
            if (!execution.CanCancel)
                return new CancellationRequestResult
                {
                    Status = CancellationRequestStatus.CannotCancel,
                    Execution = execution
                };

            string? cancellationPath = null;
            lock (_gate)
            {
                if (!_active.TryGetValue(execution.PipeId, out var current) ||
                    !ReferenceEquals(current, execution))
                    return new CancellationRequestResult
                    {
                        Status = CancellationRequestStatus.AlreadyCompleted,
                        Execution = execution
                    };
                execution.Cancellation.Cancel();
                if (!string.IsNullOrWhiteSpace(execution.PreservedWorkspace))
                {
                    cancellationPath = Path.Combine(execution.PreservedWorkspace!, "cancel.requested");
                    try
                    {
                        using (File.Open(cancellationPath, FileMode.OpenOrCreate, FileAccess.Write,
                            FileShare.ReadWrite)) { }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }

            var after = TryReconcile(execution);
            if (after.Status == ExecutionReconciliationStatus.Completed ||
                after.Status == ExecutionReconciliationStatus.NoLongerActive)
            {
                if (cancellationPath != null)
                {
                    try { File.Delete(cancellationPath); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                return CompletedCancellation(execution, after);
            }
            return new CancellationRequestResult
            {
                Status = CancellationRequestStatus.Requested,
                Execution = execution
            };
        }

        public bool Complete(ActiveRhinoExecution execution)
        {
            var removed = false;
            lock (_gate)
            {
                if (_active.TryGetValue(execution.PipeId, out var current) && ReferenceEquals(current, execution))
                {
                    _active.Remove(execution.PipeId);
                    removed = true;
                }
            }
            if (removed) execution.Dispose();
            return removed;
        }

        public void Dispose()
        {
            ActiveRhinoExecution[] executions;
            lock (_gate)
            {
                executions = _active.Values.ToArray();
                _active.Clear();
            }
            foreach (var execution in executions) execution.Dispose();
        }

        private static ExecutionReconciliation Outcome(ExecutionReconciliationStatus status,
            ActiveRhinoExecution execution)
        {
            return new ExecutionReconciliation
            {
                Status = status,
                ArtifactStatus = CompletionArtifactStatus.Missing,
                Execution = execution,
                Workspace = execution.PreservedWorkspace
            };
        }

        private static CancellationRequestResult CompletedCancellation(ActiveRhinoExecution execution,
            ExecutionReconciliation reconciliation)
        {
            return new CancellationRequestResult
            {
                Status = CancellationRequestStatus.AlreadyCompleted,
                Execution = execution,
                Reconciliation = reconciliation
            };
        }
    }
}
