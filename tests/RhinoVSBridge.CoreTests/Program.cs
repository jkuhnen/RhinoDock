using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Diagnostics;
using System.Threading.Tasks;

namespace RhinoVSBridge
{
    internal static class Program
    {
        private static int _passed;
        private static int _failed;

        private static async Task<int> Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--fake-delay")
            {
                await Task.Delay(1200);
                Console.WriteLine("FAKE_LONG_RUN_COMPLETED");
                return 0;
            }
            if (args.Length > 0 && args[0] == "--rhino")
                return await RunFakeRhinoTransportAsync(args);

            if (args.Length == 3 && args[0] == "--rhino-cancel")
            {
                var cli = new RhinoCli(@"C:\Program Files\Rhino 8\System\rhinocode.exe");
                using (var cancellation = new CancellationTokenSource())
                {
                    var firstOutput = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var run = cli.RunScriptAsync(Path.GetFullPath(args[2]), args[1], output =>
                    {
                        Console.Write(output.Text);
                        if (output.Text.Contains("RHINOVSBRIDGE_STREAM_STARTED")) firstOutput.TrySetResult(true);
                        return Task.CompletedTask;
                    }, cancellation.Token);
                    var ready = await Task.WhenAny(firstOutput.Task, Task.Delay(10000));
                    if (ready != firstOutput.Task)
                    {
                        Console.Error.WriteLine("Timed out waiting for streamed Rhino output.");
                        cancellation.Cancel();
                        return 3;
                    }
                    await Task.Delay(500);
                    cancellation.Cancel();
                    var result = await run;
                    Console.WriteLine("\nCANCELLED=" + result.Cancelled + " EXIT=" + result.ExitCode);
                    return result.Cancelled ? 0 : 1;
                }
            }
            if (args.Length == 3 && args[0] == "--rhino-integration")
            {
                var cli = new RhinoCli(@"C:\Program Files\Rhino 8\System\rhinocode.exe");
                var result = await cli.RunScriptAsync(Path.GetFullPath(args[2]), args[1]);
                Console.WriteLine(result.DisplayText);
                return result.Cancelled ? 2 : (result.Succeeded ? 0 : 1);
            }
            if (args.Length != 0)
            {
                Console.Error.WriteLine("Usage: RhinoVSBridge.CoreTests [--rhino-integration|--rhino-cancel <pipe-id> <script.py>]");
                return 2;
            }

            var tests = new (string Name, Func<Task> Run)[]
            {
                ("Windows argument quoting preserves spaces and Unicode", TestUnicodePathQuoting),
                ("Windows argument quoting escapes trailing slashes and quotes", TestDifficultQuoting),
                ("Run command targets exactly one pipe", TestRunArguments),
                ("RhinoCode JSON accepts prefixes and added fields", TestInstanceParsing),
                ("RhinoCode JSON rejects malformed output", TestMalformedInstanceOutput),
                ("Command output keeps stdout, traceback text, and exit code", TestCommandOutput),
                ("Python traceback parsing preserves Unicode and special paths", TestTracebackPathParsing),
                ("Python traceback parsing selects the innermost user frame", TestTracebackFrameSelection),
                ("Python traceback parsing handles chained exceptions", TestChainedTracebacks),
                ("Malformed tracebacks are ignored and line conversion is zero-based", TestMalformedTracebacks),
                ("Error List navigation preserves Python line numbers", TestErrorListNavigationLines),
                ("Missing RhinoCode executable produces actionable error", TestMissingExecutable),
                ("Target selection never falls back from a disconnected explicit instance", TestTargetSelection),
                ("Search path reconciliation removes only obsolete Rhino paths", TestPathReconciliation),
                ("Python project update creates backup and preserves custom paths", TestProjectUpdate),
                ("Script execution supports an infinite transport wait", TestLongRunningTransport),
                ("CPython runner preserves source paths and supports streaming cancellation", TestRunnerContract),
                ("Runner startup acknowledgment completes successfully", TestSuccessfulStartup),
                ("Delayed startup acknowledgment does not limit script duration", TestDelayedStartup),
                ("Startup timeout preserves uncertain execution state", TestStartupTimeout),
                ("Failed process launch cleans up and permits recovery", TestFailedProcessLaunchRecovery),
                ("Immediate process failure cleans up and permits recovery", TestImmediateProcessFailureRecovery),
                ("Execution coordinator prevents overlap per Rhino instance", TestExecutionCoordinator),
                ("Late successful completion releases an uncertain slot", TestLateSuccessfulCompletion),
                ("Late failed completion releases an uncertain slot", TestLateFailedCompletion),
                ("Cancellation before delayed startup is preserved and reconciled", TestCancellationBeforeDelayedStart),
                ("Cancellation after completion creates no marker", TestCancellationAfterCompletion),
                ("Missing malformed and mismatched results preserve the safety lock", TestInvalidCompletionArtifacts),
                ("Completion and cancellation race reconciles exactly once", TestCompletionCancellationRace),
                ("Reconciliation is isolated to the expected Rhino instance", TestReconciliationInstanceIsolation),
                ("Workspace cleanup removes only completed old executions", TestWorkspaceCleanup),
                ("Interrupted transports preserve execution files", TestInterruptedTransport)
            };

            foreach (var test in tests)
            {
                try
                {
                    await test.Run();
                    _passed++;
                    Console.WriteLine("PASS " + test.Name);
                }
                catch (Exception ex)
                {
                    _failed++;
                    Console.Error.WriteLine("FAIL " + test.Name + ": " + ex.Message);
                }
            }

