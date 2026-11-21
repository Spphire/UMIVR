using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Meta.XR;
using Newtonsoft.Json;
using Newtonsoft.Json.Bson;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.UnityUtils;
using Recorder.v2;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.UI;

[Serializable]
public class StereoRecordData
{
    public List<double> leftCameraAccessTimestamps;
    public List<double> leftCameraUnityTimestamps;
    public List<double> rightCameraAccessTimestamps;
    public List<double> rightCameraUnityTimestamps;

    // pose = [x, y, z, qw, qx, qy, qz]
    public List<double[]> leftCameraPoses;
    public List<double[]> rightCameraPoses;

    public List<double[]> leftWristPoses;
    public List<double[]> rightWristPoses;
    public List<double[]> leftWristPosesRaw;
    public List<double[]> rightWristPosesRaw;
    
    public List<double[]> leftCameraPoses_iphone;
    public List<double[]> rightCameraPoses_iphone;
    
    public double[] leftCameraIntrinsics;
    public double[] rightCameraIntrinsics;
    
    // 明确写入约定（元数据）
    public string poseConvention = "xyzqwqxqyqz";
    public string intrinsicsConvention = "fxfycxcy";
    public bool naiveCalibrated = false;
    public StereoRecordData()
    {
        leftCameraAccessTimestamps = new List<double>();
        leftCameraUnityTimestamps = new List<double>();
        rightCameraAccessTimestamps = new List<double>();
        rightCameraUnityTimestamps = new List<double>();
        leftCameraPoses = new List<double[]>();
        rightCameraPoses = new List<double[]>();
        leftWristPoses = new List<double[]>();
        rightWristPoses = new List<double[]>();
        leftWristPosesRaw = new List<double[]>();
        rightWristPosesRaw = new List<double[]>();
        leftCameraPoses_iphone = new List<double[]>();
        rightCameraPoses_iphone = new List<double[]>();
    }
}


public class FramePusher : MonoBehaviour
{
    public static volatile bool need2refresh = true;
    
    public PassthroughCameraAccess leftCameraAccess;
    public PassthroughCameraAccess rightCameraAccess;

    public Transform leftWristAnchor;
    public Transform rightWristAnchor;

    private HandEyeCalibrationManager handEyeCalibrationManager;
    private readonly HashSet<string> missingWristPoseWarningKeys = new HashSet<string>();
    
    public static Texture leftTexture, rightTexture;
    public static Mat leftOriginalMat, rightOriginalMat;
    public static Mat leftHalfSizeMat, rightHalfSizeMat;
    public static byte[] leftByteList, rightByteList;
    
    public Mat leftCameraIntrinsicsMat;
    public Mat rightCameraIntrinsicsMat;
    
    public long leftTimestampPrev, rightTimestampPrev;
    
    public static bool enabled = false;

    public Toggle ArUcoSwitcher;
    
    [Header("VideoRecorder")]
    public Mp4RecorderPure leftVideoRecorder;
    public Mp4RecorderPure rightVideoRecorder;
    public int fps=60;
    public bool rightDownSample;
    
    [Header("RecordData")]
    private bool recording = false;
    private DateTime startTime;
    public double duration;
    // ✅ 只保留一个 RecordData
    private StereoRecordData recordData;

    [Header("SavingFile")] private static string outputPath;
    public static PreMeta preMeta;
    public static string leftVideoFileName = "left_recording.mp4";
    public static string rightVideoFileName = "right_recording.mp4";
    public static string metaFileName = "metadata.json";
    public static string dataFileName = "frame_data.bson";
    
    [Header("Thread")]
    private static int pendingMetaThreads = 0;
    private static ManualResetEvent metaAllDone = new ManualResetEvent(true);

    [Header("Debug")] 
    public long leftRecordFrameCount = 0;
    public long rightRecordFrameCount = 0;

