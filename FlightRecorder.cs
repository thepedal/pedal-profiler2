#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;

namespace WDE.PedalProfiler2.FR
{
    // ─────────────────────────────────────────────────────────────────────────
    // Flight Recorder orchestrator — Phase 1.
    //
    // Recording is always on and cheap. Triggering is separate and rare:
    //   * Manual mark (GUI button / footswitch CC) — reliable, but carries an
    //     irreducible reaction-time smear, so its causal slice is a BAND offset
    //     back from the mark ([mark-REACT_HI, mark-REACT_LO]), not a point.
    //   * Auto mark (host deadline/underflow, phase 3) — latency-free, so its
    //     causal slice is a tight point [trigger-TIGHT, trigger].
    //
    // Attribution never trusts co-occurrence. It reports:
    //   * lift  = P(active | near glitch) / P(active | baseline)   — ~1 means
    //             "along for the ride" (PP2 §6.1) and is uninformative.
    //   * onset = fraction of marks where a machine transitioned inactive→active
    //             inside the band — the causal-flavoured signal.
    //   * z     = onset rate vs the machine's OWN base onset dynamics, so a
    //             busy machine isn't fingered just for being busy. Small N is the
    //             enemy (cf. the §6.4 phantom periodicity); mark many glitches.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Self-describing run context, populated by PP2 from its existing
    /// reflection/registry reads (§9.10) so the recorder needs no build coupling.</summary>
    public struct RunContext
    {
        public int    Bpm;
        public double BudgetMs;      // per-chunk budget, for the "over" annotation
        public int    AsioSmp;       // -1 if unknown
        public int    ChunkSmp;
        public double Ratio;         // driver:chunk
        public int    AudioThreads;  // -1 if unknown
        public bool   FillThread;
        public bool   Ci;            // cadence-inflated (ratio>3): dropout family is artifact
        public string Build;         // e.g. "1834"
        public long   DeadlineOverrunUs; // worst ASIO overrun so far, µs (auto-trigger severity)
    }

    public sealed class FlightRecorder
    {
        // ── tunables ──────────────────────────────────────────────────────────
        public double PreRollSec  = 10.0;
        public double PostRollSec = 3.0;
        public double ReactLoSec  = 0.15;   // manual-mark reaction band, near edge
        public double ReactHiSec  = 0.60;   // manual-mark reaction band, far edge
        public double AutoTightSec= 0.40;   // auto-trigger pre-slice (deadline miss); ~20 samples at 50Hz
        public double OverFactor  = 1.5;    // ElapsedMs > OverFactor*budget → "over" annotation

        private readonly double _qpcHz = Stopwatch.Frequency;

        private readonly Ring<ChunkRec> _chunkRing = new Ring<ChunkRec>(1 << 16); // ~256s @250/s
        private readonly Ring<EvtRec>   _evtRing   = new Ring<EvtRec>(1 << 14);
        private readonly Ring<CostRec>  _costRing  = new Ring<CostRec>(1 << 12);
        private readonly IndexMapKeeper _map       = new IndexMapKeeper();

        // deliver(filename, content) — PP2 routes through its §9 file+clipboard path.
        private readonly Action<string, string> _deliver;
        private Func<RunContext> _runContext;

        // pending trigger, published by the trigger source, consumed by the UI tick
        private long _pendingMarkQpc;        // 0 = none
        private long _captureUntilQpc;
        private int  _pendingIsAuto;         // 0/1

        // ── cumulative baseline + association (UI-thread only) ──────────────────
        private long   _baseTotalSamples;
        private readonly long[] _baseActiveByBit = new long[FrConst.MAX_BITS];

        private int  _markCount;
        private long _bandSamplesSeen;       // for mean band length display
        private readonly double[] _bandFracSum   = new double[FrConst.MAX_BITS]; // Σ per-mark band-active fraction
        private readonly double[] _bandFracSumSq = new double[FrConst.MAX_BITS]; // Σ (per-mark fraction)²
        private readonly int[]    _onsetByBit    = new int[FrConst.MAX_BITS];    // #marks with in-band 0→1 (descriptive)

