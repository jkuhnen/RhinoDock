using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RhinoVSBridge
{
    internal sealed class ExecutionWorkspace
    {
        public string SessionId { get; }
        public string DirectoryPath { get; }
        public string RunnerPath => Path.Combine(DirectoryPath, "bridge_runner.py");
        public string StartupPath => Path.Combine(DirectoryPath, "started.json");
        public string ResultPath => Path.Combine(DirectoryPath, "result.json");
        public string EventsPath => Path.Combine(DirectoryPath, "output.jsonl");
        public string CancellationPath => Path.Combine(DirectoryPath, "cancel.requested");
        public string MetadataPath => Path.Combine(DirectoryPath, "session.txt");

        private ExecutionWorkspace(string sessionId, string directoryPath)
        {
            SessionId = sessionId;
            DirectoryPath = directoryPath;
        }

        public static string DefaultRoot
        {
            get
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(local)) local = Path.GetTempPath();
                return Path.Combine(local, "RhinoVSBridge", "Executions");
            }
        }

        public static ExecutionWorkspace Create(string scriptPath, string pipeId, string? root = null)
        {
            var sessionId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N");
            var directory = Path.Combine(root ?? DefaultRoot, sessionId);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "output.jsonl"), string.Empty, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(directory, "session.txt"),
                "createdUtc=" + DateTime.UtcNow.ToString("O") + Environment.NewLine +
                "pipeId=" + pipeId + Environment.NewLine +
                "scriptPath=" + scriptPath + Environment.NewLine,
                new UTF8Encoding(false));
            return new ExecutionWorkspace(sessionId, directory);
        }

        public bool RequestCancellation()
        {
            try
            {
                using (File.Open(CancellationPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite)) { }
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        public bool HasCompletedResult => File.Exists(ResultPath);

        public bool TryDeleteCompleted()
        {
            if (!HasCompletedResult) return false;
            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        public static IReadOnlyList<string> CleanupCompleted(DateTime utcNow, TimeSpan minimumAge, string? root = null)
        {
            var executionRoot = root ?? DefaultRoot;
            if (!Directory.Exists(executionRoot)) return Array.Empty<string>();
            var removed = new List<string>();
            foreach (var directory in Directory.GetDirectories(executionRoot))
            {
                var result = Path.Combine(directory, "result.json");
                if (!File.Exists(result)) continue;
                DateTime completed;
                try { completed = File.GetLastWriteTimeUtc(result); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                if (utcNow - completed < minimumAge) continue;
                try
                {
                    Directory.Delete(directory, recursive: true);
                    removed.Add(directory);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return removed;
        }

        public static IReadOnlyList<string> FindIncomplete(string? root = null)
        {
            var executionRoot = root ?? DefaultRoot;
            if (!Directory.Exists(executionRoot)) return Array.Empty<string>();
            return Directory.GetDirectories(executionRoot)
                .Where(directory => !File.Exists(Path.Combine(directory, "result.json")))
                .OrderBy(directory => directory, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
