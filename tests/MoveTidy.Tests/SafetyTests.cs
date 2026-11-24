using System.Diagnostics;
using MoveTidy.Core;

namespace MoveTidy.Tests;

/// <summary>跨磁碟搬移途中來源有變動：整個不搬，什麼都不能少。（\\?\ 的用法見 CrossVolumeTests）</summary>
public sealed class ChangedWhileMovingTests : IDisposable
{
    readonly TempDir _t = new();
    readonly string _src, _dest;

    public ChangedWhileMovingTests()
    {
        _src = _t.Dir("proj");
        _dest = @"\\?\" + _t.Dir("other");
        _t.File("proj/a.txt", "舊內容");
        _t.File("proj/sub/b.txt", "b");
    }

    public void Dispose()
    {
        Mover.TestStep = null;
        _t.Dispose();
    }

    MoveResult Go(Action? copied = null, Action? parked = null, bool copy = false)
    {
        Mover.TestStep = step => { if (step == "copied") copied?.Invoke(); else if (step == "parked") parked?.Invoke(); };
        return Mover.Execute(NamePlanner.Plan([_src], _dest, new(Copy: copy)), _dest, copy: copy);
    }

    string[] Files(string dir) => [.. Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(dir, p)).Order(StringComparer.Ordinal)];

    void AssertNotMoved(MoveResult r)
    {
        Assert.Equal(MoveFailure.SourceChanged, Assert.Single(r.Failures).Code);
        Assert.Empty(r.Records);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_dest));
        Assert.Equal(["other", "proj"], Directory.GetDirectories(_t.Root).Select(p => Path.GetFileName(p)!).Order());
    }

    [Fact]
    public void NewFile_NotLost()
    {
        AssertNotMoved(Go(() => File.WriteAllText(Path.Combine(_src, "sub", "new.txt"), "新檔")));
        Assert.Equal(["a.txt", @"sub\b.txt", @"sub\new.txt"], Files(_src));
        // 變動停了之後重試就成功，新檔也跟著過去
        Assert.Equal((1, 0), (Go() is var r ? r.Moved : 0, r.Renamed));
        Assert.Equal(["a.txt", @"sub\b.txt", @"sub\new.txt"], Files(Path.Combine(_dest, "proj")));
        Assert.False(Directory.Exists(_src));
    }

    [Fact]
    public void ModifiedFile_NotLost()
    {
        AssertNotMoved(Go(() => File.WriteAllText(Path.Combine(_src, "a.txt"), "改過的內容")));
        Assert.Equal("改過的內容", File.ReadAllText(Path.Combine(_src, "a.txt")));
    }

    [Fact]
    public void SameSizeModification_NotLost()
    {
        AssertNotMoved(Go(() => { Thread.Sleep(30); File.WriteAllText(Path.Combine(_src, "sub", "b.txt"), "c"); }));
        Assert.Equal("c", File.ReadAllText(Path.Combine(_src, "sub", "b.txt")));
    }

    [Fact]
    public void DeletedFile_StaysDeleted()
    {
        AssertNotMoved(Go(() => File.Delete(Path.Combine(_src, "sub", "b.txt"))));
        Assert.Equal(["a.txt"], Files(_src));
    }

    [Fact]
    public void NewEmptyFolder_NotLost()
    {
        AssertNotMoved(Go(() => Directory.CreateDirectory(Path.Combine(_src, "空的"))));
        Assert.True(Directory.Exists(Path.Combine(_src, "空的")));
    }

    [Theory]
    [InlineData(FileShare.ReadWrite | FileShare.Delete)]  // 瀏覽器、同步程式常這樣開檔
    [InlineData(FileShare.ReadWrite)]
    public void FileStillBeingWritten_NotMoved(FileShare share)
    {
        FileStream? w = null;
        try
        {
            var r = Go(() =>
            {
                w = new FileStream(Path.Combine(_src, "sub", "b.txt"), FileMode.Append, FileAccess.Write, share);
                w.WriteByte(65);
                w.Flush();
            });
            Assert.Single(r.Failures);
            Assert.Empty(r.Records);
            Assert.Empty(Directory.EnumerateFileSystemEntries(_dest));
            w!.WriteByte(66);
        }
        finally { w?.Dispose(); }
        Assert.Equal("bAB", File.ReadAllText(Path.Combine(_src, "sub", "b.txt")));
    }

    [Fact]
    public void SourceNameRecreated_OriginalKeptUnderNumber_AndReported()
    {
        var r = Go(
            () => File.WriteAllText(Path.Combine(_src, "new.txt"), "新檔"),
            () => _t.File("proj/別的程式建的.txt", "x"));
        var f = Assert.Single(r.Failures);
        Assert.Equal(MoveFailure.SourceRelocated, f.Code);
        Assert.Equal(Path.Combine(_t.Root, "proj (2)"), f.Message);
        Assert.Equal(["a.txt", "new.txt", @"sub\b.txt"], Files(f.Message));
        Assert.Equal(["別的程式建的.txt"], Files(_src));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_dest));
    }

    [Fact]
    public void TargetNameTakenMeanwhile_NothingOverwritten()
    {
        var r = Go(parked: () => _t.File("other/proj/別人的.txt", "x"));
        Assert.Single(r.Failures);
        Assert.Equal(["a.txt", @"sub\b.txt"], Files(_src));
        Assert.Equal(["別人的.txt"], Files(Path.Combine(_dest, "proj")));
        Assert.Single(Directory.EnumerateFileSystemEntries(_dest));
    }

    [Fact]
    public void JunctionPointingInsideItself_StillMoves()
    {
        // pnpm 的 node_modules 就是這樣：連結用絕對路徑指回資料夾裡面
        using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(_src, "link")}\" \"{Path.Combine(_src, "sub")}\"") { RedirectStandardOutput = true })!)
            p.WaitForExit();
        var r = Go();
        Assert.Empty(r.Failures);
        Assert.False(Directory.Exists(_src));
        Assert.Contains(@"sub\b.txt", Files(Path.Combine(_dest, "proj")));
    }

    [Fact]
    public void SingleFile_BeingWrittenOrHeld_NotMoved_ThenMovesWithAttributes()
    {
        var f = _t.File("one.txt", "1");
        MoveResult Run() => Mover.Execute(NamePlanner.Plan([f], _dest, new()), _dest);
        foreach (var (access, share) in new[] { (FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), (FileAccess.Read, FileShare.Read) })
        {
            using (new FileStream(f, FileMode.Open, access, share)) Assert.Single(Run().Failures);
            Assert.Equal("1", File.ReadAllText(f));
            Assert.Empty(Directory.EnumerateFileSystemEntries(_dest));
        }
        var (created, written) = (new DateTime(2020, 1, 2), new DateTime(2021, 3, 4));
        File.SetCreationTime(f, created); File.SetLastWriteTime(f, written);
        File.SetAttributes(f, FileAttributes.ReadOnly);
        Assert.Equal(1, Run().Moved);
        var to = new FileInfo(Path.Combine(_dest, "one.txt"));
        Assert.Equal((created, written, true), (to.CreationTime, to.LastWriteTime, to.IsReadOnly));
        Assert.False(File.Exists(f));
        Assert.Single(Directory.EnumerateFileSystemEntries(_dest));
        to.IsReadOnly = false;
    }
}