        // ── Stage-1 host-overhead analysis (per-mark, cumulative) ───────────────
        public double HostWindowSec = 5.0;   // pre-miss window for the ramp slope
        private int  _lastG2Count = -1;      // GC Gen2 proximity tracking
        private long _lastG2Qpc;
        private int  _hostMarks;
        private readonly System.Collections.Generic.List<double> _rampMs  = new();  // median 2nd-half − 1st-half OtherMs
        private readonly System.Collections.Generic.List<double> _peakMs  = new();  // worst OtherMs gap in window
        private readonly System.Collections.Generic.List<double> _medMs   = new();  // median OtherMs in window
        private readonly System.Collections.Generic.List<double> _gcAgeMs = new();  // ms since last Gen2 at mark (NaN if none)
        private long _worstOverrunUs;        // running max of DeadlineWorstOverrunMicros seen at marks

        public FlightRecorder(Action<string, string> deliver, Func<RunContext> runContext)
        {
            _deliver = deliver ?? ((_, __) => { });
            _runContext = runContext ?? (() => default);
        }

        public IndexMapKeeper Map => _map;

        // ── write hooks ─────────────────────────────────────────────────────────

        /// <summary>AUDIO THREAD. Call from Work() next to the existing OtherMs write.</summary>
        public void OnChunk(float elapsedMs, float otherMs)
            => _chunkRing.Write(new ChunkRec { Qpc = Stopwatch.GetTimestamp(), ElapsedMs = elapsedMs, OtherMs = otherMs });

        /// <summary>AUDIO THREAD. Call from MidiControlChange (PeerCtrl §10 shows
        /// this fires for every incoming CC).</summary>
        public void OnMidiCC(int ctrl, int channel, int value)
            => _evtRing.Write(new EvtRec
            {
                Qpc = Stopwatch.GetTimestamp(), Kind = EvtKind.CC,
                Chan = (byte)channel, Ctrl = (ushort)ctrl, Value = value, SrcId = FrConst.SRC_MIDI
            });

        /// <summary>UI THREAD. Call once per UI tick. `activeNames` is PP2's
        /// published active-machine set — which in this build means the NOT-MUTED
        /// machines. Onset therefore fires on mute→unmute transitions; a silent
        /// but unmuted machine reads as active. Plan the planted cause around
        /// mute toggling accordingly.</summary>
        public void OnCostSample(IEnumerable<string> activeNames)
        {
            _map.Ensure(activeNames);   // rebuilds only when a new name appears
            ulong mask = _map.BuildActiveMask(activeNames);
            _costRing.Write(new CostRec { Qpc = Stopwatch.GetTimestamp(), ActiveMask = mask });

            // maintain baseline incrementally (no ring re-scan at freeze)
            _baseTotalSamples++;
            ulong m = mask;
            while (m != 0) { int b = BitOperations.TrailingZeroCount(m); _baseActiveByBit[b]++; m &= m - 1; }

            // GC Gen2 proximity: stamp the QPC of each Gen2 collection (BCL-only,
            // process-wide — a G2 pause stalls the whole engine).
            int g2 = GC.CollectionCount(2);
            if (_lastG2Count < 0) _lastG2Count = g2;
            else if (g2 > _lastG2Count) { _lastG2Count = g2; _lastG2Qpc = Stopwatch.GetTimestamp(); }
        }

        /// <summary>UI THREAD. Optional — OnCostSample already auto-ensures new
        /// names. Call explicitly on an add/remove if you want the graph-change
        /// marker stamped promptly.</summary>
        public void OnGraphChanged(IEnumerable<string> names) => _map.Ensure(names);

        // ── trigger ───────────────────────────────────────────────────────────

