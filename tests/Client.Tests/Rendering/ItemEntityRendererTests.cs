using System.Numerics;
using Client.Rendering;
using Shared.Content;
using Shared;
using Shared.Items;
using Shared.Lighting;
using Shared.Voxels;

namespace Client.Tests.Rendering;

/// <summary>Предметы на земле без OpenGL: какой вид у предмета и правильный ли общий меш.</summary>
public class ItemEntityRendererTests
{
    private readonly ItemRegistry _items;
    private readonly ItemVisuals _visuals;

    public ItemEntityRendererTests()
    {
        var content = GameContent.Load();
        _items = content.Items;
        _visuals = new ItemVisuals(new BlockVisuals(content.Blocks, BlockVisuals.AppearancesFrom(content.BlockTextures)),
            _items, content.ItemIcons);
    }

    private float Layer(string texture) => _visuals.Blocks.TextureNames.ToList().IndexOf(texture);

    /// <summary>Предмет-блок — кубик с текстурами верха, бока и низа этого блока (а не, скажем, все грани верхом).</summary>
    [Fact]
    public void BlockItem_IsCube_WithTopSideBottomLayers()
    {
        var look = ItemLook.For(_items.Get("base:grass"), _visuals);

        Assert.False(look.Sprite);
        Assert.Equal(new Vector3(Layer("base:grass_top"), Layer("base:grass_side"), Layer("base:dirt")), look.Layers);
    }

    [Fact]
    public void ItemWithIcon_IsSprite_WithItsLayer()
    {
        var stick = _items.Get("base:stick");
        var look = ItemLook.For(stick, _visuals);

        Assert.True(look.Sprite);
        Assert.Equal(new Vector3(_visuals.IconLayer(stick.Id)), look.Layers);
        Assert.True(look.Layers.X >= _visuals.Blocks.TextureNames.Count); // слой картинки предмета, а не блока
    }

    [Fact]
    public void NotBlockItem_WithoutIcon_IsMissingTextureSprite()
    {
        var items = new ItemRegistry();
        var stick = items.Register("base:stick", 64);
        items.Freeze();

        var look = ItemLook.For(stick, new ItemVisuals(_visuals.Blocks, items, new Dictionary<string, string>()));

        Assert.True(look.Sprite);
        Assert.Equal(Vector3.Zero, look.Layers);
    }

    /// <summary>
    /// Меш: 6 граней кубика по 2 треугольника и квадрат картинки. Все треугольники кубика смотрят наружу (обход
    /// против часовой стрелки снаружи — задние грани отсекаются), квадрат — к +Z своей плоскости: шейдер разворачивает
    /// его так, что +Z смотрит на камеру.
    /// </summary>
    [Fact]
    public void Mesh_CubeFacesOutward_SpriteFacesForward()
    {
        var vertices = ItemEntityRenderer.BuildMesh();

        Assert.Equal(42, vertices.Count);
        var cube = vertices.Take(36).ToList();
        var sprite = vertices.Skip(36).ToList();
        Assert.All(cube, v => Assert.Equal(0, v.Sprite));
        Assert.All(sprite, v => Assert.Equal(1, v.Sprite));

        var center = new Vector3(0, ItemEntityRenderer.CubeSize / 2, 0);
        for (var i = 0; i < cube.Count; i += 3)
        {
            var (a, b, c) = (cube[i].Position, cube[i + 1].Position, cube[i + 2].Position);
            var normal = Vector3.Cross(b - a, c - a);
            Assert.True(Vector3.Dot(normal, (a + b + c) / 3 - center) > 0, $"треугольник {i / 3} кубика смотрит внутрь");
        }
        for (var i = 0; i < sprite.Count; i += 3)
            Assert.True(Vector3.Cross(sprite[i + 1].Position - sprite[i].Position, sprite[i + 2].Position - sprite[i].Position).Z > 0);

        // Кубик стоит основанием на точке предмета и не больше заданного размера.
        Assert.Equal(0, cube.Min(v => v.Position.Y), 1e-6);
        Assert.Equal(ItemEntityRenderer.CubeSize, cube.Max(v => v.Position.X) - cube.Min(v => v.Position.X), 1e-6);
        // Верхняя грань — слой «верх» (0) с яркостью верха, нижняя — «низ» (2).
        Assert.All(cube.Where(v => v.Position.Y > ItemEntityRenderer.CubeSize - 1e-6 && v.Face == 0), v => Assert.Equal(1f, v.Shade));
        Assert.Equal(6, cube.Count(v => v.Face == 0));
        Assert.Equal(6, cube.Count(v => v.Face == 2));
        Assert.All(cube.Where(v => v.Face == 0), v => Assert.Equal(ItemEntityRenderer.CubeSize, v.Position.Y, 1e-6));
        Assert.All(cube.Where(v => v.Face == 2), v => Assert.Equal(0, v.Position.Y, 1e-6));
    }

    [Fact]
    public void Light_IsOfBlockWhereItemLies()
    {
        // Предмет лежит на камне y = 4: точка тела ровно на его верхней грани, свет — воздуха над ним, а не камня.
        var world = new ClientWorld();
        var chunk = new Chunk(default);
        chunk.Light.Fill(ChunkLight.Pack(0, 0));
        chunk.Light.Set(new LocalPos(3, 5, 3).Index, ChunkLight.Pack(12, 6));
        world.AddChunk(chunk);

        Assert.Equal(new Vector2(12, 6), ItemEntityRenderer.LightOf(world, new Vector3d(3.5, 5, 3.5)));
        Assert.Equal(new Vector2(12, 6), ItemEntityRenderer.LightOf(world, new Vector3d(3.5, 5 - 1e-9, 3.5)));
        Assert.Equal(new Vector2(15, 0), ItemEntityRenderer.LightOf(world, new Vector3d(3.5, 500, 3.5)));
    }
}
