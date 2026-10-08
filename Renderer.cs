using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using D3DBuffer = SharpDX.Direct3D11.Buffer;
using Resource = SharpDX.DXGI.Resource;

namespace Anime4KScaler;

public sealed class Renderer
{
    sealed class Phys { public Texture2D Tex; public ShaderResourceView Srv; public RenderTargetView Rtv; public int W, H; public bool Free; }
    sealed class VTex { public int W, H, Last = -1; public bool Ext; public Phys Phys; }
    sealed class Step { public HookPass P; public VTex[] Ins; public VTex Out; public PixelShader Ps; public D3DBuffer Cb; public ShaderResourceView[] Srvs; }
    sealed class Plan
    {
        public List<Step> Steps = new(); public List<Phys> Pool = new();
        public Texture2D NatTex; public ShaderResourceView NatSrv; public RenderTargetView NatRtv; public D3DBuffer DownCb;
        public ShaderResourceView NativeSrv, FinalSrv; public D3DBuffer BlitCb; public RenderInfo Meta;
    }

    const string VS = "float4 main(uint id:SV_VertexID):SV_Position{float2 p=float2((id<<1)&2,id&2);return float4(p*float2(2,-2)+float2(-1,1),0,1);}";
    const string PS_BLIT = "SamplerState s:register(s0);Texture2D<float4> t:register(t0);cbuffer c:register(b0){float2 o;float2 pad;};" +
                           "float4 main(float4 sv:SV_Position):SV_Target{return float4(t.SampleLevel(s,sv.xy/o,0).rgb,1);}";
    const string PS_DOWN = "SamplerState s:register(s0);Texture2D<float4> t:register(t0);cbuffer c:register(b0){float2 o;float2 src;};" +
                           "float4 main(float4 sv:SV_Position):SV_Target{float2 p=sv.xy/o;float2 d=0.25/o;" +
                           "return 0.25*(t.SampleLevel(s,p+float2(-d.x,-d.y),0)+t.SampleLevel(s,p+float2(d.x,-d.y),0)+t.SampleLevel(s,p+float2(-d.x,d.y),0)+t.SampleLevel(s,p+float2(d.x,d.y),0));}";

    readonly ShaderLibrary lib;
    readonly IntPtr hwnd, hmon;
    readonly Action<string, bool> status;
    readonly Action<RenderInfo> info;
    readonly Action failed;
    readonly object cfgLock = new();
    string cfgQ = "HQ"; int cfgMode = 1, cfgSrcH = 720; int ver;
    Thread th; volatile bool stop;

    int outW, outH;
    Device dev; DeviceContext ctx; Output1 out1; OutputDuplication dup; SwapChain sc; RenderTargetView bbRtv;
    VertexShader vs; PixelShader psBlit, psDown; SamplerState smp;
    Texture2D capTex; ShaderResourceView capSrv; int capW, capH; Format capFmt;
    Plan plan;
    readonly Dictionary<HookPass, PixelShader> psCache = new();
    static readonly ShaderResourceView[] Nulls = new ShaderResourceView[16];

    public Renderer(ShaderLibrary lib, IntPtr overlayHwnd, IntPtr monitor, Action<string, bool> status, Action<RenderInfo> info, Action failed)
    { this.lib = lib; hwnd = overlayHwnd; hmon = monitor; this.status = status; this.info = info; this.failed = failed; }

    public void SetConfig(string quality, int mode, int sourceHeight)
    {
        lock (cfgLock) { cfgQ = quality; cfgMode = mode; cfgSrcH = sourceHeight; }
        Interlocked.Increment(ref ver);
    }

    public void Start() { th = new Thread(Run) { IsBackground = true, Name = "Anime4K-Render" }; th.Start(); }
    public void Stop() { stop = true; th?.Join(8000); }

    static string Flatten(Exception e) { while (e.InnerException != null) e = e.InnerException; return e.Message; }

    void Run()
    {
        try { Init(); Loop(); }
        catch (Exception e) { if (!stop) { status(Loc.T("err_prefix") + Flatten(e), true); failed(); } }
        finally { Cleanup(); }
    }

