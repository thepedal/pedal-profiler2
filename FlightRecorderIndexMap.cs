#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace WDE.PedalProfiler2.FR
{
    // ─────────────────────────────────────────────────────────────────────────
    // machine-name → small-int bit index.
    //
    // Keyed by NAME (string), because PP2's active-machine set is published as
    // string[] names (the "not muted" list) and names are the stable persistent
    // key in ReBuzz (Core §8/§21). Immutable snapshot swapped on the UI thread;
    // never mutated from the audio thread.
    //
    // No-bit-reuse: a bit is never reissued to a different name within a session
    // (monotonic high-water mark). This makes a bit map to at most one machine
    // across the whole tape, so baseline counts aggregated over graph changes
    // stay valid and a recorded mask can't be silently re-attributed. A machine
    // removed and re-added under the SAME name correctly reclaims its own bit.
    // ─────────────────────────────────────────────────────────────────────────

    public sealed class IndexMap
    {
        public readonly int Generation;
        public readonly Dictionary<string, int> ToBit;
        public readonly string[] Names;        // bit → name, for the dump legend
        public readonly bool Exhausted;

        public IndexMap(int generation, Dictionary<string, int> toBit, string[] names, bool exhausted)
        {
            Generation = generation; ToBit = toBit; Names = names; Exhausted = exhausted;
        }

        public static IndexMap Empty =>
            new IndexMap(0, new Dictionary<string, int>(), new string[FrConst.MAX_BITS], false);
    }

    /// <summary>Owns the current IndexMap and the generation-change marker ring.
    /// All mutation on the UI thread.</summary>
    public sealed class IndexMapKeeper
    {
        private volatile IndexMap _current = IndexMap.Empty;
        private readonly Ring<GraphMarkRec> _graphMarks = new Ring<GraphMarkRec>(64);
        private int _highWater;   // monotonic → freed bits are never reissued

        public IndexMap Current => _current;

        /// <summary>UI THREAD. Assign a bit to every name; rebuild only when a
        /// name not seen before appears (cheap common case: all names known).</summary>
        public void Ensure(IEnumerable<string> names)
        {
            if (names == null) return;
            var cur = _current;
            bool needRebuild = false;
            foreach (var n in names)
                if (n != null && !cur.ToBit.ContainsKey(n)) { needRebuild = true; break; }
            if (needRebuild) Rebuild(names, cur);
        }

        private void Rebuild(IEnumerable<string> names, IndexMap prev)
        {
            // Copy prior assignments so surviving names keep their bit. Dead names
            // linger as string keys (harmless — no object refs held); their bits
            // stay burned via the high-water mark.
            var toBit = new Dictionary<string, int>(prev.ToBit);
            bool exhausted = prev.Exhausted;

            foreach (var n in names)
            {
                if (n == null) continue;
                if (toBit.ContainsKey(n)) continue;
                if (_highWater >= FrConst.MAX_BITS) { exhausted = true; continue; }
                toBit[n] = _highWater++;
            }

            var nm = new string[FrConst.MAX_BITS];
            foreach (var kv in toBit)
                if (kv.Value >= 0 && kv.Value < FrConst.MAX_BITS) nm[kv.Value] = kv.Key;

            var next = new IndexMap(prev.Generation + 1, toBit, nm, exhausted);
            _current = next;
            _graphMarks.Write(new GraphMarkRec { Qpc = Stopwatch.GetTimestamp(), Generation = next.Generation });
        }

        /// <summary>UI THREAD. Bitmask of the currently-active (not-muted) names.</summary>
        public ulong BuildActiveMask(IEnumerable<string> activeNames)
        {
            var map = _current.ToBit;
            ulong mask = 0UL;
            if (activeNames == null) return mask;
            foreach (var n in activeNames)
                if (n != null && map.TryGetValue(n, out int bit) && bit < 64) mask |= (1UL << bit);
            return mask;
        }

        /// <summary>UI THREAD. Did the map generation change inside [lo, hi]?</summary>
        public bool GraphChangedInWindow(long lo, long hi)
        {
            var arr = _graphMarks.SnapshotAll(out _);
            foreach (var g in arr)
                if (g.Qpc >= lo && g.Qpc <= hi && g.Generation > 0) return true;
            return false;
        }
    }
}
