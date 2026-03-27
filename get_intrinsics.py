import sys
import os
import json

sdk_path = r'C:\Users\QYTH4815\Documents\RealSense SDK 2.0\bin\x64'
sys.path.append(sdk_path)
os.environ['PATH'] = sdk_path + os.pathsep + os.environ['PATH']

try:
    import pyrealsense2 as rs
    pipeline = rs.pipeline()
    cfg = pipeline.start()
    profile = cfg.get_stream(rs.stream.color)
    intr = profile.as_video_stream_profile().get_intrinsics()
    pipeline.stop()
    print(json.dumps({'fx': intr.fx, 'fy': intr.fy, 'cx': intr.ppx, 'cy': intr.ppy, 'width': intr.width, 'height': intr.height}))
except Exception as e:
    print('ERROR:', e)
