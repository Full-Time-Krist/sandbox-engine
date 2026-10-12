using System.Reflection;
using Client;
using Engine.Windowing;

var product = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "Game";

using var app = new ClientApp(new WindowSettings { Title = product });
app.Run();