        /// <summary>Any thread. Manual mark (button/footswitch) or auto (host).</summary>
        public void RequestMark(bool isAuto)
        {
            long qpc = Stopwatch.GetTimestamp();
            long post = (long)(PostRollSec * _qpcHz);
            Interlocked.Exchange(ref _captureUntilQpc, qpc + post);
            Interlocked.Exchange(ref _pendingIsAuto, isAuto ? 1 : 0);
            Interlocked.Exchange(ref _pendingMarkQpc, qpc);
        }

        /// <summary>UI THREAD. Call once per UI tick AFTER OnCostSample. Performs
        /// the freeze once the post-roll tail has accrued.</summary>
        public void OnUiTick()
        {
            long mark = Interlocked.Read(ref _pendingMarkQpc);
            if (mark == 0) return;
            if (Stopwatch.GetTimestamp() < Interlocked.Read(ref _captureUntilQpc)) return; // wait for tail

            bool isAuto = Volatile.Read(ref _pendingIsAuto) != 0;
            try { Freeze(mark, isAuto); }
            finally { Interlocked.Exchange(ref _pendingMarkQpc, 0); }
        }

        // ── freeze + analysis ───────────────────────────────────────────────────

        private void Freeze(long markQpc, bool isAuto)
        {
            long pre  = (long)(PreRollSec  * _qpcHz);
            long post = (long)(PostRollSec * _qpcHz);
            long lo = markQpc - pre, hi = markQpc + post;

            var chunks = Filter(_chunkRing.SnapshotAll(out _), r => r.Qpc, lo, hi).OrderBy(r => r.Qpc).ToArray();
            var evts   = Filter(_evtRing  .SnapshotAll(out _), r => r.Qpc, lo, hi).OrderBy(r => r.Qpc).ToArray();
            var costs  = Filter(_costRing .SnapshotAll(out _), r => r.Qpc, lo, hi).OrderBy(r => r.Qpc).ToArray();
            var imap   = _map.Current;
            bool graphChanged = _map.GraphChangedInWindow(lo, hi);

            RunContext rc = default;
            try { rc = _runContext(); } catch { }

            EmitTimelineCsv(markQpc, isAuto, chunks, evts, costs, imap, graphChanged, rc);

            AccumulateAssoc(markQpc, isAuto, costs);
            string summary = RenderAssoc(imap, rc, isAuto);
            _deliver($"pp2_fr_assoc.txt", summary);   // overwrite: cumulative snapshot

            AccumulateHost(markQpc, chunks, rc);
            _deliver($"pp2_fr_host.txt", RenderHost(rc));   // overwrite: cumulative host analysis
        }

        private static IEnumerable<T> Filter<T>(T[] arr, Func<T, long> qpc, long lo, long hi)
        {
            foreach (var r in arr) { long q = qpc(r); if (q >= lo && q <= hi) yield return r; }
        }

