using Api;
using Shared;
using Shared.Items;

namespace ExampleMod;

/// <summary>
/// Пример мода: медная руда (блок и предмет — в <c>content/</c>, текстура — в <c>textures/</c>), волшебная палочка
/// с поведением на C# и два обработчика событий. Игра находит этот класс сама: он единственный реализует <see cref="IMod"/>.
/// </summary>
public sealed class ExampleMod : IMod
{
    public void Load(IModContext context)
    {
        // Определения уже загружены из JSON (свои и ванильные): берём их id по постоянным именам.
        var stone = context.Blocks.Get("base:stone").Id;
        var copperOre = context.Blocks.Get("example_mod:copper_ore").Id;
        var copperOreItem = context.Items.Get("example_mod:copper_ore").Id;
        var wand = context.Items.Get("example_mod:wand").Id;

        // Предмет example_mod:wand ссылается на это поведение полем "behavior" в своём JSON.
        context.RegisterItemBehavior("example_mod:wand", new WandBehavior(stone, copperOre));

        // Медная руда в выживании даёт вдвое больше: одну штуку кладёт игра, вторую — мод.
        context.Events.OnBlockBroken(e =>
        {
            if (e.Block != copperOre || e.Player.Mode != GameMode.Survival)
                return;
            e.Player.Inventory.Add(new ItemStack(copperOreItem, 1));
            context.Log($"{e.Player.Name} добыл медную руду: +1 в награду");
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
