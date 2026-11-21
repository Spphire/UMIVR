using UnityEngine;
using System.Collections.Generic;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.Calib3dModule;
using UnityEngine.InputSystem;

public class HandEyeCalibrationManager : MonoBehaviour
{
    // 左手和右手的标定数据
    private List<Mat> left_R_gripper2base = new List<Mat>();
    private List<Mat> left_t_gripper2base = new List<Mat>();
    private List<Mat> left_R_target2cam = new List<Mat>();
    private List<Mat> left_t_target2cam = new List<Mat>();

    private List<Mat> right_R_gripper2base = new List<Mat>();
    private List<Mat> right_t_gripper2base = new List<Mat>();
    private List<Mat> right_R_target2cam = new List<Mat>();
    private List<Mat> right_t_target2cam = new List<Mat>();

    // 请在Inspector中拖入左/右手柄Transform
    public Transform leftHandTransform;
    public Transform rightHandTransform;

    // 用于实例化HandEyeResult的Prefab
    public GameObject handEyeResultPrefab;

    public InputAction leftXAction;
    public InputAction rightAAction;
    public InputAction leftYAction;
    public InputAction rightBAction;
    // 新增左右摇杆 InputAction
    public InputAction leftStickAction;
    public InputAction rightStickAction;

    // 模式切换
    public enum Mode { HandEyeCalibration, FineTune }
    public Mode currentMode = Mode.HandEyeCalibration;

    // 微调相关
    private enum FineTuneAxis { X, Y, Z, RX, RY, RZ }
    private FineTuneAxis fineTuneAxis = FineTuneAxis.X;
    private Transform rightResultObj = null;
    private Transform leftResultObj = null;
    private float fineTuneStep = 0.001f; // 每次微调的步长

    // 模式切换按键（可根据需要自定义）
    public InputAction toggleCalibrationModeAction; // 比如左手Menu键
    public InputAction toggleFineTuneModeAction;    // 比如右手Menu键

    void OnEnable()
    {
        leftXAction.Enable();
        rightAAction.Enable();
        leftYAction.Enable();
        rightBAction.Enable();
        leftStickAction.Enable();
        rightStickAction.Enable();
        // 加载持久化结果
        LoadHandEyeResult(leftHandTransform);
        LoadHandEyeResult(rightHandTransform);
        RegisterWristAnchor(leftHandTransform);
        RegisterWristAnchor(rightHandTransform);
        toggleCalibrationModeAction.Enable();
        toggleFineTuneModeAction.Enable();
    }
    void OnDisable()
    {
        leftXAction.Disable();
        rightAAction.Disable();
        leftYAction.Disable();
        rightBAction.Disable();
        leftStickAction.Disable();
        rightStickAction.Disable();
        toggleCalibrationModeAction.Disable();
        toggleFineTuneModeAction.Disable();
    }

