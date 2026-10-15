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
    [InstalledProductRegistration("Rhino VS Bridge", "Rhino 8 CPython execution for Visual Studio", "0.2.3")]
    [ProvideMenuResource("RhinoVSBridge.CTMENU", 1)]
    [Guid(PackageGuid)]
    public sealed class BridgePackage : AsyncPackage
    {
        private const string PackageGuid = "e58ce92e-c24c-423e-aa69-8f5e51b71914";
        private static readonly Guid CommandSet = new Guid("55ec8c08-a567-4509-af25-8c2912f0516f");
        private readonly RhinoCli _cli = new RhinoCli();
        private string? _pipeId;
        private bool _running;
        private DTE2? _dte;
        private OutputWindowPane? _output;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            var dte = await GetServiceAsync(typeof(DTE)) as DTE2;
            if (dte == null) return;
            _dte = dte;
            var commands = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commands == null) return;
            Register(commands, 0x0100, RunActiveAsync);
            Register(commands, 0x0101, SelectInstanceAsync);
            Register(commands, 0x0102, SetupIntelliSenseAsync);
            Register(commands, 0x0103, ShowDiagnosticsAsync);
            Register(commands, 0x0104, ChooseExecutableAsync);
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
                        MessageBox.Show(ex.Message, "Rhino VS Bridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                });
            }, new CommandID(CommandSet, id)));
        }

        private async Task RunActiveAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_running) { MessageBox.Show("A script is already running.", "Rhino VS Bridge"); return; }
            if (_dte?.ActiveDocument == null) { MessageBox.Show("Open a .py file first."); return; }
            var document = _dte.ActiveDocument;
            var hadUnsavedChanges = !document.Saved;
            document.Save();
            var file = document.FullName;
            if (string.IsNullOrWhiteSpace(file)) { MessageBox.Show("Save your .py file before running it."); return; }
            if (!string.Equals(Path.GetExtension(file), ".py", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Path.GetExtension(file), ".py2", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("The active document must be a Rhino Python .py or .py2 script.", "Rhino VS Bridge");
                return;
            }
            if (!File.Exists(file)) { MessageBox.Show("The active script could not be saved.", "Rhino VS Bridge"); return; }

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
                await LogAsync($"\r\n=== Running {file} ===\r\nTarget: {target}\r\n");
                CommandResult result;
                try
                {
                    result = await _cli.RunScriptAsync(file, pipeId);
                }
                catch (TimeoutException)
                {
                    _pipeId = null;
                    throw;
                }
                await LogAsync(result.DisplayText + "\r\n");
                if (!result.Succeeded)
                {
                    if (!await IsInstanceAvailableAsync(pipeId))
                    {
                        _pipeId = null;
                        throw new InvalidOperationException(
                            "The selected Rhino ScriptServer connection was lost. Restart ScriptServer and select the instance again.");
                    }
                    throw new InvalidOperationException(
                        "Rhino reported that the script failed. See the Python exception and traceback above in the Rhino VS Bridge output.");
                }
                await LogAsync("=== Script completed successfully ===\r\n");
            }
            finally { _running = false; }
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
                MessageBox.Show("No Rhino instances found. Run StartScriptServer in Rhino 8.", "Rhino VS Bridge");
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
                    "Rhino VS Bridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
                    "Rhino VS Bridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var confirmation = MessageBox.Show(
                "Configure Rhino 8 CPython IntelliSense for:\r\n" + projectFile +
                "\r\n\r\nA .rhino-bridge.bak backup will be made first. " +
                "The command updates the discovered Rhino paths, removes obsolete paths managed by the bridge, and adds a project-local " +
                "scriptcontext typing shim. Close and reopen the solution afterward.",
                "Rhino VS Bridge", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (confirmation != DialogResult.OK) return;
            var result = IntelliSensePaths.ConfigurePythonProject(projectFile, paths);
            MessageBox.Show("Added " + result.AddedPaths + " and removed " + result.RemovedPaths +
                " obsolete Rhino Python search paths. " +
                (result.CompatibilityStubWritten ? "Created the scriptcontext typing shim. " : string.Empty) +
                "Close and reopen the solution, then check RhinoCommon, rhinoscriptsyntax, " +
                "scriptcontext, and local-module completion.",
                "Rhino VS Bridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private async Task ShowDiagnosticsAsync()
        {
            await LogAsync("\r\n== Rhino VS Bridge diagnostics ==\r\n");
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

        private static OutputWindowPane GetOrCreateOutputPane(OutputWindowPanes panes)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            for (var index = 1; index <= panes.Count; index++)
            {
                var pane = panes.Item(index);
                if (pane.Name == "Rhino VS Bridge") return pane;
            }
            return panes.Add("Rhino VS Bridge");
        }
    }
}