    public bool isDualEye = false;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    private IEnumerator Start()
    {
        OVRPermissionsRequester.Request(new[]
        {
            OVRPermissionsRequester.Permission.Scene,
            OVRPermissionsRequester.Permission.PassthroughCameraAccess
        });
        while (!leftCameraAccess.IsPlaying )
        {
            yield return null;
        }
        leftTexture = leftCameraAccess.GetTexture();
        leftOriginalMat = new Mat(leftTexture.height, leftTexture.width, CvType.CV_8UC4);
        leftHalfSizeMat = new Mat(leftTexture.height/2, leftTexture.width/2, CvType.CV_8UC4);
        leftByteList = new byte[(int)(leftHalfSizeMat.total() * leftHalfSizeMat.elemSize())];
        leftCameraIntrinsicsMat = GetIntrinsicsMat(leftCameraAccess);
        leftTimestampPrev =
            TimeSync.AddTimeOffset(new DateTimeOffset(leftCameraAccess.Timestamp)
                .ToUnixTimeMilliseconds());
        leftVideoRecorder = new Mp4RecorderPure(0,leftTexture.width, leftTexture.height, fps, leftVideoFileName);

        if (isDualEye)
        {
            while (!rightCameraAccess.IsPlaying)
            {
                yield return null;
            }
            rightTexture = rightCameraAccess.GetTexture();
            rightOriginalMat = new Mat(rightTexture.height, rightTexture.width, CvType.CV_8UC4);
            rightHalfSizeMat = new Mat(rightTexture.height/2, rightTexture.width/2, CvType.CV_8UC4);
            rightByteList = new byte[(int)(rightHalfSizeMat.total() * rightHalfSizeMat.elemSize())];
            rightCameraIntrinsicsMat = GetIntrinsicsMat(rightCameraAccess);
            rightTimestampPrev =
                TimeSync.AddTimeOffset(new DateTimeOffset(rightCameraAccess.Timestamp)
                    .ToUnixTimeMilliseconds());
            if (rightTexture.width > 800)
            {
                rightDownSample = true;
                rightVideoRecorder =
                    new Mp4RecorderPure(1, rightTexture.width/2, rightTexture.height/2, fps, rightVideoFileName);
            }
            else
            {
                rightDownSample = false;
                rightVideoRecorder =
                    new Mp4RecorderPure(1, rightTexture.width, rightTexture.height, fps, rightVideoFileName);
            }
        }

        enabled = true;
    }

    // Update is called once per frame
    void Update()
    {
        duration = (DateTime.Now - startTime).TotalMilliseconds;
        if (recording)
        {
            EnsureWristAnchors();
            //left
            
            long leftTimestampNow = TimeSync.AddTimeOffset(new DateTimeOffset(leftCameraAccess.Timestamp)
                .ToUnixTimeMilliseconds());
            if( leftTimestampNow != leftTimestampPrev)
            {
                leftTimestampPrev = leftTimestampNow;
                if (leftTexture is Texture2D t2d)
                {
                    Pose leftCameraPose = leftCameraAccess.GetCameraPose();
                    Transform leftRawWristTransform = GetRawWristTransform(true);
                    Transform rightRawWristTransform = GetRawWristTransform(false);
                    leftVideoRecorder.RecordFrame(t2d);
                    recordData.leftCameraAccessTimestamps.Add(leftTimestampNow/1000.0);
                    recordData.leftCameraUnityTimestamps.Add(TimeSync.GetSyncedTimestampMs()/1000.0);
                    recordData.leftCameraPoses.Add(CreatePose7(leftCameraPose));
                    recordData.leftCameraPoses_iphone.Add(Transfer2iphone(leftCameraPose));

                    AppendWristPose(recordData.leftWristPoses, leftWristAnchor, "left");
                    AppendWristPose(recordData.rightWristPoses, rightWristAnchor, "right");
                    AppendWristPose(recordData.leftWristPosesRaw, leftRawWristTransform, "left_raw");
                    AppendWristPose(recordData.rightWristPosesRaw, rightRawWristTransform, "right_raw");

                    leftRecordFrameCount++;
                    //FindAnyObjectByType<ArucoPNP>()?.Detect(t2d,leftCameraPose,leftCameraIntrinsicsMat);
                }
            }
            
            //right
            if (isDualEye)
            {
                long rightTimestampNow = TimeSync.AddTimeOffset(new DateTimeOffset(rightCameraAccess.Timestamp)
                    .ToUnixTimeMilliseconds());
                if (rightTimestampNow != rightTimestampPrev)
                {
                    rightTimestampPrev = rightTimestampNow;
                    if (rightTexture is Texture2D t2d)
                    {
                        Pose rightCameraPose = rightCameraAccess.GetCameraPose();
                        rightVideoRecorder.RecordFrame(t2d);
                        recordData.rightCameraAccessTimestamps.Add(rightTimestampNow / 1000.0);
                        recordData.rightCameraUnityTimestamps.Add(TimeSync.GetSyncedTimestampMs() / 1000.0);
                        recordData.rightCameraPoses.Add(CreatePose7(rightCameraPose));
                        recordData.rightCameraPoses_iphone.Add(Transfer2iphone(rightCameraPose));
                        rightRecordFrameCount++;
                    }
                }
            }
        }
    }