    void Update()
    {
        // 模式切换
        if (toggleCalibrationModeAction.triggered)
        {
            currentMode = Mode.HandEyeCalibration;
            Debug.Log("Mode.HandEyeCalibration");
        }
        if (toggleFineTuneModeAction.triggered)
        {
            currentMode = Mode.FineTune;
            Debug.Log("Mode.FineTune");
            // 微调时获取左右手HandEyeResult引用
            rightResultObj = rightHandTransform.Find("HandEyeResult");
            if (rightResultObj == null)
            {
                GameObject go = handEyeResultPrefab != null ? Instantiate(handEyeResultPrefab, rightHandTransform) : new GameObject("HandEyeResult");
                go.name = "HandEyeResult";
                go.transform.SetParent(rightHandTransform, false);
                rightResultObj = go.transform;
            }
            leftResultObj = leftHandTransform.Find("HandEyeResult");
            if (leftResultObj == null)
            {
                GameObject go = handEyeResultPrefab != null ? Instantiate(handEyeResultPrefab, leftHandTransform) : new GameObject("HandEyeResult");
                go.name = "HandEyeResult";
                go.transform.SetParent(leftHandTransform, false);
                leftResultObj = go.transform;
            }
        }

        if (currentMode == Mode.HandEyeCalibration)
        {
            if (leftXAction.triggered)
                AddLeftSample();
            if (rightAAction.triggered)
                AddRightSample();
            if (leftYAction.triggered)
                CalibrateLeft();
            if (rightBAction.triggered)
                CalibrateRight();
        }
        else if (currentMode == Mode.FineTune)
        {
            // 微调模式下，左右手BA切换轴，左右摇杆上下调整
            // 右手微调
            if (rightBAction.triggered)
            {
                fineTuneAxis = (FineTuneAxis)(((int)fineTuneAxis + 1) % 6);
                Debug.Log($"right switch axis up: {fineTuneAxis}");
            }
            if (rightAAction.triggered)
            {
                fineTuneAxis = (FineTuneAxis)(((int)fineTuneAxis + 5) % 6);
                Debug.Log($"right switch axis down: {fineTuneAxis}");
            }
            Vector2 rightStick = rightStickAction.ReadValue<Vector2>();
            float deltaR = rightStick.y * fineTuneStep;
            if (Mathf.Abs(deltaR) > 1e-6 && rightResultObj != null)
            {
                switch (fineTuneAxis)
                {
                    case FineTuneAxis.X:
                        rightResultObj.localPosition += new Vector3(deltaR, 0, 0);
                        break;
                    case FineTuneAxis.Y:
                        rightResultObj.localPosition += new Vector3(0, deltaR, 0);
                        break;
                    case FineTuneAxis.Z:
                        rightResultObj.localPosition += new Vector3(0, 0, deltaR);
                        break;
                    case FineTuneAxis.RX:
                        rightResultObj.localRotation *= Quaternion.Euler(deltaR * 100f, 0, 0);
                        break;
                    case FineTuneAxis.RY:
                        rightResultObj.localRotation *= Quaternion.Euler(0, deltaR * 100f, 0);
                        break;
                    case FineTuneAxis.RZ:
                        rightResultObj.localRotation *= Quaternion.Euler(0, 0, deltaR * 100f);
                        break;
                }
                SaveHandEyeResult(rightHandTransform.name, rightResultObj.localPosition, rightResultObj.localRotation);
            }

            // 左手微调
            if (leftYAction.triggered)
            {
                fineTuneAxis = (FineTuneAxis)(((int)fineTuneAxis + 1) % 6);
                Debug.Log($"left switch axis up: {fineTuneAxis}");
            }
            if (leftXAction.triggered)
            {
                fineTuneAxis = (FineTuneAxis)(((int)fineTuneAxis + 5) % 6);
                Debug.Log($"left switch axis down: {fineTuneAxis}");
            }
            Vector2 leftStick = leftStickAction.ReadValue<Vector2>();
            float deltaL = leftStick.y * fineTuneStep;
            if (Mathf.Abs(deltaL) > 1e-6 && leftResultObj != null)
            {
                switch (fineTuneAxis)
                {
                    case FineTuneAxis.X:
                        leftResultObj.localPosition += new Vector3(deltaL, 0, 0);
                        break;
                    case FineTuneAxis.Y:
                        leftResultObj.localPosition += new Vector3(0, deltaL, 0);
                        break;
                    case FineTuneAxis.Z:
                        leftResultObj.localPosition += new Vector3(0, 0, deltaL);
                        break;
                    case FineTuneAxis.RX:
                        leftResultObj.localRotation *= Quaternion.Euler(deltaL * 100f, 0, 0);
                        break;
                    case FineTuneAxis.RY:
                        leftResultObj.localRotation *= Quaternion.Euler(0, deltaL * 100f, 0);
                        break;
                    case FineTuneAxis.RZ:
                        leftResultObj.localRotation *= Quaternion.Euler(0, 0, deltaL * 100f);
                        break;
                }
                SaveHandEyeResult(leftHandTransform.name, leftResultObj.localPosition, leftResultObj.localRotation);
            }
        }
    }

    void AddLeftSample()
    {
        Matrix4x4 leftIPhone = UnityHttpReceiver.flipX * UnityHttpReceiver.leftPoseIphone * UnityHttpReceiver.flipX;
        leftIPhone = leftIPhone.inverse;
        
        // 这里请替换为你实际的位姿获取逻辑
        Mat Rg = TransformMatUtil.RotationToMat(leftHandTransform.rotation);
        Mat tg = TransformMatUtil.PositionToMat(leftHandTransform.position);
        Mat Rc = TransformMatUtil.RotationToMat(leftIPhone.rotation);
        Mat tc = TransformMatUtil.PositionToMat(leftIPhone.GetPosition());

        left_R_gripper2base.Add(Rg);
        left_t_gripper2base.Add(tg);
        left_R_target2cam.Add(Rc);
        left_t_target2cam.Add(tc);

        Debug.Log("left hand sample added");
    }

