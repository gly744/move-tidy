using MoveTidy.Core;

namespace MoveTidy.Tests;

/// <summary>每個測試用自己的暫存資料夾，結束後刪掉。</summary>
public sealed class TempDir : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "MoveTidyTests", Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Root);

    public string File(string rel, string content = "x", DateTime? time = null)
    {
        var p = Path.Combine(Root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content);
        if (time is { } t) System.IO.File.SetLastWriteTime(p, t);
        return p;
    }

    public string Dir(string rel)
    {
        var p = Path.Combine(Root, rel);
        Directory.CreateDirectory(p);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch { }
    }
}

public class PlannerTests
{
    [Fact]
    public void Keep_NoConflict_KeepsName()
    {
        using var t = new TempDir();
        var a = t.File("src/a.txt");
        var dest = t.Dir("dest");
        var plan = NamePlanner.Plan([a], dest, new());
        Assert.Equal("a.txt", plan[0].TargetName);
        Assert.False(plan[0].Renamed);
    }

    [Fact]
    public void Keep_Conflict_AddsLowestFreeNumber()
    {
        using var t = new TempDir();
        var src = new[] { t.File("src/報告.docx"), t.File("src/截圖.png") };
        t.File("dest/報告.docx");
        t.File("dest/截圖.png");
        t.File("dest/截圖 (2).png");
        var plan = NamePlanner.Plan(src, Path.Combine(t.Root, "dest"), new());
        Assert.Equal("報告 (2).docx", plan[0].TargetName);
        Assert.Equal("截圖 (3).png", plan[1].TargetName);
        Assert.All(plan, p => Assert.True(p.Renamed));
    }

    [Fact]
    public void Keep_ConflictIsCaseInsensitive()
    {
        using var t = new TempDir();
        var a = t.File("src/Photo.JPG");
        t.File("dest/photo.jpg");
        var plan = NamePlanner.Plan([a], Path.Combine(t.Root, "dest"), new());
        Assert.Equal("Photo (2).JPG", plan[0].TargetName);
    }

    [Fact]
    public void Keep_AlreadyNumberedName_DoesNotStackSuffix()
    {
        using var t = new TempDir();
        var a = t.File("src/圖 (2).png");
        t.File("dest/圖 (2).png");
        var plan = NamePlanner.Plan([a], Path.Combine(t.Root, "dest"), new());
        Assert.Equal("圖 (3).png", plan[0].TargetName);
    }

    [Fact]
    public void Keep_SameNameFromTwoFolders_BothGetUniqueNames()
    {
        using var t = new TempDir();
        var a = t.File("x/a.txt");
        var b = t.File("y/a.txt");
        var plan = NamePlanner.Plan([a, b], t.Dir("dest"), new());
        Assert.Equal(["a.txt", "a (2).txt"], plan.Select(p => p.TargetName));
    }

    [Fact]
    public void Keep_FolderConflict_RenamesFolderWithoutExtensionSplit()
    {
        using var t = new TempDir();
        var d = t.Dir("src/v1.2 資料");
        t.Dir("dest/v1.2 資料");
        var plan = NamePlanner.Plan([d], Path.Combine(t.Root, "dest"), new());
        Assert.Equal("v1.2 資料 (2)", plan[0].TargetName);
        Assert.True(plan[0].IsDirectory);
    }

    [Fact]
    public void Number_ContinuesAfterExistingNumbers_KeepsExtension()
    {
        using var t = new TempDir();
        var src = new[] { t.File("src/b.jpg"), t.File("src/a.png"), t.Dir("src/資料夾") };
        foreach (var n in new[] { "001.jpg", "002.jpg", "005.png", "readme.txt" }) t.File("dest/" + n);
        var plan = NamePlanner.Plan(src, Path.Combine(t.Root, "dest"), new(NamingMode.Number));
        Assert.Equal(["006.png", "007.jpg", "008"], plan.Select(p => p.TargetName));
    }

    [Fact]
    public void Number_ByName_UsesNaturalOrder()
    {
        using var t = new TempDir();
        var src = new[] { t.File("src/IMG_10.jpg"), t.File("src/IMG_2.jpg"), t.File("src/IMG_1.jpg") };
        var plan = NamePlanner.Plan(src, t.Dir("dest"), new(NamingMode.Number, NumberOrder.Name, 2));
        Assert.Equal(["IMG_1.jpg", "IMG_2.jpg", "IMG_10.jpg"], plan.Select(p => p.OriginalName));
        Assert.Equal(["01.jpg", "02.jpg", "03.jpg"], plan.Select(p => p.TargetName));
    }

    [Fact]
    public void Number_ByModified_OldestFirst()
    {
        using var t = new TempDir();
        var src = new[]
        {
            t.File("src/a.jpg", time: new DateTime(2026, 11, 20)),
            t.File("src/b.jpg", time: new DateTime(2026, 11, 18)),
            t.File("src/c.jpg", time: new DateTime(2026, 11, 19)),
        };
        var plan = NamePlanner.Plan(src, t.Dir("dest"), new(NamingMode.Number, NumberOrder.Modified));
        Assert.Equal(["b.jpg", "c.jpg", "a.jpg"], plan.Select(p => p.OriginalName));
        Assert.Equal(["001.jpg", "002.jpg", "003.jpg"], plan.Select(p => p.TargetName));
    }

    [Fact]
    public void Number_StartsAfterHighestNumber_OfAnyType()
    {
        using var t = new TempDir();
        t.File("dest/001.jpg");
        t.File("dest/003");   // 沒有副檔名的檔案 003
        var src = new[] { t.File("src/x.jpg") };
        var plan = NamePlanner.Plan(src, Path.Combine(t.Root, "dest"), new(NamingMode.Number));
        Assert.Equal("004.jpg", plan[0].TargetName);
    }

    [Fact]
    public void SkipReasons_AreDetected()
    {
        using var t = new TempDir();
        var dest = t.Dir("dest");
        var inside = t.File("dest/already.txt");
        var parent = t.Dir("dest/sub");
        var plan = NamePlanner.Plan([inside, Path.Combine(t.Root, "nope.txt"), parent], Path.Combine(dest, "sub", "deeper"), new());
        Assert.Equal(SkipReason.None, plan[0].Skip);
        Assert.Equal(SkipReason.Missing, plan[1].Skip);
        Assert.Equal(SkipReason.IntoItself, plan[2].Skip);

        var plan2 = NamePlanner.Plan([inside], dest, new());
        Assert.Equal(SkipReason.AlreadyThere, plan2[0].Skip);
    }

    [Fact]
    public void Natural_Comparer_Basics()
    {
        var list = new List<string> { "b10", "照片", "b2", "a", "B1" };
        list.Sort(NaturalComparer.Instance);
        Assert.Equal(["a", "B1", "b2", "b10", "照片"], list);
    }
}
