using Shared.Blocks;
using Shared.Items;

namespace Shared.Tests.Items;

public class MiningTests
{
    private static readonly BlockMining Stone = new(2.25f, "pickaxe", 1);
    private static readonly BlockMining CopperOre = new(4.5f, "pickaxe", 2);
    private static readonly BlockMining Log = new(3f, "axe", 0);
    private static readonly ToolInfo WoodenPickaxe = new("pickaxe", 1, 2);
    private static readonly ToolInfo StonePickaxe = new("pickaxe", 2, 4);
    private static readonly ToolInfo WoodenAxe = new("axe", 1, 2);

    [Fact]
    public void StoneByHand_IsSlowAndGivesNothing_PickaxeIsFastAndHarvests()
    {
        Assert.False(Mining.CanHarvest(Stone, null));
        Assert.Equal(7.5f, Mining.BreakSeconds(Stone, null), 3);
        Assert.True(Mining.CanHarvest(Stone, WoodenPickaxe));
        Assert.Equal(1.125f, Mining.BreakSeconds(Stone, WoodenPickaxe), 3);
    }

    [Fact]
    public void LowerTierTool_IsFasterThanHand_ButGivesNothing()
    {
        Assert.False(Mining.CanHarvest(CopperOre, WoodenPickaxe));
        Assert.Equal(4.5f / 2 * Mining.NoHarvestSlowdown, Mining.BreakSeconds(CopperOre, WoodenPickaxe), 3);
        Assert.True(Mining.CanHarvest(CopperOre, StonePickaxe));
        Assert.Equal(1.125f, Mining.BreakSeconds(CopperOre, StonePickaxe), 3);
    }

    [Fact]
    public void WrongToolType_IsLikeHand()
    {
        // Топор по камню — как рукой; кирка по бревну — тоже, но бревно и рукой добывается.
        Assert.Equal(Mining.BreakSeconds(Stone, null), Mining.BreakSeconds(Stone, WoodenAxe));
        Assert.False(Mining.CanHarvest(Stone, WoodenAxe));
        Assert.Equal(3f, Mining.BreakSeconds(Log, WoodenPickaxe));
        Assert.True(Mining.CanHarvest(Log, null));
        Assert.Equal(1.5f, Mining.BreakSeconds(Log, WoodenAxe));
    }

    [Fact]
    public void Ticks_RoundUp_ZeroHardnessIsInstant()
    {
        Assert.Equal(0, Mining.BreakTicks(new BlockMining(0, null, 0), null));
        Assert.Equal(18, Mining.BreakTicks(new BlockMining(0.9f, "shovel", 0), null));  // ровно 18 тиков, без лишнего из-за погрешности
        Assert.Equal(23, Mining.BreakTicks(Stone, WoodenPickaxe));                       // 22,5 → 23
        Assert.Equal(1, Mining.BreakTicks(new BlockMining(0.01f, null, 0), null));       // мгновенно — только при нулевой твёрдости
    }
}
