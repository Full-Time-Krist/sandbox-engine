namespace Engine.Ui;

/// <summary>
/// Масштаб интерфейса: сколько пикселей экрана в одном пикселе интерфейса. Только целый (1×, 2×, 3×…):
/// при дробном пиксели текстур и шрифта ложились бы на экран неровно и размывались.
/// </summary>
public static class UiScale
{
    /// <summary>
    /// Размер экрана в пикселях интерфейса, который должен поместиться всегда. Масштаб — наибольший,
    /// при котором окно не меньше этого: в 1280 × 720 — 3, в 1920 × 1080 — 4, в 800 × 600 — 2.
    /// </summary>
    public const int MinWidth = 320;
    public const int MinHeight = 240;

    public static int For(int width, int height) => Math.Max(1, Math.Min(width / MinWidth, height / MinHeight));
}
