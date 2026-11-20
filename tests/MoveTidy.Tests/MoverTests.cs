using MoveTidy.Core;

namespace MoveTidy.Tests;

public class MoverTests
{
    [Fact]
    public void Moves_FilesAndFolders_WithPlannedNames_AndCreatesDateFolder()
    {
        using var t = new TempDir();
        var src = new[] { t.File("src/報告.docx", "new"), t.Dir("src/會議資料") };
        t.File("src/會議資料/內容.txt", "inside");
        var dest = Path.Combine(t.Root, "dest", "2026-11-20");
        t.File("dest/2026-11-20/報告.docx", "old");

        var plan = NamePlanner.Plan(src, dest, new());
        var progress = new List<int>();
        var r = Mover.Execute(plan, dest, new SyncProgress(progress));

        Assert.Equal(2, r.Moved);
        Assert.Equal(1, r.Renamed);
        Assert.Empty(r.Failures);
        Assert.Equal("old", File.ReadAllText(Path.Combine(dest, "報告.docx")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(dest, "報告 (2).docx")));
        Assert.Equal("inside", File.ReadAllText(Path.Combine(dest, "會議資料", "內容.txt")));
        Assert.False(File.Exists(src[0]));
        Assert.False(Directory.Exists(src[1]));
        Assert.Equal([1, 2], progress);
    }

    [Fact]
    public void ConflictAppearingAfterPlanning_IsStillNotOverwritten()
    {
        using var t = new TempDir();
        var a = t.File("src/a.txt", "mine");
        var dest = t.Dir("dest");
        var plan = NamePlanner.Plan([a], dest, new());
        t.File("dest/a.txt", "someone else");

        var r = Mover.Execute(plan, dest);

        Assert.Equal(1, r.Moved);
        Assert.Equal("someone else", File.ReadAllText(Path.Combine(dest, "a.txt")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(dest, "a (2).txt")));
    }

    [Fact]
    public void SkippedItems_AreLeftAlone()
    {
        using var t = new TempDir();
        var dest = t.Dir("dest");
        var inside = t.File("dest/x.txt");
        var r = Mover.Execute(NamePlanner.Plan([inside], dest, new()), dest);
        Assert.Equal(0, r.Moved);
        Assert.Equal(1, r.Skipped);
        Assert.True(File.Exists(inside));
    }

    [Fact]
    public void LockedFile_IsReportedAsFailure_OthersStillMove()
    {
        using var t = new TempDir();
        var locked = t.File("src/locked.txt");
        var ok = t.File("src/ok.txt");
        var dest = t.Dir("dest");
        using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var r = Mover.Execute(NamePlanner.Plan([locked, ok], dest, new()), dest);
            Assert.Equal(1, r.Moved);
            Assert.Equal(32, Assert.Single(r.Failures).Code); // 32 = 被其他程式使用中
        }
        Assert.True(File.Exists(Path.Combine(dest, "ok.txt")));
        Assert.True(File.Exists(locked));
    }

    [Fact]
    public void Settings_RoundTrip()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Root, "s", "settings.json");
        var s = new AppSettings { Mode = NamingMode.Number, Digits = 2, Language = "cn" };
        s.Favorites.Add(new() { Name = "下載整理", Path = @"D:\下載整理", ShowDateEntry = false });
        s.Save(file);
        var back = AppSettings.Load(file);
        Assert.Equal(NamingMode.Number, back.Mode);
        Assert.Equal(2, back.Digits);
        Assert.Equal("cn", back.Language);
        Assert.Equal("下載整理", back.Favorites[0].Name);
        Assert.False(back.Favorites[0].ShowDateEntry);
        Assert.Contains("\"Number\"", File.ReadAllText(file));
    }

    sealed class SyncProgress(List<int> sink) : IProgress<int>
    {
        public void Report(int value) => sink.Add(value);
    }
}
