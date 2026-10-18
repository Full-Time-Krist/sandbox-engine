using Api;
using Shared.Blocks;
using Shared.Content;
using Shared.Items;

namespace Server.Modding;

/// <summary>Контекст загрузки одного мода: всё, что он регистрирует, помечается его id.</summary>
internal sealed class ModContext(string modId, ContentRegistries content, ItemBehaviorRegistry behaviors, GameEvents events)
    : IModContext
{
    public string ModId => modId;
    public BlockRegistry Blocks => content.Blocks;
    public ItemRegistry Items => content.Items;

    public IModEvents Events { get; } = events.For(modId);

    public void RegisterItemBehavior(string id, IItemBehavior behavior) => behaviors.Register(modId, id, behavior);
}
