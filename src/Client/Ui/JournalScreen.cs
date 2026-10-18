using Client.Rendering;
using Engine.Graphics;
using Engine.Ui;
using Shared.Items;
using Shared.Quests;
using Silk.NET.Maths;
using Silk.NET.OpenGL;

namespace Client.Ui;

/// <summary>
/// Журнал заданий (J): доступные задания со счётом и описанием, под ними выполненные — достижения. Недоступные
/// не показываются, только их число. Только просмотр; колесо прокручивает список.
/// </summary>
internal sealed class JournalScreen : IDisposable
{
    /// <summary>Панель — 304 × 216 пикселей интерфейса: помещается в 320 × 240 с отступом по 8.</summary>
    public const int PanelWidth = 304;
    public const int PanelHeight = 216;

    /// <summary>Строка задания: название и две строки описания по 10 пикселей, зазор 2.</summary>
    public const int RowHeight = 32;

    private const int Padding = 8;
    private const int HeaderHeight = 22;
    private const int LineHeight = 10;
    private const int DescriptionLines = 2;

    private static readonly UiColor Panel = new(18, 18, 22, 238);
    private static readonly UiColor Border = new(180, 180, 180, 180);
    private static readonly UiColor SlotFill = new(0, 0, 0, 150);
    private static readonly UiColor Description = new(185, 185, 185);
    private static readonly UiColor Done = new(130, 230, 130);
    private static readonly UiColor DoneRow = new(40, 70, 40, 90);

    private readonly GL _gl;
    private readonly SpriteBatch _batch;
    private readonly TrueTypeFont _font;
    private readonly TextureArray _textures;
    private readonly ItemVisuals _visuals;
    private readonly ItemRegistry _items;
    private readonly QuestRegistry _quests;
    private UiAtlas _atlas = null!;
    private Texture2D? _atlasTexture;
    private UiCanvas _canvas = null!;
    private int _scale;

    /// <summary>Прокрутка в строках и сколько строк было в последнем кадре (для предела прокрутки).</summary>
    private int _scroll;
    private int _entryCount;
    private float _scrollRemainder;

    public JournalScreen(GL gl, string fontPath, Vector2D<int> screen, TextureArray textures, ItemVisuals visuals,
        ItemRegistry items, QuestRegistry quests)
    {
        _gl = gl;
        _textures = textures;
        _visuals = visuals;
        _items = items;
        _quests = quests;
        _font = TrueTypeFont.Load(fontPath);
        _batch = new SpriteBatch(gl);
        Rebuild(UiScale.For(Math.Max(1, screen.X), Math.Max(1, screen.Y)));
    }

    /// <summary>Панель журнала по центру экрана.</summary>
    public static PixelRect PanelBounds(Vector2D<int> screen, int scale) =>
        new((screen.X - PanelWidth * scale) / 2, (screen.Y - PanelHeight * scale) / 2, PanelWidth * scale, PanelHeight * scale);

    public static int VisibleRows => (PanelHeight - HeaderHeight - Padding) / RowHeight;

    /// <summary>Строка номер <paramref name="visibleIndex"/> среди видимых.</summary>
    public static PixelRect RowBounds(Vector2D<int> screen, int scale, int visibleIndex)
    {
        var panel = PanelBounds(screen, scale);
        return new PixelRect(panel.X + Padding * scale, panel.Y + (HeaderHeight + visibleIndex * RowHeight) * scale,
            (PanelWidth - 2 * Padding) * scale, (RowHeight - 2) * scale);
    }

    public static int MaxScroll(int entries) => Math.Max(0, entries - VisibleRows);

