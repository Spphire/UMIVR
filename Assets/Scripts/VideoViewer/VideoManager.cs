using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.Video;

public class VideoManager : MonoBehaviour
{
    public VideoPlayer vp;

    public TextMeshProUGUI recordName;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        
    }

    public void SetVideo(string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            Debug.LogError("Video url is null or empty");
            return;
        }

        if (!File.Exists(url))
        {
            Debug.LogError("Video file not found: " + url);
            return;
        }

        recordName.text = Path.GetFileName(Path.GetDirectoryName(url));

        // 停止当前播放
        if (vp.isPlaying)
            vp.Stop();

        // 基本配置（确保最简单可播）
        vp.source = VideoSource.Url;
        vp.url = url;
        vp.playOnAwake = false;
        vp.playbackSpeed = 1f;

        // 先准备，再播放（避免黑屏）
        vp.Prepare();
        vp.prepareCompleted += OnPrepared;
    }

    private void OnPrepared(VideoPlayer player)
    {
        player.prepareCompleted -= OnPrepared;
        player.Play();
    }
}
