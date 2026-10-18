using System.Collections.Immutable;
using System.Numerics;
using Client.Rendering;
using Engine.Graphics;
using Engine.Ui;
using Shared;
using Shared.Blocks;
using Shared.Items;
using Shared.Protocol;
using Silk.NET.Maths;
using Silk.NET.OpenGL;

namespace Client.Ui;

/// <summary>Экран инвентаря: 27 основных слотов, хотбар, творческая палитра, список рецептов и подсказки.</summary>
internal sealed class InventoryScreen : IDisposable
{
    private const int SlotSize = 20;
    private const int Columns = Inventory.HotbarSize;

    /// <summary>Рецепты — сеткой в 4 столбца справа от инвентаря; всё вместе (296 × 198 с тремя строками палитры) помещается в 320 × 240.</summary>
    public const int RecipeColumns = 4;

    /// <summary>Отступ сетки от края панели и зазор между панелями инвентаря и рецептов, в пикселях интерфейса.</summary>
    private const int Padding = 8;
    private const int PanelGap = 4;

    /// <summary>От верха панели до сетки: заголовок и место под ним.</summary>
    private const int HeaderHeight = 25;

    /// <summary>От сетки инвентаря до первой строки палитры: зазор и её заголовок.</summary>
    private const int PaletteHeader = 20;

    /// <summary>Отступ под последней строкой сетки до низа панели.</summary>
    private const int Footer = 13;

    /// <summary>Строка многострочной подсказки: прописная 7 и зазор 3.</summary>
    private const int LineHeight = 10;

    private static readonly UiColor Panel = new(18, 18, 22, 238);
    private static readonly UiColor SlotFill = new(0, 0, 0, 150);
    private static readonly UiColor Border = new(180, 180, 180, 180);
    private static readonly UiColor HoverBorder = new(255, 255, 255, 255);

    /// <summary>Затемнение рецепта, который сейчас не сделать: поверх иконки.</summary>
    private static readonly UiColor Unavailable = new(18, 18, 22, 170);
    private static readonly UiColor Enough = new(130, 230, 130);
    private static readonly UiColor Missing = new(240, 120, 110);

    private readonly GL _gl;
    private readonly SpriteBatch _batch;
    private readonly TrueTypeFont _font;
    private readonly TextureArray _textures;
    private readonly ItemVisuals _visuals;
    private readonly ItemRegistry _items;
    private readonly BlockRegistry _blocks;
    private readonly RecipeRegistry _recipes;
    private readonly ItemNames _names;

    /// <summary>Предметы творческой палитры: все, кроме воздуха, по порядку id.</summary>
    private readonly ItemDefinition[] _palette;
    private UiAtlas _atlas = null!;
    private Texture2D? _atlasTexture;
    private UiCanvas _canvas = null!;
    private int _scale;
    private bool _dragging;
    private bool _dragOnePerSlot;
    private bool _dragStartedWithCursor;
    private int _dragStartSlot = -1;
    private readonly List<int> _dragTargets = [];

    /// <summary>Рецепты в том порядке и с той доступностью, что нарисованы в последнем кадре: клик попадает в увиденное.</summary>
    private List<RecipeEntry> _entries = [];

    /// <summary>Прокрутка списка рецептов, в строках.</summary>
    private int _recipeScroll;
    private float _scrollRemainder;

    private Inventory? _lastInventory;

    public InventoryScreen(GL gl, string fontPath, Vector2D<int> screen, TextureArray textures,
        ItemVisuals visuals, ItemRegistry items, BlockRegistry blocks, RecipeRegistry recipes, ItemNames names)
    {
        _gl = gl;
        _textures = textures;
        _visuals = visuals;
        _items = items;
        _blocks = blocks;
        _recipes = recipes;
        _names = names;
        _palette = [.. items.All.Where(item => !item.Id.IsAir)];
        _font = TrueTypeFont.Load(fontPath);
        _batch = new SpriteBatch(gl);
        Rebuild(UiScale.For(Math.Max(1, screen.X), Math.Max(1, screen.Y)));
    }