    private double[] GetIntrinsics(PassthroughCameraAccess cameraAccess)
    {
        PassthroughCameraAccess.CameraIntrinsics intrinsics = cameraAccess.Intrinsics;
        Vector2Int resolution = cameraAccess.CurrentResolution;
        Vector2Int sensorResolution = cameraAccess.Intrinsics.SensorResolution;
        
        float fx = intrinsics.FocalLength.x * resolution.x / sensorResolution[0];
        float fy = intrinsics.FocalLength.y * resolution.x / sensorResolution[0];

        float cx = intrinsics.PrincipalPoint.x * resolution.x / sensorResolution[0];
        float cy = intrinsics.PrincipalPoint.y * resolution.y / sensorResolution[1];

        return new double[]
        {
            fx, fy, cx, cy
        };
    }

    public Mat GetIntrinsicsMat(PassthroughCameraAccess cameraAccess)
    {
        var fxfycxcy = GetIntrinsics(cameraAccess);
        return PassthroughCameraAccessArUco.CreateCameraMatrix(fxfycxcy[0], fxfycxcy[1], fxfycxcy[2], fxfycxcy[3]);
    }

    public void StartRecording()
    {
        if (!enabled)
        {
            ULog.LogWarning("FramePusher is not enabled yet. Cannot start recording.");
            return;
        }

        if (recording)
        {
            ULog.LogWarning("Recording already started.");
            return;
        }
        ArUcoSwitcher.isOn = false;
        missingWristPoseWarningKeys.Clear();
        EnsureWristAnchors();
        
        recording = true;
        startTime = DateTime.Now;
        recordData = new StereoRecordData();
        recordData.leftCameraIntrinsics = GetIntrinsics(leftCameraAccess);
        if(isDualEye)
            recordData.rightCameraIntrinsics = GetIntrinsics(rightCameraAccess);
        
        leftRecordFrameCount = 0;
        rightRecordFrameCount = 0;
        
        outputPath =
            Application.persistentDataPath +
            $"/record/record_{DateTime.Now:yyyyMMdd_HHmmss}";
        // ✅ 检查并创建目录
        if (!Directory.Exists(outputPath))
        {
            Directory.CreateDirectory(outputPath);
            ULog.Log("Created record directory: " + outputPath);
        }

        leftVideoRecorder.StartRecording(outputPath);
        if(isDualEye)
            rightVideoRecorder.StartRecording(outputPath);

        ULog.Log("Recording started. Pose convention: " + recordData.poseConvention);
    }

