"""Генерирует текстуры-заглушки блоков 16×16 в src/Client/Assets/Textures/Blocks.

Без зависимостей (PNG пишется вручную через zlib), seed фиксирован: результат всегда одинаковый.
Запуск из корня репозитория: python tools/generate_textures.py
Свою текстуру можно положить вместо любой заглушки, сохранив имя файла.
"""

import random
import struct
import zlib
from pathlib import Path

SIZE = 16
OUT_DIR = Path(__file__).resolve().parent.parent / "src" / "Client" / "Assets" / "Textures" / "Blocks"


def write_png(path: Path, pixels: list[list[tuple[int, int, int]]]) -> None:
    raw = b"".join(b"\x00" + bytes(c for px in row for c in (*px, 255)) for row in pixels)

    def chunk(tag: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", SIZE, SIZE, 8, 6, 0, 0, 0)  # 8 бит, RGBA
    path.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def clamp(v: float) -> int:
    return max(0, min(255, int(v)))


def noisy(rng: random.Random, base: tuple[int, int, int], spread: int) -> list[list[tuple[int, int, int]]]:
    pixels = []
    for _ in range(SIZE):
        row = []
        for _ in range(SIZE):
            d = rng.randint(-spread, spread)
            row.append(tuple(clamp(c + d) for c in base))
        pixels.append(row)
    return pixels


def stone(rng):
    px = noisy(rng, (125, 125, 128), 12)
    for _ in range(10):  # тёмные вкрапления
        x, y = rng.randrange(SIZE), rng.randrange(SIZE)
        px[y][x] = (90, 90, 94)
    return px


def dirt(rng):
    px = noisy(rng, (118, 80, 50), 14)
    for _ in range(8):
        x, y = rng.randrange(SIZE), rng.randrange(SIZE)
        px[y][x] = (90, 60, 36)
    return px


def grass_top(rng):
    return noisy(rng, (92, 160, 64), 16)


def grass_side(rng):
    px = dirt(rng)
    for x in range(SIZE):
        depth = 3 + rng.randint(0, 2)  # неровный край травы
        for y in range(depth):
            d = rng.randint(-14, 14)
            px[y][x] = (clamp(92 + d), clamp(160 + d), clamp(64 + d))
    return px


def sand(rng):
    return noisy(rng, (219, 204, 145), 10)


def log_side(rng):
    px = []
    stripes = [rng.randint(-18, 18) for _ in range(SIZE)]  # вертикальные волокна коры
    for _ in range(SIZE):
        row = []
        for x in range(SIZE):
            d = stripes[x] + rng.randint(-6, 6)
            row.append((clamp(102 + d), clamp(72 + d), clamp(40 + d)))
        px.append(row)
    return px


def log_top(rng):
    px = []
    c = (SIZE - 1) / 2
    for y in range(SIZE):
        row = []
        for x in range(SIZE):
            r = max(abs(x - c), abs(y - c))
            if r > 6.5:  # кора по краю
                d = rng.randint(-10, 10)
                row.append((clamp(102 + d), clamp(72 + d), clamp(40 + d)))
            else:
                ring = 18 if int(r) % 2 == 0 else 0  # годовые кольца
                d = rng.randint(-5, 5) + ring
                row.append((clamp(170 + d), clamp(135 + d), clamp(85 + d)))
        px.append(row)
    return px


def planks(rng):
    px = noisy(rng, (184, 143, 82), 8)
    for y in range(SIZE):
        board = y // 4
        if y % 4 == 3:  # шов между досками
            px[y] = [(120, 88, 48)] * SIZE
            continue
        seam = 5 if board % 2 == 0 else 12  # стыки досок вразбежку
        px[y][seam] = (130, 96, 54)
    return px


TEXTURES = {
    "stone": stone,
    "dirt": dirt,
    "grass_top": grass_top,
    "grass_side": grass_side,
    "sand": sand,
    "log_side": log_side,
    "log_top": log_top,
    "planks": planks,
}


def main() -> None:
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    for name, generate in TEXTURES.items():
        rng = random.Random(name)  # свой seed у каждой текстуры: правка одной не меняет остальные
        write_png(OUT_DIR / f"{name}.png", generate(rng))
        print(f"{name}.png")


if __name__ == "__main__":
    main()