    /// <param name="survival">В творческом задания не идут: журнал говорит об этом, а не делает вид, что они стоят.</param>
    public void Draw(Vector2D<int> screen, QuestBook book, Inventory inventory, bool survival)
    {
        if (screen.X <= 0 || screen.Y <= 0)
            return;
        var scale = UiScale.For(screen.X, screen.Y);
        if (scale != _scale)
            Rebuild(scale);

        var (entries, hidden) = QuestView.Journal(_quests, book);
        _entryCount = entries.Count;
        _scroll = Math.Min(_scroll, MaxScroll(entries.Count));

        _canvas.Clear();
        _canvas.FillRect(0, 0, screen.X, screen.Y, new UiColor(0, 0, 0, 105));
        var panel = PanelBounds(screen, scale);
        _canvas.FillRect(panel.X, panel.Y, panel.Width, panel.Height, Panel);
        _canvas.DrawFrame(panel.X, panel.Y, panel.Width, panel.Height, scale, Border);
        Text("ЖУРНАЛ ЗАДАНИЙ", panel.X + 5 * scale, panel.Y + 5 * scale, UiColor.White, scale);
        var note = !survival ? "в творческом не идут" : hidden > 0 ? $"впереди ещё {hidden}" : "";
        if (note.Length > 0)
            Text(note, panel.Right - _canvas.MeasureText(note) - 5 * scale, panel.Y + 5 * scale, Description, scale);

        for (var i = 0; i < VisibleRows && _scroll + i < entries.Count; i++)
            DrawRow(RowBounds(screen, scale, i), entries[_scroll + i], book, inventory, scale);

        if (entries.Count == 0)
            Text("Заданий нет", panel.X + Padding * scale, panel.Y + HeaderHeight * scale, Description, scale);

        _batch.Draw(_canvas, screen, _atlasTexture!, _textures);
    }

    public void Scroll(float delta)
    {
        _scrollRemainder += delta;
        var steps = (int)_scrollRemainder;
        _scrollRemainder -= steps;
        _scroll = Math.Clamp(_scroll - steps, 0, MaxScroll(_entryCount));
    }

    private void DrawRow(PixelRect row, Quest quest, QuestBook book, Inventory inventory, int scale)
    {
        var completed = book.IsCompleted(quest.Name);
        if (completed)
            _canvas.FillRect(row.X, row.Y, row.Width, row.Height, DoneRow);

        var slot = new PixelRect(row.X, row.Y + 2 * scale, 20 * scale, 20 * scale);
        _canvas.FillRect(slot.X, slot.Y, slot.Width, slot.Height, SlotFill);
        _canvas.DrawFrame(slot.X, slot.Y, slot.Width, slot.Height, scale, completed ? Done : Border);
        var icon = QuestView.Icon(quest, _items);
        if (!icon.IsAir)
            ItemIcons.Draw(_canvas, _items.Get(icon), _visuals, slot.X + 2 * scale, slot.Y + 2 * scale, scale);

        var textX = slot.Right + 4 * scale;
        var textWidth = row.Right - textX;
        // Справа в строке названия — счёт или «готово»; пиксель справа — на тень.
        var status = completed ? "готово" : QuestView.ProgressText(quest, book, inventory);
        Text(quest.Title, textX, row.Y + 2 * scale, completed ? Done : UiColor.White, scale);
        if (status.Length > 0)
            Text(status, row.Right - _canvas.MeasureText(status) - scale, row.Y + 2 * scale, completed ? Done : UiColor.White, scale);

        var lines = QuestView.Wrap(quest.Description, textWidth - scale, DescriptionLines, text => _canvas.MeasureText(text));
        for (var i = 0; i < lines.Count; i++)
            Text(lines[i], textX, row.Y + (2 + (i + 1) * LineHeight) * scale, Description, scale);
    }

    private void Text(string text, int x, int y, UiColor color, int scale)
    {
        _canvas.DrawText(text, x + scale, y + scale, color.Shade(0.25f));
        _canvas.DrawText(text, x, y, color);
    }

    private void Rebuild(int scale)
    {
        _atlas = UiAtlas.Build(_font, Hud.CapHeight * scale, UiAtlas.DefaultCharacters);
        _atlasTexture?.Dispose();
        _atlasTexture = new Texture2D(_gl, _atlas.Image);
        _canvas = new UiCanvas(_atlas);
        _scale = scale;
    }

    public void Dispose()
    {
        _atlasTexture?.Dispose();
        _batch.Dispose();
        _font.Dispose();
    }
}
