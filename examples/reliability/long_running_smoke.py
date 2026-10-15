"""Finite long-run fixture. Adds no geometry and preserves a predictable result."""

import time


print("RHINOVSBRIDGE_LONG_RUN_STARTED", flush=True)
for index in range(6):
    time.sleep(0.5)
    print(f"RHINOVSBRIDGE_LONG_RUN_TICK={index}", flush=True)
print("RHINOVSBRIDGE_LONG_RUN_COMPLETED", flush=True)
