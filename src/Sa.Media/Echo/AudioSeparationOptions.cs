using static Sa.Media.Echo.CrossFeedSeparator;

namespace Sa.Media.Echo;

/// <summary>CLI options for the cross-feed audio separation pipeline.</summary>
public sealed record AudioSeparationOptions(
    /// <summary>Path to the input PCM 16-bit stereo WAV file.</summary>
    string InputPath,

    /// <summary>Optional output WAV path. Auto-generated from input filename if null.</summary>
    string? OutputPath = null,

    /// <summary>Separation mode: <c>"linear"</c> or <c>"aggressive"</c>.</summary>
    ProcessingMode Processing = ProcessingMode.Auto,

    /// <summary>dB threshold for single-speaker region detection.</summary>
    double DominanceThresholdDb = 6.0,

    /// <summary>Power-law exponent for spectral mask sharpening.</summary>
    double SpectralMaskPower = 4.0,

    /// <summary>Minimum mask floor in [0, 1] — prevents gain from collapsing to zero.</summary>
    double MaskFloor = 0.01);
