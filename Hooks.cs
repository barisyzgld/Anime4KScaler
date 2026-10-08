using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SharpDX.D3DCompiler;

namespace Anime4KScaler;

public sealed class HookPass
{
    public string Hook, Save, W, H, When;
    public StringBuilder Body = new();
    public List<string> Binds = new();
    public bool HasCode;
    public string[] Aliases;
    public string Hlsl;
    public byte[] Bytecode;
}

public static class Hooks
{
    public static List<HookPass> Parse(string src)
    {
        var list = new List<HookPass>();
        HookPass c = null;
        foreach (var line in src.Replace("\r", "").Split('\n'))
        {
            if (line.StartsWith("//!"))
            {
                int sp = line.IndexOf(' ');
                string k = sp < 0 ? line.Substring(3) : line.Substring(3, sp - 3);
                string v = sp < 0 ? "" : line.Substring(sp + 1).Trim();
                if (c == null || c.HasCode) { c = new HookPass(); list.Add(c); }
                switch (k)
                {
                    case "BIND": c.Binds.Add(v); break;
                    case "HOOK": c.Hook = v; break;
                    case "SAVE": c.Save = v; break;
                    case "WIDTH": c.W = v; break;
                    case "HEIGHT": c.H = v; break;
                    case "WHEN": c.When = v; break;
                }
            }
            else if (c != null)
            {
                c.Body.AppendLine(line);
                var t = line.Trim();
                if (t.Length > 0 && !t.StartsWith("//")) c.HasCode = true;
            }
        }
        var res = list.Where(p => p.Hook != null && p.HasCode).ToList();
        foreach (var p in res)
        {
            p.Aliases = new[] { "HOOKED", p.Hook }.Concat(p.Binds).Distinct().ToArray();
            p.Hlsl = ToHlsl(p);
        }
        return res;
    }

    static string ToHlsl(HookPass p)
    {
        var sb = new StringBuilder();
        sb.Append("#define vec2 float2\n#define vec3 float3\n#define vec4 float4\n#define ivec2 int2\n#define fract frac\n");
        sb.Append("SamplerState _smp:register(s0);\ncbuffer _cb:register(b0){float2 _out;");
        foreach (var a in p.Aliases) sb.Append($"float2 {a}_sz;");
        sb.Append("};\nstatic float2 _pos;\n");
        for (int i = 0; i < p.Aliases.Length; i++)
        {
            string a = p.Aliases[i];
            sb.Append($"Texture2D<float4> {a}_raw:register(t{i});\n");
            sb.Append($"#define {a}_size {a}_sz\n#define {a}_pos _pos\n#define {a}_pt (float2(1.0,1.0)/{a}_sz)\n");
            sb.Append($"#define {a}_tex(p) {a}_raw.SampleLevel(_smp,p,0)\n#define {a}_texOff(o) {a}_tex(_pos+{a}_pt*vec2(o))\n");
        }
        string body = p.Body.ToString();
        body = Regex.Replace(body, @"\bvec([234])\(\s*(-?[0-9.]+)\s*\)", "((float$1)$2)");
        body = Regex.Replace(body, @"(vec4 result =|result \+=)\s*mat4\(([^;]*?)\)\s*\*\s*([^;]*);", "$1 mul($3, float4x4($2));");
        sb.Append(body);
        sb.Append("\nfloat4 main(float4 sv:SV_Position):SV_Target{_pos=sv.xy/_out;return hook();}\n");
        return sb.ToString();
    }

    public static double Rpn(string expr, Func<string, (double w, double h)> size)
    {
        var st = new Stack<double>();
        foreach (var t in expr.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) st.Push(v);
            else if (t.Length == 1 && "+-*/><".IndexOf(t[0]) >= 0)
            {
                double b = st.Pop(), a = st.Pop();
                st.Push(t[0] switch { '+' => a + b, '-' => a - b, '*' => a * b, '/' => a / b, '>' => a > b ? 1 : 0, _ => a < b ? 1 : 0 });
            }
            else
            {
                int i = t.IndexOf('.');
                var s = size(t.Substring(0, i));
                st.Push(t.Substring(i + 1) == "w" ? s.w : s.h);
            }
        }
        return st.Pop();
    }

    public static byte[] GetBytecode(HookPass p)
    {
        lock (p)
        {
            if (p.Bytecode != null) return p.Bytecode;
            string key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes("v1" + p.Hlsl)));
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Anime4KScaler", "cache");
            string file = Path.Combine(dir, key + ".cso");
            try { if (File.Exists(file)) return p.Bytecode = File.ReadAllBytes(file); } catch { }
            var r = ShaderBytecode.Compile(p.Hlsl, "main", "ps_5_0", ShaderFlags.OptimizationLevel3);
            if (r.HasErrors || r.Bytecode == null) throw new Exception(Loc.T("hlsl_err") + r.Message);
            p.Bytecode = r.Bytecode.Data;
            try { Directory.CreateDirectory(dir); File.WriteAllBytes(file, p.Bytecode); } catch { }
            return p.Bytecode;
        }
    }
}

public sealed class ShaderLibrary
{
    readonly string dir;
    readonly Dictionary<string, List<HookPass>> cache = new();
    public ShaderLibrary(string dir) { this.dir = dir; }

    public List<HookPass> Chain(string quality, int mode)
    {
        var all = new List<HookPass>();
        foreach (var key in Modes.Chain(quality, mode))
        {
            string name = Modes.Files[key];
            lock (cache)
            {
                if (!cache.TryGetValue(name, out var passes))
                {
                    string path = Path.Combine(dir, name + ".glsl");
                    if (!File.Exists(path)) throw new FileNotFoundException(Loc.T("shader_nf") + path);
                    cache[name] = passes = Hooks.Parse(File.ReadAllText(path));
                }
                all.AddRange(passes);
            }
        }
        return all;
    }
}
