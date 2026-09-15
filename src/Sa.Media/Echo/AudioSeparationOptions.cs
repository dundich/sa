namespace Sa.Media.Echo;

/// <summary>CLI options for the cross-feed audio separation pipeline.</summary>
internal sealed record AudioSeparationOptions(
    /// <summary>Path to the input PCM 16-bit stereo WAV file.</summary>
    string InputPath,

    /// <summary>Separation mode: <c>"linear"</c> or <c>"aggressive"</c>.</summary>
    string SeparationMode,

    /// <summary>Optional output WAV path. Auto-generated from input filename if null.</summary>
    string? OutputPath,

    /// <summary>dB threshold for single-speaker region detection.</summary>
    double DominanceThresholdDb,

    /// <summary>Power-law exponent for spectral mask sharpening.</summary>
    double SpectralMaskPower,

    /// <summary>Minimum mask floor in [0, 1] — prevents gain from collapsing to zero.</summary>
    double MaskFloor);
