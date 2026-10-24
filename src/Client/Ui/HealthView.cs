using System.Numerics;
using Engine.Ui;
using Shared.Protocol;
using Silk.NET.Maths;

namespace Client.Ui;

/// <summary>Состояние здоровья и экрана смерти, подтверждаемое сервером.</summary>
internal sealed class HealthView
{
    public int Health { get; private set; } = 20;
    public int MaxHealth { get; private set; } = 20;
    public bool IsDead { get; private set; }
    public bool RespawnPending { get; private set; }
    public string Reason { get; private set; } = "";
    public bool CanControl => !IsDead;
    public void Apply(HealthChanged state)
    {
        MaxHealth = Math.Max(1, state.MaxHealth);
        Health = Math.Clamp(state.Health, 0, MaxHealth);
        IsDead = state.IsDead;
        Reason = state.Reason;
        if (!IsDead) RespawnPending = false;
    }
    public bool TryRespawn(Vector2 mouse, bool click, Vector2D<int> screen, int scale)
    {
        if (!IsDead || RespawnPending || !click || !RespawnButton(screen, scale).Contains(mouse)) return false;
        RespawnPending = true;
        return true;
    }
    public static PixelRect RespawnButton(Vector2D<int> screen,int scale)
    {
        var width = Math.Min(160 * scale, Math.Max(1, screen.X - 16 * scale));
        return new((screen.X - width) / 2, screen.Y / 2 + 20 * scale, width, 22 * scale);
    }
    public static void DrawHearts(UiCanvas canvas,Vector2D<int> screen,int scale,int health,int maximum)
    {
        string[] rows = ["0110110", "1111111", "1111111", "0111110", "0011100", "0001000"];
        var hotbar = HudLayout.Hotbar(screen.X, screen.Y, scale);
        var hearts = Math.Min(10, (Math.Max(1, maximum) + 1) / 2);
        for (var heart = 0; heart < hearts; heart++)
        for (var y = 0; y < rows.Length; y++)
        for (var x = 0; x < rows[y].Length; x++)
        {
            if (rows[y][x] != '1') continue;
            var filled = health >= heart * 2 + 2 || (health == heart * 2 + 1 && x < 3);
            canvas.FillRect(hotbar.X + (heart * 9 + x) * scale, hotbar.Y - (9 - y) * scale,
                scale, scale, filled ? new UiColor(235,55,65) : new UiColor(65,30,38));
        }
    }
}
