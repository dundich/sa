using static Sa.Media.Echo.CrossFeedSeparator;

namespace Sa.Media.Echo;

/// <summary>CLI options for the cross-feed audio separation pipeline.</summary>
/// <param name="InputPath">Path to the input PCM 16-bit stereo WAV file.</param>
/// <param name="OutputPath">Optional output WAV path. Auto-generated from input filename if null.</param>
/// <param name="Processing">Separation mode: <c>"linear"</c> or <c>"aggressive"</c>.</param>
/// <param name="DominanceThresholdDb">dB threshold for single-speaker region detection.</param>
/// <param name="SpectralMaskPower">Power-law exponent for spectral mask sharpening.</param>
/// <param name="MaskFloor">Minimum mask floor in [0, 1] — prevents gain from collapsing to zero.</param>
public sealed record AudioSeparationOptions(
    string InputPath,
    string? OutputPath = null,
    ProcessingMode Processing = ProcessingMode.Auto,
    double DominanceThresholdDb = 6.0,
    double SpectralMaskPower = 4.0,
    double MaskFloor = 0.01);