    void AddRightSample()
    {
        Matrix4x4 rightIPhone = UnityHttpReceiver.flipX * UnityHttpReceiver.rightPoseIphone * UnityHttpReceiver.flipX;
        rightIPhone = rightIPhone.inverse;
        
        Mat Rg = TransformMatUtil.RotationToMat(rightHandTransform.rotation);
        Mat tg = TransformMatUtil.PositionToMat(rightHandTransform.position);
        Mat Rc = TransformMatUtil.RotationToMat(rightIPhone.rotation);
        Mat tc = TransformMatUtil.PositionToMat(rightIPhone.GetPosition());

        right_R_gripper2base.Add(Rg);
        right_t_gripper2base.Add(tg);
        right_R_target2cam.Add(Rc);
        right_t_target2cam.Add(tc);

        Debug.Log("right hand sample added");
    }

    void CalibrateLeft()
    {
        try
        {
            int n = left_R_gripper2base.Count;
            if (n < 2)
            {
                Debug.LogError("sampling data is not enough, at least 2 samples are required");
                return;
            }
            Mat R_cam2gripper = new Mat();
            Mat t_cam2gripper = new Mat();
            Calib3d.calibrateHandEye(
                left_R_gripper2base, left_t_gripper2base,
                left_R_target2cam, left_t_target2cam,
                R_cam2gripper, t_cam2gripper,
                Calib3d.CALIB_HAND_EYE_TSAI
            );
            Debug.Log("left calibration finished: " + R_cam2gripper.dump() + " " + t_cam2gripper.dump());
            left_R_gripper2base = new List<Mat>();
            left_t_gripper2base = new List<Mat>();
            left_R_target2cam = new List<Mat>();
            left_t_target2cam = new List<Mat>();
            ApplyCalibrationToBase(leftHandTransform, R_cam2gripper, t_cam2gripper);
        }
        catch (System.Exception ex)
        {
            Debug.LogError("left calibration failed: " + ex.Message);
        }
    }

    void CalibrateRight()
    {
        try
        {
            int n = right_R_gripper2base.Count;
            if (n < 2)
            {
                Debug.LogError("sampling data is not enough, at least 2 samples are required");
                return;
            }
            Mat R_cam2gripper = new Mat();
            Mat t_cam2gripper = new Mat();
            Calib3d.calibrateHandEye(
                right_R_gripper2base, right_t_gripper2base,
                right_R_target2cam, right_t_target2cam,
                R_cam2gripper, t_cam2gripper,
                Calib3d.CALIB_HAND_EYE_TSAI
            );
            Debug.Log("right calibration finished: " + R_cam2gripper.dump() + " " + t_cam2gripper.dump());
            right_R_gripper2base = new List<Mat>();
            right_t_gripper2base = new List<Mat>();
            right_R_target2cam = new List<Mat>();
            right_t_target2cam = new List<Mat>();
            ApplyCalibrationToBase(rightHandTransform, R_cam2gripper, t_cam2gripper);
        }
        catch (System.Exception ex)
        {
            Debug.LogError("right hand calibration failed: " + ex.Message);
        }
    }
    // 工具：Mat(R,t) <-> Matrix4x4
    Matrix4x4 MatToMatrix4x4(Mat R, Mat t)
    {
        Matrix4x4 m = Matrix4x4.identity;
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
                m[row, col] = (float)R.get(row, col)[0];
        m.SetColumn(3, new Vector4((float)t.get(0, 0)[0], (float)t.get(1, 0)[0], (float)t.get(2, 0)[0], 1));
        return m;
    }
    Mat Matrix4x4ToMatR(Matrix4x4 m)
    {
        Mat R = new Mat(3, 3, CvType.CV_64F);
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
                R.put(row, col, m[row, col]);
        return R;
    }
    Mat Matrix4x4ToMatT(Matrix4x4 m)
    {
        Mat t = new Mat(3, 1, CvType.CV_64F);
        t.put(0, 0, m[0, 3]);
        t.put(1, 0, m[1, 3]);
        t.put(2, 0, m[2, 3]);
        return t;
    }

