using System.Text.RegularExpressions;
using MoveTidy.Core;

namespace MoveTidy.Tests;

public class RenameTests
{
    static readonly DateTime Now = new(2026, 11, 24, 15, 30, 12);

    static RenameOptions Opt(RenameStyle style, string prefix = "", NumberOrder order = NumberOrder.Name, int digits = 3) =>
        new(style, order, digits, prefix, Now);

    static string[] Names(string dir) => [.. Directory.EnumerateFileSystemEntries(dir).Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal)];

    [Theory]
    [InlineData(RenameStyle.StampRandom, @"^20261124-153012-[2-9a-hjkmnp-z]{4}\.jpg$")]
    [InlineData(RenameStyle.RandomText, @"^[2-9a-hjkmnp-z]{8}\.jpg$")]
    [InlineData(RenameStyle.RandomDigits, @"^[1-9]\d{9}\.jpg$")]
    [InlineData(RenameStyle.KeepRandom, @"^image( \(\d+\))?_[2-9a-hjkmnp-z]{4}\.jpg$")]
    [InlineData(RenameStyle.StampNumber, @"^20261124-153012-00[1-3]\.jpg$")]
    [InlineData(RenameStyle.PrefixNumber, @"^貓咪-00[1-3]\.jpg$")]
    [InlineData(RenameStyle.PureNumber, @"^00[1-3]\.jpg$")]
    public void EveryStyle_RenamesInPlace_AndUndoRestores(RenameStyle style, string pattern)
    {
        using var t = new TempDir();
        string[] src = [t.File("image.jpg", "a"), t.File("image (1).jpg", "b"), t.File("image (2).jpg", "c")];
        t.File("沒選到.jpg");

        var r = Renamer.Execute(Renamer.Plan(src, Opt(style, "貓咪")));
        Assert.Empty(r.Failures);
        Assert.Equal(3, r.Moved);
        Assert.Equal(3, Names(t.Root).Count(n => Regex.IsMatch(n, pattern)));
        Assert.True(File.Exists(Path.Combine(t.Root, "沒選到.jpg")));

        var u = Undoer.Execute(MoveBatch.From(r, t.Root, BatchKind.Rename));
        Assert.Equal(3, u.Restored);
        Assert.Equal(0, u.Renamed);
        Assert.Equal(["a", "b", "c"], src.Select(File.ReadAllText));
        Assert.Equal(4, Names(t.Root).Length);
    }

    [Theory]
    [InlineData(RenameStyle.StampRandom)]
    [InlineData(RenameStyle.RandomText)]
    [InlineData(RenameStyle.RandomDigits)]
    [InlineData(RenameStyle.KeepRandom)]
    public void Random_500Items_AllDifferent(RenameStyle style)
    {
        using var t = new TempDir();
        var src = Enumerable.Range(1, 500).Select(i => t.File($"same{i}.png")).ToList();
        var plan = Renamer.Plan(src, Opt(style));
        Assert.Equal(500, plan.Select(p => p.TargetName.ToLowerInvariant()).Distinct().Count());
    }

    [Fact]
    public void Number_ContinuesAfterUnselected()
    {
        using var t = new TempDir();
        t.File("007.jpg"); t.File("貓咪-012.png"); t.File("貓咪-x.png");
        string[] src = [t.File("b.jpg"), t.File("a.png")];
        Assert.Equal(["008.png", "009.jpg"], Renamer.Plan(src, Opt(RenameStyle.PureNumber)).Select(p => p.TargetName));
        Assert.Equal(["貓咪-013.png", "貓咪-014.jpg"], Renamer.Plan(src, Opt(RenameStyle.PrefixNumber, "貓咪")).Select(p => p.TargetName));
    }

    [Fact]
    public void Number_OrderByModifiedTime()
    {
        using var t = new TempDir();
        string[] src = [t.File("a.jpg", time: new(2026, 2, 1)), t.File("b.png", time: new(2026, 1, 1))];
        var plan = Renamer.Plan(src, Opt(RenameStyle.PureNumber, order: NumberOrder.Modified, digits: 2));
        Assert.Equal(["01.png", "02.jpg"], plan.Select(p => p.TargetName));
    }

    [Fact]
    public void Number_NamesSwapWithinSelection_NothingLost_UndoRestores()
    {
        using var t = new TempDir();
        // 依修改時間：003→001、001→002、002→003，彼此佔住對方的名字
        string[] src =
        [
            t.File("001.jpg", "一", new(2026, 1, 2)),
            t.File("002.jpg", "二", new(2026, 1, 3)),
            t.File("003.jpg", "三", new(2026, 1, 1)),
        ];
        var r = Renamer.Execute(Renamer.Plan(src, Opt(RenameStyle.PureNumber, order: NumberOrder.Modified)));
        Assert.Empty(r.Failures);
        Assert.Equal(["001.jpg", "002.jpg", "003.jpg"], Names(t.Root));
        Assert.Equal(["三", "一", "二"], src.Select(File.ReadAllText));

        var u = Undoer.Execute(MoveBatch.From(r, t.Root, BatchKind.Rename));
        Assert.Equal(3, u.Restored);
        Assert.Equal(0, u.Renamed);
        Assert.Equal(["001.jpg", "002.jpg", "003.jpg"], Names(t.Root));
        Assert.Equal(["一", "二", "三"], src.Select(File.ReadAllText));
    }

    [Fact]
    public void AlreadyNamed_IsSkipped()
    {
        using var t = new TempDir();
        string[] src = [t.File("001.jpg"), t.File("x.jpg")];
        var plan = Renamer.Plan(src, Opt(RenameStyle.PureNumber));
        Assert.Equal(SkipReason.SameName, plan[0].Skip);
        var r = Renamer.Execute(plan);
        Assert.Equal((1, 1), (r.Moved, r.Skipped));
        Assert.Equal(["001.jpg", "002.jpg"], Names(t.Root));
    }

    [Fact]
    public void LockedFile_FailsAlone_OthersStillRenamed()
    {
        using var t = new TempDir();
        var a = t.File("002.jpg", "a", new(2026, 1, 1)); var b = t.File("001.jpg", "b", new(2026, 1, 2));
        var plan = Renamer.Plan([a, b], Opt(RenameStyle.PureNumber, order: NumberOrder.Modified));
        MoveResult r;
        using (File.Open(b, FileMode.Open, FileAccess.Read, FileShare.None))
            r = Renamer.Execute(plan);
        Assert.NotEmpty(r.Failures);
        // 被鎖住的沒動，兩個檔案的內容都還在，也沒有留下暫時的名字
        Assert.Equal("b", File.ReadAllText(b));
        Assert.Equal(2, Names(t.Root).Length);
        Assert.DoesNotContain(Names(t.Root), n => n.StartsWith("~mt-"));
        Assert.Equal(["a", "b"], Directory.GetFiles(t.Root).Select(File.ReadAllText).Order());
    }

    [Fact]
    public void FolderAndFileInsideIt_BothRenamed_UndoRestores()
    {
        using var t = new TempDir();
        var f = t.File("相簿.v2/image.jpg", "x");
        var d = Path.Combine(t.Root, "相簿.v2");
        var r = Renamer.Execute(Renamer.Plan([d, f], Opt(RenameStyle.StampRandom)));
        Assert.Empty(r.Failures);
        var folder = Assert.Single(Directory.GetDirectories(t.Root));
        Assert.Matches(@"^20261124-153012-\w{4}$", Path.GetFileName(folder));   // 資料夾沒有副檔名
        Assert.Matches(@"^20261124-153012-\w{4}\.jpg$", Path.GetFileName(Assert.Single(Directory.GetFiles(folder))));

        Assert.Equal(2, Undoer.Execute(MoveBatch.From(r, t.Root, BatchKind.Rename)).Restored);
        Assert.Equal("x", File.ReadAllText(f));
    }

    [Fact]
    public void SpecialNames()
    {
        using var t = new TempDir();
        string[] src = [t.File(".gitignore"), t.File("沒副檔名"), Path.Combine(t.Root, "不存在.jpg")];
        var plan = Renamer.Plan(src, Opt(RenameStyle.PrefixNumber, " a/b:c*. "));
        Assert.Equal(["abc-001", "abc-002"], plan.Where(p => p.Skip == SkipReason.None).Select(p => p.TargetName));
        Assert.Equal(SkipReason.Missing, plan[^1].Skip);
        Assert.Equal("", Renamer.CleanPrefix(" ?. "));
    }

    [Fact]
    public void ItemMovedAwayAfterPlanning_IsFailure()
    {
        using var t = new TempDir();
        var a = t.File("a.jpg");
        var plan = Renamer.Plan([a], Opt(RenameStyle.StampRandom));
        File.Delete(a);
        Assert.Equal(2, Assert.Single(Renamer.Execute(plan).Failures).Code);
    }

    [Fact]
    public void Settings_DefaultsAndOldFiles()
    {
        using var t = new TempDir();
        var file = t.File("settings.json", """{ "Mode": "Number", "Language": "cn" }""");
        var s = AppSettings.Load(file);
        Assert.Equal(RenameStyle.StampRandom, s.RenameStyle);
        Assert.False(s.RenameNeedsWindow);
        s.RenameStyle = RenameStyle.PrefixNumber;
        s.RenamePrefix = "貓咪";
        s.Save(file);
        s = AppSettings.Load(file);
        Assert.True(s.RenameNeedsWindow);
        Assert.Equal(("貓咪", NamingMode.Number), (s.RenamePrefix, s.Mode));
    }

    // ── 貼到這裡 ──

    [Fact]
    public void PasteCopy_KeepsSource_NumbersClash_NoUndoRecord()
    {
        using var t = new TempDir();
        var a = t.File("src/a.jpg", "new"); t.File("src/相簿/1.jpg", "1");
        var dest = t.Dir("dest"); t.File("dest/a.jpg", "old");
        var plan = NamePlanner.Plan([a, Path.Combine(t.Root, "src", "相簿")], dest, new(Copy: true));
        var r = Mover.Execute(plan, dest, copy: true);
        Assert.Empty(r.Failures);
        Assert.Empty(r.Records);
        Assert.Equal("new", File.ReadAllText(a));
        Assert.Equal("old", File.ReadAllText(Path.Combine(dest, "a.jpg")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(dest, "a (2).jpg")));
        Assert.True(File.Exists(Path.Combine(dest, "相簿", "1.jpg")));
        Assert.True(File.Exists(Path.Combine(t.Root, "src", "相簿", "1.jpg")));
    }

    [Fact]
    public void PasteCopy_IntoSameFolder_MakesSecondCopy()
    {
        using var t = new TempDir();
        var a = t.File("a.jpg");
        Mover.Execute(NamePlanner.Plan([a], t.Root, new(Copy: true)), t.Root, copy: true);
        Assert.Equal(["a (2).jpg", "a.jpg"], Names(t.Root));
    }
}
