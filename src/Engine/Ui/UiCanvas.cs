using System.Numerics;
using System.Runtime.InteropServices;

namespace Engine.Ui;

/// <summary>
/// Геометрия интерфейса за кадр: четырёхугольники в пикселях экрана ((0, 0) — левый верхний угол, Y вниз),
/// в порядке рисования — что добавлено позже, то поверх. Без OpenGL: рисует её <see cref="SpriteBatch"/>,
/// а раскладку можно проверять тестами.
/// </summary>
/// <remarks>
/// Каждый четырёхугольник берёт цвет либо из атласа интерфейса (глифы и белый квадрат для заливки), либо из
/// слоя массива текстур (иконки блоков). Обе текстуры подключены одновременно, поэтому весь интерфейс уходит
/// одним вызовом отрисовки и порядок наложения задаётся просто порядком добавления.
/// </remarks>
public sealed class UiCanvas(UiAtlas atlas)
{
    /// <summary>Значение <see cref="UiVertex.Layer"/> для атласа интерфейса.</summary>
    public const float AtlasLayer = -1;

    private readonly List<UiVertex> _vertices = [];
    private readonly List<(AtlasGlyph Glyph, int PenX)> _layout = [];

    public UiAtlas Atlas => atlas;

    /// <summary>Вершины по четыре на четырёхугольник: левый верхний, правый верхний, правый нижний, левый нижний.</summary>
    public ReadOnlySpan<UiVertex> Vertices => CollectionsMarshal.AsSpan(_vertices);

    public int QuadCount => _vertices.Count / 4;

    public void Clear() => _vertices.Clear();

    /// <summary>Сплошной прямоугольник (цвет из белого квадрата атласа).</summary>
    public void FillRect(float x, float y, float width, float height, UiColor color)
    {
        var uv = atlas.WhiteUv;
        AddQuad(new Vector2(x, y), new Vector2(x + width, y), new Vector2(x + width, y + height), new Vector2(x, y + height),
            uv, uv, uv, uv, AtlasLayer, color);
    }

    /// <summary>Рамка толщиной <paramref name="thickness"/> внутрь прямоугольника, из четырёх полос без наложений.</summary>
    public void DrawFrame(float x, float y, float width, float height, float thickness, UiColor color)
    {
        FillRect(x, y, width, thickness, color);
        FillRect(x, y + height - thickness, width, thickness, color);
        FillRect(x, y + thickness, thickness, height - 2 * thickness, color);
        FillRect(x + width - thickness, y + thickness, thickness, height - 2 * thickness, color);
    }

    /// <summary>
    /// Четырёхугольник произвольной формы со слоем массива текстур: углы и их UV по часовой стрелке на экране,
    /// начиная с левого верхнего. Параллелограмм текстура покрывает без искажений (изометрические грани иконок).
    /// </summary>
    public void DrawTexturedQuad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Vector2 uvA, Vector2 uvB, Vector2 uvC, Vector2 uvD,
        int layer, UiColor color) =>
        AddQuad(a, b, c, d, uvA, uvB, uvC, uvD, layer, color);

    /// <summary>
    /// Строка текста; (<paramref name="x"/>, <paramref name="y"/>) — левый верхний угол строки. Глифы встают
    /// на целые пиксели: при растеризации в том же размере каждый тексель атласа ложится ровно в пиксель.
    /// </summary>
    public void DrawText(ReadOnlySpan<char> text, int x, int y, UiColor color)
    {
        _layout.Clear();
        atlas.Layout(text, _layout);
        var baseline = y + atlas.Ascent;
        foreach (var (glyph, penX) in _layout)
        {
            if (glyph.Width == 0 || glyph.Height == 0)
                continue;

            var left = x + penX + glyph.OffsetX;
            var top = baseline + glyph.OffsetY;
            var (uv0, uv1) = (glyph.UvMin, glyph.UvMax);
            AddQuad(new Vector2(left, top), new Vector2(left + glyph.Width, top),
                new Vector2(left + glyph.Width, top + glyph.Height), new Vector2(left, top + glyph.Height),
                uv0, new Vector2(uv1.X, uv0.Y), uv1, new Vector2(uv0.X, uv1.Y), AtlasLayer, color);
        }
    }

    /// <summary>Ширина строки в пикселях.</summary>
    public int MeasureText(ReadOnlySpan<char> text) => atlas.MeasureWidth(text);

    private void AddQuad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Vector2 uvA, Vector2 uvB, Vector2 uvC, Vector2 uvD,
        float layer, UiColor color)
    {
        var packed = color.Packed;
        _vertices.Add(new UiVertex(a, uvA, layer, packed));
        _vertices.Add(new UiVertex(b, uvB, layer, packed));
        _vertices.Add(new UiVertex(c, uvC, layer, packed));
        _vertices.Add(new UiVertex(d, uvD, layer, packed));
    }
}
