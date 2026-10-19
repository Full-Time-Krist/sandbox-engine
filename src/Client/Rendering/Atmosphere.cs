using System.Numerics;
using Engine.Graphics;
using Shared.Lighting;

namespace Client.Rendering;

/// <summary>
/// Что сцене нужно от времени суток: яркость неба (множитель небесного света), цвет неба и тумана, где туман начинается
/// и где закрывает всё. Без OpenGL — считается и проверяется тестами; шейдерам отдаётся <see cref="Apply"/>.
/// </summary>
/// <remarks>
/// Туман — цвет неба по краю прорисовки: прячет обрыв мира и заодно показывает вдали ночь и закат. Он сферический
/// (по расстоянию от глаза), полностью закрывает на дальности прорисовки, начинается на <see cref="FogStartShare"/> её.
/// </remarks>
internal readonly record struct Atmosphere(float SkyBrightness, Vector3 SkyColor, float FogStart, float FogEnd)
{
    public static readonly Vector3 DayColor = new(0.53f, 0.81f, 0.92f);
    public static readonly Vector3 NightColor = new(0.02f, 0.03f, 0.08f);
    public static readonly Vector3 SunsetColor = new(0.98f, 0.52f, 0.30f);

    /// <summary>Доля дальности прорисовки, где начинается туман.</summary>
    public const float FogStartShare = 0.65f;

    /// <param name="worldTime">Тик мира, с долей тика (<see cref="Client.WorldClock.At"/>).</param>
    /// <param name="viewDistanceBlocks">Дальность прорисовки в блоках: там туман закрывает всё.</param>
    public static Atmosphere At(double worldTime, float viewDistanceBlocks)
    {
        var brightness = DayCycle.SkyBrightness(worldTime);
        return new Atmosphere(brightness, SkyColorAt(brightness), viewDistanceBlocks * FogStartShare, viewDistanceBlocks);
    }

    /// <summary>
    /// Цвет неба по яркости неба: от ночного к дневному, а посередине перехода (рассвет, закат) — с красным.
    /// По яркости, а не по времени: рассвет и закат выглядят одинаково, и цвет непрерывен вместе с ней.
    /// </summary>
    public static Vector3 SkyColorAt(float skyBrightness)
    {
        var day = Math.Clamp((skyBrightness - DayCycle.NightSkyBrightness) / (1 - DayCycle.NightSkyBrightness), 0, 1);
        var color = Vector3.Lerp(NightColor, DayColor, day);
        // Красное сильнее всего посередине перехода и пропадает к полному дню и к ночи.
        var glow = 4 * day * (1 - day);
        return Vector3.Lerp(color, SunsetColor, glow * 0.65f);
    }

    /// <summary>Выставляет uniform'ы освещённых шейдеров (<c>chunk</c>, <c>item</c>).</summary>
    public void Apply(ShaderProgram shader)
    {
        shader.SetUniform("u_SkyBrightness", SkyBrightness);
        shader.SetUniform("u_FogColor", SkyColor);
        shader.SetUniform("u_FogStart", FogStart);
        shader.SetUniform("u_FogEnd", FogEnd);
    }
}