    void ApplyCalibrationToBase(Transform gripperTransform, Mat R, Mat t)
    {
        // R, t 已经是 target 在 gripper 下的旋转和平移
        // 1. 查找或创建HandEyeResult子物体
        Transform resultObj = gripperTransform.Find("HandEyeResult");
        if (resultObj == null)
        {
            GameObject go;
            if (handEyeResultPrefab != null)
                go = Instantiate(handEyeResultPrefab, gripperTransform);
            else
                go = new GameObject("HandEyeResult");
            go.name = "HandEyeResult";
            go.transform.SetParent(gripperTransform, false);
            resultObj = go.transform;
        }
        // 2. Mat转Unity坐标
        Matrix4x4 m = Matrix4x4.identity;
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
                m[row, col] = (float)R.get(row, col)[0];
        // 把t写入m的平移部分
        m.SetColumn(3, new Vector4(
            (float)t.get(0, 0)[0],
            (float)t.get(1, 0)[0],
            (float)t.get(2, 0)[0],
            1f));
        resultObj.localRotation = m.rotation;
        resultObj.localPosition = m.GetPosition();

        // 3. 持久化保存
        SaveHandEyeResult(gripperTransform.name, resultObj.localPosition, resultObj.localRotation);
        RegisterWristAnchor(gripperTransform, resultObj);
    }

    void SaveHandEyeResult(string handName, Vector3 pos, Quaternion rot)
    {
        PlayerPrefs.SetFloat(handName + "_her_x", pos.x);
        PlayerPrefs.SetFloat(handName + "_her_y", pos.y);
        PlayerPrefs.SetFloat(handName + "_her_z", pos.z);
        PlayerPrefs.SetFloat(handName + "_her_rx", rot.x);
        PlayerPrefs.SetFloat(handName + "_her_ry", rot.y);
        PlayerPrefs.SetFloat(handName + "_her_rz", rot.z);
        PlayerPrefs.SetFloat(handName + "_her_rw", rot.w);
        PlayerPrefs.Save();
    }

    void LoadHandEyeResult(Transform gripperTransform)
    {
        string handName = gripperTransform.name;
        if (PlayerPrefs.HasKey(handName + "_her_x"))
        {
            Vector3 pos = new Vector3(
                PlayerPrefs.GetFloat(handName + "_her_x"),
                PlayerPrefs.GetFloat(handName + "_her_y"),
                PlayerPrefs.GetFloat(handName + "_her_z")
            );
            Quaternion rot = new Quaternion(
                PlayerPrefs.GetFloat(handName + "_her_rx"),
                PlayerPrefs.GetFloat(handName + "_her_ry"),
                PlayerPrefs.GetFloat(handName + "_her_rz"),
                PlayerPrefs.GetFloat(handName + "_her_rw")
            );
            Transform resultObj = gripperTransform.Find("HandEyeResult");
            if (resultObj == null)
            {
                GameObject go;
                if (handEyeResultPrefab != null)
                    go = Instantiate(handEyeResultPrefab, gripperTransform);
                else
                    go = new GameObject("HandEyeResult");
                go.name = "HandEyeResult";
                go.transform.SetParent(gripperTransform, false);
                resultObj = go.transform;
            }
            resultObj.localPosition = pos;
            resultObj.localRotation = rot;
            RegisterWristAnchor(gripperTransform, resultObj);
        }
    }

    void RegisterWristAnchor(Transform gripperTransform)
    {
        if (gripperTransform == null)
            return;

        Transform resultObj = gripperTransform.Find("HandEyeResult");
        RegisterWristAnchor(gripperTransform, resultObj != null ? resultObj : gripperTransform);
    }

    void RegisterWristAnchor(Transform gripperTransform, Transform wristAnchor)
    {
        if (gripperTransform == null || wristAnchor == null)
            return;

        FramePusher framePusher = FindAnyObjectByType<FramePusher>();
        if (framePusher == null)
            return;

        if (gripperTransform == leftHandTransform)
            framePusher.leftWristAnchor = wristAnchor;
        else if (gripperTransform == rightHandTransform)
            framePusher.rightWristAnchor = wristAnchor;
    }

    // 示例：获取相机位姿（请根据实际情况替换）
    Quaternion GetCameraRotation()
    {
        // 这里假设主相机为目标
        return Camera.main.transform.rotation;
    }
    Vector3 GetCameraPosition()
    {
        return Camera.main.transform.position;
    }
}

// Transform到Mat的转换工具
public static class TransformMatUtil
{
    public static Mat RotationToMat(Quaternion q)
    {
        Matrix4x4 m = Matrix4x4.Rotate(q);
        Mat R = new Mat(3, 3, CvType.CV_64F);
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
                R.put(row, col, m[row, col]);
        return R;
    }
    public static Mat PositionToMat(Vector3 pos)
    {
        Mat t = new Mat(3, 1, CvType.CV_64F);
        t.put(0, 0, pos.x);
        t.put(1, 0, pos.y);
        t.put(2, 0, pos.z);
        return t;
    }
}