        private void EmitTimelineCsv(long markQpc, bool isAuto, ChunkRec[] chunks, EvtRec[] evts,
                                     CostRec[] costs, IndexMap imap, bool graphChanged, RunContext rc)
        {
            double ToMs(long q) => (q - markQpc) * 1000.0 / _qpcHz;
            var sb = new StringBuilder(1 << 16);

            sb.Append("# PP2-FR v1 | mark=").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
              .Append(" trigger=").Append(isAuto ? "auto" : "manual")
              .Append(" | qpc_hz=").Append((long)_qpcHz)
              .Append(" | pre=").Append(PreRollSec).Append(" post=").Append(PostRollSec)
              .Append(" react=").Append(ReactLoSec).Append("..").Append(ReactHiSec)
              .Append(" | bpm=").Append(rc.Bpm)
              .Append(" budget_ms=").Append(rc.BudgetMs.ToString("0.000"))
              .Append(" | asio=").Append(rc.AsioSmp).Append(" chunk_smp=").Append(rc.ChunkSmp)
              .Append(" ratio=").Append(rc.Ratio.ToString("0.0"))
              .Append(" | athreads=").Append(rc.AudioThreads)
              .Append(" fillthread=").Append(rc.FillThread ? 1 : 0)
              .Append(" ci=").Append(rc.Ci ? 1 : 0)
              .Append(" | build=").Append(rc.Build ?? "?")
              .Append(" | deadline_worst_overrun_us=").Append(rc.DeadlineOverrunUs)
              .Append(" | graph_changed_in_window=").Append(graphChanged ? 1 : 0)
              .Append(" | map_exhausted=").Append(imap.Exhausted ? 1 : 0)
              .AppendLine();

            for (int b = 0; b < imap.Names.Length; b++)
                if (imap.Names[b] != null)
                    sb.Append("# bit ").Append(b).Append(" = ").AppendLine(imap.Names[b]);

            var rows = new List<(double t, string line)>(chunks.Length + evts.Length + costs.Length);
            double overThresh = rc.BudgetMs * OverFactor;
            foreach (var r in chunks)
            {
                int over = (rc.BudgetMs > 0 && r.ElapsedMs > overThresh) ? 1 : 0;
                rows.Add((ToMs(r.Qpc), $"chunk,{r.ElapsedMs:0.000},{r.OtherMs:0.000},{over},,,,"));
            }
            foreach (var e in evts)
            {
                string src = e.SrcId == FrConst.SRC_MIDI ? "MIDI" : ("m" + e.SrcId);
                string kind = e.Kind == EvtKind.CC ? "cc"
                            : e.Kind == EvtKind.NOTE ? "note"
                            : e.Kind == EvtKind.PARAM ? "param"
                            : e.Kind == EvtKind.PRESET ? "preset" : "evt";
                rows.Add((ToMs(e.Qpc), $"{kind},,,,{src},{e.Chan},{e.Ctrl},{e.Value}"));
            }
            foreach (var c in costs)
                rows.Add((ToMs(c.Qpc), $"cost,,,,,,,{c.ActiveMask:x16}"));

            rows.Sort((a, b2) => a.t.CompareTo(b2.t));

            sb.AppendLine("t_ms,kind,elapsed_ms,other_ms,over,src,chan,ctrl,value_or_active_hex");
            foreach (var (t, line) in rows)
                sb.Append(t.ToString("0.000")).Append(',').AppendLine(line);

            _deliver($"pp2_fr_{DateTime.Now:HHmmss}.csv", sb.ToString());
        }

        private void AccumulateAssoc(long markQpc, bool isAuto, CostRec[] costs)
        {
            long lo, hi;
            if (isAuto) { lo = markQpc - (long)(AutoTightSec * _qpcHz); hi = markQpc; }
            else        { lo = markQpc - (long)(ReactHiSec   * _qpcHz); hi = markQpc - (long)(ReactLoSec * _qpcHz); }

            var band = costs.Where(c => c.Qpc >= lo && c.Qpc <= hi).OrderBy(c => c.Qpc).ToArray();
            if (band.Length == 0) return;   // band fell into a cost-sample gap; don't count this mark

            _markCount++;
            _bandSamplesSeen += band.Length;

            // Per-sample active count for each bit within THIS mark's band, plus
            // in-band 0→1 (onset) presence. The per-mark active FRACTION is the
            // unit of observation for the t-test (each mark = 1 independent point),
            // which fixes both the units mismatch (band fraction vs base rate, now
            // apples-to-apples) and the burstiness problem (no per-sample z).
            var activeCount = new int[FrConst.MAX_BITS];
            ulong everOnset = 0UL;
            ulong prev = band[0].ActiveMask;
            for (int i = 0; i < band.Length; i++)
            {
                ulong cur = band[i].ActiveMask;
                ulong m = cur;
                while (m != 0) { int b = BitOperations.TrailingZeroCount(m); activeCount[b]++; m &= m - 1; }
                if (i > 0) everOnset |= (~prev & cur);
                prev = cur;
            }

            double inv = 1.0 / band.Length;
            for (int b = 0; b < FrConst.MAX_BITS; b++)
            {
                double frac = activeCount[b] * inv;      // this mark's per-sample band activity
                _bandFracSum[b]   += frac;
                _bandFracSumSq[b] += frac * frac;
                if ((everOnset & (1UL << b)) != 0) _onsetByBit[b]++;
            }
        }

