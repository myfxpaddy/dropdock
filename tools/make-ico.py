"""Packs PNG files into a Windows .ico.

Sizes up to 64 px are stored as classic 32-bit bitmaps, which every Windows icon API reads.
Larger sizes are stored as PNG, which Explorer uses for big icons.

Usage: python3 tools/make-ico.py <folder-of-<size>.png> <output.ico>
"""
import os
import struct
import sys
import zlib


def decode_png(data):
    """Returns (width, height, rows of RGBA bytes) for an 8-bit RGBA, non-interlaced PNG."""
    assert data[:8] == b"\x89PNG\r\n\x1a\n"
    pos, idat, width, height = 8, b"", 0, 0
    while pos < len(data):
        length, kind = struct.unpack(">I4s", data[pos:pos + 8])
        body = data[pos + 8:pos + 8 + length]
        if kind == b"IHDR":
            width, height, depth, color, _, _, interlace = struct.unpack(">IIBBBBB", body)
            assert depth == 8 and color == 6 and interlace == 0, "expected 8-bit RGBA PNG"
        elif kind == b"IDAT":
            idat += body
        pos += 12 + length
    raw = zlib.decompress(idat)
    stride = width * 4
    rows, prev, i = [], bytearray(stride), 0
    for _ in range(height):
        ftype, line = raw[i], bytearray(raw[i + 1:i + 1 + stride])
        i += 1 + stride
        for x in range(stride):
            a = line[x - 4] if x >= 4 else 0
            b = prev[x]
            c = prev[x - 4] if x >= 4 else 0
            if ftype == 1:
                line[x] = (line[x] + a) & 0xFF
            elif ftype == 2:
                line[x] = (line[x] + b) & 0xFF
            elif ftype == 3:
                line[x] = (line[x] + (a + b) // 2) & 0xFF
            elif ftype == 4:
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                pred = a if pa <= pb and pa <= pc else (b if pb <= pc else c)
                line[x] = (line[x] + pred) & 0xFF
        rows.append(bytes(line))
        prev = line
    return width, height, rows


def bitmap_entry(png):
    w, h, rows = decode_png(png)
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    pixels = bytearray()
    for row in reversed(rows):  # bottom-up, BGRA
        for x in range(w):
            r, g, b, a = row[x * 4:x * 4 + 4]
            pixels += bytes((b, g, r, a))
    mask_stride = ((w + 31) // 32) * 4
    return header + bytes(pixels) + bytes(mask_stride * h)


folder, output = sys.argv[1], sys.argv[2]
sizes = sorted(int(f[:-4]) for f in os.listdir(folder) if f.endswith(".png") and f[:-4].isdigit())
images = []
for s in sizes:
    png = open(os.path.join(folder, f"{s}.png"), "rb").read()
    images.append(png if s > 64 else bitmap_entry(png))

header = struct.pack("<HHH", 0, 1, len(sizes))
offset = 6 + 16 * len(sizes)
entries = b""
for size, data in zip(sizes, images):
    dim = 0 if size >= 256 else size
    entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
    offset += len(data)

with open(output, "wb") as f:
    f.write(header + entries + b"".join(images))
print(f"wrote {output} with sizes {sizes} (bitmaps up to 64 px, PNG above)")
