using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using MoveTidy.Core;

namespace MoveTidy.Tests;

/// <summary>
/// 跨磁碟搬資料夾：不留半份、來源不會只剩一部分。
/// 目的地路徑加上 \\?\，根目錄就和來源不同，會走「複製再刪除」那條路，不需要真的有第二個磁碟。
/// </summary>
[SupportedOSPlatform("windows")]
public class CrossVolumeTests
{
    static string Other(TempDir t) => @"\\?\" + t.Dir("other");

    static string Make(TempDir t, string name = "proj")
    {
        foreach (var f in new[] { "a.txt", "b.txt", @"sub\c.txt" }) t.File(Path.Combine(name, f), f);
        return Path.Combine(t.Root, name);
    }

    static MoveResult Go(string src, string dest, bool copy = false) =>
        Mover.Execute(NamePlanner.Plan([src], dest, new(Copy: copy)), dest, copy: copy);

    static string[] Tree(string dir) =>
        [.. Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories).Where(p => !Directory.Exists(p) || !Directory.EnumerateFileSystemEntries(p).Any())
            .Select(p => Path.GetRelativePath(dir, p)).Order(StringComparer.Ordinal)];

    static readonly string[] Full = ["a.txt", "b.txt", @"sub\c.txt"];

    [Theory]
    [InlineData(FileAccess.ReadWrite, FileShare.None)]  // 讀不了：複製到一半失敗
    [InlineData(FileAccess.Read, FileShare.Read)]       // 讀得了但刪不掉：以前會把來源刪掉一半
    public void FileInUse_NothingMoves_NoHalfCopy_RetryWorks(FileAccess access, FileShare share)
    {
        using var t = new TempDir();
        var src = Make(t);
        var dest = Other(t);
        using (File.Open(Path.Combine(src, "sub", "c.txt"), FileMode.Open, access, share))
        {
            var r = Go(src, dest);
            Assert.Single(r.Failures);
            Assert.Equal(0, r.Moved);
        }
        Assert.Equal(Full, Tree(src));
        Assert.Empty(Tree(dest));
        Assert.Equal(["other", "proj"], Directory.GetDirectories(t.Root).Select(p => Path.GetFileName(p)!).Order());

        var again = Go(src, dest);
        Assert.Equal((1, 0), (again.Moved, again.Renamed));   // 沒有多出 proj (2)
        Assert.False(Directory.Exists(src));
        Assert.Equal(Full, Tree(Path.Combine(dest, "proj")));
    }

    [Fact]
    public void CopyFolder_FileInUse_NoHalfCopy()
    {
        using var t = new TempDir();
        var src = Make(t);
        var dest = t.Dir("dest");
        using (File.Open(Path.Combine(src, "sub", "c.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Single(Go(src, dest, copy: true).Failures);
        Assert.Empty(Tree(dest));
        Assert.Equal(Full, Tree(src));
    }

    [Fact]
    public void ReadOnlyFiles_MoveCompletely_AndUndo()
    {
        using var t = new TempDir();
        var src = Make(t);
        File.SetAttributes(Path.Combine(src, "b.txt"), FileAttributes.ReadOnly);
        File.SetAttributes(Path.Combine(src, "sub"), FileAttributes.Directory | FileAttributes.ReadOnly);
        var dest = Other(t);

        var r = Go(src, dest);
        Assert.Empty(r.Failures);
        Assert.Empty(r.Leftovers);
        Assert.False(Directory.Exists(src));
        var b = Path.Combine(dest, "proj", "b.txt");
        Assert.True(File.GetAttributes(b).HasFlag(FileAttributes.ReadOnly));   // 唯讀屬性跟著過去

        var u = Undoer.Execute(MoveBatch.From(r, dest));
        Assert.Equal((1, 0), (u.Restored, u.Renamed));
        Assert.Equal(Full, Tree(src));
        Assert.Empty(Tree(dest));
        File.SetAttributes(Path.Combine(src, "b.txt"), FileAttributes.Normal);
    }

    [Fact]
    public void UndeletableSource_IsReported_AndUndoStillRestoresOriginalName()
    {
        using var t = new TempDir();
        var src = Make(t);
        var dest = Other(t);
        var me = WindowsIdentity.GetCurrent().User!;
        var sub = new DirectoryInfo(Path.Combine(src, "sub"));
        var file = new FileInfo(Path.Combine(src, "sub", "c.txt"));
        var denyDir = new FileSystemAccessRule(me, FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Deny);
        var denyFile = new FileSystemAccessRule(me, FileSystemRights.Delete, AccessControlType.Deny);
        var ds = sub.GetAccessControl(); ds.AddAccessRule(denyDir); sub.SetAccessControl(ds);
        var fs = file.GetAccessControl(); fs.AddAccessRule(denyFile); file.SetAccessControl(fs);
        string? left = null;
        try
        {
            var r = Go(src, dest);
            Assert.Empty(r.Failures);
            left = Assert.Single(r.Leftovers).Left;
            Assert.Equal([@"sub\c.txt"], Tree(left));
            Assert.False(Directory.Exists(src));              // 原本的名字空出來
            Assert.Equal(Full, Tree(Path.Combine(dest, "proj")));

            var u = Undoer.Execute(MoveBatch.From(r, dest));
            Assert.Equal((1, 0), (u.Restored, u.Renamed));
            Assert.Equal(Full, Tree(src));
        }
        finally
        {
            var d = new DirectoryInfo(Path.Combine(left ?? src, "sub"));
            var f = new FileInfo(Path.Combine(d.FullName, "c.txt"));
            ds = d.GetAccessControl(); ds.RemoveAccessRule(denyDir); d.SetAccessControl(ds);
            fs = f.GetAccessControl(); fs.RemoveAccessRule(denyFile); f.SetAccessControl(fs);
        }
    }

    [Fact]
    public void JunctionInside_DoesNotDeleteWhatItPointsTo()
    {
        using var t = new TempDir();
        var src = Make(t);
        var keep = t.File("外面/重要.txt", "keep");
        using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(src, "link")}\" \"{Path.GetDirectoryName(keep)}\"") { RedirectStandardOutput = true })!)
            p.WaitForExit();
        Assert.True(File.Exists(Path.Combine(src, "link", "重要.txt")));

        var r = Go(src, Other(t));
        Assert.Empty(r.Failures);
        Assert.False(Directory.Exists(src));
        Assert.Equal("keep", File.ReadAllText(keep));
    }

    [Fact]
    public void TargetClash_KeepsExisting_AddsNumber()
    {
        using var t = new TempDir();
        var src = Make(t);
        var dest = Other(t);
        t.File("other/proj/舊的.txt");
        var r = Go(src, dest);
        Assert.Equal((1, 1), (r.Moved, r.Renamed));
        Assert.Equal(Full, Tree(Path.Combine(dest, "proj (2)")));
        Assert.Equal(["舊的.txt"], Tree(Path.Combine(dest, "proj")));
    }
}