            Console.WriteLine($"{_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        private static Task TestUnicodePathQuoting()
        {
            Equal("\"C:\\Test User\\Grüße 世界\\mein script.py\"",
                RhinoCli.QuoteWindowsArgument("C:\\Test User\\Grüße 世界\\mein script.py"));
            return Task.CompletedTask;
        }

        private static Task TestDifficultQuoting()
        {
            Equal("\"C:\\folder\\\\\"", RhinoCli.QuoteWindowsArgument("C:\\folder\\"));
            Equal("\"say\\\"hello\"", RhinoCli.QuoteWindowsArgument("say\"hello"));
            return Task.CompletedTask;
        }

        private static Task TestRunArguments()
        {
            var command = RhinoCli.BuildRunArguments("C:\\Test User\\Grüße\\script.py", "rhinocode_pipe_42");
            Equal("--rhino \"rhinocode_pipe_42\" script \"C:\\Test User\\Grüße\\script.py\"", command);
            True(command.IndexOf("--rhino", StringComparison.Ordinal) == command.LastIndexOf("--rhino", StringComparison.Ordinal),
                "The command must contain one target selector.");
            return Task.CompletedTask;
        }

        private static Task TestInstanceParsing()
        {
            const string json = "status line\r\n[{\"pipeId\":\"pipe-a\",\"processId\":12,\"futureField\":true," +
                "\"activeDoc\":{\"title\":\"A.3dm\"}},{\"pipeId\":\"\",\"processId\":13}] trailing";
            var instances = RhinoCli.ParseInstances(json);
            Equal(1, instances.Count);
            Equal("pipe-a", instances[0].PipeId);
            Equal(12, instances[0].ProcessId);
            Equal("A.3dm", instances[0].ActiveDocument?.Title);
            return Task.CompletedTask;
        }

        private static async Task TestMalformedInstanceOutput()
        {
            await ThrowsAsync<InvalidOperationException>(() => Task.Run(() => RhinoCli.ParseInstances("not json")),
                "Unexpected JSON output");
        }

        private static Task TestCommandOutput()
        {
            var result = new CommandResult
            {
                ExitCode = 7,
                StandardOutput = "printed before failure\r\n",
                StandardError = "Traceback (most recent call last):\r\nRuntimeError: boom\r\n"
            };
            True(!result.Succeeded, "A nonzero exit must be a failure.");
            Contains("printed before failure", result.DisplayText);
            Contains("Traceback", result.DisplayText);
            Contains("[rhinocode exit code: 7]", result.DisplayText);
            return Task.CompletedTask;
        }

        private static Task TestTracebackPathParsing()
        {
            const string path = @"C:\Test User\Grüße 世界\special [x], & error_test.py";
            var traceback = PythonTracebackParser.Parse(
                "Traceback (most recent call last):\r\n" +
                "  File \"" + path + "\", line 7, in <module>\r\n" +
                "    raise RuntimeError('boom')\r\n" +
                "RuntimeError: RhinoVSBridge error handling test\r\n");

            Equal(1, traceback.Frames.Count);
            Equal(1, traceback.UserFacingFrames.Count);
            Equal(path, traceback.PrimaryFrame?.FilePath);
            Equal(7, traceback.PrimaryFrame?.PythonLineNumber);
            Equal(6, traceback.PrimaryFrame?.VisualStudioLineNumber);
            Equal("<module>", traceback.PrimaryFrame?.FunctionName);
            Equal("RuntimeError: RhinoVSBridge error handling test", traceback.ExceptionMessage);
            return Task.CompletedTask;
        }

        private static Task TestTracebackFrameSelection()
        {
            var traceback = PythonTracebackParser.Parse(
                "Traceback (most recent call last):\n" +
                "  File \"C:\\Temp\\session\\bridge_runner.py\", line 64, in <module>\n" +
                "    exec(code, scope, scope)\n" +
                "  File \"D:\\Scripts\\main script.py\", line 5, in <module>\n" +
                "    helper.fail()\n" +
                "  File \"D:\\Scripts\\helper.py\", line 12, in fail\n" +
                "    raise ValueError('bad')\n" +
                "ValueError: bad\n");

            Equal(3, traceback.Frames.Count);
            True(traceback.Frames[0].IsInternalRunner, "The generated runner frame was not identified.");
            Equal(2, traceback.UserFacingFrames.Count);
            Equal(@"D:\Scripts\helper.py", traceback.PrimaryFrame?.FilePath);
            Equal(12, traceback.PrimaryFrame?.PythonLineNumber);
            Equal(@"D:\Scripts\main script.py", traceback.UserFacingFrames[1].FilePath);
            return Task.CompletedTask;
        }

        private static Task TestChainedTracebacks()
        {
            var traceback = PythonTracebackParser.Parse(
                "Traceback (most recent call last):\n" +
                "  File \"C:\\Scripts\\first.py\", line 2, in first\n" +
                "ValueError: first failure\n\n" +
                "The above exception was the direct cause of the following exception:\n\n" +
                "Traceback (most recent call last):\n" +
                "  File \"C:\\Scripts\\second.py\", line 9, in second\n" +
                "RuntimeError: final failure\n");

            Equal(2, traceback.Frames.Count);
            Equal(@"C:\Scripts\second.py", traceback.PrimaryFrame?.FilePath);
            Equal("RuntimeError: final failure", traceback.ExceptionMessage);
            return Task.CompletedTask;
        }

        private static Task TestMalformedTracebacks()
        {
            var malformed = PythonTracebackParser.Parse(
                "Traceback maybe\n  File \"\", line nope\nnot a Python traceback");
            Equal(0, malformed.Frames.Count);
            Equal(0, malformed.UserFacingFrames.Count);

            var pseudo = PythonTracebackParser.Parse(
                "Traceback (most recent call last):\n" +
                "  File \"<string>\", line 3, in <module>\n" +
                "RuntimeError: hidden\n");
            Equal(1, pseudo.Frames.Count);
            Equal(0, pseudo.UserFacingFrames.Count);
            Equal(0, PythonTracebackParser.ToVisualStudioLine(0));
            Equal(0, PythonTracebackParser.ToVisualStudioLine(1));
            Equal(6, PythonTracebackParser.ToVisualStudioLine(7));
            return Task.CompletedTask;
        }

        private static Task TestErrorListNavigationLines()
        {
            foreach (var pythonLine in new[] { 1, 2, 7, 100 })
            {
                var displayedTaskLine = PythonTracebackParser.ToVisualStudioLine(pythonLine);
                var providerLine = ErrorListNavigation.ToTaskProviderLine(displayedTaskLine);
                Equal(pythonLine, providerLine);
            }

            return Task.CompletedTask;
        }

        private static async Task TestMissingExecutable()
        {
            var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe");
            var cli = new RhinoCli(missing);
            await ThrowsAsync<InvalidOperationException>(() => cli.ExecuteAsync("list --json", 1000),
                "Choose rhinocode.exe");
        }

        private static Task TestPathReconciliation()
        {
            var root = @"C:\Users\Test User\.rhinocode\py39-rh8";
            var oldStub = Path.Combine(root, "site-stubs", "rhino3d-8.30", "");
            var newStub = Path.Combine(root, "site-stubs", "rhino3d-8.35");
            var source = Path.Combine(root, "site-rhinopython");
            var interop = Path.Combine(root, "site-interop");
            var custom = @"C:\Projects\shared";
            var unrelated = @"D:\SDKs\Rhino-custom";
            var actual = IntelliSensePaths.ReconcileSearchPaths(
                new[] { custom, oldStub, source, custom, unrelated },
                new[] { IntelliSensePaths.CompatibilityDirectory, newStub, source, interop }, root);

            SequenceEqual(new[] { custom, source, unrelated, IntelliSensePaths.CompatibilityDirectory, newStub, interop }, actual);
            return Task.CompletedTask;
        }

        private static Task TestTargetSelection()
        {
            var first = new RhinoInstance { PipeId = "pipe-first", ProcessId = 10 };
            var second = new RhinoInstance { PipeId = "pipe-second", ProcessId = 20 };
            var multiple = new[] { first, second };

            Equal(RhinoTargetResolutionKind.SelectionRequired,
                RhinoTargetSelector.Resolve(multiple, null).Kind);
            Equal(RhinoTargetResolutionKind.ExistingSelection,
                RhinoTargetSelector.Resolve(multiple, "pipe-second").Kind);

            var disconnected = RhinoTargetSelector.Resolve(new[] { first }, "pipe-second");
            Equal(RhinoTargetResolutionKind.SelectedInstanceUnavailable, disconnected.Kind);
            Equal("pipe-second", disconnected.UnavailablePipeId);
            True(disconnected.Instance == null,
                "A disconnected explicit selection must never fall back to another Rhino instance.");

            var automatic = RhinoTargetSelector.Resolve(new[] { first }, null);
            Equal(RhinoTargetResolutionKind.Automatic, automatic.Kind);
            Equal("pipe-first", automatic.Instance?.PipeId);
            return Task.CompletedTask;
        }

        private static Task TestProjectUpdate()
        {
            var folder = Path.Combine(Path.GetTempPath(), "RhinoVSBridge-tests-" + Guid.NewGuid());
            Directory.CreateDirectory(folder);
            try
            {
                var project = Path.Combine(folder, "Test.pyproj");
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".rhinocode", "py39-rh8");
                var oldStub = Path.Combine(root, "site-stubs", "rhino3d-8.30");
                var newStub = Path.Combine(root, "site-stubs", "rhino3d-8.35");
                File.WriteAllText(project,
                    "<Project><PropertyGroup><SearchPath>C:\\Custom;" + oldStub +
                    "</SearchPath></PropertyGroup></Project>");

                var update = IntelliSensePaths.UpdatePythonProject(project,
                    new[] { IntelliSensePaths.CompatibilityDirectory, newStub });

                Equal(2, update.Added);
                Equal(1, update.Removed);
                True(File.Exists(project + ".rhino-bridge.bak"), "The original project was not backed up.");
                var updated = File.ReadAllText(project);
                Contains("C:\\Custom", updated);
                Contains(newStub, updated);
                True(!updated.Contains(oldStub), "The obsolete stub path was retained.");
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
            return Task.CompletedTask;
        }
        private static async Task TestLongRunningTransport()
        {
            var executable = Environment.ProcessPath ?? throw new Exception("Test executable path unavailable.");
            var stopwatch = Stopwatch.StartNew();
            var result = await new RhinoCli(executable).ExecuteAsync("--fake-delay", Timeout.Infinite);
            True(result.Succeeded, result.DisplayText);
            Contains("FAKE_LONG_RUN_COMPLETED", result.StandardOutput);
            True(stopwatch.ElapsedMilliseconds >= 1000, "The long-running transport test returned too early.");
        }

        private static Task TestRunnerContract()
        {
            var root = Path.Combine(Path.GetTempPath(), "RhinoVSBridge-runner-contract-" + Guid.NewGuid());
            var workspace = ExecutionWorkspace.Create(@"C:\Test User\Grüße 世界\source.py", "pipe-contract", root);
            try
            {
                var runner = RhinoExecutionRunner.CreateRunner(@"C:\Test User\Grüße 世界\source.py", workspace);
                Contains("compile(source_file.read(), script_path, 'exec')", runner);
                Contains("sys.settrace(cancellation_trace)", runner);
                Contains("os.path.exists(cancel_path)", runner);
                Contains("events_file.flush()", runner);
                Contains("os.replace(temporary_startup, startup_path)", runner);
                Contains("os.fsync(result_file.fileno())", runner);
                Contains("os.replace(temporary_result, result_path)", runner);
                True(!runner.Contains(@"C:\Test User\Grüße 世界\source.py"),
                    "Unicode source paths must be encoded in the generated runner.");
                True(workspace.RequestCancellation(), "Cancellation sentinel was not created.");
                True(File.Exists(workspace.CancellationPath), "Cancellation sentinel is missing.");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            return Task.CompletedTask;
        }

        private static async Task TestSuccessfulStartup()
        {
            var root = Path.Combine(Path.GetTempPath(), "RhinoVSBridge-start-success-" + Guid.NewGuid());
            var script = CreateScriptFixture(root);
            try
            {
                var executable = Environment.ProcessPath ?? throw new Exception("Test executable path unavailable.");
                var result = await RhinoExecutionRunner.RunAsync(new RhinoCli(executable), script,
                    "test-start-success", null, CancellationToken.None, TimeSpan.FromSeconds(2), root);

                True(result.Succeeded, result.DisplayText);
                True(!result.DispatchFailed && !result.ExecutionInterrupted,
                    "Successful startup was classified as interrupted.");
                Equal(0, Directory.GetDirectories(root).Length);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static async Task TestDelayedStartup()
        {
            var root = Path.Combine(Path.GetTempPath(), "RhinoVSBridge-delayed-start-" + Guid.NewGuid());
            var script = CreateScriptFixture(root);
            try
            {
                var executable = Environment.ProcessPath ?? throw new Exception("Test executable path unavailable.");
                var stopwatch = Stopwatch.StartNew();
                var result = await RhinoExecutionRunner.RunAsync(new RhinoCli(executable), script,
                    "test-delayed-start", null, CancellationToken.None, TimeSpan.FromMilliseconds(700), root);

                True(result.Succeeded, result.DisplayText);
                True(stopwatch.ElapsedMilliseconds >= 1000,
                    "The fake script did not run beyond the startup timeout.");
                True(!result.StartupTimedOut,
                    "Execution duration was incorrectly treated as a startup timeout.");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static async Task TestStartupTimeout()
        {
            var root = Path.Combine(Path.GetTempPath(), "RhinoVSBridge-start-timeout-" + Guid.NewGuid());
            var script = CreateScriptFixture(root);
            string? preserved = null;
            try
            {
                var executable = Environment.ProcessPath ?? throw new Exception("Test executable path unavailable.");
                using (var coordinator = new RhinoExecutionCoordinator())
                {
                    True(coordinator.TryStart("test-never-start", script, true, out var execution) && execution != null,
                        "Initial execution slot was not acquired.");
                    var result = await RhinoExecutionRunner.RunAsync(new RhinoCli(executable), script,
                        "test-never-start", null, execution!.Cancellation.Token,
                        TimeSpan.FromMilliseconds(300), root);

                    True(result.StartupTimedOut && result.ExecutionInterrupted,
                        "Missing startup acknowledgment was not classified as uncertain.");
                    Contains("did not acknowledge starting", result.StandardError);
                    preserved = result.PreservedWorkspace;
                    True(!string.IsNullOrWhiteSpace(preserved) && Directory.Exists(preserved),
                        "Startup-timeout workspace was not preserved.");
                    True(coordinator.MarkUncertain(execution, preserved, result.ExecutionSessionId,
                        startupNotAcknowledged: true), "Uncertain execution identity was not retained.");
                    True(!coordinator.TryStart("test-never-start", "overlap.py", true, out _),
                        "An uncertain startup released its Rhino execution slot.");
                    var cancellation = coordinator.RequestCancellation("test-never-start");
                    True(cancellation.Status == CancellationRequestStatus.Requested &&
                        ReferenceEquals(execution, cancellation.Execution),
                        "Cancellation was not routed to the uncertain execution.");
                    True(File.Exists(Path.Combine(preserved!, "cancel.requested")),
                        "Cancellation sentinel was not written for the uncertain execution.");
                    coordinator.Complete(execution);
                }
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static async Task TestFailedProcessLaunchRecovery()
        {
            var root = Path.Combine(Path.GetTempPath(), "RhinoVSBridge-launch-failure-" + Guid.NewGuid());
            var script = CreateScriptFixture(root);
            try
            {
                using (var coordinator = new RhinoExecutionCoordinator())
                {
                    True(coordinator.TryStart("test-launch-failure", script, true, out var failed) && failed != null,
                        "Initial execution slot was not acquired.");
                    var missing = Path.Combine(root, Guid.NewGuid() + ".exe");
                    var result = await RhinoExecutionRunner.RunAsync(new RhinoCli(missing), script,
                        "test-launch-failure", null, failed!.Cancellation.Token,
                        TimeSpan.FromSeconds(1), root);

                    True(result.DispatchFailed && !result.ExecutionInterrupted,
                        "A failed process launch was not classified as safe to retry.");
                    Contains("Could not start rhinocode.exe", result.StandardError);
                    Equal(0, Directory.GetDirectories(root).Length);
                    coordinator.Complete(failed);
                    True(coordinator.TryStart("test-launch-failure", script, true, out var retry) && retry != null,
                        "Execution slot remained blocked after a failed process launch.");
                    coordinator.Complete(retry!);
                }
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static async Task TestImmediateProcessFailureRecovery()
        {
            var root = Path.Combine(Path.GetTempPath(), "RhinoVSBridge-immediate-failure-" + Guid.NewGuid());
            var script = CreateScriptFixture(root);
            try
            {
                var executable = Environment.ProcessPath ?? throw new Exception("Test executable path unavailable.");
                using (var coordinator = new RhinoExecutionCoordinator())
                {
                    True(coordinator.TryStart("test-immediate-failure", script, true, out var failed) && failed != null,
                        "Initial execution slot was not acquired.");
                    var result = await RhinoExecutionRunner.RunAsync(new RhinoCli(executable), script,
                        "test-immediate-failure", null, failed!.Cancellation.Token,
                        TimeSpan.FromSeconds(2), root);

                    True(result.DispatchFailed && !result.ExecutionInterrupted,
                        "Immediate transport failure was not classified as safe to retry.");
                    Equal(23, result.ExitCode);
                    Contains("synthetic immediate startup failure", result.StandardError);
                    Equal(0, Directory.GetDirectories(root).Length);
                    coordinator.Complete(failed);
                    True(coordinator.TryStart("test-immediate-failure", script, true, out var retry) && retry != null,
                        "Execution slot remained blocked after immediate process failure.");
                    coordinator.Complete(retry!);
                }
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static Task TestExecutionCoordinator()
        {
            using (var coordinator = new RhinoExecutionCoordinator())
            {
                True(coordinator.TryStart("pipe-a", "first.py", true, out var first) && first != null,
                    "First execution was rejected.");
                True(!coordinator.TryStart("pipe-a", "overlap.py", true, out _),
                    "Overlapping execution in the same Rhino instance was accepted.");
                True(coordinator.TryStart("pipe-b", "legacy.py2", false, out var second) && second != null,
                    "A different Rhino instance should have its own execution slot.");
                var cancellation = coordinator.RequestCancellation("pipe-a");
                True(cancellation.Status == CancellationRequestStatus.Requested &&
                    ReferenceEquals(first, cancellation.Execution),
                    "CPython cancellation was not routed to the selected instance.");
                True(first!.Cancellation.IsCancellationRequested, "Cancellation token was not signalled.");
                var legacyCancellation = coordinator.RequestCancellation("pipe-b");
                True(legacyCancellation.Status == CancellationRequestStatus.CannotCancel &&
                    legacyCancellation.Execution != null && !legacyCancellation.Execution.CanCancel,
                    "Legacy .py2 cancellation must be refused safely.");
                coordinator.Complete(first);
                coordinator.Complete(second!);
            }
            return Task.CompletedTask;
        }

        private static async Task TestLateSuccessfulCompletion()
        {
            var root = Path.Combine(Path.GetTempPath(), "RhinoVSBridge-late-success-" + Guid.NewGuid());
            Directory.CreateDirectory(root);
            try
            {
                using (var coordinator = new RhinoExecutionCoordinator())
                {
                    var timedOut = await CreateTimedOutExecutionAsync(coordinator, root, "test-never-start");
                    var monitor = coordinator.MonitorUncertainAsync(timedOut.Execution,
                        TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(2), CancellationToken.None);
                    await Task.Delay(60);
                    WriteTerminalResult(timedOut.Workspace, timedOut.SessionId, timedOut.Execution.PipeId,
                        success: true, cancelled: false, stdout: "late success");

                    var reconciliation = await monitor;
                    Equal(ExecutionReconciliationStatus.Completed, reconciliation.Status);
                    True(reconciliation.Result?.Success == true, "Late success result was not retained.");
                    True(coordinator.TryStart(timedOut.Execution.PipeId, "next.py", true, out var retry) && retry != null,
                        "Late success did not release the execution slot.");
                    coordinator.Complete(retry!);
                }
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static async Task TestLateFailedCompletion()
        {
            var root = Path.Combine(Path.GetTempPath(), "RhinoVSBridge-late-failure-" + Guid.NewGuid());
            Directory.CreateDirectory(root);
            try
            {
                using (var coordinator = new RhinoExecutionCoordinator())
                {
                    var timedOut = await CreateTimedOutExecutionAsync(coordinator, root, "test-never-start");
                    var monitor = coordinator.MonitorUncertainAsync(timedOut.Execution,
                        TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(2), CancellationToken.None);
                    await Task.Delay(60);
                    WriteTerminalResult(timedOut.Workspace, timedOut.SessionId, timedOut.Execution.PipeId,
                        success: false, cancelled: false, stderr: "late traceback");

                    var reconciliation = await monitor;
                    Equal(ExecutionReconciliationStatus.Completed, reconciliation.Status);
                    True(reconciliation.Result != null && !reconciliation.Result.Success &&
                        !reconciliation.Result.Cancelled, "Late failure was not terminal.");
                    Contains("late traceback", reconciliation.Result!.StandardError);
                    True(coordinator.TryStart(timedOut.Execution.PipeId, "next.py", true, out var retry) && retry != null,
                        "Late failure did not release the execution slot.");
                    coordinator.Complete(retry!);
                }
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static async Task TestCancellationBeforeDelayedStart()
        {
            var root = Path.Combine(Path.GetTempPath(), "RhinoVSBridge-cancel-before-start-" + Guid.NewGuid());
            Directory.CreateDirectory(root);
            try
            {
                using (var coordinator = new RhinoExecutionCoordinator())
                {
                    var uncertain = CreateUncertainExecution(coordinator, root, "pipe-cancel-before", true);
                    var cancellation = coordinator.RequestCancellation(uncertain.Execution.PipeId);
                    Equal(CancellationRequestStatus.Requested, cancellation.Status);
                    True(File.Exists(Path.Combine(uncertain.Workspace.DirectoryPath, "cancel.requested")),
                        "Delayed-start cancellation marker was not written.");

                    var monitor = coordinator.MonitorUncertainAsync(uncertain.Execution,
                        TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(2), CancellationToken.None);
                    WriteTerminalResult(uncertain.Workspace.DirectoryPath, uncertain.Workspace.SessionId,
                        uncertain.Execution.PipeId, success: false, cancelled: true,
                        stderr: "Cancellation requested from Visual Studio");
                    var reconciliation = await monitor;
                    Equal(ExecutionReconciliationStatus.Completed, reconciliation.Status);
                    True(reconciliation.Result?.Cancelled == true,
                        "Delayed runner cancellation was not reconciled as terminal.");
                }
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static Task TestCancellationAfterCompletion()
        {
            var root = Path.Combine(Path.GetTempPath(), "RhinoVSBridge-cancel-after-complete-" + Guid.NewGuid());
            Directory.CreateDirectory(root);
            try
            {
                using (var coordinator = new RhinoExecutionCoordinator())
                {
                    var uncertain = CreateUncertainExecution(coordinator, root, "pipe-cancel-after", true);
                    WriteTerminalResult(uncertain.Workspace.DirectoryPath, uncertain.Workspace.SessionId,
                        uncertain.Execution.PipeId, success: true, cancelled: false);

                    var cancellation = coordinator.RequestCancellation(uncertain.Execution.PipeId);
                    Equal(CancellationRequestStatus.AlreadyCompleted, cancellation.Status);
                    True(cancellation.Reconciliation?.Status == ExecutionReconciliationStatus.Completed,
                        "Completion was not returned to the cancellation caller.");
                    True(!File.Exists(Path.Combine(uncertain.Workspace.DirectoryPath, "cancel.requested")),
                        "Cancellation marker was created after terminal completion.");
                    True(coordinator.TryStart(uncertain.Execution.PipeId, "next.py", true, out var retry) && retry != null,
                        "Completion discovered during cancellation did not release the slot.");
                    coordinator.Complete(retry!);
                }
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            return Task.CompletedTask;
        }

        private static Task TestInvalidCompletionArtifacts()
        {
            var root = Path.Combine(Path.GetTempPath(), "RhinoVSBridge-invalid-results-" + Guid.NewGuid());
            Directory.CreateDirectory(root);
            try
            {
                using (var coordinator = new RhinoExecutionCoordinator())
                {
                    var uncertain = CreateUncertainExecution(coordinator, root, "pipe-invalid", true);
                    var missing = coordinator.TryReconcile(uncertain.Execution);
                    Equal(ExecutionReconciliationStatus.Pending, missing.Status);
                    Equal(CompletionArtifactStatus.Missing, missing.ArtifactStatus);

                    File.WriteAllText(uncertain.Workspace.ResultPath, "{not-json");
                    var malformed = coordinator.TryReconcile(uncertain.Execution);
                    Equal(ExecutionReconciliationStatus.Pending, malformed.Status);
                    Equal(CompletionArtifactStatus.UnreadableOrMalformed, malformed.ArtifactStatus);

                    WriteTerminalResult(uncertain.Workspace.DirectoryPath, "wrong-session",
                        uncertain.Execution.PipeId, success: true, cancelled: false);
                    var mismatched = coordinator.TryReconcile(uncertain.Execution);
                    Equal(ExecutionReconciliationStatus.Pending, mismatched.Status);
                    Equal(CompletionArtifactStatus.IdentityMismatch, mismatched.ArtifactStatus);
                    True(!coordinator.TryStart(uncertain.Execution.PipeId, "unsafe.py", true, out _),
                        "Invalid completion artifact released the safety lock.");
                    coordinator.Complete(uncertain.Execution);
                }
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            return Task.CompletedTask;
        }

        private static async Task TestCompletionCancellationRace()
        {
            var root = Path.Combine(Path.GetTempPath(), "RhinoVSBridge-completion-race-" + Guid.NewGuid());
            Directory.CreateDirectory(root);
            try
            {
                using (var coordinator = new RhinoExecutionCoordinator())
                {
                    var uncertain = CreateUncertainExecution(coordinator, root, "pipe-race", true);
                    var monitor = coordinator.MonitorUncertainAsync(uncertain.Execution,
                        TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(2), CancellationToken.None);
                    var writer = Task.Run(async () =>
                    {
                        await Task.Delay(10);
                        WriteTerminalResult(uncertain.Workspace.DirectoryPath, uncertain.Workspace.SessionId,
                            uncertain.Execution.PipeId, success: true, cancelled: false);
                    });
                    var cancellation = coordinator.RequestCancellation(uncertain.Execution.PipeId);
                    await writer;
                    var reconciliation = await monitor;

                    True(cancellation.Status == CancellationRequestStatus.Requested ||
                        cancellation.Status == CancellationRequestStatus.AlreadyCompleted,
                        "Race returned an invalid cancellation state.");
                    True(reconciliation.Status == ExecutionReconciliationStatus.Completed ||
                        reconciliation.Status == ExecutionReconciliationStatus.NoLongerActive,
                        "Race did not converge to one terminal reconciliation.");
                    True(coordinator.Find(uncertain.Execution.PipeId) == null,
                        "Race left the completed execution active.");
                    True(coordinator.TryStart(uncertain.Execution.PipeId, "next.py", true, out var retry) && retry != null,
                        "Race did not release the execution slot.");
                    coordinator.Complete(retry!);
                }
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static Task TestReconciliationInstanceIsolation()
        {
            var root = Path.Combine(Path.GetTempPath(), "RhinoVSBridge-instance-isolation-" + Guid.NewGuid());
            Directory.CreateDirectory(root);
            try
            {
                using (var coordinator = new RhinoExecutionCoordinator())
                {
                    var uncertain = CreateUncertainExecution(coordinator, root, "pipe-a", true);
                    WriteTerminalResult(uncertain.Workspace.DirectoryPath, uncertain.Workspace.SessionId,
                        "pipe-b", success: true, cancelled: false);
                    var wrongInstance = coordinator.TryReconcile(uncertain.Execution);
                    Equal(ExecutionReconciliationStatus.Pending, wrongInstance.Status);
                    Equal(CompletionArtifactStatus.IdentityMismatch, wrongInstance.ArtifactStatus);
                    True(!coordinator.TryStart("pipe-a", "unsafe.py", true, out _),
                        "A result for another Rhino instance released pipe-a.");
                    True(coordinator.TryStart("pipe-b", "other.py", true, out var other) && other != null,
                        "Uncertain pipe-a incorrectly blocked a different Rhino instance.");
                    coordinator.Complete(other!);

                    WriteTerminalResult(uncertain.Workspace.DirectoryPath, uncertain.Workspace.SessionId,
                        uncertain.Execution.PipeId, success: true, cancelled: false);
                    Equal(ExecutionReconciliationStatus.Completed,
                        coordinator.TryReconcile(uncertain.Execution).Status);
                }
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            return Task.CompletedTask;
        }

        private static Task TestWorkspaceCleanup()
        {
            var root = Path.Combine(Path.GetTempPath(), "RhinoVSBridge-cleanup-" + Guid.NewGuid());
            Directory.CreateDirectory(root);
            try
            {
                var oldCompleted = ExecutionWorkspace.Create("old.py", "pipe-a", root);
                File.WriteAllText(oldCompleted.ResultPath, "{}");
                File.SetLastWriteTimeUtc(oldCompleted.ResultPath, DateTime.UtcNow.AddDays(-2));
                var recentCompleted = ExecutionWorkspace.Create("recent.py", "pipe-b", root);
                File.WriteAllText(recentCompleted.ResultPath, "{}");
                var incomplete = ExecutionWorkspace.Create("possibly-running.py", "pipe-c", root);

                var removed = ExecutionWorkspace.CleanupCompleted(DateTime.UtcNow, TimeSpan.FromDays(1), root);
                True(removed.Contains(oldCompleted.DirectoryPath), "Old completed workspace was not removed.");
                True(!Directory.Exists(oldCompleted.DirectoryPath), "Old completed workspace still exists.");
                True(Directory.Exists(recentCompleted.DirectoryPath), "Recent completed workspace was removed too early.");
                True(Directory.Exists(incomplete.DirectoryPath), "Incomplete workspace was deleted unsafely.");
                True(ExecutionWorkspace.FindIncomplete(root).Contains(incomplete.DirectoryPath),
                    "Incomplete workspace was not discoverable for diagnostics.");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            return Task.CompletedTask;
        }

        private static async Task TestInterruptedTransport()
        {
            var scriptFolder = Path.Combine(Path.GetTempPath(), "RhinoVSBridge-interruption-" + Guid.NewGuid());
            Directory.CreateDirectory(scriptFolder);
            var script = Path.Combine(scriptFolder, "interrupted.py");
            File.WriteAllText(script, "print('never dispatched by fake transport')");
            string? preserved = null;
            try
            {
                var executable = Environment.ProcessPath ?? throw new Exception("Test executable path unavailable.");
                var result = await new RhinoCli(executable).RunScriptAsync(script, "pipe-interrupted");
                True(result.ExecutionInterrupted, "Interrupted transport was not identified.");
                Equal(42, result.ExitCode);
                preserved = result.PreservedWorkspace;
                True(!string.IsNullOrWhiteSpace(preserved) && Directory.Exists(preserved),
                    "Potentially active execution files were deleted.");
                True(!File.Exists(Path.Combine(preserved!, "result.json")),
                    "Fake interrupted transport unexpectedly produced a result.");
            }
            finally
            {
                if (preserved != null && Directory.Exists(preserved)) Directory.Delete(preserved, recursive: true);
                if (Directory.Exists(scriptFolder)) Directory.Delete(scriptFolder, recursive: true);
            }
        }

        private static async Task<(ActiveRhinoExecution Execution, string Workspace, string SessionId)>
            CreateTimedOutExecutionAsync(RhinoExecutionCoordinator coordinator, string root, string pipeId)
        {
            var script = CreateScriptFixture(root);
            True(coordinator.TryStart(pipeId, script, true, out var execution) && execution != null,
                "Timed-out execution slot was not acquired.");
            var executable = Environment.ProcessPath ?? throw new Exception("Test executable path unavailable.");
            var result = await RhinoExecutionRunner.RunAsync(new RhinoCli(executable), script, pipeId, null,
                execution!.Cancellation.Token, TimeSpan.FromMilliseconds(250), root);
            True(result.StartupTimedOut && result.PreservedWorkspace != null &&
                result.ExecutionSessionId != null, "Synthetic execution did not reach startup timeout.");
            True(coordinator.MarkUncertain(execution, result.PreservedWorkspace, result.ExecutionSessionId,
                startupNotAcknowledged: true), "Timed-out execution identity was not retained.");
            return (execution, result.PreservedWorkspace!, result.ExecutionSessionId!);
        }

        private static (ActiveRhinoExecution Execution, ExecutionWorkspace Workspace) CreateUncertainExecution(
            RhinoExecutionCoordinator coordinator, string root, string pipeId, bool startupNotAcknowledged)
        {
            var script = CreateScriptFixture(root);
            True(coordinator.TryStart(pipeId, script, true, out var execution) && execution != null,
                "Uncertain execution slot was not acquired.");
            var workspace = ExecutionWorkspace.Create(script, pipeId, root);
            True(coordinator.MarkUncertain(execution!, workspace.DirectoryPath, workspace.SessionId,
                startupNotAcknowledged), "Uncertain execution identity was not retained.");
            return (execution!, workspace);
        }

        private static void WriteTerminalResult(string workspace, string sessionId, string pipeId,
            bool success, bool cancelled, string stdout = "", string stderr = "")
        {
            var resultPath = Path.Combine(workspace, "result.json");
            var temporary = resultPath + ".test-" + Guid.NewGuid().ToString("N");
            var json = "{\"sessionId\":\"" + sessionId + "\",\"pipeId\":\"" + pipeId +
                "\",\"success\":" + (success ? "true" : "false") +
                ",\"cancelled\":" + (cancelled ? "true" : "false") +
                ",\"stdout\":\"" + stdout + "\",\"stderr\":\"" + stderr + "\"}";
            File.WriteAllText(temporary, json);
            if (File.Exists(resultPath)) File.Delete(resultPath);
            File.Move(temporary, resultPath);
        }

        private static string CreateScriptFixture(string root)
        {
            Directory.CreateDirectory(root);
            var script = Path.Combine(root, "fixture.py");
            File.WriteAllText(script, "print('fixture')");
            return script;
        }

        private static async Task<int> RunFakeRhinoTransportAsync(string[] args)
        {
            if (args.Length < 4) return 42;
            var pipeId = args[1];
            var workspace = Path.GetDirectoryName(args[3]);
            if (string.IsNullOrWhiteSpace(workspace)) return 42;

            if (pipeId == "test-immediate-failure")
            {
                Console.Error.WriteLine("synthetic immediate startup failure");
                return 23;
            }
            if (pipeId == "pipe-interrupted")
            {
                await Task.Delay(1200);
                Console.Error.WriteLine("synthetic connection interruption");
                return 42;
            }
            if (pipeId == "test-never-start")
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
                return 0;
            }
            if (pipeId == "test-delayed-start")
            {
                await Task.Delay(300);
                File.WriteAllText(Path.Combine(workspace, "started.json"), "{\"started\":true}");
                await Task.Delay(900);
                var identity = ReadWorkspaceIdentity(workspace);
                WriteTerminalResult(workspace, identity.SessionId, pipeId,
                    success: true, cancelled: false, stdout: "delayed");
                return 0;
            }
            if (pipeId == "test-start-success")
            {
                File.WriteAllText(Path.Combine(workspace, "started.json"), "{\"started\":true}");
                var identity = ReadWorkspaceIdentity(workspace);
                WriteTerminalResult(workspace, identity.SessionId, pipeId,
                    success: true, cancelled: false, stdout: "started");
                return 0;
            }
            return 42;
        }

        private static (string SessionId, string PipeId) ReadWorkspaceIdentity(string workspace)
        {
            var values = File.ReadAllLines(Path.Combine(workspace, "session.txt"))
                .Select(line => line.Split(new[] { '=' }, 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
            return (values["sessionId"], values["pipeId"]);
        }


        private static async Task ThrowsAsync<T>(Func<Task> action, string messagePart) where T : Exception
        {
            try { await action(); }
            catch (T ex)
            {
                Contains(messagePart, ex.Message);
                return;
            }
            throw new Exception("Expected " + typeof(T).Name + ".");
        }

        private static void Equal<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception($"Expected '{expected}', got '{actual}'.");
        }

        private static void Contains(string expected, string actual)
        {
            if (actual.IndexOf(expected, StringComparison.Ordinal) < 0)
                throw new Exception($"Expected text '{expected}' in '{actual}'.");
        }

        private static void True(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private static void SequenceEqual(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
        {
            if (!expected.SequenceEqual(actual, StringComparer.OrdinalIgnoreCase))
                throw new Exception("Expected [" + string.Join(", ", expected) + "] but got [" + string.Join(", ", actual) + "].");
        }
    }
}
