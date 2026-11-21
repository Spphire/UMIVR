using System.IO;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
using UnityEngine;

public static class TarGzPacker
{
    /// <summary>
    /// 将目录打包成 tar.gz
    /// ⚠️ 不额外包一层目录
    /// </summary>
    public static void PackDirectory(string sourceDir, string outputTarGz)
    {
        if (!Directory.Exists(sourceDir))
        {
            Debug.LogError("Source dir not found: " + sourceDir);
            return;
        }

        using (FileStream fs = File.Create(outputTarGz))
        using (GZipOutputStream gzipStream = new GZipOutputStream(fs))
        using (TarOutputStream tarStream = new TarOutputStream(gzipStream))
        {
            tarStream.IsStreamOwner = false;

            foreach (string filePath in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                AddFileToTar(tarStream, sourceDir, filePath);
            }
        }
    }

    private static void AddFileToTar(TarOutputStream tarStream, string rootDir, string filePath)
    {
        string entryName = filePath.Substring(rootDir.Length).TrimStart(Path.DirectorySeparatorChar);
        entryName = entryName.Replace("\\", "/"); // ⚠️ tar 标准用 /

        TarEntry entry = TarEntry.CreateEntryFromFile(filePath);
        entry.Name = entryName;

        tarStream.PutNextEntry(entry);

        using (FileStream fs = File.OpenRead(filePath))
        {
            fs.CopyTo(tarStream);
        }

        tarStream.CloseEntry();
    }
}