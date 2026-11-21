using UnityEngine;
using UnityEngine.UI;

public class RecorderControllerPure : MonoBehaviour
{
    
    public FramePusher framePusher;
    public static bool recording = false;
    
    public Text framesCount;
    public Text timeCost;
    
    private long lastLeftRecordedCount = 0;   // 上一秒的总帧数
    private long lastRightRecordedCount = 0;
    private float lastTime = 0f;          // 上一次统计的时间
    private float leftRecordFps = 0f;         // 实际录制FPS
    private float rightRecordFps = 0f;
    
    public IPListManager ipListManager;
    private string ipDebugText;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        framesCount.text = $"left recorded: {framePusher.leftRecordFrameCount}frames; right recorded: {framePusher.rightRecordFrameCount}frames";

        // ===== 计算实际录制 FPS =====
        if (recording)
        {
            float now = Time.time;
            if (now - lastTime >= 1f)
            {
                leftRecordFps = framePusher.leftRecordFrameCount - lastLeftRecordedCount;
                lastLeftRecordedCount = framePusher.leftRecordFrameCount;
                
                rightRecordFps = framePusher.rightRecordFrameCount - lastRightRecordedCount;
                lastRightRecordedCount = framePusher.rightRecordFrameCount;
                
                lastTime = now;
            }
            timeCost.text =
                $"[Mp4Recorder] pushFrame avg: {framePusher.duration / framePusher.leftRecordFrameCount:F3}ms ; {framePusher.duration / framePusher.rightRecordFrameCount:F3}ms\n" +
                //$"max: {QuestMp4Recorder.pushFrameMaxMs:F3} ms, count: {QuestMp4Recorder.pushFrameCount}\n" +
                $"left record: {leftRecordFps:F1} ; right record: {rightRecordFps:F1}\n" +
                $"left: {framePusher.leftVideoRecorder.readIndex} ; {framePusher.leftVideoRecorder.writeIndex}" +
                $"right: {framePusher.rightVideoRecorder.readIndex} ; {framePusher.rightVideoRecorder.writeIndex}" +
                ipDebugText;
        }

        
    }
    
    void OnDestroy()
    {
        StopRecordingForce();
    }

    public void StartRecording(string requestIP)
    {
        if (recording || framePusher==null) return;

        if (string.IsNullOrEmpty(requestIP) || !string.Equals(requestIP, ipListManager.GetSelectedIP()))
        {
            ipDebugText = $"wrong request IP: {requestIP}, target IP: {ipListManager.GetSelectedIP()}";
            return;
        }

        ipDebugText = $"Request IP: {requestIP}";
        recording = true;
        lastLeftRecordedCount = 0;
        lastRightRecordedCount = 0;
        lastTime = Time.time;
        leftRecordFps = 0;
        rightRecordFps = 0;

        framePusher.StartRecording();
    }

    private void StopRecordingForce()
    {
        if (!recording) return;
        ipDebugText = "";
        recording = false;
        framePusher?.StopRecording();

        var fm = FindAnyObjectByType<FileManager>();
        if(fm!=null) fm.LoadFiles();
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
        framePusher?.StopRecording();

        var fm = FindAnyObjectByType<FileManager>();
        if(fm!=null) fm.LoadFiles();
    }
}
