using System.Text.Json;
using System.Text.Json.Serialization;

namespace MoveTidy.Core;

public sealed class Favorite
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    /// <summary>右鍵選單是否也顯示「移到 X\今天日期」。</summary>
    public bool ShowDateEntry { get; set; } = true;
}

public sealed class AppSettings
{
    public List<Favorite> Favorites { get; set; } = [];

    // 快速移動（右鍵直接搬）的預設
    public NamingMode Mode { get; set; } = NamingMode.Keep;
    public NumberOrder Order { get; set; } = NumberOrder.Name;
    public int Digits { get; set; } = 3;
    public bool AutoClose { get; set; } = true;

    /// <summary>今天日期資料夾的名稱格式，只能是 <see cref="DateFormats"/> 其中之一。</summary>
    public string DateFormat { get; set; } = DateFormats[0];

    /// <summary>可選的日期資料夾格式：11-21、1121、11月21日、2026-11-21。</summary>
    public static readonly string[] DateFormats = ["MM-dd", "MMdd", "M月d日", "yyyy-MM-dd"];

    /// <summary>"tw" 繁體、"cn" 簡體。</summary>
    public string Language { get; set; } = "tw";

    // 「移到其他位置…」視窗記住上次的選擇
    public string? PickLastPath { get; set; }
    public bool PickDateFolder { get; set; } = true;
    public NamingMode PickMode { get; set; } = NamingMode.Keep;
    public NumberOrder PickOrder { get; set; } = NumberOrder.Name;

    public PlanOptions QuickOptions => new(Mode, Order, Digits);

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>設定資料夾。可用環境變數 MOVETIDY_DATA 指到別處（測試用）。</summary>
    public static string DataDir =>
        Environment.GetEnvironmentVariable("MOVETIDY_DATA") is { Length: > 0 } d
            ? d
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MoveTidy");

    public static string DefaultFile => System.IO.Path.Combine(DataDir, "settings.json");

    public static AppSettings Load(string? file = null)
    {
        file ??= DefaultFile;
        try
        {
            if (File.Exists(file))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), Json) ?? new();
        }
        catch (JsonException) { }
        return new();
    }

    public void Save(string? file = null)
    {
        file ??= DefaultFile;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, file, overwrite: true);
    }

    /// <summary>今天日期資料夾的名字（格式不認得時用第一種）。</summary>
    public string TodayFolderName(DateTime? now = null) => FormatDate(DateFormat, now);

    public static string FormatDate(string format, DateTime? now = null) =>
        (now ?? DateTime.Now).ToString(DateFormats.Contains(format) ? format : DateFormats[0], System.Globalization.CultureInfo.InvariantCulture);
}
