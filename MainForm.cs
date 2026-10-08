using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Anime4KScaler;

sealed class MainForm : Form
{
    readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(15, 17, 23) };
    bool ready; string pending;
    public bool AllowExit;
    public event Action<string> Command;

    public MainForm()
    {
        Text = "Anime4K Scaler";
        ClientSize = new Size(1000, 720);
        MinimumSize = new Size(860, 620);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(15, 17, 23);
        Icon = AppIcon.Get(new Size(32, 32));
        Controls.Add(web);
        InitWeb();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1;
        Native.DwmSetWindowAttribute(Handle, 20, ref on, 4);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!AllowExit && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        base.OnFormClosing(e);
    }

    async void InitWeb()
    {
        try
        {
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Anime4KScaler", "webview");
            var env = await CoreWebView2Environment.CreateAsync(null, data);
            await web.EnsureCoreWebView2Async(env);
            var s = web.CoreWebView2.Settings;
            s.AreDefaultContextMenusEnabled = false; s.IsZoomControlEnabled = false; s.IsStatusBarEnabled = false;
            web.CoreWebView2.WebMessageReceived += (o, a) => { var m = a.TryGetWebMessageAsString(); if (m != null) Command?.Invoke(m); };
            web.CoreWebView2.NavigationCompleted += (o, a) =>
            {
                ready = true;
                if (pending != null) { web.CoreWebView2.PostWebMessageAsString(pending); pending = null; }
            };
            web.CoreWebView2.NavigateToString(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Ui", "index.html")));
        }
        catch (Exception ex)
        {
            MessageBox.Show(Loc.T("webview_err") + ex.Message, "Anime4K");
        }
    }

    public void Post(string json)
    {
        if (ready && web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsString(json); else pending = json;
    }
}
