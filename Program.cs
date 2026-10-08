using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;
using System.Windows.Forms;

namespace Anime4KScaler;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "Anime4KScaler_SingleInstance", out bool first);
        if (!first) return;
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.Run(new App(args));
    }
}

sealed class OverlayForm : Form
{
    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black; AutoScaleMode = AutoScaleMode.None;
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
            return cp;
        }
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetLayeredWindowAttributes(Handle, 0, 255, 2);
        if (!Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE))
            MessageBox.Show(Loc.T("aff_unsupported"), "Anime4K");
    }
    public void ShowOn(Rectangle r)
    {
        if (!Visible) { Bounds = r; Show(); }
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, r.X, r.Y, r.Width, r.Height, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }
}

sealed class OsdForm : Form
{
    readonly Label lbl = new() { AutoSize = true, ForeColor = Color.White, Font = new Font("Segoe UI", 15, FontStyle.Bold), Padding = new Padding(14, 8, 14, 8) };
    readonly System.Windows.Forms.Timer timer = new();
    public OsdForm()
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(24, 24, 32); Opacity = 0.92; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Controls.Add(lbl);
        timer.Tick += (s, e) => { timer.Stop(); Hide(); };
        _ = Handle;
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
            return cp;
        }
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE); }
    public void ShowText(string text, Rectangle mon, int ms)
    {
        lbl.Text = text;
        if (!Visible) Show();
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, mon.Left + 32, mon.Top + 32, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        timer.Stop(); timer.Interval = ms; timer.Start();
    }
}

sealed class HotkeyWindow : NativeWindow
{
    public event Action<int> Pressed;
    public HotkeyWindow() { CreateHandle(new CreateParams()); }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY) Pressed?.Invoke((int)m.WParam);
        base.WndProc(ref m);
    }
}

sealed class App : ApplicationContext
{
    readonly record struct Fs(IntPtr Hwnd, IntPtr Mon, Rectangle Bounds);

    const int HK_TOGGLE = 1, HK_SOURCE = 10, HK_MODE0 = 20, HK_QUALITY = 30;
    static readonly int[] SourceHeights = { 720, 480, 540, 1080, 0 };

