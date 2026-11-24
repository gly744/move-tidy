using System.Security.Cryptography;
using System.Text;

namespace MoveTidy.Core;

/// <summary>好幾個程式可能同時改同一個檔案（右鍵連點、開了兩個視窗），用具名 Mutex 排隊。</summary>
static class FileLock
{
    public static void Run(string file, Action action)
    {
        using var mutex = new Mutex(false, @"Local\MoveTidy-file-" +
            Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(file).ToLowerInvariant())))[..16]);
        bool got;
        try { got = mutex.WaitOne(5000); } catch (AbandonedMutexException) { got = true; }
        if (!got) throw new IOException("檔案正在被另一個視窗使用：" + file);
        try { action(); }
        finally { mutex.ReleaseMutex(); }
    }
}
