"""Rhino Dock sample. Start Rhino 8, run StartScriptServer, then press Shift+F2 in VS."""
import Rhino
import scriptcontext as sc

point = Rhino.Geometry.Point3d(10, 20, 0)
sc.doc.Objects.AddPoint(point)
sc.doc.Views.Redraw()
print("Rhino Dock: Added point", point)