    /// <param name="isStationNear">Стоит ли блок-станок рядом с игроком (<see cref="Crafting.IsStationNear"/>).</param>
    public void Draw(Vector2D<int> screen, Inventory inventory, bool creative, Vector2 mouse, Func<BlockId, bool> isStationNear)
    {
        if (screen.X <= 0 || screen.Y <= 0)
            return;
        _lastInventory = inventory;
        var scale = UiScale.For(screen.X, screen.Y);
        if (scale != _scale)
            Rebuild(scale);

        _canvas.Clear();
        _canvas.FillRect(0, 0, screen.X, screen.Y, new UiColor(0, 0, 0, 105));
        var paletteRows = PaletteRows(creative);
        var grid = GridBounds(screen, scale, paletteRows);
        var panel = InventoryPanel(screen, scale, paletteRows);
        _canvas.FillRect(panel.X, panel.Y, panel.Width, panel.Height, Panel);
        _canvas.DrawFrame(panel.X, panel.Y, panel.Width, panel.Height, scale, Border);
        Text("ИНВЕНТАРЬ", panel.X + 5 * scale, panel.Y + 5 * scale, UiColor.White, scale);

        for (var row = 0; row < 4; row++)
        for (var col = 0; col < Columns; col++)
        {
            var slot = new PixelRect(grid.X + col * SlotSize * scale, grid.Y + row * SlotSize * scale,
                SlotSize * scale, SlotSize * scale);
            var index = row == 3 ? col : Inventory.HotbarSize + (row * Columns) + col;
            DrawSlot(slot, inventory[index], slot.Contains(mouse), scale);
            if (index == inventory.SelectedSlot && row == 3)
                _canvas.DrawFrame(slot.X, slot.Y, slot.Width, slot.Height, 2 * scale, HoverBorder);
        }

        if (creative)
        {
            var paletteY = grid.Bottom + PaletteHeader * scale;
            Text("ТВОРЧЕСКАЯ ПАЛИТРА", grid.X, paletteY - 11 * scale, UiColor.White, scale);
            for (var i = 0; i < _palette.Length; i++)
            {
                var slot = new PixelRect(grid.X + i % Columns * SlotSize * scale, paletteY + i / Columns * SlotSize * scale,
                    SlotSize * scale, SlotSize * scale);
                DrawSlot(slot, new ItemStack(_palette[i].Id, 1), slot.Contains(mouse), scale);
            }
        }

        _entries = RecipeList.Build(_recipes, inventory, isStationNear);
        DrawRecipes(screen, scale, paletteRows, mouse);

        if (HitRecipe(mouse, screen, scale, paletteRows) is { } recipe)
            DrawTooltip(RecipeTooltip(recipe, inventory), mouse, screen, scale);
        else if (FindHovered(screen, scale, creative, mouse) is { } item)
            DrawTooltip([(_names.For(item), UiColor.White)], mouse, screen, scale);

        // Предмет, взятый мышью, следует за курсором и остаётся видимым при движении по экрану.
        if (!inventory.Cursor.IsEmpty)
        {
            var x = (int)mouse.X + 2 * scale;
            var y = (int)mouse.Y + 2 * scale;
            ItemIcons.Draw(_canvas, _items.Get(inventory.Cursor.Item), _visuals, x, y, scale);
            if (inventory.Cursor.Count > 1)
                Text(inventory.Cursor.Count.ToString(), x + 8 * scale, y + 10 * scale, UiColor.White, scale);
        }

        _batch.Draw(_canvas, screen, _atlasTexture!, _textures);
    }

