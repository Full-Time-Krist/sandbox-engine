using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace Engine.Graphics;

/// <summary>
/// Включает отладочный вывод OpenGL: драйвер сам сообщает об ошибках и проблемах,
/// поэтому не нужно вызывать glGetError после каждой команды.
/// </summary>
internal static unsafe class GlDebug
{
    // Делегат хранится в статическом поле: драйвер держит на него только нативный указатель,
    // и без этой ссылки сборщик мусора удалил бы делегат.
    private static readonly DebugProc Callback = OnMessage;

    public static void Enable(GL gl)
    {
        gl.Enable(EnableCap.DebugOutput);
        // Сообщение приходит в том же потоке сразу после ошибочного вызова, поэтому стек вызовов указывает на виновника.
        gl.Enable(EnableCap.DebugOutputSynchronous);
        gl.DebugMessageCallback(Callback, null);
    }

    private static void OnMessage(GLEnum source, GLEnum type, int id, GLEnum severity, int length, nint message, nint userParam)
    {
        if (severity == GLEnum.DebugSeverityNotification)
            return;

        var text = Marshal.PtrToStringUTF8(message, length);
        Console.WriteLine($"[GL {severity} {type}] {text}");
    }
}
