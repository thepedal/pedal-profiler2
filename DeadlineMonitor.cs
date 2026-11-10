// Pedal Profiler2 — DeadlineMonitor (v2.2.2)
//
// One shared sampler for ReBuzz's deadline-referenced dropout counter:
//
//   ReBuzzCore.DeadlineMissCount            (instance prop over a static long)
//   ReBuzzCore.DeadlineWorstOverrunMicros   (high-water mark since start / reset)
//   ReBuzzCore.DriverResetCount             (device catastrophic-reset tally — NOT a dropout counter)
//   ReBuzzCore.ResetAudioDropoutCounters()  (zeroes all three)
//
// The counter is incremented by CommonAudioProvider.Read when a driver callback's
// wall time exceeds its OWN block period (frames / sampleRate), after a 1 s warm-up.
// Under ASIO the live provider (AudioWaveProvider) wraps CommonAudioProvider, so the
// counter is live on the ASIO path (PP2 MT findings 2026-11-10 §3; PedalBench
// LiveDropout 2026-07-16). It is not cadence-inflated: the reference is the real
// callback deadline, not PP2's internal chunk budget.
//
// v2.2.1 (after the first 050 dump):
//   • Episodes. A stall makes several consecutive callbacks overrun (the catch-up
//     reads also miss), so misses arrive in bursts 30–60 ms apart. Misses closer
//     than EpisodeGapSec are grouped into one episode; the hypothesis tests run on
//     episode onsets only (bursts are not independent samples).
//   • GC lookback. The 2.2.0 test looked only at the ~20 ms poll window, but the
//     stall that caused a miss happened up to one driver block (~67 ms) plus the
//     poll lag BEFORE detection. Each miss now records ms since the last observed
//     G1 / G2 and the GC pause time (GC.GetTotalPauseDuration delta) in the
//     preceding LookbackMs. The same metrics are sampled at random times (control).
//   • Pattern phase excludes the Master sequence (short tempo/automation patterns
//     made every miss look "near a pattern start" and the control 25%).
//
// Threading: everything here runs on the WPF dispatcher thread (polled from the GUI's
// ~50 Hz timer). Getters are bound as Func<long> delegates once per IBuzz instance.
// Depends only on System.* (song context supplied by the caller) so it is unit-testable.

using System;
using System.Diagnostics;
using System.Reflection;

namespace WDE.PedalProfiler2
{
    /// <summary>Song/transport context captured by the GUI at the moment a miss is detected.</summary>
    public struct MissSongContext
    {
        public bool   Valid;
        public bool   Playing;
        public int    SongPos;         // ISong.PlayPosition (ticks)
        public int    LoopStart, LoopEnd;
        public int    PatPos;          // min over non-Master sequences of PlayingPatternPosition; -1 = none playing
        public int    PatLen;          // length of the pattern that gave PatPos
        public string PatMachine;      // machine whose sequence gave PatPos
        public int    SeqsPlaying;     // non-Master sequences with a playing pattern
        public int    SeqsNearStart;   // of those, within NearStartTicks of their pattern start
        public int    NearStartTicks;  // tolerance used
        public int    Bpm, Tpb;
    }

    /// <summary>GC state relative to a moment: recency of collections and pause time just before it.</summary>
    public struct GcLook
    {
        public double MsSinceG1;   // -1 = none observed since monitoring began
        public double MsSinceG2;
        public double PauseMs;     // GC pause time in the preceding LookbackMs (NaN if unsupported)
        public double LookbackMs;  // actual lookback covered (may be shorter right after start)
    }

    public sealed class MissEvent
    {
        public double   TSec;            // seconds since the monitor's current baseline
        public DateTime Wall;
        public long     HostCount;       // host DeadlineMissCount after this event
        public int      Batch;           // misses detected in this one poll (usually 1)
        public long     WorstUs;         // host high-water mark at detection
        public bool     WorstRaised;     // this event raised the high-water mark (≈ its own overrun)
        public double   PollGapMs;       // width of the detection window
        public int      Episode;         // 1-based episode number within the window
        public bool     Onset;           // first miss of its episode
        public GcLook   Gc;
        public MissSongContext Song;
    }

