using Api;
using Server.Items;
using Shared.Items;

namespace Server.Modding;

/// <summary>
/// Ванильный мод <c>base</c>: встроен в игру, но подключается тем же API, что и сторонние моды
/// («ванилла — это тоже мод»). Его контент — <c>content/base</c>, его код — здесь.
/// </summary>
internal sealed class BaseMod : IMod
{
    public const string Id = "base";

    public void Load(IModContext context)
    {
        context.RegisterItemBehavior(ItemRegistry.HandBehavior, new HandBehavior());
        context.RegisterItemBehavior(ItemRegistry.PlaceBlockBehavior, new BlockItemBehavior());
        context.RegisterItemBehavior(ItemRegistry.SpawnMobBehavior, new SpawnEggBehavior());
    }
}
