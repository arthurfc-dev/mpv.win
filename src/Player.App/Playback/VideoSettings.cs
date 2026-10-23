using System.Text.RegularExpressions;

namespace Player.App.Playback;

internal sealed record VideoSettings
{
    public string HardwareDecoding { get; init; } = "auto";
    public string Extensions { get; init; } = "3g2,3gp,avi,flv,ivf,m2ts,m4v,mj2,mkv,mov,mp4,mpeg,mpg,mxf,ogv,rmvb,ts,webm,wmv,y4m";
    public string DitherDepth { get; init; } = "auto";
    public bool Deband { get; init; }
    public string HdrComputePeak { get; init; } = "auto";
    public bool ZeroCopy { get; init; }
    public string BackgroundColor { get; init; } = "#000000";
    public string Scale { get; init; } = "lanczos";
    public string ChromaScale { get; init; } = "";
    public string Downscale { get; init; } = "hermite";
    public bool CorrectDownscaling { get; init; } = true;
    public bool SigmoidUpscaling { get; init; } = true;

    internal static readonly string[] Decoders = ["auto", "no", "auto-copy", "d3d11va", "d3d11va-copy", "nvdec", "nvdec-copy", "dxva2-copy"];
    internal static readonly string[] Filters = ["bilinear", "bicubic_fast", "oversample", "nearest", "hermite", "bicubic", "gaussian", "spline16", "spline36", "spline64", "lanczos", "ewa_lanczos", "ewa_lanczossharp"];
    internal string? Validate()
    {
        if (!Decoders.Contains(HardwareDecoding)) return "Choose a supported Windows hardware decoder.";
        if (Extensions is null || Extensions.Length > 127 || !Regex.IsMatch(Extensions, "^[a-zA-Z0-9]+(,[a-zA-Z0-9]+)*$"))
            return "Video extensions must be comma-separated names without dots, up to 127 characters.";
        if (DitherDepth is not ("auto" or "no" or "8" or "10" or "12" or "16")) return "Choose a supported dither depth.";
        if (HdrComputePeak is not ("auto" or "yes" or "no")) return "Choose a supported HDR peak detection mode.";
        if (!Regex.IsMatch(BackgroundColor ?? "", "^#[0-9a-fA-F]{6}$")) return "Video background color must use #RRGGBB.";
        if (!Filters.Contains(Scale) || !(ChromaScale == "" || Filters.Contains(ChromaScale)) || !(Downscale == "" || Filters.Contains(Downscale)))
            return "Choose supported video scaling filters.";
        return null;
    }

    internal IEnumerable<(PlaybackOption Option, string Value)> Options()
    {
        yield return (PlaybackOption.HardwareDecoding, HardwareDecoding);
        yield return (PlaybackOption.VideoExtensions, Extensions);
        yield return (PlaybackOption.DitherDepth, DitherDepth);
        yield return (PlaybackOption.Deband, Deband ? "yes" : "no");
        yield return (PlaybackOption.HdrComputePeak, HdrComputePeak);
        // Zero-copy is read at hardware interop creation, so it is passed at startup.
        yield return (PlaybackOption.VideoBackground, BackgroundColor);
        yield return (PlaybackOption.Scale, Scale);
        yield return (PlaybackOption.ChromaScale, ChromaScale);
        yield return (PlaybackOption.Downscale, Downscale);
        yield return (PlaybackOption.CorrectDownscaling, CorrectDownscaling ? "yes" : "no");
        yield return (PlaybackOption.SigmoidUpscaling, SigmoidUpscaling ? "yes" : "no");
    }
}
