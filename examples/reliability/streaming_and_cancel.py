"""GUI fixture for live output and cooperative cancellation. Adds no geometry."""

import time


print("RHINOVSBRIDGE_STREAM_STARTED", flush=True)
for index in range(600):
    print(f"RHINOVSBRIDGE_STREAM_TICK={index}", flush=True)
    time.sleep(0.1)
print("RHINOVSBRIDGE_STREAM_COMPLETED", flush=True)
