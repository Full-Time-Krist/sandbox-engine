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
var server = new GameServer();
var (clientConnection, serverConnection) = LocalConnection.CreatePair<ClientMessage, ServerMessage>();
server.Connect(serverConnection);

// Порядок освобождения обратный: сначала закрывается окно, затем останавливается сервер.
using var host = new ServerHost(server);
host.Start();

using var app = new ClientApp(new WindowSettings { Title = product }, clientConnection, host);
app.Run();
