using System.Reflection;
using System.Text;
using Client;
using Engine.Windowing;
using Server;
using Shared.Networking;
using Shared.Protocol;

// По умолчанию консоль Windows пишет в CP866: при перенаправлении в файл кириллица превращается в кракозябры.
Console.OutputEncoding = Encoding.UTF8;

var product = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "Game";

// Одиночная игра: встроенный сервер в том же процессе, в своём потоке, связь через очереди в памяти.
// Для мультиплеера здесь будет сетевое соединение вместо LocalConnection и null вместо сервера.
// Сид можно задать аргументом: Sandbox.exe --seed 123. Пока сохранений нет (шаг 2.7), по умолчанию он постоянный,
// чтобы мир не менялся от запуска к запуску и скриншоты можно было сравнивать.
const int DefaultSeed = 1;
var seedIndex = Array.IndexOf(args, "--seed");
var seed = seedIndex >= 0 && seedIndex + 1 < args.Length ? int.Parse(args[seedIndex + 1]) : DefaultSeed;

using var server = new GameServer(seed);
var (clientConnection, serverConnection) = LocalConnection.CreatePair<ClientMessage, ServerMessage>();
server.Connect(serverConnection);

// Порядок освобождения обратный: сначала закрывается окно, затем останавливается поток сервера,
// и только потом сервер (его фоновые потоки генерации).
using var host = new ServerHost(server);
host.Start();

using var app = new ClientApp(new WindowSettings { Title = product }, clientConnection, host);
app.Run();
