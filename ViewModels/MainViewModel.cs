using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WwvDecoder.Audio;
using WwvDecoder.Clock;
using WwvDecoder.Decoder;
using WwvDecoder.Logging;
using WwvDecoder.Stations;

namespace WwvDecoder.ViewModels;

public enum LockState { Searching, Syncing, Locked }

/// <summary>
/// View model for one cell in the 60-position frame visualization grid.
/// Exposes a background brush and tooltip derived from the cell's state and value,
/// so the XAML DataTemplate needs no converters.
/// </summary>
public class FrameCellViewModel : INotifyPropertyChanged
{
    // Catppuccin-Mocha palette colours matching the rest of the UI
    private static readonly Brush BrEmpty     = MakeBrush(0x45, 0x47, 0x5A); // surface2 – not yet received
    private static readonly Brush BrConfident = MakeBrush(0xA6, 0xE3, 0xA1); // green   – both classifiers agreed
    private static readonly Brush BrErased    = MakeBrush(0xCB, 0xA6, 0xF7); // mauve   – classifiers disagreed
    private static readonly Brush BrGapFilled = MakeBrush(0xF9, 0xE2, 0xAF); // yellow  – estimated during fade
    private static readonly Brush BrCorrected = MakeBrush(0xF3, 0x8B, 0xA8); // red     – structurally overridden

    private static SolidColorBrush MakeBrush(byte r, byte g, byte b)
    {
        var b2 = new SolidColorBrush(Color.FromRgb(r, g, b));
        b2.Freeze();
        return b2;
    }

    private int _value;
    private FrameCellState _state = FrameCellState.Empty;

    public int Position { get; }

    public FrameCellViewModel(int position) => Position = position;

