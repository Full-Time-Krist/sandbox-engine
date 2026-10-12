namespace Engine.Windowing;

public sealed record WindowSettings
{
    public string Title { get; init; } = "Game";
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
    public bool VSync { get; init; } = true;
}
