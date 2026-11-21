using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Bson;

namespace Recorder
{
    public class QuestMp4Recorder : IDisposable
    {
        private const int PIPELINE_DEPTH = 30;
        private const int JOB_BATCH_SIZE = 256;

        private AndroidJavaClass mp4Recorder;

        private int width;
        private int height;
        private int fps;

        private bool recording;
        private long frameIndex;

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
        private int writeIndex;
        private int readIndex;

        private byte[] nv12Managed;
        private int pushedFrameCount;

        private string outputPath;
        public List<PassthroughCameraAccessData> dataBuffer;

        private static string _videoFileName = "recording.mp4";

        public static string videoFileName
        {
            get { return _videoFileName; }
        }
        private static string _dataFileName = "frame_data.bson";

        public static string dataFileName
        {
            get { return _dataFileName; }
        }
        
        private static string _metaFileName = "metadata.json";

        public static string metaFileName
        {
            get { return _metaFileName; }
        }
        
        private static DateTime startTime;

        public static PreMeta preMeta;
        public QuestMp4Recorder(int width, int height, int fps)
        {
            this.width = width & ~1;
            this.height = height & ~1;
            this.fps = fps;

#if UNITY_ANDROID && !UNITY_EDITOR
            mp4Recorder = new AndroidJavaClass(
                "com.Noematrix.recorder.Mp4Recorder");
#endif
        }

        // =========================================================

        public void StartRecording(string op)
        {
            if (recording) return;
            
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
            
            startTime = DateTime.Now;

            int rgbaSize = width * height * 4;
            int nv12Size = width * height * 3 / 2;

            slots = new JobSlot[PIPELINE_DEPTH];
            for (int i = 0; i < PIPELINE_DEPTH; i++)
            {
                slots[i].rgba = new NativeArray<byte>(rgbaSize, Allocator.Persistent);
                slots[i].nv12 = new NativeArray<byte>(nv12Size, Allocator.Persistent);
                slots[i].inUse = false;
            }

            nv12Managed = new byte[nv12Size];
            writeIndex = 0;
            readIndex = 0;
            frameIndex = 0;
            pushedFrameCount = 0;

            if (dataBuffer == null)
            {
                dataBuffer = new List<PassthroughCameraAccessData>();
            }else{
                dataBuffer.Clear();
            }

            string mp4Path = Path.Combine(outputPath, videoFileName);
#if UNITY_ANDROID && !UNITY_EDITOR
            mp4Recorder.CallStatic("start", mp4Path, width, height, fps);
#endif
            
            recording = true;
        }

        // =========================================================

        public void RecordFrame(Texture2D t2d, PassthroughCameraAccessData datapack)
        {
            RecordFrame(t2d);
            dataBuffer.Add(datapack);
            //
        }
        
        public void RecordFrame(Texture2D texture)
        {
            if (!recording) return;

            // ===== schedule job =====
            JobSlot slot = slots[writeIndex];
            if (!slot.inUse)
            {
                var raw = texture.GetRawTextureData<byte>();
                NativeArray<byte>.Copy(raw, slot.rgba);

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
                mp4Recorder.CallStatic("pushFrame", sbyteBuff, front.ptsUs);
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
                    mp4Recorder.CallStatic("pushFrame", sbyteBuff, slot.ptsUs);
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
                mp4Recorder.CallStatic("stop");
#endif
            string dataPath = Path.Combine(outputPath, dataFileName);
            using (var fs = File.Create(dataPath))
            using (var writer = new BsonWriter(fs))
            {
                var serializer = new JsonSerializer();
                serializer.Serialize(writer, dataBuffer);
            }

            string uuid = Guid.NewGuid().ToString();
            MetaDatav0_9 metaData;
            if (preMeta == null)
            {
                metaData = new MetaDatav0_9(
                    uuid: uuid,
                    parentUuid: uuid,
                    startTime: startTime.ToString("yyyyMMddHHmmss"),
                    duration: (float)(DateTime.Now - startTime).TotalMilliseconds,
                    frames: dataBuffer.Count,
                    dataFrames: dataBuffer.Count,
                    files: new List<FileInfo>()
                    {
                        new FileInfo(
                            videoFileName,
                            Sha256Util.ComputeFileSha256(Path.Combine(outputPath, videoFileName))
                        ),
                        new FileInfo(
                            dataFileName,
                            Sha256Util.ComputeFileSha256(Path.Combine(outputPath, dataFileName))
                        ),
                    },
                    video: new Video(dataBuffer.Count),
                    device: new Device()
                );
            }
            else
            {
                metaData = new MetaDatav0_9(
                    uuid: uuid,
                    parentUuid: preMeta.Uuid,
                    startTime: startTime.ToString("yyyyMMddHHmmss"),
                    duration: (float)(DateTime.Now - startTime).TotalMilliseconds,
                    frames: dataBuffer.Count,
                    dataFrames: dataBuffer.Count,
                    files: new List<FileInfo>()
                    {
                        new FileInfo(
                            videoFileName,
                            Sha256Util.ComputeFileSha256(Path.Combine(outputPath, videoFileName))
                        ),
                        new FileInfo(
                            dataFileName,
                            Sha256Util.ComputeFileSha256(Path.Combine(outputPath, dataFileName))
                        ),
                    },
                    video: new Video(dataBuffer.Count),
                    device: new Device(),
                    
                    taskDescription: preMeta.TaskDescription,
                    multiMode: preMeta.MultiMode,
                    userFingerprint: preMeta.UserFingerprint,
                    taskTag: preMeta.TaskTag
                    
                );
            }

            string metaPath = Path.Combine(outputPath, metaFileName);
            using (var fs = File.Create(metaPath))
            using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
            using (var writer = new JsonTextWriter(sw))
            {
                writer.Formatting = Formatting.Indented; // 美化，可选
                var serializer = new JsonSerializer();
                serializer.Serialize(writer, metaData);
            }
            preMeta = null;
            dataBuffer.Clear();
            Dispose();
        }

        // =========================================================

        public void Dispose()
        {
            if (slots != null)
            {
                for (int i = 0; i < PIPELINE_DEPTH; i++)
                {
                    if (slots[i].rgba.IsCreated)
                        slots[i].rgba.Dispose();
                    if (slots[i].nv12.IsCreated)
                        slots[i].nv12.Dispose();
                }
            }

            recording = false;
        }
    }
}
