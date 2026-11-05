using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RhinoVSBridge
{
    [DataContract]
    internal sealed class ScriptOutputEvent
    {
        [DataMember(Name = "stream")]
        public string Stream { get; set; } = "stdout";

        [DataMember(Name = "text")]
        public string Text { get; set; } = string.Empty;
    }

    internal enum CompletionArtifactStatus
    {
        Missing,
        UnreadableOrMalformed,
        IdentityMismatch,
        Valid
    }

    internal static class RhinoExecutionRunner
    {
        private const int PollIntervalMs = 100;
        private const int DefaultStartupTimeoutMs = 15000;
        private const int ImmediateFailureWindowMs = 1000;
        private const int TransportExitGraceMs = 2000;
        private const int CompletedTransportGraceMs = 5000;

        public static async Task<CommandResult> RunAsync(
            RhinoCli cli,
            string scriptPath,
            string pipeId,
            Func<ScriptOutputEvent, Task>? onOutput,
            CancellationToken cancellationRequest,
            TimeSpan? startupTimeout = null,
            string? workspaceRoot = null)
        {
            var startupLimit = startupTimeout ?? TimeSpan.FromMilliseconds(DefaultStartupTimeoutMs);
            if (startupLimit <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(startupTimeout), "The startup timeout must be positive.");

            var workspace = ExecutionWorkspace.Create(scriptPath, pipeId, workspaceRoot);
            var resultRead = false;
            var processStarted = false;
            var safeToRetry = false;
            try
            {
                File.WriteAllText(workspace.RunnerPath,
                    CreateRunner(scriptPath, workspace), new UTF8Encoding(false));

                using (cancellationRequest.Register(() => workspace.RequestCancellation()))
                using (var process = CreateProcess(cli.Executable, RhinoCli.BuildRunArguments(workspace.RunnerPath, pipeId)))
                {
                    try
                    {
                        if (!process.Start()) throw new InvalidOperationException("Could not start rhinocode.exe.");
                        processStarted = true;
                    }
                    catch (Exception ex)
                    {
                        safeToRetry = true;
                        return DispatchFailure(
                            "Could not start rhinocode.exe. Use 'Rhino: Choose rhinocode.exe' to select the Rhino 8 CLI. " +
                            ex.Message);
                    }

                    var launchedUtc = DateTime.UtcNow;
                    var cliStdout = process.StandardOutput.ReadToEndAsync();
                    var cliStderr = process.StandardError.ReadToEndAsync();
                    var output = new OutputEventTail(workspace.EventsPath);
                    DateTime? transportExitedUtc = null;
                    var startupAcknowledged = false;
                    ScriptCaptureResult? captured = null;

                    while (captured == null)
                    {
                        await output.ForwardAvailableAsync(onOutput).ConfigureAwait(false);
                        if (!startupAcknowledged && File.Exists(workspace.StartupPath))
                            startupAcknowledged = true;
                        var completionStatus = TryReadResult(workspace.ResultPath, workspace.SessionId,
                            pipeId, out captured);
                        if (completionStatus == CompletionArtifactStatus.Valid) break;
                        captured = null;

                        if (process.HasExited && process.ExitCode != 0)
                        {
                            if (!startupAcknowledged &&
                                DateTime.UtcNow - launchedUtc <= TimeSpan.FromMilliseconds(ImmediateFailureWindowMs))
                            {
                                var rejectedOut = await cliStdout.ConfigureAwait(false);
                                var rejectedError = await cliStderr.ConfigureAwait(false);
                                safeToRetry = true;
                                return DispatchFailure(
                                    "rhinocode.exe failed before Rhino acknowledged the script request.",
                                    process.ExitCode, rejectedOut, rejectedError);
                            }
                            if (transportExitedUtc == null) transportExitedUtc = DateTime.UtcNow;
                            if (DateTime.UtcNow - transportExitedUtc.Value >= TimeSpan.FromMilliseconds(TransportExitGraceMs))
                            {
                                await output.ForwardAvailableAsync(onOutput).ConfigureAwait(false);
                                var interruptedOut = await cliStdout.ConfigureAwait(false);
                                var interruptedError = await cliStderr.ConfigureAwait(false);
                                return new CommandResult
                                {
                                    ExitCode = process.ExitCode,
                                    StandardOutput = interruptedOut,
                                    StandardError = interruptedError,
                                    ExecutionInterrupted = true,
                                    PreservedWorkspace = workspace.DirectoryPath,
                                    ExecutionSessionId = workspace.SessionId
                                };
                            }
                        }
                        if (!startupAcknowledged && DateTime.UtcNow - launchedUtc >= startupLimit)
                        {
                            if (!process.HasExited)
                            {
                                try { process.Kill(); }
                                catch (InvalidOperationException) { }
                                catch (Win32Exception) { }
                            }
                            if (!process.HasExited)
                                await Task.Run(() => process.WaitForExit(CompletedTransportGraceMs)).ConfigureAwait(false);

                            var startupOut = process.HasExited
                                ? await cliStdout.ConfigureAwait(false)
                                : string.Empty;
                            var startupError = process.HasExited
                                ? await cliStderr.ConfigureAwait(false)
                                : string.Empty;
                            return new CommandResult
                            {
                                ExitCode = process.HasExited ? process.ExitCode : -1,
                                StandardOutput = startupOut,
                                StandardError = AppendDiagnostic(startupError,
                                    "Rhino did not acknowledge starting the Python runner within " +
                                    startupLimit.TotalSeconds.ToString("0.###") + " seconds."),
                                StartupTimedOut = true,
                                ExecutionInterrupted = true,
                                PreservedWorkspace = workspace.DirectoryPath,
                                ExecutionSessionId = workspace.SessionId
                            };
                        }
                        await Task.Delay(PollIntervalMs).ConfigureAwait(false);
                    }

                    resultRead = true;
                    await output.ForwardAvailableAsync(onOutput).ConfigureAwait(false);
                    if (!process.HasExited && !process.WaitForExit(CompletedTransportGraceMs))
                    {
                        // The atomic result proves Python has left the user script and finalized the runner.
                        // Only now is it safe to terminate a stuck transport process.
                        try { process.Kill(); } catch (InvalidOperationException) { }
                    }
                    await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
                    var stdout = await cliStdout.ConfigureAwait(false);
                    var stderr = await cliStderr.ConfigureAwait(false);
                    var finalCapture = captured ?? throw new InvalidOperationException(
                        "The terminal Rhino result disappeared during finalization.");
                    return new CommandResult
                    {
                        ExitCode = finalCapture.Cancelled ? 2 : (finalCapture.Success ? 0 : 1),
                        StandardOutput = stdout + finalCapture.StandardOutput,
                        StandardError = stderr + finalCapture.StandardError,
                        Cancelled = finalCapture.Cancelled
                    };
                }
            }
            catch (Exception ex) when (!processStarted)
            {
                safeToRetry = true;
                return DispatchFailure("RhinoCode dispatch failed before a request could reach Rhino. " + ex.Message);
            }
            catch (Exception ex) when (processStarted && !resultRead)
            {
                return new CommandResult
                {
                    ExitCode = -1,
                    StandardError = "Execution monitoring failed after the request may have reached Rhino: " +
                        ex.Message,
                    ExecutionInterrupted = true,
                    PreservedWorkspace = workspace.DirectoryPath,
                    ExecutionSessionId = workspace.SessionId
                };
            }
            finally
            {
                if (resultRead)
                {
                    workspace.TryDeleteCompleted();
                }
                else if (!processStarted || safeToRetry)
                {
                    try { Directory.Delete(workspace.DirectoryPath, recursive: true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                // If dispatch might have reached Rhino, never delete an incomplete workspace.
                // Rhino may still be reading the runner or may write a late result after a connection loss.
            }
        }

        private static CommandResult DispatchFailure(string diagnostic, int exitCode = -1,
            string standardOutput = "", string standardError = "")
        {
            return new CommandResult
            {
                ExitCode = exitCode,
                StandardOutput = standardOutput,
                StandardError = AppendDiagnostic(standardError, diagnostic),
                DispatchFailed = true
            };
        }

        private static string AppendDiagnostic(string existing, string diagnostic)
        {
            if (string.IsNullOrEmpty(existing)) return diagnostic;
            return existing.EndsWith("\n", StringComparison.Ordinal)
                ? existing + diagnostic
                : existing + Environment.NewLine + diagnostic;
        }

        private static Process CreateProcess(string executable, string arguments)
        {
            return new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };
        }

        internal static CompletionArtifactStatus TryReadResult(string resultPath, string expectedSessionId,
            string expectedPipeId, out ScriptCaptureResult? result)
        {
            result = null;
            if (!File.Exists(resultPath)) return CompletionArtifactStatus.Missing;
            try
            {
                using (var stream = File.Open(resultPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var serializer = new DataContractJsonSerializer(typeof(ScriptCaptureResult));
                    result = (ScriptCaptureResult?)serializer.ReadObject(stream);
                }
            }
            catch (IOException) { return CompletionArtifactStatus.UnreadableOrMalformed; }
            catch (SerializationException) { return CompletionArtifactStatus.UnreadableOrMalformed; }

            if (result == null) return CompletionArtifactStatus.UnreadableOrMalformed;
            if (!string.Equals(result.SessionId, expectedSessionId, StringComparison.Ordinal) ||
                !string.Equals(result.PipeId, expectedPipeId, StringComparison.Ordinal))
            {
                result = null;
                return CompletionArtifactStatus.IdentityMismatch;
            }
            return CompletionArtifactStatus.Valid;
        }

        internal static string CreateRunner(string scriptPath, ExecutionWorkspace workspace)
        {
            string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
            return "import base64, contextlib, io, json, os, sys, threading, tokenize, traceback\n" +
                $"script_path = base64.b64decode('{Encode(scriptPath)}').decode('utf-8')\n" +
                $"session_id = base64.b64decode('{Encode(workspace.SessionId)}').decode('utf-8')\n" +
                $"pipe_id = base64.b64decode('{Encode(workspace.PipeId)}').decode('utf-8')\n" +
                $"result_path = base64.b64decode('{Encode(workspace.ResultPath)}').decode('utf-8')\n" +
                $"startup_path = base64.b64decode('{Encode(workspace.StartupPath)}').decode('utf-8')\n" +
                $"events_path = base64.b64decode('{Encode(workspace.EventsPath)}').decode('utf-8')\n" +
                $"cancel_path = base64.b64decode('{Encode(workspace.CancellationPath)}').decode('utf-8')\n" +
                "temporary_startup = startup_path + '.tmp'\n" +
                "with open(temporary_startup, 'w', encoding='utf-8') as startup_file:\n" +
                "    json.dump({'started': True, 'sessionId': session_id, 'pipeId': pipe_id}, startup_file)\n" +
                "    startup_file.flush()\n" +
                "    os.fsync(startup_file.fileno())\n" +
                "os.replace(temporary_startup, startup_path)\n" +
                "stdout_buffer, stderr_buffer, success, cancelled = io.StringIO(), io.StringIO(), True, False\n" +
                "events_file, events_lock = open(events_path, 'a', encoding='utf-8', buffering=1), threading.Lock()\n" +
                "def emit(stream_name, text):\n" +
                "    if not text:\n" +
                "        return\n" +
                "    line = json.dumps({'stream': stream_name, 'text': text}, ensure_ascii=False)\n" +
                "    with events_lock:\n" +
                "        events_file.write(line + '\\n')\n" +
                "        events_file.flush()\n" +
                "# Mirror Python stdout to Rhino's command history as well as Visual Studio.\n" +
                "try:\n" +
                "    import Rhino\n" +
                "    rhino_write = Rhino.RhinoApp.Write\n" +
                "except (ImportError, AttributeError):\n" +
                "    rhino_write = None\n" +
                "class BridgeStream(io.TextIOBase):\n" +
                "    def __init__(self, stream_name, capture):\n" +
                "        self.stream_name, self.capture = stream_name, capture\n" +
                "    @property\n" +
                "    def encoding(self):\n" +
                "        return 'utf-8'\n" +
                "    def writable(self):\n" +
                "        return True\n" +
                "    def write(self, text):\n" +
                "        text = str(text)\n" +
                "        self.capture.write(text)\n" +
                "        emit(self.stream_name, text)\n" +
                "        if self.stream_name == 'stdout' and rhino_write is not None:\n" +
                "            try:\n" +
                "                rhino_write(text)\n" +
                "            except Exception:\n" +
                "                pass  # Rhino command window must never break script execution.\n" +
                "        return len(text)\n" +
                "    def flush(self):\n" +
                "        events_file.flush()\n" +
                "class BridgeCancellation(BaseException):\n" +
                "    pass\n" +
                "def cancellation_trace(frame, event, arg):\n" +
                "    if os.path.exists(cancel_path):\n" +
                "        raise BridgeCancellation('Cancellation requested from Visual Studio')\n" +
                "    return cancellation_trace\n" +
                "script_directory = os.path.dirname(script_path)\n" +
                "sys.path.insert(0, script_directory)\n" +
                "try:\n" +
                "    with contextlib.redirect_stdout(BridgeStream('stdout', stdout_buffer)), contextlib.redirect_stderr(BridgeStream('stderr', stderr_buffer)):\n" +
                "        try:\n" +
                "            if os.path.exists(cancel_path):\n" +
                "                raise BridgeCancellation('Cancellation requested from Visual Studio')\n" +
                "            with tokenize.open(script_path) as source_file:\n" +
                "                code = compile(source_file.read(), script_path, 'exec')\n" +
                "            scope = {'__name__': '__main__', '__file__': script_path, '__package__': None}\n" +
                "            sys.settrace(cancellation_trace)\n" +
                "            exec(code, scope, scope)\n" +
                "        except BridgeCancellation as cancellation:\n" +
                "            success, cancelled = False, True\n" +
                "            print('Rhino Dock: ' + str(cancellation), file=sys.stderr)\n" +
                "        except BaseException:\n" +
                "            success = False\n" +
                "            traceback.print_exc()\n" +
                "        finally:\n" +
                "            sys.settrace(None)\n" +
                "finally:\n" +
                "    if sys.path and sys.path[0] == script_directory:\n" +
                "        del sys.path[0]\n" +
                "    events_file.flush()\n" +
                "    events_file.close()\n" +
                "    payload = {'sessionId': session_id, 'pipeId': pipe_id, 'success': success, 'cancelled': cancelled, 'stdout': stdout_buffer.getvalue(), 'stderr': stderr_buffer.getvalue()}\n" +
                "    temporary_result = result_path + '.tmp'\n" +
                "    with open(temporary_result, 'w', encoding='utf-8') as result_file:\n" +
                "        json.dump(payload, result_file, ensure_ascii=False)\n" +
                "        result_file.flush()\n" +
                "        os.fsync(result_file.fileno())\n" +
                "    os.replace(temporary_result, result_path)\n";
        }

        private sealed class OutputEventTail
        {
            private readonly string _path;
            private long _offset;
            private readonly List<byte> _pending = new List<byte>();

            public OutputEventTail(string path) => _path = path;

            public async Task ForwardAvailableAsync(Func<ScriptOutputEvent, Task>? onOutput)
            {
                if (!File.Exists(_path)) return;
                using (var stream = File.Open(_path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    stream.Position = _offset;
                    var buffer = new byte[8192];
                    int count;
                    while ((count = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                    {
                        for (var index = 0; index < count; index++) _pending.Add(buffer[index]);
                        _offset += count;
                    }
                }

                while (true)
                {
                    var newline = _pending.IndexOf((byte)'\n');
                    if (newline < 0) break;
                    var lineBytes = _pending.GetRange(0, newline).ToArray();
                    _pending.RemoveRange(0, newline + 1);
                    if (lineBytes.Length == 0) continue;
                    var line = Encoding.UTF8.GetString(lineBytes).TrimEnd('\r');
                    ScriptOutputEvent? outputEvent;
                    using (var json = new MemoryStream(Encoding.UTF8.GetBytes(line)))
                    {
                        var serializer = new DataContractJsonSerializer(typeof(ScriptOutputEvent));
                        outputEvent = (ScriptOutputEvent?)serializer.ReadObject(json);
                    }
                    if (outputEvent != null && onOutput != null)
                    {
                        try
                        {
                            await onOutput(outputEvent).ConfigureAwait(false);
                        }
                        catch
                        {
                            // Output presentation must never change Rhino script lifetime or cleanup safety.
                        }
                    }
                }
            }
        }
    }
}
