using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WwvDecoder.Audio;

public class AudioInputDevice : IDisposable
{
    public const int SampleRate = 22050;
    public const int Channels   = 1;

    // MME state
    private WaveInEvent? _mmeCapture;

    // WASAPI state
    private WasapiCapture?          _wasapiCapture;
    private MMDevice?               _wasapiDevice;   // must outlive _wasapiCapture
    private BufferedWaveProvider?   _wasapiBuffer;
    private MediaFoundationResampler? _resampler;

    private volatile bool _isProcessing;
    private readonly object _callbackLock = new();

    // ── Device enumeration ────────────────────────────────────────────────────

    public static List<AudioDeviceInfo> GetDevices()
    {
        var devices = new List<AudioDeviceInfo>();

        // MME devices (Windows Multimedia Extension — standard Windows audio)
        for (int i = 0; i < WaveIn.DeviceCount; i++)
        {
            var caps = WaveIn.GetCapabilities(i);
            devices.Add(new AudioDeviceInfo(i, caps.ProductName));
        }

        // WASAPI capture endpoints — lower latency, bypasses Windows AGC/processing
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                devices.Add(new AudioDeviceInfo(dev.ID, $"[WASAPI] {dev.FriendlyName}"));
                dev.Dispose();
            }
        }
        catch { /* WASAPI not available on this system */ }

        return devices;
    }

    // ── Start / Stop ──────────────────────────────────────────────────────────

    public void Start(AudioDeviceInfo device, Action<float[]> onSamples)
    {
        Stop();
        _isProcessing = true;

        if (device.DriverType == AudioDriverType.Wasapi)
            StartWasapi(device, onSamples);
        else
            StartMme(device, onSamples);
    }

    // MME path — unchanged from original; requests 22050 Hz / 16-bit / mono directly.
    private void StartMme(AudioDeviceInfo device, Action<float[]> onSamples)
    {
        _mmeCapture = new WaveInEvent
        {
            DeviceNumber     = device.Index,
            WaveFormat       = new WaveFormat(SampleRate, 16, Channels),
            BufferMilliseconds = 50
        };

        // DataAvailable fires on NAudio's dedicated audio callback thread (not the UI thread).
        _mmeCapture.DataAvailable += (_, e) =>
        {
            // _callbackLock ensures Stop() waits for any in-flight callback to fully
            // complete before resetting the pipeline.
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
                catch { }
            }
        };

        _mmeCapture.StartRecording();
    }

    // WASAPI path — captures at the device's native format (e.g. 48000 Hz stereo
    // float32) and resamples via MediaFoundationResampler to 22050 Hz mono float32.
    private void StartWasapi(AudioDeviceInfo device, Action<float[]> onSamples)
    {
        var enumerator   = new MMDeviceEnumerator();
        _wasapiDevice    = enumerator.GetDevice(device.WasapiId!);
        enumerator.Dispose();

        // useEventSync=true: WASAPI event-driven mode — hardware-clock callbacks,
        // minimum latency, no Windows audio processing pipeline in the path.
        _wasapiCapture = new WasapiCapture(_wasapiDevice, useEventSync: true,
                                           audioBufferMillisecondsLength: 50);

        var nativeFormat = _wasapiCapture.WaveFormat;
        var targetFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

        // Buffer to absorb delivery jitter; overflow discards oldest audio.
        _wasapiBuffer = new BufferedWaveProvider(nativeFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration          = TimeSpan.FromSeconds(2)
        };

        // MediaFoundationResampler handles any source rate/channel/encoding →
        // 22050 Hz mono float32. ResamplerQuality 60 = maximum quality.
        _resampler = new MediaFoundationResampler(_wasapiBuffer, targetFormat)
        {
            ResamplerQuality = 60
        };

        // Pre-compute: how many target bytes correspond to one input byte.
        // target bytes-per-second / source bytes-per-second
        double bytesRatio =
            (double)(SampleRate * Channels * 4 /* float32 */) /
            (nativeFormat.SampleRate * nativeFormat.Channels * (nativeFormat.BitsPerSample / 8));

        var outBuffer = new byte[8192]; // sized for up to ~90 ms at 22050 Hz

        _wasapiCapture.DataAvailable += (_, e) =>
        {
            lock (_callbackLock)
            {
                if (!_isProcessing) return;
                try
                {
                    _wasapiBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

                    // Read the proportional number of resampled bytes for this block,
                    // rounded down to a 4-byte (float32) boundary.
                    int toRead = (int)(e.BytesRecorded * bytesRatio) & ~3;
                    if (toRead < 4) toRead = 4;
                    toRead = Math.Min(toRead, outBuffer.Length);

                    int read = _resampler.Read(outBuffer, 0, toRead);
                    if (read > 0)
                    {
                        var samples = new float[read / 4];
                        Buffer.BlockCopy(outBuffer, 0, samples, 0, read);
                        onSamples(samples);
                    }
                }
                catch { }
            }
        };

        _wasapiCapture.StartRecording();
    }

    public void Stop()
    {
        _isProcessing = false;

        _mmeCapture?.StopRecording();
        _wasapiCapture?.StopRecording();

        // Wait for any in-flight callback to finish before tearing down.
        lock (_callbackLock) { }

        _mmeCapture?.Dispose();
        _mmeCapture = null;

        _resampler?.Dispose();
        _resampler = null;
        _wasapiBuffer = null;

        // Dispose capture before device: WasapiCapture holds the MMDevice reference.
        _wasapiCapture?.Dispose();
        _wasapiCapture = null;

        _wasapiDevice?.Dispose();
        _wasapiDevice = null;
    }

    public void Dispose() => Stop();
}
