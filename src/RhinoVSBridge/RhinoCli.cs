using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;

namespace RhinoVSBridge
{
    [DataContract]
    internal sealed class RhinoInstance
    {
        [DataMember(Name = "pipeId")]
        public string PipeId { get; set; } = "";

        [DataMember(Name = "processId")]
        public int ProcessId { get; set; }

        [DataMember(Name = "activeDoc")]
        public RhinoDocument? ActiveDocument { get; set; }

        public override string ToString()
        {
            var title = ActiveDocument?.Title;
            return $"PID {ProcessId} — {(string.IsNullOrWhiteSpace(title) ? "Untitled" : title)} — {PipeId}";
        }
    }

    [DataContract]
    internal sealed class RhinoDocument
    {
        [DataMember(Name = "title")]
        public string? Title { get; set; }
    }

    internal sealed class CommandResult
    {
        public int ExitCode { get; set; }
        public string StandardOutput { get; set; } = "";
        public string StandardError { get; set; } = "";

        public string DisplayText => $"Exit code: {ExitCode}\r\n{StandardOutput}{StandardError}";
    }

    /// <summary>
    /// The official RhinoCode CLI is the ONLY execution transport. No Rhino DLL is loaded in Visual Studio.
    /// The executable uses an argument string (ProcessStartInfo.ArgumentList is unavailable on net48).
    /// </summary>
    internal sealed class RhinoCli
    {
        public string Executable { get; set; }

        public RhinoCli(string? executable = null)
        {
            Executable = executable ?? DiscoverExecutable();
        }

        public static string DiscoverExecutable()
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var fullPath = Path.Combine(programFiles, "Rhino 8", "System", "rhinocode.exe");
            return File.Exists(fullPath) ? fullPath : "rhinocode.exe";
        }

        public async Task<IReadOnlyList<RhinoInstance>> ListInstancesAsync()
        {
            var response = await ExecuteAsync("list --json", 15000).ConfigureAwait(false);
            if (response.ExitCode != 0)
                throw new InvalidOperationException("rhinocode list failed: " + response.DisplayText);

            var json = response.StandardOutput.Trim();
            // Some CLI versions emit a status prefix before JSON.
            var start = json.IndexOf('[');
            var end = json.LastIndexOf(']');
            if (start < 0 || end < start)
                throw new InvalidOperationException("Unexpected JSON output from rhinocode list: " + json);
            json = json.Substring(start, end - start + 1);
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                var serializer = new DataContractJsonSerializer(typeof(List<RhinoInstance>));
                var objects = (List<RhinoInstance>?)serializer.ReadObject(stream);
                return (objects ?? new List<RhinoInstance>())
                    .Where(x => !string.IsNullOrEmpty(x.PipeId)).ToList();
            }
        }

        public Task<CommandResult> RunScriptAsync(string fullPath, string pipeId)
        {
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("Save the script before running it in Rhino.", fullPath);
            if (!string.Equals(Path.GetExtension(fullPath), ".py", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Path.GetExtension(fullPath), ".py2", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only .py (CPython) and .py2 (IronPython) files are supported.");
            if (string.IsNullOrWhiteSpace(pipeId))
                throw new ArgumentException("Choose a running Rhino instance.", nameof(pipeId));

            return ExecuteAsync("--rhino " + QuoteWindowsArgument(pipeId) + " script " + QuoteWindowsArgument(fullPath),
                System.Threading.Timeout.Infinite);
        }

        /// <summary>Windows CommandLineToArgvW-compatible quoting (handles paths with spaces and trailing backslashes).</summary>
        internal static string QuoteWindowsArgument(string argument)
        {
            if (argument == null) throw new ArgumentNullException(nameof(argument));
            var result = new StringBuilder("\"");
            var slashes = 0;
            foreach (var ch in argument)
            {
                if (ch == '\\')
                {
                    slashes++;
                    continue;
                }
                if (ch == '"')
                {
                    result.Append('\\', slashes * 2 + 1);
                    result.Append('"');
                    slashes = 0;
                    continue;
                }
                result.Append('\\', slashes);
                slashes = 0;
                result.Append(ch);
            }
            result.Append('\\', slashes * 2);
            result.Append('"');
            return result.ToString();
        }

        private async Task<CommandResult> ExecuteAsync(string arguments, int timeoutMs)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Executable,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using (var process = new Process { StartInfo = startInfo })
            {
                if (!process.Start())
                    throw new InvalidOperationException("Could not start rhinocode.exe.");
                // Drain both pipes concurrently. Otherwise output-heavy scripts can deadlock.
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                var exited = await Task.Run(() => process.WaitForExit(timeoutMs)).ConfigureAwait(false);
                if (!exited)
                {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    throw new TimeoutException("rhinocode did not respond. Check StartScriptServer in Rhino.");
                }
                var output = await stdout.ConfigureAwait(false);
                var error = await stderr.ConfigureAwait(false);
                return new CommandResult
                {
                    ExitCode = process.ExitCode,
                    StandardOutput = output,
                    StandardError = error
                };
            }
        }
    }
}
