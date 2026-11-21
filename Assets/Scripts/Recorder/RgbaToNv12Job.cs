using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

[BurstCompile]
public struct RgbaToNv12Job : IJobParallelFor
{
    [ReadOnly] public NativeArray<byte> rgba;   // RGBA32
    [WriteOnly] public NativeArray<byte> nv12;  // YUV420 NV12

    public int width;
    public int height;

    public void Execute(int index)
    {
        int x = index % width;
        int y = index / width;

        // ===== 垂直翻转 =====
        int flippedY = height - 1 - y;

        int rgbaIndex = index * 4;

        byte r = rgba[rgbaIndex + 0];
        byte g = rgba[rgbaIndex + 1];
        byte b = rgba[rgbaIndex + 2];

        // ===== 写 Y 分量 =====
        int yIndex = flippedY * width + x;
        int yValue = (int)(0.299f * r + 0.587f * g + 0.114f * b);
        nv12[yIndex] = (byte)math.clamp(yValue, 0, 255);

        // ===== 写 UV 分量 (2x2 采样，只在偶数行偶数列写) =====
        if ((x & 1) == 0 && (y & 1) == 0)
        {
            int uvRow = flippedY / 2;
            int uvCol = x;
            int uvIndex = width * height + uvRow * width + uvCol;

            // 将 U、V 写在交错位置 (UV UV UV...)
            int u = (int)(-0.169f * r - 0.331f * g + 0.5f * b + 128);
            int v = (int)( 0.5f * r - 0.419f * g - 0.081f * b + 128);

            nv12[uvIndex + 0] = (byte)math.clamp(u, 0, 255);
            nv12[uvIndex + 1] = (byte)math.clamp(v, 0, 255);
        }
    }
}