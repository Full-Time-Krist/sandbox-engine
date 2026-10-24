using Api;
using Shared;
using Shared.Items;

namespace ExampleMod;

/// <summary>
/// Пример мода: аметистовая руда (блок и предмет — в <c>content/</c>, текстура — в <c>textures/</c>), волшебная палочка
/// с поведением на C#, рецепт и задание (в <c>content/</c>) и три обработчика событий. Кроме того, <c>content/overrides/sand.json</c> заменяет ванильный
/// песок (<c>"override": true</c>) — новая текстура; для этого мод зависит от <c>base</c> в <c>mod.json</c>.
/// Игра находит этот класс сама: он единственный реализует <see cref="IMod"/>.
/// </summary>
public sealed class ExampleMod : IMod
{
    public void Load(IModContext context)
    {
        // Определения уже загружены из JSON (свои и ванильные): берём их id по постоянным именам.
        var stone = context.Blocks.Get("base:stone").Id;
        var amethystOre = context.Blocks.Get("example_mod:amethyst_ore").Id;
        var amethystOreItem = context.Items.Get("example_mod:amethyst_ore").Id;
        var wand = context.Items.Get("example_mod:wand").Id;
        var copperIngot = context.Items.Get("base:copper_ingot").Id;

        // Предмет example_mod:wand ссылается на это поведение полем "behavior" в своём JSON.
        context.RegisterItemBehavior("example_mod:wand", new WandBehavior(stone, amethystOre));

        // Аметистовая руда в выживании даёт вдвое больше: одну штуку роняет игра, вторую кладёт в инвентарь мод.
        context.Events.OnBlockBroken(e =>
        {
            if (e.Block != amethystOre || e.Player.Mode != GameMode.Survival)
                return;
            e.Player.Inventory.Add(new ItemStack(amethystOreItem, 1));
            context.Log($"{e.Player.Name} добыл аметистовую руду: +1 в награду");
        });

        // Награда падает предметом: её можно подобрать позже, даже если инвентарь сейчас полон.
        context.Events.OnQuestCompleted(e =>
        {
            if (e.Quest.Name != "example_mod:amethyst")
                return;
            e.World.SpawnItem(e.Player.Position + new Vector3d(0, 0.6, 0), new ItemStack(copperIngot, 3));
            context.Log($"{e.Player.Name} выполнил «{e.Quest.Title}»: награда — 3 медных слитка");
        });

        // Палочка — каждому вошедшему, у кого её ещё нет.
        context.Events.OnPlayerJoined(e =>
        {
            for (var slot = 0; slot < Inventory.Size; slot++)
            {
                if (e.Player.Inventory[slot].Item == wand)
                    return;
            }
            e.Player.Inventory.Add(new ItemStack(wand, 1));
            context.Log($"{e.Player.Name} получил волшебную палочку");
        });
    }
}
