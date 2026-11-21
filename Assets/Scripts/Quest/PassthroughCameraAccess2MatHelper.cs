using System;
using System.Collections;
using System.Threading.Tasks;
using Meta.XR;
using NUnit.Framework.Constraints;
using UnityEngine;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.UnityIntegration;
using OpenCVForUnity.UnityIntegration.Helper.Source2Mat;
using UnityEngine.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Bson;

#if UNITY_EDITOR
using OpenCVForUnity.UnityIntegration.Helper.Source2Mat.Editor;
#endif

[Serializable]
public class PassthroughCameraAccessData
{
    [JsonIgnore]
    public Mat frameMat;
    [JsonIgnore]
    public Mat grayMat;
    [JsonIgnore]
    public Matrix4x4 cameraPose;
    [JsonProperty("cameraPose")]
    private float[] CameraPoseArray
    {
        get
        {
            if (cameraPose == null) return new float[16];
            var array = new float[16];
            for (int i = 0; i < 16; i++)
                array[i] = cameraPose[i];
            return array;
        }
    }

    [JsonProperty("cameraIntrinsics")]
    private double[] CameraIntrinsicsArray
    {
        get
        {
            var array = new double[]
            {
                PassthroughCameraAccess2MatHelper._intrinsicsMatrix.get(0,0)[0],
                PassthroughCameraAccess2MatHelper._intrinsicsMatrix.get(1,1)[0],

                PassthroughCameraAccess2MatHelper._intrinsicsMatrix.get(0,2)[0],
                PassthroughCameraAccess2MatHelper._intrinsicsMatrix.get(1,2)[0],
            };
            return array;
        }
    }
    public double cameraTimestamp;
    public double unityTimestamp;

    public PassthroughCameraAccessData(Mat grayMat, Matrix4x4 cameraPose, double cameraTimestamp, double unityTimestamp)
    {
        this.grayMat = grayMat;
        this.frameMat = new Mat(grayMat.height(),grayMat.width(),CvType.CV_8UC4);
        this.cameraPose = cameraPose;
        this.cameraTimestamp = cameraTimestamp;
        this.unityTimestamp = unityTimestamp;
    }

    public PassthroughCameraAccessData(int height, int width, Matrix4x4 cameraPose, double cameraTimestamp, double unityTimestamp)
    {
        this.frameMat = new Mat(height,width,CvType.CV_8UC4);
        this.grayMat = new Mat(height,width,CvType.CV_8UC1);
        this.cameraPose = cameraPose;
        this.cameraTimestamp = cameraTimestamp;
        this.unityTimestamp = unityTimestamp;
    }
}



public class PassthroughCameraAccess2MatHelper : MonoBehaviour, IDisposable
{
    public PassthroughCameraAccess cameraAccess;

    public static Texture texture;
    
    private Mat frameMat;
    private Mat grayMat;
    
    public static Matrix4x4 _cameraLocalToWorldMatrix_buffer;
    public static Mat _intrinsicsMatrix;
    public static bool enabled = false;

    public RawImage display;
    void Awake()
    {
        //Application.targetFrameRate = 60;
        //OVRManager.display.displayFrequency = 60f;
    }

    void Initiation()
    {
        frameMat = new Mat(cameraAccess.CurrentResolution.y, cameraAccess.CurrentResolution.x, CvType.CV_8UC4);
        grayMat = new Mat(cameraAccess.CurrentResolution.y, cameraAccess.CurrentResolution.x, CvType.CV_8UC1);
    }
    
    public Mat BuildIntrinsicsMatrix(PassthroughCameraAccess.CameraIntrinsics intrinsics)
    {
        float fx = intrinsics.FocalLength.x * cameraAccess.CurrentResolution.x / intrinsics.SensorResolution[0];
        float fy = intrinsics.FocalLength.y * cameraAccess.CurrentResolution.x / intrinsics.SensorResolution[0];

        float cx = intrinsics.PrincipalPoint.x * cameraAccess.CurrentResolution.x / intrinsics.SensorResolution[0];
        float cy = intrinsics.PrincipalPoint.y * cameraAccess.CurrentResolution.y / intrinsics.SensorResolution[1];


        print($"fx{fx},fy{fy},cx{cx},cy{cy}");

        return PassthroughCameraAccessArUco.CreateCameraMatrix(fx, fy, cx, cy);
    }

    private IEnumerator Start()
    {
        Initiation();
        OVRPermissionsRequester.Request(new[]
        {
            OVRPermissionsRequester.Permission.Scene,
            OVRPermissionsRequester.Permission.PassthroughCameraAccess
        });
        while (!cameraAccess.IsPlaying)
        {
            yield return null;
        }
        _intrinsicsMatrix = BuildIntrinsicsMatrix(cameraAccess.Intrinsics);
        texture = cameraAccess.GetTexture();
        /*if (display != null)
        {
            display.texture = texture;
        }*/

        enabled = true;
    }

    private void Update()
    {

    }

    /*public PassthroughCameraAccessData GetPassthroughCameraAccessData()
    {
        if (!enabled)
        {
            Debug.LogError("camera access is not playing");
            return null;
        }

        // 获取当前帧的 Texture（你自己的 cameraAccess 中应该有）
        if (texture == null)
            return null;

        // 确保 frameMat 已经创建好尺寸
        if (frameMat == null || frameMat.width() != texture.width || frameMat.height() != texture.height)
        {
            frameMat?.Dispose();
            frameMat = new Mat(texture.height, texture.width, CvType.CV_8UC4);
        }

        // 异步 GPU Readback → Mat
        try
        {
            if (texture is Texture2D t2d)
            {
                OpenCVMatUtils.Texture2DToMat(t2d, frameMat);
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"TextureToMatAsync failed: {e.Message}");
            return null;
        }

        // 灰度 Mat（如果未创建则创建）
        if (grayMat == null || grayMat.width() != frameMat.width() || grayMat.height() != frameMat.height())
        {
            grayMat?.Dispose();
            grayMat = new Mat(frameMat.height(), frameMat.width(), CvType.CV_8UC1);
        }

        // RGBA → GRAY
        Imgproc.cvtColor(
            frameMat,
            grayMat,
            Source2MatHelperUtils.ColorConversionCodes(
                Source2MatHelperColorFormat.RGBA,
                Source2MatHelperColorFormat.GRAY
            )
        );

        // 获取同步过的相机姿态
        Pose pose = cameraAccess.GetCameraPose();
        
        _cameraLocalToWorldMatrix_buffer = Matrix4x4.TRS(pose.position, pose.rotation, Vector3.one);

        return new PassthroughCameraAccessData(
            grayMat, 
            _cameraLocalToWorldMatrix_buffer, 
            new DateTimeOffset(cameraAccess.Timestamp).ToUnixTimeMilliseconds()/1000.0, 
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000.0);
    }*/
    
    public void Dispose()
    {
        ReleaseMats();
    }

    private void ReleaseMats()
    {
        frameMat?.Dispose();
        frameMat = null;

        grayMat?.Dispose();
        grayMat = null;
    }

    private void OnDestroy()
    {
        Dispose();
    }
}
