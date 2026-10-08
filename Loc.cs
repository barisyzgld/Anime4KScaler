using System.Collections.Generic;

namespace Anime4KScaler;

public sealed record RenderInfo(string Quality, int Mode, int SrcW, int SrcH, int OutW, int OutH, int Steps);

public static class Loc
{
    public static string Lang = "tr";

    static readonly Dictionary<string, (string tr, string en)> D = new()
    {
        ["compiling"] = ("Shader'lar hazırlanıyor (ilk seferde birkaç saniye sürebilir)…", "Preparing shaders (may take a few seconds the first time)…"),
        ["err_prefix"] = ("Hata: ", "Error: "),
        ["fallback_orig"] = ("orijinal görüntüye dönüldü", "fell back to original image"),
        ["mon_nf"] = ("Monitör/GPU eşleşmesi bulunamadı.", "Could not match the monitor to a GPU."),
        ["cap_fail"] = ("Ekran yakalama başlatılamadı ({0}). Başka bir yakalama uygulaması açık olabilir.", "Screen capture could not start ({0}). Another capture app may be running."),
        ["bad_tex"] = ("Geçersiz texture boyutu {0}x{1}", "Invalid texture size {0}x{1}"),
        ["hlsl_err"] = ("HLSL derleme hatası: ", "HLSL compile error: "),
        ["shader_nf"] = ("Shader bulunamadı: ", "Shader not found: "),
        ["aff_unsupported"] = ("Bu Windows sürümü pencereyi yakalamadan hariç tutmayı desteklemiyor (Windows 10 2004 / build 19041 veya üstü gerekir).", "This Windows version cannot exclude a window from capture (Windows 10 2004 / build 19041 or newer is required)."),
        ["ready_osd"] = ("Anime4K hazır · tam ekran video bekleniyor · F9: başlat/durdur", "Anime4K ready · waiting for a fullscreen video · F9: start/stop"),
        ["osd_on"] = ("Anime4K BAŞLADI", "Anime4K STARTED"),
        ["osd_off"] = ("Anime4K DURDURULDU", "Anime4K STOPPED"),
        ["balloon"] = ("Tepside çalışmaya devam ediyor. Tam ekran video açınca otomatik devreye girer.", "Still running in the tray. It kicks in automatically when a video goes fullscreen."),
        ["webview_err"] = ("Arayüz başlatılamadı. Microsoft Edge WebView2 Runtime kurulu olmalı:\nhttps://developer.microsoft.com/microsoft-edge/webview2/\n\n", "The UI could not start. Microsoft Edge WebView2 Runtime must be installed:\nhttps://developer.microsoft.com/microsoft-edge/webview2/\n\n"),
        ["tray_open"] = ("Pencereyi aç", "Open window"),
        ["tray_running"] = ("Çalışıyor (F9)", "Running (F9)"),
        ["tray_orig"] = ("Orijinal (Alt+0)", "Original (Alt+0)"),
        ["tray_src"] = ("Kaynak video çözünürlüğü (F10)", "Source video resolution (F10)"),
        ["tray_quit"] = ("Çıkış", "Exit"),
        ["tray_lang"] = ("Dil", "Language"),
        ["mode"] = ("Mod", "Mode"),
        ["label_orig"] = ("Orijinal (Anime4K kapalı)", "Original (Anime4K off)"),
        ["hq"] = ("Yüksek kalite", "High quality"),
        ["fast"] = ("Hızlı", "Fast"),
        ["screen"] = ("Ekran", "Screen"),
        ["screen_noscale"] = ("Ekran (ölçekleme yok)", "Screen (no scaling)"),
        ["src_word"] = ("kaynak", "source"),
        ["passes"] = ("geçiş", "passes"),
        ["no_upscale"] = ("yükseltme gerekmedi", "no upscaling needed"),
    };

    public static string T(string key) => D.TryGetValue(key, out var v) ? (Lang == "en" ? v.en : v.tr) : key;
    public static string F(string key, params object[] a) => string.Format(T(key), a);
    public static string Label(string quality, int mode) =>
        mode == 0 ? T("label_orig") : $"{T("mode")} {Modes.Names[mode - 1]} ({T(quality == "FAST" ? "fast" : "hq")})";
}