    public sealed class DeadlineMonitor
    {
        public const int    RingSize      = 64;
        public const double EpisodeGapSec = 0.5;    // misses closer than this belong to one episode
        public const double LookbackMs    = 250.0;  // GC pause lookback before a miss / control sample
        public const double NearG2Ms      = 500.0;  // "G2 shortly before" threshold for the test
        public const double PauseCtlQuantile = 0.90; // v2.2.2: onset "hit" = pause above the control's 90th percentile

        // ── Resolution / binding ───────────────────────────────────────────
        object?     _boundTarget;
        Func<long>? _getMiss, _getWorst, _getResets;
        Action?     _reset;
        public bool Available      { get; private set; }
        public bool ResetAvailable { get; private set; }
        public bool DriverResetsAvailable { get; private set; }

        // ── Live values ────────────────────────────────────────────────────
        public long HostCount    { get; private set; } = -1;
        public long WorstUs      { get; private set; }
        public long DriverResets { get; private set; } = -1;

        // ── Baseline (PP2 window) ──────────────────────────────────────────
        bool   _seeded;
        long   _baseCount;
        long   _baseTicks;
        public long   BaselineHostCount => _baseCount;
        public long   WindowMisses => (_seeded && HostCount >= 0) ? HostCount - _baseCount : 0;
        public double WindowSec    => _seeded ? (Stopwatch.GetTimestamp() - _baseTicks) / (double)Stopwatch.Frequency : 0;
        public double WindowRate   { get { double w = WindowSec; return w > 0.5 ? WindowMisses / w : 0; } }
        public int    Episodes     { get; private set; }
        public double EpisodeRate  { get { double w = WindowSec; return w > 0.5 ? Episodes / w : 0; } }
        public DateTime BaselineWall { get; private set; }
        public string BaselineReason { get; private set; } = "first-read";
        public bool   BaselineAfterHostReset { get; private set; }
        public int    ExternalHostResets { get; private set; }

        // ── GC bookkeeping (independent of baseline) ───────────────────────
        int  _g1 = -1, _g2 = -1;
        long _lastG1Ticks, _lastG2Ticks;             // 0 = not observed
        long _lastPollTicks;
        static readonly bool PauseSupported = ProbePause();
        // Ring of (ticks, cumulative GC pause ticks) for the pause lookback.
        const int PauseRing = 64;                   // ≥ 1.2 s at 50 Hz
        readonly long[] _prTicks = new long[PauseRing];
        readonly long[] _prPause = new long[PauseRing];
        int _prHead, _prCount;

        // ── Event ring ─────────────────────────────────────────────────────
        readonly MissEvent[] _ring = new MissEvent[RingSize];
        int    _ringHead, _ringCount;
        double _lastMissTSec = double.NegativeInfinity;
        public long LoggedEvents { get; private set; }

        public string? LastResetResult { get; private set; }

        // ── Control samples (random times while playing, fed ~2 Hz by GUI) ──
        public long CtlPhaseN { get; private set; }
        public long CtlPhaseNear { get; private set; }
        public long CtlGcN { get; private set; }
        public long CtlNearG2 { get; private set; }
        public double CtlPauseSumMs { get; private set; }
        // v2.2.2: keep control pause values so the GC-pause test can be relative
        // (050: ~82 ms of every 250 ms was GC pause at random, so a fixed 10 ms
        // threshold hit 100% of the control and the test could never fire).
        readonly System.Collections.Generic.List<double> _ctlPause = new();
        const int CtlPauseCap = 8192;
        public double CtlPauseQuantile(double q)
        {
            if (_ctlPause.Count == 0) return double.NaN;
            var a = _ctlPause.ToArray(); Array.Sort(a);
            int i = Math.Clamp((int)Math.Ceiling(q * a.Length) - 1, 0, a.Length - 1);
            return a[i];
        }
        public long CtlPauseAbove(double thr) { long n = 0; foreach (var v in _ctlPause) if (v > thr) n++; return n; }
        public int  CtlPauseN => _ctlPause.Count;

