using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace RhinoVSBridge
{
    internal static class Program
    {
        private static int _passed;
        private static int _failed;

        private static async Task<int> Main(string[] args)
        {
            if (args.Length == 3 && args[0] == "--rhino-integration")
            {
                var cli = new RhinoCli(@"C:\Program Files\Rhino 8\System\rhinocode.exe");
                var result = await cli.RunScriptAsync(Path.GetFullPath(args[2]), args[1]);
                Console.WriteLine(result.DisplayText);
                return result.Succeeded ? 0 : 1;
            }
            if (args.Length != 0)
            {
                Console.Error.WriteLine("Usage: RhinoVSBridge.CoreTests [--rhino-integration <pipe-id> <script.py>]");
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
                ("Python project update creates backup and preserves custom paths", TestProjectUpdate)
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
