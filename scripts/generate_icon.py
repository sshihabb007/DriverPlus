import os
import struct
import zlib
import math

def render_rgba(size):
    buf = bytearray(size * size * 4)
    cx = (size - 1) / 2.0
    cy = (size - 1) / 2.0
    rad = size * 0.44
    corner = size * 0.22

    for y in range(size):
        for x in range(size):
            # Rounded squircle / shield mask with anti-aliased edge
            dx = max(abs(x - cx) - (rad - corner), 0.0)
            dy = max(abs(y - cy) - (rad - corner), 0.0)
            dist = math.hypot(dx, dy) - corner
            if dist > 0.8:
                continue

            alpha = 1.0 if dist < -0.6 else max(0.0, min(1.0, (0.8 - dist) / 1.4))

            # Carbon-slate gradient background (#0F172A -> #1E293B) with subtle emerald rim
            t = (x + y) / max(1.0, 2.0 * size)
            r = int(13 + 17 * t)
            g = int(20 + 24 * t)
            b = int(34 + 30 * t)

            # Emerald inner border glow
            if -2.2 <= dist <= -0.4:
                r = int(r * 0.35 + 16 * 0.65)
                g = int(g * 0.35 + 185 * 0.65)
                b = int(b * 0.35 + 129 * 0.65)

            # Normalized coordinates [-1, 1]
            nx = (x - cx) / rad
            ny = (y - cy) / rad

            # Draw bold "D" circuit glyph on left + "+" pulse on right in crisp Emerald (#10B981 -> #34D399)
            in_d_stem = (-0.58 <= nx <= -0.36) and (-0.48 <= ny <= 0.48)
            ring_r = math.hypot((nx + 0.36) * 0.95, ny)
            in_d_arc = (nx >= -0.38) and (0.28 <= ring_r <= 0.49) and (nx <= 0.16)

            # Plus '+' badge on right
            in_plus_h = (0.16 <= nx <= 0.64) and (-0.09 <= ny <= 0.09)
            in_plus_v = (0.31 <= nx <= 0.49) and (-0.24 <= ny <= 0.24)

            if in_d_stem or in_d_arc:
                r, g, b = (248, 250, 252)  # Crisp Ice White 'D'
            elif in_plus_h or in_plus_v:
                r, g, b = (16, 215, 148)   # Vivid Emerald '+'

            idx = (y * size + x) * 4
            buf[idx] = r
            buf[idx + 1] = g
            buf[idx + 2] = b
            buf[idx + 3] = int(alpha * 255)

    return bytes(buf)

def encode_png(size, rgba_bytes):
    raw_rows = bytearray()
    stride = size * 4
    for y in range(size):
        raw_rows.append(0)  # Filter type 0 (None)
        raw_rows.extend(rgba_bytes[y * stride:(y + 1) * stride])

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    idat = zlib.compress(bytes(raw_rows), 9)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr) + chunk(b"IDAT", idat) + chunk(b"IEND", b"")

def build_ico(png_entries):
    count = len(png_entries)
    header = struct.pack("<HHH", 0, 1, count)
    offset = 6 + count * 16
    directory = bytearray()
    payload = bytearray()

    for size, png_data in png_entries:
        w = 0 if size >= 256 else size
        h = 0 if size >= 256 else size
        directory.extend(struct.pack("<BBBBHHII", w, h, 0, 0, 1, 32, len(png_data), offset))
        payload.extend(png_data)
        offset += len(png_data)

    return bytes(header + directory + payload)

def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    assets = os.path.join(root, "assets")
    os.makedirs(assets, exist_ok=True)

    sizes = [16, 24, 32, 48, 64, 128, 256]
    png_entries = []
    png_256 = None
    for s in sizes:
        rgba = render_rgba(s)
        png = encode_png(s, rgba)
        png_entries.append((s, png))
        if s == 256:
            png_256 = png

    ico_path = os.path.join(assets, "driverplus.ico")
    png_path = os.path.join(assets, "driverplus.png")
    with open(ico_path, "wb") as f:
        f.write(build_ico(png_entries))
    with open(png_path, "wb") as f:
        f.write(png_256)
    print("Generated:", ico_path, "and", png_path)

if __name__ == "__main__":
    main()
