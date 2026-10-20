using Api;
using Shared.Blocks;
using Shared.Content;
using Shared.Items;
using Shared.Quests;

namespace Server.Modding;

/// <summary>Контекст загрузки одного мода: всё, что он регистрирует, помечается его id.</summary>
internal sealed class ModContext(string modId, ContentRegistries content, ItemBehaviorRegistry behaviors, GameEvents events,
    Action<string> log) : IModContext
{
    public string ModId => modId;
    public BlockRegistry Blocks => content.Blocks;
    public ItemRegistry Items => content.Items;
    public Shared.Mobs.MobRegistry Mobs => content.Mobs;
    public RecipeRegistry Recipes => content.Recipes;
    public QuestRegistry Quests => content.Quests;

    public IModEvents Events { get; } = events.For(modId);

    public void RegisterItemBehavior(string id, IItemBehavior behavior) => behaviors.Register(modId, id, behavior);

    public void Log(string message) => log($"[Mod {modId}] {message}");
}
