using UnityEngine;
using UnityEngine.InputSystem;
using System.Net.Sockets;
using System.Collections;
using System.Collections.Concurrent;
using System.Threading;
using System.IO;
using Meta.XR;
using TMPro;
using UnityEngine.UI;

public class QuestStreamSender : MonoBehaviour
{
    [Header("Connection Settings")]
    public TMP_InputField pcIPInput;
    public int streamPort = 5555;
    
    [Header("Camera Source")]
    public PassthroughCameraAccess leftEyeCamera;
    
    [Header("Stream Settings")]
    public int jpegQuality = 80;
    public float sendInterval = 0.033f; // ~30 FPS
    public float reconnectInterval = 2f; // 重连间隔
    public int maxQueueSize = 2; // 最大队列长度

    private TcpClient tcpClient;
    private NetworkStream networkStream;
    private Thread sendThread;
    private volatile bool isStreaming = false;
    public Toggle isCapture;
    private bool wasStreamingBeforePause = false;
    private float lastCaptureTime = 0f;
    
    private readonly ConcurrentQueue<byte[]> frameQueue = new ConcurrentQueue<byte[]>();
    private string targetIP;

    private IEnumerator Start()
    {
        Debug.Log("[QuestStreamSender] Start() called!");
        
        if (leftEyeCamera == null)
        {
            Debug.LogError("[QuestStreamSender] leftEyeCamera is NULL! Please assign it in Inspector.");
            yield break;
        }
        
        Debug.Log($"[QuestStreamSender] Waiting for camera... IsPlaying={leftEyeCamera.IsPlaying}");
        
        while (!leftEyeCamera.IsPlaying)
        {
            yield return null;
        }
        
        wasStreamingBeforePause = IsCaptureEnabled();
        Debug.Log($"[QuestStreamSender] Camera ready. captureEnabled={wasStreamingBeforePause}");
        if (wasStreamingBeforePause)
            StartStream();
    }

    void Update()
    {
        bool captureEnabled = IsCaptureEnabled();
        if (captureEnabled && !isStreaming)
            StartStream();
        else if (!captureEnabled && isStreaming)
            StopStream();

        // 可选：按键开关（调试用）
        if (Keyboard.current != null && Keyboard.current.sKey.wasPressedThisFrame)
        {
            if (!isStreaming)
                StartStream();
            else
                StopStream();
        }
        
        // 主线程：捕获并编码帧
        if (captureEnabled && isStreaming && Time.time - lastCaptureTime >= sendInterval)
        {
            CaptureAndEnqueueFrame();
            lastCaptureTime = Time.time;
        }
    }

    void OnApplicationPause(bool pause)
    {
        if (pause)
        {
            // 记住当前状态并停止流
            wasStreamingBeforePause = isStreaming && IsCaptureEnabled();
            if (isStreaming)
            {
                Debug.Log("[QuestStreamSender] App paused, stopping stream...");
                StopStream();
            }
        }
        else
        {
            // 恢复之前的流状态
            if (wasStreamingBeforePause && IsCaptureEnabled())
            {
                Debug.Log("[QuestStreamSender] App resumed, restarting stream...");
                StartCoroutine(DelayedRestart());
            }
        }
    }

    private IEnumerator DelayedRestart()
    {
        // 等待相机恢复
        while (!leftEyeCamera.IsPlaying)
        {
            yield return null;
        }
        yield return new WaitForSeconds(0.5f); // 额外延迟确保稳定
        if (IsCaptureEnabled())
            StartStream();
    }

    private bool IsCaptureEnabled()
    {
        return isCapture != null && isCapture.isOn;
    }

    /// <summary>
    /// 开启图像流传输
    /// </summary>
    public void StartStream()
    {
        if (isStreaming)
        {
            Debug.LogWarning("[QuestStreamSender] Stream is already running");
            return;
        }

        targetIP = pcIPInput?.text;
        if (string.IsNullOrEmpty(targetIP))
        {
            targetIP = "127.0.0.1";
        }

        isStreaming = true;
        
        // 启动后台发送线程
        sendThread = new Thread(SendThreadLoop) { IsBackground = true };
        sendThread.Start();
        
        Debug.Log($"[QuestStreamSender] 流传输已启动，目标 {targetIP}:{streamPort}");
    }

