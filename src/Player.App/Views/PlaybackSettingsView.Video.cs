using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Player.App.Playback;

namespace Player.App.Views;

public sealed partial class PlaybackSettingsView
{
    private readonly ComboBox _hwdec = Choices(VideoSettings.Decoders.Select(x => (x, x)).ToArray());
    private readonly TextBox _videoExtensions = new() { Width = 220, MaxLength = 127, IsSpellCheckEnabled = false };
    private readonly ComboBox _dither = Choices(("auto", "Automatic"), ("no", "Off"), ("8", "8 bit"), ("10", "10 bit"), ("12", "12 bit"), ("16", "16 bit"));
    private readonly ComboBox _hdrPeak = Choices(("auto", "Automatic"), ("yes", "On"), ("no", "Off"));
    private readonly ToggleSwitch _deband = new(), _zeroCopy = new(), _correctDownscale = new(), _sigmoid = new();
    private readonly ColorPicker _videoBackground = Picker();
    private readonly ComboBox _scale = ScalingChoices(false), _cscale = ScalingChoices(true), _dscale = ScalingChoices(true);

    private static ComboBox ScalingChoices(bool inherit)
    {
        var choices = VideoSettings.Filters.Select(x => (x, x)).ToList();
        if (inherit) choices.Insert(0, ("", "Use upscaling filter"));
        return Choices(choices.ToArray());
    }
    private static TextBlock FixedVideoValue(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap, MaxWidth = 220 };

    private void InitializeVideoSettings()
    {
        AddRow(VideoGeneralFields, "Video output (vo)", "The built-in video player is chosen by mpv.win and cannot be changed here.", FixedVideoValue("libmpv · embedded GPU renderer"), advanced: true);
        AddRow(VideoGeneralFields, "Hardware acceleration", "Let your graphics card help play videos. Leave this on auto for automatic selection, or choose no to turn it off if you have playback problems.", _hwdec);
        AddRow(VideoGeneralFields, "Video extensions", "Choose which video file types can join a folder playlist, such as mkv,mp4,webm. Separate names with commas, without dots. This does not change your library or Windows defaults.", _videoExtensions, advanced: true);
        AddReset(VideoGeneralFields, () => PopulateVideoGeneral(new()));
        AddRow(VideoRenderFields, "Graphics API (gpu-api)", "mpv.win uses Direct3D 11 to display video. This is chosen by the app and cannot be changed here.", FixedVideoValue("Direct3D 11 · managed by mpv.win"));
        AddRow(VideoRenderFields, "Graphics context (gpu-context)", "The app manages the connection between the video player and your graphics card. This cannot be changed here.", FixedVideoValue("Embedded D3D11"));
        AddRow(VideoRenderFields, "Dither depth", "Add a small amount of fine noise to make color transitions look smoother. Automatic chooses the depth for you; Off disables it. This does not change Windows HDR settings.", _dither);
        AddRow(VideoRenderFields, "Debanding", "Smooth visible stripes in skies, shadows and other gradual color changes. This uses more graphics power and may soften small details.", _deband);
        AddRow(VideoRenderFields, "HDR peak detection", "Check the brightness of HDR scenes to help adjust them for your display. Automatic lets the player decide when to use this. It does not turn on HDR in Windows.", _hdrPeak);
        AddRow(VideoRenderFields, "Delayed peak detection", "Brightness adjustments use information from an earlier frame. The current video renderer always works this way, so this cannot be changed here.", FixedVideoValue("Always delayed · renderer behavior"));
        AddRow(VideoRenderFields, "D3D11 decoder zero-copy", "Avoid an extra copy of video data when using Direct3D 11 hardware decoding. This may improve efficiency, but can cause picture problems on some graphics drivers. Restart the app after applying.", _zeroCopy);
        AddRow(VideoRenderFields, "Video background", "Choose the color of empty areas around the video. Transparent parts of a video still use the player's checkerboard pattern.", ColorButton(_videoBackground, "Video background color"));
        AddRow(VideoRenderFields, "Raw libplacebo options", "These controls need a different video renderer and are not available in this version of mpv.win.", FixedVideoValue("Unavailable with embedded GPU renderer"));
        AddReset(VideoRenderFields, () => PopulateVideoRender(new()));
        AddRow(VideoScalingFields, "Upscaling filter", "Choose how to draw a video when it is larger than its original size. Filters can trade sharper detail for smoother edges.", _scale);
        AddRow(VideoScalingFields, "Chroma filter", "Choose how to fill in color detail when the video stores colors at a lower resolution. Use upscaling filter uses the same choice as above.", _cscale);
        AddRow(VideoScalingFields, "Downscaling filter", "Choose how to keep detail when shrinking a video. Use upscaling filter uses the same choice as above.", _dscale);
        AddRow(VideoScalingFields, "Correct downscaling", "Keep more detail when shrinking a video with a compatible filter. This uses more graphics power.", _correctDownscale);
        AddRow(VideoScalingFields, "Sigmoid upscaling", "Reduce bright or dark outlines around edges when enlarging a video. The result depends on the video and filter.", _sigmoid);
        AddReset(VideoScalingFields, () => PopulateVideoScaling(new()));
        VideoLibplaceboFields.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational,
            Title = "Not available in this version", Message = "These settings need a different video renderer, called gpu-next. The current player cannot use them." });
        AddRow(VideoLibplaceboFields, "Preset", "These ready-made quality settings are not available with the current video renderer. Use Render options and Scaling instead.", FixedVideoValue("Not available"));
        VideoLibplaceboFields.Children.Add(new TextBlock { Text = "You can adjust picture quality using the Render options and Scaling sections.", TextWrapping = TextWrapping.Wrap });
    }
    private void PopulateVideo(VideoSettings s) { PopulateVideoGeneral(s); PopulateVideoRender(s); PopulateVideoScaling(s); }
    private void PopulateVideoGeneral(VideoSettings s) { Select(_hwdec, s.HardwareDecoding); _videoExtensions.Text = s.Extensions; }
    private void PopulateVideoRender(VideoSettings s)
    {
        Select(_dither, s.DitherDepth); Select(_hdrPeak, s.HdrComputePeak); _deband.IsOn = s.Deband;
        _zeroCopy.IsOn = s.ZeroCopy; _videoBackground.Color = ParseColor(s.BackgroundColor);
    }
    private void PopulateVideoScaling(VideoSettings s)
    {
        Select(_scale, s.Scale); Select(_cscale, s.ChromaScale); Select(_dscale, s.Downscale);
        _correctDownscale.IsOn = s.CorrectDownscaling; _sigmoid.IsOn = s.SigmoidUpscaling;
    }
    private VideoSettings ReadVideoSettings() => new() {
        HardwareDecoding = Value(_hwdec), Extensions = string.Join(',', _videoExtensions.Text.Split(',').Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)),
        DitherDepth = Value(_dither), Deband = _deband.IsOn, HdrComputePeak = Value(_hdrPeak), ZeroCopy = _zeroCopy.IsOn,
        BackgroundColor = Hex(_videoBackground), Scale = Value(_scale), ChromaScale = Value(_cscale), Downscale = Value(_dscale),
        CorrectDownscaling = _correctDownscale.IsOn, SigmoidUpscaling = _sigmoid.IsOn };
}
