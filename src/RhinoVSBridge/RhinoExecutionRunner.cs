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

    internal static class RhinoExecutionRunner
    {
        private const int PollIntervalMs = 100;
        private const int TransportExitGraceMs = 2000;
        private const int CompletedTransportGraceMs = 5000;

        public static async Task<CommandResult> RunAsync(
            RhinoCli cli,
            string scriptPath,
            string pipeId,
            Func<ScriptOutputEvent, Task>? onOutput,
            CancellationToken cancellationRequest)
        {
            var workspace = ExecutionWorkspace.Create(scriptPath, pipeId);
            var resultRead = false;
            var processStarted = false;
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
                    catch (Win32Exception ex)
                    {
                        throw new InvalidOperationException(
                            "Could not start rhinocode.exe. Use 'Rhino: Choose rhinocode.exe' to select the Rhino 8 CLI.", ex);
                    }

                    var cliStdout = process.StandardOutput.ReadToEndAsync();
                    var cliStderr = process.StandardError.ReadToEndAsync();
                    var output = new OutputEventTail(workspace.EventsPath);
                    DateTime? transportExitedUtc = null;
                    ScriptCaptureResult? captured = null;

                    while (captured == null)
                    {
                        await output.ForwardAvailableAsync(onOutput).ConfigureAwait(false);
                        captured = TryReadResult(workspace.ResultPath);
                        if (captured != null) break;

                        if (process.HasExited && process.ExitCode != 0)
                        {
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
                                    PreservedWorkspace = workspace.DirectoryPath
                                };
                            }
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
                    return new CommandResult
                    {
                        ExitCode = captured.Cancelled ? 2 : (captured.Success ? 0 : 1),
                        StandardOutput = stdout + captured.StandardOutput,
                        StandardError = stderr + captured.StandardError,
                        Cancelled = captured.Cancelled
                    };
                }
            }
            catch (Exception ex) when (processStarted && !resultRead)
            {
                return new CommandResult
                {
                    ExitCode = -1,
                    StandardError = "Execution monitoring failed after the request may have reached Rhino: " +
                        ex.Message,
                    ExecutionInterrupted = true,
                    PreservedWorkspace = workspace.DirectoryPath
                };
            }
            finally
            {
                if (resultRead)
                {
                    workspace.TryDeleteCompleted();
                }
                else if (!processStarted)
                {
                    try { Directory.Delete(workspace.DirectoryPath, recursive: true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                // If dispatch might have reached Rhino, never delete an incomplete workspace.
                // Rhino may still be reading the runner or may write a late result after a connection loss.
            }
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

        internal static ScriptCaptureResult? TryReadResult(string resultPath)
        {
            if (!File.Exists(resultPath)) return null;
            try
            {
                using (var stream = File.Open(resultPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var serializer = new DataContractJsonSerializer(typeof(ScriptCaptureResult));
                    return (ScriptCaptureResult?)serializer.ReadObject(stream);
                }
            }
            catch (IOException) { return null; }
            catch (SerializationException) { return null; }
        }

        internal static string CreateRunner(string scriptPath, ExecutionWorkspace workspace)
        {
            string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
            return "import base64, contextlib, io, json, os, sys, threading, tokenize, traceback\n" +
                $"script_path = base64.b64decode('{Encode(scriptPath)}').decode('utf-8')\n" +
                $"result_path = base64.b64decode('{Encode(workspace.ResultPath)}').decode('utf-8')\n" +
                $"events_path = base64.b64decode('{Encode(workspace.EventsPath)}').decode('utf-8')\n" +
                $"cancel_path = base64.b64decode('{Encode(workspace.CancellationPath)}').decode('utf-8')\n" +
                "stdout_buffer, stderr_buffer, success, cancelled = io.StringIO(), io.StringIO(), True, False\n" +
                "events_file, events_lock = open(events_path, 'a', encoding='utf-8', buffering=1), threading.Lock()\n" +
                "def emit(stream_name, text):\n" +
                "    if not text:\n" +
                "        return\n" +
                "    line = json.dumps({'stream': stream_name, 'text': text}, ensure_ascii=False)\n" +
                "    with events_lock:\n" +
                "        events_file.write(line + '\\n')\n" +
                "        events_file.flush()\n" +
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
                "            with tokenize.open(script_path) as source_file:\n" +
                "                code = compile(source_file.read(), script_path, 'exec')\n" +
                "            scope = {'__name__': '__main__', '__file__': script_path, '__package__': None}\n" +
                "            sys.settrace(cancellation_trace)\n" +
                "            exec(code, scope, scope)\n" +
                "        except BridgeCancellation as cancellation:\n" +
                "            success, cancelled = False, True\n" +
                "            print('Rhino VS Bridge: ' + str(cancellation), file=sys.stderr)\n" +
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
                "    payload = {'success': success, 'cancelled': cancelled, 'stdout': stdout_buffer.getvalue(), 'stderr': stderr_buffer.getvalue()}\n" +
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
