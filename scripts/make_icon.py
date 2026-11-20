"""把 SVG 圖示轉成 Windows 用的 .ico（內含多種尺寸）。

用法：python scripts/make_icon.py [設計代號，預設 a]
  - 大尺寸（32 以上）用 預覽/圖示/<代號>.svg
  - 小尺寸（16～24）有 <代號>-small.svg 就用它，畫面比較清楚
輸出：src/MoveTidy/Assets/app.ico，以及預覽用的 PNG 放在 預覽/圖示/png/
只用 Python 內建模組；用本機的 Microsoft Edge（無頭模式）把 SVG 畫成 PNG。
"""
import pathlib
import struct
import subprocess
import sys
import tempfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
ICONS = ROOT / "預覽" / "圖示"
OUT_ICO = ROOT / "src" / "MoveTidy" / "Assets" / "app.ico"
SIZES = [256, 128, 64, 48, 40, 32, 24, 20, 16]
SMALL_MAX = 24
BASE = 256  # 頁面上圖片的 CSS 大小，用縮放倍率產生各尺寸

EDGE_CANDIDATES = [
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
]


def find_edge() -> str:
    for p in EDGE_CANDIDATES:
        if pathlib.Path(p).exists():
            return p
    sys.exit("找不到 Microsoft Edge")


def render(edge: str, svg: pathlib.Path, size: int, tmp: pathlib.Path) -> bytes:
    page = tmp / f"r{size}.html"
    page.write_text(
        f'<!doctype html><html><body style="margin:0;background:transparent">'
        f'<img src="{svg.as_uri()}" width="{BASE}" height="{BASE}" style="display:block"></body></html>',
        encoding="utf-8",
    )
    out = tmp / f"r{size}.png"
    subprocess.run(
        [edge, "--headless=new", "--disable-gpu", "--hide-scrollbars",
         "--default-background-color=00000000",
         f"--force-device-scale-factor={size / BASE}",
         f"--window-size={BASE},{BASE}",
         f"--screenshot={out}", page.as_uri()],
        check=True, capture_output=True, timeout=60,
    )
    data = out.read_bytes()
    w, h = struct.unpack(">II", data[16:24])
    if (w, h) != (size, size):
        sys.exit(f"{size}px 輸出尺寸不對：{w}x{h}")
    return data


def build_ico(images: list[tuple[int, bytes]]) -> bytes:
    header = struct.pack("<HHH", 0, 1, len(images))
    entries, blobs = b"", b""
    offset = 6 + 16 * len(images)
    for size, png in images:
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(png), offset)
        blobs += png
        offset += len(png)
    return header + entries + blobs


def main() -> None:
    design = sys.argv[1] if len(sys.argv) > 1 else "a"
    big = ICONS / f"{design}.svg"
    small = ICONS / f"{design}-small.svg"
    if not small.exists():
        small = big
    edge = find_edge()
    png_dir = ICONS / "png"
    png_dir.mkdir(exist_ok=True)
    images = []
    with tempfile.TemporaryDirectory() as t:
        tmp = pathlib.Path(t)
        for s in SIZES:
            data = render(edge, small if s <= SMALL_MAX else big, s, tmp)
            (png_dir / f"{design}-{s}.png").write_bytes(data)
            images.append((s, data))
    OUT_ICO.parent.mkdir(parents=True, exist_ok=True)
    OUT_ICO.write_bytes(build_ico(images))
    print(f"完成：{OUT_ICO}（{len(images)} 種尺寸）")


if __name__ == "__main__":
    main()
