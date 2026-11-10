// Pedal Profiler2 — AllocSampler (v2.3.0)
//
// In-process allocation sampler. Subscribes an EventListener to the runtime's own
// "Microsoft-Windows-DotNETRuntime" event source with the GC keyword at Verbose, which
// turns on GCAllocationTick events: one roughly every 100 KB allocated on a thread,
// each carrying the amount since that thread's previous tick, the type of the object that
// crossed the threshold, the heap kind (small / large / pinned) and the OS thread id.
// Summing the amounts per type and per thread gives a sampled breakdown of WHO is
// allocating and WHAT — no external tools, no ReBuzz change.
//
// Why (050 dumps, 2026-11-10): 200 MB/s of managed allocation → gen0 35/s, gen1 9/s,
// GC pause ≈ 33% of wall time, a background gen2 every ~13 s, and every deadline-miss
// episode starting ≤ 500 ms after a gen2 (25/25). Finding the allocator is the fix path.
//
// Accuracy: tick-sampled, so per-type totals are statistical (the type named is the one
// that happened to cross the 100 KB line); over seconds at 100+ MB/s the proportions are
// good. Off by default; costs a lock + dictionary update per ~100 KB allocated while on.
//
// Threading: OnEventWritten runs on the runtime's event dispatch thread; readers take a
// snapshot under the same lock from the UI thread. Fields use initialisers because the
// EventListener base constructor can call OnEventSourceCreated before our constructor body.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime.InteropServices;

namespace WDE.PedalProfiler2
{
    public sealed class AllocSampler : EventListener
    {
        const string RuntimeSource = "Microsoft-Windows-DotNETRuntime";
        const long   GcKeyword     = 0x1;

        readonly object _lk = new object();
        EventSource? _rt;
        volatile bool _on;

        sealed class ThreadAcc
        {
            public long Bytes;
            public readonly Dictionary<string, long> Types = new Dictionary<string, long>();
        }

        Dictionary<string, (long bytes, long n)> _byType = new Dictionary<string, (long, long)>();
        Dictionary<long, ThreadAcc> _byThread = new Dictionary<long, ThreadAcc>();
        long _total, _ticks, _large, _pinned;
        long _startTs, _stopTs;
        public bool Enabled => _on;
        public long SelfTid { get; private set; }   // the runtime's event-dispatch thread (our own overhead lands here)
        public bool SourceSeen => _rt != null;

        public void Start()
        {
            lock (_lk)
            {
                _byType = new Dictionary<string, (long, long)>();
                _byThread = new Dictionary<long, ThreadAcc>();
                _total = _ticks = _large = _pinned = 0;
                _startTs = Stopwatch.GetTimestamp(); _stopTs = 0;
                _on = true;
            }
            var rt = _rt;
            if (rt != null) { try { EnableEvents(rt, EventLevel.Verbose, (EventKeywords)GcKeyword); } catch { } }
        }

        public void Stop()
        {
            if (_on) lock (_lk) _stopTs = Stopwatch.GetTimestamp();
            _on = false;
            var rt = _rt;
            if (rt != null) { try { DisableEvents(rt); } catch { } }
        }

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name != RuntimeSource) return;
            _rt = source;
            if (_on) { try { EnableEvents(source, EventLevel.Verbose, (EventKeywords)GcKeyword); } catch { } }
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (!_on) return;
            var name = e.EventName;
            if (name == null || !name.StartsWith("GCAllocationTick", StringComparison.Ordinal)) return;
            try
            {
                var names = e.PayloadNames;
                var pl = e.Payload;
                if (names == null || pl == null) return;
                long amount = 0; string type = "?"; long kind = 0;
                for (int i = 0; i < names.Count && i < pl.Count; i++)
                {
                    switch (names[i])
                    {
                        case "AllocationAmount64": amount = Convert.ToInt64(pl[i]); break;
                        case "AllocationAmount":   if (amount == 0) amount = Convert.ToInt64(pl[i]); break;
                        case "TypeName":           type = pl[i] as string ?? "?"; break;
                        case "AllocationKind":     kind = Convert.ToInt64(pl[i]); break;
                    }
                }
                if (amount <= 0) return;
                long tid = e.OSThreadId;
                if (SelfTid == 0) { try { SelfTid = OsThread.Id(); } catch { } }
                lock (_lk)
                {
                    _total += amount; _ticks++;
                    if (kind == 1) _large += amount; else if (kind == 2) _pinned += amount;
                    _byType.TryGetValue(type, out var t);
                    _byType[type] = (t.bytes + amount, t.n + 1);
                    if (!_byThread.TryGetValue(tid, out var ta)) _byThread[tid] = ta = new ThreadAcc();
                    ta.Bytes += amount;
                    ta.Types.TryGetValue(type, out var tb);
                    ta.Types[type] = tb + amount;
                }
            }
            catch { }
        }

        // ── Snapshot for the dump ──────────────────────────────────────────
        public sealed class Report
        {
            public double Seconds;
            public long   TotalBytes, Ticks, LargeBytes, PinnedBytes;
            public List<(string type, long bytes, long n)> Types = new();
            public List<(long tid, long bytes, List<(string type, long bytes)> top)> Threads = new();
            public double MBps(long b) => Seconds > 0 ? b / 1048576.0 / Seconds : 0;
        }

        public Report Snapshot(int topTypes = 15, int topThreads = 8, int typesPerThread = 4)
        {
            var r = new Report();
            lock (_lk)
            {
                r.Seconds = _startTs > 0 ? ((_stopTs > 0 ? _stopTs : Stopwatch.GetTimestamp()) - _startTs) / (double)Stopwatch.Frequency : 0;
                r.TotalBytes = _total; r.Ticks = _ticks; r.LargeBytes = _large; r.PinnedBytes = _pinned;
                var types = new List<(string, long, long)>();
                foreach (var kv in _byType) types.Add((kv.Key, kv.Value.bytes, kv.Value.n));
                types.Sort((a, b) => b.Item2.CompareTo(a.Item2));
                for (int i = 0; i < types.Count && i < topTypes; i++) r.Types.Add(types[i]);

                var thr = new List<(long, ThreadAcc)>();
                foreach (var kv in _byThread) thr.Add((kv.Key, kv.Value));
                thr.Sort((a, b) => b.Item2.Bytes.CompareTo(a.Item2.Bytes));
                for (int i = 0; i < thr.Count && i < topThreads; i++)
                {
                    var tt = new List<(string, long)>();
                    foreach (var kv in thr[i].Item2.Types) tt.Add((kv.Key, kv.Value));
                    tt.Sort((a, b) => b.Item2.CompareTo(a.Item2));
                    if (tt.Count > typesPerThread) tt.RemoveRange(typesPerThread, tt.Count - typesPerThread);
                    r.Threads.Add((thr[i].Item1, thr[i].Item2.Bytes, tt));
                }
            }
            return r;
        }
    }

    /// <summary>OS thread id of the calling thread (Windows); 0 where unavailable.</summary>
    public static class OsThread
    {
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        static bool _broken;
        public static long Id()
        {
            if (_broken) return 0;
            try { return GetCurrentThreadId(); }
            catch { _broken = true; return 0; }
        }
    }
}
