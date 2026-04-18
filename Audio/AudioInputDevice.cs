using NAudio.Wave;

namespace WwvDecoder.Audio;

public class AudioInputDevice : IDisposable
{
    public const int SampleRate = 22050;
    public const int Channels = 1;

    private WaveInEvent? _waveIn;
    private volatile bool _isProcessing;
    private readonly object _callbackLock = new();

    public static List<AudioDeviceInfo> GetDevices()
    {
        var devices = new List<AudioDeviceInfo>();
        for (int i = 0; i < WaveIn.DeviceCount; i++)
        {
            var caps = WaveIn.GetCapabilities(i);
            devices.Add(new AudioDeviceInfo(i, caps.ProductName));
        }
        return devices;
    }

    public void Start(AudioDeviceInfo device, Action<float[]> onSamples)
    {
        Stop();
        _isProcessing = true;

        _waveIn = new WaveInEvent
        {
            DeviceNumber = device.Index,
            WaveFormat = new WaveFormat(SampleRate, 16, Channels),
            BufferMilliseconds = 50
        };

        // DataAvailable fires on NAudio's dedicated audio callback thread (not the UI thread).
        // The DSP pipeline processes samples synchronously on this thread, so it must be efficient.
        // UI updates are marshaled back to the UI thread via Application.Current.Dispatcher.
        _waveIn.DataAvailable += (_, e) =>
        {
            // _callbackLock ensures Stop() waits for any in-flight callback to fully complete
            // before resetting the pipeline. Without this, Reset() and ProcessSamples() can
            // run concurrently on two threads, corrupting DSP filter state.
            lock (_callbackLock)
            {
                if (!_isProcessing) return;

                try
                {
                    var samples = new float[e.BytesRecorded / 2];
                    for (int i = 0; i < samples.Length; i++)
                    {
                        short raw = BitConverter.ToInt16(e.Buffer, i * 2);
                        samples[i] = raw / 32768f;
                    }
                    onSamples(samples);
                }
                catch
                {
                    // Swallow all exceptions on the audio callback thread.
                    // An unhandled exception here would kill the entire process since
                    // NAudio's callback thread has no top-level exception handler.
                }
            }
        };

        _waveIn.StartRecording();
    }

    public void Stop()
    {
        _isProcessing = false;       // Tell any running callback to exit
        _waveIn?.StopRecording();    // Stop the audio driver from queuing new buffers
        // Acquire then immediately release the lock to wait for any in-flight callback
        // to complete. After this point it is safe to reset the pipeline.
        lock (_callbackLock) { }
        _waveIn?.Dispose();
        _waveIn = null;
    }

    public void Dispose() => Stop();
}
