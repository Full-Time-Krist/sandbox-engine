using Client.Rendering;
using Engine.Graphics;
using Engine.Ui;
using Shared.Items;
using Shared.Quests;
using Silk.NET.Maths;
using Silk.NET.OpenGL;

namespace Client.Ui;

/// <summary>
/// Интерфейс поверх мира: перекрестие, хотбар с иконками и числом штук, название предмета при смене слота,
/// текущие задания в левом верхнем углу и всплывающее сообщение о выполненном задании.
/// Всё строится заново каждый кадр (это сотня четырёхугольников) и рисуется двумя вызовами: перекрестие
/// с инверсией фона и остальное с обычной прозрачностью.
/// </summary>
internal sealed class Hud : IDisposable
{
    /// <summary>Высота прописной буквы в пикселях интерфейса — 7, как у шрифта Minecraft.</summary>
    public const int CapHeight = 7;

    /// <summary>Сколько секунд название предмета видно после смены слота; последние <see cref="NameFadeSeconds"/> оно гаснет.</summary>
    private const double NameSeconds = 2;
    private const double NameFadeSeconds = 0.5;

    /// <summary>Сколько секунд видно сообщение о выполненном задании; последние <see cref="NameFadeSeconds"/> оно гаснет.</summary>
    private const double ToastSeconds = 4;

    /// <summary>Сколько доступных заданий подсказывать в углу экрана: больше — уже список, а он в журнале.</summary>
    public const int TrackedQuests = 3;

    private static readonly UiColor ToastTitle = new(255, 215, 90);
    private static readonly UiColor TrackerBackground = new(0, 0, 0, 110);

    private static readonly UiColor HotbarBackground = new(0, 0, 0, 120);
    private static readonly UiColor SlotBorder = new(255, 255, 255, 40);
    private static readonly UiColor SelectionColor = new(255, 255, 255, 230);

    /// <summary>Тень текста — тот же цвет в четыре раза темнее, на пиксель интерфейса правее и ниже.</summary>
    private const float ShadowBrightness = 0.25f;

    private readonly SpriteBatch _batch;
    private readonly TrueTypeFont _font;
    private readonly TextureArray _blockTextures;
    private readonly ItemVisuals _visuals;
    private readonly ItemRegistry _items;
    private readonly ItemNames _names;
    private readonly QuestRegistry _quests;
    private readonly GL _gl;

    /// <summary>Выполненные задания, о которых ещё не сообщили (первое — показывается сейчас).</summary>
    private readonly ToastQueue<Quest> _toasts = new(ToastSeconds, NameFadeSeconds);

    private UiAtlas _atlas = null!;
    private Texture2D? _atlasTexture;
    private UiCanvas _canvas = null!;
    private UiCanvas _crosshair = null!;
    private int _scale;

    // Что было в руке в прошлом кадре: при смене слота или предмета его название показывается заново.
    private int _lastSlot = -1;
    private ItemId _lastItem;
    private double _nameTimeLeft;

    /// <param name="fontPath">TTF-шрифт интерфейса; растеризуется при запуске и при смене масштаба.</param>
    /// <param name="screen">Размер кадра при запуске: атлас под него строится сразу.</param>
    /// <param name="blockTextures">Массив текстур блоков: из него рисуются иконки. Им владеет вызывающий.</param>
    public Hud(GL gl, string fontPath, Vector2D<int> screen, TextureArray blockTextures, ItemVisuals visuals, ItemRegistry items,
        ItemNames names, QuestRegistry quests)
    {
        _quests = quests;
        _gl = gl;
        _font = TrueTypeFont.Load(fontPath);
        _batch = new SpriteBatch(gl);
        _blockTextures = blockTextures;
        _visuals = visuals;
        _items = items;
        _names = names;

        // Атлас — при загрузке, а не в первом кадре: первое построение с компиляцией кода растеризации длится
        // десятки миллисекунд (замер: 22 мс против 3–8 мс у повторных при смене размера окна).
        Rebuild(UiScale.For(Math.Max(1, screen.X), Math.Max(1, screen.Y)));
    }

    /// <param name="screen">Размер кадра в пикселях.</param>
    /// <param name="deltaTime">Время с прошлого кадра: по нему гаснет название предмета.</param>
    /// <param name="quests">Задания игрока; null — не подсказывать (творческий режим: там задания не идут).</param>
    public void Draw(Vector2D<int> screen, Inventory inventory, double deltaTime, bool inventoryOpen = false, QuestBook? quests = null, HealthView? health = null)
    {
        if (screen.X <= 0 || screen.Y <= 0 || inventoryOpen)
            return;

        var scale = UiScale.For(screen.X, screen.Y);
        if (scale != _scale)
            Rebuild(scale);

        _crosshair.Clear();
        foreach (var bar in HudLayout.Crosshair(screen.X, screen.Y, scale))
            _crosshair.FillRect(bar.X, bar.Y, bar.Width, bar.Height, UiColor.White);
        _batch.Draw(_crosshair, screen, _atlasTexture!, null, UiBlend.Invert);

        _canvas.Clear();
        DrawHotbar(screen, inventory, scale);
        if (health != null) HealthView.DrawHearts(_canvas, screen, scale, health.Health, health.MaxHealth);
        DrawHeldName(screen, inventory, scale, deltaTime);
        if (quests != null)
            DrawTracker(inventory, quests, scale);
        _batch.Draw(_canvas, screen, _atlasTexture!, _blockTextures);
    }

