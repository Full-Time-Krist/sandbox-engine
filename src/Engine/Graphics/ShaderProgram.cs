using System.Numerics;
using Silk.NET.OpenGL;

namespace Engine.Graphics;

/// <summary>Шейдерная программа из вершинного и фрагментного шейдеров.</summary>
public sealed unsafe class ShaderProgram : IDisposable
{
    private readonly GL _gl;
    private readonly Dictionary<string, int> _uniformLocations = new();

    public uint Handle { get; }

    public ShaderProgram(GL gl, string vertexSource, string fragmentSource, string name = "shader")
    {
        _gl = gl;

        var vertex = Compile(ShaderType.VertexShader, vertexSource, name);
        uint fragment;
        try
        {
            fragment = Compile(ShaderType.FragmentShader, fragmentSource, name);
        }
        catch
        {
            gl.DeleteShader(vertex);
            throw;
        }

        Handle = gl.CreateProgram();
        gl.AttachShader(Handle, vertex);
        gl.AttachShader(Handle, fragment);
        gl.LinkProgram(Handle);

        // После линковки исходные шейдеры больше не нужны.
        gl.DetachShader(Handle, vertex);
        gl.DetachShader(Handle, fragment);
        gl.DeleteShader(vertex);
        gl.DeleteShader(fragment);

        gl.GetProgram(Handle, ProgramPropertyARB.LinkStatus, out var linked);
        if (linked == 0)
        {
            var log = gl.GetProgramInfoLog(Handle);
            gl.DeleteProgram(Handle);
            throw new InvalidOperationException($"Ошибка линковки шейдера '{name}':\n{log}");
        }
    }

    /// <summary>Загружает пару файлов <c>{basePath}.vert</c> и <c>{basePath}.frag</c>.</summary>
    public static ShaderProgram FromFiles(GL gl, string basePath)
    {
        var vertex = File.ReadAllText(basePath + ".vert");
        var fragment = File.ReadAllText(basePath + ".frag");
        return new ShaderProgram(gl, vertex, fragment, Path.GetFileName(basePath));
    }

    public void Use() => _gl.UseProgram(Handle);

    // ProgramUniform* (DSA) пишет в программу напрямую, её не нужно делать активной.
    public void SetUniform(string name, float value) => _gl.ProgramUniform1(Handle, Location(name), value);
    public void SetUniform(string name, int value) => _gl.ProgramUniform1(Handle, Location(name), value);
    public void SetUniform(string name, Vector3 value) => _gl.ProgramUniform3(Handle, Location(name), value.X, value.Y, value.Z);

    public void SetUniform(string name, Matrix4x4 value)
    {
        // System.Numerics хранит матрицы построчно, GLSL читает их по столбцам.
        // Без транспонирования в GLSL придёт транспонированная матрица, поэтому порядок умножения
        // в шейдере (P * V * M) совпадает с порядком в C# (M * V * P).
        _gl.ProgramUniformMatrix4(Handle, Location(name), 1, false, (float*)&value);
    }

    private int Location(string name)
    {
        if (_uniformLocations.TryGetValue(name, out var location))
            return location;

        location = _gl.GetUniformLocation(Handle, name);
        if (location == -1)
            Console.WriteLine($"[Shader] uniform '{name}' не найден (опечатка или выброшен компилятором как неиспользуемый)");

        _uniformLocations[name] = location;
        return location;
    }

    private uint Compile(ShaderType type, string source, string name)
    {
        var shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, source);
        _gl.CompileShader(shader);

        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out var compiled);
        if (compiled == 0)
        {
            var log = _gl.GetShaderInfoLog(shader);
            _gl.DeleteShader(shader);
            throw new InvalidOperationException($"Ошибка компиляции {type} '{name}':\n{log}");
        }

        return shader;
    }

    public void Dispose() => _gl.DeleteProgram(Handle);
}
