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
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;
using System.Diagnostics;
using System.IO;
using System.Text;

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
        // ─── Host ────────────────────────────────────────────────────────────
        IBuzzMachineHost _host;
        public IBuzzMachineHost Host { get => _host; set => _host = value; }

        // ─── Parameters ──────────────────────────────────────────────────────
        // Window controls averaging responsiveness (same idea as v1).
        [ParameterDecl(Name = "Window", MinValue = 16, MaxValue = 128, DefValue = 64,
                       Description = "Buffers per averaging window")]
        public int Window { get; set; } = 64;


        // ─── Snapshot bridge (audio → UI, volatile reference swap) ───────────
        volatile Profile2Snapshot _snapshot = new Profile2Snapshot();
        public Profile2Snapshot Snapshot => _snapshot;


        // ─── Active-machines snapshot (UI → audio, for spike attribution) ────
        // Published by GUI on every UI tick (~100ms). Audio thread reads
        // atomically at spike capture time. Reference reads are atomic.
        volatile string[] _activeMachinesPublished = Array.Empty<string>();
        public void PublishActiveMachines(string[] names)
            => _activeMachinesPublished = names ?? Array.Empty<string>();


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


        // ─── Baseline period low-pass (128-buffer time constant) ─────────────
        long _baselinePeriodTicks;


        // ─── Spike ring buffer ───────────────────────────────────────────────
        const int  SPIKE_SLOTS       = 8;
        const long SPIKE_COOLDOWN_MS = 500;

        readonly Spike2Record[] _spikeRing = new Spike2Record[SPIKE_SLOTS];
        int  _spikeWrite      = 0;
        int  _spikeCount      = 0;
        long _lastSpikeTicks  = 0;


        // ─── Reference timestamp for spike "+mm:ss" displays ─────────────────
        readonly long _loadTimestamp;


        // ─── Constructor ─────────────────────────────────────────────────────
        // §16.1: do NOT touch host.Machine here — it's null. Subscribing to
        // Song events happens in the GUI's Machine setter instead.
        public Profiler2Machine(IBuzzMachineHost host)
        {
            _host = host;
            _loadTimestamp = Stopwatch.GetTimestamp();
        }


        // ─── MachineState persistence (per IBuzzMachine.cs:28) ───────────────
        // ReBuzz serializes byte[] returned from MachineState getter into the
        // song file. On load, calls setter with the deserialized bytes BEFORE
        // creating the GUI. The GUI reads PersistedSelection in its Machine
        // setter to restore selection across song load/save.
        //
        // Format:
        //   bytes 0..3   : magic "PP2S" (Pedal Profiler2 State)
        //   byte 4       : version (1)
        //   bytes 5..6   : UInt16 length of selected-machine name in UTF-8
        //   bytes 7..    : UTF-8 bytes of name (may be empty)
        //
        // Forward compatibility: unknown versions return empty state. Future
        // fields appended after the name preserve back-compat by length-tag.

        const uint  STATE_MAGIC   = 0x53325050u;  // "PP2S" little-endian
        const byte  STATE_VERSION = 1;

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
                    if (version != STATE_VERSION) return;        // future version — ignore
                    ushort nameLen = br.ReadUInt16();
                    if (nameLen == 0) { PersistedSelection = null; return; }
                    if (nameLen > value.Length - 7) return;      // truncated
                    var nameBytes = br.ReadBytes(nameLen);
                    PersistedSelection = Encoding.UTF8.GetString(nameBytes);
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
                if (otherTicks > _baselinePeriodTicks * 3 / 2 &&
                    (nowTicks - _lastSpikeTicks) >= cooldownTicks &&
                    _baselinePeriodTicks > 0)
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
                        TotalBuffers   = _totalBuffers
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