    /// <summary>Экран смерти использует тот же шрифт и цвета панелей, что остальной интерфейс.</summary>
    public void DrawDeath(Vector2D<int> screen, HealthView health, System.Numerics.Vector2 mouse)
    {
        if (!health.IsDead || screen.X <= 0 || screen.Y <= 0) return;
        var scale = UiScale.For(screen.X, screen.Y);
        if (_scale != scale) Rebuild(scale);
        _canvas.Clear();
        _canvas.FillRect(0, 0, screen.X, screen.Y, new UiColor(45, 8, 12, 190));
        const string title = "Вы погибли";
        var y = screen.Y / 2 - 40 * scale;
        DrawShadowedText(title, (screen.X - _canvas.MeasureText(title)) / 2, y, UiColor.White, scale);
        const string hint = "Вещи остались на месте смерти";
        DrawShadowedText(hint, (screen.X - _canvas.MeasureText(hint)) / 2, y + 18 * scale,
            new UiColor(210, 190, 190), scale);
        var button = HealthView.RespawnButton(screen, scale);
        var hover = !health.RespawnPending && button.Contains(mouse);
        _canvas.FillRect(button.X, button.Y, button.Width, button.Height,
            hover ? new UiColor(75, 75, 85, 245) : new UiColor(25, 25, 32, 245));
        _canvas.DrawFrame(button.X, button.Y, button.Width, button.Height, scale,
            hover ? UiColor.White : new UiColor(150, 150, 160));
        var label = health.RespawnPending ? "Возрождение..." : "Возродиться";
        DrawShadowedText(label, button.X + (button.Width - _canvas.MeasureText(label)) / 2,
            button.Y + (button.Height - CapHeight * scale) / 2, UiColor.White, scale);
        _batch.Draw(_canvas, screen, _atlasTexture!, _blockTextures);
    }

    /// <summary>Сообщить о выполненном задании: сообщения идут по очереди, каждое — <see cref="ToastSeconds"/> секунды.</summary>
    public void ShowQuestCompleted(Quest quest) => _toasts.Show(quest);

    /// <summary>
    /// Сообщение о выполненном задании в правом верхнем углу. Рисуется последним, поверх экранов инвентаря и журнала:
    /// достижение не должно потеряться, если игрок в этот момент что-то делал в инвентаре.
    /// </summary>
    public void DrawToasts(Vector2D<int> screen, double deltaTime)
    {
        if (!_toasts.TryAdvance(deltaTime, out var quest, out var alpha) || screen.X <= 0 || screen.Y <= 0)
            return;

        var scale = UiScale.For(screen.X, screen.Y);
        if (scale != _scale)
            Rebuild(scale);
        const string Header = "Задание выполнено!";
        var width = Math.Max(_canvas.MeasureText(Header), _canvas.MeasureText(quest.Title)) + (ItemIcons.Size + 12) * scale;
        var box = HudLayout.Toast(screen.X, width, scale);

        _canvas.Clear();
        _canvas.FillRect(box.X, box.Y, box.Width, box.Height, new UiColor(18, 18, 22, (byte)(230 * alpha)));
        _canvas.DrawFrame(box.X, box.Y, box.Width, box.Height, scale, ToastTitle.WithAlpha(alpha));
        var icon = QuestView.Icon(quest, _items);
        if (!icon.IsAir)
            ItemIcons.Draw(_canvas, _items.Get(icon), _visuals, box.X + 4 * scale, box.Y + 4 * scale, scale);
        var textX = box.X + (ItemIcons.Size + 8) * scale;
        DrawShadowedText(Header, textX, box.Y + 4 * scale, ToastTitle.WithAlpha(alpha), scale);
        DrawShadowedText(quest.Title, textX, box.Y + 14 * scale, UiColor.White.WithAlpha(alpha), scale);
        _batch.Draw(_canvas, screen, _atlasTexture!, _blockTextures);
    }

