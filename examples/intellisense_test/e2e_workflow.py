"""End-to-end fixture. Run only with Rhino: Run Active Python Script (Shift+F2)."""

import System
import Rhino
import scriptcontext as sc


# During the save-before-run GUI test this value is edited without saving first.
TOKEN = "SAVED_VERSION"
POINT = Rhino.Geometry.Point3d(125.0, 250.0, 75.0)
OBJECT_NAME = "RhinoVSBridge E2E " + TOKEN

attributes = Rhino.DocObjects.ObjectAttributes()
attributes.Name = OBJECT_NAME
object_id = sc.doc.Objects.AddPoint(POINT, attributes)
if object_id == System.Guid.Empty:
    raise RuntimeError("Rhino did not add the end-to-end test point")

sc.doc.Views.Redraw()
print(
    "RHINOVSBRIDGE_E2E_SUCCESS "
    f"token={TOKEN} object_id={object_id} point=({POINT.X}, {POINT.Y}, {POINT.Z})"
)
