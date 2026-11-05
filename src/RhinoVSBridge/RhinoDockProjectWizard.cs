using System;
using System.Collections.Generic;
using System.Security;
using EnvDTE;
using Microsoft.VisualStudio.TemplateWizard;

namespace RhinoVSBridge
{
    /// <summary>
    /// Substitutes machine-specific Rhino 8 typing paths before the new Python
    /// project is loaded by PTVS. No project-file rewrite or reload is required.
    /// </summary>
    public sealed class RhinoDockProjectWizard : IWizard
    {
        public void RunStarted(object automationObject, Dictionary<string, string> replacementsDictionary,
            WizardRunKind runKind, object[] customParams)
        {
            var searchPaths = new List<string> { IntelliSensePaths.CompatibilityDirectory };
            searchPaths.AddRange(IntelliSensePaths.Discover());
            // Values are inserted into XML, so escape paths such as "R&D".
            replacementsDictionary["$rhinodocksearchpaths$"] =
                SecurityElement.Escape(string.Join(";", searchPaths)) ?? string.Empty;
        }

        public void ProjectFinishedGenerating(Project project)
        {
            if (project == null || string.IsNullOrWhiteSpace(project.FullName))
                return;
            // The generated local shim makes scriptcontext.doc available in Pylance.
            // This does not modify Rhino's own Python installation.
            IntelliSensePaths.EnsureCompatibilityStubs(project.FullName);
        }

        public void ProjectItemFinishedGenerating(ProjectItem projectItem) { }
        public void BeforeOpeningFile(ProjectItem projectItem) { }
        public void RunFinished() { }
        public bool ShouldAddProjectItem(string filePath) => true;
    }
}
