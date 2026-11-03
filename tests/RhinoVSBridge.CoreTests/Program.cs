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
                    execution.MarkUncertain(preserved);
                    True(!coordinator.TryStart("test-never-start", "overlap.py", true, out _),
                        "An uncertain startup released its Rhino execution slot.");
                    True(coordinator.RequestCancellation("test-never-start", out var cancelled) &&
                        ReferenceEquals(execution, cancelled),
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
                True(coordinator.RequestCancellation("pipe-a", out var cancelled) && ReferenceEquals(first, cancelled),
                    "CPython cancellation was not routed to the selected instance.");
                True(first!.Cancellation.IsCancellationRequested, "Cancellation token was not signalled.");
                True(!coordinator.RequestCancellation("pipe-b", out var legacy) && legacy != null && !legacy.CanCancel,
                    "Legacy .py2 cancellation must be refused safely.");
                coordinator.Complete(first);
                coordinator.Complete(second!);
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
                File.WriteAllText(Path.Combine(workspace, "result.json"),
                    "{\"success\":true,\"cancelled\":false,\"stdout\":\"delayed\",\"stderr\":\"\"}");
                return 0;
            }
            if (pipeId == "test-start-success")
            {
                File.WriteAllText(Path.Combine(workspace, "started.json"), "{\"started\":true}");
                File.WriteAllText(Path.Combine(workspace, "result.json"),
                    "{\"success\":true,\"cancelled\":false,\"stdout\":\"started\",\"stderr\":\"\"}");
                return 0;
            }
            return 42;
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
