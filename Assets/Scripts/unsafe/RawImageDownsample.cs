using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

public static class RawImageDownsample
{
    public static unsafe void Downsample2x_TopLeft_RGBA32(
        NativeArray<byte> src,
        int srcWidth,
        int srcHeight,
        NativeArray<byte> dst
    )
    {
        int dstWidth  = srcWidth  >> 1;
        int dstHeight = srcHeight >> 1;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        // 可选的安全检查（Editor / Development）
        if (dst.Length < dstWidth * dstHeight * 4)
            throw new System.ArgumentException("dst NativeArray is too small");
#endif

        uint* srcU = (uint*)src.GetUnsafeReadOnlyPtr();
        uint* dstU = (uint*)dst.GetUnsafePtr();

        int srcW = srcWidth;
        int dstW = dstWidth;

        for (int y = 0; y < dstHeight; y++)
        {
            int srcRow = (y << 1) * srcW;
            int dstRow = y * dstW;

            for (int x = 0; x < dstW; x++)
            {
                // 只取左上角
                dstU[dstRow + x] = srcU[srcRow + (x << 1)];
            }
        }
    }
    
    public static void Downsample2x_TopLeft_RGBA32_safe(
        NativeArray<byte> src,
        int srcWidth,
        int srcHeight,
        NativeArray<byte> dst
    )
    {
        int dstWidth  = srcWidth  >> 1;
        int dstHeight = srcHeight >> 1;

        // ----------- 参数校验（强烈建议保留）-----------
        if (src.Length < srcWidth * srcHeight * 4)
            throw new System.ArgumentException("src NativeArray size mismatch");

        if (dst.Length < dstWidth * dstHeight * 4)
            throw new System.ArgumentException("dst NativeArray is too small");

        // ------------------------------------------------

        for (int y = 0; y < dstHeight; y++)
        {
            int srcRowByte = (y << 1) * srcWidth * 4;
            int dstRowByte = y * dstWidth * 4;

            for (int x = 0; x < dstWidth; x++)
            {
                int srcPixelByte = srcRowByte + (x << 1) * 4;
                int dstPixelByte = dstRowByte + x * 4;

                // RGBA32 = 4 byte copy
                dst[dstPixelByte + 0] = src[srcPixelByte + 0];
                dst[dstPixelByte + 1] = src[srcPixelByte + 1];
                dst[dstPixelByte + 2] = src[srcPixelByte + 2];
                dst[dstPixelByte + 3] = src[srcPixelByte + 3];
            }
        }
    }
}