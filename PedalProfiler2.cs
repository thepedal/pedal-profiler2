// Pedal Profiler2 — Machine Inspector for ReBuzz
//
// A single-machine focused profiler. Companion to Pedal Profiler (v1), which
// is a global dashboard. v2's job is the opposite: select ONE machine and
// look at it in depth.
//
// Architecture
// ─────────────────────────────────────────────────────────────────────────────
// Control machine (void Work()), so ReBuzz calls us FIRST in every buffer,
// before all generators/effects. Same timing-bracket trick as v1:
//
//   [Our Work start] ── [all other machines Work] ── [Our Work start again]
//        ▲                                                  ▲
//        lastStart                                          nowStart
//        nowStart − lastEnd  = "other" duration this buffer
//        nowStart − lastStart = buffer period
//
// v2 adds two diagnostics on top of v1's global numbers:
//
//   (1) Spike attribution — each captured spike includes a snapshot of which
//       machines were not muted at capture time. The UI thread publishes this
//       active-machines list every 100ms; the audio thread reads it atomically
//       (volatile string[]) and stamps it into each SpikeRecord.
//
//   (2) Solo measurement — UI-thread state machine that briefly mutes every
//       machine except a selected one, snapshots AvgOtherMs, then restores
//       previous mute state. Reports the isolated cost of one machine. Also
//       drives a Profile-All mode that cycles through every machine.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;

namespace WDE.PedalProfiler2
{
    // ─── Immutable snapshot published from audio thread to UI thread ─────────
    // Audio thread writes a fresh instance per measurement window; UI thread
    // reads via volatile reference. Reference writes/reads are atomic on x64.
    public sealed class Profile2Snapshot
    {
        public double CpuPct       { get; init; }
        public double PeakCpuPct   { get; init; }
        public double AvgOtherMs   { get; init; }
        public double PeakOtherMs  { get; init; }
        public double PeriodMs     { get; init; }
        public double BudgetMs     { get; init; }
        public int    SampleRate   { get; init; }
        public bool   IsValid      { get; init; }

        // Spike log — newest first. Each entry includes ActiveMachines snapshot.
        public Spike2Record[] Spikes { get; init; } = Array.Empty<Spike2Record>();

        // Dropout stats (≥90% budget = near-dropout, same as v1)
        public int  WindowDropouts { get; init; }
        public long TotalDropouts  { get; init; }
        public long TotalBuffers   { get; init; }

        // Raw spike count — every chunk where other > 150% baseline, with NO
        // cooldown. The Spikes[] ring is cooldown-limited (≤1 per 500 ms) for a
        // readable list; this is the true overrun count for honest rate stats.
        // When the recorded-ring intervals collapse to ~the cooldown, the UI
        // uses this to report the real rate instead of a bogus periodicity.
        public long SpikeRawTotal  { get; init; }
        public double ElapsedSec   { get; init; }   // seconds since machine load
    }

    // ─── Spike capture with attribution ──────────────────────────────────────
    public struct Spike2Record
    {
        public double   SpikeMs;        // "other" duration that triggered capture
        public double   BudgetMs;       // baseline period at capture time
        public double   ElapsedSec;     // seconds since machine load
        public int      Bpm;            // BPM at capture time
        public string[] ActiveMachines; // who was NOT muted (UI-published snapshot)
    }


    [MachineDecl(Name = "Pedal Profiler2", ShortName = "Pro2", Author = "WDE",
                 MaxTracks = 0, InputCount = 0, OutputCount = 0)]
    public class Profiler2Machine : IBuzzMachine
    {
        // ─── Version (single source of truth) ────────────────────────────────
        // Referenced from the reflection dump header and the About window.
        // Keeps the dump, About dialog, and git tag from drifting apart.
        internal const string Version = "2.1.0";


