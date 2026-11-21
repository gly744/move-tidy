using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;

namespace MoveTidy;

/// <summary>
/// 介面文字集中在這裡。新增文字時繁體（tw）和簡體（cn）兩邊都要填。
/// XAML 用法：Text="{local:T key}"，切換語言時畫面會自動更新。
/// </summary>
public sealed class L : INotifyPropertyChanged
{
    public static readonly L I = new();

    static readonly Dictionary<string, (string tw, string cn)> S = new()
    {
        ["app"] = ("移動整理", "移动整理"),
        ["menu_to"] = ("移到「{0}」", "移到「{0}」"),
        ["menu_to_date"] = ("移到「{0}\\今天日期」", "移到「{0}\\今天日期」"),
        ["menu_other"] = ("移到其他位置…", "移到其他位置…"),
        ["menu_settings"] = ("設定…", "设置…"),

        ["pick_title"] = ("移動 {0} 個項目", "移动 {0} 个项目"),
        ["dest"] = ("目的地", "目的地"),
        ["browse"] = ("瀏覽…", "浏览…"),
        ["choose_dest"] = ("請選擇目的地", "请选择目的地"),
        ["pick_folder_title"] = ("選擇目的地資料夾", "选择目标文件夹"),
        ["date_folder"] = ("建立今天日期資料夾", "创建今天日期文件夹"),
        ["naming"] = ("命名方式", "命名方式"),
        ["keep"] = ("保留原名", "保留原名"),
        ["keep_d"] = ("重名時自動加號碼：照片.jpg → 照片 (2).jpg", "重名时自动加编号：照片.jpg → 照片 (2).jpg"),
        ["num"] = ("純數字編號", "纯数字编号"),
        ["num_d"] = ("全部改成 001、002…，目的地已有號碼就接著編", "全部改成 001、002…，目的地已有编号就接着编"),
        ["by_name"] = ("依檔名", "按文件名"),
        ["by_time"] = ("依修改時間", "按修改时间"),
        ["after"] = ("移動後的名字", "移动后的名字"),
        ["renamed"] = ("已改名", "已改名"),
        ["skip_there"] = ("已在這裡", "已在这里"),
        ["skip_into"] = ("不能放進自己", "不能放进自己"),
        ["skip_missing"] = ("找不到", "找不到"),
        ["cancel"] = ("取消", "取消"),
        ["move"] = ("移動", "移动"),

        ["moving"] = ("正在移動 {0} 個項目", "正在移动 {0} 个项目"),
        ["done"] = ("移動完成", "移动完成"),
        ["done_fail"] = ("有 {0} 個項目沒搬成功", "有 {0} 个项目没搬成功"),
        ["renamed_n"] = ("{0} 個重名已自動改名", "{0} 个重名已自动改名"),
        ["no_conflict"] = ("沒有重名", "没有重名"),
        ["skipped_n"] = ("{0} 個略過", "{0} 个略过"),
        ["closing"] = ("{0} 秒後自動關閉", "{0} 秒后自动关闭"),
        ["close"] = ("關閉", "关闭"),
        ["open_folder"] = ("打開資料夾", "打开文件夹"),
        ["fav_missing"] = ("找不到這個常用目的地，請到設定裡檢查。", "找不到这个常用目的地，请到设置里检查。"),
        ["error"] = ("出錯了：{0}", "出错了：{0}"),
        ["err_in_use"] = ("檔案正在被其他程式使用，關掉它再試一次。", "文件正在被其他程序使用，关掉它再试一次。"),
        ["err_denied"] = ("沒有權限搬這個項目。", "没有权限移动这个项目。"),
        ["err_disk_full"] = ("目的地的磁碟空間不夠。", "目的地的磁盘空间不够。"),
        ["err_too_long"] = ("路徑太長了。", "路径太长了。"),
        ["err_not_found"] = ("找不到這個項目，可能已經被移走了。", "找不到这个项目，可能已经被移走了。"),

        ["set_title"] = ("移動整理 · 設定", "移动整理 · 设置"),
        ["nav_fav"] = ("常用目的地", "常用目的地"),
        ["nav_naming"] = ("命名規則", "命名规则"),
        ["nav_menu"] = ("右鍵選單", "右键菜单"),
        ["nav_lang"] = ("語言", "语言"),
        ["fav_hint"] = ("這些資料夾會出現在右鍵選單裡，名字可以直接改。", "这些文件夹会出现在右键菜单里，名字可以直接改。"),
        ["fav_empty"] = ("還沒有常用目的地，按「新增…」加一個。", "还没有常用目的地，按「新增…」加一个。"),
        ["show_date"] = ("也顯示今天日期版本", "也显示今天日期版本"),
        ["add"] = ("新增…", "新增…"),
        ["up"] = ("上移", "上移"),
        ["down"] = ("下移", "下移"),
        ["remove"] = ("移除", "移除"),
        ["quick_def"] = ("快速移動的預設", "快速移动的默认设置"),
        ["quick_def_d"] = ("從右鍵選單直接移到常用目的地時，用這裡的規則。", "从右键菜单直接移到常用目的地时，用这里的规则。"),
        ["order"] = ("編號順序", "编号顺序"),
        ["digits"] = ("編號位數", "编号位数"),
        ["auto_close"] = ("完成後自動關閉", "完成后自动关闭"),
        ["date_fmt"] = ("日期資料夾名稱", "日期文件夹名称"),
        ["date_fmt_d"] = ("右鍵「移到…\\今天日期」和「移到其他位置…」建的日期資料夾用這個名字。同一天搬好幾批，都會放進同一個資料夾。",
                          "右键「移到…\\今天日期」和「移到其他位置…」建的日期文件夹用这个名字。同一天搬好几批，都会放进同一个文件夹。"),
        ["menu_on"] = ("右鍵選單已安裝", "右键菜单已安装"),
        ["menu_off"] = ("右鍵選單還沒安裝", "右键菜单还没安装"),
        ["menu_install"] = ("安裝右鍵選單", "安装右键菜单"),
        ["menu_reinstall"] = ("重新安裝", "重新安装"),
        ["menu_uninstall"] = ("移除右鍵選單", "移除右键菜单"),
        ["menu_note"] = ("Windows 11 要先點「顯示更多選項」，或按住 Shift 再按右鍵，就會看到「移動整理」。常用目的地有變動時，選單會自動更新。",
                         "Windows 11 要先点「显示更多选项」，或按住 Shift 再按右键，就会看到「移动整理」。常用目的地有变动时，菜单会自动更新。"),
        ["menu_exe"] = ("選單使用的程式：{0}", "菜单使用的程序：{0}"),
        ["menu_moved"] = ("選單指向的程式位置和現在這個不同，按「重新安裝」改成現在這個。", "菜单指向的程序位置和现在这个不同，按「重新安装」改成现在这个。"),
        ["lang_d"] = ("介面和右鍵選單都會換成這個語言。", "界面和右键菜单都会换成这个语言。"),
        ["tw"] = ("繁體中文", "繁體中文"),
        ["cn"] = ("简体中文", "简体中文"),
    };

    string _lang = "tw";

    public string Lang
    {
        get => _lang;
        set
        {
            _lang = value == "cn" ? "cn" : "tw";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Lang)));
            LanguageChanged?.Invoke();
        }
    }

    public string this[string key] =>
        S.TryGetValue(key, out var v) ? (_lang == "cn" ? v.cn : v.tw) : $"[{key}]";

    public static string Get(string key) => I[key];
    public static string F(string key, params object[] args) => string.Format(I[key], args);

    /// <summary>檢查每個字串兩種語言都有填（測試用）。</summary>
    public static IEnumerable<string> MissingKeys() =>
        S.Where(kv => string.IsNullOrWhiteSpace(kv.Value.tw) || string.IsNullOrWhiteSpace(kv.Value.cn)).Select(kv => kv.Key);

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? LanguageChanged;
}

[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider sp) =>
        new Binding($"[{Key}]") { Source = L.I, Mode = BindingMode.OneWay }.ProvideValue(sp);
}
