using UnityEngine;
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.IO;
using TMPro;
using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Net.NetworkInformation;

/// <summary>
/// Unity TCP HTTP JSON Receiver（Android / IL2CPP 可用，支持多请求连续接收）
/// 
/// 接口：
// — POST /unity         位姿数据
// — POST /record        录制控制（start / finish / mode）
/// 
/// 规则：
// — 后台线程：网络 + JSON
// — 主线程：所有 Unity API
/// </summary>
public class UnityHttpReceiver : MonoBehaviour
{
    [Header("Server")]
    public int serverPort = 4050;

    [Header("UI")]
    public TextMeshProUGUI title;

    public TextMeshProUGUI leftMessage;
    public TextMeshProUGUI rightMessage;
    public TextMeshProUGUI result;

    [Header("Recorder（推荐拖引用）")]
    public RecorderControllerPure recorder;

    private TcpListener listener;
    private Thread listenerThread;
    private volatile bool isRunning;

    private readonly ConcurrentQueue<string> leftMessageQueue = new();
    private readonly ConcurrentQueue<string> rightMessageQueue = new();
    private readonly ConcurrentQueue<string> uiQueue = new();
    private readonly ConcurrentQueue<Action> mainThreadActions = new();

    public static bool calibrated = false;
    public Transform leftPhoneCameraCenter;
    public Transform rightPhoneCameraCenter;
    public GameObject leftDebug;
    public GameObject rightDebug;
    public static Matrix4x4 flipX;
    public static Matrix4x4 iphone2unity;

    [Header("Received Pose")] 
    public static Matrix4x4 leftPoseIphone;

    public static float leftTimestampMSprev;
    public static Matrix4x4 rightPoseIphone;
    public static float rightTimestampMSprev;

    void Awake()
    {
        if (recorder == null)
            recorder = FindAnyObjectByType<RecorderControllerPure>();
        flipX = Matrix4x4.Scale(new Vector3(1, 1, -1));
        iphone2unity = Matrix4x4.identity;
        leftPoseIphone = Matrix4x4.identity;
        rightPoseIphone = Matrix4x4.identity;
    }

    public static string GetWifiIPv4()
    {
        try
        {
            // 获取 Unity Activity
            AndroidJavaClass unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            AndroidJavaObject activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");

            // 获取 WifiManager
            AndroidJavaObject wifiManager = activity.Call<AndroidJavaObject>("getSystemService", "wifi");

            // 获取 WifiInfo
            AndroidJavaObject wifiInfo = wifiManager.Call<AndroidJavaObject>("getConnectionInfo");

            // 获取 IP（整型）
            int ipInt = wifiInfo.Call<int>("getIpAddress");

            // 转成字符串形式
            string ipStr = string.Format("{0}.{1}.{2}.{3}",
                ipInt & 0xFF,
                (ipInt >> 8) & 0xFF,
                (ipInt >> 16) & 0xFF,
                (ipInt >> 24) & 0xFF);

            return ipStr;
        }
        catch (System.Exception e)
        {
            ULog.LogError("Failed to get Wi-Fi IP: " + e);
            return "Unavailable";
        }
    }
    
    public static string GetWindowsIPv4()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;

            // 只要物理网卡
            if (ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet &&
                ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)
                continue;

            // // 排除 Tailscale / VPN
            // if (ni.Description.Contains("Tailscale") ||
            //     ni.Description.Contains("VPN"))
            //     continue;

            var ipProps = ni.GetIPProperties();

