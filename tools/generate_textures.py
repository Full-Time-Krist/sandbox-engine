"""Генерирует текстуры-заглушки 16×16: блоки — в src/Client/Assets/Textures/Blocks, картинки предметов — в …/Items,
стадии трещин при ломании — в …/Effects, солнце и луна — в …/Sky.

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
SKY_DIR = TEXTURES_DIR / "Sky"
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


def crafting_table_top(rng):
    px = planks(rng)
    for i in range(SIZE):  # рамка и сетка 3 × 3 — место для узора ремесла
        for j in (0, 5, 10, 15):
            px[i][j] = (92, 64, 34)
            px[j][i] = (92, 64, 34)
    return px


def crafting_table_side(rng):
    px = planks(rng)
    for x in range(SIZE):  # тёмная столешница сверху
        px[0][x] = px[1][x] = (110, 78, 42)
    for y in range(4, 13):  # пила и молоток на боку
        px[y][4] = (150, 150, 155)
        px[y][5] = (120, 120, 126)
    for x in range(9, 14):
        px[5][x] = (130, 130, 136)
    for y in range(6, 13):
        px[y][11] = (96, 64, 34)
    return px


def furnace_side(rng):
    px = noisy(rng, (112, 112, 116), 12)
    for y in range(SIZE):  # кладка: швы между камнями вразбежку
        for x in range(SIZE):
            if y % 4 == 3 or (x + (4 if (y // 4) % 2 else 0)) % 8 == 7:
                px[y][x] = (70, 70, 74)
    for y in range(8, 14):  # топка с огнём внизу
        for x in range(4, 12):
            px[y][x] = (28, 24, 22) if y < 11 else (clamp(220 + rng.randint(-20, 20)), clamp(110 + rng.randint(-30, 30)), 30)
    return px


def furnace_top(rng):
    px = noisy(rng, (104, 104, 108), 14)
    for x in range(SIZE):
        px[0][x] = px[SIZE - 1][x] = (70, 70, 74)
        px[x][0] = px[x][SIZE - 1] = (70, 70, 74)
    return px


HANDLE, HANDLE_LIGHT = (96, 64, 34), (130, 92, 52)  # дерево: рукояти инструментов, палка, факел
FLAME = ((255, 244, 180), (255, 200, 70), (236, 128, 36))  # сверху вниз: почти белое, жёлтое, оранжевое


def torch_block(rng):
    # Факел в мире — столбик из столбцов 7–8 и строк 6–15 (верх — строки 7–8). Строки одноцветные во всю ширину:
    # мип-уровни усредняют соседние пиксели, и тонкий столбик вдали не смешается с пустым фоном.
    px = []
    for y in range(SIZE):
        if 6 <= y <= 8:
            color = FLAME[y - 6]
        else:
            color = HANDLE_LIGHT if y % 3 == 0 else HANDLE
        px.append([color] * SIZE)
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
    "crafting_table_top": crafting_table_top,
    "crafting_table_side": crafting_table_side,
    "furnace_side": furnace_side,
    "furnace_top": furnace_top,
    "torch": torch_block,
}


# --- Картинки предметов: инструменты. Рукоять по диагонали снизу слева, навершие вверху справа. ---

MATERIALS = {  # навершие: основной цвет и блик
    "wooden": ((150, 108, 60), (190, 146, 88)),
    "stone": ((120, 120, 124), (165, 165, 170)),
    "copper": ((196, 110, 58), (236, 160, 98)),
}


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


def stick():
    px = blank()
    handle(px, 12)
    return px


def copper_ingot():
    base, light = MATERIALS["copper"]
    dark = (150, 78, 40)
    px = blank()
    for y in range(6, 11):  # брусок в перспективе: светлый верх, тёмный низ
        for x in range(2 + (10 - y) // 2, 14 - (y - 6) // 2):
            px[y][x] = light if y == 6 else (dark if y == 10 else base)
    return px


ITEMS = {f"{material}_{kind}": (lambda kind=kind, material=material: tool(kind, material))
         for material in MATERIALS for kind in ("pickaxe", "axe", "shovel")}
ITEMS["stick"] = stick
ITEMS["copper_ingot"] = copper_ingot


def torch_item():
    px = blank()
    for y in range(6, 15):  # палка по центру
        px[y][7] = HANDLE_LIGHT
        px[y][8] = HANDLE
    for y, xs in ((2, (7, 8)), (3, (6, 7, 8, 9)), (4, (6, 7, 8, 9)), (5, (7, 8))):  # пламя: светлая сердцевина
        for x in xs:
            px[y][x] = FLAME[0] if y >= 4 and x in (7, 8) else (FLAME[1] if y <= 3 or x in (7, 8) else FLAME[2])
    return px


def charcoal():
    base, light, dark = (52, 48, 46), (84, 78, 74), (30, 28, 27)
    px = blank()
    for y in range(4, 13):  # неровный кусок угля: светлый верх и левый край, тёмные низ и правый
        left, right = 3 + abs(8 - y) // 2, 13 - abs(7 - y) // 2
        for x in range(left, right):
            px[y][x] = light if y == 4 or x == left else (dark if y == 12 or x == right - 1 else base)
    for x, y in ((6, 7), (9, 9), (8, 6), (10, 8)):  # блики
        px[y][x] = light
    return px


ITEMS["torch"] = torch_item
ITEMS["charcoal"] = charcoal


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


# --- Небо: солнце и луна — квадраты на небесной сфере (шаг 8.4). Углы прозрачные: так они кажутся круглее. ---

def sky_disc(color, edge, rng, craters=()):
    px = blank()
    for y in range(SIZE):
        for x in range(SIZE):
            dx, dy = x - 7.5, y - 7.5
            r = (dx * dx + dy * dy) ** 0.5
            if r > 8.2:
                continue
            base = edge if r > 6.2 else color
            d = rng.randint(-6, 6)
            px[y][x] = (clamp(base[0] + d), clamp(base[1] + d), clamp(base[2] + d), 255)
    for cx, cy, size in craters:
        for y in range(cy, cy + size):
            for x in range(cx, cx + size):
                r, g, b, a = px[y][x]
                px[y][x] = (clamp(r - 45), clamp(g - 45), clamp(b - 40), a)
    return px


def sun(rng):
    return sky_disc((255, 246, 200), (255, 214, 120), rng)


def moon(rng):
    return sky_disc((214, 220, 232), (176, 184, 200), rng, craters=((4, 5, 3), (9, 3, 2), (8, 9, 3), (4, 11, 2)))


SKY = {"sun": sun, "moon": moon}


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

    SKY_DIR.mkdir(parents=True, exist_ok=True)
    for name, generate in SKY.items():
        write_png(SKY_DIR / f"{name}.png", generate(random.Random(name)))
        print(f"Sky/{name}.png")


if __name__ == "__main__":
    main()