        private string RenderAssoc(IndexMap imap, RunContext rc, bool lastWasAuto)
        {
            var sb = new StringBuilder(1 << 12);
            double meanL = _markCount > 0 ? (double)_bandSamplesSeen / _markCount : 0;

            sb.Append("── FLIGHT RECORDER ASSOC ─── (")
              .Append(_markCount).Append(" marks, ")
              .Append(lastWasAuto ? "auto trigger" : $"manual band {ReactLoSec*1000:0}–{ReactHiSec*1000:0} ms pre-mark")
              .Append(", mean ").Append(meanL.ToString("0.0")).Append(" cost-samples/band)")
              .AppendLine();

            if (_markCount == 0) { sb.AppendLine("  no marks with cost samples in band yet."); return sb.ToString(); }

            var rowsList = new List<(int bit, double baseRate, double bandRate, double lift, double onset, double t)>();
            for (int b = 0; b < FrConst.MAX_BITS; b++)
            {
                if (imap.Names[b] == null) continue;
                if (_baseTotalSamples == 0) continue;

                double baseRate = (double)_baseActiveByBit[b] / _baseTotalSamples;   // per-sample
                double mean     = _bandFracSum[b] / _markCount;                      // per-sample, mean over marks
                double lift     = baseRate > 1e-9 ? mean / baseRate : double.NaN;
                double onset    = (double)_onsetByBit[b] / _markCount;               // descriptive

                // t of per-mark band fraction vs baseline; each mark one observation.
                double t;
                if (_markCount >= 2)
                {
                    double var = (_bandFracSumSq[b] - _markCount * mean * mean) / (_markCount - 1);
                    double std = var > 0 ? Math.Sqrt(var) : 0.0;
                    t = std > 1e-9 ? (mean - baseRate) / (std / Math.Sqrt(_markCount)) : double.NaN;
                }
                else t = double.NaN;

                rowsList.Add((b, baseRate, mean, lift, onset, t));
            }

            // Lead with the robust signal: t of band-activity lift, then lift.
            var ordered = rowsList
                .OrderByDescending(r => double.IsNaN(r.t) ? double.NegativeInfinity : r.t)
                .ThenByDescending(r => double.IsNaN(r.lift) ? double.NegativeInfinity : r.lift);

            sb.AppendLine("  machine                 base%   band%   lift   onset   t     note");
            foreach (var r in ordered)
            {
                string name = imap.Names[r.bit];
                if (name.Length > 22) name = name.Substring(0, 22);
                string note =
                    (!double.IsNaN(r.t) && r.t >= 2.0 && !double.IsNaN(r.lift) && r.lift >= 1.3) ? "← elevated during misses" :
                    (r.baseRate >= 0.90) ? "← always on; uninformative" :
                    (!double.IsNaN(r.lift) && r.lift < 1.1) ? "≈ baseline" : "";

                sb.Append("  ")
                  .Append(name.PadRight(22)).Append(' ')
                  .Append((r.baseRate * 100).ToString("0.0").PadLeft(5)).Append("  ")
                  .Append((r.bandRate * 100).ToString("0.0").PadLeft(5)).Append("  ")
                  .Append((double.IsNaN(r.lift) ? "  na" : r.lift.ToString("0.0")).PadLeft(5)).Append("  ")
                  .Append(r.onset.ToString("0.00").PadLeft(5)).Append("  ")
                  .Append((double.IsNaN(r.t) ? " na" : r.t.ToString("0.0")).PadLeft(4)).Append("  ")
                  .AppendLine(note);
            }

            sb.AppendLine();
            sb.AppendLine("  base% / band% are both per-sample active fractions (apples-to-apples); lift = band/base.");
            sb.AppendLine("  t = per-mark band activity vs baseline, each mark one observation (robust to bursty");
            sb.AppendLine("  activity — no per-sample independence assumed). onset = fraction of marks the machine");
            sb.AppendLine("  switched inactive→active in-band (descriptive only).");
            sb.AppendLine("  |t| ≳ 2 with lift ≳ 1.3 is a real lean; collect 25+ marks before trusting it (cf. §6.4).");
            if (rc.Ci)
                sb.AppendLine("  ci=1: dropout/over annotations are cadence artifacts (ratio>3, §15.2).");
            if (imap.Exhausted)
                sb.AppendLine("  WARN: bit map exhausted (>64 machines this session) — some machines untracked.");
            return sb.ToString();
        }