    /// <summary>
    /// Обрабатывает нажатие, удержание и отпускание мыши. Нажатие на стопку берёт её обычным кликом;
    /// последующие слоты, пересечённые во время удержания, получают распределение при отпускании.
    /// Клик по доступному рецепту просит сервер сделать предмет (с Shift — сколько хватит).
    /// </summary>
    public bool HandlePointer(Vector2 mouse, Vector2D<int> screen, bool primaryPressed, bool secondaryPressed,
        bool primaryDown, bool secondaryDown, bool shift, Inventory inventory, bool creative, Action<ClientMessage> send)
    {
        var scale = UiScale.For(Math.Max(1, screen.X), Math.Max(1, screen.Y));
        var paletteRows = PaletteRows(creative);

        if (_dragging)
        {
            if (primaryDown || secondaryDown)
            {
                AddDragTarget(HitSlot(mouse, screen, scale, paletteRows));
                return true;
            }

            // Учесть последний слот, если мышь отпустили сразу после входа в него.
            AddDragTarget(HitSlot(mouse, screen, scale, paletteRows));
            if (_dragTargets.Count > 0)
            {
                send(new InventoryDragDistribute(ImmutableArray.CreateRange(_dragTargets), _dragOnePerSlot));
            }
            else if (_dragStartedWithCursor && _dragStartSlot >= 0)
            {
                // Обычный клик предметом на курсоре, без движения мыши.
                send(new InventoryClick(_dragStartSlot,
                    _dragOnePerSlot ? InventoryClickKind.Secondary : InventoryClickKind.Primary));
            }

            ResetDrag();
            return true;
        }

        if (!primaryPressed && !secondaryPressed)
            return false;

        if (primaryPressed && HitRecipe(mouse, screen, scale, paletteRows) is { } recipe)
        {
            // Недоступный сервер всё равно не сделал бы: не шлём лишнего.
            if (recipe.Available)
                send(new CraftRecipe(recipe.Recipe.Name, shift));
            return true;
        }

        var slot = HitSlot(mouse, screen, scale, paletteRows);
        if (slot is null)
        {
            if (creative && primaryPressed && PaletteHit(mouse, screen, scale) is { } item)
            {
                send(new CreativeGiveItem(item.Id));
                return true;
            }
            return false;
        }

        var onePerSlot = secondaryPressed;
        if (shift && !onePerSlot)
        {
            send(new InventoryClick(slot.Value, InventoryClickKind.QuickMove));
            return true;
        }

        _dragging = true;
        _dragOnePerSlot = onePerSlot;
        _dragStartedWithCursor = !inventory.Cursor.IsEmpty;
        _dragStartSlot = slot.Value;
        _dragTargets.Clear();

        if (!_dragStartedWithCursor)
        {
            // Сохраняем привычный клик: сервер поднимает стопку (или её половину) первым сообщением.
            send(new InventoryClick(slot.Value, onePerSlot
                ? InventoryClickKind.Secondary
                : InventoryClickKind.Primary));
        }

        return true;
    }

    /// <summary>Колесо мыши над списком рецептов прокручивает его по строкам; от себя — вверх.</summary>
    public void Scroll(Vector2 mouse, Vector2D<int> screen, bool creative, float delta)
    {
        var scale = UiScale.For(Math.Max(1, screen.X), Math.Max(1, screen.Y));
        var paletteRows = PaletteRows(creative);
        if (!RecipePanel(screen, scale, paletteRows).Contains(mouse))
            return;
        _scrollRemainder += delta;
        var steps = (int)_scrollRemainder;
        _scrollRemainder -= steps;
        _recipeScroll = Math.Clamp(_recipeScroll - steps, 0, MaxRecipeScroll(_entries.Count, screen, scale, paletteRows));
    }

    /// <summary>
    /// Закрытие экрана. Незаконченное перетаскивание отменяется: иначе при следующем открытии первый кадр
    /// без нажатой кнопки «отпустил» бы его и отправил клик по слоту, с которого оно началось.
    /// </summary>
    public void Close() => ResetDrag();

    /// <summary>Строк творческой палитры: все предметы по 9 в строке; вне творческого режима палитры нет.</summary>
    private int PaletteRows(bool creative) => creative ? (_palette.Length + Columns - 1) / Columns : 0;

    /// <summary>
    /// Панель инвентаря: заголовок, сетка 9 × 4, в творческом режиме — палитра в <paramref name="paletteRows"/> строк.
    /// Вместе с панелью рецептов справа стоит по центру экрана.
    /// </summary>
    public static PixelRect InventoryPanel(Vector2D<int> screen, int scale, int paletteRows)
    {
        var width = Columns * SlotSize + 2 * Padding;
        var height = HeaderHeight + 4 * SlotSize + (paletteRows > 0 ? PaletteHeader + paletteRows * SlotSize : 0) + Footer;
        var total = width + PanelGap + RecipePanelWidth;
        return new PixelRect((screen.X - total * scale) / 2, (screen.Y - height * scale) / 2, width * scale, height * scale);
    }

    /// <summary>Сетка слотов инвентаря (9 × 4) внутри панели.</summary>
    public static PixelRect GridBounds(Vector2D<int> screen, int scale, int paletteRows)
    {
        var panel = InventoryPanel(screen, scale, paletteRows);
        return new PixelRect(panel.X + Padding * scale, panel.Y + HeaderHeight * scale, Columns * SlotSize * scale, 4 * SlotSize * scale);
    }

    private const int RecipePanelWidth = RecipeColumns * SlotSize + 2 * Padding;

    /// <summary>Панель рецептов справа от панели инвентаря, той же высоты.</summary>
    public static PixelRect RecipePanel(Vector2D<int> screen, int scale, int paletteRows)
    {
        var inventory = InventoryPanel(screen, scale, paletteRows);
        return new PixelRect(inventory.Right + PanelGap * scale, inventory.Y, RecipePanelWidth * scale, inventory.Height);
    }