/// <summary>兩個視窗交錯保存，不會把對方改的設定寫回舊的；右鍵選單認 ID，不認第幾個。</summary>
public class SettingsSaveTests
{
    static AppSettings Two() => new() { Favorites = [new() { Name = "A", Path = @"C:\A" }, new() { Name = "B", Path = @"C:\B" }] };

    [Fact]
    public void InterleavedWindows_KeepEachOthersChanges()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Root, "settings.json");
        Two().Save(file);
        // 「移到其他位置」先開著，設定視窗調了順序，然後前一個視窗才保存
        AppSettings.Update(s => s.Favorites.Reverse(), file);
        AppSettings.Update(s => s.PickLastPath = @"C:\x", file);

        var now = AppSettings.Load(file);
        Assert.Equal(["B", "A"], now.Favorites.Select(f => f.Name));
        Assert.Equal(@"C:\x", now.PickLastPath);
    }

    [Fact]
    public async Task ManySavesAtOnce_NoneLost()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Root, "settings.json");
        new AppSettings { Digits = 0 }.Save(file);
        await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Task.Run(() => AppSettings.Update(s => s.Digits++, file))));
        Assert.Equal(40, AppSettings.Load(file).Digits);
    }

    [Fact]
    public void FavoriteId_SurvivesReorderRenameAndReload()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Root, "settings.json");
        var s = Two();
        var (a, b) = (s.Favorites[0].Id, s.Favorites[1].Id);
        Assert.NotEqual(a, b);
        s.Save(file);
        Assert.DoesNotContain(a, File.ReadAllText(file));
        s = AppSettings.Update(x => { x.Favorites.Reverse(); x.Favorites[0].Name = "改過名"; }, file);
        Assert.Equal(@"C:\B", s.FindFavorite(b)!.Path);
        Assert.Equal(@"C:\A", s.FindFavorite(a)!.Path);
        s = AppSettings.Update(x => x.Favorites.RemoveAt(0), file);
        Assert.Null(s.FindFavorite(b));                       // 移除了就找不到，不會拿到別的
        Assert.Equal(@"C:\A", s.FindFavorite(a)!.Path);
    }
}
