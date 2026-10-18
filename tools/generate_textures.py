"""Генерирует текстуры-заглушки 16×16: блоки — в src/Client/Assets/Textures/Blocks, картинки предметов — в …/Items,
стадии трещин при ломании — в …/Effects.

Без зависимостей (PNG пишется вручную через zlib), seed фиксирован: результат всегда одинаковый.
Запуск из корня репозитория: python tools/generate_textures.py
Свою текстуру можно положить вместо любой заглушки, сохранив имя файла.
"""

import random
import struct
import zlib
from pathlib import Path

SIZE = 16
TEXTURES_DIR = Path(__file__).resolve().parent.parent / "src" / "Client" / "Assets" / "Textures"
OUT_DIR = TEXTURES_DIR / "Blocks"
ITEMS_DIR = TEXTURES_DIR / "Items"
EFFECTS_DIR = TEXTURES_DIR / "Effects"
CLEAR = (0, 0, 0, 0)


def write_png(path: Path, pixels: list[list[tuple[int, int, int]]]) -> None:
    # Пиксель — (r, g, b) непрозрачный или (r, g, b, a) с прозрачностью (картинки предметов, трещины).
    raw = b"".join(b"\x00" + bytes(c for px in row for c in (px if len(px) == 4 else (*px, 255))) for row in pixels)

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


def leaves(rng):
    # Листва непрозрачная (как «быстрая графика» Minecraft): пятна светлой и тёмной зелени вместо просветов.
    px = noisy(rng, (58, 112, 42), 18)
    for _ in range(26):
        x, y = rng.randrange(SIZE), rng.randrange(SIZE)
        px[y][x] = (34, 72, 26) if rng.random() < 0.6 else (88, 150, 60)
    return px


def copper_ore(rng):
    px = stone(rng)
    for _ in range(6):  # рыжие вкрапления пятнами 2×2 с неровным краем
        x, y = rng.randrange(1, SIZE - 2), rng.randrange(1, SIZE - 2)
        for dx, dy in ((0, 0), (1, 0), (0, 1), (1, 1)):
            if rng.random() < 0.85:
                r = rng.randint(196, 228)
                px[y + dy][x + dx] = (r, clamp(r * 0.56), clamp(r * 0.3))
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
    "leaves": leaves,
    "copper_ore": copper_ore,
}


# --- Картинки предметов: инструменты. Рукоять по диагонали снизу слева, навершие вверху справа. ---

MATERIALS = {  # навершие: основной цвет и блик
    "wooden": ((150, 108, 60), (190, 146, 88)),
    "stone": ((120, 120, 124), (165, 165, 170)),
    "copper": ((196, 110, 58), (236, 160, 98)),
}
HANDLE, HANDLE_LIGHT = (96, 64, 34), (130, 92, 52)


def blank():
    return [[CLEAR] * SIZE for _ in range(SIZE)]


def handle(px, length):
    for i in range(length):  # от (2, 13) вверх вправо
        x, y = 2 + i, 13 - i
        px[y][x] = HANDLE
        px[y][x + 1] = HANDLE_LIGHT


def tool(kind, material):
    base, light = MATERIALS[material]
    px = blank()
    if kind == "pickaxe":
        handle(px, 10)
        for x, y in ((6, 3), (7, 2), (8, 2), (9, 2), (10, 2), (11, 3), (12, 3), (12, 4), (13, 5), (13, 6), (13, 7), (14, 8), (5, 4), (4, 5)):
            px[y][x] = base
        for x, y in ((8, 3), (9, 3), (10, 3), (11, 4), (12, 5)):
            px[y][x] = light
    elif kind == "axe":
        handle(px, 10)
        for y in range(2, 9):
            for x in range(9, 9 + (4 if 3 <= y <= 7 else 3)):
                px[y][x] = base
        for y in range(3, 8):
            px[y][9] = light
    else:  # shovel
        handle(px, 8)
        for x, y in ((10, 2), (11, 2), (12, 2), (9, 3), (10, 3), (11, 3), (12, 3), (13, 3), (9, 4), (10, 4), (11, 4), (12, 4),
                     (13, 4), (10, 5), (11, 5), (12, 5)):
            px[y][x] = base
        for x, y in ((10, 3), (11, 3), (10, 4)):
            px[y][x] = light
    return px


ITEMS = {f"{material}_{kind}": (lambda kind=kind, material=material: tool(kind, material))
         for material in MATERIALS for kind in ("pickaxe", "axe", "shovel")}


# --- Стадии трещин: 0 — едва заметная, 9 — почти разваленный блок. Тёмные линии с прозрачностью поверх блока. ---

def crack_paths(rng):
    """Ломаные из центра к краям: стадия N рисует первые части каждой ломаной."""
    paths = []
    for angle in range(8):
        x, y = 7.5, 7.5
        dx, dy = [(1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1)][angle]
        path = []
        for _ in range(10):
            x += dx + rng.uniform(-0.6, 0.6)
            y += dy + rng.uniform(-0.6, 0.6)
            path.append((int(x), int(y)))
        paths.append(path)
    return paths


def crack(stage, paths):
    px = blank()
    for path in paths:
        for x, y in path[: stage + 1]:
            if 0 <= x < SIZE and 0 <= y < SIZE:
                px[y][x] = (20, 20, 20, 200)
    return px


def main() -> None:
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    for name, generate in TEXTURES.items():
        rng = random.Random(name)  # свой seed у каждой текстуры: правка одной не меняет остальные
        write_png(OUT_DIR / f"{name}.png", generate(rng))
        print(f"{name}.png")

    ITEMS_DIR.mkdir(parents=True, exist_ok=True)
    for name, generate in ITEMS.items():
        write_png(ITEMS_DIR / f"{name}.png", generate())
        print(f"Items/{name}.png")

    EFFECTS_DIR.mkdir(parents=True, exist_ok=True)
    paths = crack_paths(random.Random("crack"))
    for stage in range(10):
        write_png(EFFECTS_DIR / f"destroy_{stage}.png", crack(stage, paths))
    print("Effects/destroy_0..9.png")


if __name__ == "__main__":
    main()
