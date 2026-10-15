"""Run inside Rhino after examples/hello_rhino.py; fails unless it added one point."""

import Rhino


points = [
    obj
    for obj in Rhino.RhinoDoc.ActiveDoc.Objects
    if obj.ObjectType == Rhino.DocObjects.ObjectType.Point
]

if len(points) != 1:
    raise AssertionError("Expected exactly one point object, found {}".format(len(points)))