            foreach (var addr in ipProps.UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    // 排除 169.254.x.x
                    if (!addr.Address.ToString().StartsWith("169.254"))
                        return addr.Address.ToString();
                }
            }
        }

        return "Unavailable";
    }


    void OnEnable() => StartServer();
    void OnDisable() => StopServer();
    void OnApplicationPause(bool pause)
    {
        if (pause) StopServer();
        else StartServer();
    }

    void Update()
    {
        while (leftMessageQueue.TryDequeue(out var msg))
            if (leftMessage) leftMessage.text = msg;
        
        while (rightMessageQueue.TryDequeue(out var msg))
            if (rightMessage) rightMessage.text = msg;

        while (mainThreadActions.TryDequeue(out var action))
            action?.Invoke();
        if (calibrated)
        {
            var resultMat = TransferAR2Unity(true);
            //result.text = ToDebugString(resultMat);
            ApplyMatrixToTransform(leftDebug.transform,resultMat);
            ApplyMatrixToTransform(rightDebug.transform,TransferAR2Unity(false));
        }
    }
    
    public static string ToDebugString(Matrix4x4 m, int decimals = 3)
    {
        string fmt = "F" + decimals;
        StringBuilder sb = new StringBuilder();

        for (int row = 0; row < 4; row++)
        {
            sb.Append("[ ");
            for (int col = 0; col < 4; col++)
            {
                sb.Append(m[row, col].ToString(fmt));
                if (col < 3) sb.Append(", ");
            }
            sb.AppendLine(" ]");
        }

        return sb.ToString();
    }

    public void StartServer()
    {
        if (isRunning) return;

        try
        {
            listener = new TcpListener(IPAddress.Any, serverPort);
            listener.Start();

            isRunning = true;
            listenerThread = new Thread(ServerLoop) { IsBackground = true };
            listenerThread.Start();

            if (title)
            {
#if UNITY_EDITOR || WINDOWS_UWP
                title.text = $"Listening on {GetWindowsIPv4()}:{serverPort}";
#else
                title.text = $"Listening on {GetWifiIPv4()}:{serverPort}";
#endif
            }
            
                
            ULog.Log("TCP Server started");
        }
        catch (Exception ex)
        {
            ULog.LogError(ex);
            if (title) title.text = "StartServer failed: " + ex.Message;
        }
    }

    public void StopServer()
    {
        if (!isRunning) return;
        isRunning = false;

        try
        {
            listener?.Stop();
            listener = null;

            listenerThread?.Join(300);
            listenerThread = null;

            ULog.Log("TCP Server stopped");
        }
        catch (Exception ex)
        {
            ULog.LogError(ex);
        }
    }

    private void ServerLoop()
    {
        while (isRunning)
        {
            try
            {
                if (!listener.Pending())
                {
                    Thread.Sleep(5);
                    continue;
                }

                TcpClient client = listener.AcceptTcpClient();
                ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
            }
            catch (SocketException) { break; }
            catch (Exception ex) { ULog.LogError(ex); }
        }
    }

    /*private void HandleClient(TcpClient client)
    {
        try
        {
            string clientIP = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();

            using var stream = client.GetStream();
            var buffer = new List<byte>();

            var tmp = new byte[4096];
            while (client.Connected)
            {
                int bytesRead = stream.Read(tmp, 0, tmp.Length);
                if (bytesRead <= 0) break;

                buffer.AddRange(tmp[..bytesRead]);

                // 循环处理所有完整 HTTP 请求
                while (TryParseHttpRequest(buffer, out string path, out string body, out int requestLength))
                {
                    buffer.RemoveRange(0, requestLength);

                    switch (path)
                    {
                        case "/unity": HandleUnity(body); Send200(stream); break;
                        case "/record": HandleRecord(body, clientIP); Send200(stream); break;
                        default: Send404(stream); break;
                    }
                }
            }
        }
        catch (Exception ex) { ULog.LogError(ex); }
        finally { client.Close(); }
    }*/
    
    private void HandleClient(TcpClient client)
    {
        var remoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
        string clientIp = remoteEndPoint?.Address.ToString();

        try
        {
            using var stream = client.GetStream();
            var buffer = new List<byte>();
            var tmp = new byte[4096];

            while (true)
            {
                int bytesRead;
                try
                {
                    bytesRead = stream.Read(tmp, 0, tmp.Length);
                }
                catch (IOException)
                {
                    // 客户端强制断开（RST）—— 正常情况
                    break;
                }

                if (bytesRead <= 0)
                    break;

                buffer.AddRange(tmp.AsSpan(0, bytesRead).ToArray());

                while (TryParseHttpRequest(buffer, out string path, out string body, out int requestLength))
                {
                    buffer.RemoveRange(0, requestLength);

                    switch (path)
                    {
                        case "/unity":
                            HandleUnity(body);
                            Send200(stream);
                            break;
                        case "/record":
                            HandleRecord(body, clientIp);
                            Send200(stream);
                            break;
                        default:
                            Send404(stream);
                            break;
                    }

                    // 👇 HTTP/1.1 非 keep-alive，处理完一次就断
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            ULog.LogError(ex);
        }
        finally
        {
            client.Close();
        }
    }


    // ---------------- HTTP 解析器 ----------------
    // 返回 true = 成功解析一个请求
    private bool TryParseHttpRequest(List<byte> buffer, out string path, out string body, out int totalLength)
    {
        path = null;
        body = null;
        totalLength = 0;

        // 查找头部结束 "\r\n\r\n"
        int headerEnd = -1;
        for (int i = 0; i < buffer.Count - 3; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
            {
                headerEnd = i + 4;
                break;
            }
        }
        if (headerEnd == -1) return false; // 还没完整 header

        // 解析 header
        string headerStr = Encoding.UTF8.GetString(buffer.GetRange(0, headerEnd).ToArray());
        string[] lines = headerStr.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return false;

        string[] reqParts = lines[0].Split(' ');
        if (reqParts.Length < 2) return false;
        path = reqParts[1];

        int contentLength = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(lines[i].Split(':')[1].Trim(), out int cl))
                    contentLength = cl;
            }
        }

        int expectedTotal = headerEnd + contentLength;
        if (buffer.Count < expectedTotal) return false; // body 还没到齐

        body = Encoding.UTF8.GetString(buffer.GetRange(headerEnd, contentLength).ToArray());
        totalLength = expectedTotal;
        return true;
    }

    // ---------------- API Handlers ----------------
    private void HandleUnity(string body)
    {
        UnityData data;
        try
        {
            data = JsonUtility.FromJson<UnityData>(body);
        }
        catch
        {
            ULog.LogError("Invalid JSON: " + body);
            return;
        }
        if (data == null ||
            data.pos == null || data.pos.Length < 3 ||
            data.quat == null || data.quat.Length < 4 ||
            string.IsNullOrEmpty(data.camera_position))
        {
            ULog.LogError("Invalid UnityData content: " + body);
            return;
        }
        string msg = $"t={data.t:0.000}\n" +
                      $"px=({data.pos?[0]:0.000}\n"+
                      $"py=({data.pos?[1]:0.000}\n"+
                      $"pz=({data.pos?[2]:0.000}\n"+
                      $"qw=({data.quat?[0]:0.000}\n"+
                      $"qx=({data.quat?[1]:0.000}\n"+
                      $"qy=({data.quat?[2]:0.000}\n"+
                      $"qz=({data.quat?[3]:0.000}\n";
        Vector3 position = new Vector3(
            x:data.pos[0],
            y:data.pos[1],
            z:data.pos[2]
        );
        Quaternion rotation = new Quaternion(
            x: data.quat[1],
            y: data.quat[2],
            z: data.quat[3],
            w: data.quat[0]
        );
        if (data.camera_position.Contains("left"))
        {
            leftTimestampMSprev = data.t * 1000.0f;
            leftPoseIphone = Matrix4x4.TRS(position, rotation, Vector3.one);
            msg = "[left]\n" + msg;
            leftMessageQueue.Enqueue(msg);
        }else if (data.camera_position.Contains("right"))
        {
            rightTimestampMSprev = data.t * 1000.0f;
            rightPoseIphone = Matrix4x4.TRS(position, rotation, Vector3.one);
            msg = "[right]\n" + msg;
            rightMessageQueue.Enqueue(msg);
        }
    }

    private void HandleRecord(string body, string clientIP)
    {
        uiQueue.Enqueue($"[RECORD] {body}");
        JObject root;
        try { root = JObject.Parse(body); } 
        catch { ULog.LogError("Invalid JSON"); return; }

        if (root.ContainsKey("start"))
        {
            uiQueue.Enqueue("[RECORD] START");
            mainThreadActions.Enqueue(() => recorder?.StartRecording(clientIP));
        }
        else if (root.ContainsKey("finish"))
        {
            if (root.TryGetValue("finish", out var finishToken))
            {
                var preMetaToken = finishToken["premeta"];
                if (preMetaToken != null)
                {
                    try
                    {
                        FramePusher.preMeta = preMetaToken.ToObject<PreMeta>();
                        uiQueue.Enqueue("[RECORD] STOP");
                        mainThreadActions.Enqueue(() => recorder?.StopRecording(clientIP));
                    }
                    catch (Exception e)
                    {
                        ULog.LogError($"StopRecording error: {e}");
                    }
                }
            }
        }
        else if (root.ContainsKey("mode"))
        {

        }
    }

    // ---------------- HTTP Responses ----------------
    private void Send200(NetworkStream stream)
    {
        byte[] resp = Encoding.UTF8.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: application/json\r\n" +
            "Content-Length: 15\r\n\r\n" +
            "{\"status\":\"ok\"}"
        );
        stream.Write(resp, 0, resp.Length);
    }

    private void Send404(NetworkStream stream)
    {
        byte[] resp = Encoding.UTF8.GetBytes(
            "HTTP/1.1 404 Not Found\r\n" +
            "Content-Length: 0\r\n\r\n"
        );
        stream.Write(resp, 0, resp.Length);
    }
    
    struct CalibrationError
    {
        public float positionErrorMm;
        public float rotationErrorDeg;
    }

    public void CalibrateArUco()
    {
        float currentTimestamp = TimeSync.GetSyncedTimestampMs();
        bool leftCalibrated = false;
        bool rightCalibrated = false;
        Vector3 pos = Vector3.zero;
        Quaternion rot =  Quaternion.identity;
        CalibrationError debugErrors = new CalibrationError();
        if(leftPhoneCameraCenter!=null && currentTimestamp-leftTimestampMSprev<1000f/30)
        {
            calibrated = true;
            leftCalibrated = true;
            //先将vr场景中的gameobject坐标转换为右手坐标系
            Matrix4x4 unityPivot_rightHandCoor = flipX * Matrix4x4.TRS(leftPhoneCameraCenter.position,leftPhoneCameraCenter.rotation,Vector3.one) * flipX;
            //假设vr场景中的gameobject和http获取的手机ar环境(右手坐标系)leftPoseIphone物理意义上重合，求两者坐标系转换
            Matrix4x4 temp = unityPivot_rightHandCoor * leftPoseIphone.inverse;
            pos = temp.GetPosition();
            rot = temp.rotation;
            
        }

        if (rightPhoneCameraCenter != null && currentTimestamp - rightTimestampMSprev < 1000f / 30)
        {
            calibrated = true;
            rightCalibrated = true;
            Matrix4x4 unityPivot_rightHandCoor = flipX * Matrix4x4.TRS(rightPhoneCameraCenter.position,rightPhoneCameraCenter.rotation,Vector3.one) * flipX;
            //假设vr场景中的gameobject和http获取的手机ar环境(右手坐标系)leftPoseIphone物理意义上重合，求两者坐标系转换
            Matrix4x4 temp = unityPivot_rightHandCoor * rightPoseIphone.inverse;
            if (leftCalibrated)
            {
                debugErrors.positionErrorMm=Vector3.Distance(temp.GetPosition(),pos)*1000f;
                pos = (pos+temp.GetPosition())/2;
                Quaternion tempQ = temp.rotation;
                debugErrors.rotationErrorDeg=Quaternion.Angle(tempQ,rot);
                if (Quaternion.Dot(rot, tempQ) < 0f)
                {
                    tempQ = new Quaternion(-tempQ.x, -tempQ.y, -tempQ.z, -tempQ.w);
                }
                Quaternion qAvg = new Quaternion(
                    rot.x + tempQ.x,
                    rot.y + tempQ.y,
                    rot.z + tempQ.z,
                    rot.w + tempQ.w
                );
                rot = Quaternion.Normalize(qAvg);
            }
            else
            {
                pos = temp.GetPosition();
                rot = temp.rotation;
            }
        }

        if (calibrated)
        {
            iphone2unity = Matrix4x4.TRS(pos, rot, Vector3.one);
            result.text = $"leftDetected:{leftCalibrated};rightDetected:{rightCalibrated}";
            if (leftCalibrated && rightCalibrated)
            {
                result.text = $"\nposEMM:{debugErrors.positionErrorMm};rotEDEG:{debugErrors.rotationErrorDeg};";
            }
        }
    }

    public static Matrix4x4 TransferAR2Unity(bool isLeft)
    {
        if (isLeft)
        {
            //http获取的手机ar环境(右手坐标系)leftPoseIphone 转换到unity vr场景(左手坐标系)
            return flipX * iphone2unity * leftPoseIphone * flipX;
        }
        else
        {
            return flipX * iphone2unity * rightPoseIphone * flipX;
        }
    }
    
    public static void ApplyMatrixToTransform(Transform t, Matrix4x4 m)
    {
        // 1. 位置（第 4 列）
        Vector3 position = m.GetColumn(3);

        // 2. 旋转（从前向和上向重建）
        Vector3 forward = m.GetColumn(2);
        Vector3 up = m.GetColumn(1);
        
        if (forward.sqrMagnitude < 1e-6f || up.sqrMagnitude < 1e-6f)
        {
            return;
        }
        
        Quaternion rotation = Quaternion.LookRotation(forward, up);


        t.SetPositionAndRotation(position, rotation);
    }

}