    /// <summary>Сколько строк рецептов видно: сетка начинается на уровне сетки инвентаря и не заходит на нижний отступ.</summary>
    public static int VisibleRecipeRows(Vector2D<int> screen, int scale, int paletteRows)
    {
        var panel = RecipePanel(screen, scale, paletteRows);
        var top = GridBounds(screen, scale, paletteRows).Y;
        return (panel.Bottom - Padding * scale - top) / (SlotSize * scale);
    }

    /// <summary>Ячейка рецепта номер <paramref name="visibleIndex"/> среди видимых (0 — левая верхняя).</summary>
    public static PixelRect RecipeCell(Vector2D<int> screen, int scale, int paletteRows, int visibleIndex)
    {
        var panel = RecipePanel(screen, scale, paletteRows);
        var top = GridBounds(screen, scale, paletteRows).Y;
        return new PixelRect(panel.X + Padding * scale + visibleIndex % RecipeColumns * SlotSize * scale,
            top + visibleIndex / RecipeColumns * SlotSize * scale, SlotSize * scale, SlotSize * scale);
    }

    public static int MaxRecipeScroll(int recipeCount, Vector2D<int> screen, int scale, int paletteRows) =>
        Math.Max(0, (recipeCount + RecipeColumns - 1) / RecipeColumns - VisibleRecipeRows(screen, scale, paletteRows));

    private void DrawRecipes(Vector2D<int> screen, int scale, int paletteRows, Vector2 mouse)
    {
        var panel = RecipePanel(screen, scale, paletteRows);
        _canvas.FillRect(panel.X, panel.Y, panel.Width, panel.Height, Panel);
        _canvas.DrawFrame(panel.X, panel.Y, panel.Width, panel.Height, scale, Border);
        Text("РЕМЕСЛО", panel.X + 5 * scale, panel.Y + 5 * scale, UiColor.White, scale);

        var rows = VisibleRecipeRows(screen, scale, paletteRows);
        var maxScroll = MaxRecipeScroll(_entries.Count, screen, scale, paletteRows);
        // Рецептов могло стать меньше (окно выросло — строк больше): прокрутка не должна уводить за конец списка.
        _recipeScroll = Math.Min(_recipeScroll, maxScroll);
        var first = _recipeScroll * RecipeColumns;
        for (var i = 0; i < rows * RecipeColumns && first + i < _entries.Count; i++)
        {
            var entry = _entries[first + i];
            var cell = RecipeCell(screen, scale, paletteRows, i);
            DrawSlot(cell, entry.Recipe.Result, cell.Contains(mouse), scale);
            if (!entry.Available)
                _canvas.FillRect(cell.X + scale, cell.Y + scale, cell.Width - 2 * scale, cell.Height - 2 * scale, Unavailable);
        }

        if (maxScroll > 0)
        {
            // Полоса прокрутки в правом отступе: длина бегунка — доля видимых строк, положение — прокрутка.
            var top = GridBounds(screen, scale, paletteRows).Y;
            var track = new PixelRect(panel.Right - 5 * scale, top, 2 * scale, rows * SlotSize * scale);
            var totalRows = rows + maxScroll;
            var thumbHeight = Math.Max(scale, track.Height * rows / totalRows);
            var thumbY = track.Y + (track.Height - thumbHeight) * _recipeScroll / maxScroll;
            _canvas.FillRect(track.X, track.Y, track.Width, track.Height, SlotFill);
            _canvas.FillRect(track.X, thumbY, track.Width, thumbHeight, Border);
        }
    }

    /// <summary>Подсказка рецепта: что получится, сколько каждого ингредиента есть из нужного и нужен ли станок.</summary>
    private List<(string Text, UiColor Color)> RecipeTooltip(RecipeEntry entry, Inventory inventory)
    {
        var result = entry.Recipe.Result;
        var title = _names.For(_items.Get(result.Item));
        var lines = new List<(string, UiColor)> { (result.Count > 1 ? $"{title} ×{result.Count}" : title, UiColor.White) };
        foreach (var ingredient in entry.Recipe.Ingredients)
        {
            var have = inventory.Count(ingredient.Item);
            lines.Add(($"{_names.For(_items.Get(ingredient.Item))} {have}/{ingredient.Count}",
                have >= ingredient.Count ? Enough : Missing));
        }
        if (entry.Recipe.Station is { } station)
        {
            // Станок — блок; его название — у одноимённого предмета.
            var item = _items.ForBlock(station);
            var name = item.IsAir ? _blocks.Get(station).Name : _names.For(_items.Get(item));
            lines.Add(($"У станка: {name}", entry.StationNear ? Enough : Missing));
        }
        return lines;
    }