    public void StopRecording()
    {
        recording = false;
        ArUcoSwitcher.isOn = false;

        leftVideoRecorder.StopRecording();
        string leftVideoSha256 = leftVideoRecorder.finalSha256;
        string rightVideoSha256 = null;
        if (isDualEye)
        {
            rightVideoRecorder.StopRecording();
            rightVideoSha256 = rightVideoRecorder.finalSha256;
        }

        var fileInfos = new List<FileInfo>();
        if (!string.IsNullOrEmpty(leftVideoSha256))
        {
            fileInfos.Add(
                new FileInfo(
                    leftVideoFileName,
                    leftVideoSha256
                    )
                );
        }

        if (!string.IsNullOrEmpty(rightVideoSha256))
        {
            fileInfos.Add(
                new FileInfo(
                    rightVideoFileName,
                    rightVideoSha256
                    )
                );
        }

        //meta
        string uuid = Guid.NewGuid().ToString();
        MetaDatav0_9 metaData;
        if (preMeta == null)
        {
            metaData = new MetaDatav0_9(
                uuid: uuid,
                parentUuid: uuid,
                startTime: startTime.ToString("yyyyMMddHHmmss"),
                duration: (DateTime.Now - startTime).TotalSeconds,
                frames: recordData.leftCameraAccessTimestamps.Count,
                dataFrames: recordData.leftCameraUnityTimestamps.Count,
                files: fileInfos,
                video: new Video(recordData.leftCameraAccessTimestamps.Count),
                device: new Device()
            );
        }
        else
        {
            metaData = new MetaDatav0_9(
                uuid: uuid,
                parentUuid: preMeta.Uuid,
                startTime: startTime.ToString("yyyyMMddHHmmss"),
                duration: (DateTime.Now - startTime).TotalSeconds,
                frames: recordData.leftCameraAccessTimestamps.Count,
                dataFrames: recordData.leftCameraUnityTimestamps.Count,
                files: fileInfos,
                video: new Video(recordData.leftCameraAccessTimestamps.Count),
                device: new Device(),
                
                taskDescription: preMeta.TaskDescription,
                multiMode: preMeta.MultiMode,
                userFingerprint: preMeta.UserFingerprint,
                taskTag: preMeta.TaskTag
                
            );
        }
        
        
        
        StereoRecordData recordDataSnapshot = recordData;
        MetaDatav0_9 metaDataSnapshot = metaData;
        string outputPathSnapshot = outputPath;
        metaAllDone.Reset();
        Interlocked.Increment(ref pendingMetaThreads);
        new Thread(() =>
            {
                try
                {
                    SaveData(recordDataSnapshot, metaDataSnapshot, outputPathSnapshot);
                    need2refresh = true;
                }
                catch (Exception e)
                {
                    ULog.LogError("SaveMeta failed: " + e);
                }
                finally
                {
                    if (Interlocked.Decrement(ref pendingMetaThreads) == 0)
                    {
                        metaAllDone.Set();
                    }
                }
                
            }
        )
        {
            IsBackground = false
        }.Start();
        preMeta = null;
        recordData = new StereoRecordData();

        
    }
    
    private void OnApplicationQuit()
    {
        ULog.Log("Application quitting, waiting for meta write...");

        // 防止卡死：最多等 5 秒
        bool finished = metaAllDone.WaitOne(5000);

        if (!finished)
        {
            ULog.LogWarning("Meta writing did not finish before quit.");
        }
        else
        {
            ULog.Log("All meta writing finished.");
        }
        
    }