    readonly Settings S = Settings.Load();
    readonly ShaderLibrary lib = new(System.IO.Path.Combine(AppContext.BaseDirectory, "Shaders"));
    readonly OverlayForm overlay = new();
    readonly OsdForm osd = new();
    readonly HotkeyWindow hk = new();
    readonly NotifyIcon tray = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 300 };
    bool enabled, active, lastErr;
    readonly MainForm win = new();
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    Renderer renderer; IntPtr curMon; Rectangle curBounds;
    RenderInfo lastInfo; string lastMsg = "";
    bool balloonShown;

    public App(string[] args)
    {
        Loc.Lang = S.Language; enabled = S.Enabled;
        win.Command += OnUiCommand;
        win.FormClosing += (s, e) => { if (!win.AllowExit && e.CloseReason == CloseReason.UserClosing && !balloonShown) { balloonShown = true; tray.ShowBalloonTip(2500, "Anime4K Scaler", Loc.T("balloon"), ToolTipIcon.Info); } };
        if (!args.Contains("--tray")) win.Show();
        tray.DoubleClick += (s, e) => ShowWindow();
        _ = overlay.Handle;
        hk.Pressed += OnHotkey;
        Native.RegisterHotKey(hk.Handle, HK_TOGGLE, Native.MOD_NOREPEAT, 0x78);

        tray.Icon = AppIcon.Get(SystemInformation.SmallIconSize); tray.Text = "Anime4K Scaler"; tray.Visible = true;
        tray.ContextMenuStrip = new ContextMenuStrip();
        tray.ContextMenuStrip.Opening += (s, e) => BuildMenu(tray.ContextMenuStrip);

        timer.Tick += (s, e) => Tick();
        timer.Start();
        if (!win.Visible) Osd(Loc.T("ready_osd"), 4000);
    }

    void ShowWindow() { win.Show(); win.WindowState = FormWindowState.Normal; win.Activate(); PushState(); }

    Fs? FindFullscreen()
    {
        var h = Native.GetForegroundWindow();
        if (h == IntPtr.Zero || !Native.IsWindowVisible(h) || Native.IsIconic(h)) return null;
        Native.GetWindowThreadProcessId(h, out uint pid);
        if (pid == Environment.ProcessId) return null;
        var sb = new StringBuilder(64); Native.GetClassName(h, sb, 64);
        string cn = sb.ToString();
        if (cn is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return null;
        Native.GetWindowRect(h, out var r);
        var mon = Native.MonitorFromWindow(h, 2);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!Native.GetMonitorInfo(mon, ref mi)) return null;
        var m = mi.rcMonitor;
        if (r.Left != m.Left || r.Top != m.Top || r.Right != m.Right || r.Bottom != m.Bottom) return null;
        return new Fs(h, mon, Rectangle.FromLTRB(m.Left, m.Top, m.Right, m.Bottom));
    }

    void Tick()
    {
        var fs = enabled ? FindFullscreen() : null;
        if (!active) { if (fs != null) Activate(fs.Value); }
        else if (fs == null || fs.Value.Mon != curMon) Deactivate();
    }

    void Activate(Fs fs)
    {
        try
        {
            overlay.ShowOn(fs.Bounds);
            curMon = fs.Mon; curBounds = fs.Bounds;
            renderer = new Renderer(lib, overlay.Handle, fs.Mon, Status, OnInfo, OnFailed);
            renderer.SetConfig(S.Quality, S.Mode, S.SourceHeight);
            renderer.Start();
            active = true;
            RegisterModeKeys(true);
            PushState();
        }
        catch (Exception e) { enabled = false; overlay.Hide(); lastMsg = Loc.T("err_prefix") + e.Message; lastErr = true; Osd(lastMsg, 8000); PushState(); }
    }

    void Deactivate()
    {
        if (!active) return;
        active = false;
        RegisterModeKeys(false);
        var r = renderer; renderer = null;
        r?.Stop();
        overlay.Hide();
        lastInfo = null;
        PushState();
    }

    void Status(string s, bool isError)
    {
        if (osd.IsHandleCreated) osd.BeginInvoke(new Action(() =>
        {
            lastMsg = s; lastErr = isError;
            Osd(s, isError ? 8000 : 2500);
            PushState();
        }));
    }
    void OnInfo(RenderInfo i)
    {
        if (osd.IsHandleCreated) osd.BeginInvoke(new Action(() =>
        {
            lastInfo = i;
            if (!lastErr) { lastMsg = ""; Osd(InfoText(i), 2500); }
            PushState();
        }));
    }
    static string InfoText(RenderInfo i) =>
        $"{Loc.Label(i.Quality, i.Mode)} · {Loc.T("src_word")} {i.SrcW}×{i.SrcH} → {i.OutW}×{i.OutH} · {i.Steps} {Loc.T("passes")}" +
        (i.Mode > 0 && i.Steps == 0 ? $" ({Loc.T("no_upscale")})" : "");
    void OnFailed()
    {
        if (osd.IsHandleCreated) osd.BeginInvoke(new Action(() => { enabled = false; Deactivate(); PushState(); }));
    }

    void Osd(string text, int ms = 2000)
    {
        var b = active ? curBounds : (Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 800, 600));
        osd.ShowText(text, b, ms);
    }

    void RegisterModeKeys(bool on)
    {
        uint mod = Native.MOD_ALT | Native.MOD_NOREPEAT;
        for (int n = 0; n <= 6; n++)
        {
            if (on) Native.RegisterHotKey(hk.Handle, HK_MODE0 + n, mod, (uint)(0x30 + n)); else Native.UnregisterHotKey(hk.Handle, HK_MODE0 + n);
        }
        if (on) { Native.RegisterHotKey(hk.Handle, HK_QUALITY, mod, 0x51); Native.RegisterHotKey(hk.Handle, HK_SOURCE, Native.MOD_NOREPEAT, 0x79); }
        else { Native.UnregisterHotKey(hk.Handle, HK_QUALITY); Native.UnregisterHotKey(hk.Handle, HK_SOURCE); }
    }

    void OnHotkey(int id)
    {
        if (id == HK_TOGGLE)
        {
            SetEnabled(!enabled);
            Osd(Loc.T(enabled ? "osd_on" : "osd_off"), 1500);
        }
        else if (id == HK_SOURCE) CycleSource();
        else if (id == HK_QUALITY) { S.Quality = S.Quality == "HQ" ? "FAST" : "HQ"; Apply(); }
        else if (id >= HK_MODE0 && id <= HK_MODE0 + 6) { S.Mode = id - HK_MODE0; Apply(); }
    }

    void SetEnabled(bool v) { enabled = v; S.Enabled = v; S.Save(); if (v) { lastMsg = ""; lastErr = false; } Tick(); PushState(); }

    void SetLanguage(string l) { S.Language = l == "en" ? "en" : "tr"; Loc.Lang = S.Language; S.Save(); PushState(); }

    void CycleSource()
    {
        int i = Array.IndexOf(SourceHeights, S.SourceHeight);
        S.SourceHeight = SourceHeights[(i + 1) % SourceHeights.Length];
        Apply();
    }

    void Apply()
    {
        S.Save();
        if (renderer != null) renderer.SetConfig(S.Quality, S.Mode, S.SourceHeight);
        else if (!win.Visible) Osd(Loc.Label(S.Quality, S.Mode) + " · " + Loc.T("src_word") + " " + (S.SourceHeight == 0 ? Loc.T("screen") : S.SourceHeight + "p"));
        PushState();
    }

    void PushState()
    {
        var st = new
        {
            type = "state", enabled, active, mode = S.Mode, quality = S.Quality, sourceHeight = S.SourceHeight,
            language = S.Language, autostart = Autostart.Get(),
            info = lastInfo, message = lastMsg, messageIsError = lastErr, modeNames = Modes.Names,
        };
        win.Post(JsonSerializer.Serialize(st, Json));
    }

    void OnUiCommand(string json)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            var r = d.RootElement;
            switch (r.GetProperty("cmd").GetString())
            {
                case "ready": PushState(); break;
                case "enabled": SetEnabled(r.GetProperty("value").GetBoolean()); break;
                case "mode": S.Mode = r.GetProperty("value").GetInt32(); Apply(); break;
                case "quality": S.Quality = r.GetProperty("value").GetString() == "FAST" ? "FAST" : "HQ"; Apply(); break;
                case "sourceHeight": S.SourceHeight = r.GetProperty("value").GetInt32(); Apply(); break;
                case "language": SetLanguage(r.GetProperty("value").GetString()); break;
                case "autostart": Autostart.Set(r.GetProperty("value").GetBoolean()); PushState(); break;
                case "hide": win.Hide(); break;
                case "quit": Quit(); break;
            }
        }
        catch (Exception e) { lastMsg = Loc.T("err_prefix") + e.Message; lastErr = true; PushState(); }
    }

    void BuildMenu(ContextMenuStrip m)
    {
        m.Items.Clear();
        ToolStripMenuItem Item(string t, bool chk, Action a) { var i = new ToolStripMenuItem(t) { Checked = chk }; i.Click += (s, e) => a(); return i; }
        m.Items.Add(Item(Loc.T("tray_open"), false, ShowWindow));
        m.Items.Add(Item(Loc.T("tray_running"), enabled, () => OnHotkey(HK_TOGGLE)));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(Item(Loc.T("tray_orig"), S.Mode == 0, () => { S.Mode = 0; Apply(); }));
        for (int i = 1; i <= 6; i++) { int k = i; m.Items.Add(Item($"{Loc.T("mode")} {Modes.Names[i - 1]} (Alt+{i})", S.Mode == i, () => { S.Mode = k; Apply(); })); }
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(Item(Loc.T("hq"), S.Quality == "HQ", () => { S.Quality = "HQ"; Apply(); }));
        m.Items.Add(Item(Loc.T("fast"), S.Quality == "FAST", () => { S.Quality = "FAST"; Apply(); }));
        m.Items.Add(new ToolStripSeparator());
        var src = new ToolStripMenuItem(Loc.T("tray_src"));
        foreach (var h in SourceHeights) { int k = h; src.DropDownItems.Add(Item(k == 0 ? Loc.T("screen_noscale") : k + "p", S.SourceHeight == k, () => { S.SourceHeight = k; Apply(); })); }
        m.Items.Add(src);
        m.Items.Add(new ToolStripSeparator());
        var lang = new ToolStripMenuItem(Loc.T("tray_lang"));
        lang.DropDownItems.Add(Item("Türkçe", S.Language == "tr", () => SetLanguage("tr")));
        lang.DropDownItems.Add(Item("English", S.Language == "en", () => SetLanguage("en")));
        m.Items.Add(lang);
        m.Items.Add(Item(Loc.T("tray_quit"), false, Quit));
    }

    void Quit()
    {
        timer.Stop();
        Native.UnregisterHotKey(hk.Handle, HK_TOGGLE);
        Deactivate();
        tray.Visible = false; tray.Dispose();
        win.AllowExit = true; win.Close();
        ExitThread();
    }
}

static class Autostart
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "Anime4KScaler";
    public static bool Get()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(Key); return k?.GetValue(Name) != null; } catch { return false; }
    }
    public static void Set(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key, true);
            if (on) k.SetValue(Name, "\"" + Environment.ProcessPath + "\" --tray"); else k.DeleteValue(Name, false);
        }
        catch { }
    }
}
