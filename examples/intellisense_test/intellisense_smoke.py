"""Static-analysis fixture. Run in Rhino only, never with the selected VS interpreter."""

import Rhino
import rhinoscriptsyntax as rs
import scriptcontext as sc

from local_module import make_label


point = Rhino.Geometry.Point3d(1.0, 2.0, 3.0)
line_id = rs.AddLine(Rhino.Geometry.Point3d.Origin, point)
active_document = sc.doc
label = make_label(point.X, point.Y, point.Z)

# Completion probes in Visual Studio after configuring Search Paths:
#   Rhino.Geometry.        -> Point3d
#   Rhino.Geometry.Point3d. -> Origin, XAxis, YAxis, ZAxis
#   rs.                    -> AddLine
#   make_label             -> local_module signature
