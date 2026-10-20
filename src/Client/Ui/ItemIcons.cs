using System.Numerics;
using Client.Rendering;
using Engine.Ui;
using Shared.Items;

namespace Client.Ui;

/// <summary>
/// Иконки предметов. Предмет со своей картинкой (<c>icon</c> в JSON) рисуется плоским квадратом из её слоя.
/// Предмет-блок без картинки — изометрическим кубиком прямо из массива текстур блоков: три грани (верх, южная слева,
/// восточная справа) с той же яркостью, что в мире; новый блок сразу получает иконку без своих файлов.
/// Остальные — квадратом «нет текстуры».
/// </summary>
internal static class ItemIcons
{
    /// <summary>Сторона иконки в пикселях интерфейса.</summary>
    public const int Size = 16;

    // Номера граней в BlockFace.All: видны верх, юг (+Z) и восток (+X) — смотрим с юго-востока сверху.
    private const int East = 0;
    private const int Top = 2;
    private const int South = 4;

    /// <summary>Иконка с левым верхним углом в (<paramref name="x"/>, <paramref name="y"/>), сторона — <see cref="Size"/> × масштаб.</summary>
    public static void Draw(UiCanvas canvas, ItemDefinition item, ItemVisuals visuals, float x, float y, int scale)
    {
        var s = (float)Size * scale;
        var origin = new Vector2(x, y);

        var icon = visuals.IconLayer(item.Id);
        if (icon != ItemVisuals.NoIcon || item.Block is not { } block)
        {
            // Своя картинка или, у предмета-не-блока без неё, слой 0 — «нет текстуры».
            canvas.DrawTexturedQuad(origin, origin + new Vector2(s, 0), origin + new Vector2(s, s), origin + new Vector2(0, s),
                Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY, Math.Max(icon, 0), UiColor.White);
            return;
        }

        // Изометрия 2 : 1 в квадрате s × s: у полного куба верх — ромб s × s/2, бока — по s/2 в ширину и s/2 в высоту.
        // Рисуется общий короб формы блока (у полублока — нижняя половина). Углы у куба и полублока кратны s/4 =
        // 4 × масштаб, то есть лежат на целых пикселях.
        var box = visuals.Blocks.Shape(block).Bounds;
        Vector2 P(Vector3 p) => origin + new Vector2((0.5f + 0.5f * (p.X - p.Z)) * s, (0.25f * (p.X + p.Z) + 0.5f * (1 - p.Y)) * s);
        float x0 = box.Min.X, y0 = box.Min.Y, z0 = box.Min.Z, x1 = box.Max.X, y1 = box.Max.Y, z1 = box.Max.Z;

        // UV — как у граней в мире (BlockFace.UvAt): у верха u = x, v = z, дальний северо-западный угол — вверху ромба;
        // у боков v = 1 − y — верх текстуры у верха блока, у полублока бока берут нижнюю половину картинки.
        Face(Top, new(x0, y1, z0), new(x1, y1, z0), new(x1, y1, z1), new(x0, y1, z1));
        Face(South, new(x0, y1, z1), new(x1, y1, z1), new(x1, y0, z1), new(x0, y0, z1));
        Face(East, new(x1, y1, z1), new(x1, y1, z0), new(x1, y0, z0), new(x1, y0, z1));

        void Face(int face, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            var f = BlockFace.All[face];
            canvas.DrawTexturedQuad(P(a), P(b), P(c), P(d), f.UvAt(a), f.UvAt(b), f.UvAt(c), f.UvAt(d),
                visuals.Blocks.GetLayer(block, face), UiColor.White.Shade(f.Brightness));
        }
    }
}