    /// <summary>
    /// 关闭图像流传输
    /// </summary>
    public void StopStream()
    {
        if (!isStreaming)
        {
            Debug.LogWarning("[QuestStreamSender] Stream is not running");
            return;
        }

        isStreaming = false;
        
        // 等待线程结束
        if (sendThread != null && sendThread.IsAlive)
        {
            sendThread.Join(500);
            sendThread = null;
        }
        
        CloseConnection();
        
        // 清空队列
        while (frameQueue.TryDequeue(out _)) { }
        
        Debug.Log("[QuestStreamSender] Stream stopped");
    }

    /// <summary>
    /// 主线程：捕获帧并编码为JPG，放入队列
    /// </summary>
    void CaptureAndEnqueueFrame()
    {
        try
        {
            Texture2D tex = leftEyeCamera.GetTexture() as Texture2D;
            if (tex == null)
                return;

            // 编码为JPG（必须在主线程）
            byte[] jpgData = tex.EncodeToJPG(jpegQuality);
            
            // 队列满则丢弃旧帧
            if (frameQueue.Count >= maxQueueSize)
            {
                frameQueue.TryDequeue(out _);
            }
            
            frameQueue.Enqueue(jpgData);
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[QuestStreamSender] 捕获帧失败: {e.Message}");
        }
    }

    /// <summary>
    /// 后台线程：连接 + 发送循环
    /// </summary>
    void SendThreadLoop()
    {
        Debug.Log("[QuestStreamSender] 后台发送线程已启动");
        
        while (isStreaming)
        {
            try
            {
                // 尝试连接
                if (!TryConnect())
                {
                    Thread.Sleep((int)(reconnectInterval * 1000));
                    continue;
                }
                
                Debug.Log("[QuestStreamSender] ✓ 已连接到 PC，开始发送帧");
                
                // 连接成功，持续发送
                while (isStreaming && tcpClient != null && tcpClient.Connected)
                {
                    if (frameQueue.TryDequeue(out byte[] jpgData))
                    {
                        if (!SendFrameData(jpgData))
                        {
                            Debug.LogWarning("[QuestStreamSender] 发送失败，准备重连...");
                            break;
                        }
                    }
                    else
                    {
                        Thread.Sleep(5); // 队列空，短暂休眠
                    }
                }
                
                CloseConnection();
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[QuestStreamSender] 线程异常: {e.Message}");
                CloseConnection();
                Thread.Sleep((int)(reconnectInterval * 1000));
            }
        }
        
        CloseConnection();
        Debug.Log("[QuestStreamSender] 后台发送线程已退出");
    }

    /// <summary>
    /// 后台线程：尝试连接到PC（阻塞操作）
    /// </summary>
    bool TryConnect()
    {
        try
        {
            tcpClient = new TcpClient();
            tcpClient.Connect(targetIP, streamPort); // 阻塞连接，但在后台线程无妨
            
            networkStream = tcpClient.GetStream();
            networkStream.WriteTimeout = 1000;
            networkStream.ReadTimeout = 1000;
            
            return true;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[QuestStreamSender] 连接失败: {e.Message}");
            CloseConnection();
            return false;
        }
    }

    /// <summary>
    /// 后台线程：发送已编码的帧数据
    /// </summary>
    bool SendFrameData(byte[] jpgData)
    {
        if (networkStream == null || !networkStream.CanWrite)
            return false;

        try
        {
            // 发送格式：[4字节大端序长度][JPG数据]
            byte[] sizeBytes = System.BitConverter.GetBytes(jpgData.Length);
            if (System.BitConverter.IsLittleEndian)
                System.Array.Reverse(sizeBytes);
            
            networkStream.Write(sizeBytes, 0, 4);
            networkStream.Write(jpgData, 0, jpgData.Length);
            return true;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[QuestStreamSender] 发送错误: {e.Message}");
            return false;
        }
    }

    void CloseConnection()
    {
        try
        {
            if (networkStream != null)
            {
                networkStream.Close();
                networkStream.Dispose();
                networkStream = null;
            }
            if (tcpClient != null)
            {
                tcpClient.Close();
                tcpClient.Dispose();
                tcpClient = null;
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"Error closing connection: {e.Message}");
        }
    }

    void OnDestroy()
    {
        StopStream();
    }

    /// <summary>
    /// 获取当前连接状态
    /// </summary>
    public bool IsStreaming => isStreaming;
}
