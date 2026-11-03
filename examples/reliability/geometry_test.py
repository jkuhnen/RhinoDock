import Rhino

doc = Rhino.RhinoDoc.ActiveDoc

point = Rhino.Geometry.Point3d(10, 20, 0)
object_id = doc.Objects.AddPoint(point)

doc.Views.Redraw()

print("Point created:", object_id)
print("Object count:", doc.Objects.Count)
