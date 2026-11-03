import Rhino
import scriptcontext as sc

print("Rhino version:", Rhino.RhinoApp.Version)
print("Document:", sc.doc.Name)

raise RuntimeError("RhinoVSBridge error handling test")
