using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using Recorder;

public class RecorderController : MonoBehaviour
{
    public int fps = 60;

    public static QuestMp4Recorder recorder;
    public static bool recording = false;

    public static  int recordedCount = 0;
    public Text framesCount;
    public Text timeCost;
    
    private long lastRecordedCount = 0;   // 上一秒的总帧数
    private float lastTime = 0f;          // 上一次统计的时间
    private float recordFps = 0f;         // 实际录制FPS
    
    public IPListManager ipListManager;
    private string ipDebugText;
    
    IEnumerator Start()
    {
        while (!PassthroughCameraAccess2MatHelper.enabled)
            yield return null;

        recorder = new QuestMp4Recorder(
            PassthroughCameraAccess2MatHelper.texture.width,
            PassthroughCameraAccess2MatHelper.texture.height,
            fps
        );
    }

    void Update()
    {
        framesCount.text = $"recorded frames count: {recordedCount}";

        // ===== 计算实际录制 FPS =====
        if (recording)
        {
            float now = Time.time;
            if (now - lastTime >= 1f)
            {
                recordFps = QuestMp4Recorder.pushFrameCount - lastRecordedCount;
                lastRecordedCount = QuestMp4Recorder.pushFrameCount;
                lastTime = now;
            }
        }

        timeCost.text =
            $"[Mp4Recorder] pushFrame avg: {QuestMp4Recorder.pushFrameTotalMs / QuestMp4Recorder.pushFrameCount:F3} ms, " +
            $"max: {QuestMp4Recorder.pushFrameMaxMs:F3} ms, count: {QuestMp4Recorder.pushFrameCount}\n" +
            $"record fps: {recordFps:F1}\n" +
            ipDebugText;
    }

    void OnDestroy()
    {
        StopRecordingForce();
    }

    public void StartRecording(string requestIP)
    {
        if (recording || recorder==null) return;

        if (string.IsNullOrEmpty(requestIP) || !string.Equals(requestIP, ipListManager.GetSelectedIP()))
        {
            ipDebugText = $"wrong request IP: {requestIP}, target IP: {ipListManager.GetSelectedIP()}";
            return;
        }

        ipDebugText = $"Request IP: {requestIP}";
        recording = true;
        recordedCount = 0;
        
        lastRecordedCount = 0;
        lastTime = Time.time;
        recordFps = 0;

        string path =
            Application.persistentDataPath +
            $"/record/record_{DateTime.Now:yyyyMMdd_HHmmss}";

        recorder.StartRecording(path);
    }

    private void StopRecordingForce()
    {
        if (!recording) return;
        ipDebugText = "";
        recording = false;
        recorder?.StopRecording();
    }

    public void StopRecording(string requestIP)
    {
        if (!recording) return;
        
        if (string.IsNullOrEmpty(requestIP) || !string.Equals(requestIP, ipListManager.GetSelectedIP()))
        {
            ipDebugText = $"wrong request IP: {requestIP}, target IP: {ipListManager.GetSelectedIP()}";
            return;
        }
        ipDebugText = "";
        recording = false;
        recorder?.StopRecording();

        var fm = FindAnyObjectByType<FileManager>();
        if(fm!=null) fm.LoadFiles();
    }
}
