"""Rhino 8 / CPython 3.9 smoke test. Execute from Visual Studio via Rhino VS Bridge."""
import rhinoscriptsyntax as rs
import Rhino

point_id = rs.AddPoint(0, 0, 0)
rs.Redraw()
print("Hello from Visual Studio. Point: {}. Rhino: {}".format(point_id, Rhino.RhinoApp.Version))
