namespace Shared.Voxels;

/// <summary>Координаты чанка: мировые координаты блока, делённые на размер чанка с округлением вниз.</summary>
public readonly record struct ChunkPos(int X, int Y, int Z)
{
    /// <summary>Мировые координаты угла чанка с наименьшими X, Y, Z.</summary>
    public BlockPos Origin => new(X << ChunkMath.Bits, Y << ChunkMath.Bits, Z << ChunkMath.Bits);

    public BlockPos ToWorld(LocalPos local) =>
        new((X << ChunkMath.Bits) + local.X, (Y << ChunkMath.Bits) + local.Y, (Z << ChunkMath.Bits) + local.Z);

    public override string ToString() => $"chunk ({X}, {Y}, {Z})";
}