    private void DrawTooltip(IReadOnlyList<(string Text, UiColor Color)> lines, Vector2 mouse, Vector2D<int> screen, int scale)
    {
        var width = lines.Max(line => _canvas.MeasureText(line.Text)) + 8 * scale;
        var height = (lines.Count * LineHeight + 6) * scale;
        var x = Math.Clamp((int)mouse.X + 12 * scale, 0, Math.Max(0, screen.X - width));
        var y = Math.Clamp((int)mouse.Y + 12 * scale, 0, Math.Max(0, screen.Y - height));
        _canvas.FillRect(x, y, width, height, new UiColor(8, 8, 8, 245));
        _canvas.DrawFrame(x, y, width, height, scale, Border);
        for (var i = 0; i < lines.Count; i++)
            Text(lines[i].Text, x + 4 * scale, y + (4 + i * LineHeight) * scale, lines[i].Color, scale);
    }

    private RecipeEntry? HitRecipe(Vector2 mouse, Vector2D<int> screen, int scale, int paletteRows)
    {
        if (!RecipePanel(screen, scale, paletteRows).Contains(mouse))
            return null;
        var first = _recipeScroll * RecipeColumns;
        var visible = VisibleRecipeRows(screen, scale, paletteRows) * RecipeColumns;
        for (var i = 0; i < visible && first + i < _entries.Count; i++)
        {
            if (RecipeCell(screen, scale, paletteRows, i).Contains(mouse))
                return _entries[first + i];
        }
        return null;
    }

    private void AddDragTarget(int? slot)
    {
        if (slot is not { } value || value == _dragStartSlot || _dragTargets.Contains(value))
            return;
        _dragTargets.Add(value);
    }

    private void ResetDrag()
    {
        _dragging = false;
        _dragOnePerSlot = false;
        _dragStartedWithCursor = false;
        _dragStartSlot = -1;
        _dragTargets.Clear();
    }

    private static int? HitSlot(Vector2 mouse, Vector2D<int> screen, int scale, int paletteRows)
    {
        var grid = GridBounds(screen, scale, paletteRows);
        for (var row = 0; row < 4; row++)
        for (var col = 0; col < Columns; col++)
        {
            var slot = new PixelRect(grid.X + col * SlotSize * scale, grid.Y + row * SlotSize * scale,
                SlotSize * scale, SlotSize * scale);
            if (slot.Contains(mouse))
                return row == 3 ? col : Inventory.HotbarSize + row * Columns + col;
        }
        return null;
    }

    /// <summary>Предмет палитры под мышью (только в творческом режиме, где палитра есть).</summary>
    private ItemDefinition? PaletteHit(Vector2 mouse, Vector2D<int> screen, int scale)
    {
        var grid = GridBounds(screen, scale, PaletteRows(creative: true));
        var top = grid.Bottom + PaletteHeader * scale;
        if (mouse.X < grid.X || mouse.X >= grid.Right || mouse.Y < top)
            return null;
        var index = (int)((mouse.Y - top) / (SlotSize * scale)) * Columns + (int)((mouse.X - grid.X) / (SlotSize * scale));
        return index < _palette.Length ? _palette[index] : null;
    }

    private ItemDefinition? FindHovered(Vector2D<int> screen, int scale, bool creative, Vector2 mouse)
    {
        if (HitSlot(mouse, screen, scale, PaletteRows(creative)) is { } slot)
        {
            var stack = _lastInventory![slot];
            return stack.IsEmpty ? null : _items.Get(stack.Item);
        }
        return creative ? PaletteHit(mouse, screen, scale) : null;
    }

    private void DrawSlot(PixelRect slot, ItemStack stack, bool hovered, int scale)
    {
        _canvas.FillRect(slot.X, slot.Y, slot.Width, slot.Height, SlotFill);
        _canvas.DrawFrame(slot.X, slot.Y, slot.Width, slot.Height, scale, hovered ? HoverBorder : Border);
        if (stack.IsEmpty)
            return;
        var (x, y) = (slot.X + 2 * scale, slot.Y + 2 * scale);
        ItemIcons.Draw(_canvas, _items.Get(stack.Item), _visuals, x, y, scale);
        if (stack.Count > 1)
        {
            var count = stack.Count.ToString();
            Text(count, slot.Right - _canvas.MeasureText(count) - 2 * scale,
                slot.Bottom - 8 * scale, UiColor.White, scale);
        }
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
