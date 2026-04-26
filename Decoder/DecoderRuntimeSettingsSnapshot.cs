namespace WwvDecoder.Decoder;

public readonly record struct DecoderRuntimeSettingsSnapshot(
    bool EnableAgc,
    bool EnableAdaptiveLowpass,
    double InputTrimDb);