    public int Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ToolTip));
        }
    }

    public FrameCellState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Background));
            OnPropertyChanged(nameof(ToolTip));
        }
    }

    public Brush Background => _state switch
    {
        FrameCellState.Confident  => BrConfident,
        FrameCellState.Erased     => BrErased,
        FrameCellState.GapFilled  => BrGapFilled,
        FrameCellState.Corrected  => BrCorrected,
        _                        => BrEmpty
    };

    public string ToolTip
    {
        get
        {
            string val = _value switch { 2 => "M", 1 => "1", _ => "0" };
            return $"[{Position:D2}]  {val}  ·  {_state}";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly AudioInputDevice _audioInput = new();
    private readonly DecoderPipeline _pipeline;
    private readonly SystemTimeSetter _timeSetter = new();
    private readonly FileLogger _fileLogger = new();
    private readonly DiagnosticLogger _diagLogger = new();

    private bool _isListening;
    private string _knownDateText = DateTime.UtcNow.ToString("yyyy-MM-dd");
    private double _signalStrength;
    private double _subcarrierStrength;
    private double _lockStrength;
    private LockState _lockState = LockState.Searching;
    private string _countdownDisplay = "";
    private string _decodedTimeDisplay = "--:--:-- UTC";
    private string _decodedDateDisplay = "--- --, ----  ·  Day ---";
    private string _localTimeDisplay = "--:--:--";
    private string _dayOfYear = "---";
    private double _utcOffsetHours;
    private string _selectedUtcOffsetLabel = "UTC\u00B10";
    private string _dut1Display = "---";
    private string _dstDisplay = "---";
    private string _leapSecondDisplay = "None";
    private double _confidencePercent;
    private string _confidenceDisplay = "0 / 2";
    private TimeFrame? _latestFrame;
    private AudioDeviceInfo? _selectedDevice;
    private StationInfo? _selectedStation;

    private bool _autoSyncMinuteStart;
    private string _lastMinuteSyncInfo = "";
    private bool _enableInputAgc = true;
    private bool _enableAdaptiveLowpass = true;
    private double _inputTrimDb;
    private double _syncScore;
    private string _coarseCarrierDisplay = "100.0 Hz";
    private string _agcGainDisplay = "0.0 dB";

    // 1 kHz tick indicator
    private TickLockState _tickLockState = TickLockState.NoSignal;
    private double _tickDotOpacity = 0.35;
    private readonly System.Windows.Threading.DispatcherTimer _tickDimTimer;

    // Minute tone indicator
    private double _minuteDotOpacity = 0.35;
    private readonly System.Windows.Threading.DispatcherTimer _minuteDimTimer;

    // Receiver mode alert
    private string? _receiverModeAlert;

    /// <summary>
    /// When true, the system clock's seconds field is zeroed automatically each time
    /// the 1000 Hz minute pulse is detected. Useful for HF digital-mode software
    /// (WSPR, FT8, JS8Call) that needs the minute boundary to be accurate.
    /// No decoded frame is required — the minute pulse is detected independently of BCD decoding.
    /// </summary>
    public bool AutoSyncMinuteStart
    {
        get => _autoSyncMinuteStart;
        set { _autoSyncMinuteStart = value; OnPropertyChanged(); }
    }

    /// <summary>Result of the most recent minute-start sync (shown next to the checkbox).</summary>
    public string LastMinuteSyncInfo
    {
        get => _lastMinuteSyncInfo;
        private set { _lastMinuteSyncInfo = value; OnPropertyChanged(); }
    }

    /// <summary>Non-null when the receiver appears to be in the wrong demodulation mode.</summary>
    public string? ReceiverModeAlert
    {
        get => _receiverModeAlert;
        private set
        {
            if (_receiverModeAlert == value) return;
            _receiverModeAlert = value;
            OnPropertyChanged();
        }
    }

    public bool EnableInputAgc
    {
        get => _enableInputAgc;
        set { _enableInputAgc = value; OnPropertyChanged(); }
    }

    public bool EnableAdaptiveLowpass
    {
        get => _enableAdaptiveLowpass;
        set { _enableAdaptiveLowpass = value; OnPropertyChanged(); }
    }

    public double InputTrimDb
    {
        get => _inputTrimDb;
        set
        {
            double rounded = Math.Round(Math.Clamp(value, -24.0, 24.0), 1);
            if (Math.Abs(_inputTrimDb - rounded) < 0.001) return;
            _inputTrimDb = rounded;
            OnPropertyChanged();
            OnPropertyChanged(nameof(InputTrimDisplay));
        }
    }

    public string InputTrimDisplay => $"{_inputTrimDb:+0.0;-0.0;+0.0} dB";

    public double SyncScore
    {
        get => _syncScore;
        private set
        {
            _syncScore = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SyncScoreDisplay));
        }
    }

    public string SyncScoreDisplay => $"{_syncScore:F0}%";

    public string CoarseCarrierDisplay
    {
        get => _coarseCarrierDisplay;
        private set { _coarseCarrierDisplay = value; OnPropertyChanged(); }
    }

    public string AgcGainDisplay
    {
        get => _agcGainDisplay;
        private set { _agcGainDisplay = value; OnPropertyChanged(); }
    }

    // ── 1 kHz tick indicator ─────────────────────────────────────────────────

    /// <summary>Label shown next to the tick dot: "No Signal", "Searching", or "Locked".</summary>
    public string TickLockText => _tickLockState switch
    {
        TickLockState.Locked    => "Locked",
        TickLockState.Searching => "Searching",
        _                       => "No Signal",
    };

    /// <summary>Color of the tick dot and label, reflecting lock state.</summary>
    public string TickDotColor => _tickLockState switch
    {
        TickLockState.Locked    => "#A6E3A1",   // green
        TickLockState.Searching => "#F9E2AF",   // yellow
        _                       => "#585B70",   // muted gray
    };

    /// <summary>
    /// Opacity of the tick dot. Jumps to 1.0 on each incoming tick and
    /// decays back to 0.35 after 400 ms — producing a visible flash.
    /// </summary>
    public double TickDotOpacity
    {
        get => _tickDotOpacity;
        private set { _tickDotOpacity = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Opacity of the minute-pulse dot. Flashes to 1.0 when the 1 kHz
    /// minute tone is detected and dims back after 1.5 s.
    /// </summary>
    public double MinuteDotOpacity
    {
        get => _minuteDotOpacity;
        private set { _minuteDotOpacity = value; OnPropertyChanged(); }
    }

    public MainViewModel()
    {
        _pipeline = new DecoderPipeline(OnSignalUpdate, OnFrameDecoded, msg => Log(msg), OnFrameUpdate,
            getSettings: GetDecoderSettings,
            diagnosticLogger: _diagLogger);
        _pipeline.MinutePulseDetected += OnMinutePulseDetected;

        // Flash the tick dot for 400 ms on each incoming second tick.
        _tickDimTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _tickDimTimer.Tick += (_, _) =>
        {
            _tickDimTimer.Stop();
            TickDotOpacity = 0.35;
        };

        _pipeline.TickHeartbeat += state =>
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                _tickLockState = state;
                TickDotOpacity = 1.0;
                OnPropertyChanged(nameof(TickLockText));
                OnPropertyChanged(nameof(TickDotColor));
                _tickDimTimer.Stop();
                _tickDimTimer.Start();
            });
        };

        // Flash the minute dot for 1.5 s on each detected WWV/WWVH minute tone.
        _minuteDimTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        _minuteDimTimer.Tick += (_, _) =>
        {
            _minuteDimTimer.Stop();
            MinuteDotOpacity = 0.35;
        };

        _pipeline.MinutePulseDetected += _ =>
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                MinuteDotOpacity = 1.0;
                _minuteDimTimer.Stop();
                _minuteDimTimer.Start();
            });
        };
        LoadDevices();
        LoadStations();

        // Default UTC offset to the system's current local offset (respects DST)
        int sysOffset = (int)Math.Round(TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalHours);
        sysOffset = Math.Clamp(sysOffset, -12, 14);
        _utcOffsetHours = sysOffset;
        _selectedUtcOffsetLabel = FormatOffset(sysOffset);

        ToggleListenCommand         = new RelayCommand(ToggleListen);
        SetClockCommand             = new RelayCommand(SetClock, () => CanSetClock);
        ClearLogCommand             = new RelayCommand(() => { LogText = string.Empty; OnPropertyChanged(nameof(LogText)); });
        ShowStationReferenceCommand = new RelayCommand(ShowStationReference);
        ApplyKnownDateCommand       = new RelayCommand(ApplyKnownDate);
        ClearKnownDateCommand       = new RelayCommand(ClearKnownDate);
    }

    // Well-known standard-time abbreviations by UTC offset (hour).
    // DST offsets are intentionally omitted — the decoded UTC time is always standard;
    // the user's local DST adjustment is handled by Windows, not this selector.
    private static readonly Dictionary<int, string> _tzAbbreviations = new()
    {
        [-12] = "IDLW",  // International Date Line West
        [-11] = "SST",   // Samoa Standard Time
        [-10] = "HST",   // Hawaii Standard Time
        [ -9] = "AKST",  // Alaska Standard Time
        [ -8] = "PST",   // Pacific Standard Time
        [ -7] = "MST",   // Mountain Standard Time
        [ -6] = "CST",   // Central Standard Time
        [ -5] = "EST",   // Eastern Standard Time
        [ -4] = "AST",   // Atlantic Standard Time
        [ -3] = "BRT",   // Brasília Time
        [ -1] = "AZOT",  // Azores Standard Time
        [  0] = "UTC",   // Universal Coordinated Time / GMT
        [  1] = "CET",   // Central European Time
        [  2] = "EET",   // Eastern European Time
        [  3] = "MSK",   // Moscow Standard Time
        [  4] = "GST",   // Gulf Standard Time
        [  5] = "PKT",   // Pakistan Standard Time
        [  6] = "BST",   // Bangladesh Standard Time
        [  7] = "ICT",   // Indochina Time
        [  8] = "CST/AWST", // China Standard / Australia Western
        [  9] = "JST",   // Japan Standard Time
        [ 10] = "AEST",  // Australian Eastern Standard Time
        [ 11] = "SBT",   // Solomon Islands Time
        [ 12] = "NZST",  // New Zealand Standard Time
        [ 13] = "NZDT",  // New Zealand Daylight Time
        [ 14] = "LINT",  // Line Islands Time
    };

    private static string FormatOffset(int hours)
    {
        string utcPart = hours == 0 ? "UTC\u00B10" : hours > 0 ? $"UTC+{hours}" : $"UTC{hours}";
        return _tzAbbreviations.TryGetValue(hours, out string? abbr)
            ? $"{utcPart}  {abbr}"
            : utcPart;
    }

    private static double ParseOffset(string label)
    {
        if (label.Contains('\u00B1')) return 0; // "UTC±0  UTC"
        // Label format: "UTC+N  ABBR" or "UTC-N  ABBR" or "UTC+N"
        // Extract only the numeric part after "UTC", stopping at any whitespace.
        var s = label[3..]; // everything after "UTC" — e.g. "+5  PKT" or "-8  PST"
        int spaceIdx = s.IndexOf(' ');
        if (spaceIdx > 0) s = s[..spaceIdx];
        return double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static readonly IReadOnlyList<string> _utcOffsetOptions =
        Enumerable.Range(-12, 27).Select(h => FormatOffset(h)).ToList();

    public IReadOnlyList<string> UtcOffsetOptions => _utcOffsetOptions;

    public string SelectedUtcOffsetLabel
    {
        get => _selectedUtcOffsetLabel;
        set
        {
            if (value == null) return;
            _selectedUtcOffsetLabel = value;
            _utcOffsetHours = ParseOffset(value);
            OnPropertyChanged();
            RefreshLocalTime();
        }
    }

    // ── Collections ────────────────────────────────────────────────────────────

    public ObservableCollection<AudioDeviceInfo> AudioDevices { get; } = [];

    /// <summary>60 cells, one per BCD frame position, updated in real time.</summary>
    public ObservableCollection<FrameCellViewModel> FrameCells { get; } =
        new(Enumerable.Range(0, 60).Select(i => new FrameCellViewModel(i)));
    public string LogText { get; private set; } = string.Empty;

    /// <summary>All known stations shown in the selector (active/uncertain only).</summary>
    public ObservableCollection<StationInfo> AllStations { get; } = [];

    // ── Device ─────────────────────────────────────────────────────────────────

    public AudioDeviceInfo? SelectedDevice
    {
        get => _selectedDevice;
        set { _selectedDevice = value; OnPropertyChanged(); }
    }

    // ── Station ────────────────────────────────────────────────────────────────

    public StationInfo? SelectedStation
    {
        get => _selectedStation;
        set
        {
            _selectedStation = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DecoderSupportLabel));
            OnPropertyChanged(nameof(DecoderSupportBackground));
            OnPropertyChanged(nameof(DecoderSupportForeground));
        }
    }

    /// <summary>Badge label shown next to the frequencies line.</summary>
    public string DecoderSupportLabel => _selectedStation switch
    {
        null                                          => "",
        { IsDecoderSupported: true }                  => "Decoder Supported",
        { TimeCodeFormat: TimeCodeFormat.ChuFsk }     => "Future: CHU FSK",
        { TimeCodeFormat: TimeCodeFormat.RwmPhase }   => "Future: Phase Shift",
        { TimeCodeFormat: TimeCodeFormat.TicksOnly }  => "Ticks Only",
        _                                             => "Unsupported"
    };

    private static readonly Brush SupportedBg = Frozen(45, 74, 56);
    private static readonly Brush UnsupportedBg = Frozen(69, 71, 90);
    private static readonly Brush SupportedFg = Frozen(166, 227, 161);
    private static readonly Brush UnsupportedFg = Frozen(166, 173, 200);

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public Brush DecoderSupportBackground => _selectedStation?.IsDecoderSupported == true
        ? SupportedBg : UnsupportedBg;

    public Brush DecoderSupportForeground => _selectedStation?.IsDecoderSupported == true
        ? SupportedFg : UnsupportedFg;

    // ── Listen state ───────────────────────────────────────────────────────────

    public bool IsListening
    {
        get => _isListening;
        private set { _isListening = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNotListening)); }
    }

    public bool IsNotListening => !_isListening;

    // ── Signal meters ──────────────────────────────────────────────────────────

    public double SignalStrength
    {
        get => _signalStrength;
        private set { _signalStrength = value; OnPropertyChanged(); OnPropertyChanged(nameof(SignalStrengthDb)); }
    }

    public string SignalStrengthDb => _signalStrength > 0
        ? $"{20 * Math.Log10(_signalStrength / 100.0):F1} dB"
        : "--- dB";

    public double SubcarrierStrength
    {
        get => _subcarrierStrength;
        private set { _subcarrierStrength = value; OnPropertyChanged(); OnPropertyChanged(nameof(SubcarrierStrengthDb)); }
    }

    public string SubcarrierStrengthDb => _subcarrierStrength > 0
        ? $"{20 * Math.Log10(_subcarrierStrength / 100.0):F1} dB"
        : "--- dB";

    public double LockStrength
    {
        get => _lockStrength;
        private set { _lockStrength = value; OnPropertyChanged(); }
    }

    public LockState LockState
    {
        get => _lockState;
        private set { _lockState = value; OnPropertyChanged(); }
    }

    public string CountdownDisplay
    {
        get => _countdownDisplay;
        private set { _countdownDisplay = value; OnPropertyChanged(); }
    }

    // ── Decoded time ───────────────────────────────────────────────────────────

    public string DecodedTimeDisplay
    {
        get => _decodedTimeDisplay;
        private set { _decodedTimeDisplay = value; OnPropertyChanged(); }
    }

    public string DecodedDateDisplay
    {
        get => _decodedDateDisplay;
        private set { _decodedDateDisplay = value; OnPropertyChanged(); }
    }

    public string LocalTimeDisplay
    {
        get => _localTimeDisplay;
        private set { _localTimeDisplay = value; OnPropertyChanged(); }
    }

    public string DayOfYear
    {
        get => _dayOfYear;
        private set { _dayOfYear = value; OnPropertyChanged(); }
    }

    public string Dut1Display
    {
        get => _dut1Display;
        private set { _dut1Display = value; OnPropertyChanged(); }
    }

    public string DstDisplay
    {
        get => _dstDisplay;
        private set { _dstDisplay = value; OnPropertyChanged(); }
    }

    public string LeapSecondDisplay
    {
        get => _leapSecondDisplay;
        private set { _leapSecondDisplay = value; OnPropertyChanged(); }
    }

    public double ConfidencePercent
    {
        get => _confidencePercent;
        private set { _confidencePercent = value; OnPropertyChanged(); }
    }

    public string ConfidenceDisplay
    {
        get => _confidenceDisplay;
        private set { _confidenceDisplay = value; OnPropertyChanged(); }
    }

    public bool CanSetClock => _latestFrame != null && _latestFrame.IsValid && _latestFrame.ConfidenceFrames >= 2;

    // ── Commands ──────────────────────────────────────────────────────────────

    // ── UTC Date hint ──────────────────────────────────────────────────────────

    /// <summary>
    /// Date text entered by the operator (yyyy-MM-dd, always UTC).
    /// Pre-filled with today's UTC date at startup.
    /// </summary>
    public string KnownDateText
    {
        get => _knownDateText;
        set { _knownDateText = value; OnPropertyChanged(); }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public ICommand ToggleListenCommand { get; }
    public ICommand SetClockCommand { get; }
    public ICommand ClearLogCommand { get; }
    public ICommand ShowStationReferenceCommand { get; }
    public ICommand ApplyKnownDateCommand { get; }
    public ICommand ClearKnownDateCommand { get; }

    // ── Private methods ───────────────────────────────────────────────────────

    private void LoadDevices()
    {
        foreach (var d in AudioInputDevice.GetDevices())
            AudioDevices.Add(d);
        SelectedDevice = AudioDevices.FirstOrDefault();
    }

    private void LoadStations()
    {
        foreach (var s in StationsDatabase.ActiveOrUncertain)
            AllStations.Add(s);
        // Default to WWV
        SelectedStation = AllStations.FirstOrDefault(s => s.CallSign == "WWV")
                          ?? AllStations.FirstOrDefault();
    }

    private void ToggleListen()
    {
        if (_isListening)
        {
            _audioInput.Stop();
            _pipeline.Reset();
            IsListening = false;
            LockState = LockState.Searching;
            LockStrength = 0;
            SignalStrength = 0;
            SubcarrierStrength = 0;
            CountdownDisplay = "";
            DecodedTimeDisplay = "--:--:-- UTC";
            DecodedDateDisplay = "--- --, ----  ·  Day ---";
            LocalTimeDisplay = "--:--:--";
            Log("Stopped listening.");
        }
        else
        {
            if (_selectedDevice == null) { Log("No audio device selected."); return; }
            if (_selectedStation == null) { Log("No station selected."); return; }

            if (!_selectedStation.IsDecoderSupported)
            {
                Log($"Warning: {_selectedStation.CallSign} uses {DecoderSupportLabel} — " +
                    "full decode not yet supported. Signal level will still be shown.");
            }

            _pipeline.Reset();
            _audioInput.Start(_selectedDevice, _pipeline.ProcessSamples);
            IsListening = true;
            Log($"Listening on: {_selectedDevice.Name}");
            Log($"Station: {_selectedStation.CallSign}  |  {_selectedStation.Location}, {_selectedStation.Country}");
            Log($"Frequencies: {_selectedStation.FrequencyList}");

            // Auto-seed year and DOY persistent bits from the system UTC date so the
            // decoder has a reasonable starting point even before the first frame decode.
            // The operator date field is kept in sync so the UI reflects what was applied.
            // This prevents wrong-year decodes (e.g. 2074) when the signal is too weak to
            // deliver reliable year bits on the first frame. Overridden automatically by
            // the first successfully validated frame decode, or manually via Apply/Clear.
            var utcToday = DateTime.UtcNow.Date;
            _knownDateText = utcToday.ToString("yyyy-MM-dd");
            OnPropertyChanged(nameof(KnownDateText));
            _pipeline.SetKnownDate(utcToday);
        }
    }

    private void SetClock()
    {
        if (_latestFrame == null) return;
        try
        {
            var delta = _timeSetter.SetTime(_latestFrame.UtcTime);
            Log($"Clock set. Delta was {delta.TotalMilliseconds:+0.0;-0.0} ms");
        }
        catch (Exception ex)
        {
            Log($"Error setting clock: {ex.Message}");
        }
    }

    private void OnMinutePulseDetected(double pulseWidthSeconds)
    {
        if (!_autoSyncMinuteStart) return;
        try
        {
            var delta = _timeSetter.SyncMinuteStart(pulseWidthSeconds);
            // Pipeline uses Stopwatch (monotonic) for all timing — no adjustment needed after clock step.
            string info = $"{DateTime.UtcNow:HH:mm} UTC  ({delta.TotalMilliseconds:+0.0;-0.0} ms)";
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                LastMinuteSyncInfo = info;
                Log($"Minute-start sync: {info}");
            });
        }
        catch (Exception ex)
        {
            Application.Current?.Dispatcher.InvokeAsync(() =>
                Log($"Minute-start sync failed: {ex.Message}"));
        }
    }

    private void ApplyKnownDate()
    {
        if (DateTime.TryParseExact(_knownDateText, "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var date))
        {
            _pipeline.SetKnownDate(date);
        }
        else
        {
            Log($"Invalid UTC date '{_knownDateText}' — enter as yyyy-MM-dd (e.g. 2026-04-04)");
        }
    }

    private void ClearKnownDate() => _pipeline.ClearKnownDate();

    private void ShowStationReference()
    {
        var win = new StationReferenceWindow
        {
            Owner = Application.Current.MainWindow
        };
        win.ShowDialog();
    }

    private void OnSignalUpdate(SignalStatus status)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            SignalStrength = status.SignalStrengthPercent;
            SubcarrierStrength = status.SubcarrierStrengthPercent;
            LockStrength = status.LockStrengthPercent;
            LockState = status.LockState;
            SyncScore = status.SyncScorePercent;
            CoarseCarrierDisplay = status.SyncScorePercent >= 5
                ? $"{status.CoarseCarrierHz:F1} Hz"
                : "--.- Hz";
            AgcGainDisplay = status.AgcEnabled
                ? $"{status.AgcGainDb:+0.0;-0.0;+0.0} dB"
                : $"Bypass ({status.AgcGainDb:+0.0;-0.0;+0.0} dB trim)";

            if (status.FrameSecondsRemaining > 0)
                CountdownDisplay = $"{status.FrameSecondsRemaining}s";
            else
                CountdownDisplay = "";

            ReceiverModeAlert = status.ReceiverModeAlert;

            // Keep tick state in sync for the NoSignal decay (no heartbeat fires then).
            if (status.TickState != _tickLockState)
            {
                _tickLockState = status.TickState;
                OnPropertyChanged(nameof(TickLockText));
                OnPropertyChanged(nameof(TickDotColor));
            }
        });
    }

    private void OnFrameUpdate(FrameCell[] cells)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            for (int i = 0; i < 60; i++)
            {
                FrameCells[i].Value = cells[i].Value;
                FrameCells[i].State = cells[i].State;
            }
        });
    }

    private void OnFrameDecoded(TimeFrame frame)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            _latestFrame = frame;
            var t = frame.UtcTime;
            DecodedTimeDisplay = $"{t:HH:mm:ss} UTC";
            DecodedDateDisplay = $"{t:MMM dd, yyyy}  ·  Day {t.DayOfYear:D3}";
            DayOfYear = t.DayOfYear.ToString("D3");
            RefreshLocalTime();
            Dut1Display = $"{frame.Dut1Seconds:+0.0;-0.0} s";
            DstDisplay = frame.DstActive ? "Active" : "Off";
            LeapSecondDisplay = frame.LeapSecondPending ? "Pending" : "None";

            int needed = 2;
            ConfidencePercent = Math.Min(100, (frame.ConfidenceFrames / (double)needed) * 100);
            ConfidenceDisplay = $"{Math.Min(frame.ConfidenceFrames, needed)} / {needed}";

            OnPropertyChanged(nameof(CanSetClock));

            if (frame.IsValid && frame.ConfidenceFrames >= needed)
                Log($"Frame valid: {t:yyyy-MM-dd HH:mm:ss} UTC  DUT1={frame.Dut1Seconds:+0.0;-0.0}s");
        });
    }

    private void RefreshLocalTime()
    {
        if (_latestFrame == null || !_latestFrame.IsValid) return;
        var localTime = _latestFrame.UtcTime.AddHours(_utcOffsetHours);
        LocalTimeDisplay = $"{localTime:HH:mm:ss}  ({_selectedUtcOffsetLabel})";
    }

    private void Log(string message)
    {
        var entry = $"{DateTime.Now:HH:mm:ss}  {DateTime.UtcNow:HH:mm:ss}Z  {message}";
        _fileLogger.WriteLine(entry);
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            // Trim oldest lines when the log exceeds 200 lines
            var lines = LogText.Length == 0
                ? []
                : new System.Collections.Generic.List<string>(LogText.Split('\n'));
            lines.Add(entry);
            if (lines.Count > 200)
                lines.RemoveRange(0, lines.Count - 200);
            LogText = string.Join("\n", lines);
            OnPropertyChanged(nameof(LogText));
        });
    }

    // ── INotifyPropertyChanged ────────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ── IDisposable ─────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _audioInput?.Dispose();
        _fileLogger?.Dispose();
        _diagLogger?.Dispose();
    }

    private DecoderRuntimeSettingsSnapshot GetDecoderSettings() =>
        new(
            EnableAgc: _enableInputAgc,
            EnableAdaptiveLowpass: _enableAdaptiveLowpass,
            InputTrimDb: _inputTrimDb);
}
