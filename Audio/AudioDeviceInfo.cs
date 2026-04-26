namespace WwvDecoder.Audio;

public enum AudioDriverType { Mme, Wasapi }

public class AudioDeviceInfo
{
    public int Index { get; }           // MME device index; -1 for WASAPI
    public string Name { get; }
    public AudioDriverType DriverType { get; }
    public string? WasapiId { get; }    // WASAPI endpoint ID; null for MME

    // MME constructor
    public AudioDeviceInfo(int index, string name)
    {
        Index      = index;
        Name       = name;
        DriverType = AudioDriverType.Mme;
    }

    // WASAPI constructor
    public AudioDeviceInfo(string wasapiId, string name)
    {
        Index      = -1;
        WasapiId   = wasapiId;
        Name       = name;
        DriverType = AudioDriverType.Wasapi;
    }

    public override string ToString() => Name;
}
