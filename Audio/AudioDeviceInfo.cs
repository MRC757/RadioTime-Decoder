namespace WwvDecoder.Audio;

public class AudioDeviceInfo(int index, string name)
{
    public int Index { get; } = index;
    public string Name { get; } = name;
    public override string ToString() => Name;
}
