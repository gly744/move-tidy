using MoveTidy.Core;

namespace MoveTidy.Tests;

public class FolderTidyTests
{
    [Theory]
    [InlineData("11-21", true)]
    [InlineData("1121", true)]
    [InlineData("11月21日", true)]
    [InlineData("2026-11-21", true)]
    [InlineData("02-29", true)]      // 閏日也認得
    [InlineData("13-01", false)]     // 沒有 13 月
    [InlineData("1199", false)]
    [InlineData("照片", false)]
    [InlineData("2026", false)]
    [InlineData("11-21 舊的", false)]
    public void IsDateFolderName(string name, bool expected) =>
        Assert.Equal(expected, FolderTidy.IsDateFolderName(name));

    [Fact]
    public void Collect_SkipsDateFoldersAndSystemFiles()
    {
        using var t = new TempDir();
        t.File("a.jpg"); t.File("b.txt");
        t.Dir("11-20"); t.Dir("2026-11-19"); t.Dir("旅行");
        var ini = t.File("desktop.ini");
        File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);

        var names = FolderTidy.Collect(t.Root, includeFolders: true).Select(Path.GetFileName).ToList();
        Assert.Equal(["a.jpg", "b.txt", "旅行"], names);

        var filesOnly = FolderTidy.Collect(t.Root, includeFolders: false).Select(Path.GetFileName).ToList();
        Assert.Equal(["a.jpg", "b.txt"], filesOnly);
    }

    [Fact]
    public void Tidy_MovesEverythingIntoToday_TwiceTheSameDay()
    {
        using var t = new TempDir();
        var s = new AppSettings();
        var today = new DateTime(2026, 11, 21);
        var dest = Path.Combine(t.Root, s.TodayFolderName(today));

        for (int i = 1; i <= 150; i++) t.File($"p{i}.jpg");   // 超過右鍵的 100 個上限
        t.Dir("旅行"); t.File("旅行/x.jpg");
        Mover.Execute(NamePlanner.Plan(FolderTidy.Collect(t.Root, true), dest, s.QuickOptions), dest);

        // 同一天第二批，其中一個重名
        t.File("p1.jpg", "new"); t.File("q.jpg");
        Mover.Execute(NamePlanner.Plan(FolderTidy.Collect(t.Root, true), dest, s.QuickOptions), dest);

        Assert.Equal(["11-21"], Directory.EnumerateFileSystemEntries(t.Root).Select(Path.GetFileName));
        Assert.Equal(150 + 1 + 2, Directory.EnumerateFileSystemEntries(dest).Count()); // 150 張 + 旅行 + p1 (2) + q
        Assert.True(File.Exists(Path.Combine(dest, "p1 (2).jpg")));
        Assert.True(File.Exists(Path.Combine(dest, "旅行", "x.jpg")));
    }
}
