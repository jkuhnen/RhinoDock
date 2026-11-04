using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;

namespace RhinoVSBridge
{
    internal sealed class RhinoErrorList : IDisposable
    {
        private static readonly Guid ProviderId =
            new Guid("2fbd1c4d-8b9c-4c42-9c22-fb73a15621c7");
        private readonly ErrorListProvider _provider;

        public RhinoErrorList(IServiceProvider serviceProvider)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _provider = new ErrorListProvider(serviceProvider)
            {
                ProviderGuid = ProviderId,
                ProviderName = "Rhino Dock"
            };
        }

        public void Clear()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _provider.Tasks.Clear();
            _provider.Refresh();
        }

        public int Publish(PythonTraceback traceback)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (traceback == null) throw new ArgumentNullException(nameof(traceback));

            var count = 0;
            foreach (var frame in traceback.UserFacingFrames)
            {
                var task = new ErrorTask
                {
                    ErrorCategory = TaskErrorCategory.Error,
                    Category = TaskCategory.User,
                    Text = "Rhino Dock: " + traceback.ExceptionMessage,
                    Document = frame.FilePath,
                    Line = frame.VisualStudioLineNumber,
                    Column = 0
                };
                task.Navigate += Navigate;
                _provider.Tasks.Add(task);
                count++;
            }

            if (count > 0)
            {
                _provider.Refresh();
                _provider.ForceShowErrors();
                _provider.Show();
            }
            return count;
        }

        private void Navigate(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (sender is ErrorTask task)
            {
                // Error List tasks use a zero-based Line for display, but
                // TaskProvider.Navigate subtracts one from positive line values.
                // Navigate with a separate one-based task so the displayed task
                // remains unchanged and both surfaces identify the same line.
                var navigationTask = new ErrorTask
                {
                    Document = task.Document,
                    Line = ErrorListNavigation.ToTaskProviderLine(task.Line),
                    Column = task.Column
                };
                _provider.Navigate(navigationTask, VSConstants.LOGVIEWID_Code);
            }
        }

        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _provider.Dispose();
        }
    }
}