        // ─── ReBuzz context menu — About entry ───────────────────────────────
        // ReBuzz picks up an optional `Commands` property on the machine class
        // (duck-typed, per IBuzzMachine.cs comment at line 31) and injects the
        // entries into the machine's right-click menu in the Machine View.
        public IEnumerable<IMenuItem> Commands
        {
            get
            {
                yield return new MenuItemVM()
                {
                    Text = "About...",
                    Command = new SimpleCommand()
                    {
                        CanExecuteDelegate = p => true,
                        ExecuteDelegate = p => MessageBox.Show(
                            $"Pedal Profiler2   v{Version}\n\n" +
                            "Per-machine CPU inspector for ReBuzz.\n" +
                            "Cost measurement, spike attribution, dump export,\n" +
                            "and Flight Recorder — opt-in glitch capture that\n" +
                            "auto-marks real ASIO deadline misses and reports\n" +
                            "which machines were active when they happened.\n\n" +
                            "github.com/thepedal/pedal-profiler2\n" +
                            "MIT License",
                            "About Pedal Profiler2")
                    }
                };

                // Flight Recorder — mouse fallback for marking. For reaction-timed
                // marking during the validation test prefer the "FR Mark" parameter
                // (MIDI-learn it to a momentary footswitch); a menu click is too slow.
                yield return new MenuItemVM()
                {
                    Text = "Flight Recorder — Mark now",
                    Command = new SimpleCommand()
                    {
                        CanExecuteDelegate = p => true,
                        ExecuteDelegate = p => MarkGlitch()
                    }
                };
            }
        }


        // ─── Host ────────────────────────────────────────────────────────────
        IBuzzMachineHost _host;
        public IBuzzMachineHost Host { get => _host; set => _host = value; }

        // ─── Parameters ──────────────────────────────────────────────────────
        // Window controls averaging responsiveness (same idea as v1).
        [ParameterDecl(Name = "Window", MinValue = 16, MaxValue = 128, DefValue = 64,
                       Description = "Buffers per averaging window")]
        public int Window { get; set; } = 64;

        // ─── Flight Recorder mark trigger (MIDI-learnable) ───────────────────
        // Rising edge (0 → non-zero) marks a glitch for the Flight Recorder.
        // MIDI-learn a momentary footswitch to this in ReBuzz for hands-free,
        // reaction-timed marking during the validation test. Appended after
        // Window so existing songs' parameter indices are unchanged.
        int _frMarkEdge;
        [ParameterDecl(Name = "FR Mark", MinValue = 0, MaxValue = 1, DefValue = 0,
                       Description = "Rising edge marks a glitch for the Flight Recorder")]
        public int FrMark
        {
            get => _frMarkEdge;
            set { if (value != 0 && _frMarkEdge == 0) MarkGlitch(); _frMarkEdge = value; }
        }


        // ─── Snapshot bridge (audio → UI, volatile reference swap) ───────────
        volatile Profile2Snapshot _snapshot = new Profile2Snapshot();
        public Profile2Snapshot Snapshot => _snapshot;


        // ─── Active-machines snapshot (UI → audio, for spike attribution) ────
        // Published by GUI on every UI tick (~100ms). Audio thread reads
        // atomically at spike capture time. Reference reads are atomic.
        volatile string[] _activeMachinesPublished = Array.Empty<string>();
        // Unchanged purpose: the not-muted set for PP2's own spike attribution.
        // The Flight Recorder no longer rides this — it's fed the IsActive set at
        // 50 Hz via FlightRecorderCostSample (below).
        public void PublishActiveMachines(string[] names)
            => _activeMachinesPublished = names ?? Array.Empty<string>();


        // ─── Per-chunk OtherMs snapshot (UI thread reads at dump time) ───────
        // Returns the most recent up to OTHER_RING_SIZE samples in chronological
        // order (oldest first) along with the all-time total write count so the
        // caller can tell if the ring is full (n == OTHER_RING_SIZE) or partial
        // (n == totalWrites < OTHER_RING_SIZE). Audio thread keeps writing
        // during the copy; we tolerate up to one torn 64-bit slot on the
        // boundary since percentiles over thousands of samples are robust to
        // single-sample noise.
        public void CopyRecentOtherMs(out double[] copy, out long totalWrites)
        {
            long total = _otherRingTotal;   // aligned 64-bit read, atomic on x64
            int n = (int)Math.Min(total, OTHER_RING_SIZE);
            var dst = new double[n];
            double freq = Stopwatch.Frequency;
            if (freq <= 0) freq = 1;
            long start = total - n;
            for (int i = 0; i < n; i++)
            {
                long ticks = _otherTicksRing[(int)((start + i) % OTHER_RING_SIZE)];
                dst[i] = ticks / freq * 1000.0;
            }
            copy = dst;
            totalWrites = total;
        }


