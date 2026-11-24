using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MoveTidy.Core;

/// <summary>
/// 認出「是不是原來那個項目」的特徵。搬完時記下，撤回前比對，對不上就不動它。
/// 檔案看大小和修改時間；資料夾看系統給的編號（同名的新資料夾編號不同），沒有編號的磁碟看建立時間。
/// 在同一個磁碟裡改名或搬動都不會變。
/// </summary>
public static class FileStamp
{
    /// <summary>讀不到時回傳 null。</summary>
    public static string? Of(string path, bool isDirectory)
    {
        try
        {
            if (!isDirectory)
            {
                var f = new FileInfo(path);
                return f.Exists ? $"f:{f.Length}:{f.LastWriteTimeUtc.Ticks}" : null;
            }
            var d = new DirectoryInfo(path);
            if (!d.Exists) return null;
            return DirectoryId(path) is { } id ? "d:" + id : $"c:{d.CreationTimeUtc.Ticks}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    static string? DirectoryId(string path)
    {
        // FAT、網路磁碟的編號不固定，不用
        var root = Path.GetPathRoot(path) ?? "";
        if (root.StartsWith(@"\\?\", StringComparison.Ordinal)) root = root[4..];
        try { if (new DriveInfo(root).DriveFormat is not ("NTFS" or "ReFS")) return null; }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return null; }

        // 0x02000000 = 可以打開資料夾；0x00200000 = 連結就看連結本身
        using var h = CreateFileW(path, 0, FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open, 0x02200000, 0);
        return !h.IsInvalid && GetFileInformationByHandle(h, out var i)
            ? $"{i.Volume:x}-{i.IndexHigh:x8}{i.IndexLow:x8}"
            : null;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]   // 時間欄位是兩個 32 位元，不補空隙
    struct Info
    {
        public uint Attributes;
        public long Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share, nint security, FileMode mode, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Info info);
}
