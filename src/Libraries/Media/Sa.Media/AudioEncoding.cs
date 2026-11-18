namespace Sa.Media;

/// <summary>
/// Audio sample encodings supported by the library.
/// </summary>
public enum AudioEncoding
{
    /// <summary>8-bit unsigned PCM.</summary>
    Pcm8BitUnsigned,

    /// <summary>16-bit signed PCM.</summary>
    Pcm16BitSigned,

    /// <summary>24-bit signed PCM.</summary>
    Pcm24BitSigned,

    /// <summary>32-bit signed PCM.</summary>
    Pcm32BitSigned,

    /// <summary>32-bit IEEE floating-point samples.</summary>
    IeeeFloat32Bit,

    /// <summary>64-bit IEEE floating-point samples.</summary>
    IeeeFloat64Bit
}
