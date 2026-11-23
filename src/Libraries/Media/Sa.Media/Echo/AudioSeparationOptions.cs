using static Sa.Media.Echo.CrossFeedSeparator;

namespace Sa.Media.Echo;

/// <summary>CLI options for the cross-feed audio separation pipeline.</summary>
public sealed record AudioSeparationOptions
{
    /// <summary>Path of the input WAV file.</summary>
    public required string InputPath { get; init; }

    /// <summary>
    /// Output path. When omitted, the file is written next to the input as
    /// <c>&lt;name&gt;_linear.wav</c> or <c>&lt;name&gt;_aggressive.wav</c>, depending on
    /// <see cref="Processing"/>. It must not point at the input file itself.
    /// </summary>
    public string? OutputPath { get; init; }

    /// <summary>Separation mode.</summary>
    public ProcessingMode Processing { get; init; } = ProcessingMode.Auto;

    /// <summary>dB threshold for single-speaker region detection.</summary>
    public double DominanceThresholdDb { get; init; } = 6.0;

    /// <summary>Power-law exponent for spectral mask sharpening.</summary>
    public double SpectralMaskPower { get; init; } = 4.0;

    /// <summary>Minimum mask floor in [0, 1] — prevents gain from collapsing to zero.</summary>
    public double MaskFloor { get; init; } = 0.01;
}
