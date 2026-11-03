"""Read-only verifier for geometry created by e2e_workflow.py."""

import scriptcontext as sc


PREFIX = "RhinoVSBridge E2E "
matches = []
for rhino_object in sc.doc.Objects:
    name = rhino_object.Attributes.Name or ""
    if name.startswith(PREFIX):
        point = rhino_object.Geometry.Location
        matches.append((name, point.X, point.Y, point.Z))

print(f"RHINOVSBRIDGE_E2E_OBJECT_COUNT={len(matches)}")
for match in matches:
    print(f"RHINOVSBRIDGE_E2E_OBJECT={match}")
