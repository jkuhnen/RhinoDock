"""Visual Studio Pylance probe. Execute Rhino code only through Rhino VS Bridge."""

import Rhino
import Rhino.Geometry as rg
import rhinoscriptsyntax as rs
import scriptcontext as sc

from local_module import make_label


rhino_point = Rhino.Geometry.Point3d(1.0, 2.0, 3.0)
geometry_point = rg.Point3d(4.0, 5.0, 6.0)
line_id = rs.AddLine(rg.Point3d.Origin, geometry_point)
active_document = sc.doc
label = make_label(geometry_point.X, geometry_point.Y, geometry_point.Z)

# Manual VS 2026 verification points:
# - type `Rhino.` and expect Geometry
# - type `rg.` and expect Point3d
# - type `rg.Point3d.` and expect Origin
# - type `rs.` and expect AddLine
# - type `sc.` and expect doc
# - hover Point3d and AddLine; invoke parameter info at their calls
# - Go to Definition on Point3d, AddLine, doc, and make_label