/// <summary>撤回前確認還是原來那個項目。</summary>
public class UndoIdentityTests
{
    static MoveBatch Move(TempDir t, string[] src, string dest) =>
        MoveSession.Run(NamePlanner.Plan(src, dest, new()), dest, MoveOp.Move, historyFile: Path.Combine(t.Root, "data", "history.json")).Batch;

    [Fact]
    public void ReplacedOrEditedFile_NotTouched_OthersRestored()
    {
        using var t = new TempDir();
        string[] src = [t.File("src/換掉.txt", "原檔"), t.File("src/編輯.txt", "原檔"), t.File("src/沒動.txt", "原檔")];
        var dest = t.Dir("dest");
        Move(t, src, dest);
        File.Delete(Path.Combine(dest, "換掉.txt")); t.File("dest/換掉.txt", "別的檔案");
        File.AppendAllText(Path.Combine(dest, "編輯.txt"), "+");

        // 從紀錄檔讀回來的也一樣認得
        var u = Undoer.Execute(MoveHistory.Last(Path.Combine(t.Root, "data", "history.json"))!);
        Assert.Equal((1, 2), (u.Restored, u.Changed.Count));
        Assert.False(File.Exists(src[0]));
        Assert.False(File.Exists(src[1]));
        Assert.Equal("原檔", File.ReadAllText(src[2]));
        Assert.Equal("別的檔案", File.ReadAllText(Path.Combine(dest, "換掉.txt")));
        Assert.Equal("原檔+", File.ReadAllText(Path.Combine(dest, "編輯.txt")));
    }