    void Init()
    {
        Adapter1 adapter = null; Output output = null;
        using (var fac = new Factory1())
        {
            foreach (var ad in fac.Adapters1)
            {
                foreach (var o in ad.Outputs) if (o.Description.MonitorHandle == hmon) { adapter = ad; output = o; break; }
                if (output != null) break;
            }
        }
        if (output == null) throw new Exception(Loc.T("mon_nf"));
        var b = output.Description.DesktopBounds; outW = b.Right - b.Left; outH = b.Bottom - b.Top;

        dev = new Device(adapter, DeviceCreationFlags.BgraSupport);
        ctx = dev.ImmediateContext;
        out1 = output.QueryInterface<Output1>();
        try { dup = out1.DuplicateOutput(dev); }
        catch (SharpDXException ex) { throw new Exception(Loc.F("cap_fail", ex.Message)); }

        var desc = new SwapChainDescription
        {
            BufferCount = 1,
            ModeDescription = new ModeDescription(outW, outH, new Rational(0, 1), Format.B8G8R8A8_UNorm),
            IsWindowed = true, OutputHandle = hwnd, SampleDescription = new SampleDescription(1, 0),
            SwapEffect = SwapEffect.Discard, Usage = Usage.RenderTargetOutput,
        };
        using (var fac2 = new Factory1())
        {
            sc = new SwapChain(fac2, dev, desc);
            fac2.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAll);
        }
        using (var bb = sc.GetBackBuffer<Texture2D>(0)) bbRtv = new RenderTargetView(dev, bb);