        // ── v2.2.2: GC totals over the miss window (since baseline) ─────────
        int    _g2AtBase;
        long   _pauseAtBaseTicks = -1;
        long   _allocAtBase;
        public int    WindowG2 => Math.Max(0, GC.CollectionCount(2) - _g2AtBase);
        public double WindowPausePct
        {
            get
            {
                if (!PauseSupported || _pauseAtBaseTicks < 0) return double.NaN;
                double w = WindowSec; if (w < 1) return double.NaN;
                try { return (GC.GetTotalPauseDuration().Ticks - _pauseAtBaseTicks) / (double)TimeSpan.TicksPerSecond / w * 100.0; }
                catch { return double.NaN; }
            }
        }
        public double WindowAllocMBps
        {
            get
            {
                double w = WindowSec; if (w < 1) return double.NaN;
                try { return (GC.GetTotalAllocatedBytes(false) - _allocAtBase) / 1048576.0 / w; } catch { return double.NaN; }
            }
        }

        /// <summary>Random-time control sample for the pattern-phase and GC tests.</summary>
        public void AddControlSample(in MissSongContext c)
        {
            if (!c.Valid || !c.Playing) return;
            if (c.PatPos >= 0)
            {
                CtlPhaseN++;
                if (c.PatPos <= c.NearStartTicks) CtlPhaseNear++;
            }
            var g = LookGc(Stopwatch.GetTimestamp());
            if (g.LookbackMs >= LookbackMs * 0.8)
            {
                CtlGcN++;
                if (g.MsSinceG2 >= 0 && g.MsSinceG2 <= NearG2Ms) CtlNearG2++;
                if (!double.IsNaN(g.PauseMs))
                {
                    CtlPauseSumMs += g.PauseMs;
                    if (_ctlPause.Count < CtlPauseCap) _ctlPause.Add(g.PauseMs);
                }
            }
        }

        // ───────────────────────────────────────────────────────────────────
        // Poll. Returns the number of NEW misses since the previous poll.
        // ───────────────────────────────────────────────────────────────────
        public int Sample(object? buzz, Func<MissSongContext>? songContext)
        {
            long now = Stopwatch.GetTimestamp();
            PollGc(now);
            if (buzz == null) return 0;
            if (!ReferenceEquals(buzz, _boundTarget)) Bind(buzz);

            double gapMs = _lastPollTicks > 0 ? (now - _lastPollTicks) * 1000.0 / Stopwatch.Frequency : 0;
            _lastPollTicks = now;

            if (_getResets != null) { try { DriverResets = _getResets(); } catch { } }
            if (_getMiss == null) return 0;

            long miss, worst = WorstUs;
            try { miss = _getMiss(); } catch { return 0; }
            if (_getWorst != null) { try { worst = _getWorst(); } catch { } }

            if (!_seeded)
            {
                HostCount = miss; WorstUs = worst;
                Rebase(miss, now, BaselineReason);
                return 0;
            }
            if (miss < HostCount)
            {
                ExternalHostResets++;
                HostCount = miss; WorstUs = worst;
                Rebase(miss, now, "host-reset (external)");
                BaselineAfterHostReset = true;
                return 0;
            }

            int fresh = (int)Math.Min(int.MaxValue, miss - HostCount);
            bool raised = worst > WorstUs;
            HostCount = miss; WorstUs = worst;
            if (fresh <= 0) return 0;

            MissSongContext ctx = default;
            if (songContext != null) { try { ctx = songContext(); } catch { ctx = default; } }

            double t = (now - _baseTicks) / (double)Stopwatch.Frequency;
            bool onset = t - _lastMissTSec > EpisodeGapSec;
            if (onset) Episodes++;
            _lastMissTSec = t;

            var ev = new MissEvent
            {
                TSec = t, Wall = DateTime.Now,
                HostCount = miss, Batch = fresh,
                WorstUs = worst, WorstRaised = raised,
                PollGapMs = gapMs,
                Episode = Episodes, Onset = onset,
                Gc = LookGc(now),
                Song = ctx
            };
            _ring[_ringHead] = ev;
            _ringHead = (_ringHead + 1) % RingSize;
            if (_ringCount < RingSize) _ringCount++;
            LoggedEvents++;
            return fresh;
        }

