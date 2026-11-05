using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace RhinoVSBridge
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("Rhino Dock", "Rhino 8 CPython execution for Visual Studio", "0.6.0")]
    [ProvideMenuResource("RhinoVSBridge.CTMENU", 1)]
    [Guid(PackageGuid)]
    public sealed class BridgePackage : AsyncPackage
    {
        private const string PackageGuid = "e58ce92e-c24c-423e-aa69-8f5e51b71914";
        private static readonly TimeSpan ReconciliationPollInterval = TimeSpan.FromMilliseconds(500);
        private static readonly TimeSpan ReconciliationMonitorDuration = TimeSpan.FromMinutes(10);
        private static readonly Guid CommandSet = new Guid("55ec8c08-a567-4509-af25-8c2912f0516f");
        private readonly RhinoCli _cli = new RhinoCli();
        private readonly RhinoExecutionCoordinator _executions = new RhinoExecutionCoordinator();
        private string? _pipeId;
        private bool _running;
        private DTE2? _dte;
        private OutputWindowPane? _output;
        private RhinoErrorList? _errors;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var dte = await GetServiceAsync(typeof(DTE)) as DTE2;
            if (dte == null) return;
            _dte = dte;
            _errors = new RhinoErrorList(this);
            var commands = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commands == null) return;
            Register(commands, 0x0100, RunActiveAsync);
            Register(commands, 0x0101, SelectInstanceAsync);
            Register(commands, 0x0102, SetupIntelliSenseAsync);
            Register(commands, 0x0103, ShowDiagnosticsAsync);
            Register(commands, 0x0104, ChooseExecutableAsync);
            Register(commands, 0x0105, CancelActiveAsync);

            var removed = ExecutionWorkspace.CleanupCompleted(DateTime.UtcNow, TimeSpan.FromDays(1));
            var incomplete = ExecutionWorkspace.FindIncomplete();
            if (removed.Count > 0) await LogAsync("Removed " + removed.Count + " completed execution workspace(s).\r\n");
            if (incomplete.Count > 0)
                await LogAsync("Preserved " + incomplete.Count + " incomplete execution workspace(s) because Rhino may still use them. " +
                    "See " + ExecutionWorkspace.DefaultRoot + "\r\n");
        }

        private void Register(OleMenuCommandService commands, int id, Func<Task> action)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            commands.AddCommand(new MenuCommand((sender, e) =>
            {
                // Use VS's joinable task scheduler; all action exceptions are handled centrally.
                _ = JoinableTaskFactory.RunAsync(async () =>
                {
                    try { await action(); }
                    catch (Exception ex)
                    {
                        await LogAsync("ERROR: " + ex.Message + Environment.NewLine);
                        await JoinableTaskFactory.SwitchToMainThreadAsync();
                        MessageBox.Show(ex.Message, "Rhino Dock", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                });
            }, new CommandID(CommandSet, id)));
        }

        private async Task RunActiveAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_running)
            {
                await LogAsync("A Rhino script command is currently running. Wait for completion or request cancellation.\r\n");
                MessageBox.Show("A Rhino script is currently running. Wait for completion or request cancellation.",
                    "Rhino Dock");
                return;
            }
            if (_dte?.ActiveDocument == null) { MessageBox.Show("Open a .py file first."); return; }
            var document = _dte.ActiveDocument;
            var hadUnsavedChanges = !document.Saved;
            document.Save();
            var file = document.FullName;
            if (string.IsNullOrWhiteSpace(file)) { MessageBox.Show("Save your .py file before running it."); return; }
            if (!string.Equals(Path.GetExtension(file), ".py", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Path.GetExtension(file), ".py2", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("The active document must be a Rhino Python .py or .py2 script.", "Rhino Dock");
                return;
            }
            if (!File.Exists(file)) { MessageBox.Show("The active script could not be saved.", "Rhino Dock"); return; }

            _running = true;
            try
            {
                if (hadUnsavedChanges) await LogAsync("Saved unsaved changes: " + file + "\r\n");
                var instances = await _cli.ListInstancesAsync();
                var resolution = RhinoTargetSelector.Resolve(instances, _pipeId);
                if (resolution.Kind == RhinoTargetResolutionKind.NoInstances)
                {
                    _pipeId = null;
                    throw new InvalidOperationException("No Rhino script server found. Run StartScriptServer in Rhino 8 first.");
                }
                if (resolution.Kind == RhinoTargetResolutionKind.SelectedInstanceUnavailable)
                {
                    var unavailable = resolution.UnavailablePipeId;
                    _pipeId = null;
                    throw new InvalidOperationException(
                        "The previously selected Rhino instance (" + unavailable + ") is no longer available. " +
                        "No script was sent. Use 'Rhino: Select Instance' before running again.");
                }
                RhinoInstance? target = resolution.Instance;
                if (resolution.Kind == RhinoTargetResolutionKind.SelectionRequired)
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    var chosenPipe = ChooseInstance(instances);
                    if (chosenPipe == null) return;
                    target = instances.First(instance => instance.PipeId == chosenPipe);
                }
                if (target == null) return;
                var pipeId = target.PipeId;
                _pipeId = pipeId;
                var canCancel = string.Equals(Path.GetExtension(file), ".py", StringComparison.OrdinalIgnoreCase);
                var existing = _executions.Find(pipeId);
                if (existing != null)
                {
                    var reconciliation = _executions.TryReconcile(existing);
                    if (reconciliation.Status == ExecutionReconciliationStatus.Completed)
                        await ReportReconciledExecutionAsync(reconciliation);
                }
                if (!_executions.TryStart(pipeId, file, canCancel, out var execution) || execution == null)
                {
                    var blocking = _executions.Find(pipeId);
                    var message = DescribeBlockingExecution(blocking);
                    await LogAsync(message + "\r\n");
                    throw new InvalidOperationException(message);
                }
                await ClearPythonErrorsAsync();
                var executionFinalized = false;
                try
                {
                    await LogAsync($"\r\n=== Running {file} ===\r\nTarget: {target}\r\n" +
                        (canCancel ? "Use Tools > Rhino: Cancel Running Script to request cooperative cancellation.\r\n" :
                        "Legacy .py2 execution cannot be cancelled safely by Rhino Dock.\r\n"));
                    var result = await _cli.RunScriptAsync(file, pipeId,
                        output => LogAsync(output.Text), execution.Cancellation.Token);

                    if (result.DispatchFailed)
                    {
                        executionFinalized = true;
                        await LogAsync(result.DisplayText + "\r\n");
                        throw new InvalidOperationException(
                            "RhinoCode could not dispatch the script. No Rhino-side runner started, so it is safe to try again. " +
                            "See the Rhino Dock output for details.");
                    }
                    if (result.ExecutionInterrupted)
                    {
                        await LogAsync(result.DisplayText + "\r\n");
                        if (!_executions.MarkUncertain(execution, result.PreservedWorkspace,
                            result.ExecutionSessionId, result.StartupTimedOut))
                            throw new InvalidOperationException(
                                "The execution became uncertain, but its workspace identity could not be retained safely.");
                        BeginReconciliationMonitor(execution);
                        if (!await IsInstanceAvailableAsync(pipeId)) _pipeId = null;
                        if (result.StartupTimedOut)
                            throw new InvalidOperationException(
                                "Rhino did not acknowledge starting the Python runner before the startup timeout. " +
                                "The request may still start later, so another script will not be sent to this Rhino instance. " +
                                "Execution files were preserved at: " + result.PreservedWorkspace);
                        throw new InvalidOperationException(
                            "The RhinoCode transport ended before Rhino returned a final script result. " +
                            "The script may still be running, so its execution files were preserved at: " +
                            result.PreservedWorkspace);
                    }
                    executionFinalized = true;

                    if (!canCancel) await LogAsync(result.DisplayText + "\r\n");
                    else await LogAsync("[rhinocode exit code: " + result.ExitCode + "]\r\n");

                    if (result.Cancelled)
                    {
                        await LogAsync("=== Cancellation acknowledged inside Rhino ===\r\n");
                        return;
                    }
                    if (!result.Succeeded)
                    {
                        await PublishPythonErrorsAsync(result.StandardError);
                        if (!await IsInstanceAvailableAsync(pipeId))
                        {
                            _pipeId = null;
                            throw new InvalidOperationException(
                                "The selected Rhino ScriptServer connection was lost. Restart ScriptServer and select the instance again.");
                        }
                        throw new InvalidOperationException(
                            "Rhino reported that the script failed. See the Python exception and traceback above in the Rhino Dock output.");
                    }
                    await LogAsync("=== Script completed successfully ===\r\n");
                }
                finally
                {
                    if (executionFinalized) _executions.Complete(execution);
                }
            }
            finally { _running = false; }
        }

        private async Task CancelActiveAsync()
        {
            var cancellation = _executions.RequestCancellation(_pipeId);
            var execution = cancellation.Execution;
            if (cancellation.Status == CancellationRequestStatus.NoExecution)
            {
                await LogAsync("No Rhino Dock execution is active for the selected Rhino instance; the slot is ready.\r\n");
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                MessageBox.Show("No Rhino script started by this extension is currently running.",
                    "Rhino Dock", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (cancellation.Status == CancellationRequestStatus.AlreadyCompleted)
            {
                if (cancellation.Reconciliation?.Status == ExecutionReconciliationStatus.Completed)
                    await ReportReconciledExecutionAsync(cancellation.Reconciliation);
                else
                    await LogAsync("The execution completed while cancellation was being requested. " +
                        "No cancellation marker was retained; the slot is ready.\r\n");
                return;
            }
            if (cancellation.Status == CancellationRequestStatus.CannotCancel && execution != null)
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                MessageBox.Show("The active .py2 script cannot be cancelled safely. " +
                    "Rhino Dock will not terminate rhinocode.exe because that does not prove execution stopped inside Rhino.",
                    "Rhino Dock", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (cancellation.Status == CancellationRequestStatus.Requested && execution != null)
            {
                var detail = execution.State == RhinoExecutionState.StartupNotAcknowledged
                    ? " The runner has not started yet; the cancellation marker will be checked before user code if startup is delayed."
                    : execution.State == RhinoExecutionState.Uncertain
                        ? " Execution state is uncertain; the slot remains blocked until a terminal result is verified."
                        : " Waiting for Rhino CPython to reach a safe Python trace point.";
                await LogAsync("Cancellation requested for " + execution.ScriptPath + " in " + execution.PipeId +
                    "." + detail + "\r\n");
            }
        }

        private void BeginReconciliationMonitor(ActiveRhinoExecution execution)
        {
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    var reconciliation = await _executions.MonitorUncertainAsync(execution,
                        ReconciliationPollInterval, ReconciliationMonitorDuration, DisposalToken);
                    if (reconciliation.Status == ExecutionReconciliationStatus.Completed)
                    {
                        await ReportReconciledExecutionAsync(reconciliation);
                    }
                    else if (reconciliation.Status == ExecutionReconciliationStatus.Pending)
                    {
                        await LogAsync("Automatic reconciliation stopped after " +
                            ReconciliationMonitorDuration.TotalMinutes.ToString("0") +
                            " minutes. The execution is still uncertain and remains blocked. " +
                            "Last completion artifact state: " + reconciliation.ArtifactStatus + ". " +
                            "Run or Cancel will recheck its exact completion artifact on demand.\r\n");
                    }
                }
                catch (OperationCanceledException) when (DisposalToken.IsCancellationRequested)
                {
                    // Package shutdown stops bounded reconciliation without touching execution state.
                }
                catch (Exception ex)
                {
                    await LogAsync("Automatic execution reconciliation failed safely; the slot remains blocked: " +
                        ex.Message + "\r\n");
                }
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _executions.Dispose();
                if (_errors != null)
                {
                    JoinableTaskFactory.Run(async () =>
                    {
                        await JoinableTaskFactory.SwitchToMainThreadAsync();
                        _errors.Dispose();
                    });
                }
            }
            base.Dispose(disposing);
        }

        private async Task ReportReconciledExecutionAsync(ExecutionReconciliation reconciliation)
        {
            var execution = reconciliation.Execution;
            var result = reconciliation.Result;
            if (execution == null || result == null) return;

            var origin = execution.State == RhinoExecutionState.StartupNotAcknowledged
                ? "startup timeout"
                : "uncertain connection state";
            await LogAsync("\r\n=== Execution completed after " + origin + " ===\r\n" +
                "Script: " + execution.ScriptPath + "\r\nRhino: " + execution.PipeId + "\r\n");
            if (!string.IsNullOrEmpty(result.StandardOutput)) await LogAsync(result.StandardOutput);
            if (!string.IsNullOrEmpty(result.StandardError)) await LogAsync(result.StandardError);
            if (result.Cancelled)
                await LogAsync("\r\nLate execution acknowledged cancellation.\r\n");
            else if (result.Success)
                await LogAsync("\r\nLate execution completed successfully.\r\n");
            else
            {
                await PublishPythonErrorsAsync(result.StandardError);
                await LogAsync("\r\nLate execution completed with a Python failure.\r\n");
            }
            await LogAsync("Execution slot released and ready for another run.\r\n");

            if (!string.IsNullOrWhiteSpace(reconciliation.Workspace) &&
                !ExecutionWorkspace.TryDeleteCompleted(reconciliation.Workspace!))
                await LogAsync("Completed execution files could not be removed and remain at: " +
                    reconciliation.Workspace + "\r\n");
        }

        private static string DescribeBlockingExecution(ActiveRhinoExecution? execution)
        {
            if (execution == null)
                return "The selected Rhino execution slot changed concurrently. Try again.";
            if (execution.State == RhinoExecutionState.StartupNotAcknowledged)
                return "The previous script startup was not acknowledged and its execution state is still uncertain. " +
                    "The selected Rhino instance remains blocked until a terminal result is verified.";
            if (execution.State == RhinoExecutionState.Uncertain)
                return "The previous script has an uncertain execution state after a transport interruption. " +
                    "The selected Rhino instance remains blocked until a terminal result is verified.";
            return "A script is currently running in the selected Rhino instance.";
        }

        private async Task<bool> IsInstanceAvailableAsync(string pipeId)
        {
            try { return (await _cli.ListInstancesAsync()).Any(x => x.PipeId == pipeId); }
            catch { return false; }
        }

        private async Task SelectInstanceAsync()
        {
            var instances = await _cli.ListInstancesAsync();
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            if (instances.Count == 0)
            {
                MessageBox.Show("No Rhino instances found. Run StartScriptServer in Rhino 8.", "Rhino Dock");
                return;
            }
            var selected = ChooseInstance(instances);
            if (selected != null)
            {
                _pipeId = selected;
                await LogAsync("Selected Rhino: " + _pipeId + "\r\n");
            }
        }

        private static string? ChooseInstance(IReadOnlyList<RhinoInstance> instances)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            using (var dialog = new Form
            {
                Text = "Select Rhino 8 Instance",
                StartPosition = FormStartPosition.CenterParent,
                Width = 760,
                Height = 210,
                MinimizeBox = false,
                MaximizeBox = false,
                FormBorderStyle = FormBorderStyle.FixedDialog
            })
            {
                var picker = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Left = 18, Top = 35, Width = 710
                };
                picker.Items.AddRange(instances.Cast<object>().ToArray());
                picker.SelectedIndex = 0;
                var label = new Label { Left = 18, Top = 12, AutoSize = true, Text = "Choose the Rhino window that should execute scripts:" };
                var ok = new Button { Text = "Use Selected Instance", DialogResult = DialogResult.OK, Left = 495, Top = 93, Width = 155 };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 657, Top = 93, Width = 70 };
                dialog.Controls.AddRange(new Control[] { label, picker, ok, cancel });
                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;
                return dialog.ShowDialog() == DialogResult.OK ? (picker.SelectedItem as RhinoInstance)?.PipeId : null;
            }
        }

        private async Task SetupIntelliSenseAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var paths = IntelliSensePaths.Discover();
            if (paths.Count == 0)
            {
                MessageBox.Show("No Rhino 8 Python stubs found. Open Rhino's ScriptEditor once, then try again.",
                    "Rhino Dock", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var project = _dte?.ActiveDocument?.ProjectItem?.ContainingProject;
            if (project == null && _dte?.Solution?.Projects?.Count > 0)
                project = _dte.Solution.Projects.Item(1);
            var projectFile = project?.FullName;
            if (projectFile == null || string.IsNullOrWhiteSpace(projectFile) ||
                !projectFile.EndsWith(".pyproj", StringComparison.OrdinalIgnoreCase))
            {
                var list = string.Join(Environment.NewLine, paths);
                Clipboard.SetText(list);
                MessageBox.Show("No .pyproj selected. Rhino search paths were copied to clipboard. " +
                    "Open a Python project and add them under Search Paths.\r\n\r\n" + list,
                    "Rhino Dock", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var confirmation = MessageBox.Show(
                "Configure Rhino 8 CPython IntelliSense for:\r\n" + projectFile +
                "\r\n\r\nA .rhino-dock.bak backup (with a numbered suffix if one exists) will be made first. " +
                "The command updates the discovered Rhino paths, removes obsolete paths managed by Rhino Dock, and adds a project-local " +
                "scriptcontext typing shim. Close and reopen the solution afterward.",
                "Rhino Dock", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (confirmation != DialogResult.OK) return;
            var result = IntelliSensePaths.ConfigurePythonProject(projectFile, paths);
            MessageBox.Show("Added " + result.AddedPaths + " and removed " + result.RemovedPaths +
                " obsolete Rhino Python search paths. " +
                (result.CompatibilityStubWritten ? "Created the scriptcontext typing shim. " : string.Empty) +
                "Close and reopen the solution, then check RhinoCommon, rhinoscriptsyntax, " +
                "scriptcontext, and local-module completion.",
                "Rhino Dock", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private async Task ShowDiagnosticsAsync()
        {
            await LogAsync("\r\n== Rhino Dock diagnostics ==\r\n");
            await LogAsync("RhinoCode CLI: " + _cli.Executable + "\r\n");
            foreach (var path in IntelliSensePaths.Discover())
                await LogAsync("Python search path: " + path + "\r\n");
            var instances = await _cli.ListInstancesAsync();
            foreach (var instance in instances)
                await LogAsync(instance + "\r\n");
            await LogAsync(instances.Count + " Rhino instance(s) found.\r\n");
        }

        private async Task ChooseExecutableAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            using (var dialog = new OpenFileDialog
            {
                Title = "Locate Rhino 8 rhinocode.exe",
                Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
                CheckFileExists = true,
                FileName = "rhinocode.exe"
            })
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    _cli.Executable = dialog.FileName;
                    await LogAsync("RhinoCode CLI: " + _cli.Executable + "\r\n");
                }
            }
        }

        private async Task LogAsync(string message)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_dte == null) return;
            if (_output == null)
            {
                var panes = _dte.ToolWindows.OutputWindow.OutputWindowPanes;
                _output = GetOrCreateOutputPane(panes);
            }
            _output.OutputString(message);
            _output.Activate();
        }

        private async Task PublishPythonErrorsAsync(string tracebackText)
        {
            var traceback = PythonTracebackParser.Parse(tracebackText);
            if (traceback.UserFacingFrames.Count == 0) return;

            try
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var count = _errors?.Publish(traceback) ?? 0;
                if (count > 0)
                    await LogAsync("Published " + count +
                        " navigable Python traceback location(s) to Error List > Rhino Dock.\r\n");
            }
            catch (Exception ex)
            {
                await LogAsync("Python traceback navigation could not be published; " +
                    "the complete traceback remains above: " + ex.Message + "\r\n");
            }
        }

        private async Task ClearPythonErrorsAsync()
        {
            try
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                _errors?.Clear();
            }
            catch (Exception ex)
            {
                await LogAsync("Stale Rhino Dock Error List entries could not be cleared: " +
                    ex.Message + "\r\n");
            }
        }

        private static OutputWindowPane GetOrCreateOutputPane(OutputWindowPanes panes)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            for (var index = 1; index <= panes.Count; index++)
            {
                var pane = panes.Item(index);
                if (pane.Name == "Rhino Dock") return pane;
            }
            return panes.Add("Rhino Dock");
        }
    }
}
