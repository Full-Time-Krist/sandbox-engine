using System.Reflection;
using System.Text;
using Client;
using Engine.Windowing;
using Server;
using Shared;
using Shared.Content;
using Shared.Modding;
using Shared.Networking;
using Shared.Protocol;

// По умолчанию консоль Windows пишет в CP866: при перенаправлении в файл кириллица превращается в кракозябры.
Console.OutputEncoding = Encoding.UTF8;

var product = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "Game";

// Мир: Sandbox.exe --world имя. Сохранения лежат в %LOCALAPPDATA%\<название игры>\Saves\<имя мира>\,
// а не рядом с exe: папку сборки удаляет dotnet clean.
var worldName = StringArgument("--world") ?? "world";
if (worldName.Length == 0 || worldName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || worldName.Trim('.').Length == 0)
    throw new ArgumentException($"Недопустимое имя мира: '{worldName}'");
var savePath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), product, "Saves", worldName, "world.db");

// Сид и точка появления действуют только при создании мира, дальше они хранятся в сохранении.
// Сид: --seed 123, без аргумента — случайный. Точка появления: --spawn 1000000 1000000 — для проверки мира
// вдали от центра.
var seed = IntArgument("--seed", 0);
(int, int)? spawnColumn = IntArgument("--spawn", 0) is { } sx ? (sx, IntArgument("--spawn", 1) ?? 0) : null;

// Режим новых игроков: --mode survival (по умолчанию) или --mode creative. Тоже только при создании мира.
GameMode? defaultMode = null;
if (StringArgument("--mode") is { } modeName)
{
    defaultMode = GameModes.TryParse(modeName, out var mode)
        ? mode
        : throw new ArgumentException($"Неизвестный режим: '{modeName}' (survival или creative)");
}

// Моды: общая папка %LOCALAPPDATA%\<название игры>\Mods\ (Linux: ~/.local/share/<название игры>/Mods/), включены во всех мирах.
// --mods ПАПКА подменяет её: так проверки вживую не трогают моды игрока. Общую папку создаём, чтобы её было легко найти.
var modsDirectory = StringArgument("--mods")
    ?? Directory.CreateDirectory(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), product, "Mods")).FullName;
IReadOnlyList<ModInfo> mods = [];

// Одиночная игра: встроенный сервер в том же процессе, в своём потоке, связь через очереди в памяти.
// Для мультиплеера здесь будет сетевое соединение вместо LocalConnection и null вместо сервера.
using var server = CreateServer();
if (server == null)
    return 1;
var (clientConnection, serverConnection) = LocalConnection.CreatePair<ClientMessage, ServerMessage>();
server.Connect(serverConnection);

// Порядок освобождения обратный: сначала закрывается окно, затем останавливается поток сервера,
// и только потом сервер (он останавливает генерацию и дописывает сохранение).
using var host = new ServerHost(server);
host.Start();

using var app = new ClientApp(new WindowSettings { Title = product }, clientConnection, host, mods);
app.Run();
return 0;

// Сервер первым читает моды и контент: ошибку показываем одной строкой с модом или файлом, а не стеком вызовов.
GameServer? CreateServer()
{
    try
    {
        mods = ModDiscovery.Discover(modsDirectory);
        return new GameServer(seed, spawnColumn: spawnColumn, savePath: savePath, defaultMode: defaultMode, mods: mods);
    }
    catch (ContentLoadException exception)
    {
        Console.Error.WriteLine($"[Content] Ошибка в контенте игры: {exception.Message}");
        return null;
    }
    catch (ModLoadException exception)
    {
        Console.Error.WriteLine($"[Mods] {exception.Message}");
        return null;
    }
}

// Строка после аргумента name, или null, если аргумента нет.
string? StringArgument(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

// Число после аргумента name (offset — номер числа: у --spawn их два), или null, если аргумента нет.
int? IntArgument(string name, int offset)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 + offset < args.Length ? int.Parse(args[index + 1 + offset]) : null;
}
