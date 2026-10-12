namespace Client.Rendering;

/// <summary>Имена текстур граней блока (файлы <c>Assets/Textures/Blocks/{имя}.png</c>).</summary>
internal sealed record BlockAppearance(string Top, string Side, string Bottom)
{
    public static BlockAppearance Uniform(string texture) => new(texture, texture, texture);

    public string For(FaceKind kind) => kind switch
    {
        FaceKind.Top => Top,
        FaceKind.Bottom => Bottom,
        _ => Side,
    };
}