    [Fact]
    public void ReplacedFolder_NotTouched_FolderWithEditedContent_Restored()
    {
        using var t = new TempDir();
        t.File("src/換掉/1.txt"); t.File("src/加了檔/1.txt");
        string[] src = [Path.Combine(t.Root, "src", "換掉"), Path.Combine(t.Root, "src", "加了檔")];
        var dest = t.Dir("dest");
        var batch = Move(t, src, dest);
        Directory.Delete(Path.Combine(dest, "換掉"), true); t.File("dest/換掉/別的.txt");   // 馬上用同名重建
        t.File("dest/加了檔/2.txt");

        var u = Undoer.Execute(batch);
        Assert.Equal((1, 1), (u.Restored, u.Changed.Count));
        Assert.True(File.Exists(Path.Combine(dest, "換掉", "別的.txt")));
        Assert.True(File.Exists(Path.Combine(src[1], "2.txt")));
    }

    [Fact]
    public void RenameSwap_ChangedOne_IsNeverMovedAside()
    {
        using var t = new TempDir();
        string[] src = [t.File("001.jpg", "一", new(2026, 1, 2)), t.File("002.jpg", "二", new(2026, 1, 3)), t.File("003.jpg", "三", new(2026, 1, 1))];
        var (_, batch) = MoveSession.Run(Renamer.Plan(src, new(RenameStyle.PureNumber, NumberOrder.Modified)), t.Root, MoveOp.Rename, historyFile: Path.Combine(t.Root, "data", "h.json"));
        File.WriteAllText(src[1], "換成別的");   // 002 現在是原來的 001
        var u = Undoer.Execute(batch);
        Assert.Equal((2, 1), (u.Restored, u.Changed.Count));
        Assert.Equal("換成別的", File.ReadAllText(src[1]));
        Assert.Equal(["三", "二", "換成別的"], Directory.GetFiles(t.Root).Select(File.ReadAllText).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ReplacedWithSameSizeAndTime_NotTouched()
    {
        using var t = new TempDir();
        var src = t.File("src/x.txt", "AAAA");
        var dest = t.Dir("dest");
        var batch = Move(t, [src], dest);
        var to = Path.Combine(dest, "x.txt");
        var time = File.GetLastWriteTimeUtc(to);
        File.Delete(to); File.WriteAllText(to, "BBBB"); File.SetLastWriteTimeUtc(to, time);
        var u = Undoer.Execute(batch);
        Assert.Equal((0, 1), (u.Restored, u.Changed.Count));
        Assert.Equal("BBBB", File.ReadAllText(to));
    }

    [Fact]
    public void StampFromPreviousVersion_StillMatches()
    {
        using var t = new TempDir();
        var f = t.File("a.txt");
        var now = FileStamp.Of(f, false)!;
        Assert.Equal(4, now.Split(':').Length);
        var old = string.Join(':', now.Split(':').Take(3));
        Assert.True(FileStamp.Matches(old, f, false));
        Assert.True(FileStamp.Matches(now, f, false));
        File.AppendAllText(f, "+");
        Assert.False(FileStamp.Matches(old, f, false));
        // 改名、搬到同磁碟別的資料夾，還是同一個
        now = FileStamp.Of(f, false)!;
        var moved = Path.Combine(t.Dir("sub"), new string('長', 60) + ".txt");
        File.Move(f, moved);
        Assert.True(FileStamp.Matches(now, moved, false));
    }

    [Fact]
    public void FailedItems_StayInHistory_AndCanBeRetried()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Root, "data", "history.json");
        string[] src = [t.File("src/1.txt", "1"), t.File("src/2.txt", "2"), t.File("src/3.txt", "3")];
        var dest = Path.Combine(t.Root, "dest");
        var batch = Move(t, src, dest);
        using (File.Open(Path.Combine(dest, "2.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var u = MoveSession.Undo(batch, historyFile: file);
            Assert.Equal((2, 1), (u.Restored, u.Failures.Count));
        }
        var left = MoveHistory.Last(file)!;
        Assert.Equal((batch.Id, 1), (left.Id, left.Items.Count));
        Assert.Equal(1, MoveSession.Undo(left, historyFile: file).Restored);
        Assert.Equal(["1", "2", "3"], src.Select(File.ReadAllText));
        Assert.False(Directory.Exists(dest));          // 這次才建的資料夾，全部撤回後才刪
        Assert.Null(MoveHistory.Last(file));
    }

    [Fact]
    public void RenameSwap_FailedItem_CanBeRetriedWhereverItEndedUp()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Root, "data", "history.json");
        string[] src = [t.File("p/001.jpg", "一", new(2026, 1, 2)), t.File("p/002.jpg", "二", new(2026, 1, 3)), t.File("p/003.jpg", "三", new(2026, 1, 1))];
        var (_, batch) = MoveSession.Run(Renamer.Plan(src, new(RenameStyle.PureNumber, NumberOrder.Modified)), Path.Combine(t.Root, "p"), MoveOp.Rename, historyFile: file);
        using (File.Open(src[0], FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.NotEmpty(MoveSession.Undo(batch, historyFile: file).Failures);
        while (MoveHistory.Last(file) is { } b) Assert.NotEqual(0, MoveSession.Undo(b, historyFile: file).Restored);
        Assert.Equal(["一", "三", "二"], Directory.GetFiles(Path.Combine(t.Root, "p")).Select(File.ReadAllText).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(Directory.GetFiles(Path.Combine(t.Root, "p")), n => Path.GetFileName(n).StartsWith("~mt-"));
    }

    [Fact]
    public void OldRecordsWithoutStamp_StillUndo()
    {
        using var t = new TempDir();
        var to = t.File("dest/a.txt"); var from = Path.Combine(t.Root, "a.txt");
        Assert.Equal(1, Undoer.Execute(new MoveBatch { Dest = t.Root, Items = [new(from, to, false)] }).Restored);
        Assert.True(File.Exists(from));
    }

    [Fact]
    public void CrossVolume_MoveThenUndo_Works()
    {
        using var t = new TempDir();
        string[] src = [t.File("src/a.txt", "a"), Path.GetDirectoryName(t.File("src/d/b.txt", "b"))!];
        var dest = @"\\?\" + t.Dir("other");
        var u = Undoer.Execute(Move(t, src, dest));
        Assert.Equal((2, 0), (u.Restored, u.Changed.Count));
        Assert.Equal(("a", "b"), (File.ReadAllText(src[0]), File.ReadAllText(Path.Combine(src[1], "b.txt"))));
    }
}

/// <summary>邊搬邊記錄，程式意外結束也能撤回已經搬的。</summary>
public class JournalTests
{
    [Fact]
    public void Interrupted_IsRecovered_AndCanBeUndone()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Root, "data", "history.json");
        var dest = t.Dir("dest");
        string[] src = [t.File("a.txt", "a"), t.File("b.txt", "b")];
        var batch = new MoveBatch { Dest = dest };
        using (var j = MoveJournal.Start(batch, file))
        {
            j.Moved(Mover.Execute(NamePlanner.Plan([src[0]], dest, new()), dest).Records[0]);
            Assert.Null(MoveHistory.Last(file));      // 還在進行中的不算
        }                                             // 沒有 Complete 就結束＝程式被關掉
        var pending = Assert.Single(Directory.GetFiles(Path.Combine(t.Root, "data"), "pending-*"));
        File.AppendAllText(pending, "{\"Moved\":{\"From\":\"寫到一");   // 最後一行只寫到一半

        var last = MoveHistory.Last(file)!;
        Assert.Equal(batch.Id, last.Id);
        Assert.Equal(1, Undoer.Execute(last).Restored);
        Assert.Equal("a", File.ReadAllText(src[0]));
        Assert.Empty(Directory.GetFiles(Path.Combine(t.Root, "data"), "pending-*"));
        MoveHistory.Remove(last.Id, file);
        Assert.Null(MoveHistory.Last(file));
    }

    [Fact]
    public void Finished_LeavesNoPendingFile_AndOneHistoryEntry()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Root, "data", "history.json");
        var dest = Path.Combine(t.Root, "dest");
        var (r, batch) = MoveSession.Run(NamePlanner.Plan([t.File("a.txt")], dest, new()), dest, MoveOp.Move, historyFile: file);
        Assert.Equal(1, r.Moved);
        Assert.True(batch.CreatedDest);
        Assert.Equal(batch.Id, MoveHistory.Last(file)!.Id);
        Assert.Equal(["history.json"], Directory.GetFiles(Path.Combine(t.Root, "data")).Select(p => Path.GetFileName(p)!));
        // 複製不留撤回紀錄
        MoveSession.Run(NamePlanner.Plan([t.File("c.txt")], dest, new(Copy: true)), dest, MoveOp.Copy, historyFile: file);
        Assert.Equal(batch.Id, MoveHistory.Last(file)!.Id);
    }

    [Fact]
    public void RecordIsWritten_BeforeSourceIsDeleted()
    {
        using var t = new TempDir();
        var src = t.Dir("proj"); t.File("proj/a.txt");
        var dest = @"\\?\" + t.Dir("other");
        var file = Path.Combine(t.Root, "data", "history.json");
        // 刪來源時被鎖住一個檔，代表「刪到一半」的樣子；這時紀錄已經寫好了
        var batch = new MoveBatch { Dest = dest };
        string pendingText = "";
        FileStream? hold = null;
        Mover.TestStep = step =>
        {
            if (step != "committed") return;
            Assert.Single(Directory.GetDirectories(t.Root, "~mt-*"));
            hold = File.Open(Path.Combine(Directory.GetDirectories(t.Root, "~mt-*")[0], "a.txt"), FileMode.Open, FileAccess.Read, FileShare.Read);
        };
        using (var j = MoveJournal.Start(batch, file))
        {
            var r = Mover.Execute(NamePlanner.Plan([src], dest, new()), dest, journal: j);
            Assert.Single(r.Leftovers);
            using var sr = new StreamReader(new FileStream(Directory.GetFiles(Path.Combine(t.Root, "data"), "pending-*")[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            pendingText = sr.ReadToEnd();
        }
        Mover.TestStep = null;
        hold!.Dispose();
        Assert.Contains("\"Moved\"", pendingText);

        // 下次啟動：確定在目的地了，把沒刪完的來源刪完，並且可以撤回
        MoveJournal.Recover(file);
        Assert.Empty(Directory.GetDirectories(t.Root, "~mt-*"));
        Assert.Equal(1, MoveSession.Undo(MoveHistory.Last(file)!, historyFile: file).Restored);
        Assert.True(File.Exists(Path.Combine(src, "a.txt")));
    }

    sealed class Crash : Exception { }

    /// <summary>模擬程式在某一步突然消失：該步之後什麼都不做，連收拾都沒有。</summary>
    static void CrashAt(string stopAt, string src, string dest, string file)
    {
        Mover.TestStep = step =>
        {
            if (step != stopAt) return;
            Mover.TestStep = null;
            throw new Crash();
        };
        Mover.TestSkipCleanup = true;
        try
        {
            using var j = MoveJournal.Start(new MoveBatch { Dest = dest }, file);
            Assert.Throws<Crash>(() => Mover.Execute(NamePlanner.Plan([src], dest, new()), dest, journal: j));
        }
        finally { Mover.TestSkipCleanup = false; Mover.TestStep = null; }
    }

    [Theory]
    [InlineData("copied")]
    [InlineData("parked")]
    [InlineData("committed")]   // 換成正式名字了，但還沒記：兩邊都留著，不冒險刪
    public void CrashDuringCrossVolumeMove_NextStartPutsSourceBack(string step)
    {
        using var t = new TempDir();
        var src = t.Dir("proj"); t.File("proj/a.txt", "a"); t.File("proj/sub/b.txt", "b");
        var dest = @"\\?\" + t.Dir("other");
        var file = Path.Combine(t.Root, "data", "history.json");
        CrashAt(step, src, dest, file);

        MoveJournal.Recover(file);
        Assert.Equal(("a", "b"), (File.ReadAllText(Path.Combine(src, "a.txt")), File.ReadAllText(Path.Combine(src, "sub", "b.txt"))));
        Assert.Empty(Directory.GetDirectories(t.Root, "~mt-*"));
        Assert.Empty(Directory.GetDirectories(dest, "~mt-*"));
        Assert.Equal(step == "committed", Directory.Exists(Path.Combine(dest, "proj")));
        Assert.Null(MoveHistory.Last(file));
        Assert.Empty(Directory.GetFiles(Path.Combine(t.Root, "data"), "pending-*"));
    }

    [Fact]
    public void CrashThenSourceNameReused_ContentKeptUnderNumber()
    {
        using var t = new TempDir();
        var src = t.Dir("proj"); t.File("proj/a.txt", "a");
        var dest = @"\\?\" + t.Dir("other");
        var file = Path.Combine(t.Root, "data", "history.json");
        CrashAt("parked", src, dest, file);
        t.File("proj/後來建的.txt");
        MoveJournal.Recover(file);
        Assert.Equal("a", File.ReadAllText(Path.Combine(t.Root, "proj (2)", "a.txt")));
        Assert.True(File.Exists(Path.Combine(src, "後來建的.txt")));
    }

    [Fact]
    public void CrashDuringRenameSwap_ParkedFileGetsItsNameBack()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Root, "data", "history.json");
        var a = t.File("pics/001.jpg", "一");
        var parked = Path.Combine(t.Root, "pics", "~mt-12345678.jpg");
        using (var j = MoveJournal.Start(new MoveBatch { Kind = BatchKind.Rename, Dest = t.Root }, file))
        {
            j.Parking(new(a, parked, false));
            File.Move(a, parked);
        }
        MoveJournal.Recover(file);
        Assert.Equal("一", File.ReadAllText(a));
        Assert.Single(Directory.GetFiles(Path.Combine(t.Root, "pics")));
    }

    [Fact]
    public void CrashDuringCopy_HalfCopyIsCleaned_SourceKept()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Root, "data", "history.json");
        var (f, d) = (t.File("src/a.txt", "a"), Path.GetDirectoryName(t.File("src/d/b.txt", "b"))!);
        var (cf, cd) = (t.File("dest/~mt-11111111", "half"), t.Dir("dest/~mt-22222222"));
        using (var j = MoveJournal.Start(new MoveBatch(), file))
        {
            j.Parking(new(f, "", false, cf));
            j.Parking(new(d, "", true, cd));
        }
        MoveJournal.Recover(file);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(t.Root, "dest")));
        Assert.True(File.Exists(f) && File.Exists(Path.Combine(d, "b.txt")));
    }

    [Fact]
    public void BrokenPendingFile_DoesNotBlockAnything()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Root, "data", "history.json");
        t.File("data/pending-x.jsonl", "{\"Batch\":{\"Items\":null}}\n{\"Park\":{}}\n壞掉");
        t.File("data/pending-y.jsonl", "");
        Assert.Null(MoveHistory.Last(file));
    }

    /// <summary>搬好了、來源刪到一半時中斷：dest\\proj 是完整的，~mt-1-proj 是沒刪完的來源。</summary>
    static (string src, string parked, string to, string file) InterruptedWhileDeleting(TempDir t)
    {
        var (src, parked, to) = (Path.Combine(t.Root, "proj"), t.Dir("~mt-1-proj"), t.Dir("dest/proj"));
        foreach (var d in new[] { "~mt-1-proj", "dest/proj" }) { t.File(d + "/a.txt", "a"); t.File(d + "/sub/b.txt", "b"); t.File(d + "/c.txt", "c"); }
        File.SetAttributes(Path.Combine(parked, "a.txt"), FileAttributes.ReadOnly);
        var file = Path.Combine(t.Root, "data", "history.json");
        using var j = MoveJournal.Start(new MoveBatch { Dest = Path.Combine(t.Root, "dest") }, file);
        j.Parking(new(src, parked, true, Path.Combine(t.Root, "dest", "~mt-2")));
        j.Moved(MoveRecord.Of(src, to, true));
        return (src, parked, to, file);
    }

    [Fact]
    public void Recover_TargetComplete_DeletesLeftoverSource()
    {
        using var t = new TempDir();
        var (src, parked, to, file) = InterruptedWhileDeleting(t);
        MoveJournal.Recover(file);
        Assert.False(Directory.Exists(parked));
        Assert.False(Directory.Exists(src));
        Assert.Equal(3, Directory.GetFiles(to, "*", SearchOption.AllDirectories).Length);
        Assert.Equal(to, MoveHistory.Last(file)!.Items[0].To);
    }

    [Fact]
    public void Recover_TargetLostOrChangedFiles_KeepsThoseInSource()
    {
        using var t = new TempDir();
        var (src, parked, to, file) = InterruptedWhileDeleting(t);
        File.Delete(Path.Combine(to, "sub", "b.txt"));       // 目的地少了一個
        File.WriteAllText(Path.Combine(to, "c.txt"), "x");   // 大小一樣、內容不同
        MoveJournal.Recover(file);
        Assert.False(Directory.Exists(parked));              // 不會留在奇怪的名字裡
        Assert.Equal(["c.txt", @"sub\b.txt"], Directory.GetFiles(src, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(src, p)).Order());
        Assert.Equal("c", File.ReadAllText(Path.Combine(src, "c.txt")));
        Assert.Equal("x", File.ReadAllText(Path.Combine(to, "c.txt")));
        Assert.True(File.Exists(Path.Combine(to, "a.txt")));
    }

    [Fact]
    public void Recover_TargetFolderGoneOrReplaced_KeepsWholeSource()
    {
        using var t = new TempDir();
        var (src, _, to, file) = InterruptedWhileDeleting(t);
        Directory.Delete(to, true);
        t.File("dest/proj/別的.txt");
        MoveJournal.Recover(file);
        Assert.Equal(3, Directory.GetFiles(src, "*", SearchOption.AllDirectories).Length);
        File.SetAttributes(Path.Combine(src, "a.txt"), FileAttributes.Normal);
    }

    [Fact]
    public void RunningJournal_IsLeftAlone()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Root, "data", "history.json");
        var a = t.File("a"); var parked = Path.Combine(t.Root, "~mt-1");
        using var j = MoveJournal.Start(new MoveBatch(), file);
        j.Parking(new(a, parked, false));
        File.Move(a, parked);
        MoveJournal.Recover(file);
        Assert.True(File.Exists(parked));
    }

    [Fact]
    public void HistoryNotWritable_MoveStillWorks()
    {
        using var t = new TempDir();
        var blocker = t.File("data");                 // 設定資料夾的位置被一個檔案佔住
        var dest = t.Dir("dest");
        var (r, batch) = MoveSession.Run(NamePlanner.Plan([t.File("a.txt")], dest, new()), dest, MoveOp.Move, historyFile: Path.Combine(blocker, "history.json"));
        Assert.Equal((1, 1), (r.Moved, batch.Items.Count));
    }
}
