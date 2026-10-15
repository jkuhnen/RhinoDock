using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace RhinoVSBridge
{
    internal sealed class ActiveRhinoExecution : IDisposable
    {
        public string PipeId { get; }
        public string ScriptPath { get; }
        public DateTime StartedUtc { get; }
        public bool CanCancel { get; }
        public string? PreservedWorkspace { get; private set; }
        public CancellationTokenSource Cancellation { get; } = new CancellationTokenSource();

        public ActiveRhinoExecution(string pipeId, string scriptPath, bool canCancel)
        {
            PipeId = pipeId;
            ScriptPath = scriptPath;
            StartedUtc = DateTime.UtcNow;
            CanCancel = canCancel;
        }

        public void Dispose() => Cancellation.Dispose();
        public void MarkUncertain(string? workspace)
        {
            if (!string.IsNullOrWhiteSpace(workspace)) PreservedWorkspace = workspace;
        }

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

        public bool RequestCancellation(string? pipeId, out ActiveRhinoExecution? execution)
        {
            execution = Find(pipeId);
            if (execution == null) return false;
            if (!execution.CanCancel) return false;
            execution.Cancellation.Cancel();
            if (!string.IsNullOrWhiteSpace(execution.PreservedWorkspace))
            {
                try
                {
                    using (File.Open(Path.Combine(execution.PreservedWorkspace!, "cancel.requested"),
                        FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite)) { }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return true;
        }

        public void Complete(ActiveRhinoExecution execution)
        {
            lock (_gate)
            {
                if (_active.TryGetValue(execution.PipeId, out var current) && ReferenceEquals(current, execution))
                    _active.Remove(execution.PipeId);
            }
            execution.Dispose();
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
    }
}