        /// <summary>Reset the counters: host-side if the build supports it, else local rebaseline.</summary>
        public void Reset(object? buzz)
        {
            if (buzz != null && !ReferenceEquals(buzz, _boundTarget)) Bind(buzz);
            bool hostDone = false;
            string? failure = null;
            if (_reset != null)
            {
                try { _reset(); hostDone = true; }
                catch (Exception ex) { failure = $"host reset FAILED ({ex.GetType().Name}) — local rebaseline {DateTime.Now:HH:mm:ss}"; }
            }
            long now = Stopwatch.GetTimestamp();
            if (hostDone)
            {
                HostCount = 0; WorstUs = 0; DriverResets = DriverResetsAvailable ? 0 : -1;
                Rebase(0, now, "reset (host)");
                BaselineAfterHostReset = true;
                LastResetResult = $"host counters reset {DateTime.Now:HH:mm:ss}";
            }
            else
            {
                Rebase(HostCount < 0 ? 0 : HostCount, now, "reset (local)");
                _seeded = HostCount >= 0;
                LastResetResult = failure ?? (Available
                    ? $"local rebaseline {DateTime.Now:HH:mm:ss} (build has no ResetAudioDropoutCounters; worst overrun stays since-start)"
                    : "nothing to reset — build has no DeadlineMissCount");
            }
        }

        /// <summary>Newest-first copy of the logged events (up to RingSize).</summary>
        public MissEvent[] RecentEvents()
        {
            var a = new MissEvent[_ringCount];
            for (int i = 0; i < _ringCount; i++)
                a[i] = _ring[(_ringHead - 1 - i + RingSize * 2) % RingSize];
            return a;
        }

        /// <summary>Misses (sum of Batch) logged in the last <paramref name="sec"/> seconds; saturated = ring may have dropped older ones.</summary>
        public (long count, bool saturated) MissesInLast(double sec)
        {
            double nowT = WindowSec;
            long n = 0; int seen = 0;
            foreach (var e in RecentEvents())
            {
                if (nowT - e.TSec > sec) break;
                n += e.Batch; seen++;
            }
            return (n, seen == RingSize);
        }

        // ── GC internals ───────────────────────────────────────────────────
        void PollGc(long now)
        {
            int g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            if (_g1 >= 0)
            {
                // Stamp at the poll that observed the change (≤ one poll late).
                if (g1 != _g1) _lastG1Ticks = now;
                if (g2 != _g2) _lastG2Ticks = now;
            }
            _g1 = g1; _g2 = g2;
            if (PauseSupported)
            {
                long p;
                try { p = GC.GetTotalPauseDuration().Ticks; } catch { return; }
                _prTicks[_prHead] = now; _prPause[_prHead] = p;
                _prHead = (_prHead + 1) % PauseRing;
                if (_prCount < PauseRing) _prCount++;
            }
        }

        /// <summary>GC look-back from <paramref name="now"/> (Stopwatch ticks). Public for tests.</summary>
        public GcLook LookGc(long now)
        {
            double f = Stopwatch.Frequency;
            var g = new GcLook
            {
                MsSinceG1 = _lastG1Ticks > 0 ? (now - _lastG1Ticks) * 1000.0 / f : -1,
                MsSinceG2 = _lastG2Ticks > 0 ? (now - _lastG2Ticks) * 1000.0 / f : -1,
                PauseMs = double.NaN, LookbackMs = 0
            };
            if (!PauseSupported || _prCount == 0) return g;
            long cutoff = now - (long)(LookbackMs / 1000.0 * f);
            int newest = (_prHead - 1 + PauseRing) % PauseRing;
            // Walk back to the oldest entry still at/after the cutoff… then one more
            // (the last entry before the cutoff) so the window fully covers LookbackMs.
            int idx = newest, steps = 1;
            while (steps < _prCount)
            {
                int prev = (idx - 1 + PauseRing) % PauseRing;
                idx = prev; steps++;
                if (_prTicks[idx] <= cutoff) break;
            }
            g.PauseMs    = (_prPause[newest] - _prPause[idx]) / (double)TimeSpan.TicksPerMillisecond;
            g.LookbackMs = (now - _prTicks[idx]) * 1000.0 / f;
            return g;
        }

