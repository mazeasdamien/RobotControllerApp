using System;
using System.Runtime.InteropServices;

namespace RobotHub.Services
{
    /// <summary>
    /// Reads Intel RealSense D-series color camera intrinsics via P/Invoke into librealsense2.dll.
    /// Result is cached after the first successful read — the camera is only opened once per process.
    /// Returns null if the camera is not connected or the driver is unavailable.
    /// </summary>
    public static class RealSenseIntrinsics
    {
        public const int RS2_API_VERSION = 11201;
        public const int RS2_STREAM_COLOR = 2;

        [StructLayout(LayoutKind.Sequential)]
        public struct Rs2Intrinsics
        {
            public int width;
            public int height;
            public float ppx;   // principal point x
            public float ppy;   // principal point y
            public float fx;    // focal length x
            public float fy;    // focal length y
            public int model;   // rs2_distortion enum
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
            public float[] coeffs;
        }

        [DllImport("realsense2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr rs2_create_context(int api_version, out IntPtr error);

        [DllImport("realsense2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr rs2_create_pipeline(IntPtr context, out IntPtr error);

        [DllImport("realsense2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr rs2_pipeline_start(IntPtr pipeline, out IntPtr error);

        [DllImport("realsense2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr rs2_pipeline_profile_get_stream(IntPtr profile, int stream_type, int stream_index, out IntPtr error);

        [DllImport("realsense2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void rs2_get_video_stream_intrinsics(IntPtr mode, out Rs2Intrinsics intrinsics, out IntPtr error);

        [DllImport("realsense2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void rs2_pipeline_stop(IntPtr pipeline, out IntPtr error);

        [DllImport("realsense2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void rs2_delete_context(IntPtr context);

        [DllImport("realsense2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void rs2_delete_pipeline(IntPtr pipeline);

        [DllImport("realsense2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void rs2_delete_pipeline_profile(IntPtr profile);

        [DllImport("realsense2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void rs2_delete_stream_profile(IntPtr profile);

        [DllImport("realsense2.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int rs2_get_api_version(out IntPtr error);

        private static Rs2Intrinsics? _cachedIntrinsics;
        private static bool _hasAttemptedFetch;
        private static readonly object _lock = new();

        /// <summary>
        /// Opens the first connected RealSense device, reads the color stream intrinsics,
        /// then immediately stops the pipeline. Result is cached for the process lifetime.
        /// </summary>
        public static Rs2Intrinsics? GetColorIntrinsics()
        {
            lock (_lock)
            {
                if (_hasAttemptedFetch) return _cachedIntrinsics;
                _hasAttemptedFetch = true;
            }

            IntPtr ctx = IntPtr.Zero, pipe = IntPtr.Zero, prof = IntPtr.Zero, stream = IntPtr.Zero;
            try
            {
                int apiVersion = rs2_get_api_version(out IntPtr err);
                if (err != IntPtr.Zero) return null;

                ctx = rs2_create_context(apiVersion, out err);
                if (ctx == IntPtr.Zero) return null;

                pipe = rs2_create_pipeline(ctx, out err);
                if (pipe == IntPtr.Zero) return null;

                prof = rs2_pipeline_start(pipe, out err);
                if (prof == IntPtr.Zero) return null;

                stream = rs2_pipeline_profile_get_stream(prof, RS2_STREAM_COLOR, 0, out err);
                if (stream == IntPtr.Zero) return null;

                rs2_get_video_stream_intrinsics(stream, out Rs2Intrinsics intr, out _);
                rs2_pipeline_stop(pipe, out _);

                _cachedIntrinsics = intr;
                return intr;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (stream != IntPtr.Zero) rs2_delete_stream_profile(stream);
                if (prof != IntPtr.Zero) rs2_delete_pipeline_profile(prof);
                if (pipe != IntPtr.Zero) rs2_delete_pipeline(pipe);
                if (ctx != IntPtr.Zero) rs2_delete_context(ctx);
            }
        }
    }
}
