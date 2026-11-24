namespace MoveTidy.Core;

/// <summary>Code 是 Windows 錯誤碼（例如 32 = 檔案被其他程式使用中），介面用它顯示中文說明。</summary>
public sealed record MoveFailure(string Source, string Message, int Code = 0)
{
    public static MoveFailure From(string source, Exception ex) =>
        new(source, ex.Message, ex is UnauthorizedAccessException ? 5 : ex.HResult & 0xFFFF);
}

public sealed class MoveResult
{
    public int Moved { get; set; }
    public int Renamed { get; set; }
    public int Skipped { get; set; }
    public List<MoveFailure> Failures { get; } = [];
    /// <summary>已經搬好，但來源有東西刪不掉：原本的路徑 → 殘留所在的資料夾。</summary>
    public List<(string Source, string Left)> Leftovers { get; } = [];
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
                string? left = null;
                if (!copy) left = MoveOne(item.Source, target, item.IsDirectory);
                else if (item.IsDirectory) CopyDirectory(item.Source, target);
                else File.Copy(item.Source, target, overwrite: false);
                result.Moved++;
                if (left is not null) result.Leftovers.Add((item.Source, left));
                if (!copy) result.Records.Add(new(item.Source, target, item.IsDirectory));
                if (!string.Equals(targetName, item.OriginalName, StringComparison.OrdinalIgnoreCase)) result.Renamed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Failures.Add(MoveFailure.From(item.Source, ex));
            }
            progress?.Report(++done);
        }
        return result;
    }

    /// <summary>
    /// 搬一個項目，要嘛整個搬成功，要嘛來源原封不動（失敗會丟例外）。
    /// 通常回傳 null；已經搬好、但來源有東西刪不掉時，回傳殘留所在的資料夾。
    /// </summary>
    public static string? MoveOne(string source, string target, bool isDirectory)
    {
        if (!isDirectory) { MoveFile(source, target); return null; }
        if (SameRoot(source, target))
        {
            try { Directory.Move(source, target); return null; }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) == 17) { } // 同一個磁碟代號，其實掛著另一個磁碟
        }
        return MoveDirectoryByCopy(source, target);
    }

    static bool SameRoot(string a, string b) =>
        string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);

    static void MoveFile(string source, string target)
    {
        var before = new FileInfo(source);
        var (size, time) = (before.Exists ? before.Length : -1, before.LastWriteTimeUtc);
        File.Move(source, target, overwrite: false);
        if (SameRoot(source, target)) return;

        // 跨磁碟時，Windows 複製成功但來源刪不掉也算成功：自己再刪一次，還是不行就收回複製的那份，當作沒搬
        var after = new FileInfo(source);
        if (!after.Exists || after.Length != size || after.LastWriteTimeUtc != time) return;
        try { DeleteFile(source); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (File.Exists(source)) DeleteFile(target);
            throw;
        }
    }

    /// <summary>
    /// 跨磁碟搬資料夾：
    /// 1. 複製成目的地裡的暫時名字，全部成功才換成正式名字（失敗就清掉，不留半份）
    /// 2. 把來源改成暫時名字；改不了代表裡面有檔案在用，收回複製的那份，來源原封不動
    /// 3. 刪除來源（包括唯讀檔）；真的刪不掉的留在暫時名字裡，原本的名字空出來，撤回才放得回去
    /// </summary>
    static string? MoveDirectoryByCopy(string source, string target)
    {
        CopyDirectory(source, target);
        var parked = TempName(source) + "-" + Path.GetFileName(source);
        try
        {
            RenameDirectory(source, parked, tries: 5);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!Directory.Exists(source)) return null; // 來源剛好被別人移走：複製的這份要留著
            DeleteTree(target);
            throw;
        }
        return DeleteTree(parked) ? null : parked;
    }

    /// <summary>複製整個資料夾；中途失敗會清掉已複製的部分。</summary>
    static void CopyDirectory(string source, string target)
    {
        var temp = TempName(target);
        try
        {
            CopyInto(source, temp);
            RenameDirectory(temp, target, tries: 20);
        }
        catch
        {
            if (Directory.Exists(temp)) DeleteTree(temp);
            throw;
        }
    }

    static void CopyInto(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyInto(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    /// <summary>同一個資料夾裡的暫時名字。</summary>
    internal static string TempName(string path) =>
        Path.Combine(Path.GetDirectoryName(path)!, $"~mt-{Guid.NewGuid():N}"[..12]);

    /// <summary>改資料夾名字。防毒或索引可能剛好在讀，多試幾次。</summary>
    static void RenameDirectory(string from, string to, int tries)
    {
        for (int i = 1; ; i++)
        {
            try { Directory.Move(from, to); return; }
            catch (Exception ex) when (i < tries && ex is IOException or UnauthorizedAccessException && !Path.Exists(to)) { Thread.Sleep(200); }
        }
    }

    /// <summary>刪除整個資料夾（包括唯讀檔），刪不掉的略過。裡面的捷徑連結只刪連結，不碰它指到的地方。</summary>
    static bool DeleteTree(string dir)
    {
        bool ok = true;
        try
        {
            foreach (var e in new DirectoryInfo(dir).GetFileSystemInfos())
            {
                try
                {
                    bool link = e.Attributes.HasFlag(FileAttributes.ReparsePoint) && e.LinkTarget is not null;
                    if (e is DirectoryInfo && !link) ok &= DeleteTree(e.FullName);
                    else if (e is DirectoryInfo) e.Delete();
                    else DeleteFile(e.FullName);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ok = false; }
            }
            if (!ok) return false;
            var self = new DirectoryInfo(dir);
            if (self.Attributes.HasFlag(FileAttributes.ReadOnly)) self.Attributes &= ~FileAttributes.ReadOnly;
            self.Delete();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    static void DeleteFile(string path)
    {
        var attr = File.GetAttributes(path);
        if (attr.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, attr & ~FileAttributes.ReadOnly);
        try { File.Delete(path); }
        catch when (attr.HasFlag(FileAttributes.ReadOnly) && File.Exists(path)) { File.SetAttributes(path, attr); throw; }
    }
}
