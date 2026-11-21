using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Recorder;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using UnityEngine.Video;

[ExecuteAlways]
public class FileView : MonoBehaviour
{
    [Header("References")]
    public TextMeshProUGUI nameDisplay;
    public Toggle toggle;
    private FileManager manager;
    public TextMeshProUGUI videoSizeDisplay;
    public RawImage videoImage;

    [Header("Upload Settings")]
    public bool uploaded = false;
    public TextMeshProUGUI uploadedText;
    private bool isUploading = false;
    private Action<float> progressCallback;
    public Image progressBackground;
    private Material progressMaterial;
    private MaterialPropertyBlock mpb;

    public bool manualTrigger = false;

    private float normal = 0.01f;
    private float pressed = 0.05f;
    private float toggled = 0.1f;
    
    [Header("Upload Limit")]
    private static readonly SemaphoreSlim PackSemaphore =
        new SemaphoreSlim(2, 2);
    
    [Header("Debug")] 
    [Range(0,1)]
    public float debug_p;
    public void OnToggleClick(bool isOn)
    {
        ColorBlock cb = toggle.colors;
        cb.highlightedColor = new Color(1, 1, 1, pressed);
        if (!isOn)
        {
            cb.normalColor = new Color(1, 1, 1, normal);
            cb.selectedColor = new Color(1, 1, 1, normal);
            
            cb.pressedColor = new Color(1, 1, 1, toggled);
        }
        else
        {
            cb.normalColor = new Color(1, 1, 1, toggled);
            cb.selectedColor = new Color(1, 1, 1, toggled);
            
            cb.pressedColor = new Color(1, 1, 1, normal);
        }
        toggle.colors = cb;
        
        manager.OnChildToggleClick(this, isOn);
    }
    
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        if (nameDisplay == null) ULog.LogError($"param: 'name' is null, need {typeof(TextMeshProUGUI)}");
        if (toggle == null) ULog.LogError($"param: 'toggle' is null, need {typeof(Toggle)}");
        manager = FindAnyObjectByType<FileManager>();
        if (manager == null) ULog.LogError($"manager is null, need {typeof(FileManager)}");
        if (progressBackground == null) ULog.LogError($"progressBackground is null, need {typeof(Image)}");
        progressMaterial = new(progressBackground.material);
        progressBackground.material = progressMaterial;
        if (progressMaterial == null) ULog.LogError($"progressMaterial is null, need {typeof(Material)}");
        
        if (uploadedText == null) ULog.LogError($"uploadedText is null, need {typeof(TextMeshProUGUI)}");

        toggle.isOn = false;

