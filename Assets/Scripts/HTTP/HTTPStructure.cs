using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;

[Serializable]
public class UnityData
{
    public float[] pos;
    public float t;
    public string camera_position;
    public float[] quat;
    public bool recording;
}

[Serializable]
public class Device
{
    public string identifier;
    public string model;
    public string name;
    public string system;
    public string version;

    public Device()
    {
        identifier = SystemInfo.deviceUniqueIdentifier;
        model = SystemInfo.deviceModel;
        name = SystemInfo.deviceName;
        system = SystemInfo.operatingSystem;
        version = SystemInfo.operatingSystem;
    }
}

[Serializable]
public class DepthMap
{
    public int bit_depth;
    public float depth_max;
    public float depth_min;
    public int frames;
    public int height;
    public int width;
}

[Serializable]
public class Video
{
    public int frames;
    public int height;
    public int width;

    public Video(int frames, int height=480, int width=640)
    {
        this.frames = frames;
        this.height = height;
        this.width = width;
    }
}

[Serializable]
public class FileInfo
{
    public string filename;
    public string sha256;

    public FileInfo(string filename, string sha256)
    {
        this.filename = filename;
        this.sha256 = sha256;
    }
}

[Serializable]
public class MetaDatav0_9
{
    public string uuid;
    public string parent_uuid;
    public string codebase_version = "v0.9";
    public string start_time;
    public double duration;
    public string task_description;
    public string task_tag;
    
    public string camera_position;
    public string device_orientation;
    public string multi_mode;
    public string user_fingerprint;

    public int data_frames;
    public int frames;
    public int fps;

    public Device device;
    public DepthMap depth_map;
    public Video video;

    public List<string> problems;
    public List<FileInfo> files;

    public MetaDatav0_9(
        string uuid = "",
        string taskTag = "QuestTest",
        int fps = 60,
        int frames = 30,
        Device device = null,
        Video video = null,
        DepthMap depthMap = null,

        string parentUuid = "",
        string taskDescription = "",
        double duration = 0.0,
        int dataFrames = 0,
        string cameraPosition = "head",
        string deviceOrientation = "",
        string multiMode = "",
        string userFingerprint = "",
        List<string> problems = null,
        List<FileInfo> files = null,
        string startTime = null
    )
    {
        // 必要字段
        this.uuid = uuid;
        this.task_tag = taskTag;
        this.fps = fps;
        this.frames = frames;

        this.device = device ?? new Device();
        this.video = video ?? new Video(0);
        this.depth_map = depthMap ?? new DepthMap();

        // 可选字段
        this.parent_uuid = parentUuid;
        this.task_description = taskDescription;
        this.duration = duration;
        this.data_frames = dataFrames > 0 ? dataFrames : frames;

        this.camera_position = cameraPosition;
        this.device_orientation = deviceOrientation;
        this.multi_mode = multiMode;
        this.user_fingerprint = userFingerprint;

        this.start_time = startTime ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        this.problems = problems ?? new List<string>();
        this.files = files ?? new List<FileInfo>();
    }
}

[Serializable]
public class RecordData
{
    public bool start;
    
}

[Serializable]
public class PreMeta
{
    [JsonProperty("uuid")]
    public string Uuid;

    [JsonProperty("task_description")]
    public string TaskDescription;

    [JsonProperty("multi_mode")]
    public string MultiMode;

    [JsonProperty("user_fingerprint")]
    public string UserFingerprint;

    [JsonProperty("task_tag")]
    public string TaskTag;

    // 可选：调试友好
    public override string ToString()
    {
        return
            $"uuid={Uuid}\n" +
            $"task_description={TaskDescription}\n" +
            $"multi_mode={MultiMode}\n" +
            $"user_fingerprint={UserFingerprint}\n" +
            $"task_tag={TaskTag}";
    }
}