        static bool ProbePause()
        {
            try { _ = GC.GetTotalPauseDuration(); return true; } catch { return false; }
        }

        // ── internals ──────────────────────────────────────────────────────
        void Rebase(long count, long nowTicks, string reason)
        {
            _seeded = true;
            _baseCount = count;
            _baseTicks = nowTicks;
            BaselineWall = DateTime.Now;
            BaselineReason = reason;
            BaselineAfterHostReset = false;
            _ringHead = 0; _ringCount = 0; LoggedEvents = 0;
            Array.Clear(_ring, 0, _ring.Length);
            Episodes = 0; _lastMissTSec = double.NegativeInfinity;
            CtlPhaseN = CtlPhaseNear = CtlGcN = CtlNearG2 = 0; CtlPauseSumMs = 0; _ctlPause.Clear();
            try { _g2AtBase = GC.CollectionCount(2); } catch { }
            try { _pauseAtBaseTicks = PauseSupported ? GC.GetTotalPauseDuration().Ticks : -1; } catch { _pauseAtBaseTicks = -1; }
            try { _allocAtBase = GC.GetTotalAllocatedBytes(false); } catch { }
        }

        void Bind(object target)
        {
            _boundTarget = target;
            _getMiss   = BindLong(target, "DeadlineMissCount");
            _getWorst  = BindLong(target, "DeadlineWorstOverrunMicros");
            _getResets = BindLong(target, "DriverResetCount");
            _reset     = BindAction(target, "ResetAudioDropoutCounters");
            Available             = _getMiss   != null;
            ResetAvailable        = _reset     != null;
            DriverResetsAvailable = _getResets != null;
            _seeded = false; HostCount = -1; DriverResets = -1;
            BaselineReason = "first-read";
        }

        static Func<long>? BindLong(object target, string name)
        {
            try
            {
                var pi = target.GetType().GetProperty(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                var get = pi?.GetGetMethod(nonPublic: true);
                if (get == null) return null;
                if (get.ReturnType == typeof(long))
                {
                    return get.IsStatic
                        ? (Func<long>)get.CreateDelegate(typeof(Func<long>))
                        : (Func<long>)get.CreateDelegate(typeof(Func<long>), target);
                }
                object? tgt = get.IsStatic ? null : target;
                return () => Convert.ToInt64(pi!.GetValue(tgt));
            }
            catch { return null; }
        }

        static Action? BindAction(object target, string name)
        {
            try
            {
                var mi = target.GetType().GetMethod(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static,
                    binder: null, types: Type.EmptyTypes, modifiers: null);
                if (mi == null) return null;
                return mi.IsStatic
                    ? (Action)mi.CreateDelegate(typeof(Action))
                    : (Action)mi.CreateDelegate(typeof(Action), target);
            }
            catch { return null; }
        }
    }

    /// <summary>Pure summaries over the miss log, for the dump. No ReBuzz/WPF dependencies.</summary>
    public static class MissAnalysis
    {
        public static MissEvent[] Onsets(MissEvent[] newestFirst)
        {
            var l = new System.Collections.Generic.List<MissEvent>();
            if (newestFirst != null) foreach (var e in newestFirst) if (e.Onset) l.Add(e);
            return l.ToArray();
        }

        public struct IntervalStats { public int n; public double meanS, sdS, cv, minS, maxS; }

