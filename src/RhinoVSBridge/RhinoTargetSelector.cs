using System;
using System.Collections.Generic;
using System.Linq;

namespace RhinoVSBridge
{
    internal enum RhinoTargetResolutionKind
    {
        NoInstances,
        Automatic,
        ExistingSelection,
        SelectionRequired,
        SelectedInstanceUnavailable
    }

    internal sealed class RhinoTargetResolution
    {
        public RhinoTargetResolutionKind Kind { get; set; }
        public RhinoInstance? Instance { get; set; }
        public string? UnavailablePipeId { get; set; }
    }

    internal static class RhinoTargetSelector
    {
        public static RhinoTargetResolution Resolve(
            IReadOnlyList<RhinoInstance> instances,
            string? selectedPipeId)
        {
            if (instances == null) throw new ArgumentNullException(nameof(instances));
            if (instances.Count == 0)
                return new RhinoTargetResolution { Kind = RhinoTargetResolutionKind.NoInstances };

            if (!string.IsNullOrWhiteSpace(selectedPipeId))
            {
                var selected = instances.FirstOrDefault(instance => instance.PipeId == selectedPipeId);
                if (selected != null)
                {
                    return new RhinoTargetResolution
                    {
                        Kind = RhinoTargetResolutionKind.ExistingSelection,
                        Instance = selected
                    };
                }
                return new RhinoTargetResolution
                {
                    Kind = RhinoTargetResolutionKind.SelectedInstanceUnavailable,
                    UnavailablePipeId = selectedPipeId
                };
            }

            if (instances.Count == 1)
            {
                return new RhinoTargetResolution
                {
                    Kind = RhinoTargetResolutionKind.Automatic,
                    Instance = instances[0]
                };
            }
            return new RhinoTargetResolution { Kind = RhinoTargetResolutionKind.SelectionRequired };
        }
    }
}
