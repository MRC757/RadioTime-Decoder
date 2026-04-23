namespace WwvDecoder.Decoder;

public readonly record struct DecoderRuntimeSettingsSnapshot(
    bool EnableAgc,
    bool EnableAle,
    bool EnableAdaptiveLowpass,
    double InputTrimDb);
