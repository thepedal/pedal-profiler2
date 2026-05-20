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

                // Pre-seed selection from persisted state BEFORE populating
                // the combo. RefreshMachineList will pick this up if the name
                // is in the song; otherwise it falls back to index 0.
                _selectedName = _machine?.PersistedSelection;

                RefreshMachineList();
            }
        }


        // ─── Dispatcher timer (100ms UI refresh) ─────────────────────────────
        readonly DispatcherTimer _timer;


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
            var raw = ReadEnginePerfRaw(m);
            if (raw == null) return null;

            int sampleRate = _machine.Snapshot?.SampleRate ?? 0;
            if (sampleRate <= 0) return null;

            string name = m.Name;
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

        TextBlock   _globalStatusText= null!;

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

            Unloaded += (_, __) =>
            {
                _timer.Stop();
                _measureTimer?.Stop();
                _muteDeltaTimer?.Stop();

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
            // MachineCore, and a parameter's ParameterCore via reflection and
            // writes everything to the DC console. Maps the actual reachable
            // surface of ReBuzz internals so we can pick which fields are worth
            // exposing properly (e.g. per-machine Work() timing if it exists).
            root.Children.Add(SectionHeader("Diagnostics"));
            var diagRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            var dumpBtn = MakeButton("Dump Internals (DC)", 150);
            dumpBtn.Click += (_, __) => DumpInternalsToDC();
            diagRow.Children.Add(dumpBtn);
            diagRow.Children.Add(new TextBlock
            {
                Text       = "writes to ReBuzz Debug Console",
                Foreground = BrushSubText,
                FontFamily = Mono,
                FontSize   = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin     = new Thickness(8, 0, 0, 0)
            });
            root.Children.Add(diagRow);

            // ── Global status footer ─────────────────────────────────────────
            root.Children.Add(new Border
            {
                Background = BrushPanel,
                BorderBrush = BrushBorder,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 8, 0, 0),
                Child = (_globalStatusText = new TextBlock
                {
                    Text       = "Global: — CPU · — dropouts · — spikes",
                    Foreground = BrushSubText,
                    FontFamily = Mono,
                    FontSize   = 10
                })
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
            if (_selectedIMachine != null)
                UpdateEngineCost(_selectedIMachine, snap.BudgetMs);

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
            RefreshGlobalStatus(snap);
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
        // Mute-delta detection (passive — fires when user toggles mute)
        // ═════════════════════════════════════════════════════════════════════
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
            // delta samples.
            if (_enginePerf.TryGetValue(_selectedIMachine.Name, out var perfSt)
                && !double.IsNaN(perfSt.SmoothedMsPerBuffer))
            {
                _engineCostText.Text       = FormatMsWithPct(perfSt.SmoothedMsPerBuffer, budget);
                _engineCostText.Foreground = CostColor(perfSt.SmoothedMsPerBuffer, budget);
                _engineSubText.Text        = "live  ·  delta-sampled";
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

            if (withData == 0)
            {
                _spikeAttribText.Text       = $"({total} spikes captured, no attribution data yet)";
                _spikeAttribText.Foreground = BrushSubText;
            }
            else
            {
                double pct = activeCount * 100.0 / withData;
                _spikeAttribText.Text = $"Active during {activeCount} of last {withData} spikes ({pct:F0}%)";
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
                Text       = "  time     bpm   spike   active machines",
                Foreground = BrushSubText,
                FontFamily = Mono,
                FontSize   = 9,
                Margin     = new Thickness(0, 0, 0, 2)
            });

            foreach (var sp in snap.Spikes)
            {
                // "+M:SS.s" time format
                double elapsed = sp.ElapsedSec;
                int minutes = (int)(elapsed / 60.0);
                double secs = elapsed - minutes * 60.0;
                string timeStr = $"+{minutes}:{secs:00.0}";

                // Render active-machines list with the selected name highlighted
                var inline = new TextBlock
                {
                    FontFamily   = Mono,
                    FontSize     = 10,
                    TextWrapping = TextWrapping.Wrap,
                    Margin       = new Thickness(0, 1, 0, 1)
                };

                // Fixed-width prefix: time, bpm, spike-ms
                inline.Inlines.Add(new System.Windows.Documents.Run
                {
                    Text       = $"  {timeStr,-8} {sp.Bpm,3}   {sp.SpikeMs,5:F2}   ",
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
            if (!snap.IsValid)
            {
                _globalStatusText.Text = "Global: warming up…";
                return;
            }
            int spikeCount = snap.Spikes?.Length ?? 0;
            _globalStatusText.Text =
                $"Global: {snap.CpuPct:F1}% CPU  ·  {snap.TotalDropouts} dropouts  ·  {spikeCount} spike(s) logged";
            _globalStatusText.Foreground = snap.CpuPct >= 90 ? BrushBad
                                         : snap.CpuPct >= 70 ? BrushWarn
                                         : BrushSubText;
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
        // Reflection-dump diagnostic
        // ═════════════════════════════════════════════════════════════════════
        // Empirical map of what's reachable on ReBuzz internals. Walks:
        //   - IBuzz (Global.Buzz) — engine root
        //   - Selected IMachine cast to its concrete MachineCore type
        //   - One of the selected machine's IParameters cast to ParameterCore
        //   - The Snapshot from this Profiler2Machine instance (sanity check)
        //
        // Output via DCWriteLine, one line per call (it doesn't handle
        // multi-line strings — see Tracker §7.5). View in ReBuzz under the
        // Debug Console window.
        //
        // No engine state is modified. Read-only inspection.
        void DumpInternalsToDC()
        {
            var buzz = _subscribedBuzz;
            if (buzz == null) return;

            void Line(string s)
            {
                try { buzz.DCWriteLine(s); } catch { }
            }

            Line("");
            Line("════════════════════════════════════════════════════════════");
            Line("Pedal Profiler2 — reflection dump  " + DateTime.Now.ToString("HH:mm:ss"));
            Line("════════════════════════════════════════════════════════════");

            DumpObject(buzz,                "IBuzz (Global.Buzz)",                 Line, maxDepth: 1);
            DumpObject(buzz.Song,           "IBuzz.Song",                          Line, maxDepth: 1);

            if (_selectedIMachine != null)
            {
                DumpObject(_selectedIMachine, $"IMachine '{_selectedIMachine.Name}'", Line, maxDepth: 1);

                // First parameter of group 1 (globals), if any
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
            {
                DumpObject(_machine.Snapshot, "Profile2Snapshot (this v2 instance)", Line, maxDepth: 1);
            }

            // ─────────────────────────────────────────────────────────────────
            // PHASE 2: drill into perf/engine objects revealed by phase 1
            // ─────────────────────────────────────────────────────────────────
            Line("");
            Line("──── PHASE 2: drill-down on perf/engine objects ────");

            // Engine-level performance — what the host itself tracks for total CPU
            DumpObject(GetProp(buzz, "PerformanceCurrent"),
                       "BuzzPerformanceData (buzz.PerformanceCurrent)", Line, maxDepth: 1);
            DumpObject(GetProp(buzz, "PerformanceData"),
                       "BuzzPerformanceData (buzz.PerformanceData)",    Line, maxDepth: 1);

            // The audio engine itself — buffer state, driver-side stats
            DumpObject(GetProp(buzz, "AudioEngine"),
                       "AudioEngine (buzz.AudioEngine)",                Line, maxDepth: 1);

            // Engine settings — likely buffer size, sample-rate config, etc.
            DumpObject(GetField(buzz, "engineSettings"),
                       "EngineSettings (buzz.engineSettings)",          Line, maxDepth: 1);

            // PER-MACHINE PERFORMANCE — the headline target. If this carries a
            // work-time field, we can replace the entire mute-based measurement
            // apparatus with direct reads.
            if (_selectedIMachine != null)
            {
                DumpObject(GetProp(_selectedIMachine, "PerformanceData"),
                           $"MachinePerformanceData '{_selectedIMachine.Name}'.PerformanceData",
                           Line, maxDepth: 1);
                DumpObject(GetProp(_selectedIMachine, "PerformanceDataCurrent"),
                           $"MachinePerformanceData '{_selectedIMachine.Name}'.PerformanceDataCurrent",
                           Line, maxDepth: 1);
            }

            // Sample one Spike2Record so the user can see what we capture
            var snap = _machine?.Snapshot;
            if (snap?.Spikes != null && snap.Spikes.Length > 0)
            {
                DumpObject((object)snap.Spikes[0], "Spike2Record [0]", Line, maxDepth: 1);
            }

            Line("════════════════════════════════════════════════════════════");
            Line("end reflection dump");
            Line("");

            // Open the debug console so the user sees the output without hunting
            try
            {
                System.Windows.Application.Current?.Dispatcher?.BeginInvoke(
                    (Action)(() => { try { buzz.ExecuteCommand(BuzzCommand.DebugConsole); } catch { } }));
            } catch { }
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