    /// <summary>Доступные задания в левом верхнем углу: иконка, название и счёт — что делать дальше.</summary>
    private void DrawTracker(Inventory inventory, QuestBook book, int scale)
    {
        var available = QuestView.Available(_quests, book);
        if (available.Count == 0)
            return;

        var lines = available.Take(TrackedQuests)
            .Select(quest => (Quest: quest, Text: QuestView.ProgressText(quest, book, inventory) is { Length: > 0 } progress
                ? $"{quest.Title} {progress}" : quest.Title))
            .ToArray();
        var width = lines.Max(line => _canvas.MeasureText(line.Text)) + (ItemIcons.Size + 10) * scale;
        var rows = lines.Length + (available.Count > TrackedQuests ? 1 : 0);
        var margin = HudLayout.HotbarMargin * scale;
        _canvas.FillRect(margin, margin, width, (rows * HudLayout.TrackerRow + 4) * scale, TrackerBackground);
        for (var i = 0; i < lines.Length; i++)
        {
            var y = margin + (2 + i * HudLayout.TrackerRow) * scale;
            var icon = QuestView.Icon(lines[i].Quest, _items);
            if (!icon.IsAir)
                ItemIcons.Draw(_canvas, _items.Get(icon), _visuals, margin + 2 * scale, y, scale);
            DrawShadowedText(lines[i].Text, margin + (ItemIcons.Size + 6) * scale, y + 5 * scale, UiColor.White, scale);
        }
        if (available.Count > TrackedQuests)
        {
            DrawShadowedText($"и ещё {available.Count - TrackedQuests} (J — журнал)", margin + (ItemIcons.Size + 6) * scale,
                margin + (2 + lines.Length * HudLayout.TrackerRow + 5) * scale, new UiColor(200, 200, 200), scale);
        }
    }

    /// <summary>Атлас растеризуется под масштаб: глифы в размере экрана, пиксель в пиксель, без растягивания.</summary>
    private void Rebuild(int scale)
    {
        _atlas = UiAtlas.Build(_font, CapHeight * scale, UiAtlas.DefaultCharacters);
        _atlasTexture?.Dispose();
        _atlasTexture = new Texture2D(_gl, _atlas.Image);
        _canvas = new UiCanvas(_atlas);
        _crosshair = new UiCanvas(_atlas);
        _scale = scale;
    }

    private void DrawHotbar(Vector2D<int> screen, Inventory inventory, int scale)
    {
        var hotbar = HudLayout.Hotbar(screen.X, screen.Y, scale);
        _canvas.FillRect(hotbar.X, hotbar.Y, hotbar.Width, hotbar.Height, HotbarBackground);
        for (var i = 0; i < Inventory.HotbarSize; i++)
        {
            var slot = HudLayout.Slot(hotbar, i, scale);
            _canvas.DrawFrame(slot.X, slot.Y, slot.Width, slot.Height, scale, SlotBorder);
        }

        var selection = HudLayout.Selection(HudLayout.Slot(hotbar, inventory.SelectedSlot, scale), scale);
        _canvas.DrawFrame(selection.X, selection.Y, selection.Width, selection.Height, 2 * scale, SelectionColor);

        for (var i = 0; i < Inventory.HotbarSize; i++)
        {
            var stack = inventory[i];
            if (stack.IsEmpty)
                continue;

            var (x, y) = HudLayout.Icon(HudLayout.Slot(hotbar, i, scale), scale);
            ItemIcons.Draw(_canvas, _items.Get(stack.Item), _visuals, x, y, scale);

            // Число штук — у правого нижнего угла иконки, базовая линия на пиксель ниже её края. Одну штуку не пишем.
            if (stack.Count > 1)
            {
                var text = stack.Count.ToString();
                var right = x + (ItemIcons.Size + 1) * scale;
                var baseline = y + (ItemIcons.Size + 1) * scale;
                DrawShadowedText(text, right - _canvas.MeasureText(text), baseline - _atlas.Ascent, UiColor.White, scale);
            }
        }
    }

    /// <summary>Название предмета в руке над хотбаром: появляется при смене слота или предмета и через пару секунд гаснет.</summary>
    private void DrawHeldName(Vector2D<int> screen, Inventory inventory, int scale, double deltaTime)
    {
        var held = inventory.Selected;
        if (inventory.SelectedSlot != _lastSlot || held.Item != _lastItem)
        {
            _nameTimeLeft = held.IsEmpty ? 0 : NameSeconds;
            (_lastSlot, _lastItem) = (inventory.SelectedSlot, held.Item);
        }
        else
        {
            _nameTimeLeft = Math.Max(0, _nameTimeLeft - deltaTime);
        }

        if (_nameTimeLeft <= 0 || held.IsEmpty)
            return;

        var name = _names.For(_items.Get(held.Item));
        var alpha = (float)Math.Min(1, _nameTimeLeft / NameFadeSeconds);
        var hotbar = HudLayout.Hotbar(screen.X, screen.Y, scale);
        var x = (screen.X - _canvas.MeasureText(name)) / 2;
        var y = hotbar.Y - 15 * scale - _atlas.Ascent;
        DrawShadowedText(name, x, y, UiColor.White.WithAlpha(alpha), scale);
    }

    private void DrawShadowedText(string text, int x, int y, UiColor color, int scale)
    {
        _canvas.DrawText(text, x + scale, y + scale, color.Shade(ShadowBrightness));
        _canvas.DrawText(text, x, y, color);
    }

    public void Dispose()
    {
        _atlasTexture?.Dispose();
        _batch.Dispose();
        _font.Dispose();
    }
}