        vs = new VertexShader(dev, Compile(VS, "vs_5_0"));
        psBlit = new PixelShader(dev, Compile(PS_BLIT, "ps_5_0"));
        psDown = new PixelShader(dev, Compile(PS_DOWN, "ps_5_0"));
        smp = new SamplerState(dev, new SamplerStateDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp,
            ComparisonFunction = Comparison.Never, MinimumLod = 0, MaximumLod = float.MaxValue,
        });
    }

    static byte[] Compile(string hlsl, string profile)
    {
        var r = ShaderBytecode.Compile(hlsl, "main", profile, ShaderFlags.OptimizationLevel3);
        if (r.HasErrors || r.Bytecode == null) throw new Exception(r.Message);
        return r.Bytecode.Data;
    }

    void Loop()
    {
        int seen = -1;
        while (!stop)
        {
            bool fresh = false;
            var r = dup.TryAcquireNextFrame(100, out OutputDuplicateFrameInformation info, out Resource res);
            if (r.Failure)
            {
                int code = r.Code;
                if (code == SharpDX.DXGI.ResultCode.WaitTimeout.Result.Code) { }
                else if (code == SharpDX.DXGI.ResultCode.AccessLost.Result.Code) { Thread.Sleep(150); dup.Dispose(); dup = out1.DuplicateOutput(dev); continue; }
                else r.CheckError();
            }
            else
            {
                try
                {
                    if (info.AccumulatedFrames > 0 || capTex == null)
                    {
                        using (var t = res.QueryInterface<Texture2D>()) { EnsureCap(t); ctx.CopyResource(t, capTex); }
                        fresh = true;
                    }
                }
                finally { res.Dispose(); dup.ReleaseFrame(); }
            }
            if (capTex == null) continue;
            int v = Volatile.Read(ref ver);
            if (v != seen || plan == null) { Rebuild(); seen = v; fresh = true; }
            if (fresh) Draw();
        }
    }

    void EnsureCap(Texture2D t)
    {
        var d = t.Description;
        if (capTex != null && capW == d.Width && capH == d.Height && capFmt == d.Format) return;
        DisposePlan();
        capSrv?.Dispose(); capTex?.Dispose();
        d.BindFlags = BindFlags.ShaderResource; d.CpuAccessFlags = CpuAccessFlags.None;
        d.OptionFlags = ResourceOptionFlags.None; d.Usage = ResourceUsage.Default;
        capTex = new Texture2D(dev, d); capSrv = new ShaderResourceView(dev, capTex);
        capW = d.Width; capH = d.Height; capFmt = d.Format;
    }

    void Rebuild()
    {
        DisposePlan();
        string q; int m, sh;
        lock (cfgLock) { q = cfgQ; m = cfgMode; sh = cfgSrcH; }
        if (m > 0) status(Loc.T("compiling"), false);
        try { plan = BuildPlan(q, m, sh); }
        catch (Exception e)
        {
            DisposePlan();
            status(Loc.T("err_prefix") + Flatten(e) + " — " + Loc.T("fallback_orig"), true);
            plan = BuildPlan(q, 0, sh);
            info(plan.Meta);
            return;
        }
        info(plan.Meta);
    }

    Plan BuildPlan(string q, int m, int srcH)
    {
        var pl = new Plan();
        int nw = capW, nh = capH;
        if (m > 0 && srcH > 0 && srcH < capH) { nh = srcH; nw = (int)Math.Round((double)capW * srcH / capH); }
        if (nw != capW)
        {
            pl.NatTex = MakeTex(nw, nh); pl.NatSrv = new ShaderResourceView(dev, pl.NatTex); pl.NatRtv = new RenderTargetView(dev, pl.NatTex);
            pl.DownCb = MakeCb(new float[] { nw, nh, capW, capH }); pl.NativeSrv = pl.NatSrv;
        }
        else pl.NativeSrv = capSrv;
        pl.BlitCb = MakeCb(new float[] { outW, outH, 0, 0 });

        var native = new VTex { W = nw, H = nh, Ext = true };
        var main = native;
        var names = new Dictionary<string, VTex>();
        var steps = pl.Steps;

        if (m > 0)
        {
            var chain = lib.Chain(q, m);
            (double w, double h) Sz(string n)
            {
                if (n == "OUTPUT") return (outW, outH);
                if (n == "NATIVE") return (native.W, native.H);
                if (n == "MAIN" || n == "HOOKED") return (main.W, main.H);
                if (names.TryGetValue(n, out var t)) return (t.W, t.H);
                throw new KeyNotFoundException(n);
            }
            foreach (var stage in new[] { "MAIN", "PREKERNEL" })
                foreach (var p in chain)
                {
                    if (p.Hook != stage) continue;
                    try
                    {
                        if (p.When != null && !(Hooks.Rpn(p.When, Sz) > 0)) continue;
                        var ins = new VTex[p.Aliases.Length]; bool ok = true;
                        for (int i = 0; i < ins.Length; i++)
                        {
                            string a = p.Aliases[i];
                            if (a == "HOOKED" || a == "MAIN" || a == p.Hook) ins[i] = main;
                            else if (a == "NATIVE") ins[i] = native;
                            else if (!names.TryGetValue(a, out ins[i])) { ok = false; break; }
                        }
                        if (!ok) continue;
                        int w = Math.Max(1, (int)Math.Round(p.W != null ? Hooks.Rpn(p.W, Sz) : main.W));
                        int h = Math.Max(1, (int)Math.Round(p.H != null ? Hooks.Rpn(p.H, Sz) : main.H));
                        var o = new VTex { W = w, H = h };
                        steps.Add(new Step { P = p, Ins = ins, Out = o });
                        if (p.Save != null && p.Save != "MAIN") names[p.Save] = o; else main = o;
                    }
                    catch (KeyNotFoundException) { }
                }

            try { Parallel.ForEach(steps.Select(s => s.P).Distinct().ToList(), p => Hooks.GetBytecode(p)); }
            catch (AggregateException ae) { throw ae.InnerException ?? ae; }
        }

        for (int i = 0; i < steps.Count; i++)
        {
            foreach (var t in steps[i].Ins) t.Last = i;
            steps[i].Out.Last = Math.Max(steps[i].Out.Last, i);
        }
        main.Last = int.MaxValue;
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i]; var o = s.Out;
            var ph = pl.Pool.FirstOrDefault(x => x.Free && x.W == o.W && x.H == o.H);
            if (ph == null) { ph = NewPhys(o.W, o.H); pl.Pool.Add(ph); }
            ph.Free = false; o.Phys = ph;
            foreach (var t in s.Ins.Append(o).Distinct()) if (!t.Ext && t.Last == i) t.Phys.Free = true;
        }
        foreach (var s in steps)
        {
            if (!psCache.TryGetValue(s.P, out var ps)) psCache[s.P] = ps = new PixelShader(dev, Hooks.GetBytecode(s.P));
            s.Ps = ps;
            var f = new float[((2 + 2 * s.Ins.Length + 3) / 4) * 4];
            f[0] = s.Out.W; f[1] = s.Out.H;
            for (int i = 0; i < s.Ins.Length; i++) { f[2 + 2 * i] = s.Ins[i].W; f[3 + 2 * i] = s.Ins[i].H; }
            s.Cb = MakeCb(f);
            s.Srvs = s.Ins.Select(t => t.Ext ? pl.NativeSrv : t.Phys.Srv).ToArray();
        }
        pl.FinalSrv = main.Ext ? pl.NativeSrv : main.Phys.Srv;
        pl.Meta = new RenderInfo(q, m, nw, nh, outW, outH, steps.Count);
        return pl;
    }

    Texture2D MakeTex(int w, int h)
    {
        if (w < 1 || h < 1 || w > 16384 || h > 16384) throw new Exception(Loc.F("bad_tex", w, h));
        return new Texture2D(dev, new Texture2DDescription
        {
            Width = w, Height = h, MipLevels = 1, ArraySize = 1, Format = Format.R16G16B16A16_Float,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget, CpuAccessFlags = CpuAccessFlags.None, OptionFlags = ResourceOptionFlags.None,
        });
    }

    Phys NewPhys(int w, int h)
    {
        var t = MakeTex(w, h);
        return new Phys { Tex = t, Srv = new ShaderResourceView(dev, t), Rtv = new RenderTargetView(dev, t), W = w, H = h };
    }

    D3DBuffer MakeCb(float[] data) => D3DBuffer.Create(dev, BindFlags.ConstantBuffer, data);

    void Pass(PixelShader ps, D3DBuffer cb, ShaderResourceView[] srvs, RenderTargetView rtv, int w, int h)
    {
        ctx.OutputMerger.SetRenderTargets(rtv);
        ctx.Rasterizer.SetViewport(0, 0, w, h);
        ctx.PixelShader.Set(ps);
        ctx.PixelShader.SetConstantBuffer(0, cb);
        ctx.PixelShader.SetShaderResources(0, srvs);
        ctx.Draw(3, 0);
        ctx.PixelShader.SetShaderResources(0, Nulls);
    }

    void Draw()
    {
        var p = plan; if (p == null) return;
        ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        ctx.VertexShader.Set(vs);
        ctx.PixelShader.SetSampler(0, smp);
        if (p.NatRtv != null) Pass(psDown, p.DownCb, new[] { capSrv }, p.NatRtv, p.NatTex.Description.Width, p.NatTex.Description.Height);
        foreach (var s in p.Steps) Pass(s.Ps, s.Cb, s.Srvs, s.Out.Phys.Rtv, s.Out.W, s.Out.H);
        Pass(psBlit, p.BlitCb, new[] { p.FinalSrv }, bbRtv, outW, outH);
        sc.Present(1, PresentFlags.None);
    }

    void DisposePlan()
    {
        if (plan == null) return;
        foreach (var s in plan.Steps) s.Cb?.Dispose();
        foreach (var x in plan.Pool) { x.Rtv.Dispose(); x.Srv.Dispose(); x.Tex.Dispose(); }
        plan.DownCb?.Dispose(); plan.BlitCb?.Dispose();
        plan.NatRtv?.Dispose(); plan.NatSrv?.Dispose(); plan.NatTex?.Dispose();
        plan = null;
    }

    void Cleanup()
    {
        try { ctx?.ClearState(); } catch { }
        DisposePlan();
        foreach (var p in psCache.Values) p.Dispose();
        psCache.Clear();
        capSrv?.Dispose(); capTex?.Dispose();
        smp?.Dispose(); psDown?.Dispose(); psBlit?.Dispose(); vs?.Dispose();
        bbRtv?.Dispose(); sc?.Dispose(); dup?.Dispose(); out1?.Dispose(); ctx?.Dispose(); dev?.Dispose();
    }
}
