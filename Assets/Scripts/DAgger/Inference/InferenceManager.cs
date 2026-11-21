using Newtonsoft.Json.Linq;
using UnityEngine;
using TMPro;
using System.Collections;
using System.Collections.Generic;
using Meta.XR;
using UnityEngine.XR;

public class InferenceManager : MonoBehaviour
{
    public static InferenceManager Instance { get; private set; }

    [Header("IP Address Input")]
    public TMP_InputField leftHandIP;
    public TMP_InputField rightHandIP;
    public TMP_InputField inferenceServerIP;

    public PassthroughCameraAccess leftEyeCamera;

    public Transform rightHandAnchor;

    private bool isInferenceRunning = false;

    private List<GameObject> nodeList = new List<GameObject>();
    [Header("Node Prefab")]
    public GameObject nodePrefab;
    
    void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }

    void Update()
    {
        // 可扩展：自动推理或定时推理
    }

    // 推理入口
    public void Inference()
    {
        isInferenceRunning = true;
        string leftIP = leftHandIP.text;
        string rightIP = rightHandIP.text;
        StartCoroutine(InferenceCoroutine(leftIP, rightIP));
    }

    private IEnumerator InferenceCoroutine(string leftIP, string rightIP)
    {
        string leftWristBase64 = null;
        string rightWristBase64 = null;
        string leftEyeBase64 = null;
        Texture2D leftEyeTex = null;

        bool leftDone = false;
        bool rightDone = false;
        bool eyeDone = false;

        // 左手GET
        StartCoroutine(GetFrameCoroutine(leftIP, "left", val => { leftWristBase64 = val; leftDone = true; }));
        // 右手GET
        StartCoroutine(GetFrameCoroutine(rightIP, "right", val => { rightWristBase64 = val; rightDone = true; }));
        // 左眼图像
        System.Action eyeAction = () => {
            Texture2D srcTex = GetLeftEyeImage();
            // Texture2D -> Mat
            OpenCVForUnity.CoreModule.Mat mat = new OpenCVForUnity.CoreModule.Mat(srcTex.height, srcTex.width, OpenCVForUnity.CoreModule.CvType.CV_8UC3);
            OpenCVForUnity.UnityUtils.Utils.texture2DToMat(srcTex, mat);
            // Resize
            OpenCVForUnity.CoreModule.Mat matResized = new OpenCVForUnity.CoreModule.Mat();
            OpenCVForUnity.ImgprocModule.Imgproc.resize(mat, matResized, new OpenCVForUnity.CoreModule.Size(320, 240));
            // Mat -> JPG bytes
            var encodeParams = new OpenCVForUnity.CoreModule.MatOfInt(OpenCVForUnity.ImgcodecsModule.Imgcodecs.IMWRITE_JPEG_QUALITY, 80);
            var buf = new OpenCVForUnity.CoreModule.MatOfByte();
            OpenCVForUnity.ImgcodecsModule.Imgcodecs.imencode(".jpg", matResized, buf, encodeParams);
            byte[] leftEyeBytes = buf.toArray();
            leftEyeBase64 = System.Convert.ToBase64String(leftEyeBytes);
            eyeDone = true;
        };
        eyeAction.Invoke();

        // 等待全部完成
        while (!(leftDone && rightDone && eyeDone))
        {
            yield return null;
        }

        // 打包
        string json = PackImagesToJson(leftWristBase64, rightWristBase64, leftEyeBase64);
        Debug.Log($"packed JSON: {json}");

        // POST到inference server
        string serverIP = inferenceServerIP.text;
        string url = $"http://{serverIP}:4050/v1/inference";
        using (UnityEngine.Networking.UnityWebRequest request = UnityEngine.Networking.UnityWebRequest.PostWwwForm(url, ""))
        {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);
            request.uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(bodyRaw);
            request.SetRequestHeader("Content-Type", "application/json");
            yield return request.SendWebRequest();
            if (request.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
            {
                Debug.Log($"Inference POST成功: {request.downloadHandler.text}");
                try
                {
                    var responseObj = JObject.Parse(request.downloadHandler.text);
                    var dataArray = responseObj["data"] as JArray;
                    if (dataArray != null)
                    {
                        int count = dataArray.Count;
                        // 扩容
                        while (nodeList.Count < count)
                        {
                            if (nodePrefab != null)
                            {
                                var go = Instantiate(nodePrefab);
                                nodeList.Add(go);
                            }
                        }
                        // 复用/激活并设置位置
                        for (int i = 0; i < count; i++)
                        {
                            var item = dataArray[i];
                            float x = item[0].ToObject<float>();
                            float y = item[1].ToObject<float>();
                            float z = item[2].ToObject<float>();
                            float qw = item[3].ToObject<float>();
                            float qx = item[4].ToObject<float>();
                            float qy = item[5].ToObject<float>();
                            float qz = item[6].ToObject<float>();
                            Vector3 pos = new Vector3(x, y, z);
                            Quaternion rot = new Quaternion(qx, qy, qz, qw);
                            var go = nodeList[i];
                            go.transform.SetPositionAndRotation(pos, rot);
                            if (!go.activeSelf) go.SetActive(true);
                        }
                        // 多余的隐藏
                        for (int i = count; i < nodeList.Count; i++)
                        {
                            if (nodeList[i].activeSelf) nodeList[i].SetActive(false);
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"解析服务器返回数据失败: {ex}");
                }
            }
            else
            {
                Debug.LogError($"Inference POST失败: {request.error}");
            }
        }

        isInferenceRunning = false;
    }

    // GET请求获取左右手图像
    private IEnumerator GetFrameCoroutine(string ip, string hand, System.Action<string> callback)
    {
        if (string.IsNullOrEmpty(ip))
        {
            callback?.Invoke(null);
        }
        else
        {
            string url = $"http://{ip}:4050/getFrame";
            using (UnityEngine.Networking.UnityWebRequest request = UnityEngine.Networking.UnityWebRequest.Get(url))
            {
                yield return request.SendWebRequest();
                if (request.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    FrameResponse response = JsonUtility.FromJson<FrameResponse>(request.downloadHandler.text);
                    Debug.Log($"{hand} img base64: {response.img}");
                    callback?.Invoke(response.img);
                }
                else
                {
                    Debug.LogError($"Failed to get frame from {hand} ({ip}): {request.error}");
                    callback?.Invoke(null);
                }
            }
        }
    }

    // 获取Quest左眼相机图像（假设为640x480）
    public Texture2D GetLeftEyeImage()
    {
        return leftEyeCamera.GetTexture() as Texture2D;
    }

    // 使用OpenCV降分辨率到320x240
    public Texture2D DownscaleImage(Texture2D src)
    {
        // OpenCVForUnity: Texture2D -> Mat
        OpenCVForUnity.CoreModule.Mat mat = new OpenCVForUnity.CoreModule.Mat(src.height, src.width, OpenCVForUnity.CoreModule.CvType.CV_8UC3);
        OpenCVForUnity.UnityUtils.Utils.texture2DToMat(src, mat);
        // Resize
        OpenCVForUnity.CoreModule.Mat matResized = new OpenCVForUnity.CoreModule.Mat();
        OpenCVForUnity.ImgprocModule.Imgproc.resize(mat, matResized, new OpenCVForUnity.CoreModule.Size(320, 240));
        // Mat -> Texture2D
        Texture2D texResized = new Texture2D(320, 240, TextureFormat.RGB24, false);
        OpenCVForUnity.UnityUtils.Utils.matToTexture2D(matResized, texResized);
        return texResized;
    }

    // 打包左右手和左眼图像为JSON
    public string PackImagesToJson(string leftWristBase64, string rightWristBase64, string leftEyeBase64)
    {
        var obj = new JObject();
        if (!string.IsNullOrEmpty(leftWristBase64)) obj["left_wrist_img"] = leftWristBase64;
        if (!string.IsNullOrEmpty(rightWristBase64)) obj["right_wrist_img"] = rightWristBase64;
        if (!string.IsNullOrEmpty(leftEyeBase64)) obj["left_eye_img"] = leftEyeBase64;
        var right = rightHandAnchor?.Find("HandEyeResult");
        if (right != null)
        {
            Vector3 pos = right.position;
            Quaternion rot = right.rotation;
            var initPose = new JArray
            {
                pos.x, pos.y, pos.z,
                rot.w, rot.x, rot.y, rot.z
            };
            obj["init_pose"] = initPose;
        }
        return obj.ToString();
    }

    [System.Serializable]
    public class FrameResponse
    {
        public string img;
    }
}
