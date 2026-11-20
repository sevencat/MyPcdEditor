using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGLES;

namespace MyPcdEditor.Rendering.Gles3;

/// <summary>着色器编译/链接失败。</summary>
public sealed class ShaderException(string message) : Exception(message);

/// <summary>
/// 一个 GL program（顶点 + 片元）。
/// </summary>
/// <remarks>
/// 与牛奶项目一致：uniform location 按名字缓存，热路径上不再每帧走驱动字符串查找。
/// 矩阵上传必须自己转置——GLES 3.0 规定 <c>transpose</c> 只能为 <c>false</c>（ADR-003）。
/// </remarks>
public sealed class Shader : IDisposable
{
    private readonly Dictionary<string, int> _uniformLocations = new(StringComparer.Ordinal);

    private uint _program;

    public Shader()
    {
        _program = GlContext.Gl.CreateProgram();
    }

    public uint ProgramId => _program;

    public void CompileProgram(string vertexShaderSource, string fragmentShaderSource)
    {
        var gl = GlContext.Gl;
        uint vertex = CompileShader(gl, vertexShaderSource, GLEnum.VertexShader);
        uint fragment = CompileShader(gl, fragmentShaderSource, GLEnum.FragmentShader);

        _uniformLocations.Clear();

        gl.AttachShader(_program, vertex);
        gl.AttachShader(_program, fragment);
        gl.LinkProgram(_program);

        gl.GetProgram(_program, GLEnum.LinkStatus, out int linked);
        gl.DetachShader(_program, vertex);
        gl.DetachShader(_program, fragment);
        gl.DeleteShader(vertex);
        gl.DeleteShader(fragment);

        if (linked == 0)
        {
            string log = gl.GetProgramInfoLog(_program);
            throw new ShaderException("program 链接失败：" + Environment.NewLine + log);
        }
    }

    public void Use() => GlContext.Gl.UseProgram(_program);

    public int GetUniformLocation(string name)
    {
        if (_uniformLocations.TryGetValue(name, out int location))
        {
            return location;
        }

        location = GlContext.Gl.GetUniformLocation(_program, name);
        _uniformLocations[name] = location;
        return location;
    }

    public void SetInt(string name, int value)
    {
        int location = GetUniformLocation(name);
        if (location >= 0)
        {
            GlContext.Gl.Uniform1(location, value);
        }
    }

    public void SetFloat(string name, float value)
    {
        int location = GetUniformLocation(name);
        if (location >= 0)
        {
            GlContext.Gl.Uniform1(location, value);
        }
    }

    public void SetVector2(string name, Vector2 value)
    {
        int location = GetUniformLocation(name);
        if (location >= 0)
        {
            GlContext.Gl.Uniform2(location, value);
        }
    }

    public void SetVector3(string name, Vector3 value)
    {
        int location = GetUniformLocation(name);
        if (location >= 0)
        {
            GlContext.Gl.Uniform3(location, value);
        }
    }

    /// <summary>上传 4x4 矩阵（内部转置成列主序）。</summary>
    public void SetMatrix4(string name, in Matrix4x4 matrix)
    {
        int location = GetUniformLocation(name);
        if (location < 0)
        {
            return;
        }

        Matrix4x4 columnMajor = Matrix4x4.Transpose(matrix);
        ReadOnlySpan<float> data = MemoryMarshal.CreateReadOnlySpan(ref columnMajor.M11, 16);
        GlContext.Gl.UniformMatrix4(location, 1, false, data);
    }

    private static uint CompileShader(GL gl, string source, GLEnum type)
    {
        uint shader = gl.CreateShader(type);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);
        gl.GetShader(shader, GLEnum.CompileStatus, out int compiled);

        if (compiled == 0)
        {
            string log = gl.GetShaderInfoLog(shader);
            gl.DeleteShader(shader);
            throw new ShaderException($"{type} 编译失败：" + Environment.NewLine + log);
        }

        return shader;
    }

    public void Dispose()
    {
        if (_program == 0)
        {
            return;
        }

        GlContext.Gl.DeleteProgram(_program);
        _program = 0;
        _uniformLocations.Clear();
    }
}
