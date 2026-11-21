using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public static class Sha256Util
{
    private static readonly SemaphoreSlim ShaSemaphore =
        new SemaphoreSlim(2, 2);

    public static async Task<string> ComputeFileSha256AsyncLimited(string filePath)
    {
        await ShaSemaphore.WaitAsync();
        try
        {
            return await ComputeFileSha256Async(filePath);
        }
        finally
        {
            ShaSemaphore.Release();
        }
    }
    public static async Task<string> ComputeFileSha256Async(string filePath)
    {
        using (var sha256 = SHA256.Create())
        using (var stream = new FileStream(
                   filePath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   bufferSize: 1024 * 1024, // 1MB
                   useAsync: true))
        {
            byte[] buffer = new byte[1024 * 1024];
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)
                       .ConfigureAwait(false)) > 0)
            {
                sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
            }

            sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

            return BytesToHex(sha256.Hash);
        }
    }
    public static string ComputeFileSha256(string filePath)
    {
        using (var sha256 = SHA256.Create())
        using (var stream = File.OpenRead(filePath))
        {
            byte[] hash = sha256.ComputeHash(stream);
            return BytesToHex(hash);
        }
    }
    
    public static string ComputeSha256(byte[] data)
    {
        using (var sha256 = SHA256.Create())
        {
            byte[] hash = sha256.ComputeHash(data);
            return BytesToHex(hash);
        }
    }

    private static string BytesToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            sb.Append(b.ToString("x2")); // 小写 hex，和 Python 一致
        }
        return sb.ToString();
    }
}