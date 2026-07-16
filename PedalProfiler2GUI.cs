// Pedal Profiler2 — GUI
//
// PreferWindowedGUI = true (separate resizable window). The window shows ONE
// selected machine in depth. Selector at top, cost panel with sparkline and
// Solo Measurement button, connections graph (1-hop neighbors, clickable to
// navigate), spike attribution row, parameter table with writes/sec, activity
// summary, global status footer, Profile-All button.
//
// Two state machines run on DispatcherTimer ticks:
//   - Solo Measurement: mute-all-others → wait → snapshot → restore
//   - Profile All:     loops Solo Measurement across every machine in song

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;

namespace WDE.PedalProfiler2
{
    // ─────────────────────────────────────────────────────────────────────────
    // GUI Factory — discovered by reflection. PreferWindowedGUI = true gives
    // us a separate resizable window instead of an embedded panel.
    // ─────────────────────────────────────────────────────────────────────────
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = true)]
    public class Profiler2GUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new Profiler2GUI();
    }


    public class Profiler2GUI : UserControl, IMachineGUI
    {
        // ─── Backing for IMachineGUI ─────────────────────────────────────────
        IMachine?         _iMachine;
        Profiler2Machine? _machine;
        IBuzz?            _subscribedBuzz;

        public IMachine Machine
        {
            get => _iMachine!;
            set
            {
                // Unsubscribe from old buzz
                UnhookMasterTap();
                if (_subscribedBuzz?.Song != null)
                {
                    try
                    {
                        _subscribedBuzz.Song.MachineAdded   -= OnMachineAdded;
                        _subscribedBuzz.Song.MachineRemoved -= OnMachineRemoved;
                    } catch { }
                }

                _iMachine = value;
                _machine  = value?.ManagedMachine as Profiler2Machine;

                _subscribedBuzz = value?.Graph?.Buzz;
                if (_subscribedBuzz?.Song != null)
                {
                    _subscribedBuzz.Song.MachineAdded   += OnMachineAdded;
                    _subscribedBuzz.Song.MachineRemoved += OnMachineRemoved;
                }
                HookMasterTap();

                // Pre-seed selection from persisted state BEFORE populating
                // the combo. RefreshMachineList will pick this up if the name
                // is in the song; otherwise it falls back to index 0.
                _selectedName = _machine?.PersistedSelection;

                RefreshMachineList();
            }
        }


        // ─── Dispatcher timer (100ms UI refresh) ─────────────────────────────
        readonly DispatcherTimer _timer;

        // ─── Flight Recorder fast timer (~50Hz cost sampling + auto-trigger) ─
        DispatcherTimer? _frCostTimer;
        System.Reflection.PropertyInfo? _isActiveProp;      bool _isActiveResolved;
        System.Reflection.PropertyInfo? _deadlineMissProp;
        System.Reflection.PropertyInfo? _deadlineOverrunProp; bool _deadlineResolved;
        long _lastDeadlineMiss = -1;   // -1 = not yet seeded

        // Flight Recorder opt-in UI
        CheckBox?   _frEnableCheckbox;
        TextBox?    _frPathBox;
        TextBlock?  _frStatusText;
        bool        _frUiSynced;       // one-shot: pull persisted state into the controls once


        // ─── Selected machine (by name; resolved on each tick) ──────────────
        string?   _selectedName;
        IMachine? _selectedIMachine;


        // ─── Per-selected-machine sparkline history (60s) ────────────────────
        // Sample every UI tick (100ms) → 600 samples for 60s. We keep 120
        // displayed; show the last 60s. Cost displayed is mute-delta in ms.
        const int HISTORY_LEN = 120;
        readonly double[] _machineCostHistory = new double[HISTORY_LEN];
        int               _machineCostIdx     = 0;
        bool              _machineCostFull    = false;
        double            _lastKnownMachineCost = double.NaN;


        // ─── Mute-delta tracking (passive) ───────────────────────────────────
        // Same idea as v1: when the SELECTED machine's mute state changes,
        // snap AvgOtherMs before/after, store delta as "marginal cost".
        bool   _lastSeenSelectedMuted = false;
        double _marginalCostMs        = double.NaN;
        bool   _muteDeltaPending      = false;
        bool   _muteDeltaWasMutedAtStart;
        double _muteDeltaCostBefore;
        DispatcherTimer? _muteDeltaTimer;


        // ─── Parameter activity tracking ─────────────────────────────────────
        // For the SELECTED machine: poll all parameter values every tick;
        // when changed, bump a write-rate counter; decay over time.
        readonly Dictionary<string, int>    _paramLastValue       = new();
        readonly Dictionary<string, double> _paramWritesPerSec    = new();
        readonly Dictionary<string, long>   _paramLastChangeTicks = new();
        long _lastParamPollTicks = 0;


        // ─── MasterTap subscription — actual master-bus audio analysis ───────
        // buzz.MasterTap delivers the rendered master output buffer each chunk.
        // We hook into it to extract live peak and RMS values for L and R.
        //
        // THREADING NOTE: in ReBuzz ≤1826 MasterTap fired on the AUDIO THREAD;
        // in 1827+ it is a proper event marshalled to the GUI thread via
        // dispatcher.BeginInvoke. We keep this handler allocation-free, lock-free
        // and exception-swallowed regardless — that's correct (and harmless) on
        // either thread, and means the same code is safe across both builds.
        // Volatile fields used for UI-thread reads (redundant but harmless when
        // the callback is already on the GUI thread).
        volatile float _masterPeakL;
        volatile float _masterPeakR;
        volatile float _masterRmsL;
        volatile float _masterRmsR;
        long           _masterTapCallCount;
        bool           _masterTapHooked;
        Delegate?      _masterTapDelegate;   // stored so Unhook can Remove the exact instance
        bool           _masterTapViaEvent;   // true = subscribed via event add/remove (1827+), false = field manipulation (1826)
        // Peak hold — the UI tick decays these slowly so the visible meter
        // doesn't flicker. Updated from UI thread.
        float          _masterPeakHoldL;
        float          _masterPeakHoldR;
        long           _masterPeakHoldLastTickMs;

        void OnMasterTap(float[] samples, bool stereo, SongTime time)
        {
            try
            {
                int n = samples.Length;
                if (n <= 0) return;

                float peakL = 0, peakR = 0;
                double sumSqL = 0, sumSqR = 0;

                if (stereo && n >= 2)
                {
                    int frames = n / 2;
                    for (int i = 0; i < n - 1; i += 2)
                    {
                        float l = samples[i];
                        float r = samples[i + 1];
                        float aL = l < 0 ? -l : l;
                        float aR = r < 0 ? -r : r;
                        if (aL > peakL) peakL = aL;
                        if (aR > peakR) peakR = aR;
                        sumSqL += (double)l * l;
                        sumSqR += (double)r * r;
                    }
                    // Buzz convention: samples are in ±32768 range (16-bit
                    // equivalent), not ±1.0. The ÷32768 conversion to dBFS
                    // happens at the driver boundary, *after* MasterTap.
                    _masterRmsL = (float)(Math.Sqrt(sumSqL / frames) / BUZZ_FULL_SCALE);
                    _masterRmsR = (float)(Math.Sqrt(sumSqR / frames) / BUZZ_FULL_SCALE);
                    _masterPeakL = peakL / BUZZ_FULL_SCALE;
                    _masterPeakR = peakR / BUZZ_FULL_SCALE;
                }
                else
                {
                    for (int i = 0; i < n; i++)
                    {
                        float v = samples[i];
                        float a = v < 0 ? -v : v;
                        if (a > peakL) peakL = a;
                        sumSqL += (double)v * v;
                    }
                    float rms = (float)(Math.Sqrt(sumSqL / n) / BUZZ_FULL_SCALE);
                    _masterRmsL = rms;
                    _masterRmsR = rms;
                    float peak = peakL / BUZZ_FULL_SCALE;
                    _masterPeakL = peak;
                    _masterPeakR = peak;
                }
                System.Threading.Interlocked.Increment(ref _masterTapCallCount);
            }
            catch
            {
                // Audio thread — silently swallow. Never throw.
            }
        }
        const float BUZZ_FULL_SCALE = 32768f;

        void HookMasterTap()
        {
            if (_masterTapHooked || _subscribedBuzz == null) return;
            try
            {
                const System.Reflection.BindingFlags FLAGS =
                    System.Reflection.BindingFlags.Public    |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance;

                var t = _subscribedBuzz.GetType();
                var methodInfo = GetType().GetMethod(
                    nameof(OnMasterTap),
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (methodInfo == null)
                {
                    try { _subscribedBuzz.DCWriteLine("[PP2] OnMasterTap method info lookup failed"); } catch { }
                    return;
                }

                // ReBuzz 1827+ declares MasterTap as a proper `event` (fired on
                // the GUI thread via dispatcher). Prefer the add/remove accessors
                // — they're atomic against other subscribers (HDRecorder, Signal
                // Analysis, About window). Fall back to direct field manipulation
                // for older builds where MasterTap was a plain Action field.
                var ev = t.GetEvent("MasterTap", FLAGS);
                if (ev != null && ev.EventHandlerType != null)
                {
                    try
                    {
                        var ours = Delegate.CreateDelegate(ev.EventHandlerType, this, methodInfo);
                        ev.AddEventHandler(_subscribedBuzz, ours);
                        _masterTapDelegate = ours;
                        _masterTapViaEvent = true;
                        _masterTapHooked   = true;
                        return;
                    }
                    catch (Exception ex)
                    {
                        try { _subscribedBuzz.DCWriteLine($"[PP2] MasterTap event subscribe failed: {ex.Message} — trying field"); } catch { }
                    }
                }

                // Fallback: plain field (1826 and earlier). CreateDelegate against
                // the field's exact delegate type sidesteps SongTime namespace
                // mismatch via structural signature matching.
                var fi = t.GetField("MasterTap", FLAGS);
                if (fi == null)
                {
                    try { _subscribedBuzz.DCWriteLine("[PP2] MasterTap not found as event or field"); } catch { }
                    return;
                }

                Delegate fieldDelegate;
                try
                {
                    fieldDelegate = Delegate.CreateDelegate(fi.FieldType, this, methodInfo);
                }
                catch (Exception ex)
                {
                    try { _subscribedBuzz.DCWriteLine($"[PP2] MasterTap delegate signature mismatch: {ex.Message}"); } catch { }
                    return;
                }

                var existing = fi.GetValue(_subscribedBuzz) as Delegate;
                fi.SetValue(_subscribedBuzz, Delegate.Combine(existing, fieldDelegate));
                _masterTapDelegate = fieldDelegate;
                _masterTapViaEvent = false;
                _masterTapHooked   = true;
            }
            catch (Exception ex)
            {
                try { _subscribedBuzz?.DCWriteLine($"[PP2] HookMasterTap failed: {ex.GetType().Name}: {ex.Message}"); } catch { }
            }
        }

        void UnhookMasterTap()
        {
            if (!_masterTapHooked || _subscribedBuzz == null) return;
            try
            {
                const System.Reflection.BindingFlags FLAGS =
                    System.Reflection.BindingFlags.Public    |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance;
                var t = _subscribedBuzz.GetType();

                if (_masterTapViaEvent)
                {
                    var ev = t.GetEvent("MasterTap", FLAGS);
                    if (ev != null && _masterTapDelegate != null)
                        ev.RemoveEventHandler(_subscribedBuzz, _masterTapDelegate);
                }
                else
                {
                    var fi = t.GetField("MasterTap", FLAGS);
                    if (fi != null && _masterTapDelegate != null)
                    {
                        var existing  = fi.GetValue(_subscribedBuzz) as Delegate;
                        var remaining = Delegate.Remove(existing, _masterTapDelegate);
                        fi.SetValue(_subscribedBuzz, remaining);
                    }
                }
            }
            catch { }
            _masterTapHooked   = false;
            _masterTapViaEvent = false;
            _masterTapDelegate = null;
        }


        // ─── Engine-reported per-machine cost (from MachinePerformanceData) ──
        // ReBuzz internally tracks (PerformanceCount, SampleCount) per machine
        // via PerformanceDataCurrent — both Int64, both cumulative. Reading the
        // delta over a known time window gives us the machine's actual DSP cost
        // with no muting, no measurement noise from window averaging, no
        // perturbation of the audio thread.
        //
        // PerformanceCount appears to be in Stopwatch.Frequency ticks (i.e.
        // QueryPerformanceCounter), so:
        //
        //   cpu_fraction = (delta_perf * sample_rate)
        //                  ─────────────────────────────
        //                  (delta_samp * QPC_frequency)
        //
        //   ms_per_buffer = cpu_fraction * budget_ms
        //
        // Reflection set up lazily — first read on a given machine resolves the
        // PerformanceDataCurrent property + PerformanceCount/SampleCount fields
        // and caches them per-type, so subsequent reads are direct.
        class EnginePerfState
        {
            public long LastPerformanceCount;
            public long LastSampleCount;
            public long LastReadTickMs;
            public bool HasPrior;
            public double SmoothedMsPerBuffer = double.NaN;
            public double SmoothedCpuPct      = double.NaN;
            public bool IsNative;   // true → engine perf data not tracked by host for this machine
        }
        readonly Dictionary<string, EnginePerfState> _enginePerf = new();

        // Type-level reflection cache. The performance objects are all of the
        // same concrete type per ReBuzz session, so we resolve once.
        System.Reflection.PropertyInfo? _perfDataCurrentProp;
        System.Reflection.FieldInfo?    _perfCountField;
        System.Reflection.FieldInfo?    _sampleCountField;
        bool _enginePerfResolved;
        bool _enginePerfAvailable;

        (long perf, long samp)? ReadEnginePerfRaw(IMachine m)
        {
            if (_enginePerfResolved && !_enginePerfAvailable) return null;

            try
            {
                if (_perfDataCurrentProp == null)
                {
                    _perfDataCurrentProp = m.GetType().GetProperty("PerformanceDataCurrent");
                    if (_perfDataCurrentProp == null) { _enginePerfResolved = true; return null; }
                }
                var perfObj = _perfDataCurrentProp.GetValue(m);
                if (perfObj == null) return null;  // some machines may not have data yet

                if (_perfCountField == null || _sampleCountField == null)
                {
                    var t = perfObj.GetType();
                    _perfCountField   = t.GetField("PerformanceCount");
                    _sampleCountField = t.GetField("SampleCount");
                    if (_perfCountField == null || _sampleCountField == null)
                    {
                        _enginePerfResolved = true;
                        return null;
                    }
                }

                long perf = (long)(_perfCountField.GetValue(perfObj) ?? 0L);
                long samp = (long)(_sampleCountField.GetValue(perfObj) ?? 0L);
                _enginePerfResolved = true;
                _enginePerfAvailable = true;
                return (perf, samp);
            }
            catch
            {
                _enginePerfResolved = true;
                return null;
            }
        }

        // Returns the smoothed (ms_per_buffer, cpu_pct) for the given machine,
        // or null when no valid delta is available yet. Updates the per-machine
        // EMA state as a side effect.
        (double msPerBuffer, double cpuPct)? UpdateEngineCost(IMachine m, double budgetMs)
        {
            if (m == null || _machine == null) return null;

            // Native machines (e.g. the Master) don't populate
            // MachinePerformanceData — values stay at zero forever. Detect
            // upfront by ManagedMachine == null and mark accordingly so the
            // UI can say so instead of showing "warming up" indefinitely.
            string name = m.Name;
            bool isNative = false;
            try { isNative = (m.ManagedMachine == null); } catch { }
            if (isNative)
            {
                if (!_enginePerf.TryGetValue(name, out var nativeSt))
                {
                    nativeSt = new EnginePerfState { IsNative = true };
                    _enginePerf[name] = nativeSt;
                }
                else
                {
                    nativeSt.IsNative = true;
                }
                return null;
            }

            var raw = ReadEnginePerfRaw(m);
            if (raw == null) return null;

            int sampleRate = _machine.Snapshot?.SampleRate ?? 0;
            if (sampleRate <= 0) return null;

            if (!_enginePerf.TryGetValue(name, out var st))
            {
                st = new EnginePerfState
                {
                    LastPerformanceCount = raw.Value.perf,
                    LastSampleCount      = raw.Value.samp,
                    LastReadTickMs       = Environment.TickCount64,
                    HasPrior             = true
                };
                _enginePerf[name] = st;
                return null;  // first sample — need two for a delta
            }

            long deltaPerf = raw.Value.perf - st.LastPerformanceCount;
            long deltaSamp = raw.Value.samp - st.LastSampleCount;
            st.LastPerformanceCount = raw.Value.perf;
            st.LastSampleCount      = raw.Value.samp;
            st.LastReadTickMs       = Environment.TickCount64;

            if (deltaSamp <= 0 || deltaPerf < 0) return null;  // paused or counter reset

            double qpcFreq = Stopwatch.Frequency;
            if (qpcFreq <= 0) return null;

            double cpuFraction = (deltaPerf * (double)sampleRate)
                                 / (deltaSamp * qpcFreq);
            double msPerBuffer = (budgetMs > 0) ? cpuFraction * budgetMs : double.NaN;
            double cpuPct      = cpuFraction * 100.0;

            // EMA smoothing — alpha 0.2 ≈ 1-second time constant at 100ms ticks
            const double alpha = 0.2;
            if (double.IsNaN(st.SmoothedMsPerBuffer))
            {
                st.SmoothedMsPerBuffer = msPerBuffer;
                st.SmoothedCpuPct      = cpuPct;
            }
            else
            {
                st.SmoothedMsPerBuffer = st.SmoothedMsPerBuffer * (1 - alpha) + msPerBuffer * alpha;
                st.SmoothedCpuPct      = st.SmoothedCpuPct      * (1 - alpha) + cpuPct      * alpha;
            }

            return (st.SmoothedMsPerBuffer, st.SmoothedCpuPct);
        }


        // ─── Measurement state machine — handles both Solo & Marginal modes ──
        // Solo:     mute ALL OTHER non-control machines, settle, read AvgOtherMs.
        //           Result = cost of target running alone.
        // Marginal: capture current AvgOtherMs as "before", mute ONLY the target,
        //           settle, read AvgOtherMs as "after", result = before − after.
        //           Subtracts the constant host/muted-machines overhead, giving
        //           a truer "what this machine contributes to the current mix".
        enum MeasureMode  { Solo, Marginal }
        enum MeasureState { Idle, MutingPhase, Settling, Measuring, Restoring, Done }

        MeasureState _measureState  = MeasureState.Idle;
        MeasureMode  _measureMode   = MeasureMode.Solo;
        DispatcherTimer? _measureTimer;
        Dictionary<string, bool>? _savedMuteStates;
        string? _measureTargetName;
        double  _measureBeforeMs    = double.NaN;   // marginal-mode "before" snapshot

        // Latest results (separate so both can persist for display)
        double _soloResultMs        = double.NaN;
        double _soloPeakMs          = double.NaN;   // peak per-buffer "other" within window at solo time
        long   _soloLastMeasureTime = 0;

        // Median-of-recent-readings, to dampen the per-measurement noise
        // discussed in the README. Last 5 solo readings, oldest overwritten.
        const int RECENT_SOLO_LEN = 5;
        readonly double[] _recentSolo = new double[RECENT_SOLO_LEN];
        int  _recentSoloIdx   = 0;
        int  _recentSoloCount = 0;


        // ─── Profile-all state machine ───────────────────────────────────────
        enum ProfileAllState { Idle, Running, Done }
        ProfileAllState _profileAllState = ProfileAllState.Idle;
        List<string>?   _profileAllQueue;
        int             _profileAllIdx;
        readonly Dictionary<string, double> _profileAllResults = new();
        double          _profileAllBudgetMs = double.NaN;  // captured at start, used for bar scaling


        // ═════════════════════════════════════════════════════════════════════
        // UI elements (built once in BuildUI, referenced by Refresh* methods)
        // ═════════════════════════════════════════════════════════════════════

        ComboBox    _machineSelector = null!;
        Button      _prevBtn         = null!;
        Button      _nextBtn         = null!;
        Button      _muteBtn         = null!;
        TextBlock   _typeBadge       = null!;

        TextBlock   _periodInfoText  = null!;   // "Buffer: 5.33 ms (256 spl @ 48 kHz, 64-buf avg)"
        TextBlock   _engineTotalText = null!;   // sum of engine-reported costs across all machines
        Canvas      _engineStackCanvas = null!; // visual stacked bar of per-machine engine cost vs host overhead
        TextBlock   _engineCostText  = null!;   // primary live engine-reported cost
        TextBlock   _engineSubText   = null!;   // "live · MComp"
        TextBlock   _soloCostText    = null!;
        TextBlock   _soloSubText     = null!;   // "peak X.XX ms · median Y.YY ms"
        TextBlock   _marginalCostText= null!;
        Button      _measureSoloBtn  = null!;
        Button      _measureMarginalBtn = null!;
        TextBlock   _measureStatus   = null!;
        Canvas      _sparklineCanvas = null!;

        WrapPanel  _upstreamPanel   = null!;
        WrapPanel  _downstreamPanel = null!;

        TextBlock   _spikeAttribText = null!;
        Button      _spikeListToggleBtn = null!;
        StackPanel  _spikeListPanel     = null!;
        bool        _spikeListExpanded  = false;

        StackPanel  _paramListPanel  = null!;

        TextBlock   _activityText    = null!;
        Canvas      _masterBarL      = null!;
        Canvas      _masterBarR      = null!;
        TextBlock   _masterValsL     = null!;
        TextBlock   _masterValsR     = null!;
        TextBlock   _masterStatusText = null!;

        TextBlock   _globalStatusText= null!;
        TextBlock   _engineSettingsText = null!;
        TextBlock?  _dumpStatusText;
        CheckBox?   _verboseDumpCheckbox;
        TextBlock   _gcStatusText    = null!;

        Button      _profileAllBtn   = null!;
        Button      _profileAllEngineBtn = null!;
        TextBlock   _profileAllStatus= null!;
        StackPanel  _profileAllResultsPanel = null!;


        // ═════════════════════════════════════════════════════════════════════
        // Dark theme — match v1's palette
        // ═════════════════════════════════════════════════════════════════════
        static readonly Brush BrushBg       = Frozen(new SolidColorBrush(Color.FromRgb( 24, 26, 30)));
        static readonly Brush BrushPanel    = Frozen(new SolidColorBrush(Color.FromRgb( 32, 35, 40)));
        static readonly Brush BrushTrack    = Frozen(new SolidColorBrush(Color.FromRgb( 40, 43, 48)));
        static readonly Brush BrushBorder   = Frozen(new SolidColorBrush(Color.FromRgb( 60, 65, 72)));
        static readonly Brush BrushText     = Frozen(new SolidColorBrush(Color.FromRgb(200,205,215)));
        static readonly Brush BrushSubText  = Frozen(new SolidColorBrush(Color.FromRgb(140,148,160)));
        static readonly Brush BrushHeader   = Frozen(new SolidColorBrush(Color.FromRgb(180,210,230)));
        static readonly Brush BrushAccent   = Frozen(new SolidColorBrush(Color.FromRgb( 90,170,230)));
        static readonly Brush BrushOk       = Frozen(new SolidColorBrush(Color.FromRgb(110,200,130)));
        static readonly Brush BrushWarn     = Frozen(new SolidColorBrush(Color.FromRgb(230,180, 80)));
        static readonly Brush BrushBad      = Frozen(new SolidColorBrush(Color.FromRgb(230,100,100)));
        static readonly Brush BrushMuted    = Frozen(new SolidColorBrush(Color.FromRgb(110,115,125)));
        static readonly Brush BrushFlash    = Frozen(new SolidColorBrush(Color.FromRgb(230,210,120)));
        static readonly Brush BrushSparkline= Frozen(new SolidColorBrush(Color.FromRgb( 90,170,230)));

        static readonly FontFamily Mono = new FontFamily("Consolas");

        static Brush Frozen(SolidColorBrush b) { b.Freeze(); return b; }


        // ═════════════════════════════════════════════════════════════════════
        // Constructor — build UI, start dispatcher timer
        // ═════════════════════════════════════════════════════════════════════
        public Profiler2GUI()
        {
            Background = BrushBg;
            Foreground = BrushText;
            MinWidth   = 420;
            MinHeight  = 580;
            Width      = 520;
            Height     = 780;

            BuildUI();

            _timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _timer.Tick += OnTick;
            _timer.Start();

            // Flight Recorder: fast cost sampling + deadline-miss auto-trigger,
            // decoupled from the heavy 100ms UI refresh.
            _frCostTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(20)   // ~50Hz nominal
            };
            _frCostTimer.Tick += OnFrCostTick;
            _frCostTimer.Start();

            Unloaded += (_, __) =>
            {
                _timer.Stop();
                _frCostTimer?.Stop();
                _measureTimer?.Stop();
                _muteDeltaTimer?.Stop();
                UnhookMasterTap();

                if (_subscribedBuzz?.Song != null)
                {
                    try
                    {
                        _subscribedBuzz.Song.MachineAdded   -= OnMachineAdded;
                        _subscribedBuzz.Song.MachineRemoved -= OnMachineRemoved;
                    } catch { }
                }
            };
        }


        // ═════════════════════════════════════════════════════════════════════
        // UI construction
        // ═════════════════════════════════════════════════════════════════════
        void BuildUI()
        {
            var root = new StackPanel { Margin = new Thickness(10) };

            // ── Title ────────────────────────────────────────────────────────
            root.Children.Add(new TextBlock
            {
                Text       = "PEDAL PROFILER2 — MACHINE INSPECTOR",
                Foreground = BrushHeader,
                FontFamily = Mono,
                FontSize   = 12,
                FontWeight = FontWeights.Bold,
                Margin     = new Thickness(0, 0, 0, 8)
            });

            // ── Selector row ─────────────────────────────────────────────────
            var selRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            selRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            selRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            selRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            selRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            selRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _prevBtn = MakeButton("◀", 28);
            _prevBtn.Click += (_, __) => CycleSelection(-1);
            Grid.SetColumn(_prevBtn, 0);
            selRow.Children.Add(_prevBtn);

            _machineSelector = new ComboBox
            {
                Margin       = new Thickness(4, 0, 4, 0),
                Background   = BrushPanel,
                Foreground   = BrushText,
                BorderBrush  = BrushBorder,
                FontFamily   = Mono,
                FontSize     = 11
            };
            _machineSelector.SelectionChanged += (_, __) =>
            {
                if (_machineSelector.SelectedItem is string s)
                    SelectMachine(s);
            };
            Grid.SetColumn(_machineSelector, 1);
            selRow.Children.Add(_machineSelector);

            _nextBtn = MakeButton("▶", 28);
            _nextBtn.Click += (_, __) => CycleSelection(+1);
            Grid.SetColumn(_nextBtn, 2);
            selRow.Children.Add(_nextBtn);

            _typeBadge = new TextBlock
            {
                Text       = "—",
                Foreground = BrushSubText,
                FontFamily = Mono,
                FontSize   = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin     = new Thickness(8, 0, 4, 0),
                MinWidth   = 38,
                TextAlignment = TextAlignment.Center
            };
            Grid.SetColumn(_typeBadge, 3);
            selRow.Children.Add(_typeBadge);

            _muteBtn = MakeButton("MUTE", 50);
            _muteBtn.Click += (_, __) => ToggleSelectedMute();
            Grid.SetColumn(_muteBtn, 4);
            selRow.Children.Add(_muteBtn);

            root.Children.Add(selRow);

            // ── Cost panel ───────────────────────────────────────────────────
            root.Children.Add(SectionHeader("Cost"));

            // Buffer/budget info — the denominator everything else is measured against.
            // Format: "Buffer: 5.33 ms  (256 spl @ 48 kHz · 64-buf avg)"
            _periodInfoText = new TextBlock
            {
                Text       = "Buffer: —",
                Foreground = BrushSubText,
                FontFamily = Mono,
                FontSize   = 10,
                Margin     = new Thickness(0, 0, 0, 6)
            };
            root.Children.Add(_periodInfoText);

            // Engine Total — sum of engine-reported cost across all non-control
            // non-native machines. Compared against the buffer budget, the gap
            // is "unaccounted-for" buffer time — host overhead, idle waits,
            // driver lag. Critical for diagnosing whether dropouts are caused
            // by machine work or by host-level pressure.
            _engineTotalText = new TextBlock
            {
                Text       = "Engine Total: —",
                Foreground = BrushSubText,
                FontFamily = Mono,
                FontSize   = 10,
                Margin     = new Thickness(0, 0, 0, 2)
            };
            root.Children.Add(_engineTotalText);

            // Visual stacked bar — each machine gets a colored segment sized
            // proportional to its smoothed cost; the rest of the bar (in red)
            // is unaccounted host overhead. ToolTips on each segment show
            // the contributing machine name and ms value.
            _engineStackCanvas = new Canvas
            {
                Height     = 14,
                Margin     = new Thickness(0, 0, 0, 6),
                Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a))
            };
            _engineStackCanvas.Background.Freeze();
            // Redraw on resize so the bar tracks panel width
            _engineStackCanvas.SizeChanged += (_, __) => RefreshEngineStack();
            root.Children.Add(_engineStackCanvas);

            var costGrid = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            costGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            costGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            costGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // ENGINE — primary live reading from MachinePerformanceData
            var engineCol = new StackPanel();
            engineCol.Children.Add(new TextBlock { Text = "ENGINE", Foreground = BrushSubText, FontFamily = Mono, FontSize = 9 });
            _engineCostText = new TextBlock { Text = "—", Foreground = BrushAccent, FontFamily = Mono, FontSize = 14, FontWeight = FontWeights.Bold };
            engineCol.Children.Add(_engineCostText);
            _engineSubText = new TextBlock { Text = "", Foreground = BrushSubText, FontFamily = Mono, FontSize = 9 };
            engineCol.Children.Add(_engineSubText);
            Grid.SetColumn(engineCol, 0);
            costGrid.Children.Add(engineCol);

            // SOLO — snapshot via mute-all-others
            var soloCol = new StackPanel();
            soloCol.Children.Add(new TextBlock { Text = "SOLO", Foreground = BrushSubText, FontFamily = Mono, FontSize = 9 });
            _soloCostText = new TextBlock { Text = "—", Foreground = BrushAccent, FontFamily = Mono, FontSize = 14, FontWeight = FontWeights.Bold };
            soloCol.Children.Add(_soloCostText);
            _soloSubText = new TextBlock { Text = "", Foreground = BrushSubText, FontFamily = Mono, FontSize = 9 };
            soloCol.Children.Add(_soloSubText);
            Grid.SetColumn(soloCol, 1);
            costGrid.Children.Add(soloCol);

            // MARGINAL — what muting THIS would save
            var marginalCol = new StackPanel();
            marginalCol.Children.Add(new TextBlock { Text = "MARGINAL", Foreground = BrushSubText, FontFamily = Mono, FontSize = 9 });
            _marginalCostText = new TextBlock { Text = "—", Foreground = BrushOk, FontFamily = Mono, FontSize = 14, FontWeight = FontWeights.Bold };
            marginalCol.Children.Add(_marginalCostText);
            Grid.SetColumn(marginalCol, 2);
            costGrid.Children.Add(marginalCol);

            root.Children.Add(costGrid);

            // Measure buttons — both go through the same state machine via mode
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            _measureSoloBtn = MakeButton("Measure Solo", 110);
            _measureSoloBtn.Click += (_, __) => StartMeasurement(MeasureMode.Solo, null);
            btnRow.Children.Add(_measureSoloBtn);

            _measureMarginalBtn = MakeButton("Measure Marginal", 130);
            _measureMarginalBtn.Margin = new Thickness(6, 0, 0, 0);
            _measureMarginalBtn.Click += (_, __) => StartMeasurement(MeasureMode.Marginal, null);
            btnRow.Children.Add(_measureMarginalBtn);
            root.Children.Add(btnRow);

            _measureStatus = new TextBlock
            {
                Text       = "",
                Foreground = BrushWarn,
                FontFamily = Mono,
                FontSize   = 10,
                Margin     = new Thickness(0, 0, 0, 4)
            };
            root.Children.Add(_measureStatus);

            // ── Sparkline ────────────────────────────────────────────────────
            var sparkBox = new Border
            {
                BorderBrush     = BrushBorder,
                BorderThickness = new Thickness(1),
                Background      = BrushTrack,
                Margin          = new Thickness(0, 0, 0, 4),
                Height          = 48
            };
            _sparklineCanvas = new Canvas { ClipToBounds = true };
            _sparklineCanvas.SizeChanged += (_, __) => DrawSparkline();
            sparkBox.Child = _sparklineCanvas;
            root.Children.Add(sparkBox);

            root.Children.Add(new TextBlock
            {
                Text       = "marginal cost over time (60 s) — populated by mute toggles or measurements",
                Foreground = BrushSubText,
                FontFamily = Mono,
                FontSize   = 9,
                Margin     = new Thickness(0, 0, 0, 10)
            });

            // ── Connections ──────────────────────────────────────────────────
            root.Children.Add(SectionHeader("Connections"));

            var upWrap = new StackPanel { Margin = new Thickness(0, 0, 0, 2) };
            upWrap.Children.Add(new TextBlock { Text = "↑ FROM", Foreground = BrushSubText, FontFamily = Mono, FontSize = 9 });
            _upstreamPanel = new WrapPanel { Orientation = Orientation.Horizontal };
            upWrap.Children.Add(_upstreamPanel);
            root.Children.Add(upWrap);

            root.Children.Add(new TextBlock
            {
                Text       = "── THIS ──",
                Foreground = BrushAccent,
                FontFamily = Mono,
                FontSize   = 10,
                FontWeight = FontWeights.Bold,
                Margin     = new Thickness(0, 2, 0, 2)
            });

            var dnWrap = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            dnWrap.Children.Add(new TextBlock { Text = "↓ TO", Foreground = BrushSubText, FontFamily = Mono, FontSize = 9 });
            _downstreamPanel = new WrapPanel { Orientation = Orientation.Horizontal };
            dnWrap.Children.Add(_downstreamPanel);
            root.Children.Add(dnWrap);

            // ── Spike attribution ────────────────────────────────────────────
            root.Children.Add(SectionHeader("Spike Attribution"));
            _spikeAttribText = new TextBlock
            {
                Text         = "—",
                Foreground   = BrushText,
                FontFamily   = Mono,
                FontSize     = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 2)
            };
            root.Children.Add(_spikeAttribText);

            // Expand/collapse toggle for the spike list. Collapsed by default —
            // most users only need the summary count above.
            _spikeListToggleBtn = new Button
            {
                Content      = "▶ show spike list",
                Background   = Brushes.Transparent,
                BorderBrush  = Brushes.Transparent,
                Foreground   = BrushSubText,
                FontFamily   = Mono,
                FontSize     = 10,
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding      = new Thickness(0, 0, 0, 0),
                Cursor       = Cursors.Hand
            };
            _spikeListToggleBtn.Click += (_, __) =>
            {
                _spikeListExpanded = !_spikeListExpanded;
                _spikeListToggleBtn.Content = _spikeListExpanded ? "▼ hide spike list" : "▶ show spike list";
                _spikeListPanel.Visibility = _spikeListExpanded ? Visibility.Visible : Visibility.Collapsed;
                if (_spikeListExpanded) RefreshSpikeList();
            };
            root.Children.Add(_spikeListToggleBtn);

            _spikeListPanel = new StackPanel
            {
                Visibility = Visibility.Collapsed,
                Margin     = new Thickness(0, 2, 0, 10)
            };
            root.Children.Add(_spikeListPanel);

            // ── Parameters ───────────────────────────────────────────────────
            root.Children.Add(SectionHeader("Parameters"));
            var paramScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 220,
                Margin    = new Thickness(0, 0, 0, 10)
            };
            _paramListPanel = new StackPanel();
            paramScroll.Content = _paramListPanel;
            root.Children.Add(paramScroll);

            // ── Activity ─────────────────────────────────────────────────────
            root.Children.Add(SectionHeader("Activity"));
            _activityText = new TextBlock
            {
                Text       = "—",
                Foreground = BrushText,
                FontFamily = Mono,
                FontSize   = 11,
                Margin     = new Thickness(0, 0, 0, 10)
            };
            root.Children.Add(_activityText);

            // ── Master Output VU ─────────────────────────────────────────────
            // Live peak + RMS of the actual master-bus audio, captured via the
            // buzz.MasterTap hook (audio thread). Real WPF-rectangle VU meters,
            // not Unicode text — block characters did font fallback in the
            // text path which made bar widths unreliable.
            root.Children.Add(SectionHeader("Master Output"));

            var masterGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            masterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            masterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            masterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            masterGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            masterGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Row 0 — Left channel
            var lLabel = new TextBlock
            {
                Text = "L ",
                Foreground = BrushSubText,
                FontFamily = Mono,
                FontSize   = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin     = new Thickness(0, 1, 4, 1)
            };
            Grid.SetRow(lLabel, 0); Grid.SetColumn(lLabel, 0);
            masterGrid.Children.Add(lLabel);

            _masterBarL = new Canvas
            {
                Height = 14,
                Margin = new Thickness(0, 1, 4, 1),
                Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a))
            };
            _masterBarL.Background.Freeze();
            _masterBarL.SizeChanged += (_, __) => RefreshMasterOutput();
            Grid.SetRow(_masterBarL, 0); Grid.SetColumn(_masterBarL, 1);
            masterGrid.Children.Add(_masterBarL);

            _masterValsL = new TextBlock
            {
                Text = "—",
                Foreground = BrushText,
                FontFamily = Mono,
                FontSize   = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin     = new Thickness(4, 1, 0, 1)
            };
            Grid.SetRow(_masterValsL, 0); Grid.SetColumn(_masterValsL, 2);
            masterGrid.Children.Add(_masterValsL);

            // Row 1 — Right channel
            var rLabel = new TextBlock
            {
                Text = "R ",
                Foreground = BrushSubText,
                FontFamily = Mono,
                FontSize   = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin     = new Thickness(0, 1, 4, 1)
            };
            Grid.SetRow(rLabel, 1); Grid.SetColumn(rLabel, 0);
            masterGrid.Children.Add(rLabel);

            _masterBarR = new Canvas
            {
                Height = 14,
                Margin = new Thickness(0, 1, 4, 1),
                Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a))
            };
            _masterBarR.Background.Freeze();
            _masterBarR.SizeChanged += (_, __) => RefreshMasterOutput();
            Grid.SetRow(_masterBarR, 1); Grid.SetColumn(_masterBarR, 1);
            masterGrid.Children.Add(_masterBarR);

            _masterValsR = new TextBlock
            {
                Text = "—",
                Foreground = BrushText,
                FontFamily = Mono,
                FontSize   = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin     = new Thickness(4, 1, 0, 1)
            };
            Grid.SetRow(_masterValsR, 1); Grid.SetColumn(_masterValsR, 2);
            masterGrid.Children.Add(_masterValsR);

            root.Children.Add(masterGrid);

            _masterStatusText = new TextBlock
            {
                Text = "",
                Foreground = BrushSubText,
                FontFamily = Mono,
                FontSize   = 10,
                Margin     = new Thickness(0, 0, 0, 10)
            };
            root.Children.Add(_masterStatusText);

            // ── Profile-All ──────────────────────────────────────────────────
            root.Children.Add(SectionHeader("Profile All Machines"));

            var profRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };

            _profileAllEngineBtn = MakeButton("Profile All (Engine)", 150);
            _profileAllEngineBtn.Click += (_, __) => StartProfileAllEngine();
            profRow.Children.Add(_profileAllEngineBtn);

            _profileAllBtn = MakeButton("Profile All (Solo)", 130);
            _profileAllBtn.Margin = new Thickness(6, 0, 0, 0);
            _profileAllBtn.Click += (_, __) => StartProfileAll();
            profRow.Children.Add(_profileAllBtn);

            _profileAllStatus = new TextBlock
            {
                Text       = "",
                Foreground = BrushWarn,
                FontFamily = Mono,
                FontSize   = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin     = new Thickness(8, 0, 0, 0)
            };
            profRow.Children.Add(_profileAllStatus);
            root.Children.Add(profRow);

            _profileAllResultsPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            root.Children.Add(_profileAllResultsPanel);

            // ── Diagnostics ──────────────────────────────────────────────────
            // Reflection-dump button: walks Global.Buzz, the selected machine's
            // ── Buffer Sweep Log ────────────────────────────────────────────
            // Automatically records (buffer, budget, unaccounted, dropout-rate)
            // each time the user changes ASIO buffer size, after ~3 seconds
            // of stable measurement. Builds a table showing how host overhead
            // and dropout rate scale with buffer size. If unaccounted stays
            // roughly constant → fixed-cost host overhead; if it scales with
            // buffer → per-sample work.
            root.Children.Add(SectionHeader("Buffer Sweep Log"));
            _sweepStatusText = new TextBlock
            {
                Text       = "(change ASIO buffer size in ReBuzz to record a sample)",
                Foreground = BrushSubText,
                FontFamily = Mono,
                FontSize   = 10,
                Margin     = new Thickness(0, 0, 0, 4)
            };
            root.Children.Add(_sweepStatusText);

            var sweepCtrlRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            var sweepClearBtn = MakeButton("Clear Log", 80);
            sweepClearBtn.Click += (_, __) => { _sweepLog.Clear(); RefreshSweepDisplay(); };
            sweepCtrlRow.Children.Add(sweepClearBtn);
            var sweepRecordBtn = MakeButton("Force Record", 110);
            sweepRecordBtn.Margin = new Thickness(6, 0, 0, 0);
            sweepRecordBtn.Click += (_, __) => RecordSweepSample(force: true);
            sweepCtrlRow.Children.Add(sweepRecordBtn);
            root.Children.Add(sweepCtrlRow);

            _sweepLogPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            root.Children.Add(_sweepLogPanel);

            // ── Diagnostics ──────────────────────────────────────────────────
            // Reflection-dump button: walks Global.Buzz, the selected machine's
            // MachineCore, and a parameter's ParameterCore via reflection and
            // writes everything to the DC console. Maps the actual reachable
            // surface of ReBuzz internals so we can pick which fields are worth
            // exposing properly (e.g. per-machine Work() timing if it exists).
            root.Children.Add(SectionHeader("Diagnostics"));
            var diagRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            var dumpBtn = MakeButton("Dump → File + Clipboard", 200);
            dumpBtn.Click += (_, __) => DumpInternals();
            diagRow.Children.Add(dumpBtn);
            _verboseDumpCheckbox = new CheckBox
            {
                Content    = "verbose (Phase 2 reflection)",
                Foreground = BrushSubText,
                FontFamily = Mono,
                FontSize   = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin     = new Thickness(10, 0, 0, 0),
                IsChecked  = false
            };
            diagRow.Children.Add(_verboseDumpCheckbox);
            _dumpStatusText = new TextBlock
            {
                Text       = "writes timestamped .txt to Documents\\ReBuzz PP2 Dumps + clipboard",
                Foreground = BrushSubText,
                FontFamily = Mono,
                FontSize   = 10,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin     = new Thickness(8, 0, 0, 0)
            };
            diagRow.Children.Add(_dumpStatusText);
            root.Children.Add(diagRow);

            // ── Flight Recorder (opt-in glitch capture) ──────────────────────
            root.Children.Add(SectionHeader("Flight Recorder"));
            var frRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            _frEnableCheckbox = new CheckBox
            {
                Content    = "Enable capture",
                Foreground = BrushSubText,
                FontFamily = Mono,
                FontSize   = 11,
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked  = false
            };
            _frEnableCheckbox.Checked   += (_, __) => { if (_machine != null) _machine.FrEnabled = true;  _lastDeadlineMiss = -1; UpdateFrStatus(); };
            _frEnableCheckbox.Unchecked += (_, __) => { if (_machine != null) _machine.FrEnabled = false; UpdateFrStatus(); };
            frRow.Children.Add(_frEnableCheckbox);

            var frOpenBtn = new Button
            {
                Content = "Open folder",
                FontFamily = Mono, FontSize = 10,
                Margin  = new Thickness(12, 0, 0, 0),
                Padding = new Thickness(6, 1, 6, 1)
            };
            frOpenBtn.Click += (_, __) => OpenFrFolder();
            frRow.Children.Add(frOpenBtn);
            root.Children.Add(frRow);

            var frPathRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            frPathRow.Children.Add(new TextBlock
            {
                Text = "Folder:", Foreground = BrushSubText, FontFamily = Mono, FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center
            });
            _frPathBox = new TextBox
            {
                Text       = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pp2_fr"),
                FontFamily = Mono, FontSize = 10, MinWidth = 300,
                Margin     = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _frPathBox.TextChanged += (_, __) => { if (_machine != null) _machine.FrOutputDir = _frPathBox.Text; UpdateFrStatus(); };
            frPathRow.Children.Add(_frPathBox);
            root.Children.Add(frPathRow);

            _frStatusText = new TextBlock
            {
                Text = "Off — window can stay open with no files written (default).",
                Foreground = BrushSubText, FontFamily = Mono, FontSize = 10,
                TextWrapping = TextWrapping.Wrap
            };
            root.Children.Add(_frStatusText);

            // ── Global status footer ─────────────────────────────────────────
            var footerStack = new StackPanel();
            footerStack.Children.Add(_globalStatusText = new TextBlock
            {
                Text       = "Global: — CPU · — dropouts · — spikes",
                Foreground = BrushSubText,
                FontFamily = Mono,
                FontSize   = 10
            });
            footerStack.Children.Add(_engineSettingsText = new TextBlock
            {
                Text       = "Engine: …",
                Foreground = BrushSubText,
                FontFamily = Mono,
                FontSize   = 10,
                Margin     = new Thickness(0, 2, 0, 0)
            });
            footerStack.Children.Add(_gcStatusText = new TextBlock
            {
                Text       = "GC: …",
                Foreground = BrushSubText,
                FontFamily = Mono,
                FontSize   = 10,
                Margin     = new Thickness(0, 2, 0, 0)
            });
            root.Children.Add(new Border
            {
                Background = BrushPanel,
                BorderBrush = BrushBorder,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 8, 0, 0),
                Child = footerStack
            });

            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = root
            };
        }


        // ─── Helpers ─────────────────────────────────────────────────────────
        static Border SectionHeader(string title)
        {
            return new Border
            {
                Background = BrushPanel,
                Margin     = new Thickness(0, 4, 0, 4),
                Padding    = new Thickness(6, 2, 6, 2),
                Child      = new TextBlock
                {
                    Text       = title,
                    Foreground = BrushHeader,
                    FontFamily = Mono,
                    FontSize   = 10,
                    FontWeight = FontWeights.Bold
                }
            };
        }

        static Button MakeButton(string text, double minWidth)
        {
            var b = new Button
            {
                Content     = text,
                MinWidth    = minWidth,
                Padding     = new Thickness(8, 2, 8, 2),
                Background  = BrushPanel,
                Foreground  = BrushText,
                BorderBrush = BrushBorder,
                FontFamily  = Mono,
                FontSize    = 11,
                Cursor      = Cursors.Hand
            };
            return b;
        }


        // ═════════════════════════════════════════════════════════════════════
        // Song.Machines event handlers (UI thread)
        // ═════════════════════════════════════════════════════════════════════
        void OnMachineAdded  (IMachine m) => RefreshMachineList();
        void OnMachineRemoved(IMachine m) => RefreshMachineList();


        void RefreshMachineList()
        {
            if (_subscribedBuzz?.Song == null) return;
            List<string> names;
            try { names = _subscribedBuzz.Song.Machines.Select(m => m.Name).OrderBy(n => n).ToList(); }
            catch { return; }

            // Preserve current selection if still present
            var prevSel = _selectedName;
            _machineSelector.SelectionChanged -= OnSelectorChanged;   // suppress recursion (no-op; we don't subscribe by name here, kept defensive)
            _machineSelector.Items.Clear();
            foreach (var n in names) _machineSelector.Items.Add(n);

            if (prevSel != null && names.Contains(prevSel))
                _machineSelector.SelectedItem = prevSel;
            else if (names.Count > 0)
                _machineSelector.SelectedIndex = 0;
        }

        void OnSelectorChanged(object? s, SelectionChangedEventArgs e) { /* placeholder for symmetry */ }


        void SelectMachine(string name)
        {
            if (_selectedName == name && _selectedIMachine != null) return;

            _selectedName     = name;
            _selectedIMachine = null;
            if (_subscribedBuzz?.Song != null)
            {
                try { _selectedIMachine = _subscribedBuzz.Song.Machines.FirstOrDefault(m => m.Name == name); }
                catch { }
            }

            // Persist back to the machine — gets serialized into the song
            // file on next save via MachineState getter.
            if (_machine != null) _machine.PersistedSelection = name;

            // Reset per-selection state
            _machineCostIdx        = 0;
            _machineCostFull       = false;
            Array.Clear(_machineCostHistory, 0, _machineCostHistory.Length);
            _lastKnownMachineCost  = double.NaN;
            _marginalCostMs        = double.NaN;
            _soloResultMs          = double.NaN;
            _soloPeakMs            = double.NaN;
            _recentSoloIdx         = 0;
            _recentSoloCount       = 0;
            _paramLastValue.Clear();
            _paramWritesPerSec.Clear();
            _paramLastChangeTicks.Clear();
            if (_selectedIMachine != null)
            {
                try { _lastSeenSelectedMuted = _selectedIMachine.IsMuted; } catch { _lastSeenSelectedMuted = false; }
            }

            RefreshConnections();
            RefreshParameterRows();
            DrawSparkline();
        }


        void CycleSelection(int dir)
        {
            int n = _machineSelector.Items.Count;
            if (n == 0) return;
            int i = _machineSelector.SelectedIndex;
            if (i < 0) i = 0;
            i = ((i + dir) % n + n) % n;
            _machineSelector.SelectedIndex = i;
        }


        void ToggleSelectedMute()
        {
            if (_selectedIMachine == null) return;
            try { _selectedIMachine.IsMuted = !_selectedIMachine.IsMuted; } catch { }
        }


        // ═════════════════════════════════════════════════════════════════════
        // Main UI tick (100ms)
        // ═════════════════════════════════════════════════════════════════════
        void OnTick(object? sender, EventArgs e)
        {
            if (_machine == null) return;

            // One-shot: pull persisted FR state (from MachineState) into the controls.
            if (!_frUiSynced && _frEnableCheckbox != null)
            {
                _frEnableCheckbox.IsChecked = _machine.FrEnabled;
                if (_frPathBox != null && !string.IsNullOrEmpty(_machine.FrOutputDir))
                    _frPathBox.Text = _machine.FrOutputDir;
                _frUiSynced = true;
                UpdateFrStatus();
            }

            var snap = _machine.Snapshot;
            if (snap == null) return;

            // ── Publish active machines list for spike attribution ───────────
            PublishActiveMachinesIfDue();

            // ── Resolve selection if a name is set but reference went stale ─
            if (_selectedName != null && _selectedIMachine == null)
            {
                if (_subscribedBuzz?.Song != null)
                {
                    try { _selectedIMachine = _subscribedBuzz.Song.Machines.FirstOrDefault(m => m.Name == _selectedName); }
                    catch { }
                }
            }

            // ── Mute-delta detection on selected machine ────────────────────
            DetectMuteDelta(snap);

            // ── Engine-reported cost (from MachinePerformanceData) ──────────
            // Poll every machine each tick (not just selected) so the Engine
            // Total sum is always live. Cost is tiny — cached reflection +
            // two field reads per machine — but lets us see what fraction of
            // the buffer is unaccounted for (= host overhead).
            if (_subscribedBuzz?.Song != null)
            {
                try
                {
                    foreach (var m in _subscribedBuzz.Song.Machines)
                    {
                        try
                        {
                            if (m.IsControlMachine) continue;
                            UpdateEngineCost(m, snap.BudgetMs);
                        } catch { }
                    }
                } catch { }
            }

            // ── Parameter activity poll for selected machine ────────────────
            PollParameterActivity();

            // ── Sparkline: capture marginal cost samples over time ──────────
            UpdateMachineCostHistory();

            // ── Push to UI fields ────────────────────────────────────────────
            RefreshHeader();
            RefreshCostPanel();
            RefreshSpikeAttribution(snap);
            RefreshParameterValues();
            RefreshActivity();
            RefreshMasterOutput();
            RefreshGlobalStatus(snap);
            UpdateSweep(snap);
            DrawSparkline();
        }


        // ═════════════════════════════════════════════════════════════════════
        // Active-machines publication for spike attribution
        // ═════════════════════════════════════════════════════════════════════
        void PublishActiveMachinesIfDue()
        {
            // Every tick is fine — list is small, allocation modest.
            if (_machine == null || _subscribedBuzz?.Song == null) return;
            try
            {
                var active = _subscribedBuzz.Song.Machines
                    .Where(m => !m.IsControlMachine)
                    .Where(m => { try { return !m.IsMuted; } catch { return false; } })
                    .Select(m => m.Name)
                    .ToArray();
                _machine.PublishActiveMachines(active);
            } catch { }
        }


        // ═════════════════════════════════════════════════════════════════════
        // Flight Recorder fast tick (~50Hz): cost sampling on the IsActive set,
        // plus latency-free auto-trigger on real ASIO deadline misses.
        // ═════════════════════════════════════════════════════════════════════
        bool ReadIsActive(IMachine m)
        {
            try
            {
                if (!_isActiveResolved)
                {
                    _isActiveProp = m.GetType().GetProperty("IsActive");
                    _isActiveResolved = true;
                }
                if (_isActiveProp == null) return false;
                return _isActiveProp.GetValue(m) is bool b && b;
            }
            catch { return false; }
        }

        void OnFrCostTick(object? sender, EventArgs e)
        {
            if (_machine == null || !_machine.FrEnabled || _subscribedBuzz?.Song == null) return;

            // Only sample while playing — keeps the baseline denominator honest
            // (stopped time would otherwise dilute every machine's base rate).
            bool playing = false;
            try { playing = _subscribedBuzz.Playing; } catch { }
            if (!playing) return;

            // Active-set = ReBuzz's own per-machine activity flag (producing
            // output), NOT mute state. Read via reflection in case IsActive isn't
            // on the public IMachine interface.
            try
            {
                var active = _subscribedBuzz.Song.Machines
                    .Where(m => { try { return !m.IsControlMachine && ReadIsActive(m); } catch { return false; } })
                    .Select(m => m.Name)
                    .ToArray();
                _machine.FlightRecorderCostSample(active);
            }
            catch { }

            // Deadline-miss auto-trigger: fire a mark the instant ReBuzz's real
            // ASIO deadline-miss counter increments (unlike the cadence-inflated
            // internal-budget dropout tally — this is the ground-truth trigger).
            try
            {
                var buzz = _subscribedBuzz;
                if (!_deadlineResolved)
                {
                    _deadlineMissProp    = buzz.GetType().GetProperty("DeadlineMissCount");
                    _deadlineOverrunProp = buzz.GetType().GetProperty("DeadlineWorstOverrunMicros");
                    _deadlineResolved = true;
                }
                if (_deadlineMissProp != null)
                {
                    long miss = Convert.ToInt64(_deadlineMissProp.GetValue(buzz));
                    if (_lastDeadlineMiss < 0)
                    {
                        _lastDeadlineMiss = miss;          // seed — don't fire on the first read
                    }
                    else if (miss > _lastDeadlineMiss)
                    {
                        _lastDeadlineMiss = miss;
                        if (_deadlineOverrunProp != null)
                            try { _machine.LastDeadlineOverrunUs = Convert.ToInt64(_deadlineOverrunProp.GetValue(buzz)); } catch { }
                        _machine.MarkGlitchAuto();
                    }
                }
            }
            catch { }
        }

        void UpdateFrStatus()
        {
            if (_frStatusText == null) return;
            bool on = _frEnableCheckbox?.IsChecked == true;
            string dir = _machine?.FrOutputDir ?? _frPathBox?.Text ?? "";
            if (on)
            {
                _frStatusText.Text = "Recording ON — auto-marks on ASIO deadline misses → " + dir;
                _frStatusText.Foreground = BrushOk;
            }
            else
            {
                _frStatusText.Text = "Off — window can stay open with no files written (default).";
                _frStatusText.Foreground = BrushSubText;
            }
        }

        void OpenFrFolder()
        {
            try
            {
                string dir = _machine?.FrOutputDir ?? _frPathBox?.Text ?? "";
                if (string.IsNullOrWhiteSpace(dir)) return;
                System.IO.Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dir, UseShellExecute = true
                });
            }
            catch { }
        }
        void DetectMuteDelta(Profile2Snapshot snap)
        {
            if (_selectedIMachine == null) return;
            if (_measureState != MeasureState.Idle || _profileAllState == ProfileAllState.Running) return; // suppress during automated measurements
            if (_muteDeltaPending) return;

            bool nowMuted;
            try { nowMuted = _selectedIMachine.IsMuted; } catch { return; }
            if (nowMuted == _lastSeenSelectedMuted) return;

            // State changed — snapshot before, wait 1.5s, snapshot after
            _muteDeltaWasMutedAtStart = _lastSeenSelectedMuted;
            _muteDeltaCostBefore      = snap.AvgOtherMs;
            _lastSeenSelectedMuted    = nowMuted;
            _muteDeltaPending         = true;

            _muteDeltaTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _muteDeltaTimer.Tick += (_, __) =>
            {
                _muteDeltaTimer!.Stop();
                _muteDeltaPending = false;

                if (_machine == null) return;
                double after = _machine.Snapshot?.AvgOtherMs ?? 0;
                // If we were unmuted at start and now muted: delta = before - after (positive = savings)
                // If we were muted at start and now unmuted: delta = after - before (positive = cost)
                double delta = _muteDeltaWasMutedAtStart
                    ? after - _muteDeltaCostBefore
                    : _muteDeltaCostBefore - after;
                if (delta < 0) delta = 0;
                _marginalCostMs = delta;
            };
            _muteDeltaTimer.Start();
        }


        // ═════════════════════════════════════════════════════════════════════
        // Parameter activity polling
        // ═════════════════════════════════════════════════════════════════════
        void PollParameterActivity()
        {
            if (_selectedIMachine == null) return;
            long nowTicks = Environment.TickCount64;

            // Decay write rates each tick (10 Hz polling)
            // Decay factor ≈ 0.9 per tick → time constant ~1 second
            foreach (var key in _paramWritesPerSec.Keys.ToList())
                _paramWritesPerSec[key] *= 0.9;

            try
            {
                var groups = _selectedIMachine.ParameterGroups;
                if (groups == null) return;
                for (int gi = 1; gi < groups.Count; gi++) // skip group 0 (input)
                {
                    var g = groups[gi];
                    if (g?.Parameters == null) continue;
                    foreach (var p in g.Parameters)
                    {
                        if (p == null) continue;
                        string key = $"{gi}:{p.Name}";
                        int v;
                        try { v = p.GetValue(0); } catch { continue; }

                        if (_paramLastValue.TryGetValue(key, out int last))
                        {
                            if (last != v)
                            {
                                _paramWritesPerSec[key] = _paramWritesPerSec.TryGetValue(key, out var r) ? r + 1.0 : 1.0;
                                _paramLastChangeTicks[key] = nowTicks;
                            }
                        }
                        _paramLastValue[key] = v;
                    }
                }
            } catch { }

            _lastParamPollTicks = nowTicks;
        }


        // ═════════════════════════════════════════════════════════════════════
        // Sparkline history update
        // ═════════════════════════════════════════════════════════════════════
        void UpdateMachineCostHistory()
        {
            // Record latest known cost (marginal or solo, whichever is freshest)
            double cost = double.NaN;
            if (!double.IsNaN(_marginalCostMs)) cost = _marginalCostMs;
            else if (!double.IsNaN(_soloResultMs)) cost = _soloResultMs;

            if (double.IsNaN(cost))
            {
                // No measurement yet — push NaN (drawn as gap)
                _machineCostHistory[_machineCostIdx] = double.NaN;
            }
            else
            {
                _machineCostHistory[_machineCostIdx] = cost;
                _lastKnownMachineCost = cost;
            }
            _machineCostIdx = (_machineCostIdx + 1) % HISTORY_LEN;
            if (_machineCostIdx == 0) _machineCostFull = true;
        }


        // ═════════════════════════════════════════════════════════════════════
        // Header / type badge / mute button refresh
        // ═════════════════════════════════════════════════════════════════════
        void RefreshHeader()
        {
            if (_selectedIMachine == null)
            {
                _typeBadge.Text = "—";
                _typeBadge.Foreground = BrushSubText;
                _muteBtn.IsEnabled = false;
                return;
            }

            _typeBadge.Text       = TypeTag(_selectedIMachine);
            _typeBadge.Foreground = BrushSubText;
            _muteBtn.IsEnabled    = true;

            bool muted = false;
            try { muted = _selectedIMachine.IsMuted; } catch { }
            _muteBtn.Content    = muted ? "UNMUTE" : "MUTE";
            _muteBtn.Foreground = muted ? BrushMuted : BrushText;
        }

        static string TypeTag(IMachine m)
        {
            if (m.IsControlMachine) return "CTRL";
            try
            {
                var inputs = m.Inputs;
                if (inputs != null && inputs.Any()) return "FX";
            } catch { }
            return "GEN";
        }


        // ─── Buffer Sweep Log ────────────────────────────────────────────────
        // Detects when the user changes ASIO buffer size in ReBuzz (the budget
        // changes), waits ~3 seconds for new measurement to stabilize, then
        // records a sample (buffer, budget, unaccounted, dropout-rate). The
        // resulting table shows how host overhead scales with buffer size —
        // central diagnostic for the "host overhead dominates small buffers"
        // hypothesis.
        class SweepEntry
        {
            public int      BufferSamples;
            public double   BudgetMs;
            public double   UnaccountedMs;
            public double   UnaccountedPct;
            public double   DropoutRatePct;
            public DateTime When;
        }
        readonly List<SweepEntry> _sweepLog = new();
        TextBlock   _sweepStatusText = null!;
        StackPanel  _sweepLogPanel   = null!;

        double _sweepLastBudget;
        long   _sweepStableSinceMs;
        long   _sweepLastDropoutsSnapshot;
        long   _sweepLastBuffersSnapshot;
        const long SWEEP_STABILIZE_MS = 3000;

        void UpdateSweep(Profile2Snapshot snap)
        {
            double budget = snap.BudgetMs;
            if (budget <= 0) return;

            long now = Environment.TickCount64;

            // Detect a real ASIO buffer-size change (vs. fill-thread chunk
            // jitter). With AudioBufferFillThread on, the fill thread reads the
            // ring buffer in variable blocks, so BudgetMs wobbles ~15-20% even
            // when the ASIO buffer is unchanged. Real buffer changes are large
            // (typically ≥2× — e.g. 256↔512↔1024), so a 35% threshold cleanly
            // separates a deliberate change from fill-thread noise. (Note: with
            // the fill thread active, the Sweep tracks fill-chunk cadence, not
            // the ASIO buffer — it's only a true buffer-size map with the fill
            // thread OFF.)
            bool changed = _sweepLastBudget > 0
                         && Math.Abs(budget - _sweepLastBudget) / _sweepLastBudget > 0.35;

            if (changed || _sweepLastBudget == 0)
            {
                _sweepStableSinceMs       = now;
                _sweepLastBudget          = budget;
                _sweepLastDropoutsSnapshot = snap.TotalDropouts;
                _sweepLastBuffersSnapshot  = snap.TotalBuffers;
                _sweepStatusText.Text       = $"Buffer changed → stabilizing… ({SWEEP_STABILIZE_MS / 1000} s)";
                _sweepStatusText.Foreground = BrushWarn;
                return;
            }
            _sweepLastBudget = budget;

            // Stable enough? Record.
            if (_sweepStableSinceMs > 0 && (now - _sweepStableSinceMs) >= SWEEP_STABILIZE_MS)
            {
                RecordSweepSample(force: false);
                _sweepStableSinceMs = 0;  // wait for next change
            }
            else if (_sweepStableSinceMs > 0)
            {
                long remaining = SWEEP_STABILIZE_MS - (now - _sweepStableSinceMs);
                _sweepStatusText.Text       = $"Stabilizing… {remaining / 1000.0:F1} s remaining";
                _sweepStatusText.Foreground = BrushWarn;
            }
            else
            {
                _sweepStatusText.Text       = _sweepLog.Count == 0
                                            ? "(change ASIO buffer size in ReBuzz to record a sample)"
                                            : $"({_sweepLog.Count} sample(s) recorded — change buffer to capture another)";
                _sweepStatusText.Foreground = BrushSubText;
            }
        }

        void RecordSweepSample(bool force)
        {
            var snap = _machine?.Snapshot;
            if (snap == null || !snap.IsValid) return;
            double budget = snap.BudgetMs;
            int    sr     = snap.SampleRate;
            if (budget <= 0 || sr <= 0) return;

            // Recompute engine total at moment of recording
            double engineSumMs = 0;
            foreach (var kv in _enginePerf)
            {
                if (kv.Value.IsNative) continue;
                if (double.IsNaN(kv.Value.SmoothedMsPerBuffer)) continue;
                engineSumMs += kv.Value.SmoothedMsPerBuffer;
            }
            double unaccountedMs = budget - engineSumMs;

            // Dropout rate since the stabilization point (or all-time if forced)
            long dropoutsDelta;
            long buffersDelta;
            if (force || _sweepLastBuffersSnapshot == 0)
            {
                dropoutsDelta = snap.TotalDropouts;
                buffersDelta  = snap.TotalBuffers;
            }
            else
            {
                dropoutsDelta = snap.TotalDropouts - _sweepLastDropoutsSnapshot;
                buffersDelta  = snap.TotalBuffers  - _sweepLastBuffersSnapshot;
            }
            double dropoutPct = buffersDelta > 0 ? dropoutsDelta * 100.0 / buffersDelta : 0;

            int spl = (int)Math.Round(budget * sr / 1000.0);

            // De-dupe: if the most recent entry matches this buffer size, replace it
            if (_sweepLog.Count > 0 && Math.Abs(_sweepLog[^1].BufferSamples - spl) <= 4)
            {
                _sweepLog.RemoveAt(_sweepLog.Count - 1);
            }

            _sweepLog.Add(new SweepEntry
            {
                BufferSamples = spl,
                BudgetMs      = budget,
                UnaccountedMs = unaccountedMs,
                UnaccountedPct = budget > 0 ? (unaccountedMs / budget) * 100.0 : 0,
                DropoutRatePct = dropoutPct,
                When = DateTime.Now
            });

            // Reset the dropout-delta baseline so next sample measures from now
            _sweepLastDropoutsSnapshot = snap.TotalDropouts;
            _sweepLastBuffersSnapshot  = snap.TotalBuffers;

            RefreshSweepDisplay();
        }

        void RefreshSweepDisplay()
        {
            _sweepLogPanel.Children.Clear();
            if (_sweepLog.Count == 0) return;

            _sweepLogPanel.Children.Add(new TextBlock
            {
                Text       = "  buffer   budget    unaccounted        dropout    captured",
                Foreground = BrushSubText,
                FontFamily = Mono,
                FontSize   = 9,
                Margin     = new Thickness(0, 0, 0, 2)
            });

            foreach (var e in _sweepLog)
            {
                var row = new TextBlock
                {
                    FontFamily = Mono,
                    FontSize   = 10,
                    Margin     = new Thickness(0, 1, 0, 1)
                };
                // Color the unaccounted % to telegraph host-overhead severity
                var unaccColor = e.UnaccountedPct >= 80 ? BrushBad
                              : e.UnaccountedPct >= 50 ? BrushWarn
                              : BrushOk;
                row.Inlines.Add(new System.Windows.Documents.Run
                {
                    Text       = $"  {e.BufferSamples,4} spl  {e.BudgetMs,6:F2} ms  ",
                    Foreground = BrushText
                });
                row.Inlines.Add(new System.Windows.Documents.Run
                {
                    Text       = $"{e.UnaccountedMs,5:F2} ms ({e.UnaccountedPct,5:F1}%)",
                    Foreground = unaccColor,
                    FontWeight = FontWeights.Bold
                });
                row.Inlines.Add(new System.Windows.Documents.Run
                {
                    Text       = $"  {e.DropoutRatePct,5:F1}%   {e.When:HH:mm:ss}",
                    Foreground = BrushText
                });
                _sweepLogPanel.Children.Add(row);
            }

            // Pattern hint — if unaccounted is roughly constant across rows,
            // it's fixed-cost host overhead. If it scales with buffer, it's
            // per-sample work.
            if (_sweepLog.Count >= 2)
            {
                double minU = double.MaxValue, maxU = double.MinValue;
                foreach (var e in _sweepLog)
                {
                    if (e.UnaccountedMs < minU) minU = e.UnaccountedMs;
                    if (e.UnaccountedMs > maxU) maxU = e.UnaccountedMs;
                }
                double range = maxU - minU;
                double mean  = (minU + maxU) / 2;
                string verdict;
                if (mean > 0 && range / mean < 0.30)
                    verdict = $"→ unaccounted is roughly constant ({mean:F2} ms): fixed-cost host overhead";
                else
                    verdict = $"→ unaccounted scales with buffer (min {minU:F2} ms, max {maxU:F2} ms): includes per-sample work";

                _sweepLogPanel.Children.Add(new TextBlock
                {
                    Text       = verdict,
                    Foreground = BrushAccent,
                    FontFamily = Mono,
                    FontSize   = 10,
                    FontStyle  = FontStyles.Italic,
                    Margin     = new Thickness(0, 6, 0, 0)
                });
            }
        }


        // ─── Master Output VU ────────────────────────────────────────────────
        // Reads the volatile peak/RMS values populated from the audio thread
        // and draws color-coded VU bars on the L/R canvases. Peak-hold decay
        // is UI-side at 6 dB/sec so visible meters don't flicker.
        void RefreshMasterOutput()
        {
            // Pull volatile snapshots
            float peakL = _masterPeakL;
            float peakR = _masterPeakR;
            float rmsL  = _masterRmsL;
            float rmsR  = _masterRmsR;
            long  calls = System.Threading.Interlocked.Read(ref _masterTapCallCount);

            // Decay peak-hold by ~6 dB/sec
            long now = Environment.TickCount64;
            if (_masterPeakHoldLastTickMs > 0)
            {
                float dt = (now - _masterPeakHoldLastTickMs) / 1000.0f;
                float decay = (float)Math.Pow(10, -6 * dt / 20.0); // -6 dB/sec
                _masterPeakHoldL *= decay;
                _masterPeakHoldR *= decay;
            }
            _masterPeakHoldLastTickMs = now;
            if (peakL > _masterPeakHoldL) _masterPeakHoldL = peakL;
            if (peakR > _masterPeakHoldR) _masterPeakHoldR = peakR;

            if (!_masterTapHooked)
            {
                _masterStatusText.Text       = "(MasterTap not available — see Debug Console)";
                _masterStatusText.Foreground = BrushSubText;
                _masterValsL.Text = _masterValsR.Text = "—";
                _masterBarL.Children.Clear();
                _masterBarR.Children.Clear();
                return;
            }
            if (calls == 0)
            {
                _masterStatusText.Text       = "(no audio rendered yet)";
                _masterStatusText.Foreground = BrushSubText;
                _masterValsL.Text = _masterValsR.Text = "—";
                _masterBarL.Children.Clear();
                _masterBarR.Children.Clear();
                return;
            }
            _masterStatusText.Text = "";

            DrawVuBar(_masterBarL, _masterPeakHoldL, peakL, rmsL);
            DrawVuBar(_masterBarR, _masterPeakHoldR, peakR, rmsR);

            _masterValsL.Text = $"peak {FormatDb(peakL),7}  ·  rms {FormatDb(rmsL),7}";
            _masterValsR.Text = $"peak {FormatDb(peakR),7}  ·  rms {FormatDb(rmsR),7}";
        }

        static string FormatDb(float lin)
        {
            if (lin < 1e-6f) return "-inf";
            double db = 20.0 * Math.Log10(lin);
            return $"{db:F1} dB";
        }

        // Draws one VU bar with color zones (green/yellow/orange/red) and a
        // peak-hold marker. RMS rendered as a darker mid-bar line for context.
        // Scale: -60 dB → empty (left), 0 dB → full (right).
        void DrawVuBar(Canvas canvas, float peakHold, float currentPeak, float rms)
        {
            canvas.Children.Clear();
            double w = canvas.ActualWidth;
            double h = canvas.Height;
            if (w <= 0 || h <= 0) return;

            // Convert linear amplitudes to dB and then to bar fractions (0..1)
            static double LinToFrac(float lin)
            {
                if (lin < 1e-6f) return 0;
                double db = 20.0 * Math.Log10(lin);
                double frac = (db + 60) / 60.0;
                return frac < 0 ? 0 : frac > 1 ? 1 : frac;
            }

            double fracPeak = LinToFrac(peakHold);
            double fracCur  = LinToFrac(currentPeak);
            double fracRms  = LinToFrac(rms);

            // Build the "filled" portion as a sequence of colored zone rectangles,
            // each clipped at the current peak-hold position. Zones (dB → frac):
            //   -60..-18 (0.00..0.70)  green
            //   -18..-6  (0.70..0.90)  yellow
            //   -6..0    (0.90..1.00)  orange
            //   above 0  (>1.00)       red — only visible if clipping
            void AddZone(double startFrac, double endFrac, Color c)
            {
                double s = startFrac * w;
                double e = Math.Min(endFrac, fracPeak) * w;
                if (e <= s) return;
                var brush = new SolidColorBrush(c);
                brush.Freeze();
                var r = new Rectangle { Width = e - s, Height = h, Fill = brush };
                Canvas.SetLeft(r, s);
                Canvas.SetTop(r, 0);
                canvas.Children.Add(r);
            }
            AddZone(0.00, 0.70, Color.FromRgb(0x40, 0xb0, 0x40));  // green
            AddZone(0.70, 0.90, Color.FromRgb(0xd0, 0xc0, 0x30));  // yellow
            AddZone(0.90, 1.00, Color.FromRgb(0xe0, 0x80, 0x30));  // orange
            if (fracPeak > 1.0)
            {
                // shouldn't happen since we clip to 1.0, but if clipping is
                // ever extended beyond 0 dBFS this would be the red zone
            }

            // Tick markers at -36, -24, -18, -12, -6 dB
            foreach (double db in new[] { -36.0, -24.0, -18.0, -12.0, -6.0 })
            {
                double x = ((db + 60) / 60.0) * w;
                var tick = new Rectangle
                {
                    Width  = 1,
                    Height = h * 0.3,
                    Fill   = new SolidColorBrush(Color.FromArgb(0x80, 0x70, 0x70, 0x70))
                };
                ((SolidColorBrush)tick.Fill).Freeze();
                Canvas.SetLeft(tick, x);
                Canvas.SetTop(tick, h - h * 0.3);
                canvas.Children.Add(tick);
            }

            // RMS marker — thin darker bar inside the filled zone
            if (fracRms > 0)
            {
                double rmsX = fracRms * w;
                var rmsLine = new Rectangle
                {
                    Width = 1.5,
                    Height = h * 0.6,
                    Fill = new SolidColorBrush(Color.FromArgb(0xb0, 0xff, 0xff, 0xff))
                };
                ((SolidColorBrush)rmsLine.Fill).Freeze();
                Canvas.SetLeft(rmsLine, rmsX - 0.75);
                Canvas.SetTop(rmsLine, h * 0.2);
                canvas.Children.Add(rmsLine);
            }

            // Current-peak indicator (instantaneous, not held) — small white line
            if (fracCur > 0)
            {
                double curX = fracCur * w;
                var marker = new Rectangle
                {
                    Width = 2,
                    Height = h,
                    Fill = Brushes.White,
                    Opacity = 0.85
                };
                Canvas.SetLeft(marker, Math.Min(curX, w - 2));
                Canvas.SetTop(marker, 0);
                canvas.Children.Add(marker);
            }
        }


        // ─── Engine Total stacked bar ─────────────────────────────────────────
        // Each managed machine that's reporting engine cost gets a colored
        // segment; the remainder (host overhead) is filled in red. ToolTips
        // identify each segment. Recomputed each cost-panel refresh.
        static readonly Color[] _stackColors =
        {
            Color.FromRgb(0x4a, 0x9e, 0xff),  // blue
            Color.FromRgb(0x6a, 0xc8, 0x6a),  // green
            Color.FromRgb(0xff, 0xa0, 0x4a),  // orange
            Color.FromRgb(0xc6, 0x82, 0xff),  // purple
            Color.FromRgb(0x4a, 0xc8, 0xc8),  // cyan
            Color.FromRgb(0xff, 0x82, 0xa0),  // pink
            Color.FromRgb(0xc8, 0xc8, 0x4a),  // yellow
            Color.FromRgb(0x82, 0xff, 0xa0),  // mint
        };

        void RefreshEngineStack()
        {
            _engineStackCanvas.Children.Clear();
            double budget = _machine?.Snapshot?.BudgetMs ?? 0;
            double width  = _engineStackCanvas.ActualWidth;
            if (budget <= 0 || width <= 0) return;

            // Gather managed machine costs in descending order of magnitude.
            // Native machines contribute nothing to the engine sum so we skip them
            // but count them so the tooltip on host overhead can mention them.
            var entries = new List<(string name, double ms)>();
            int nativeCount = 0;
            foreach (var kv in _enginePerf)
            {
                if (kv.Value.IsNative) { nativeCount++; continue; }
                if (double.IsNaN(kv.Value.SmoothedMsPerBuffer)) continue;
                if (kv.Value.SmoothedMsPerBuffer <= 0) continue;
                entries.Add((kv.Key, kv.Value.SmoothedMsPerBuffer));
            }
            entries.Sort((a, b) => b.ms.CompareTo(a.ms));

            double scale = width / budget;  // px per ms
            double x = 0;
            int idx = 0;
            foreach (var (name, ms) in entries)
            {
                double w = ms * scale;
                if (w < 0.5) { idx++; continue; }
                var brush = new SolidColorBrush(_stackColors[idx % _stackColors.Length]);
                brush.Freeze();
                var rect = new Rectangle
                {
                    Width  = w,
                    Height = _engineStackCanvas.Height,
                    Fill   = brush,
                    ToolTip = $"{name}: {ms:F2} ms ({ms / budget * 100:F1}% of buffer)"
                };
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, 0);
                _engineStackCanvas.Children.Add(rect);
                x += w;
                idx++;
            }

            // Host overhead = whatever's left of the budget
            double hostW = width - x;
            if (hostW > 0.5)
            {
                double hostMs = hostW / scale;
                string label = nativeCount > 0
                    ? $"host overhead + native ({nativeCount}): {hostMs:F2} ms ({hostMs / budget * 100:F1}%)"
                    : $"host overhead: {hostMs:F2} ms ({hostMs / budget * 100:F1}%)";
                // Color intensifies as host fraction grows
                double hostPct = hostMs / budget;
                Color hostColor = hostPct >= 0.8 ? Color.FromRgb(0xff, 0x4a, 0x4a)
                                : hostPct >= 0.5 ? Color.FromRgb(0xff, 0x90, 0x4a)
                                : Color.FromRgb(0x70, 0x40, 0x40);
                var hostBrush = new SolidColorBrush(hostColor);
                hostBrush.Freeze();
                var hostRect = new Rectangle
                {
                    Width  = hostW,
                    Height = _engineStackCanvas.Height,
                    Fill   = hostBrush,
                    ToolTip = label
                };
                Canvas.SetLeft(hostRect, x);
                Canvas.SetTop(hostRect, 0);
                _engineStackCanvas.Children.Add(hostRect);
            }
        }


        // ═════════════════════════════════════════════════════════════════════
        // Cost panel refresh
        // ═════════════════════════════════════════════════════════════════════
        void RefreshCostPanel()
        {
            // ── Always update the buffer-period line, even with no selection ─
            double budget = _machine?.Snapshot?.BudgetMs ?? 0;
            int    sr     = _machine?.Snapshot?.SampleRate ?? 0;
            if (budget > 0 && sr > 0)
            {
                // samples per buffer = BudgetMs * SampleRate / 1000, rounded to nearest power-of-2-ish int
                int spl = (int)Math.Round(budget * sr / 1000.0);
                _periodInfoText.Text =
                    $"Buffer: {budget:F2} ms  ({spl} spl @ {sr / 1000.0:F1} kHz · {_machine!.Window}-buf avg)";
            }
            else
            {
                _periodInfoText.Text = "Buffer: —  (warming up)";
            }

            // ── Engine Total ────────────────────────────────────────────────
            // Sum smoothed engine cost across all managed machines that have
            // reported data. The gap (budget - sum) is the "unaccounted-for"
            // budget — host overhead, scheduling waits, driver lag. Critical
            // for distinguishing machine-cost dropouts from host-overhead
            // dropouts.
            double engineSumMs = 0;
            int    engineCounted = 0;
            int    engineNative  = 0;
            foreach (var kv in _enginePerf)
            {
                if (kv.Value.IsNative) { engineNative++; continue; }
                if (double.IsNaN(kv.Value.SmoothedMsPerBuffer)) continue;
                engineSumMs += kv.Value.SmoothedMsPerBuffer;
                engineCounted++;
            }
            if (budget > 0 && engineCounted > 0)
            {
                double sumPct = (engineSumMs / budget) * 100.0;
                double gapMs  = budget - engineSumMs;
                double gapPct = (gapMs / budget) * 100.0;
                _engineTotalText.Text =
                    $"Engine Total: {engineSumMs:F2} ms ({sumPct:F1}%)  ·  unaccounted: {gapMs:F2} ms ({gapPct:F1}%)  ·  n={engineCounted}" +
                    (engineNative > 0 ? $" (+{engineNative} native)" : "");
                // Color the line — high "unaccounted" fraction is the diagnostic
                // signal we're looking for: host overhead consuming buffer time
                _engineTotalText.Foreground = gapPct >= 80 ? BrushBad
                                            : gapPct >= 50 ? BrushWarn
                                            : BrushSubText;
            }
            else
            {
                _engineTotalText.Text = engineNative > 0
                    ? $"Engine Total: —  ({engineNative} native machine(s); engine data unavailable)"
                    : "Engine Total: —  (warming up)";
                _engineTotalText.Foreground = BrushSubText;
            }

            // Redraw stacked bar for engine total visualization
            RefreshEngineStack();

            bool isControl = false;
            try { isControl = _selectedIMachine?.IsControlMachine ?? false; } catch { }

            if (_selectedIMachine == null)
            {
                _engineCostText.Text       = "—";
                _engineSubText.Text        = "";
                _soloCostText.Text         = "—";
                _soloSubText.Text          = "";
                _marginalCostText.Text     = "—";
                _measureSoloBtn.IsEnabled  = false;
                _measureMarginalBtn.IsEnabled = false;
                return;
            }

            if (isControl)
            {
                _engineCostText.Text   = "N/A";
                _engineSubText.Text    = "(control)";
                _soloCostText.Text     = "N/A";
                _soloSubText.Text      = "";
                _marginalCostText.Text = "N/A";
                _engineCostText.Foreground   = BrushSubText;
                _soloCostText.Foreground     = BrushSubText;
                _marginalCostText.Foreground = BrushSubText;
                _measureSoloBtn.IsEnabled  = false;
                _measureMarginalBtn.IsEnabled = false;
                return;
            }

            bool canMeasure = (_measureState == MeasureState.Idle && _profileAllState != ProfileAllState.Running);
            _measureSoloBtn.IsEnabled    = canMeasure;
            _measureMarginalBtn.IsEnabled = canMeasure;

            // ── ENGINE ───────────────────────────────────────────────────────
            // Read smoothed value from per-machine state populated by OnTick's
            // UpdateEngineCost. Falls back to "warming up" until we have two
            // delta samples. Native machines (ManagedMachine == null) show
            // "—" with explicit "(native: N/A)" sub-text.
            if (_enginePerf.TryGetValue(_selectedIMachine.Name, out var perfSt))
            {
                if (perfSt.IsNative)
                {
                    _engineCostText.Text       = "—";
                    _engineCostText.Foreground = BrushSubText;
                    _engineSubText.Text        = "(native: N/A)";
                }
                else if (!double.IsNaN(perfSt.SmoothedMsPerBuffer))
                {
                    _engineCostText.Text       = FormatMsWithPct(perfSt.SmoothedMsPerBuffer, budget);
                    _engineCostText.Foreground = CostColor(perfSt.SmoothedMsPerBuffer, budget);
                    _engineSubText.Text        = "live  ·  delta-sampled";
                }
                else
                {
                    _engineCostText.Text       = "…";
                    _engineCostText.Foreground = BrushSubText;
                    _engineSubText.Text        = "warming up";
                }
            }
            else
            {
                _engineCostText.Text       = _enginePerfResolved && !_enginePerfAvailable
                                            ? "(unavail.)"
                                            : "…";
                _engineCostText.Foreground = BrushSubText;
                _engineSubText.Text        = _enginePerfResolved && !_enginePerfAvailable
                                            ? "engine field not found"
                                            : "warming up";
            }

            // ── SOLO ─────────────────────────────────────────────────────────
            if (double.IsNaN(_soloResultMs))
            {
                _soloCostText.Text       = "—";
                _soloCostText.Foreground = BrushSubText;
                _soloSubText.Text        = "";
            }
            else
            {
                _soloCostText.Text       = FormatMsWithPct(_soloResultMs, budget);
                _soloCostText.Foreground = CostColor(_soloResultMs, budget);

                // Sub-line: "peak X.XX ms · median Y.YY ms (n=N)"
                var parts = new List<string>();
                if (!double.IsNaN(_soloPeakMs))
                    parts.Add($"peak {_soloPeakMs:F2}");
                if (_recentSoloCount >= 2)
                {
                    double med = MedianOfRecentSolo();
                    parts.Add($"med {med:F2} (n={_recentSoloCount})");
                }
                _soloSubText.Text = string.Join(" · ", parts);
            }

            // ── MARGINAL ─────────────────────────────────────────────────────
            if (double.IsNaN(_marginalCostMs))
            {
                _marginalCostText.Text       = "—";
                _marginalCostText.Foreground = BrushSubText;
            }
            else
            {
                _marginalCostText.Text       = FormatMsWithPct(_marginalCostMs, budget);
                _marginalCostText.Foreground = CostColor(_marginalCostMs, budget);
            }
        }

        // ─── Formatting helpers ──────────────────────────────────────────────
        static string FormatMsWithPct(double ms, double budget)
        {
            if (budget > 0)
            {
                double pct = ms / budget * 100.0;
                return $"{ms:F2} ms ({pct:F0}%)";
            }
            return $"{ms:F2} ms";
        }

        static Brush CostColor(double ms, double budget)
        {
            if (budget <= 0) return BrushAccent;
            double pct = ms / budget * 100.0;
            if (pct >= 70) return BrushBad;
            if (pct >= 40) return BrushWarn;
            return BrushAccent;
        }

        double MedianOfRecentSolo()
        {
            int n = _recentSoloCount;
            if (n == 0) return double.NaN;
            var arr = new double[n];
            Array.Copy(_recentSolo, arr, n);
            Array.Sort(arr);
            return (n % 2 == 1) ? arr[n / 2] : (arr[n / 2 - 1] + arr[n / 2]) / 2.0;
        }


        // ═════════════════════════════════════════════════════════════════════
        // Connections refresh — 1-hop upstream + downstream
        // ═════════════════════════════════════════════════════════════════════
        void RefreshConnections()
        {
            _upstreamPanel.Children.Clear();
            _downstreamPanel.Children.Clear();
            if (_selectedIMachine == null || _subscribedBuzz?.Song == null) return;

            // Upstream — from m.Inputs (each conn.Source feeds INTO selected)
            try
            {
                var ins = _selectedIMachine.Inputs;
                if (ins != null)
                {
                    foreach (var conn in ins)
                    {
                        var src = conn?.Source;
                        if (src != null) _upstreamPanel.Children.Add(MakeNeighborChip(src.Name));
                    }
                }
            } catch { }
            if (_upstreamPanel.Children.Count == 0)
                _upstreamPanel.Children.Add(MakePlaceholder("(no inputs)"));

            // Downstream — reverse lookup: which machines have selected in THEIR inputs?
            try
            {
                foreach (var m in _subscribedBuzz.Song.Machines)
                {
                    if (m == _selectedIMachine) continue;
                    try
                    {
                        var ins = m.Inputs;
                        if (ins == null) continue;
                        if (ins.Any(c => c?.Source == _selectedIMachine))
                            _downstreamPanel.Children.Add(MakeNeighborChip(m.Name));
                    } catch { }
                }
            } catch { }
            if (_downstreamPanel.Children.Count == 0)
                _downstreamPanel.Children.Add(MakePlaceholder("(no outputs)"));
        }

        Border MakeNeighborChip(string name)
        {
            var btn = new Button
            {
                Content     = name,
                Background  = BrushTrack,
                Foreground  = BrushText,
                BorderBrush = BrushBorder,
                FontFamily  = Mono,
                FontSize    = 10,
                Padding     = new Thickness(6, 1, 6, 1),
                Margin      = new Thickness(0, 0, 4, 0),
                Cursor      = Cursors.Hand
            };
            btn.Click += (_, __) =>
            {
                if (_machineSelector.Items.Contains(name))
                    _machineSelector.SelectedItem = name;
            };
            return new Border { Child = btn };
        }

        TextBlock MakePlaceholder(string text)
            => new TextBlock { Text = text, Foreground = BrushSubText, FontFamily = Mono, FontSize = 10, Margin = new Thickness(0, 2, 0, 0) };


        // ═════════════════════════════════════════════════════════════════════
        // Spike attribution
        // ═════════════════════════════════════════════════════════════════════
        void RefreshSpikeAttribution(Profile2Snapshot snap)
        {
            if (_selectedName == null || snap.Spikes == null || snap.Spikes.Length == 0)
            {
                _spikeAttribText.Text       = "(no spikes captured yet)";
                _spikeAttribText.Foreground = BrushSubText;
                if (_spikeListExpanded) RefreshSpikeList();
                return;
            }

            int total       = snap.Spikes.Length;
            int activeCount = 0;
            int withData    = 0;
            foreach (var s in snap.Spikes)
            {
                if (s.ActiveMachines == null || s.ActiveMachines.Length == 0) continue;
                withData++;
                if (Array.IndexOf(s.ActiveMachines, _selectedName) >= 0) activeCount++;
            }

            // Compute interval statistics. IMPORTANT: the Spikes[] ring is
            // cooldown-limited (≤1 capture per SPIKE_COOLDOWN_MS = 500 ms in the
            // machine). When real overruns are frequent, recorded intervals
            // collapse to ~the cooldown and masquerade as "periodic" with σ≈0.
            // That's an artifact, not a real timer. Detect this by comparing the
            // mean interval to the cooldown, and when overruns are cooldown-
            // limited, report the TRUE overrun rate from SpikeRawTotal instead.
            const double SPIKE_COOLDOWN_MS = 500.0;   // must match machine
            string intervalSummary = "";

            // True overrun rate (no cooldown) — the honest signal
            double rawRatePerSec = (snap.ElapsedSec > 0.5)
                                 ? snap.SpikeRawTotal / snap.ElapsedSec
                                 : 0;

            if (snap.Spikes.Length >= 2)
            {
                double sum = 0, sumSq = 0; int n = 0;
                for (int i = 1; i < snap.Spikes.Length; i++)
                {
                    double dt = Math.Abs((snap.Spikes[i].ElapsedSec - snap.Spikes[i - 1].ElapsedSec) * 1000.0);
                    if (dt > 0) { sum += dt; sumSq += dt * dt; n++; }
                }
                if (n > 0)
                {
                    double mean = sum / n;
                    double var  = (sumSq / n) - (mean * mean);
                    double std  = var > 0 ? Math.Sqrt(var) : 0;
                    double cv   = mean > 0 ? std / mean : 0;

                    // Cooldown-limited if the mean recorded interval is within
                    // ~25% of the cooldown floor — recorded cadence is dominated
                    // by the throttle, so the periodicity stat is meaningless.
                    bool cooldownLimited = mean <= SPIKE_COOLDOWN_MS * 1.25;

                    if (cooldownLimited && rawRatePerSec > 0)
                    {
                        intervalSummary = $"  ·  overruns {rawRatePerSec:F0}/s (recorded list capped at 1/{SPIKE_COOLDOWN_MS/1000:F1}s — intervals not meaningful)";
                    }
                    else
                    {
                        string regularity = cv < 0.15 ? "periodic" : cv < 0.40 ? "semi-periodic" : "irregular";
                        intervalSummary = $"  ·  intervals μ={mean:F0}ms σ={std:F0}ms ({regularity})";
                    }
                }
            }
            else if (rawRatePerSec > 0)
            {
                intervalSummary = $"  ·  overruns {rawRatePerSec:F0}/s";
            }

            if (withData == 0)
            {
                _spikeAttribText.Text       = $"({total} spikes captured, no attribution data yet){intervalSummary}";
                _spikeAttribText.Foreground = BrushSubText;
            }
            else
            {
                double pct = activeCount * 100.0 / withData;
                _spikeAttribText.Text = $"Active during {activeCount} of last {withData} spikes ({pct:F0}%){intervalSummary}";
                _spikeAttribText.Foreground = pct >= 80 ? BrushBad
                                            : pct >= 40 ? BrushWarn
                                            : BrushOk;
            }

            if (_spikeListExpanded) RefreshSpikeList();
        }

        // ─── Spike list — one row per captured spike, with timestamp + actives ─
        void RefreshSpikeList()
        {
            _spikeListPanel.Children.Clear();
            var snap = _machine?.Snapshot;
            if (snap?.Spikes == null || snap.Spikes.Length == 0)
            {
                _spikeListPanel.Children.Add(MakePlaceholder("(no spikes captured)"));
                return;
            }

            // Header row — column labels
            _spikeListPanel.Children.Add(new TextBlock
            {
                Text       = "  time     bpm   spike    Δprev   active machines",
                Foreground = BrushSubText,
                FontFamily = Mono,
                FontSize   = 9,
                Margin     = new Thickness(0, 0, 0, 2)
            });

            double? prevElapsed = null;
            foreach (var sp in snap.Spikes)
            {
                // "+M:SS.s" time format
                double elapsed = sp.ElapsedSec;
                int minutes = (int)(elapsed / 60.0);
                double secs = elapsed - minutes * 60.0;
                string timeStr = $"+{minutes}:{secs:00.0}";

                // Interval since previous spike (— for the first row).
                // Spikes are stored newest-first; take abs so the gap is always
                // shown as a positive duration regardless of iteration direction.
                string deltaStr;
                if (prevElapsed.HasValue)
                {
                    double dtMs = Math.Abs((elapsed - prevElapsed.Value) * 1000.0);
                    deltaStr = dtMs >= 1000 ? $"{dtMs / 1000.0:F1}s" : $"{dtMs:F0}ms";
                }
                else deltaStr = "—";
                prevElapsed = elapsed;

                // Render active-machines list with the selected name highlighted
                var inline = new TextBlock
                {
                    FontFamily   = Mono,
                    FontSize     = 10,
                    TextWrapping = TextWrapping.Wrap,
                    Margin       = new Thickness(0, 1, 0, 1)
                };

                // Fixed-width prefix: time, bpm, spike-ms, Δprev
                inline.Inlines.Add(new System.Windows.Documents.Run
                {
                    Text       = $"  {timeStr,-8} {sp.Bpm,3}   {sp.SpikeMs,5:F2}   {deltaStr,6}   ",
                    Foreground = BrushText
                });

                bool selfInThis = false;
                if (sp.ActiveMachines != null && sp.ActiveMachines.Length > 0)
                {
                    for (int i = 0; i < sp.ActiveMachines.Length; i++)
                    {
                        var nm = sp.ActiveMachines[i];
                        bool isSelected = (nm == _selectedName);
                        if (isSelected) selfInThis = true;
                        inline.Inlines.Add(new System.Windows.Documents.Run
                        {
                            Text       = (i > 0 ? ", " : "") + nm,
                            Foreground = isSelected ? BrushBad : BrushText,
                            FontWeight = isSelected ? FontWeights.Bold : FontWeights.Normal
                        });
                    }
                }
                else
                {
                    inline.Inlines.Add(new System.Windows.Documents.Run
                    {
                        Text       = "(no attribution data)",
                        Foreground = BrushSubText
                    });
                }

                // Subtle background tint when the selected machine was active
                _spikeListPanel.Children.Add(new Border
                {
                    Background = selfInThis ? BrushTrack : Brushes.Transparent,
                    Padding    = new Thickness(2, 0, 2, 0),
                    Child      = inline
                });
            }
        }


        // ═════════════════════════════════════════════════════════════════════
        // Parameter list — built once on selection, values updated each tick
        // ═════════════════════════════════════════════════════════════════════
        readonly Dictionary<string, TextBlock> _paramValueText = new();
        readonly Dictionary<string, TextBlock> _paramRateText  = new();
        readonly Dictionary<string, Border>    _paramRow       = new();

        void RefreshParameterRows()
        {
            _paramListPanel.Children.Clear();
            _paramValueText.Clear();
            _paramRateText.Clear();
            _paramRow.Clear();

            if (_selectedIMachine == null) return;
            IList<IParameterGroup>? groups = null;
            try { groups = _selectedIMachine.ParameterGroups; } catch { }
            if (groups == null) return;

            for (int gi = 1; gi < groups.Count; gi++) // skip Input group 0
            {
                var g = groups[gi];
                if (g?.Parameters == null || g.Parameters.Count == 0) continue;

                _paramListPanel.Children.Add(new TextBlock
                {
                    Text       = gi == 1 ? "GLOBAL" : (gi == 2 ? "TRACK (track 0)" : $"GROUP {gi}"),
                    Foreground = BrushSubText,
                    FontFamily = Mono,
                    FontSize   = 9,
                    Margin     = new Thickness(0, 6, 0, 2)
                });

                foreach (var p in g.Parameters)
                {
                    if (p == null) continue;
                    string key = $"{gi}:{p.Name}";

                    var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });

                    var nameTb = new TextBlock
                    {
                        Text       = p.Name ?? "?",
                        Foreground = BrushText,
                        FontFamily = Mono,
                        FontSize   = 10,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    };
                    Grid.SetColumn(nameTb, 0);
                    row.Children.Add(nameTb);

                    var valTb = new TextBlock
                    {
                        Text       = "—",
                        Foreground = BrushText,
                        FontFamily = Mono,
                        FontSize   = 10,
                        TextAlignment = TextAlignment.Right
                    };
                    Grid.SetColumn(valTb, 1);
                    row.Children.Add(valTb);

                    var rateTb = new TextBlock
                    {
                        Text       = "",
                        Foreground = BrushSubText,
                        FontFamily = Mono,
                        FontSize   = 10,
                        TextAlignment = TextAlignment.Right
                    };
                    Grid.SetColumn(rateTb, 2);
                    row.Children.Add(rateTb);

                    var border = new Border
                    {
                        Background = Brushes.Transparent,
                        Padding    = new Thickness(4, 0, 4, 0),
                        Child      = row
                    };

                    _paramListPanel.Children.Add(border);
                    _paramValueText[key] = valTb;
                    _paramRateText[key]  = rateTb;
                    _paramRow[key]       = border;
                }
            }
        }

        void RefreshParameterValues()
        {
            if (_selectedIMachine == null) return;
            IList<IParameterGroup>? groups = null;
            try { groups = _selectedIMachine.ParameterGroups; } catch { }
            if (groups == null) return;

            long nowTicks = Environment.TickCount64;

            for (int gi = 1; gi < groups.Count; gi++)
            {
                var g = groups[gi];
                if (g?.Parameters == null) continue;
                foreach (var p in g.Parameters)
                {
                    if (p == null) continue;
                    string key = $"{gi}:{p.Name}";
                    if (!_paramValueText.TryGetValue(key, out var valTb)) continue;

                    int v;
                    string disp;
                    try { v = p.GetValue(0); disp = v.ToString(); }
                    catch { disp = "—"; }
                    valTb.Text = disp;

                    // Rate display: only show if recent activity
                    if (_paramWritesPerSec.TryGetValue(key, out var rate) && rate > 0.05)
                        _paramRateText[key].Text = rate.ToString("F1") + "/s";
                    else
                        _paramRateText[key].Text = "";

                    // Flash background for recently changed
                    if (_paramLastChangeTicks.TryGetValue(key, out var lastChg)
                        && (nowTicks - lastChg) < 200
                        && _paramRow.TryGetValue(key, out var row))
                    {
                        row.Background = BrushFlash;
                    }
                    else if (_paramRow.TryGetValue(key, out var row2))
                    {
                        row2.Background = Brushes.Transparent;
                    }
                }
            }
        }


        // ═════════════════════════════════════════════════════════════════════
        // Activity panel
        // ═════════════════════════════════════════════════════════════════════
        void RefreshActivity()
        {
            if (_selectedIMachine == null)
            {
                _activityText.Text = "—";
                return;
            }
            int tracks = 0;
            try { tracks = _selectedIMachine.TrackCount; } catch { }
            string type = "";
            try { type = _selectedIMachine.IsControlMachine ? "control" : (_selectedIMachine.Inputs?.Any() == true ? "effect" : "generator"); } catch { }
            _activityText.Text = $"Type: {type}   TrackCount: {tracks}";
        }


        // ═════════════════════════════════════════════════════════════════════
        // Global status footer
        // ═════════════════════════════════════════════════════════════════════
        void RefreshGlobalStatus(Profile2Snapshot snap)
        {
            SampleTrueDropouts();

            if (!snap.IsValid)
            {
                _globalStatusText.Text = "Global: warming up…";
            }
            else
            {
                int spikeCount = snap.Spikes?.Length ?? 0;

                // TRUE deadline-referenced dropouts (Option 2 v2): device
                // callbacks whose wall-time overran the buffer deadline — actual
                // missed deadlines, valid whether or not the fill thread is on.
                // The budget-relative snap.TotalDropouts is the legacy fallback.
                string dropSeg;
                if (_trueDropoutAvailable)
                    dropSeg = $"{_sessionResets} driver reset(s) (true dropout)";
                else
                    dropSeg = $"{snap.TotalDropouts} dropouts (budget)";

                _globalStatusText.Text =
                    $"Global: {snap.CpuPct:F1}% CPU  ·  {dropSeg}  ·  {spikeCount} spike(s) logged";
                // Colour bad if any missed deadline this session, else fall back to CPU.
                _globalStatusText.Foreground =
                      (_trueDropoutAvailable && _sessionResets > 0) ? BrushBad
                    : snap.CpuPct >= 90 ? BrushBad
                    : snap.CpuPct >= 70 ? BrushWarn
                    : BrushSubText;
            }

            // Engine settings — read from buzz.engineSettings each tick so toggles
            // in ReBuzz's settings window reflect live. Colored to flag the priority
            // value when it's not on the audio-focused profile (likely cause of OS
            // scheduling stalls under load).
            var es = ReadEngineSettings();
            if (es == null)
            {
                _engineSettingsText.Text = "Engine: (settings unavailable)";
                _engineSettingsText.Foreground = BrushSubText;
            }
            else
            {
                string mt   = es.Multithreading      ? "MT"          : "single-thread";
                string pmm  = es.ProcessMutedMachines ? "process-muted" : "skip-muted";
                string llgc = es.LowLatencyGC        ? "lowGC"       : "normalGC";
                // SubTickResolution (1827+): "Lower"/"Low" reduce sub-tick CPU
                // load — directly relevant to per-chunk overhead. Only shown when
                // the build exposes it and it's not the default "Normal".
                string subtick = (!string.IsNullOrEmpty(es.SubTickResolution) && es.SubTickResolution != "Normal")
                               ? $" · subtick={es.SubTickResolution}"
                               : "";
                string fill = es.AudioBufferFillThread ? " · fillthread" : "";
                _engineSettingsText.Text = $"Engine: {es.Priority} · {mt} · {pmm} · {llgc}{subtick}{fill}";

                // Flag the audio priority — non-AllFocusOnAudio profiles correlate
                // with scheduling-induced stalls when the box is under CPU pressure.
                bool prioHigh = es.Priority == "AllFocusOnAudio";
                _engineSettingsText.Foreground = prioHigh ? BrushSubText : BrushWarn;
            }

            // GC tracking — strong candidate for the 40 ms peaks now that we've
            // ruled out driver, song, transport, and OS priority
            UpdateGcStatus();
        }

        // ─── GC pressure tracking ────────────────────────────────────────────
        // The dropout pattern survives empty song + stopped transport + driver
        // change + priority change. That points at the .NET runtime itself —
        // most likely Gen 2 collections producing ~40 ms pauses. Track Gen 0/1/2
        // collection counts, compute per-second rates, and timestamp the last
        // Gen 2 event. If spikes correlate with Gen 2 increments → smoking gun.
        long _gcLastTickMs;
        int  _gcG0Prev, _gcG1Prev, _gcG2Prev;
        double _gcG0PerSec, _gcG1PerSec, _gcG2PerSec;
        long _gcLastGen2Ms = -1;

        void UpdateGcStatus()
        {
            int g0 = GC.CollectionCount(0);
            int g1 = GC.CollectionCount(1);
            int g2 = GC.CollectionCount(2);
            long now = Environment.TickCount64;

            // First call — seed and skip rate calc
            if (_gcLastTickMs == 0)
            {
                _gcG0Prev = g0; _gcG1Prev = g1; _gcG2Prev = g2;
                _gcLastTickMs = now;
                _gcStatusText.Text = $"GC: G0={g0}  G1={g1}  G2={g2}  ·  warming up";
                _gcStatusText.Foreground = BrushSubText;
                return;
            }

            double dt = (now - _gcLastTickMs) / 1000.0;
            if (dt >= 0.05)
            {
                double dg0 = (g0 - _gcG0Prev) / dt;
                double dg1 = (g1 - _gcG1Prev) / dt;
                double dg2 = (g2 - _gcG2Prev) / dt;
                const double alpha = 0.2;
                _gcG0PerSec = _gcG0PerSec * (1 - alpha) + dg0 * alpha;
                _gcG1PerSec = _gcG1PerSec * (1 - alpha) + dg1 * alpha;
                _gcG2PerSec = _gcG2PerSec * (1 - alpha) + dg2 * alpha;

                // Mark Gen 2 events so we can highlight them and correlate with spikes
                if (g2 > _gcG2Prev) _gcLastGen2Ms = now;

                _gcG0Prev = g0; _gcG1Prev = g1; _gcG2Prev = g2;
                _gcLastTickMs = now;
            }

            // Heap size — non-forcing read
            long heapBytes = 0;
            try { heapBytes = GC.GetTotalMemory(false); } catch { }
            double heapMB = heapBytes / 1024.0 / 1024.0;

            // Time since last Gen 2 — useful for spike correlation
            string sinceG2;
            if (_gcLastGen2Ms < 0) sinceG2 = "—";
            else
            {
                long ago = now - _gcLastGen2Ms;
                sinceG2 = ago < 1000 ? $"{ago} ms" : $"{ago / 1000.0:F1} s";
            }

            _gcStatusText.Text =
                $"GC: G0={g0} ({_gcG0PerSec:F1}/s)  ·  G1={g1} ({_gcG1PerSec:F2}/s)  ·  G2={g2} ({_gcG2PerSec:F2}/s)  ·  heap {heapMB:F1} MB  ·  last G2 {sinceG2} ago";

            // Flash red briefly after a Gen 2 collection — it's the suspected
            // root cause, so make it visually loud when it happens
            bool recentGen2 = (_gcLastGen2Ms >= 0 && (now - _gcLastGen2Ms) < 1500);
            _gcStatusText.Foreground = recentGen2 ? BrushBad
                                     : _gcG2PerSec > 0.5 ? BrushWarn
                                     : BrushSubText;
        }


        // ─── Engine settings reflection ──────────────────────────────────────
        class EngineSettingsInfo
        {
            public string Priority = "?";
            public bool   Multithreading;
            public bool   ProcessMutedMachines;
            public bool   LowLatencyGC;
            public string SubTickResolution = "";   // 1827+: "Lower"/"Low" reduce sub-tick CPU
            public bool   AudioBufferFillThread;     // 1827+: background ring-buffer fill thread
            public bool   MachineDelayCompensation;
            public bool   AccurateBPM;
            public bool   SubTickTiming;
            public bool   UseCachedWorkOrder;        // 1834+ (#111): cached topological work order
            public bool   HasUseCachedWorkOrder;     // false on older builds where property doesn't exist
        }

        // Resolved once on first successful read, then cached.
        System.Reflection.FieldInfo?    _engineSettingsField;
        System.Reflection.PropertyInfo? _esPriorityProp;
        System.Reflection.PropertyInfo? _esMultithreadingProp;
        System.Reflection.PropertyInfo? _esProcessMutedProp;
        System.Reflection.PropertyInfo? _esLowLatencyGCProp;
        System.Reflection.PropertyInfo? _esSubTickResProp;
        System.Reflection.PropertyInfo? _esFillThreadProp;
        System.Reflection.PropertyInfo? _esDelayCompProp;
        System.Reflection.PropertyInfo? _esAccurateBpmProp;
        System.Reflection.PropertyInfo? _esSubTickTimingProp;
        System.Reflection.PropertyInfo? _esUseCachedWorkOrderProp;   // 1834+; null on older builds
        bool _esPropsResolved;
        bool _engineSettingsResolveFailed;

        // ─── Deadline-referenced dropout counters (PP2 Option 2) ─────────────
        // TRUE dropouts from ReBuzzCore (public prop DriverResetCount — 1835+;
        // null on older builds). This is the device driver's own reset/xrun
        // notification (ASIO DriverResetRequest / WASAPI PlaybackStopped-with-
        // exception) — the only real, device-reported dropout signal observable
        // in-process. Coarse (one reset can bundle several lost buffers) but
        // real, unlike the budget-relative TotalDropouts. Delta-sampled per UI
        // tick; guarded against a host-side counter reset.
        System.Reflection.PropertyInfo? _driverResetProp;
        System.Reflection.PropertyInfo? _fillThreadActiveProp;
        bool _underrunPropsResolved;
        bool _underrunResolveFailed;
        long _lastResetCount = -1;
        long _sessionResets;          // total driver resets since counters last reset
        bool _trueDropoutAvailable;   // build exposes the counter

        void SampleTrueDropouts()
        {
            if (_underrunResolveFailed) return;
            var buzz = _subscribedBuzz;
            if (buzz == null) return;
            try
            {
                if (!_underrunPropsResolved)
                {
                    var t = buzz.GetType();
                    _driverResetProp      = t.GetProperty("DriverResetCount");
                    _fillThreadActiveProp = t.GetProperty("FillThreadActive");
                    _underrunPropsResolved = true;
                    if (_driverResetProp == null)
                    {
                        _underrunResolveFailed = true;   // older host — fall back to budget metric
                        return;
                    }
                }
                long rc = (long)(_driverResetProp!.GetValue(buzz) ?? 0L);
                _trueDropoutAvailable = true;

                if (_lastResetCount < 0) _lastResetCount = rc;
                if (rc < _lastResetCount) { _lastResetCount = rc; _sessionResets = 0; } // host reset
                _sessionResets += rc - _lastResetCount;
                _lastResetCount = rc;
            }
            catch { _underrunResolveFailed = true; }
        }

        EngineSettingsInfo? ReadEngineSettings()
        {
            if (_engineSettingsResolveFailed) return null;
            var buzz = _subscribedBuzz;
            if (buzz == null) return null;

            try
            {
                if (_engineSettingsField == null)
                {
                    _engineSettingsField = buzz.GetType().GetField("engineSettings",
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Public    |
                        System.Reflection.BindingFlags.Instance);
                    if (_engineSettingsField == null) { _engineSettingsResolveFailed = true; return null; }
                }
                var es = _engineSettingsField.GetValue(buzz);
                if (es == null) return null;

                if (!_esPropsResolved)
                {
                    var t = es.GetType();
                    _esPriorityProp       = t.GetProperty("PriorityProfile");
                    _esMultithreadingProp = t.GetProperty("Multithreading");
                    _esProcessMutedProp   = t.GetProperty("ProcessMutedMachines");
                    _esLowLatencyGCProp   = t.GetProperty("LowLatencyGC");
                    _esSubTickResProp     = t.GetProperty("SubTickResolution");  // may be null on older builds
                    _esFillThreadProp     = t.GetProperty("AudioBufferFillThread"); // 1827+; null on older
                    _esDelayCompProp      = t.GetProperty("MachineDelayCompensation");
                    _esAccurateBpmProp    = t.GetProperty("AccurateBPM");
                    _esSubTickTimingProp  = t.GetProperty("SubTickTiming");
                    _esUseCachedWorkOrderProp = t.GetProperty("UseCachedWorkOrder");   // 1834+; null on older
                    _esPropsResolved = true;
                }

                var info = new EngineSettingsInfo();
                try { info.Priority             = _esPriorityProp?.GetValue(es)?.ToString() ?? "?"; } catch { }
                try { info.Multithreading       = (bool)(_esMultithreadingProp?.GetValue(es) ?? false); } catch { }
                try { info.ProcessMutedMachines = (bool)(_esProcessMutedProp?.GetValue(es) ?? false); } catch { }
                try { info.LowLatencyGC         = (bool)(_esLowLatencyGCProp?.GetValue(es) ?? false); } catch { }
                try { info.SubTickResolution    = _esSubTickResProp?.GetValue(es)?.ToString() ?? ""; } catch { }
                try { info.AudioBufferFillThread = (bool)(_esFillThreadProp?.GetValue(es) ?? false); } catch { }
                try { info.MachineDelayCompensation = (bool)(_esDelayCompProp?.GetValue(es) ?? false); } catch { }
                try { info.AccurateBPM           = (bool)(_esAccurateBpmProp?.GetValue(es) ?? false); } catch { }
                try { info.SubTickTiming         = (bool)(_esSubTickTimingProp?.GetValue(es) ?? false); } catch { }
                if (_esUseCachedWorkOrderProp != null)
                {
                    try
                    {
                        info.UseCachedWorkOrder = (bool)(_esUseCachedWorkOrderProp.GetValue(es) ?? false);
                        info.HasUseCachedWorkOrder = true;
                    }
                    catch { info.HasUseCachedWorkOrder = false; }
                }
                return info;
            }
            catch
            {
                _engineSettingsResolveFailed = true;
                return null;
            }
        }


        // ═════════════════════════════════════════════════════════════════════
        // Sparkline drawing
        // ═════════════════════════════════════════════════════════════════════
        void DrawSparkline()
        {
            _sparklineCanvas.Children.Clear();
            double w = _sparklineCanvas.ActualWidth;
            double h = _sparklineCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            // Find max for scaling
            double max = 0;
            for (int i = 0; i < HISTORY_LEN; i++)
                if (!double.IsNaN(_machineCostHistory[i]) && _machineCostHistory[i] > max)
                    max = _machineCostHistory[i];
            if (max <= 0) max = 1;

            int count = _machineCostFull ? HISTORY_LEN : _machineCostIdx;
            if (count < 2) return;

            var pts = new PointCollection();
            for (int i = 0; i < count; i++)
            {
                int idx = _machineCostFull
                    ? (_machineCostIdx + i) % HISTORY_LEN
                    : i;
                double v = _machineCostHistory[idx];
                if (double.IsNaN(v))
                {
                    // gap — flush current polyline, start new
                    if (pts.Count > 1)
                    {
                        _sparklineCanvas.Children.Add(new Polyline
                        {
                            Points          = pts,
                            Stroke          = BrushSparkline,
                            StrokeThickness = 1.2
                        });
                    }
                    pts = new PointCollection();
                    continue;
                }
                double x = i * (w / (count - 1));
                double y = h - (v / max) * h * 0.9 - 2;
                pts.Add(new Point(x, y));
            }
            if (pts.Count > 1)
            {
                _sparklineCanvas.Children.Add(new Polyline
                {
                    Points          = pts,
                    Stroke          = BrushSparkline,
                    StrokeThickness = 1.2
                });
            }

            // Latest value text
            if (!double.IsNaN(_lastKnownMachineCost))
            {
                _sparklineCanvas.Children.Add(new TextBlock
                {
                    Text       = "max " + max.ToString("F2") + " ms",
                    Foreground = BrushSubText,
                    FontFamily = Mono,
                    FontSize   = 8,
                    Margin     = new Thickness(2, 0, 0, 0)
                });
            }
        }


        // ═════════════════════════════════════════════════════════════════════
        // Measurement state machine — Solo or Marginal
        // ═════════════════════════════════════════════════════════════════════
        //   Idle → MutingPhase → Settling (1.5s) → Measuring → Restoring → Done
        //
        //   Solo mode:     captures _measureBeforeMs = current AvgOtherMs (unused
        //                  for compute, kept for symmetry), mutes ALL OTHER
        //                  non-control machines, measures = snapshot.AvgOtherMs.
        //                  Result = the target running alone (plus residual
        //                  overhead from still-running muted machines + host).
        //   Marginal mode: captures _measureBeforeMs = current AvgOtherMs, mutes
        //                  ONLY the target, measures snapshot.AvgOtherMs after
        //                  settle. Result = before − after. Subtracts out the
        //                  constant overhead that pollutes the solo reading.
        //
        // Profile-All only uses Solo mode (running marginal across every machine
        // would be misleading — each marginal subtracts a different baseline).
        // ═════════════════════════════════════════════════════════════════════
        void StartMeasurement(MeasureMode mode, string? targetName)
        {
            if (_measureState != MeasureState.Idle) return;
            if (_machine == null || _subscribedBuzz?.Song == null) return;

            string? target = targetName ?? _selectedName;
            if (target == null) return;

            IMachine? tgt = null;
            try { tgt = _subscribedBuzz.Song.Machines.FirstOrDefault(m => m.Name == target); }
            catch { }
            if (tgt == null) { _measureStatus.Text = "target not found"; return; }
            try { if (tgt.IsControlMachine) { _measureStatus.Text = "control machine — N/A"; return; } }
            catch { }

            _measureMode        = mode;
            _measureTargetName  = target;
            _measureState       = MeasureState.MutingPhase;
            _savedMuteStates    = new Dictionary<string, bool>();
            _measureBeforeMs    = _machine.Snapshot?.AvgOtherMs ?? double.NaN;
            _measureStatus.Foreground = BrushWarn;
            _measureStatus.Text = mode == MeasureMode.Solo
                ? $"Soloing {target}…"
                : $"Measuring marginal cost of {target}…";

            // Save all current mute states; mutate per mode.
            // Skip control machines either way — muting them is no-op or
            // meaningless and we shouldn't fight them.
            try
            {
                foreach (var m in _subscribedBuzz.Song.Machines)
                {
                    string name = m.Name;
                    bool isCtl = false;
                    try { isCtl = m.IsControlMachine; } catch { }
                    bool curMute = false;
                    try { curMute = m.IsMuted; } catch { }
                    _savedMuteStates[name] = curMute;

                    if (isCtl) continue;

                    if (mode == MeasureMode.Solo)
                    {
                        // Mute everyone except target
                        if (name == target) {
                            if (curMute) { try { m.IsMuted = false; } catch { } }
                        } else {
                            if (!curMute) { try { m.IsMuted = true; } catch { } }
                        }
                    }
                    else // Marginal
                    {
                        // Mute ONLY the target
                        if (name == target) {
                            if (!curMute) { try { m.IsMuted = true; } catch { } }
                        }
                        // others: leave alone
                    }
                }
            } catch { }

            // Wait for averaging window to settle, then measure
            _measureTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _measureTimer.Tick += MeasureTick_Read;
            _measureTimer.Start();
        }

        void MeasureTick_Read(object? s, EventArgs e)
        {
            _measureTimer!.Stop();
            _measureTimer.Tick -= MeasureTick_Read;
            _measureState = MeasureState.Measuring;

            var snap = _machine?.Snapshot;
            double after = snap?.AvgOtherMs ?? 0;
            double peak  = snap?.PeakOtherMs ?? double.NaN;

            if (_measureMode == MeasureMode.Solo)
            {
                _soloResultMs = after;
                _soloPeakMs   = peak;
                _soloLastMeasureTime = Environment.TickCount64;

                // Push into recent-readings ring for median
                _recentSolo[_recentSoloIdx] = after;
                _recentSoloIdx = (_recentSoloIdx + 1) % RECENT_SOLO_LEN;
                if (_recentSoloCount < RECENT_SOLO_LEN) _recentSoloCount++;

                // Store in Profile-All bucket if we're in that mode
                if (_profileAllState == ProfileAllState.Running && _measureTargetName != null)
                    _profileAllResults[_measureTargetName] = after;
            }
            else // Marginal
            {
                double delta = _measureBeforeMs - after;
                if (delta < 0) delta = 0;   // clamp negative noise to 0
                _marginalCostMs = delta;
            }

            // Restore mute states
            _measureState = MeasureState.Restoring;
            _measureStatus.Text = "Restoring mute states…";

            if (_savedMuteStates != null && _subscribedBuzz?.Song != null)
            {
                try
                {
                    foreach (var m in _subscribedBuzz.Song.Machines)
                    {
                        if (_savedMuteStates.TryGetValue(m.Name, out bool saved))
                        {
                            bool cur = false; try { cur = m.IsMuted; } catch { }
                            if (cur != saved) { try { m.IsMuted = saved; } catch { } }
                        }
                    }
                } catch { }
            }
            _savedMuteStates = null;

            // Short settle, then announce
            _measureTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _measureTimer.Tick += MeasureTick_Done;
            _measureTimer.Start();
        }

        void MeasureTick_Done(object? s, EventArgs e)
        {
            _measureTimer!.Stop();
            _measureTimer.Tick -= MeasureTick_Done;
            _measureState = MeasureState.Idle;

            if (_profileAllState == ProfileAllState.Running)
            {
                _profileAllIdx++;
                AdvanceProfileAll();
            }
            else
            {
                _measureStatus.Foreground = BrushOk;
                _measureStatus.Text = _measureMode == MeasureMode.Solo
                    ? $"Solo = {_soloResultMs:F2} ms"
                    : $"Marginal = {_marginalCostMs:F2} ms";
            }
        }


        // ═════════════════════════════════════════════════════════════════════
        // Profile All state machine
        // ═════════════════════════════════════════════════════════════════════
        void StartProfileAll()
        {
            if (_profileAllState != ProfileAllState.Idle) return;
            if (_subscribedBuzz?.Song == null) return;

            try
            {
                _profileAllQueue = _subscribedBuzz.Song.Machines
                    .Where(m => { try { return !m.IsControlMachine; } catch { return false; } })
                    .Select(m => m.Name)
                    .ToList();
            } catch { _profileAllQueue = new List<string>(); }

            if (_profileAllQueue.Count == 0)
            {
                _profileAllStatus.Text = "(no non-control machines to profile)";
                return;
            }

            _profileAllResults.Clear();
            _profileAllResultsPanel.Children.Clear();
            _profileAllIdx       = 0;
            _profileAllState     = ProfileAllState.Running;
            _profileAllBtn.IsEnabled       = false;
            _profileAllEngineBtn.IsEnabled = false;
            // Snapshot budget at start — bars will rescale against this rather
            // than against the heaviest machine, so absolute proportions stay
            // meaningful across runs.
            _profileAllBudgetMs  = _machine?.Snapshot?.BudgetMs ?? double.NaN;
            AdvanceProfileAll();
        }

        void AdvanceProfileAll()
        {
            if (_profileAllQueue == null) return;

            if (_profileAllIdx >= _profileAllQueue.Count)
            {
                // Done — render results
                _profileAllState = ProfileAllState.Done;
                _profileAllBtn.IsEnabled       = true;
                _profileAllEngineBtn.IsEnabled = true;
                _profileAllStatus.Text   = $"Done — solo-profiled {_profileAllResults.Count} machine(s)";
                _profileAllStatus.Foreground = BrushOk;
                RenderProfileAllResults();
                return;
            }

            string next = _profileAllQueue[_profileAllIdx];
            _profileAllStatus.Text = $"Soloing {_profileAllIdx + 1}/{_profileAllQueue.Count}: {next}";
            _profileAllStatus.Foreground = BrushWarn;
            StartMeasurement(MeasureMode.Solo, next);
        }

        // ─── Engine-data Profile All — no muting, ~1 second total ──────────────
        // Reads (PerformanceCount, SampleCount) for every non-control machine at
        // T0, waits 1 second, reads again at T1, computes per-machine delta cost
        // from the difference. Non-invasive — playback continues normally —
        // and the values are the engine's own per-machine accounting rather
        // than our inferred mute-delta numbers.
        void StartProfileAllEngine()
        {
            if (_profileAllState != ProfileAllState.Idle) return;
            if (_subscribedBuzz?.Song == null || _machine == null) return;

            // Read T0 snapshot for every non-control machine
            var t0 = new Dictionary<string, (long perf, long samp)>();
            try
            {
                foreach (var m in _subscribedBuzz.Song.Machines)
                {
                    bool isCtl = false;
                    try { isCtl = m.IsControlMachine; } catch { }
                    if (isCtl) continue;
                    var raw = ReadEnginePerfRaw(m);
                    if (raw == null) continue;
                    t0[m.Name] = raw.Value;
                }
            } catch { }

            if (t0.Count == 0)
            {
                _profileAllStatus.Text = "(engine perf data not available)";
                _profileAllStatus.Foreground = BrushWarn;
                return;
            }

            _profileAllResults.Clear();
            _profileAllResultsPanel.Children.Clear();
            _profileAllState               = ProfileAllState.Running;
            _profileAllBtn.IsEnabled       = false;
            _profileAllEngineBtn.IsEnabled = false;
            _profileAllBudgetMs            = _machine.Snapshot?.BudgetMs ?? double.NaN;
            int sampleRate                 = _machine.Snapshot?.SampleRate ?? 48000;
            double budgetMs                = _profileAllBudgetMs;

            _profileAllStatus.Text       = $"Sampling engine perf for 1.0 s ({t0.Count} machines)…";
            _profileAllStatus.Foreground = BrushWarn;

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
            timer.Tick += (_, __) =>
            {
                timer.Stop();
                try
                {
                    foreach (var m in _subscribedBuzz!.Song.Machines)
                    {
                        bool isCtl = false;
                        try { isCtl = m.IsControlMachine; } catch { }
                        if (isCtl) continue;
                        if (!t0.TryGetValue(m.Name, out var prev)) continue;
                        var cur = ReadEnginePerfRaw(m);
                        if (cur == null) continue;

                        long dp = cur.Value.perf - prev.perf;
                        long ds = cur.Value.samp - prev.samp;
                        if (ds <= 0 || dp < 0) continue;

                        double cpuFraction = (dp * (double)sampleRate)
                                             / (ds * (double)Stopwatch.Frequency);
                        double msPerBuffer = (budgetMs > 0)
                                            ? cpuFraction * budgetMs
                                            : cpuFraction * 5.33;  // fallback
                        _profileAllResults[m.Name] = msPerBuffer;
                    }
                } catch { }

                _profileAllState               = ProfileAllState.Done;
                _profileAllBtn.IsEnabled       = true;
                _profileAllEngineBtn.IsEnabled = true;
                _profileAllStatus.Text         = $"Done — engine-profiled {_profileAllResults.Count} machine(s)";
                _profileAllStatus.Foreground   = BrushOk;
                RenderProfileAllResults();
            };
            timer.Start();
        }


        void RenderProfileAllResults()
        {
            _profileAllResultsPanel.Children.Clear();
            if (_profileAllResults.Count == 0) return;

            var sorted = _profileAllResults
                .OrderByDescending(kv => kv.Value)
                .ToList();

            // Scale against the buffer budget when known, so bar lengths reflect
            // absolute fraction of CPU budget (and stay comparable across runs)
            // rather than just within-this-run ranking. Fall back to heaviest
            // machine when budget unknown.
            double scale = _profileAllBudgetMs;
            if (double.IsNaN(scale) || scale <= 0) scale = sorted[0].Value;
            if (scale <= 0) scale = 1;

            string scaleNote = double.IsNaN(_profileAllBudgetMs)
                ? "(scaled to heaviest)"
                : $"(scaled to {_profileAllBudgetMs:F2} ms buffer budget)";

            _profileAllResultsPanel.Children.Add(new TextBlock
            {
                Text       = "Solo cost — sorted descending  " + scaleNote + "  (click to focus)",
                Foreground = BrushSubText,
                FontFamily = Mono,
                FontSize   = 9,
                Margin     = new Thickness(0, 4, 0, 4)
            });

            foreach (var kv in sorted)
            {
                var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });

                // Bar background
                var barBg = new Rectangle
                {
                    Fill   = BrushTrack,
                    Height = 16,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                Grid.SetColumn(barBg, 0);
                Grid.SetColumnSpan(barBg, 2);
                row.Children.Add(barBg);

                // Bar fill — proportion of scale (budget or heaviest)
                double prop = Math.Min(1.0, kv.Value / scale);
                var fill = new Rectangle
                {
                    Fill   = (prop >= 0.7) ? BrushBad : (prop >= 0.4) ? BrushWarn : BrushSparkline,
                    Height = 16,
                    Width  = 0,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                Grid.SetColumn(fill, 0);
                Grid.SetColumnSpan(fill, 2);
                row.Children.Add(fill);

                var name = new TextBlock
                {
                    Text       = "  " + kv.Key,
                    Foreground = BrushText,
                    FontFamily = Mono,
                    FontSize   = 10,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(name, 0);
                row.Children.Add(name);

                string valueText = !double.IsNaN(_profileAllBudgetMs) && _profileAllBudgetMs > 0
                    ? $"{kv.Value:F2} ms ({kv.Value / _profileAllBudgetMs * 100.0:F0}%)  "
                    : $"{kv.Value:F2} ms  ";
                var val = new TextBlock
                {
                    Text       = valueText,
                    Foreground = BrushText,
                    FontFamily = Mono,
                    FontSize   = 10,
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(val, 1);
                row.Children.Add(val);

                row.SizeChanged += (_, __) => { fill.Width = row.ActualWidth * prop; };

                var btn = new Button
                {
                    Background  = Brushes.Transparent,
                    BorderBrush = Brushes.Transparent,
                    Padding     = new Thickness(0),
                    Cursor      = Cursors.Hand,
                    Content     = row
                };
                string focusTarget = kv.Key;
                btn.Click += (_, __) =>
                {
                    if (_machineSelector.Items.Contains(focusTarget))
                        _machineSelector.SelectedItem = focusTarget;
                };
                _profileAllResultsPanel.Children.Add(btn);
            }
        }


        // ═════════════════════════════════════════════════════════════════════
        // Reflection-dump diagnostic (v2 — self-describing, percentile-based)
        // ═════════════════════════════════════════════════════════════════════
        // Produces a single paste-and-read diagnostic dump with every metric
        // needed for host-overhead / per-chunk optimisation work. Implements
        // the v2 spec (Notes_PedalProfiler2 §13 / Notes_Core §41):
        //   - Self-describing run context (transport, graph, settings)
        //   - Per-chunk OtherMs percentiles from the machine-side ring
        //   - Peak classification (TRANSIENT vs SUSTAINED) via 2×p99 sparsity
        //   - driver:chunk ratio cadence-inflation flag
        //   - Per-machine ENGINE% table from the existing MachinePerformanceData
        //     polling (no manual QPC arithmetic, no 1-s blocking pause)
        //   - GC summary, spike attribution with full ActiveMachines lists
        //   - Trust-vs-artifact legend at the foot
        //   - Machine-readable single line for diff/plot of a dump sequence
        //   - Optional Phase 2 raw reflection (verbose checkbox)
        //
        // Output: markdown-fenced text to file + system clipboard. No engine
        // state is modified. Read-only inspection.
        void DumpInternals()
        {
            var buzz = _subscribedBuzz;
            if (buzz == null) return;

            var sb = new System.Text.StringBuilder(64 * 1024);
            void Line(string s) => sb.AppendLine(s);
            bool verbose = _verboseDumpCheckbox?.IsChecked == true;

            // ── Collect once ────────────────────────────────────────────────
            var snap = _machine?.Snapshot;
            var es   = ReadEngineSettings();

            double budgetMs  = snap?.BudgetMs  ?? 0;
            int    srate     = snap?.SampleRate ?? 0;
            double elapsedS  = snap?.ElapsedSec ?? 0;
            int    chunkSmp  = (budgetMs > 0 && srate > 0)
                                 ? (int)Math.Round(budgetMs * srate / 1000.0) : 0;

            // ASIO buffer (real read from registry; "unknown" if we can't)
            var (asioBufSmp, driverLabel) = TryReadAsioBufferSize(buzz);
            double? driverChunkRatio = (asioBufSmp.HasValue && chunkSmp > 0)
                                         ? (double?)((double)asioBufSmp.Value / chunkSmp) : null;
            bool cadenceInflated = driverChunkRatio.HasValue && driverChunkRatio.Value > 3.0;

            // AudioThreads count (registry; default 4). Not a live property on
            // ReBuzzCore, but affects barrier / straggler behaviour and needs to
            // be per-dump reproducible.
            int? audioThreads = TryReadAudioThreads();

            // Per-chunk OtherMs ring → percentiles
            double[] otherMs = Array.Empty<double>();
            long     otherTotal = 0;
            try { _machine?.CopyRecentOtherMs(out otherMs, out otherTotal); } catch { otherMs = Array.Empty<double>(); }
            var pct = BuildPercentiles(otherMs);
            var (peakClass, peakOverCount, peakRateText) =
                ClassifyPeak(otherMs, pct.p99, elapsedS);

            // Graph topology counts
            var graph = WalkGraphCounts(buzz);

            // Per-machine cost (uses existing UpdateEngineCost EMA — no blocking)
            var perMachine = CollectPerMachineCosts(buzz, budgetMs);

            // SubTickSize (IBuzz public field, 260 on 1827)
            int subTickSize = 0;
            try
            {
                var fi = buzz.GetType().GetField("SubTickSize",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                if (fi != null) subTickSize = (int)(fi.GetValue(buzz) ?? 0);
            } catch { }

            // GC values (read fresh; the running UI tick has been polling these too)
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            double heapMB = 0;
            try { heapMB = GC.GetTotalMemory(false) / 1024.0 / 1024.0; } catch { }
            string g2AgoStr = "—";
            int? g2AgoSec = null;
            if (_gcLastGen2Ms >= 0)
            {
                long ms = Environment.TickCount64 - _gcLastGen2Ms;
                g2AgoSec = (int)(ms / 1000);
                g2AgoStr = ms < 1000 ? $"{ms} ms" : $"{ms / 1000.0:F1} s";
            }

            // Transport + song name + BPM
            bool playing = false; int bpm = 0; string songName = "(unsaved)";
            try { playing = buzz.Playing; } catch { }
            try { bpm = buzz.BPM; } catch { }
            try { songName = buzz.Song?.SongName ?? "(unsaved)"; } catch { }
            if (string.IsNullOrEmpty(songName)) songName = "(unsaved)";
            else { try { songName = System.IO.Path.GetFileName(songName); } catch { } }

            double engineTotalMs  = perMachine.Sum(p => double.IsNaN(p.msPerBuf) ? 0 : p.msPerBuf);
            double engineTotalPct = (budgetMs > 0) ? engineTotalMs / budgetMs * 100.0 : 0;
            double unaccountedMs  = Math.Max(0, budgetMs - engineTotalMs);
            double unaccountedPct = (budgetMs > 0) ? unaccountedMs / budgetMs * 100.0 : 0;
            int    managedCount   = perMachine.Count(p => !p.isNative);
            int    nativeCount    = perMachine.Count(p =>  p.isNative);

            // ── EMIT ────────────────────────────────────────────────────────
            Line("```");

            // Header
            int buildNumber = 0;
            try
            {
                var pi = buzz.GetType().GetProperty("BuildNumber");
                if (pi != null) buildNumber = (int)(pi.GetValue(buzz) ?? 0);
            } catch { }
            string buildStr = buildNumber > 0 ? $"ReBuzz Build {buildNumber}" : "ReBuzz Build ?";
            Line($"═══ PP2 DUMP  {DateTime.Now:yyyy-MM-dd HH:mm:ss} │ PP2 v{Profiler2Machine.Version} │ {buildStr} ═══");
            Line("");

            // Machine-readable single line (versioned, parseable).
            // Bumped v1 → v2 in PP2 v1.9.3: added `ci=` (cadence-inflated flag
            // for the whole dropout family) and `athreads=` (AudioThreads from
            // registry). Diffing scripts targeting v1 should update the field
            // set OR pin to v1-format legacy dumps.
            string mline = BuildMachineLine(
                version: "PP2 v2",
                t: elapsedS, chunkMs: budgetMs,
                p50: pct.p50, p90: pct.p90, p99: pct.p99, max: pct.max,
                dropPct: PercentDropouts(snap), wdrop: snap?.WindowDropouts ?? 0,
                ovrPerSec: OverrunRate(snap), peakClass: peakClass,
                g2: g2, g2age: g2AgoSec,
                engPct: engineTotalPct, unaccPct: unaccountedPct,
                play: playing, bpm: bpm,
                graph: graph,
                asioBufSmp: asioBufSmp, chunkSmp: chunkSmp,
                ratio: driverChunkRatio,
                cadenceInflated: cadenceInflated, audioThreads: audioThreads,
                qpcHz: (long)Stopwatch.Frequency);
            Line(mline);
            Line("");

            // ── RUN CONTEXT ─────────────────────────────────────────────
            Line("── RUN CONTEXT ─────────────────────────────────────────────");
            Line(P("QPC freq (Stopwatch.Frequency)", $"{Stopwatch.Frequency} Hz"));
            Line(P("SampleRate",                     $"{srate}"));
            Line(P("BPM",                            $"{bpm}"));
            Line(P("Transport",                      playing ? "PLAYING" : "STOPPED",
                                                     $"(Playing={playing})"));
            Line(P("Song",                           songName));
            string asioStr = asioBufSmp.HasValue
                ? $"{asioBufSmp.Value} smp ({(asioBufSmp.Value * 1000.0 / Math.Max(1, srate)):F2} ms)"
                : "unknown";
            Line(P($"{driverLabel} buffer (driver)", asioStr,
                   asioBufSmp.HasValue ? "[from registry]" : "[no registry entry — driver pre-config]"));
            Line(P("Chunk period (BudgetMs)",
                   chunkSmp > 0 ? $"{budgetMs:F2} ms (~{chunkSmp} smp)" : $"{budgetMs:F2} ms"));
            if (driverChunkRatio.HasValue)
                Line(P("driver : chunk ratio",
                       $"{driverChunkRatio.Value:F2}",
                       cadenceInflated
                         ? "[>3 ⇒ WHOLE dropout family cadence-inflated: TotalDropouts%, WindowDropouts, overruns/s, p99/max, PeakOtherMs — Core §34.1, PP2 §9.5]"
                         : "[<3 ⇒ dropout family not cadence-inflated (still fill-thread-schedule-influenced when FillThread=ON)]"));
            else
                Line(P("driver : chunk ratio", "na", "(ASIO buffer unknown)"));
            Line(P("AudioThreads (registry)",
                   audioThreads.HasValue ? audioThreads.Value.ToString() : "unknown",
                   audioThreads.HasValue
                     ? "[HKCU\\Software\\ReBuzz\\Settings\\AudioThreads]"
                     : "[no registry entry — default 4]"));
            Line(P("Engine settings", FormatEngineSettings(es, subTickSize)));
            Line(P("Graph", FormatGraphCounts(graph)));
            Line("");

            // ── PER-CHUNK TIMING ────────────────────────────────────────
            Line("── PER-CHUNK TIMING (p50/p90 trust; p99/max symptom, see caveats) ─");
            if (pct.n > 0)
            {
                Line(P("OtherMs",
                       $"p50={pct.p50:F2}  p90={pct.p90:F2}  p99={pct.p99:F2}  max={pct.max:F2}",
                       $"(n={pct.n} chunks, window≈{(pct.n * Math.Max(budgetMs,0.001) / 1000.0):F1}s)"));
                // p99/max caveat — fill-thread-schedule under FillThread=ON (PP2 §14.2)
                // AND cadence-inflated at ratio > 3 (Core §34.1, PP2 §9.5). Both are
                // symptom metrics under those regimes, not cost metrics.
                bool fillOn = es?.AudioBufferFillThread == true;
                if (fillOn && cadenceInflated)
                    Line(P("  ↳ p99/max caveat", "fill-thread-schedule AND cadence-inflated",
                           "[both regimes active — symptom only]"));
                else if (fillOn)
                    Line(P("  ↳ p99/max caveat", "fill-thread-schedule",
                           "[FillThread=ON: p99/max reflect fill-thread sleep gaps, not chunk cost — PP2 §14.2]"));
                else if (cadenceInflated)
                    Line(P("  ↳ p99/max caveat", "cadence-inflated",
                           "[ratio>3: p99/max over-report vs driver deadline — PP2 §9.5]"));
                Line(P("peak class", peakClass, peakRateText));
            }
            else
            {
                Line(P("OtherMs", "(no samples yet — too early?)"));
            }
            Line(P("AvgOtherMs",
                   snap != null ? $"{snap.AvgOtherMs:F2}" : "—",
                   "[≈ Budget = utilisation artifact; NOT cpu]"));
            Line(P("CpuPct",
                   snap != null ? $"{snap.CpuPct:F3}" : "—",
                   "[buffer-cycle utilisation; ~100% when healthy; ignore]"));
            Line("");

            // ── DROPOUTS / OVERRUNS ─────────────────────────────────────
            Line("── DROPOUTS / OVERRUNS ─────────────────────────────────────");
            int wdrop = snap?.WindowDropouts ?? 0;
            string wdropTrail = cadenceInflated
                ? "  [cadence-inflated; ratio>3 — internal-budget reference, not ASIO deadline; PP2 §10]"
                : "← rare-vs-recurring discriminator (current window only)";
            Line(P("WindowDropouts", $"{wdrop}", wdropTrail));
            if (snap != null && snap.TotalBuffers > 0)
            {
                double dropPct = snap.TotalDropouts * 100.0 / snap.TotalBuffers;
                string cad = cadenceInflated ? "  [cadence-inflated; ratio>3]" : "";
                Line(P("TotalDropouts / TotalBuffers",
                       $"{snap.TotalDropouts} / {snap.TotalBuffers} = {dropPct:F2}%{cad}"));
            }
            else
                Line(P("TotalDropouts / TotalBuffers", "—"));
            double rate = OverrunRate(snap);
            string rateTrail = cadenceInflated
                ? "[SpikeRawTotal/ElapsedSec — a TALLY, not distinct events]  [cadence-inflated; ratio>3]"
                : "[SpikeRawTotal/ElapsedSec — a TALLY, not distinct events]";
            Line(P("Overrun rate", $"{rate:F1}/s", rateTrail));
            Line(P("ElapsedSec", $"{elapsedS:F1}"));
            Line("");

            // ── COST DECOMPOSITION ──────────────────────────────────────
            Line("── COST DECOMPOSITION ──────────────────────────────────────");
            Line(P("Engine Total",
                   $"{engineTotalMs:F3} ms ({engineTotalPct:F1}%)",
                   $"n={managedCount} managed (+{nativeCount} native)"));
            string verdict = unaccountedPct > 90 ? "→ HOST OVERHEAD DOMINATES (>90%)"
                           : unaccountedPct > 50 ? "→ host overhead significant"
                           : unaccountedPct > 20 ? "→ machines and host comparable"
                           : "→ machine work dominates";
            Line(P("Unaccounted",
                   $"{unaccountedMs:F3} ms ({unaccountedPct:F1}%)",
                   verdict));
            Line("");

            // ── PER-MACHINE COST ────────────────────────────────────────
            Line("── PER-MACHINE COST (continuous EMA from MachinePerformanceData) ─");
            if (perMachine.Count > 0)
            {
                Line(string.Format("  {0,-30} {1,-5} {2,8}   {3,8}", "machine", "type", "ENGINE%", "ms/buf"));
                Line("  " + new string('─', 56));
                foreach (var p in perMachine)
                {
                    if (p.isNative)
                        Line(string.Format("  {0,-30} {1,-5} {2,8}   {3,8}",
                            Trunc(p.name, 30), p.type, "n/a", "(native)"));
                    else if (p.isUnavailable)
                    {
                        string label = p.statusTag == "idle" ? "(idle)" : "(warming up)";
                        Line(string.Format("  {0,-30} {1,-5} {2,8}   {3,8}",
                            Trunc(p.name, 30), p.type, "—", label));
                    }
                    else
                        Line(string.Format("  {0,-30} {1,-5} {2,8:F2} {3,8:F3}",
                            Trunc(p.name, 30), p.type, p.enginePct, p.msPerBuf));
                }
            }
            else
            {
                Line("  (no machines)");
            }
            Line("");

            // ── GC ──────────────────────────────────────────────────────
            Line("── GC ──────────────────────────────────────────────────────");
            string g2AgePhrase = _gcLastGen2Ms >= 0
                ? $"last G2 {g2AgoStr} ago"
                : "last G2 not observed since PP2 loaded";
            Line(string.Format("  G0={0} ({1:F1}/s)  G1={2} ({3:F2}/s)  G2={4} ({5:F2}/s)  heap {6:F1} MB  {7}",
                g0, _gcG0PerSec, g1, _gcG1PerSec, g2, _gcG2PerSec, heapMB, g2AgePhrase));
            Line("");

            // ── SPIKES ──────────────────────────────────────────────────
            Line("── SPIKES (recorded ring, full ActiveMachines, uncapped attribution) ─");
            Line(string.Format("  rate {0:F1}/s  (recorded list capped at 1 / 0.5 s — intervals NOT meaningful, §6.4)", rate));
            if (snap?.Spikes != null && snap.Spikes.Length > 0)
            {
                int i = 0;
                Spike2Record prev = default;
                bool havePrev = false;
                foreach (var sp in snap.Spikes)
                {
                    string dprev = havePrev
                        ? $"Δprev={(prev.ElapsedSec - sp.ElapsedSec)*1000:F0}ms"
                        : "Δprev=—";
                    string active = (sp.ActiveMachines == null || sp.ActiveMachines.Length == 0)
                        ? "(none)"
                        : string.Join(",", sp.ActiveMachines);
                    Line(string.Format("  [{0}] t={1:F2}  spike={2:F2}ms  budget={3:F2}  {4}  active=[{5}]",
                        i, sp.ElapsedSec, sp.SpikeMs, sp.BudgetMs, dprev, active));
                    prev = sp; havePrev = true; i++;
                }
            }
            else
            {
                Line("  (no spikes recorded)");
            }
            Line("");

            // ── PHASE 2 raw reflection (verbose only) ───────────────────
            if (verbose)
            {
                Line("── PHASE 2: raw reflection (verbose) ───────────────────────");
                DumpObject(buzz,      "IBuzz (Global.Buzz)",                 Line, maxDepth: 1);
                DumpObject(buzz.Song, "IBuzz.Song",                          Line, maxDepth: 1);

                if (_selectedIMachine != null)
                {
                    DumpObject(_selectedIMachine, $"IMachine '{_selectedIMachine.Name}'", Line, maxDepth: 1);
                    try
                    {
                        var groups = _selectedIMachine.ParameterGroups;
                        if (groups != null && groups.Count >= 2 && groups[1].Parameters.Count > 0)
                        {
                            var p = groups[1].Parameters[0];
                            DumpObject(p, $"IParameter '{p.Name}'", Line, maxDepth: 1);
                        }
                    } catch { }
                }
                if (_machine != null)
                    DumpObject(_machine.Snapshot, "Profile2Snapshot (this v2 instance)", Line, maxDepth: 1);

                Line("");
                DumpObject(GetProp(buzz, "PerformanceCurrent"),
                           "BuzzPerformanceData (buzz.PerformanceCurrent)", Line, maxDepth: 1);
                DumpObject(GetProp(buzz, "PerformanceData"),
                           "BuzzPerformanceData (buzz.PerformanceData)",    Line, maxDepth: 1);
                DumpObject(GetProp(buzz, "AudioEngine"),
                           "AudioEngine (buzz.AudioEngine)",                Line, maxDepth: 1);
                DumpObject(GetField(buzz, "engineSettings"),
                           "EngineSettings (buzz.engineSettings)",          Line, maxDepth: 1);
                if (_selectedIMachine != null)
                {
                    DumpObject(GetProp(_selectedIMachine, "PerformanceData"),
                               $"MachinePerformanceData '{_selectedIMachine.Name}'.PerformanceData",
                               Line, maxDepth: 1);
                    DumpObject(GetProp(_selectedIMachine, "PerformanceDataCurrent"),
                               $"MachinePerformanceData '{_selectedIMachine.Name}'.PerformanceDataCurrent",
                               Line, maxDepth: 1);
                }
                var snap2 = _machine?.Snapshot;
                if (snap2?.Spikes != null && snap2.Spikes.Length > 0)
                    DumpObject((object)snap2.Spikes[0], "Spike2Record [0]", Line, maxDepth: 1);
                Line("");
            }

            // ── Trust-vs-artifact legend (always at foot) ───────────────
            Line("── LEGEND ──────────────────────────────────────────────────");
            Line("  TRUST:  ears · OtherMs p50/p90 · per-machine ENGINE% · GC last-G2 age · Unaccounted%");
            Line("  DISTRUST as audible-dropout proxies (all cadence-inflate together at driver:chunk > 3):");
            Line("     TotalDropouts%, WindowDropouts, overruns/s, OtherMs p99/max, PeakOtherMs");
            Line("     — internal-budget reference, not ASIO deadline (perf handoff §10, PP2 §9.5, §14.2)");
            Line("  LABEL:  AvgOtherMs / CpuPct = buffer-cycle utilisation (≈100% healthy AND saturated) — not CPU");
            Line("  LABEL:  p99 / max under FillThread=ON reflect fill-thread sleep gaps, not chunk cost (PP2 §14.2)");
            Line("  NOTE:   Overrun rate / SpikeRawTotal = per-cycle TALLY, NOT distinct large events");
            Line("");
            Line("end PP2 dump");
            Line("```");

            string dump = sb.ToString();
            int kb = (dump.Length + 1023) / 1024;

            // --- Write to file -------------------------------------------------
            string? filePath = null;
            string? fileError = null;
            try
            {
                string folder = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "ReBuzz PP2 Dumps");
                System.IO.Directory.CreateDirectory(folder);
                filePath = System.IO.Path.Combine(folder,
                    $"pp2_dump_{DateTime.Now:yyyy-MM-dd_HHmmss}.txt");
                System.IO.File.WriteAllText(filePath, dump);
            }
            catch (Exception ex)
            {
                fileError = $"{ex.GetType().Name}: {ex.Message}";
                filePath = null;
            }

            // --- Copy to clipboard --------------------------------------------
            bool clipboardOk = false;
            string? clipboardError = null;
            try
            {
                System.Windows.Clipboard.SetText(dump);
                clipboardOk = true;
            }
            catch (Exception ex)
            {
                clipboardError = $"{ex.GetType().Name}: {ex.Message}";
            }

            // --- Surface status -----------------------------------------------
            string msg;
            Brush brush;
            if (filePath != null && clipboardOk)
            {
                msg   = $"OK · {kb} KB → clipboard + {filePath}";
                brush = BrushOk;
            }
            else if (filePath != null)
            {
                msg   = $"file OK ({kb} KB): {filePath} — clipboard FAILED: {clipboardError}";
                brush = BrushWarn;
            }
            else if (clipboardOk)
            {
                msg   = $"clipboard OK ({kb} KB) — file FAILED: {fileError}";
                brush = BrushWarn;
            }
            else
            {
                msg   = $"FAILED — file: {fileError} · clipboard: {clipboardError}";
                brush = BrushBad;
            }

            if (_dumpStatusText != null)
            {
                _dumpStatusText.Text       = msg;
                _dumpStatusText.Foreground = brush;
            }
            try { buzz.DCWriteLine($"[PP2] dump: {msg}"); } catch { }
        }

        // ── Dump helpers ────────────────────────────────────────────────────

        // Indent the "key value [trail]" rows uniformly.
        static string P(string key, string value, string trail = "")
        {
            // Indented row in the spec layout: 2-space indent, key padded to 32,
            // value, then optional trailing comment / context.
            if (string.IsNullOrEmpty(trail))
                return $"  {key,-32} {value}";
            return $"  {key,-32} {value}   {trail}";
        }

        static string Trunc(string s, int max)
            => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max - 1) + "…");

        static double PercentDropouts(Profile2Snapshot? s)
            => (s != null && s.TotalBuffers > 0) ? s.TotalDropouts * 100.0 / s.TotalBuffers : 0;

        static double OverrunRate(Profile2Snapshot? s)
            => (s != null && s.ElapsedSec > 0) ? s.SpikeRawTotal / s.ElapsedSec : 0;

        // Reads ReBuzz's stored ASIO/WASAPI buffer size from the registry. ReBuzz
        // writes/reads this at engine init (AudioEngine.cs:99). There is no live
        // property exposing the running buffer size, so the registry is the
        // ground truth for "what the user selected last". Returns (null,
        // driverLabel) if the registry entry is absent (e.g. driver never
        // configured in this install) — Core §34.5/§41.2 forbid inferring from
        // Work() timing.
        (int? sizeSmp, string driverLabel) TryReadAsioBufferSize(IBuzz buzz)
        {
            string label = "Driver";
            string subKey;
            try
            {
                string drv = (buzz.SelectedAudioDriver ?? "").Trim();
                if (drv.StartsWith("ASIO ", StringComparison.OrdinalIgnoreCase))
                {
                    label  = "ASIO";
                    subKey = @"Software\ReBuzz\ASIO";
                }
                else if (drv.IndexOf("WASAPI", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    label  = "WASAPI";
                    subKey = @"Software\ReBuzz\WASAPI";
                }
                else
                {
                    return (null, "Driver");
                }
            }
            catch { return (null, "Driver"); }

            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(subKey);
                if (k == null) return (null, label);
                var v = k.GetValue("BufferSize");
                if (v is int i && i > 0) return (i, label);
                if (v != null && int.TryParse(v.ToString(), out int parsed) && parsed > 0)
                    return (parsed, label);
            }
            catch { }
            return (null, label);
        }

        // Reads ReBuzz's stored AudioThreads count from the registry
        // (HKCU\Software\ReBuzz\Settings\AudioThreads). ReBuzz reads this at
        // engine init in AudioEngine.cs / CommonAudioProvider.cs (default 4);
        // it's the count of worker threads used by the multi-thread audio graph
        // dispatch. Directly affects barrier / straggler behaviour on wide songs
        // (perf handoff §1, §6.2), so surface it per-dump for reproducibility.
        // Returns null if the registry entry is absent (never opened Prefs).
        int? TryReadAudioThreads()
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\ReBuzz\Settings");
                if (k == null) return null;
                var v = k.GetValue("AudioThreads");
                if (v is int i && i > 0) return i;
                if (v != null && int.TryParse(v.ToString(), out int parsed) && parsed > 0)
                    return parsed;
            }
            catch { }
            return null;
        }

        struct PercentileResult { public int n; public double p50, p90, p99, max; }

        static PercentileResult BuildPercentiles(double[] values)
        {
            var r = new PercentileResult();
            if (values == null || values.Length == 0) return r;
            // Sort a copy so the snapshot order isn't disturbed (it isn't reused here,
            // but keep the convention safe).
            var sorted = (double[])values.Clone();
            Array.Sort(sorted);
            r.n   = sorted.Length;
            r.p50 = Quantile(sorted, 0.50);
            r.p90 = Quantile(sorted, 0.90);
            r.p99 = Quantile(sorted, 0.99);
            r.max = sorted[sorted.Length - 1];
            return r;
        }

        static double Quantile(double[] sorted, double q)
        {
            // Nearest-rank quantile — good enough for diagnostic percentiles.
            if (sorted.Length == 0) return 0;
            if (q <= 0) return sorted[0];
            if (q >= 1) return sorted[sorted.Length - 1];
            int idx = (int)Math.Ceiling(q * sorted.Length) - 1;
            if (idx < 0) idx = 0;
            if (idx >= sorted.Length) idx = sorted.Length - 1;
            return sorted[idx];
        }

        // §6.4's cooldown skepticism generalised from spike-rate to the peak.
        // Counts how many chunks in the window exceeded 2×p99; few sparse hits =
        // TRANSIENT, many = SUSTAINED. Bound at "≈ 1 / N s" for transients so the
        // rarity is concrete.
        static (string label, int count, string rate) ClassifyPeak(double[] values, double p99, double elapsedS)
        {
            if (values == null || values.Length == 0) return ("UNKNOWN", 0, "(no samples)");
            double thr = 2.0 * p99;
            if (!(thr > 0)) thr = double.PositiveInfinity;
            int over = 0;
            for (int i = 0; i < values.Length; i++) if (values[i] > thr) over++;
            // "Sparse" if <0.5% of samples (≈ 1 in 200 chunks)
            double sparseLimit = Math.Max(2, values.Length * 0.005);
            string label = over <= sparseLimit ? "TRANSIENT" : "SUSTAINED";
            string rateText;
            if (over == 0)
                rateText = $"(max ≤ 2×p99 — no extremes in n={values.Length} chunks)";
            else if (label == "TRANSIENT" && elapsedS > 0)
                rateText = $"(max exceeded 2×p99 on {over}/{values.Length} chunks ≈ 1 / {elapsedS / over:F0} s)";
            else
                rateText = $"({over}/{values.Length} chunks exceeded 2×p99 — recurring cost)";
            return (label, over, rateText);
        }

        struct GraphCounts
        {
            public int total, active, muted, bypassed, soloed, control, native;
        }

        static GraphCounts WalkGraphCounts(IBuzz buzz)
        {
            var g = new GraphCounts();
            try
            {
                var ms = buzz.Song?.Machines;
                if (ms == null) return g;
                foreach (var m in ms)
                {
                    g.total++;
                    bool muted = false, bypassed = false, soloed = false, ctrl = false, isNative = false;
                    try { muted    = m.IsMuted; } catch { }
                    try { bypassed = m.IsBypassed; } catch { }
                    try { soloed   = m.IsSoloed; } catch { }
                    try { ctrl     = m.IsControlMachine; } catch { }
                    try { isNative = (m.ManagedMachine == null); } catch { }
                    if (muted)    g.muted++;
                    if (bypassed) g.bypassed++;
                    if (soloed)   g.soloed++;
                    if (ctrl)     g.control++;
                    if (isNative) g.native++;
                    // "active" = not muted and not bypassed (still produces output)
                    if (!muted && !bypassed) g.active++;
                }
            } catch { }
            return g;
        }

        static string FormatGraphCounts(GraphCounts g)
            => $"{g.total} machines  ·  active {g.active} · muted {g.muted} · bypassed {g.bypassed} · soloed {g.soloed} · control {g.control} · native {g.native}";

        static string FormatEngineSettings(EngineSettingsInfo? es, int subTickSize)
        {
            if (es == null) return "(unavailable)";
            string sub = string.IsNullOrEmpty(es.SubTickResolution) ? "n/a" : es.SubTickResolution;
            // UseCachedWorkOrder is 1834+ (#111). Show it inline if the property
            // exists on the running build; omit silently on older builds so the
            // dump stays clean across preview versions.
            string cwo = es.HasUseCachedWorkOrder ? $"  CachedWorkOrder={B(es.UseCachedWorkOrder)}" : "";
            return $"FillThread={B(es.AudioBufferFillThread)}  SubTickRes={sub}  SubTickSize={subTickSize}  MT={B(es.Multithreading)}\n" +
                   $"                                   ProcMuted={B(es.ProcessMutedMachines)}  DelayComp={B(es.MachineDelayCompensation)}  LowLatGC={B(es.LowLatencyGC)}  AccurateBPM={B(es.AccurateBPM)}\n" +
                   $"                                   SubTickTiming={B(es.SubTickTiming)}  Prio={es.Priority}{cwo}";
            static string B(bool b) => b ? "ON" : "OFF";
        }

        struct PerMachineRow
        {
            public string name;
            public string type;       // "GEN"/"FX"/"CTRL"
            public double enginePct;  // smoothed
            public double msPerBuf;   // smoothed
            public bool   isNative;
            public bool   isUnavailable;
            public string statusTag;  // "ok"/"idle"/"warmup"  (for the "(idle)" vs "(warming up)" display)
        }

        // Walks all machines, reads the existing smoothed (ENGINE%, ms/buf) from
        // _enginePerf (populated by the running UI tick), and returns rows sorted
        // descending by enginePct. Skips Master and control machines from the
        // sortable cost ranking (the spec excludes them) but counts them inline
        // so totals are honest.
        List<PerMachineRow> CollectPerMachineCosts(IBuzz buzz, double budgetMs)
        {
            var rows = new List<PerMachineRow>();
            try
            {
                var ms = buzz.Song?.Machines;
                if (ms == null) return rows;
                foreach (var m in ms)
                {
                    if (m == null) continue;
                    string typeTag = TypeTag(m);
                    if (typeTag == "CTRL") continue;       // exclude control machines from cost ranking

                    bool isNative = false;
                    try { isNative = (m.ManagedMachine == null); } catch { }

                    if (isNative)
                    {
                        rows.Add(new PerMachineRow {
                            name = m.Name, type = typeTag,
                            enginePct = 0, msPerBuf = double.NaN,
                            isNative = true, isUnavailable = false });
                        continue;
                    }

                    var cost = UpdateEngineCost(m, budgetMs);
                    double enginePct = 0, msPerBuf = double.NaN;
                    string statusTag = "warmup";   // "ok" / "idle" / "warmup"

                    if (cost != null)
                    {
                        enginePct = cost.Value.cpuPct;
                        msPerBuf  = cost.Value.msPerBuffer;
                        statusTag = "ok";
                    }
                    else if (_enginePerf.TryGetValue(m.Name, out var st))
                    {
                        if (!double.IsNaN(st.SmoothedCpuPct))
                        {
                            // Have prior smoothed data but this tick happened
                            // to read no progress — use the smoothed state.
                            enginePct = st.SmoothedCpuPct;
                            msPerBuf  = st.SmoothedMsPerBuffer;
                            statusTag = "ok";
                        }
                        else if (st.HasPrior)
                        {
                            // Entry seeded across many UI ticks but every read
                            // had deltaSamp == 0 — the machine's SampleCount
                            // hasn't advanced. Likely an idle generator (e.g.
                            // a sample player waiting for a trigger) or one
                            // ReBuzz doesn't populate perfData for. Distinct
                            // from "no data yet" so we don't mislead the reader.
                            statusTag = "idle";
                        }
                    }

                    rows.Add(new PerMachineRow {
                        name = m.Name, type = typeTag,
                        enginePct = enginePct, msPerBuf = msPerBuf,
                        isNative = false,
                        isUnavailable = statusTag != "ok",
                        statusTag = statusTag });
                }
            } catch { }
            // Sort: managed-cost rows desc, then unavailable, then native
            rows.Sort((a, b) =>
            {
                int sa = a.isNative ? 2 : a.isUnavailable ? 1 : 0;
                int sb = b.isNative ? 2 : b.isUnavailable ? 1 : 0;
                if (sa != sb) return sa.CompareTo(sb);
                return b.enginePct.CompareTo(a.enginePct);
            });
            return rows;
        }

        static string BuildMachineLine(
            string version, double t, double chunkMs,
            double p50, double p90, double p99, double max,
            double dropPct, int wdrop, double ovrPerSec, string peakClass,
            int g2, int? g2age,
            double engPct, double unaccPct,
            bool play, int bpm,
            GraphCounts graph,
            int? asioBufSmp, int chunkSmp, double? ratio,
            bool cadenceInflated, int? audioThreads,
            long qpcHz)
        {
            string asio  = asioBufSmp.HasValue ? asioBufSmp.Value.ToString() : "unknown";
            string ratioS = ratio.HasValue ? $"{ratio.Value:F2}" : "na";
            string g2ageS = g2age.HasValue ? g2age.Value.ToString() : "na";
            string ci     = cadenceInflated ? "1" : "0";
            string ath    = audioThreads.HasValue ? audioThreads.Value.ToString() : "na";
            // ci=1 means the whole dropout family (drop/wdrop/ovr/p99/max) is
            // cadence-inflated at this song's driver:chunk ratio > 3. A diffing
            // script should suppress those columns from comparison when ci=1.
            return $"{version} | t={t:F1} | chunk={chunkMs:F2} | " +
                   $"other_p50={p50:F2} p90={p90:F2} p99={p99:F2} max={max:F2} | " +
                   $"drop={dropPct:F2}% wdrop={wdrop} ovr={ovrPerSec:F1}/s peak={peakClass} ci={ci} | " +
                   $"g2={g2} g2age={g2ageS} | " +
                   $"eng={engPct:F1}% unacc={unaccPct:F1}% | " +
                   $"play={(play ? 1 : 0)} bpm={bpm} | " +
                   $"mach={graph.total}(a{graph.active} m{graph.muted} b{graph.bypassed} s{graph.soloed} c{graph.control} n{graph.native}) | " +
                   $"asio={asio} chunk_smp={chunkSmp} ratio={ratioS} athreads={ath} | " +
                   $"qpc={qpcHz}";
        }

        // Reflection helpers — best-effort public-property and private-field access
        static object? GetProp(object? obj, string name)
        {
            if (obj == null) return null;
            try { return obj.GetType().GetProperty(name)?.GetValue(obj); }
            catch { return null; }
        }

        static object? GetField(object? obj, string name)
        {
            if (obj == null) return null;
            try
            {
                var fi = obj.GetType().GetField(name,
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public    |
                    System.Reflection.BindingFlags.Instance);
                return fi?.GetValue(obj);
            } catch { return null; }
        }

        // Walk a single object: print its concrete type, then all public +
        // non-public instance properties & fields, dispatching by "interesting"
        // type. Stops at depth 0 for object-typed children (just notes their
        // type + a short preview) to keep output bounded.
        static void DumpObject(object? obj, string label, Action<string> Line, int maxDepth)
        {
            Line("");
            Line("── " + label + " ──");
            if (obj == null) { Line("  (null)"); return; }

            var t = obj.GetType();
            Line("  runtime type: " + t.FullName);
            Line("  assembly:     " + (t.Assembly?.GetName().Name ?? "?"));

            const System.Reflection.BindingFlags FLAGS =
                System.Reflection.BindingFlags.Public    |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance;

            // Properties first — usually the documented surface
            System.Reflection.PropertyInfo[] props;
            try { props = t.GetProperties(FLAGS); } catch { props = Array.Empty<System.Reflection.PropertyInfo>(); }
            Line($"  properties ({props.Length}):");
            foreach (var p in props.OrderBy(p => p.Name))
            {
                string val = "?";
                try
                {
                    if (p.GetIndexParameters().Length > 0) { val = "[indexer]"; }
                    else
                    {
                        var v = p.GetValue(obj);
                        val = FormatValue(v, maxDepth);
                    }
                }
                catch (Exception ex) { val = "<" + ex.GetType().Name + ">"; }
                Line($"    {p.Name,-32}  {ShortType(p.PropertyType),-26}  = {val}");
            }

            // Fields — where most "interesting" engine state lives
            System.Reflection.FieldInfo[] fields;
            try { fields = t.GetFields(FLAGS); } catch { fields = Array.Empty<System.Reflection.FieldInfo>(); }
            Line($"  fields ({fields.Length}):");
            foreach (var f in fields.OrderBy(f => f.Name))
            {
                string val = "?";
                try { val = FormatValue(f.GetValue(obj), maxDepth); }
                catch (Exception ex) { val = "<" + ex.GetType().Name + ">"; }
                Line($"    {f.Name,-32}  {ShortType(f.FieldType),-26}  = {val}");
            }
        }

        static string ShortType(Type t)
        {
            if (t == null) return "?";
            // Strip namespaces but keep generics visible
            string n = t.Name;
            if (t.IsGenericType)
            {
                var args = string.Join(",", t.GetGenericArguments().Select(a => a.Name));
                int tick = n.IndexOf('`');
                if (tick >= 0) n = n.Substring(0, tick);
                n = $"{n}<{args}>";
            }
            return n;
        }

        static string FormatValue(object? v, int maxDepth)
        {
            if (v == null) return "null";
            var t = v.GetType();
            // Primitives, strings, decimals, enums — print directly
            if (t.IsPrimitive || t.IsEnum || v is string || v is decimal || v is DateTime || v is TimeSpan)
            {
                string s = v.ToString() ?? "";
                if (s.Length > 80) s = s.Substring(0, 77) + "...";
                return v is string ? "\"" + s + "\"" : s;
            }
            // Collections — show count + first few
            if (v is System.Collections.IEnumerable enumerable && !(v is string))
            {
                int count = 0;
                var preview = new System.Text.StringBuilder();
                preview.Append("[");
                foreach (var item in enumerable)
                {
                    if (count > 0) preview.Append(", ");
                    if (count < 4) preview.Append(FormatValue(item, 0));
                    count++;
                    if (count > 4) { preview.Append("…"); break; }
                }
                preview.Append("] count≈").Append(count);
                return preview.ToString();
            }
            // Complex objects at depth 0 — just print type + ToString preview
            if (maxDepth <= 0)
            {
                string s = ShortType(t);
                try
                {
                    string ts = v.ToString() ?? "";
                    if (ts != t.FullName && ts.Length > 0 && ts.Length < 60) s += " {" + ts + "}";
                }
                catch { }
                return s;
            }
            return ShortType(t);
        }
    }
}
