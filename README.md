# 移動整理（MoveTidy）

Windows 右鍵小工具：把檔案或資料夾搬到別的資料夾時，自動處理重名，還可以順手建一個今天日期的資料夾。

- 右鍵 →「移動整理」→「移到『常用目的地』」：直接搬，右下角小窗顯示進度，完成後自動關閉
- 「移到『常用目的地\今天日期』」：先建 `11-20` 這樣的日期資料夾再放進去（同一天搬好幾批都放進同一個）；名稱格式可在設定裡選 `11-20`、`1120`、`11月20日`、`2026-11-20`
- 「移到其他位置…」：當場選目的地、要不要日期資料夾、命名方式，搬之前先看每個項目會變成什麼名字
- 命名方式：**保留原名**（重名才加 ` (2)`、` (3)`）或 **純數字編號**（`001.jpg`、`002.png`…，目的地已有號碼就接著編，可依檔名或修改時間排序）
- 設定程式管理常用目的地、預設規則、右鍵選單和語言（繁體／簡體）

Windows 11 的右鍵選單要先點「顯示更多選項」，或按住 Shift 再按右鍵，才看得到「移動整理」。

## 安裝

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download)（建置用，裝好後也會一起裝上執行環境）。

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install.ps1
```

程式會裝到 `%LOCALAPPDATA%\Programs\MoveTidy\MoveTidy.exe`，並安裝右鍵選單。再執行一次就是更新，設定會保留。
打開設定：執行上面的 `MoveTidy.exe`，或在右鍵選單點「設定…」。

移除右鍵選單：在設定裡按「移除右鍵選單」，或執行 `MoveTidy.exe --uninstall-menu`。

## 開發

```powershell
dotnet build MoveTidy.sln
dotnet test MoveTidy.sln
```

| 位置 | 內容 |
|---|---|
| `src/MoveTidy.Core` | 命名規則（`NamePlanner`）、實際搬移（`Mover`）、設定檔（`AppSettings`） |
| `src/MoveTidy` | WPF 介面：設定、移動視窗、進度小窗、右鍵選單安裝、多檔合併 |
| `src/MoveTidy/L.cs` | 所有介面文字（繁體、簡體各一份） |
| `tests/MoveTidy.Tests` | 自動測試，只在暫存資料夾裡建檔案 |
| `預覽/外觀預覽.html` | 開工前的三種外觀預覽，選定 B「深色俐落」 |
| `預覽/圖示/`、`預覽/圖示預覽.html` | 圖示設計稿（SVG）；目前用 A。改完執行 `python scripts/make_icon.py a` 重新產生 `app.ico`（需要 Microsoft Edge） |

設定檔在 `%APPDATA%\MoveTidy\settings.json`。測試或試用時可以設環境變數 `MOVETIDY_DATA` 指到別的資料夾，就不會碰到真正的設定。

右鍵選多個項目時，檔案總管會為每個項目各啟動一次程式；第一個啟動的負責收集（等 0.7 秒沒有新項目），其他的把路徑交給它後馬上結束，所以多選也只會跳一個視窗。

## 授權

[MIT](LICENSE)：可以自由使用、修改和分享，保留版權聲明即可。
