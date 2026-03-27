using System;
using System.Runtime.InteropServices;

public static class RealSenseInterop
{
    [DllImport("realsense2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr rs2_create_context(int api_version, out IntPtr error);

    [DllImport("realsense2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr rs2_create_pipeline(IntPtr context, out IntPtr error);

    [DllImport("realsense2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr rs2_pipeline_start(IntPtr pipeline, out IntPtr error);

    [DllImport("realsense2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr rs2_pipeline_profile_get_stream(IntPtr profile, int stream_type, int stream_index, out IntPtr error);

    [DllImport("realsense2.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern void rs2_get_video_stream_intrinsics(IntPtr mode, out Rs2Intrinsics intrinsics, out IntPtr error);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rs2Intrinsics
    {
        public int width;
        public int height;
        public float ppx;
        public float ppy;
        public float fx;
        public float fy;
        public int model;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
        public float[] coeffs;
    }
}
