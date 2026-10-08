using System.Collections.Generic;

namespace Anime4KScaler;

public static class Modes
{
    public static readonly Dictionary<string, string> Files = new()
    {
        ["CL"] = "Anime4K_Clamp_Highlights", ["RS"] = "Anime4K_Restore_CNN_S", ["RM"] = "Anime4K_Restore_CNN_M", ["RVL"] = "Anime4K_Restore_CNN_VL",
        ["SS"] = "Anime4K_Restore_CNN_Soft_S", ["SM"] = "Anime4K_Restore_CNN_Soft_M", ["SVL"] = "Anime4K_Restore_CNN_Soft_VL",
        ["US"] = "Anime4K_Upscale_CNN_x2_S", ["UM"] = "Anime4K_Upscale_CNN_x2_M", ["UVL"] = "Anime4K_Upscale_CNN_x2_VL",
        ["DM"] = "Anime4K_Upscale_Denoise_CNN_x2_M", ["DVL"] = "Anime4K_Upscale_Denoise_CNN_x2_VL",
        ["A2"] = "Anime4K_AutoDownscalePre_x2", ["A4"] = "Anime4K_AutoDownscalePre_x4",
    };

    public static readonly string[] Names = { "A", "B", "C", "A+A", "B+B", "C+A" };

    public static readonly string[] HQ =
    {
        "CL RVL UVL A2 A4 UM", "CL SVL UVL A2 A4 UM", "CL DVL A2 A4 UM",
        "CL RVL UVL A2 A4 RM UM", "CL SVL UVL A2 A4 SM UM", "CL DVL A2 A4 RM UM",
    };
    public static readonly string[] Fast =
    {
        "CL RM UM A2 A4 US", "CL SM UM A2 A4 US", "CL DM A2 A4 US",
        "CL RM UM A2 A4 RS US", "CL SM UM A2 A4 SS US", "CL DM A2 A4 RS US",
    };

    public static string[] Chain(string quality, int mode) => (quality == "FAST" ? Fast : HQ)[mode - 1].Split(' ');
    public static string Label(string quality, int mode) => Loc.Label(quality, mode);
}
