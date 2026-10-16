using System.Numerics;
using Client.Rendering;
using Engine.Graphics;
using Engine.Ui;
using Shared;
using Shared.Items;
using Shared.Protocol;
using Silk.NET.Maths;
using Silk.NET.OpenGL;

namespace Client.Ui;

/// <summary>Экран инвентаря: 27 основных слотов, хотбар, творческая палитра и подсказки.</summary>
internal sealed class InventoryScreen : IDisposable
{
    private const int SlotSize = 20;
    private const int Columns = Inventory.HotbarSize;
    private static readonly UiColor Panel = new(18, 18, 22, 238);
    private static readonly UiColor SlotFill = new(0, 0, 0, 150);
    private static readonly UiColor Border = new(180, 180, 180, 180);
    private static readonly UiColor HoverBorder = new(255, 255, 255, 255);

    private readonly GL _gl;
    private readonly SpriteBatch _batch;
    private readonly TrueTypeFont _font;
    private readonly TextureArray _textures;
    private readonly BlockVisuals _visuals;
    private readonly ItemRegistry _items;
    private UiAtlas _atlas = null!;
    private Texture2D? _atlasTexture;
    private UiCanvas _canvas = null!;
    private int _scale;

    public InventoryScreen(GL gl, string fontPath, Vector2D<int> screen, TextureArray textures,
        BlockVisuals visuals, ItemRegistry items)
    {
        _gl = gl;
        _textures = textures;
        _visuals = visuals;
        _items = items;
        _font = TrueTypeFont.Load(fontPath);
        _batch = new SpriteBatch(gl);
        Rebuild(UiScale.For(Math.Max(1, screen.X), Math.Max(1, screen.Y)));
    }

    public void Draw(Vector2D<int> screen, Inventory inventory, bool creative, Vector2 mouse)
    {
        if (screen.X <= 0 || screen.Y <= 0)
            return;
        _lastInventory = inventory;
        var scale = UiScale.For(screen.X, screen.Y);
        if (scale != _scale)
            Rebuild(scale);

        _canvas.Clear();
        _canvas.FillRect(0, 0, screen.X, screen.Y, new UiColor(0, 0, 0, 105));
        var grid = GridBounds(screen, scale);
        var rows = creative ? 7 : 4;
        var panel = new PixelRect(grid.X - 8 * scale, grid.Y - 25 * scale,
            grid.Width + 16 * scale, (rows * SlotSize + 38) * scale);
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
            var paletteY = grid.Y + 4 * SlotSize * scale + 9 * scale;
            Text("ТВОРЧЕСКАЯ ПАЛИТРА", grid.X, paletteY, UiColor.White, scale);
            var items = _items.All.Where(item => !item.Id.IsAir).ToArray();
            for (var i = 0; i < items.Length; i++)
            {
                var col = i % Columns;
                var row = i / Columns;
                var slot = new PixelRect(grid.X + col * SlotSize * scale,
                    paletteY + 11 * scale + row * SlotSize * scale, SlotSize * scale, SlotSize * scale);
                DrawSlot(slot, new ItemStack(items[i].Id, 1), slot.Contains(mouse), scale);
            }
        }

        var hovered = FindHovered(screen, scale, creative, mouse);
        if (hovered is { } item)
        {
            var name = ItemNames.For(item);
            var width = _canvas.MeasureText(name) + 8 * scale;
            var x = Math.Clamp((int)mouse.X + 12 * scale, 0, Math.Max(0, screen.X - width));
            var y = Math.Clamp((int)mouse.Y + 12 * scale, 0, Math.Max(0, screen.Y - 18 * scale));
            _canvas.FillRect(x, y, width, 16 * scale, new UiColor(8, 8, 8, 245));
            _canvas.DrawFrame(x, y, width, 16 * scale, scale, Border);
            Text(name, x + 4 * scale, y + 4 * scale, UiColor.White, scale);
        }

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

    public bool HandleClick(Vector2 mouse, Vector2D<int> screen, bool primary, bool secondary, bool shift,
        Inventory inventory, bool creative, Action<ClientMessage> send)
    {
        if (!primary && !secondary)
            return false;
        var scale = UiScale.For(Math.Max(1, screen.X), Math.Max(1, screen.Y));
        var grid = GridBounds(screen, scale);
        for (var row = 0; row < 4; row++)
        for (var col = 0; col < Columns; col++)
        {
            var slot = new PixelRect(grid.X + col * SlotSize * scale, grid.Y + row * SlotSize * scale,
                SlotSize * scale, SlotSize * scale);
            if (!slot.Contains(mouse))
                continue;
            var index = row == 3 ? col : Inventory.HotbarSize + row * Columns + col;
            send(new InventoryClick(index, primary
                ? shift ? InventoryClickKind.QuickMove : InventoryClickKind.Primary
                : InventoryClickKind.Secondary));
            return true;
        }

        if (creative)
        {
            var paletteY = grid.Y + 4 * SlotSize * scale + 20 * scale;
            var col = (int)((mouse.X - grid.X) / (SlotSize * scale));
            var row = (int)((mouse.Y - paletteY) / (SlotSize * scale));
            var items = _items.All.Where(item => !item.Id.IsAir).ToArray();
            if (mouse.X >= grid.X && mouse.X < grid.X + grid.Width && col >= 0 && col < Columns && row >= 0 && row * Columns + col < items.Length &&
                mouse.Y >= paletteY && mouse.Y < paletteY + ((items.Length + Columns - 1) / Columns) * SlotSize * scale)
            {
                send(new CreativeGiveItem(items[row * Columns + col].Id));
                return true;
            }
        }
        return false;
    }

    private ItemDefinition? FindHovered(Vector2D<int> screen, int scale, bool creative, Vector2 mouse)
    {
        var grid = GridBounds(screen, scale);
        for (var row = 0; row < 4; row++)
        for (var col = 0; col < Columns; col++)
        {
            var slot = new PixelRect(grid.X + col * SlotSize * scale, grid.Y + row * SlotSize * scale,
                SlotSize * scale, SlotSize * scale);
            if (!slot.Contains(mouse))
                continue;
            var index = row == 3 ? col : Inventory.HotbarSize + row * Columns + col;
            var stack = _lastInventory![index];
            return stack.IsEmpty ? null : _items.Get(stack.Item);
        }

        if (!creative)
            return null;
        var paletteY = grid.Y + 4 * SlotSize * scale + 20 * scale;
        var colIndex = (int)((mouse.X - grid.X) / (SlotSize * scale));
        var rowIndex = (int)((mouse.Y - paletteY) / (SlotSize * scale));
        var items = _items.All.Where(item => !item.Id.IsAir).ToArray();
        var indexInPalette = rowIndex * Columns + colIndex;
        if (mouse.X >= grid.X && mouse.X < grid.X + grid.Width && colIndex >= 0 && colIndex < Columns && rowIndex >= 0 && indexInPalette < items.Length &&
            mouse.Y >= paletteY && mouse.Y < paletteY + ((items.Length + Columns - 1) / Columns) * SlotSize * scale)
            return items[indexInPalette];
        return null;
    }

    private Inventory? _lastInventory;

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

    private PixelRect GridBounds(Vector2D<int> screen, int scale)
    {
        var width = Columns * SlotSize * scale;
        var height = 4 * SlotSize * scale;
        return new PixelRect((screen.X - width) / 2, (screen.Y - height) / 2 - (12 * scale), width, height);
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
