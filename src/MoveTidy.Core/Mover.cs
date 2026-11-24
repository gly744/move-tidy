namespace MoveTidy.Core;

/// <summary>Code 是 Windows 錯誤碼（例如 32 = 檔案被其他程式使用中），介面用它顯示中文說明。</summary>
public sealed record MoveFailure(string Source, string Message, int Code = 0);

public sealed class MoveResult
{
    public int Moved { get; set; }
    public int Renamed { get; set; }
    public int Skipped { get; set; }
    public List<MoveFailure> Failures { get; } = [];
    /// <summary>實際搬成功的每一項（給撤回用）。</summary>
    public List<MoveRecord> Records { get; } = [];
    /// <summary>目的地資料夾是這次才建立的。</summary>
    public bool CreatedDest { get; set; }
}

/// <summary>依照 <see cref="NamePlanner"/> 的計畫實際搬移（或複製）。</summary>
public static class Mover
{
    /// <param name="copy">只複製，來源留著；不留撤回紀錄。</param>
    public static MoveResult Execute(IReadOnlyList<PlannedItem> plan, string destDir, IProgress<int>? progress = null, CancellationToken ct = default, bool copy = false)
    {
        var result = new MoveResult();
        destDir = NamePlanner.Normalize(destDir);
        result.CreatedDest = !Directory.Exists(destDir);
        Directory.CreateDirectory(destDir);
        int done = 0;
        foreach (var item in plan)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Skip != SkipReason.None)
            {
                result.Skipped++;
                progress?.Report(++done);
                continue;
            }
            try
            {
                var targetName = item.TargetName;
                var target = Path.Combine(destDir, targetName);
                if (Path.Exists(target))
                {
                    // 計畫之後目的地又多了同名項目：退回「名字 (n)」
                    targetName = NamePlanner.UniqueKeepName(targetName, item.IsDirectory, NamePlanner.NamesIn(destDir));
                    target = Path.Combine(destDir, targetName);
                }
                if (!copy) MoveOne(item.Source, target, item.IsDirectory);
                else if (item.IsDirectory) CopyDirectory(item.Source, target);
                else File.Copy(item.Source, target, overwrite: false);
                result.Moved++;
                if (!copy) result.Records.Add(new(item.Source, target, item.IsDirectory));
                if (!string.Equals(targetName, item.OriginalName, StringComparison.OrdinalIgnoreCase)) result.Renamed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Failures.Add(new(item.Source, ex.Message, ex is UnauthorizedAccessException ? 5 : ex.HResult & 0xFFFF));
            }
            progress?.Report(++done);
        }
        return result;
    }

    /// <summary>搬一個項目（資料夾跨磁碟時先複製再刪除）。</summary>
    public static void MoveOne(string source, string target, bool isDirectory)
    {
        if (isDirectory) MoveDirectory(source, target);
        else File.Move(source, target, overwrite: false);
    }

    static void MoveDirectory(string source, string target)
    {
        if (string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(source, target);
            return;
        }
        // 跨磁碟：先完整複製，成功後才刪除來源
        CopyDirectory(source, target);
        Directory.Delete(source, recursive: true);
    }

    static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }
}
