using System;
using System.Collections.Generic;
using Meta.XR;
using OpenCVForUnity.Calib3dModule;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.ObjdetectModule;
using OpenCVForUnity.UnityIntegration;
using OpenCVForUnity.UnityIntegration.Helper.AR;
using OpenCVForUnity.UnityIntegration.Helper.Source2Mat;
using UnityEngine;

public class ArucoPNP : MonoBehaviour
{
   
    private struct DetectionResult
    {
        public int MarkerId;
        public Vector2[] ImagePoints;
        public Vector3[] ObjectPoints;
    }

    public double MarkerLength = 0.045f;
    
    public ARGameObject aRGameObject0;
    public ARGameObject aRGameObject1;
    
    ArucoDetector _arucoDetector;
    private Mat frameMat;

    private Mat grayMat;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        DetectorParameters detectorParams = new DetectorParameters();
        detectorParams.set_minDistanceToBorder(3);
        detectorParams.set_useAruco3Detection(true);
        detectorParams.set_cornerRefinementMethod(Objdetect.CORNER_REFINE_SUBPIX);
        detectorParams.set_minSideLengthCanonicalImg(14);
        detectorParams.set_errorCorrectionRate(0.6);
        RefineParameters refineParameters = new RefineParameters(9f, 3f, true);
        _arucoDetector = new ArucoDetector(
            Objdetect.getPredefinedDictionary(Objdetect.DICT_4X4_50), 
            detectorParams, refineParameters);
    }

    // Update is called once per frame
    void Update()
    {
        
    }

    public void Detect(Texture2D t2d, Pose cameraPose, Mat cameraIntrinsics)
    {
        if (frameMat == null)
        {
            frameMat = new Mat(t2d.height, t2d.width, CvType.CV_8UC4);
        }

        if (grayMat == null)
        {
            grayMat = new Mat(t2d.height, t2d.width, CvType.CV_8UC1);
        }
        OpenCVMatUtils
            .Texture2DToMat(t2d, frameMat);
        Imgproc.cvtColor(
            frameMat,
            grayMat,
            Source2MatHelperUtils.ColorConversionCodes(
                Source2MatHelperColorFormat.RGBA,
                Source2MatHelperColorFormat.GRAY
            )
        );
        
        List<Mat> corners = new List<Mat>();
        Mat ids = new Mat();
        List<Mat> rejectedCorners = new List<Mat>();
        _arucoDetector.detectMarkers(grayMat, corners, ids, rejectedCorners);
        //Calib3d.undistortPoints(corners, ids, cameraMatrix, distCoeffs);
        
        List<DetectionResult> detectionResults = EstimatePoseCanonicalMarker(corners, ids);
        bool findFirst0 = false;
        bool findFirst1 = false;
        foreach (DetectionResult result in detectionResults)
        {
            if (result.MarkerId == 0 && aRGameObject0 != null && !findFirst0)
            {
                findFirst0 = true;

                SolvePnPToUnityWorldPose(
                    result.ImagePoints, 
                    result.ObjectPoints, 
                    cameraPose, 
                    out Pose resultPose, 
                    cameraIntrinsics);
                aRGameObject0.transform.position = resultPose.position;
                aRGameObject0.transform.rotation = resultPose.rotation;
            }
            if (result.MarkerId == 1 && aRGameObject1 != null && !findFirst1)
            {
                findFirst1 = true;
                SolvePnPToUnityWorldPose(result.ImagePoints, result.ObjectPoints, cameraPose, out Pose resultPose);
                aRGameObject1.transform.position = resultPose.position;
                aRGameObject1.transform.rotation = resultPose.rotation;
            }

            if (findFirst0 && findFirst1)
            {
                break;
            }
        }
    }
    
    private List<DetectionResult> EstimatePoseCanonicalMarker(List<Mat> corners, Mat ids)
    {
        using (MatOfPoint3f objectPoints = new MatOfPoint3f(
                   new Point3(-MarkerLength / 2f, MarkerLength / 2f, 0),
                   new Point3(MarkerLength / 2f, MarkerLength / 2f, 0),
                   new Point3(MarkerLength / 2f, -MarkerLength / 2f, 0),
                   new Point3(-MarkerLength / 2f, -MarkerLength / 2f, 0)
               ))
        {
            // Store detection results for thread-safe transfer to main thread
            List<DetectionResult> detectionResults = new List<DetectionResult>();

#if NET_STANDARD_2_1 && !OPENCV_DONT_USE_UNSAFE_CODE
            Span<int> idsValues = ids.AsSpan<int>();
#else
                int[] idsArray = new int[ids.total() * ids.channels()];
                ids.get(0, 0, idsArray);
                int[] idsValues = idsArray;
#endif

            for (int i = 0; i < idsValues.Length; i++)
            {
                using (Mat corner_4x1 = corners[i].reshape(2, 4)) // 1*4*CV_32FC2 => 4*1*CV_32FC2
                using (MatOfPoint2f imagePoints = new MatOfPoint2f(corner_4x1))
                {
                    // Convert to thread-safe data structures
                    DetectionResult result = new DetectionResult
                    {
                        MarkerId = idsValues[i],
                        ImagePoints = imagePoints.toVector2Array(),
                        ObjectPoints = objectPoints.toVector3Array()
                    };
                    detectionResults.Add(result);
                }
            }

            return detectionResults;
        }
    }
    
    public static bool SolvePnPToUnityWorldPose(
        Vector2[] imagePoints,
        Vector3[] objectPoints,
        
        Pose cameraPose,           // 新增参数：相机世界位姿
        out Pose unityWorldPose,
        Mat camMatrix = null,
        MatOfDouble distCoeffs = null,
        bool objectPointsAreLeftHanded = false
    )
    {
        // 如果 distCoeffs 为 null，则使用默认零畸变
        if (distCoeffs == null)
            distCoeffs = new MatOfDouble(new double[] { 0, 0, 0, 0, 0 });

        // 如果 camMatrix 为 null，则初始化为单位矩阵
        if (camMatrix == null)
        {
            camMatrix = Mat.eye(3, 3, CvType.CV_64FC1);
            camMatrix.put(0, 0, 640f);
            camMatrix.put(0, 2, 320f);
            camMatrix.put(1, 1, 640f);
            camMatrix.put(1, 2, 240f);
        }

        unityWorldPose = default;

        // ---------- 基本校验 ----------
        if (imagePoints == null || objectPoints == null)
            return false;
        if (imagePoints.Length != objectPoints.Length || imagePoints.Length < 4)
            return false;

        // ---------- Unity → OpenCV 坐标系 ----------
        Vector3[] cvObjectPoints = new Vector3[objectPoints.Length];
        for (int i = 0; i < objectPoints.Length; i++)
        {
            Vector3 p = objectPoints[i];
            cvObjectPoints[i] = objectPointsAreLeftHanded
                ? new Vector3(p.x, -p.y, p.z) // 翻转 Y
                : p;
        }

        MatOfPoint3f objPts = new MatOfPoint3f(cvObjectPoints);
        MatOfPoint2f imgPts = new MatOfPoint2f(imagePoints);

        using Mat rvec = new Mat(3, 1, CvType.CV_64FC1);
        using Mat tvec = new Mat(3, 1, CvType.CV_64FC1);

        // ---------- SolvePnP ----------
        bool success = Calib3d.solvePnP(
            objPts,
            imgPts,
            camMatrix,
            distCoeffs,
            rvec,
            tvec,
            false,
            Calib3d.SOLVEPNP_ITERATIVE
        );

        if (!success)
            return false;

        // ---------- OpenCV → Unity 局部坐标 ----------
        OpenCVARUtils.PoseData poseData = OpenCVARUtils.ConvertRvecTvecToPoseData(rvec, tvec);
        var _transformMatrix = OpenCVARUtils.ConvertPoseDataToMatrix(ref poseData, true);
        
        Matrix4x4 matrix = Matrix4x4.TRS(cameraPose.position, cameraPose.rotation, Vector3.one) * _transformMatrix;


        unityWorldPose = new Pose(
            OpenCVARUtils.ExtractTranslationFromMatrix(ref matrix),
            OpenCVARUtils.ExtractRotationFromMatrix(ref matrix)
        );

        return true;
    }

}
