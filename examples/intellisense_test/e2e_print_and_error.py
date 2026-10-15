"""Output/traceback fixture. A RuntimeError is intentional."""

print("RHINOVSBRIDGE_STDOUT before intentional exception — Grüße 世界")
raise RuntimeError("RHINOVSBRIDGE_EXPECTED_ERROR — traceback must reach Visual Studio")