        /// <summary>Intervals (seconds) between consecutive entries of a newest-first list.</summary>
        public static IntervalStats Intervals(MissEvent[] newestFirst)
        {
            var r = new IntervalStats();
            if (newestFirst == null || newestFirst.Length < 2) return r;
            int n = newestFirst.Length - 1;
            double sum = 0, sum2 = 0, mn = double.MaxValue, mx = 0;
            for (int i = 0; i < n; i++)
            {
                double d = newestFirst[i].TSec - newestFirst[i + 1].TSec;
                sum += d; sum2 += d * d;
                if (d < mn) mn = d;
                if (d > mx) mx = d;
            }
            r.n = n; r.meanS = sum / n;
            r.sdS = Math.Sqrt(Math.Max(0, sum2 / n - r.meanS * r.meanS));
            r.cv = r.meanS > 0 ? r.sdS / r.meanS : 0;
            r.minS = mn; r.maxS = mx;
            return r;
        }

        public struct Proportion { public int n, hits; public double ctlFrac; public long ctlN; public double p; }

        static Proportion Finish(int n, int hits, long ctlN, long ctlHits)
        {
            var r = new Proportion { n = n, hits = hits, ctlN = ctlN };
            r.ctlFrac = ctlN > 0 ? (double)ctlHits / ctlN : double.NaN;
            r.p = (n > 0 && ctlN > 0) ? BinomTail(n, hits, r.ctlFrac) : double.NaN;
            return r;
        }

        /// <summary>Onsets within NearStartTicks of a (non-Master) pattern start vs random-time control.</summary>
        public static Proportion PatternPhase(MissEvent[] onsets, long ctlN, long ctlNear, out int tol)
        {
            int n = 0, hits = 0; tol = 0;
            foreach (var e in onsets)
            {
                var c = e.Song;
                if (!c.Valid || !c.Playing || c.PatPos < 0) continue;
                n++; tol = c.NearStartTicks;
                if (c.PatPos <= c.NearStartTicks) hits++;
            }
            return Finish(n, hits, ctlN, ctlNear);
        }

        /// <summary>Onsets with a G2 observed within NearG2Ms before them vs control.</summary>
        public static Proportion NearG2(MissEvent[] onsets, long ctlN, long ctlHits)
        {
            int n = 0, hits = 0;
            foreach (var e in onsets)
            {
                n++;
                if (e.Gc.MsSinceG2 >= 0 && e.Gc.MsSinceG2 <= DeadlineMonitor.NearG2Ms) hits++;
            }
            return Finish(n, hits, ctlN, ctlHits);
        }

        /// <summary>
        /// Onsets whose lookback GC pause exceeded <paramref name="thresholdMs"/> (the control's
        /// PauseCtlQuantile) vs the control fraction above it. meanPause = mean over onsets.
        /// </summary>
        public static Proportion PauseHit(MissEvent[] onsets, double thresholdMs, long ctlN, long ctlAbove, out double meanPauseMs)
        {
            int n = 0, hits = 0; double sum = 0;
            foreach (var e in onsets)
            {
                if (double.IsNaN(e.Gc.PauseMs)) continue;
                n++; sum += e.Gc.PauseMs;
                if (e.Gc.PauseMs > thresholdMs) hits++;
            }
            meanPauseMs = n > 0 ? sum / n : double.NaN;
            return Finish(n, hits, ctlN, ctlAbove);
        }

        /// <summary>P(X ≥ k) for X ~ Binomial(n, p). Exact sum; n is small (≤ RingSize).</summary>
        public static double BinomTail(int n, int k, double p)
        {
            if (k <= 0) return 1.0;
            if (k > n || !(p > 0)) return 0.0;
            if (p >= 1) return 1.0;
            double sum = 0;
            for (int i = k; i <= n; i++)
                sum += Math.Exp(LogChoose(n, i) + i * Math.Log(p) + (n - i) * Math.Log(1 - p));
            return Math.Min(1.0, sum);
        }

        static double LogChoose(int n, int k)
        {
            double r = 0;
            for (int i = 1; i <= k; i++) r += Math.Log(n - k + i) - Math.Log(i);
            return r;
        }
    }
}