        // ─── Cached sample rate (audio thread writes, UI thread reads) ───────
        volatile int _cachedSampleRate;


        // ─── Timing bracket state ────────────────────────────────────────────
        long _lastWorkStart;
        long _lastWorkEnd;
        bool _haveLastEnd;


        // ─── Window accumulators (reset every Window buffers) ────────────────
        long _sumOtherTicks;
        long _sumPeriodTicks;
        long _maxOtherTicks;
        long _maxOtherPeriodTicks;  // paired peak — see v1 bug fix
        int  _windowCount;
        int  _windowDropouts;


        // ─── All-time totals ─────────────────────────────────────────────────
        long _totalBuffers;
        long _totalDropouts;


        // ─── Per-chunk OtherTicks ring (for percentiles at dump time, §5.1) ──
        // Fixed-size circular buffer of every chunk's "other" duration in QPC
        // ticks. Audio thread appends each Work() call; GUI thread copies a
        // stable enough snapshot at dump time via CopyRecentOtherMs(). For
        // percentiles over thousands of samples, a one-sample torn read on the
        // boundary is noise — we don't lock. Size = 4096 ≈ 16 s at ~250
        // chunks/s, plenty for the optimisation A/B loop.
        const int OTHER_RING_SIZE = 4096;
        readonly long[] _otherTicksRing = new long[OTHER_RING_SIZE];
        long _otherRingTotal = 0;   // monotonic write count; index = total % SIZE


        // ─── Baseline period low-pass (128-buffer time constant) ─────────────
        long _baselinePeriodTicks;


        // ─── Spike ring buffer ───────────────────────────────────────────────
        const int  SPIKE_SLOTS       = 8;
        const long SPIKE_COOLDOWN_MS = 500;

        readonly Spike2Record[] _spikeRing = new Spike2Record[SPIKE_SLOTS];
        int  _spikeWrite      = 0;
        int  _spikeCount      = 0;
        long _lastSpikeTicks  = 0;
        long _spikeRawTotal   = 0;   // every >150% overrun, no cooldown


        // ─── Reference timestamp for spike "+mm:ss" displays ─────────────────
        readonly long _loadTimestamp;


        // ─── Flight Recorder (phase-1 glitch correlator) ─────────────────────
        // Always-on ring tape + mark/freeze. Owned by the machine so the whole
        // subsystem runs without touching the GUI file:
        //   • OnChunk  — audio thread, from Work()
        //   • OnMidiCC — audio thread, from MidiControlChange() (CC timeline)
        //   • OnCostSample + OnUiTick — UI thread, from PublishActiveMachines()
        //   • RequestMark — from the FR Mark parameter / Commands menu
        // Artifacts land in FrOutputDir (see DeliverFrArtifact).
        readonly FR.FlightRecorder _fr;

        // ── Opt-in capture (default OFF — no files unless the user enables it) ─
        // Set by the GUI checkbox; read on the audio thread in Work() and on the
        // UI thread by the mark/cost entry points. Persisted in MachineState.
        public volatile bool FrEnabled;                 // default false
        public string FrOutputDir = Path.Combine(Path.GetTempPath(), "pp2_fr");


        // ─── Constructor ─────────────────────────────────────────────────────
        // §16.1: do NOT touch host.Machine here — it's null. Subscribing to
        // Song events happens in the GUI's Machine setter instead.
        public Profiler2Machine(IBuzzMachineHost host)
        {
            _host = host;
            _loadTimestamp = Stopwatch.GetTimestamp();
            _fr = new FR.FlightRecorder(DeliverFrArtifact, BuildFrRunContext);
        }


        // ─── Flight Recorder plumbing ────────────────────────────────────────

        // Mark a glitch. Thread-safe (RequestMark only stamps a QPC); the actual
        // freeze happens on the next FlightRecorderCostSample/OnUiTick. No-op when
        // capture is disabled so nothing is ever written behind the user's back.
        public void MarkGlitch() { if (!FrEnabled) return; try { _fr?.RequestMark(false); } catch { } }

