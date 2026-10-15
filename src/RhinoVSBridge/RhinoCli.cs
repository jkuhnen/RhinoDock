using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
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

    [DataContract]
    internal sealed class ScriptCaptureResult
    {
        [DataMember(Name = "success")]
        public bool Success { get; set; }

        [DataMember(Name = "stdout")]
        public string StandardOutput { get; set; } = "";

        [DataMember(Name = "stderr")]
        public string StandardError { get; set; } = "";
    }

    internal sealed class CommandResult
    {
        public int ExitCode { get; set; }
        public string StandardOutput { get; set; } = "";
        public string StandardError { get; set; } = "";

        public bool Succeeded => ExitCode == 0;

        public string DisplayText
        {
            get
            {
                var text = new StringBuilder();
                AppendOutput(text, StandardOutput);
                AppendOutput(text, StandardError);
                text.Append("[rhinocode exit code: ").Append(ExitCode).Append(']');
                return text.ToString();
            }
        }

        private static void AppendOutput(StringBuilder target, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            target.Append(value);
            if (!value.EndsWith("\n", StringComparison.Ordinal)) target.AppendLine();
        }
    }

    /// <summary>
    /// The official RhinoCode CLI is the ONLY execution transport. No Rhino DLL is loaded in Visual Studio.
    /// The executable uses an argument string (ProcessStartInfo.ArgumentList is unavailable on net48).
    /// </summary>
    internal sealed class RhinoCli
    {
        internal const int DefaultScriptTimeoutMs = 5 * 60 * 1000;

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

            return ParseInstances(response.StandardOutput);
        }

        internal static IReadOnlyList<RhinoInstance> ParseInstances(string output)
        {
            var json = (output ?? string.Empty).Trim();
            // Some CLI versions emit a status prefix before JSON.
            var start = json.IndexOf('[');
            var end = json.LastIndexOf(']');
            if (start < 0 || end < start)
                throw new InvalidOperationException("Unexpected JSON output from rhinocode list: " + json);
            json = json.Substring(start, end - start + 1);
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                var serializer = new DataContractJsonSerializer(typeof(List<RhinoInstance>));
                try
                {
                    var objects = (List<RhinoInstance>?)serializer.ReadObject(stream);
                    return (objects ?? new List<RhinoInstance>())
                        .Where(x => !string.IsNullOrWhiteSpace(x.PipeId)).ToList();
                }
                catch (SerializationException ex)
                {
                    throw new InvalidOperationException("Unexpected JSON schema from rhinocode list.", ex);
                }
            }
        }

        public async Task<CommandResult> RunScriptAsync(string fullPath, string pipeId)
        {
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("Save the script before running it in Rhino.", fullPath);
            if (!string.Equals(Path.GetExtension(fullPath), ".py", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Path.GetExtension(fullPath), ".py2", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only .py (CPython) and .py2 (IronPython) files are supported.");
            if (string.IsNullOrWhiteSpace(pipeId))
                throw new ArgumentException("Choose a running Rhino instance.", nameof(pipeId));

            if (string.Equals(Path.GetExtension(fullPath), ".py2", StringComparison.OrdinalIgnoreCase))
                return await ExecuteAsync(BuildRunArguments(fullPath, pipeId), DefaultScriptTimeoutMs).ConfigureAwait(false);

            var runDirectory = Path.Combine(Path.GetTempPath(), "RhinoVSBridge", Guid.NewGuid().ToString("N"));
            var runnerPath = Path.Combine(runDirectory, "bridge_runner.py");
            var resultPath = Path.Combine(runDirectory, "result.json");
            Directory.CreateDirectory(runDirectory);
            try
            {
                File.WriteAllText(runnerPath, CreateCaptureRunner(fullPath, resultPath), new UTF8Encoding(false));
                var cliResult = await ExecuteAsync(BuildRunArguments(runnerPath, pipeId), DefaultScriptTimeoutMs)
                    .ConfigureAwait(false);
                if (!cliResult.Succeeded) return cliResult;

                var captured = await WaitForCaptureResultAsync(resultPath, DefaultScriptTimeoutMs).ConfigureAwait(false);
                return new CommandResult
                {
                    ExitCode = captured.Success ? 0 : 1,
                    StandardOutput = cliResult.StandardOutput + captured.StandardOutput,
                    StandardError = cliResult.StandardError + captured.StandardError
                };
            }
            finally
            {
                try { Directory.Delete(runDirectory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        internal static string CreateCaptureRunner(string scriptPath, string resultPath)
        {
            var script = Convert.ToBase64String(Encoding.UTF8.GetBytes(scriptPath));
            var result = Convert.ToBase64String(Encoding.UTF8.GetBytes(resultPath));
            return "import base64, contextlib, io, json, os, sys, tokenize, traceback\n" +
                $"script_path = base64.b64decode('{script}').decode('utf-8')\n" +
                $"result_path = base64.b64decode('{result}').decode('utf-8')\n" +
                "stdout_buffer, stderr_buffer, success = io.StringIO(), io.StringIO(), True\n" +
                "script_directory = os.path.dirname(script_path)\n" +
                "sys.path.insert(0, script_directory)\n" +
                "try:\n" +
                "    with contextlib.redirect_stdout(stdout_buffer), contextlib.redirect_stderr(stderr_buffer):\n" +
                "        try:\n" +
                "            with tokenize.open(script_path) as source_file:\n" +
                "                code = compile(source_file.read(), script_path, 'exec')\n" +
                "            scope = {'__name__': '__main__', '__file__': script_path, '__package__': None}\n" +
                "            exec(code, scope, scope)\n" +
                "        except BaseException:\n" +
                "            success = False\n" +
                "            traceback.print_exc()\n" +
                "finally:\n" +
                "    if sys.path and sys.path[0] == script_directory:\n" +
                "        del sys.path[0]\n" +
                "    payload = {'success': success, 'stdout': stdout_buffer.getvalue(), 'stderr': stderr_buffer.getvalue()}\n" +
                "    temporary_result = result_path + '.tmp'\n" +
                "    with open(temporary_result, 'w', encoding='utf-8') as result_file:\n" +
                "        json.dump(payload, result_file, ensure_ascii=False)\n" +
                "    os.replace(temporary_result, result_path)\n";
        }

        internal static string BuildRunArguments(string fullPath, string pipeId)
        {
            return "--rhino " + QuoteWindowsArgument(pipeId) +
                " script " + QuoteWindowsArgument(fullPath);
        }

        private static async Task<ScriptCaptureResult> WaitForCaptureResultAsync(string resultPath, int timeoutMs)
        {
            var stopwatch = Stopwatch.StartNew();
            while (!File.Exists(resultPath))
            {
                if (stopwatch.ElapsedMilliseconds >= timeoutMs)
                    throw new TimeoutException(
                        "Rhino accepted the script request but did not return a result. Its ScriptServer connection may have been interrupted.");
                await Task.Delay(50).ConfigureAwait(false);
            }
            string json;
            try { json = File.ReadAllText(resultPath, Encoding.UTF8); }
            catch (IOException)
            {
                await Task.Delay(50).ConfigureAwait(false);
                json = File.ReadAllText(resultPath, Encoding.UTF8);
            }
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                var serializer = new DataContractJsonSerializer(typeof(ScriptCaptureResult));
                try
                {
                    return (ScriptCaptureResult?)serializer.ReadObject(stream)
                        ?? throw new InvalidOperationException("Rhino returned an empty script result.");
                }
                catch (SerializationException ex)
                {
                    throw new InvalidOperationException("Rhino returned an invalid script result.", ex);
                }
            }
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

        internal async Task<CommandResult> ExecuteAsync(string arguments, int timeoutMs)
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
                try
                {
                    if (!process.Start())
                        throw new InvalidOperationException("Could not start rhinocode.exe.");
                }
                catch (Win32Exception ex)
                {
                    throw new InvalidOperationException(
                        "Could not start rhinocode.exe. Use 'Rhino: Choose rhinocode.exe' to select the Rhino 8 CLI.", ex);
                }
                // Drain both pipes concurrently. Otherwise output-heavy scripts can deadlock.
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                var exited = await Task.Run(() => process.WaitForExit(timeoutMs)).ConfigureAwait(false);
                if (!exited)
                {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
                    throw new TimeoutException(
                        "rhinocode did not finish within five minutes. Rhino may be busy or its ScriptServer connection was interrupted.");
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