    private static void SaveData(StereoRecordData recordDataSnapshot, MetaDatav0_9 metaDataSnapshot, string outputPathSnapshot)
    {
        //bson
        string dataPath = Path.Combine(outputPathSnapshot, dataFileName);
        byte[] bsonBytes;
        using (var ms = new MemoryStream())
        using (var writer = new BsonWriter(ms))
        {
            var serializer = new JsonSerializer();
            serializer.Serialize(writer, recordDataSnapshot);
            writer.Flush();
            bsonBytes = ms.ToArray();
        }
        File.WriteAllBytes(dataPath, bsonBytes);

        metaDataSnapshot.files.Add(new FileInfo(
            dataFileName,
            Sha256Util.ComputeSha256(bsonBytes)
            )
        );
        
        // //meta
        // metaDataSnapshot.files = new List<FileInfo>()
        // {
        //     new FileInfo(
        //         leftVideoFileName,
        //         Sha256Util.ComputeFileSha256(Path.Combine(outputPathSnapshot, leftVideoFileName))
        //     ),
        //     new FileInfo(
        //         rightVideoFileName,
        //         Sha256Util.ComputeFileSha256(Path.Combine(outputPathSnapshot, rightVideoFileName))
        //     ),
        //     new FileInfo(
        //         dataFileName,
        //         Sha256Util.ComputeFileSha256(Path.Combine(outputPathSnapshot, dataFileName))
        //     ),
        // };

        string metaPath = Path.Combine(outputPathSnapshot, metaFileName);
        using (var fs = File.Create(metaPath))
        using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
        using (var writer = new JsonTextWriter(sw))
        {
            writer.Formatting = Formatting.Indented; // 美化，可选
            var serializer = new JsonSerializer();
            serializer.Serialize(writer, metaDataSnapshot);
        }
    }
    
    private static double[] CreatePose7(Pose pose)
    {
        Vector3 position = pose.position;
        Quaternion rotation = pose.rotation;
        return new double[7]
        {
            position.x,
            position.y,
            position.z,
            rotation.w,
            rotation.x,
            rotation.y,
            rotation.z
        };
    }
    
    private static double[] Transfer2iphone(Pose pose)
    {

        Matrix4x4 poseMat = Matrix4x4.TRS(pose.position, pose.rotation, Vector3.one);
        Matrix4x4 poseMat_iphone = UnityHttpReceiver.iphone2unity.inverse * UnityHttpReceiver.flipX * poseMat *
                                   UnityHttpReceiver.flipX;
        Vector3 position = poseMat_iphone.GetPosition();
        Quaternion rotation = poseMat_iphone.rotation;
        return new double[7]
        {
            position.x,
            position.y,
            position.z,
            rotation.w,
            rotation.x,
            rotation.y,
            rotation.z
        };
    }


    public void OnDestroy()
    {
        leftVideoRecorder.Dispose();
        rightVideoRecorder.Dispose();
    }

    private void EnsureWristAnchors()
    {
        if (handEyeCalibrationManager == null)
            handEyeCalibrationManager = FindAnyObjectByType<HandEyeCalibrationManager>();

        if (handEyeCalibrationManager == null)
            return;

        leftWristAnchor = ResolvePreferredWristAnchor(
            handEyeCalibrationManager.leftHandTransform,
            leftWristAnchor
        );
        rightWristAnchor = ResolvePreferredWristAnchor(
            handEyeCalibrationManager.rightHandTransform,
            rightWristAnchor
        );
    }

    private static Transform ResolvePreferredWristAnchor(Transform handTransform, Transform currentAnchor)
    {
        if (handTransform == null)
            return currentAnchor;

        Transform calibratedAnchor = handTransform.Find("HandEyeResult");
        if (calibratedAnchor != null)
            return calibratedAnchor;

        return currentAnchor != null ? currentAnchor : handTransform;
    }

    private Transform GetRawWristTransform(bool left)
    {
        if (handEyeCalibrationManager == null)
            return null;

        return left
            ? handEyeCalibrationManager.leftHandTransform
            : handEyeCalibrationManager.rightHandTransform;
    }

    private void AppendWristPose(List<double[]> poseList, Transform wristAnchor, string side)
    {
        if (wristAnchor != null)
        {
            Pose wristPose = new Pose(wristAnchor.position, wristAnchor.rotation);
            poseList.Add(CreatePose7(wristPose));
            return;
        }

        poseList.Add(null);
        if (missingWristPoseWarningKeys.Add(side))
        {
            ULog.LogWarning($"Missing wrist anchor while recording. side={side}");
        }
    }
}