        progressCallback = null;
        mpb = new MaterialPropertyBlock();
        progressCallback += p =>
        {
            progressMaterial = new(progressBackground.material);
            progressMaterial.SetFloat("_Progress", p);
            progressBackground.material =  progressMaterial;
        };
        progressCallback += p => ULog.Log($"{nameDisplay.text} uploading: Progress {p*100:F1}%");
    }

    // Update is called once per frame
    void Update()
    {
        progressBackground.material.SetFloat("_Progress", debug_p);
        progressBackground.SetMaterialDirty();
    }

    public string GetVideoPath()
    {
        string mp4Path = Application.persistentDataPath +
                         $"/record/{gameObject.name}/{QuestMp4Recorder.videoFileName}";
        if (!File.Exists(mp4Path))
        {
            mp4Path = Application.persistentDataPath +
                      $"/record/{gameObject.name}/{FramePusher.leftVideoFileName}";
        }
        
        return mp4Path;
    }

    public void PlayVideo()
    {
        var vp = FindAnyObjectByType<VideoManager>();
        if (vp != null)
        {
            string mp4Path = GetVideoPath();
            vp.SetVideo(mp4Path);
        }
    }

    public void OnDelete()
    {
        string folderPath = Path.Combine(
            Application.persistentDataPath,
            "record",
            gameObject.name
        );

        if (!Directory.Exists(folderPath))
        {
            ULog.LogWarning("Delete failed, folder not found: " + folderPath);
            return;
        }

        try
        {
            // true = 递归删除（包含子文件）
            Directory.Delete(folderPath, true);
            ULog.Log("Deleted folder: " + folderPath);
        }
        catch (System.Exception e)
        {
            ULog.LogError("Failed to delete folder: " + e);
            return;
        }

    }

    public void Upload()
    {
        if (!isUploading && progressBackground.material.GetFloat("_Progress") != 1.0f)
        {
            progressMaterial = new(progressBackground.material);
            progressMaterial.SetFloat("_Progress", 1.0f);
            progressBackground.material =  progressMaterial;
        }
        
        // 调用异步上传方法，不阻塞主线程
        if (isUploading || uploaded)return;
        isUploading = true;
        # if UNITY_EDITOR
            StartCoroutine(FakeUploadCoroutine());
            
        # else
            progressCallback?.Invoke(0f);
            _ = UploadAsync();
        #endif
        
    }

    public void SetUploaded(bool value)
    {
        uploaded = value;
        uploadedText.text = uploaded ? "uploaded":"";

        if (uploaded)
        {
            if (!manager.uploadedPersistence.recorded.Contains(name))
            {
                manager.uploadedPersistence.recorded.Add(name);
            }
        }
        else
        {
            if (manager.uploadedPersistence.recorded.Contains(name))
            {
                manager.uploadedPersistence.recorded.Remove(name);
            }
        }

        manager.SaveUploadedPersistence();
    }

    // 异步上传方法
    private async Task UploadAsync()
    {
        string folderPath = Path.Combine(Application.persistentDataPath, "record", name);
        //check sha256
        MetaDatav0_9 metaData = JsonUtility.FromJson<MetaDatav0_9>(
            await File.ReadAllTextAsync(Path.Join(folderPath, FramePusher.metaFileName))
            );
        string leftVideoSha256 = await Sha256Util.ComputeFileSha256AsyncLimited(Path.Join(folderPath, FramePusher.leftVideoFileName));
        progressCallback?.Invoke(0.1f);
        string rightVideoSha256 = await Sha256Util.ComputeFileSha256AsyncLimited(Path.Join(folderPath, FramePusher.rightVideoFileName));
        progressCallback?.Invoke(0.2f);
        string bsonSha256 = await Sha256Util.ComputeFileSha256AsyncLimited(Path.Join(folderPath, FramePusher.dataFileName));

        // foreach (FileInfo fi in metaData.files)
        // {
        //     if (fi.filename == FramePusher.leftVideoFileName && fi.sha256 != leftVideoSha256)
        //     {
        //         ULog.LogWarning($"sha256 {leftVideoSha256} does not match recorded sha256 {fi.sha256}");
        //         fi.sha256 = leftVideoSha256;
        //     }
        //
        //     if (fi.filename == FramePusher.rightVideoFileName && fi.sha256 != rightVideoSha256)
        //     {
        //         ULog.LogWarning($"sha256 {rightVideoSha256} does not match recorded sha256 {fi.sha256}");
        //         fi.sha256 = rightVideoSha256;
        //     }
        // }
        
        // temp
        metaData.files = new List<FileInfo>()
        {
            new FileInfo(FramePusher.leftVideoFileName, leftVideoSha256),
            new FileInfo(FramePusher.rightVideoFileName, rightVideoSha256),
            new FileInfo(FramePusher.dataFileName, bsonSha256)
        };
        
        string metaPath = Path.Combine(folderPath, FramePusher.metaFileName);
        // using (var fs = File.Create(metaPath))
        // using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
        // using (var writer = new JsonTextWriter(sw))
        // {
        //     writer.Formatting = Formatting.Indented; // 美化，可选
        //     var serializer = new JsonSerializer();
        //     serializer.Serialize(writer, metaData);
        // }

        await Task.Run(() =>
        {
            using (var fs = new FileStream(
                       metaPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None))
            using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
            using (var writer = new JsonTextWriter(sw))
            {
                writer.Formatting = Formatting.Indented;
                var serializer = new JsonSerializer();
                serializer.Serialize(writer, metaData);
            }
        });
        
        //pack targz
        if (!Directory.Exists(folderPath))
        {
            ULog.LogError("Record folder not found: " + folderPath);
            return;
        }

        string tarTempPath = Path.Combine(Application.persistentDataPath, "temp");
        if (!Directory.Exists(tarTempPath))
        {
            Directory.CreateDirectory(tarTempPath);
        }

        string tarGzPath = Path.Combine(tarTempPath, $"{name}.tar.gz");

        // ⚡ 在后台线程打包 tar.gz
        bool packSuccess = false;

        await PackSemaphore.WaitAsync();
        try
        {
            packSuccess = await Task.Run(() =>
            {
                TarGzPacker.PackDirectory(folderPath, tarGzPath);
                return File.Exists(tarGzPath);
            });
        }
        finally
        {
            PackSemaphore.Release();
        }

        if (!packSuccess)
        {
            ULog.LogError("Failed to create tar.gz: " + tarGzPath);
            return;
        }

        ULog.Log("Tar.gz created: " + tarGzPath);
        // 上传必须回到 Unity 主线程，使用 Coroutine 包装
        StartCoroutine(UploadCoroutine(tarGzPath));
    }

    private IEnumerator FakeUploadCoroutine()
    {
        float progress = 0f;
        while (progress < 1f)
        {
            progressCallback.Invoke(progress);
            progress += 0.05f;
            yield return new WaitForSecondsRealtime(0.1f);
        }
        isUploading = false;
    }

    private IEnumerator UploadCoroutine(string tarGzPath)
    {
        progressCallback?.Invoke(0.3f);
        byte[] fileData = File.ReadAllBytes(tarGzPath);

        WWWForm form = new WWWForm();
        form.AddBinaryData(
            "file",                // ⚠️ 后端要求字段名 archive
            fileData,
            Path.GetFileName(tarGzPath),
            "application/x-tar"
        );

        string ip_port = manager.GetComponent<IPListManager>().GetSelectedIP();
        if (!string.IsNullOrEmpty(ip_port))
        {
            string uploadUrl = $"http://{ip_port}/v1/upload_record";

            using (UnityWebRequest request = UnityWebRequest.Post(uploadUrl, form))
            {
                request.timeout = 60;

                ULog.Log("Uploading: " + tarGzPath);

                request.SendWebRequest();

                while (!request.isDone)
                {
                    progressCallback?.Invoke(request.uploadProgress*0.7f+0.3f);
                    yield return null;
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    ULog.LogError("Upload failed: " + request.error);
                    ULog.LogError("Server response: " + request.downloadHandler.text);
                }
                else
                {
                    ULog.Log("Upload success!");
                    ULog.Log("Server response: " + request.downloadHandler.text);
                    SetUploaded(true);
                }
            }
        }

        isUploading = false;
    }
    
}
