using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Bson;
using Unity.Collections.LowLevel.Unsafe;

namespace Recorder.v2
{
    public class Mp4RecorderPure : IDisposable
    {
        private const int PIPELINE_DEPTH = 30;
        private const int JOB_BATCH_SIZE = 256;

        private AndroidJavaClass mp4RecorderList;
        private int recorderId;

        private int width;
        private int height;
        private int fps;

        private bool recording;
        private long frameIndex;

        public string finalSha256;

        // ===== pushFrame 性能统计 =====
        public static long pushFrameCount;
        public static double pushFrameTotalMs;
        public static double pushFrameMaxMs;

        private struct JobSlot
        {
            public NativeArray<byte> rgba;
            public NativeArray<byte> nv12;
            public JobHandle handle;
            public long ptsUs;
            public bool inUse;
        }

        private JobSlot[] slots;
        private bool buffersAllocated;
        private int rgbaSize;
        private int nv12Size;
        public int writeIndex;
        public int readIndex;

        private byte[] nv12Managed;
        private int pushedFrameCount;

        private string outputPath;

        private string videoFileName = "recording.mp4";

        public bool dontPushJava = false;
        
        public NativeArray<byte> rgbaBuffer;
        
        public Mp4RecorderPure(int recorderId, int width, int height, int fps, string videoFileName)
        {
            this.recorderId = recorderId;
            this.width = width & ~1;
            this.height = height & ~1;
            this.fps = fps;
            this.videoFileName = videoFileName;
            
            rgbaSize = this.width * this.height * 4;
            nv12Size = this.width * this.height * 3 / 2;

#if UNITY_ANDROID && !UNITY_EDITOR
            mp4RecorderList = new AndroidJavaClass(
                "com.Noematrix.recorder.Mp4RecorderList");
#endif
        }

        // =========================================================

        public void StartRecording(string op)
        {
            if (recording) return;
            EnsureBuffersAllocated();
            
            // 检查路径是否存在
            outputPath = op;
            if (!Directory.Exists(outputPath))
            {
                // 创建目录（包括所有父目录）
                Directory.CreateDirectory(outputPath);
                ULog.Log($"创建目录: {outputPath}");
            }
            else
            {
                ULog.Log($"目录已存在: {outputPath}");
            }

            
            writeIndex = 0;
            readIndex = 0;
            frameIndex = 0;
            pushedFrameCount = 0;
            

            string mp4Path = Path.Combine(outputPath, videoFileName);
#if UNITY_ANDROID && !UNITY_EDITOR
            mp4RecorderList.CallStatic("start", recorderId, mp4Path, width, height, fps);
#endif
            
            recording = true;
        }
        
        // =========================================================
        public void RecordFrame(Texture2D texture)
        {
            if (!recording || !buffersAllocated) return;

            // ===== schedule job =====
            JobSlot slot = slots[writeIndex];
            if (!slot.inUse)
            {
                var raw = texture.GetRawTextureData<byte>();

                if(raw.Length == width * 2 * height * 2 * 4)
                {
                    RawImageDownsample.Downsample2x_TopLeft_RGBA32_safe(raw, width * 2, height * 2, slot.rgba);
                }
                else
                {
                    NativeArray<byte>.Copy(raw, slot.rgba);
                }

                slot.ptsUs = frameIndex * 1_000_000L / fps;

                slot.handle = new RgbaToNv12Job
                {
                    rgba = slot.rgba,
                    nv12 = slot.nv12,
                    width = width,
                    height = height
                }.Schedule(width * height, JOB_BATCH_SIZE);

                slot.inUse = true;
                slots[writeIndex] = slot;

                writeIndex = (writeIndex + 1) % PIPELINE_DEPTH;
                frameIndex++;
            }

            // ===== 完成 Job 并直接推送到 Java =====
            JobSlot front = slots[readIndex];
            if (front.inUse && front.handle.IsCompleted)
            {
                front.handle.Complete();

                front.nv12.CopyTo(nv12Managed);

                var sbyteBuff = new sbyte[nv12Managed.Length];
                Buffer.BlockCopy(nv12Managed, 0, sbyteBuff, 0, nv12Managed.Length);

#if UNITY_ANDROID && !UNITY_EDITOR
                var sw = System.Diagnostics.Stopwatch.StartNew();
                mp4RecorderList.CallStatic("pushFrame", recorderId, sbyteBuff, front.ptsUs);
                sw.Stop();

                double ms = sw.Elapsed.TotalMilliseconds;
                pushFrameCount++;
                pushFrameTotalMs += ms;
                if (ms > pushFrameMaxMs)
                    pushFrameMaxMs = ms;
#endif
                pushedFrameCount++;

                front.inUse = false;
                slots[readIndex] = front;

                readIndex = (readIndex + 1) % PIPELINE_DEPTH;
            }
        }
        
