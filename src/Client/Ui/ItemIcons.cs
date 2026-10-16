using System.Numerics;
using Client.Rendering;
using Engine.Ui;
using Shared.Items;

namespace Client.Ui;

/// <summary>
/// Иконки предметов: блок рисуется изометрическим кубиком прямо из массива текстур блоков — три грани
/// (верх, южная слева, восточная справа) с той же яркостью, что в мире. Без отдельного рендера в текстуру
/// и без файлов: новый блок сразу получает иконку.
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
    public static void Draw(UiCanvas canvas, ItemDefinition item, BlockVisuals visuals, float x, float y, int scale)
    {
        var s = (float)Size * scale;
        var origin = new Vector2(x, y);

        if (item.Block is not { } block)
        {
            // Своих картинок у предметов пока нет: предмет-не-блок рисуется квадратом «нет текстуры».
            canvas.DrawTexturedQuad(origin, origin + new Vector2(s, 0), origin + new Vector2(s, s), origin + new Vector2(0, s),
                Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY, 0, UiColor.White);
            return;
        }

        // Изометрия 2 : 1 в квадрате s × s: верх — ромб s × s/2, бока — по s/2 в ширину и s/2 в высоту по вертикали.
        // Углы кратны s/4 = 4 × масштаб, то есть лежат на целых пикселях.
        Vector2 P(float fx, float fy) => origin + new Vector2(fx * s, fy * s);
        var top = P(0.5f, 0);
        var right = P(1, 0.25f);
        var center = P(0.5f, 0.5f);
        var left = P(0, 0.25f);
        var bottomLeft = P(0, 0.75f);
        var bottom = P(0.5f, 1);
        var bottomRight = P(1, 0.75f);

        // UV — как у граней в мире (BlockFace): у верха u = x, v = z, дальний северо-западный угол — вверху ромба;
        // у боков v = 0 — верх текстуры у верха блока.
        Face(Top, top, right, center, left);
        Face(South, left, center, bottom, bottomLeft);
        Face(East, center, right, bottomRight, bottom);

        void Face(int face, Vector2 a, Vector2 b, Vector2 c, Vector2 d) =>
            canvas.DrawTexturedQuad(a, b, c, d, Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY,
                visuals.GetLayer(block, face), UiColor.White.Shade(BlockFace.All[face].Brightness));
    }
}