        // ── Stage-1 host-overhead analysis ──────────────────────────────────────
        // Asks whether the misses have a TEMPORAL SIGNATURE, using only the
        // per-chunk tape we already capture + a BCL GC poll. No host cooperation.
        // The point is the gate: a signature justifies a ReBuzz-side feed; a flat,
        // GC-independent floor says stop instrumenting and do the static perf fixes.

        private void AccumulateHost(long markQpc, ChunkRec[] chunks, RunContext rc)
        {
            long lo  = markQpc - (long)(HostWindowSec * _qpcHz);
            long mid = markQpc - (long)(HostWindowSec * 0.5 * _qpcHz);
            var win = chunks.Where(c => c.Qpc >= lo && c.Qpc <= markQpc).OrderBy(c => c.Qpc).ToArray();
            if (win.Length < 8) return;   // too few chunks in the window to say anything

            var firstH  = win.Where(c => c.Qpc <  mid).Select(c => (double)c.OtherMs).ToArray();
            var secondH = win.Where(c => c.Qpc >= mid).Select(c => (double)c.OtherMs).ToArray();
            if (firstH.Length < 2 || secondH.Length < 2) return;

            double ramp = Median(secondH) - Median(firstH);                 // >0 = rising toward the miss
            double peak = win.Max(c => (double)c.OtherMs);
            double med  = Median(win.Select(c => (double)c.OtherMs).ToArray());
            double gcAge = _lastG2Qpc > 0 ? (markQpc - _lastG2Qpc) * 1000.0 / _qpcHz : double.NaN;

            _hostMarks++;
            _rampMs.Add(ramp); _peakMs.Add(peak); _medMs.Add(med); _gcAgeMs.Add(gcAge);
            if (rc.DeadlineOverrunUs > _worstOverrunUs) _worstOverrunUs = rc.DeadlineOverrunUs;
        }