        // Fed by the GUI's fast (~50 Hz) recorder timer with ReBuzz's IsActive set
        // (machines actually producing output), then pumps the freeze check.
        public void FlightRecorderCostSample(string[] activeNames)
        {
            if (!FrEnabled) return;
            try { _fr?.OnCostSample(activeNames); _fr?.OnUiTick(); } catch { }
        }

        // Auto-mark from a real ASIO deadline miss — a latency-free trigger, unlike
        // manual marks. Severity (worst overrun µs) is stashed for the CSV header.
        public long LastDeadlineOverrunUs;
        public void MarkGlitchAuto() { if (!FrEnabled) return; try { _fr?.RequestMark(true); } catch { } }

        // MIDI CC capture for the recorder timeline. Duck-typed by ReBuzz (like
        // Commands); called on the audio thread for incoming CC (PeerCtrl §10).
        // Harmless if this build doesn't route CC to control machines.
        public void MidiControlChange(int ctrl, int channel, int value)
        {
            try { _fr?.OnMidiCC(ctrl, channel, value); } catch { }
        }

        // Artifact delivery to the user-configured folder (default %TEMP%\pp2_fr).
        // Instance method so it reads the live FrOutputDir.
        void DeliverFrArtifact(string name, string content)
        {
            try
            {
                string dir = string.IsNullOrWhiteSpace(FrOutputDir)
                    ? Path.Combine(Path.GetTempPath(), "pp2_fr")
                    : FrOutputDir;
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, name), content);
            }
            catch { /* never let recorder I/O disturb the host */ }
        }

        // Run context for the CSV header. Only the fields available machine-side
        // are filled here (Bpm, BudgetMs, Build); ASIO/ratio/athreads/fillthread/ci
        // need PP2's GUI reflection and are left unknown for the validation build.
        FR.RunContext BuildFrRunContext()
        {
            var rc = new FR.RunContext
            {
                AsioSmp = -1, ChunkSmp = 0, Ratio = 0, AudioThreads = -1,
                FillThread = false, Ci = false, Build = Version
            };
            try { rc.Bpm = _host.MasterInfo.BeatsPerMin; } catch { }
            try { var s = _snapshot; if (s.IsValid) rc.BudgetMs = s.BudgetMs; } catch { }
            rc.DeadlineOverrunUs = LastDeadlineOverrunUs;
            return rc;
        }


        // ─── MachineState persistence (per IBuzzMachine.cs:28) ───────────────
        // ReBuzz serializes byte[] returned from MachineState getter into the
        // song file. On load, calls setter with the deserialized bytes BEFORE
        // creating the GUI. The GUI reads PersistedSelection in its Machine
        // setter to restore selection across song load/save.
        //
        // Format:
        //   bytes 0..3   : magic "PP2S" (Pedal Profiler2 State)
        //   byte 4       : version (2)
        //   bytes 5..6   : UInt16 length of selected-machine name in UTF-8
        //   bytes 7..    : UTF-8 bytes of name (may be empty)
        //   [v2+] byte   : FrEnabled (0/1)
        //   [v2+] UInt16 : length of FrOutputDir in UTF-8, then the UTF-8 bytes
        //
        // Back-compat: v1 blobs (name only) still load; FR settings default
        // (disabled, %TEMP%\pp2_fr). Unknown magic/version is ignored.

        const uint  STATE_MAGIC   = 0x53325050u;  // "PP2S" little-endian
        const byte  STATE_VERSION = 2;

        // What the GUI writes when selection changes. Read on Machine setter
        // to restore. UI-thread only — no audio-thread access.
        public string? PersistedSelection { get; set; }

        public byte[] MachineState
        {
            get
            {
                try
                {
                    using var ms = new MemoryStream();
                    using var bw = new BinaryWriter(ms);
                    bw.Write(STATE_MAGIC);
                    bw.Write(STATE_VERSION);
                    var nameBytes = string.IsNullOrEmpty(PersistedSelection)
                        ? Array.Empty<byte>()
                        : Encoding.UTF8.GetBytes(PersistedSelection!);
                    if (nameBytes.Length > ushort.MaxValue)
                        nameBytes = Array.Empty<byte>();  // defensive — names won't realistically be this long
                    bw.Write((ushort)nameBytes.Length);
                    bw.Write(nameBytes);

                    // v2 fields
                    bw.Write((byte)(FrEnabled ? 1 : 0));
                    var dirBytes = string.IsNullOrEmpty(FrOutputDir)
                        ? Array.Empty<byte>()
                        : Encoding.UTF8.GetBytes(FrOutputDir);
                    if (dirBytes.Length > ushort.MaxValue) dirBytes = Array.Empty<byte>();
                    bw.Write((ushort)dirBytes.Length);
                    bw.Write(dirBytes);
                    return ms.ToArray();
                }
                catch { return Array.Empty<byte>(); }
            }
            set
            {
                if (value == null || value.Length < 7) return;  // too short to be ours
                try
                {
                    using var ms = new MemoryStream(value);
                    using var br = new BinaryReader(ms);
                    uint magic = br.ReadUInt32();
                    if (magic != STATE_MAGIC) return;            // not our format
                    byte version = br.ReadByte();
                    if (version != 1 && version != 2) return;    // unknown — ignore

                    ushort nameLen = br.ReadUInt16();
                    if (nameLen > 0)
                    {
                        if (nameLen > ms.Length - ms.Position) return;   // truncated
                        PersistedSelection = Encoding.UTF8.GetString(br.ReadBytes(nameLen));
                    }
                    else PersistedSelection = null;

                    if (version >= 2 && ms.Position < ms.Length)
                    {
                        FrEnabled = br.ReadByte() != 0;
                        ushort dirLen = br.ReadUInt16();
                        if (dirLen > 0 && dirLen <= ms.Length - ms.Position)
                            FrOutputDir = Encoding.UTF8.GetString(br.ReadBytes(dirLen));
                    }
                }
                catch { /* corrupt state — silently ignore, start fresh */ }
            }
        }


        // ─── Work() — audio thread, called every buffer, BEFORE generators ──
        public void Work()
        {
            long nowTicks = Stopwatch.GetTimestamp();

            // Cache sample rate from MasterInfo (valid inside Work()).
            // Atomic int write — UI thread reads safely.
            try { _cachedSampleRate = _host.MasterInfo.SamplesPerSec; } catch { }

            if (_haveLastEnd && _lastWorkStart != 0)
            {
                long otherTicks  = nowTicks - _lastWorkEnd;     // all other machines
                long periodTicks = nowTicks - _lastWorkStart;   // full buffer period

                if (otherTicks  < 0) otherTicks  = 0;
                if (periodTicks <= 0) periodTicks = 1;

                // ── Per-chunk ring append (for dump-time percentiles, §5.1) ─
                _otherTicksRing[(int)(_otherRingTotal % OTHER_RING_SIZE)] = otherTicks;
                _otherRingTotal++;

                // ── Flight Recorder per-chunk tape (audio thread; alloc-free) ─
                if (FrEnabled)
                {
                    double frFq = Stopwatch.Frequency;
                    _fr?.OnChunk((float)(periodTicks / frFq * 1000.0),
                                 (float)(otherTicks  / frFq * 1000.0));
                }

                // ── Accumulate for window averaging ─────────────────────────
                _sumOtherTicks  += otherTicks;
                _sumPeriodTicks += periodTicks;
                _windowCount++;
                _totalBuffers++;

                // ── Paired peak: track other AND its period together ────────
                if (otherTicks > _maxOtherTicks)
                {
                    _maxOtherTicks       = otherTicks;
                    _maxOtherPeriodTicks = periodTicks;
                }

                // ── Baseline period: 128-buffer low-pass for spike detect ──
                if (_baselinePeriodTicks == 0)
                    _baselinePeriodTicks = periodTicks;
                else
                    _baselinePeriodTicks = (_baselinePeriodTicks * 127 + periodTicks) / 128;

                // ── Near-dropout: other ≥ 90% of baseline period ────────────
                if (otherTicks * 10 >= _baselinePeriodTicks * 9)
                {
                    _windowDropouts++;
                    _totalDropouts++;
                }

                // ── Spike capture: other > 150% of baseline, with cooldown ──
                long cooldownTicks = (long)(SPIKE_COOLDOWN_MS * Stopwatch.Frequency / 1000);
                bool isOverrun = otherTicks > _baselinePeriodTicks * 3 / 2 && _baselinePeriodTicks > 0;
                if (isOverrun)
                    _spikeRawTotal++;   // true count, no cooldown — for honest rate stats
                if (isOverrun &&
                    (nowTicks - _lastSpikeTicks) >= cooldownTicks)
                {
                    double freq = Stopwatch.Frequency;
                    int bpm = 0;
                    try { bpm = _host.MasterInfo.BeatsPerMin; } catch { }

                    // Atomic ref read of the UI-published active machines list.
                    // At most ~100ms stale; fine for spike attribution.
                    string[] active = _activeMachinesPublished;

                    _spikeRing[_spikeWrite] = new Spike2Record
                    {
                        SpikeMs        = otherTicks / freq * 1000.0,
                        BudgetMs       = _baselinePeriodTicks / freq * 1000.0,
                        ElapsedSec     = (nowTicks - _loadTimestamp) / freq,
                        Bpm            = bpm,
                        ActiveMachines = active
                    };
                    _spikeWrite = (_spikeWrite + 1) % SPIKE_SLOTS;
                    if (_spikeCount < SPIKE_SLOTS) _spikeCount++;
                    _lastSpikeTicks = nowTicks;
                }

                // ── End of averaging window → publish snapshot ──────────────
                int windowSize = Window;
                if (windowSize < 16) windowSize = 16;
                if (windowSize > 128) windowSize = 128;

                if (_windowCount >= windowSize)
                {
                    double freq = Stopwatch.Frequency;

                    double avgOtherMs  = (_sumOtherTicks / (double)_windowCount) / freq * 1000.0;
                    double avgPeriodMs = (_sumPeriodTicks / (double)_windowCount) / freq * 1000.0;
                    double peakOtherMs = _maxOtherTicks / freq * 1000.0;

                    double cpuPct     = avgPeriodMs > 0 ? avgOtherMs  / avgPeriodMs * 100.0 : 0.0;
                    double peakCpuPct = _maxOtherPeriodTicks > 0
                        ? (_maxOtherTicks / (double)_maxOtherPeriodTicks) * 100.0
                        : 0.0;

                    if (cpuPct     > 100.0) cpuPct     = 100.0;
                    if (peakCpuPct > 100.0) peakCpuPct = 100.0;

                    // ── Copy spike ring newest-first into snapshot ──────────
                    var spikes = new Spike2Record[_spikeCount];
                    for (int i = 0; i < _spikeCount; i++)
                    {
                        int slot = (_spikeWrite - 1 - i + SPIKE_SLOTS) % SPIKE_SLOTS;
                        spikes[i] = _spikeRing[slot];
                    }

                    // ── Publish — atomic reference write ────────────────────
                    _snapshot = new Profile2Snapshot
                    {
                        CpuPct         = cpuPct,
                        PeakCpuPct     = peakCpuPct,
                        AvgOtherMs     = avgOtherMs,
                        PeakOtherMs    = peakOtherMs,
                        PeriodMs       = avgPeriodMs,
                        BudgetMs       = avgPeriodMs,
                        SampleRate     = _cachedSampleRate,
                        IsValid        = true,
                        Spikes         = spikes,
                        WindowDropouts = _windowDropouts,
                        TotalDropouts  = _totalDropouts,
                        TotalBuffers   = _totalBuffers,
                        SpikeRawTotal  = _spikeRawTotal,
                        ElapsedSec     = (nowTicks - _loadTimestamp) / freq
                    };

                    // Reset window accumulators
                    _sumOtherTicks       = 0;
                    _sumPeriodTicks      = 0;
                    _maxOtherTicks       = 0;
                    _maxOtherPeriodTicks = 0;
                    _windowCount         = 0;
                    _windowDropouts      = 0;
                }
            }

            // Bracket points: record start now, end at the bottom.
            _lastWorkStart = nowTicks;
            _lastWorkEnd   = Stopwatch.GetTimestamp();
            _haveLastEnd   = true;
        }
    }
}
