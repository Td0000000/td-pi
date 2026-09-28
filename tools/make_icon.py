#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
td-pi 像素风 π 图标生成器(纯 Python,无第三方依赖)。

字形骨架取自 pixelarticons 的 pi 图标(MIT License, https://github.com/halfmage/pixelarticons),
按 2/3 比例重绘到 16x16 网格,并使用应用品牌配色(Catppuccin Mocha)。
输出:
  - src/TdPi.App/Assets/AppIcon.ico   (16/32/48/64/128 DIB + 256 PNG,exe/窗口/托盘共用)
  - src/TdPi.App/Assets/icon-16.png / icon-32.png / icon-48.png / icon-256.png

用法: python tools/make_icon.py
"""

import os
import struct
import zlib

# ---------------- 像素矩阵(16x16) ----------------
# . 透明  B 主色  (描边/高光/暗部由规则自动生成)
GLYPH = [
    "................",
    "................",
    "....BBBBBBBBBB..",
    "....BBBBBBBBBB..",
    "..BB.BB..BB.....",
    "..BB.BB..BB.....",
    "..BB.BB..BB.....",
    ".....BB..BB.....",
    ".....BB..BB.....",
    ".....BB..BB.....",
    ".....BB..BB.....",
    ".....BB..BB.....",
    ".....BB..BBBBB..",
    ".....BB...BBBB..",
    "................",
    "................",
]

# 配色(Catppuccin Mocha,与应用 UI 品牌色一致)
COLORS = {
    "B": (0x7A, 0xA2, 0xF7, 0xFF),  # 蓝(brand #7aa2f7)
    "L": (0xC8, 0xD7, 0xFF, 0xFF),  # 高光(顶边)
    "D": (0x5B, 0x78, 0xDD, 0xFF),  # 暗部(底边)
    "O": (0x18, 0x18, 0x25, 0xFF),  # 描边 #181825
    ".": (0, 0, 0, 0),
}


def bevel(matrix):
    """填充主色并按规则着色:上邻为空 → 高光 L,下邻为空 → 暗部 D。"""
    h, w = len(matrix), len(matrix[0])
    grid = [[COLORS.get(c, COLORS["."]) for c in row] for row in matrix]

    def filled(x, y):
        return 0 <= x < w and 0 <= y < h and matrix[y][x] == "B"

    for y in range(h):
        for x in range(w):
            if matrix[y][x] != "B":
                continue
            if not filled(x, y - 1):
                grid[y][x] = COLORS["L"]
            elif not filled(x, y + 1):
                grid[y][x] = COLORS["D"]
    return grid


def outline(grid, matrix):
    """为字形四周加 1px 描边(4 邻接)。"""
    h, w = len(matrix), len(matrix[0])

    def filled(x, y):
        return 0 <= x < w and 0 <= y < h and matrix[y][x] == "B"

    out = [row[:] for row in grid]
    for y in range(h):
        for x in range(w):
            if matrix[y][x] == "B":
                continue
            if filled(x - 1, y) or filled(x + 1, y) or filled(x, y - 1) or filled(x, y + 1):
                out[y][x] = COLORS["O"]
    return out


def render(matrix):
    return outline(bevel(matrix), matrix)


def upscale(grid, k):
    """最近邻整数放大(保持像素风锐利)。"""
    h, w = len(grid), len(grid[0])
    out = []
    for y in range(h):
        row = []
        for x in range(w):
            row.extend([grid[y][x]] * k)
        out.extend([row] * k)
    return out


def to_rgba(grid):
    h, w = len(grid), len(grid[0])
    data = bytearray()
    for row in grid:
        for px in row:
            data.extend(px)
    return bytes(data), w, h


def write_png(path, rgba, w, h):
    def chunk(typ, data):
        return (struct.pack(">I", len(data)) + typ + data
                + struct.pack(">I", zlib.crc32(typ + data) & 0xFFFFFFFF))

    ihdr = struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)
    raw = b"".join(b"\x00" + rgba[y * w * 4:(y + 1) * w * 4] for y in range(h))
    png = (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr)
           + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))
    with open(path, "wb") as f:
        f.write(png)
    return png


def dib_entry(rgba, w, h):
    """ICO 的 BMP(DIB)条目:BITMAPINFOHEADER + 自底向上 BGRA + AND 掩码。"""
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, w * h * 4, 0, 0, 0, 0)
    xor = bytearray()
    for y in range(h - 1, -1, -1):
        row = rgba[y * w * 4:(y + 1) * w * 4]
        for x in range(w):
            r, g, b, a = row[x * 4:x * 4 + 4]
            xor += bytes((b, g, r, a))
    and_stride = ((w + 31) // 32) * 4
    and_mask = b"\x00" * and_stride * h
    return header + bytes(xor) + and_mask


def write_ico(path, entries):
    """entries: [(size, data_bytes)]  size==256 用 PNG,data 为 PNG 字节;其余为 DIB。"""
    count = len(entries)
    header = struct.pack("<HHH", 0, 1, count)
    offset = 6 + 16 * count
    directory = b""
    body = b""
    for size, data in entries:
        directory += struct.pack("<BBBBHHII",
                                 0 if size >= 256 else size,
                                 0 if size >= 256 else size,
                                 0, 0, 1, 32, len(data), offset)
        body += data
        offset += len(data)
    with open(path, "wb") as f:
        f.write(header + directory + body)


def ascii_preview(grid):
    codes = {}
    for y, row in enumerate(grid):
        line = ""
        for px in row:
            key = px
            if key not in codes:
                if px[3] == 0:
                    codes[key] = "."
                elif px == COLORS["B"]:
                    codes[key] = "B"
                elif px == COLORS["L"]:
                    codes[key] = "L"
                elif px == COLORS["D"]:
                    codes[key] = "D"
                else:
                    codes[key] = "O"
            line += codes[key]
        print(f"{y:2d} {line}")


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    assets = os.path.join(here, "..", "src", "TdPi.App", "Assets")
    os.makedirs(assets, exist_ok=True)

    grid = render(GLYPH)
    print("== 16x16 像素图预览 ==")
    ascii_preview(grid)

    base_rgba, w, h = to_rgba(grid)
    assert (w, h) == (16, 16)

    pngs = {}
    for size in (16, 32, 48, 256):
        k = size // 16
        rgba, sw, sh = to_rgba(upscale(grid, k))
        pngs[size] = write_png(os.path.join(assets, f"icon-{size}.png"), rgba, sw, sh)

    dibs = {s: dib_entry(to_rgba(upscale(grid, s // 16))[0], s, s) for s in (16, 32, 48, 64, 128)}
    entries = [(s, dibs[s]) for s in (16, 32, 48, 64, 128)] + [(256, pngs[256])]
    write_ico(os.path.join(assets, "AppIcon.ico"), entries)

    print(f"完成 → {os.path.abspath(assets)}")
    for name in ("AppIcon.ico", "icon-16.png", "icon-32.png", "icon-48.png", "icon-256.png"):
        p = os.path.join(assets, name)
        print(f"  {name}: {os.path.getsize(p)} bytes")


if __name__ == "__main__":
    main()
