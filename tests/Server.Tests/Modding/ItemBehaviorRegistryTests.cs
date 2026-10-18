using Api;
using Server.Modding;
using Shared.Content;
using Shared.Items;

namespace Server.Tests.Modding;

public class ItemBehaviorRegistryTests
{
    private readonly ItemBehaviorRegistry _registry = new();

    private sealed class NoOp : IItemBehavior
    {
        public void Use(ItemAction action, in ItemUseContext context) { }
    }

    [Fact]
    public void Bind_GivesEachItemItsBehavior_EmptySlotGetsHand()
    {
        var hand = new NoOp();
        var place = new NoOp();
        var wand = new NoOp();
        _registry.Register("base", ItemRegistry.HandBehavior, hand);
        _registry.Register("base", ItemRegistry.PlaceBlockBehavior, place);
        _registry.Register("example_mod", "example_mod:wand", wand);
        var items = new ItemRegistry();
        var stick = items.Register("base:stick", 64).Id;
        var stone = items.Register("base:stone", 64, new Shared.Blocks.BlockId(1)).Id;
        var magic = items.Register("example_mod:magic_stick", 1, behavior: "example_mod:wand").Id;
        items.Freeze();

        var bound = _registry.Bind(items);

        Assert.Same(hand, bound[ItemId.Air.Value]);
        Assert.Same(hand, bound[stick.Value]);
        Assert.Same(place, bound[stone.Value]);
        Assert.Same(wand, bound[magic.Value]);
        Assert.True(_registry.IsFrozen);
    }

    [Fact]
    public void Bind_UnknownBehavior_IsContentError_NamingItemAndBehavior()
    {
        _registry.Register("base", ItemRegistry.HandBehavior, new NoOp());
        var items = new ItemRegistry();
        items.Register("example_mod:wand", 1, behavior: "example_mod:missing");
        items.Freeze();

        var exception = Assert.Throws<ContentLoadException>(() => _registry.Bind(items));

        Assert.Contains("'example_mod:wand'", exception.Message);
        Assert.Contains("'example_mod:missing'", exception.Message);
    }

    [Theory]
    [InlineData("example_mod", "wand")]                 // без пространства имён
    [InlineData("example_mod", "base:wand")]            // чужое пространство имён
    [InlineData("example_mod", "example_mod:Wand")]     // неверные символы
    public void Register_InvalidOrForeignName_Throws(string modId, string id)
    {
        var exception = Assert.Throws<ArgumentException>(() => _registry.Register(modId, id, new NoOp()));

        Assert.Contains($"'{modId}'", exception.Message);
    }

    [Fact]
    public void Register_Duplicate_OrAfterBind_Throws()
    {
        _registry.Register("base", ItemRegistry.HandBehavior, new NoOp());
        Assert.Throws<InvalidOperationException>(() => _registry.Register("base", ItemRegistry.HandBehavior, new NoOp()));

        var items = new ItemRegistry();
        items.Freeze();
        _registry.Bind(items);
        Assert.Throws<InvalidOperationException>(() => _registry.Register("base", "base:late", new NoOp()));
    }

    [Fact]
    public void BaseMod_RegistersBehaviorsForAllVanillaItems()
    {
        var content = GameContent.Load();
        new BaseMod().Load(new ModContext(BaseMod.Id, content, _registry, new GameEvents(_ => { })));

        var bound = _registry.Bind(content.Items);

        Assert.All(bound, Assert.NotNull);
    }
}