        private string RenderHost(RunContext rc)
        {
            var sb = new StringBuilder(1 << 11);
            sb.Append("── FLIGHT RECORDER HOST ─── (").Append(_hostMarks)
              .Append(" marks, ").Append(HostWindowSec.ToString("0")).Append("s pre-miss window)").AppendLine();
            if (_hostMarks == 0) { sb.AppendLine("  no host windows yet (need ≥8 chunks in-window)."); return sb.ToString(); }

            const double RAMP_EPS = 0.30;   // ms — below this the 2-half median delta is flat
            int rising = 0, flat = 0, falling = 0;
            foreach (var r in _rampMs) { if (r > RAMP_EPS) rising++; else if (r < -RAMP_EPS) falling++; else flat++; }
            double medRamp = Median(_rampMs.ToArray());

            int spikey = 0;
            for (int i = 0; i < _hostMarks; i++)
                if (_medMs[i] > 1e-6 && _peakMs[i] > 5.0 * _medMs[i]) spikey++;

            int gcKnown = 0, g50 = 0, g200 = 0;
            var gcVals = new System.Collections.Generic.List<double>();
            foreach (var a in _gcAgeMs)
                if (!double.IsNaN(a)) { gcKnown++; gcVals.Add(a); if (a <= 50) g50++; if (a <= 200) g200++; }

            sb.Append("  ramp (median OtherMs, 2nd half − 1st half):  median ")
              .Append(medRamp.ToString("+0.00;-0.00;0.00")).Append(" ms   [")
              .Append(rising).Append(" rising / ").Append(flat).Append(" flat / ").Append(falling).Append(" falling]").AppendLine();
            sb.Append("  shape:  ").Append(spikey).Append("/").Append(_hostMarks)
              .Append(" spike-dominated (peak > 5×median)").AppendLine();
            if (gcKnown > 0)
                sb.Append("  GC Gen2:  ").Append(g50).Append("/").Append(gcKnown).Append(" misses within 50ms, ")
                  .Append(g200).Append("/").Append(gcKnown).Append(" within 200ms;  median age ")
                  .Append(Median(gcVals.ToArray()).ToString("0")).Append(" ms").AppendLine();
            else
                sb.AppendLine("  GC Gen2:  none observed during capture (G2 not implicated)");
            sb.Append("  worst ASIO overrun (session):  ").Append(_worstOverrunUs).Append(" µs").AppendLine();
            sb.Append("  peak pre-miss chunk gap:  median ").Append(Median(_peakMs.ToArray()).ToString("0.0"))
              .Append(" ms, max ").Append((_peakMs.Count > 0 ? _peakMs.Max() : 0).ToString("0.0")).Append(" ms").AppendLine();

            bool rampSig  = rising * 2 >= _hostMarks && medRamp > RAMP_EPS;
            bool gcSig    = gcKnown > 0 && g50 * 2 >= gcKnown;   // TIGHT: miss during a pause, not just near one
                                                                  // (200ms is base-rate-confounded when G2s are frequent)
            bool spikeSig = spikey * 2 >= _hostMarks;

            sb.AppendLine();
            sb.Append("  VERDICT: ");
            if (gcSig)         sb.AppendLine("misses cluster near Gen2 collections — allocation-rate signature");
            else if (rampSig)  sb.AppendLine("host cost ramps before misses — drain / sustained-overhead signature");
            else if (spikeSig) sb.AppendLine("isolated spikes, GC-independent — acute stalls (lock / IPC / DPC)");
            else               sb.AppendLine("no temporal signature — flat host-overhead floor");
            if (gcSig)         sb.AppendLine("           → target DSP-path allocations (facade allocs, SongTime news — perf-sweep A1/A2).");
            else if (rampSig)  sb.AppendLine("           → per-chunk overhead accumulates; profile the fill-thread/dispatch steady state.");
            else if (spikeSig) sb.AppendLine("           → hunt the stall; a host feed exposing barrier/lock state would localise it.");
            else               sb.AppendLine("           → runtime correlation won't help here; do the static perf fixes and re-measure.");
            sb.AppendLine("  (Stage-1 gate: a signature justifies a ReBuzz-side feed; flatness says instrument no further.)");
            if (rc.FillThread)
                sb.AppendLine("  NOTE: FillThread=ON — OtherMs is the fill-thread schedule (bimodal); ramp uses medians");
            if (rc.FillThread)
                sb.AppendLine("        to stay robust, and peak gaps include fill-thread sleeps, not just overruns (§14.2).");
            return sb.ToString();
        }

        private static double Median(double[] xs)
        {
            if (xs.Length == 0) return 0.0;
            var a = (double[])xs.Clone();
            Array.Sort(a);
            int n = a.Length;
            return (n % 2 == 1) ? a[n / 2] : 0.5 * (a[n / 2 - 1] + a[n / 2]);
        }
    }
}
