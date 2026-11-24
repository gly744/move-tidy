namespace MoveTidy.Core;

/// <summary>Code 是 Windows 錯誤碼（例如 32 = 檔案被其他程式使用中），介面用它顯示中文說明。</summary>
public sealed record MoveFailure(string Source, string Message, int Code = 0)
{
    /// <summary>來源在搬的途中有變動，整個沒搬。</summary>
    public const int SourceChanged = -1;
    /// <summary>沒搬成，而且來源沒能換回原名；Message 是它現在的位置。</summary>
    public const int SourceRelocated = -2;

    public static MoveFailure From(string source, Exception ex) => ex switch
    {
        SourceChangedException => new(source, ex.Message, SourceChanged),
        SourceRelocatedException r => new(source, r.At, SourceRelocated),
        UnauthorizedAccessException => new(source, ex.Message, 5),
        _ => new(source, ex.Message, ex.HResult & 0xFFFF),
    };
}

sealed class SourceChangedException() : IOException("來源在搬的途中有變動");

sealed class SourceRelocatedException(string at, Exception inner) : IOException("來源沒能換回原名：" + at, inner)
{
    public string At { get; } = at;
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
    /// <param name="journal">邊做邊記，意外中斷時才收拾得回來。</param>
    public static MoveResult Execute(IReadOnlyList<PlannedItem> plan, string destDir, IProgress<int>? progress = null, CancellationToken ct = default, bool copy = false, MoveJournal? journal = null)
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
                if (!copy) left = MoveOne(item.Source, target, item.IsDirectory, () =>
                {
                    var rec = MoveRecord.Of(item.Source, target, item.IsDirectory);
                    result.Records.Add(rec);
                    journal?.Moved(rec);
                }, note => journal?.Parking(note));
                else if (item.IsDirectory) CopyDirectory(item.Source, target, note => journal?.Parking(note));
                else CopyFile(item.Source, target, note => journal?.Parking(note));
                result.Moved++;
                if (left is not null) result.Leftovers.Add((item.Source, left));
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
    /// <param name="committed">東西已經在目的地了（可能還在刪來源）：這時就該留撤回紀錄。</param>
    /// <param name="parking">正要把來源換成暫時的名字。</param>
    public static string? MoveOne(string source, string target, bool isDirectory, Action? committed = null, Action<ParkNote>? parking = null)
    {
        if (!isDirectory)
        {
            MoveFile(source, target, parking);
            committed?.Invoke();
            return null;
        }
        if (SameRoot(source, target))
        {
            try
            {
                Directory.Move(source, target);
                committed?.Invoke();
                return null;
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) == 17) { } // 同一個磁碟代號，其實掛著另一個磁碟
        }
        return MoveDirectoryByCopy(source, target, committed, parking);
    }

    static bool SameRoot(string a, string b) =>
        string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>搬一個檔案。跨磁碟時全程擋住別人寫入來源，複製完整了才刪來源。</summary>
    static void MoveFile(string source, string target, Action<ParkNote>? parking)
    {
        if (SameRoot(source, target) || IsLink(new FileInfo(source)))
        {
            File.Move(source, target, overwrite: false);
            return;
        }
        // 有人正在寫這個檔案時，這裡就會失敗，什麼都不動
        using var hold = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        CopyFile(source, target, parking);
        KeepCreationTime(source, target);   // 要在換成正式名字之後：Windows 會把剛刪掉的同名檔的建立時間套上來
        try { DeleteFile(source); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!File.Exists(source)) return;   // 來源剛好被別人移走或刪掉：複製的這份要留著
            DeleteFile(target);
            throw;
        }
    }

    /// <summary>複製一個檔案：先用暫時名字，完整了才換成正式名字。</summary>
    static void CopyFile(string source, string target, Action<ParkNote>? parking)
    {
        var temp = TempName(target);
        parking?.Invoke(new(source, "", false, temp));
        try
        {
            File.Copy(source, temp, overwrite: false);
            File.Move(temp, target, overwrite: false);
        }
        catch
        {
            if (File.Exists(temp)) try { DeleteFile(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>複製不會保留建立時間，補上（做不到也不影響搬移）。</summary>
    static void KeepCreationTime(string source, string copy)
    {
        try
        {
            var attr = File.GetAttributes(copy);
            bool readOnly = attr.HasFlag(FileAttributes.ReadOnly);
            if (readOnly) File.SetAttributes(copy, attr & ~FileAttributes.ReadOnly);
            File.SetCreationTimeUtc(copy, File.GetCreationTimeUtc(source));
            if (readOnly) File.SetAttributes(copy, attr);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>測試用：在跨磁碟搬資料夾的各個步驟之間插入動作。</summary>
    [ThreadStatic] internal static Action<string>? TestStep;

    /// <summary>測試用：模擬程式突然消失，失敗時不收拾。</summary>
    [ThreadStatic] internal static bool TestSkipCleanup;

    /// <summary>
    /// 跨磁碟搬資料夾：
    /// 1. 複製成目的地裡的暫時名字，同時記下每個檔案複製前的大小和修改時間
    /// 2. 把來源改成暫時名字：改不了代表裡面有檔案在用；改好了就沒人能再從原路徑寫進去
    /// 3. 核對來源和當初記的一樣，有新增、修改、刪除就不搬
    /// 4. 複製的那份換成正式名字，再刪除來源（包括唯讀檔）
    /// 4 以前任何一步失敗：來源換回原名，清掉複製的那份。
    /// 來源真的刪不掉的留在暫時名字裡，原本的名字空出來，撤回才放得回去。
    /// </summary>
    static string? MoveDirectoryByCopy(string source, string target, Action? committed, Action<ParkNote>? parking)
    {
        var temp = TempName(target);
        var parked = TempName(source) + "-" + Path.GetFileName(source);
        bool isParked = false;
        parking?.Invoke(new(source, parked, true, temp));
        try
        {
            var snap = new Dictionary<string, Entry>(StringComparer.Ordinal);
            CopyInto(source, temp, snap, "");
            TestStep?.Invoke("copied");
            RenameDirectory(source, parked, tries: 5);
            isParked = true;
            TestStep?.Invoke("parked");
            int seen = 0;
            if (!Matches(parked, "", snap, ref seen) || seen != snap.Count) throw new SourceChangedException();
            RenameDirectory(temp, target, tries: 20);
        }
        catch (Exception ex) when (!TestSkipCleanup)
        {
            var at = isParked ? Unpark(parked, source, true) : source;
            // 萬一暫時換名的來源不見了，複製的那份就是唯一一份，不能刪
            if (Directory.Exists(temp) && (!isParked || Directory.Exists(at))) DeleteTree(temp);
            if (at != source) throw new SourceRelocatedException(at, ex);
            throw;
        }
        TestStep?.Invoke("committed");
        committed?.Invoke();
        return DeleteTree(parked) ? null : parked;
    }

    /// <summary>把來源換回原名；原名剛好又被別的程式建了一個，就加號碼。回傳最後的位置。</summary>
    internal static string Unpark(string parked, string source, bool isDirectory)
    {
        try
        {
            var folder = Path.GetDirectoryName(source)!;
            var back = Path.Combine(folder, NamePlanner.UniqueKeepName(Path.GetFileName(source), isDirectory, NamePlanner.NamesIn(folder)));
            if (isDirectory) RenameDirectory(parked, back, tries: 20);
            else File.Move(parked, back, overwrite: false);
            return back;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return parked; }
    }

    /// <summary>複製整個資料夾；中途失敗會清掉已複製的部分。</summary>
    static void CopyDirectory(string source, string target, Action<ParkNote>? parking)
    {
        var temp = TempName(target);
        parking?.Invoke(new(source, "", true, temp));
        try
        {
            CopyInto(source, temp, null, "");
            RenameDirectory(temp, target, tries: 20);
        }
        catch
        {
            if (Directory.Exists(temp)) DeleteTree(temp);
            throw;
        }
    }

    enum EntryKind { File, Directory, Link }

    /// <summary>複製前的樣子。連結只記是連結（刪來源時也只刪連結本身）。</summary>
    readonly record struct Entry(EntryKind Kind, long Size = 0, DateTime Time = default);

    static bool IsLink(FileSystemInfo e) => e.Exists && e.Attributes.HasFlag(FileAttributes.ReparsePoint) && e.LinkTarget is not null;

    /// <param name="snap">不是 null 就記下來源每一項的樣子（用相對路徑）。</param>
    static void CopyInto(string source, string target, Dictionary<string, Entry>? snap, string rel)
    {
        Directory.CreateDirectory(target);
        foreach (var e in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            var (to, r) = (Path.Combine(target, e.Name), Path.Combine(rel, e.Name));
            bool link = IsLink(e);
            if (e is FileInfo f)
            {
                f.Refresh();
                snap?.Add(r, link ? new(EntryKind.Link) : new(EntryKind.File, f.Length, f.LastWriteTimeUtc));
                File.Copy(f.FullName, to, overwrite: false);
            }
            else
            {
                snap?.Add(r, new(link ? EntryKind.Link : EntryKind.Directory));
                CopyInto(e.FullName, to, link ? null : snap, r);
            }
        }
    }

    /// <summary>資料夾現在的樣子和當初記的完全一樣，而且沒有檔案正在被寫入。</summary>
    static bool Matches(string dir, string rel, Dictionary<string, Entry> snap, ref int seen)
    {
        foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos())
        {
            var r = Path.Combine(rel, e.Name);
            if (!snap.TryGetValue(r, out var was)) return false;
            seen++;
            if (IsLink(e)) { if (was.Kind != EntryKind.Link) return false; }
            else if (e is FileInfo f)
            {
                // 先擋住寫入再看大小和時間，才不會看完又被改
                try
                {
                    using var hold = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    f.Refresh();
                    if (was != new Entry(EntryKind.File, hold.Length, f.LastWriteTimeUtc)) return false;
                }
                catch (IOException) { return false; }
            }
            else if (was.Kind != EntryKind.Directory || !Matches(e.FullName, r, snap, ref seen)) return false;
        }
        return true;
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
    internal static bool DeleteTree(string dir)
    {
        bool ok = true;
        try
        {
            foreach (var e in new DirectoryInfo(dir).GetFileSystemInfos())
            {
                try
                {
                    if (e is DirectoryInfo && !IsLink(e)) ok &= DeleteTree(e.FullName);
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

    internal static void DeleteFile(string path)
    {
        var attr = File.GetAttributes(path);
        if (attr.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, attr & ~FileAttributes.ReadOnly);
        try { File.Delete(path); }
        catch when (attr.HasFlag(FileAttributes.ReadOnly) && File.Exists(path)) { File.SetAttributes(path, attr); throw; }
    }
}