        public void RecordFrame(byte[] raw)
        {
            if (!recording || !buffersAllocated) return;

            // ===== schedule job =====
            JobSlot slot = slots[writeIndex];
            if (!slot.inUse)
            {
                slot.rgba.CopyFrom(raw);

                slot.ptsUs = frameIndex * 1_000_000L / fps;

                slot.handle = new RgbaToNv12Job
                {
                    rgba = slot.rgba,
                    nv12 = slot.nv12,
                    width = width,
                    height = height
                }.Schedule(width * height, JOB_BATCH_SIZE);

                slot.inUse = true;
                slots[writeIndex] = slot;

                writeIndex = (writeIndex + 1) % PIPELINE_DEPTH;
                frameIndex++;
            }

            // ===== 完成 Job 并直接推送到 Java =====
            JobSlot front = slots[readIndex];
            if (front.inUse && front.handle.IsCompleted)
            {
                front.handle.Complete();

                front.nv12.CopyTo(nv12Managed);

                var sbyteBuff = new sbyte[nv12Managed.Length];
                Buffer.BlockCopy(nv12Managed, 0, sbyteBuff, 0, nv12Managed.Length);

#if UNITY_ANDROID && !UNITY_EDITOR
                var sw = System.Diagnostics.Stopwatch.StartNew();
                mp4RecorderList.CallStatic("pushFrame", recorderId, sbyteBuff, front.ptsUs);
                sw.Stop();

                double ms = sw.Elapsed.TotalMilliseconds;
                pushFrameCount++;
                pushFrameTotalMs += ms;
                if (ms > pushFrameMaxMs)
                    pushFrameMaxMs = ms;
#endif
                pushedFrameCount++;

                front.inUse = false;
                slots[readIndex] = front;

                readIndex = (readIndex + 1) % PIPELINE_DEPTH;
            }
        }

        // =========================================================

        public void StopRecording()
        {
            if (!recording) return;

            recording = false;

            // ===== flush remaining JobSlots =====
            bool flushed;
            do
            {
                flushed = false;

                JobSlot slot = slots[readIndex];
                if (slot.inUse)
                {
                    slot.handle.Complete();

                    slot.nv12.CopyTo(nv12Managed);

                    var sbyteBuff = new sbyte[nv12Managed.Length];
                    Buffer.BlockCopy(nv12Managed, 0, sbyteBuff, 0, nv12Managed.Length);

#if UNITY_ANDROID && !UNITY_EDITOR
                    mp4RecorderList.CallStatic("pushFrame", recorderId, sbyteBuff, slot.ptsUs);
#endif
                    pushedFrameCount++;

                    slot.inUse = false;
                    slots[readIndex] = slot;

                    flushed = true;
                }

                readIndex = (readIndex + 1) % PIPELINE_DEPTH;

            } while (flushed);

#if UNITY_ANDROID && !UNITY_EDITOR
            if (pushedFrameCount > 0)
            {
                finalSha256 = mp4RecorderList.CallStatic<string>("stop", recorderId);
            }
            else
            {
                mp4RecorderList.CallStatic<string>("stop", recorderId);
                finalSha256 = null;
            }
#endif

            ReleaseBuffers();
            //Dispose();
        }

        // =========================================================

        private void EnsureBuffersAllocated()
        {
            if (buffersAllocated) return;

            slots = new JobSlot[PIPELINE_DEPTH];
            for (int i = 0; i < PIPELINE_DEPTH; i++)
            {
                slots[i].rgba = new NativeArray<byte>(rgbaSize, Allocator.Persistent);
                slots[i].nv12 = new NativeArray<byte>(nv12Size, Allocator.Persistent);
                slots[i].inUse = false;
            }

            nv12Managed = new byte[nv12Size];
            buffersAllocated = true;
        }

        private void ReleaseBuffers()
        {
            if (!buffersAllocated || slots == null) return;

            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i].inUse)
                    slots[i].handle.Complete();
                if (slots[i].rgba.IsCreated)
                    slots[i].rgba.Dispose();
                if (slots[i].nv12.IsCreated)
                    slots[i].nv12.Dispose();
                slots[i].inUse = false;
            }

            slots = null;
            nv12Managed = null;
            buffersAllocated = false;
        }

        public void Dispose()
        {
            ReleaseBuffers();
        }
    }
}
