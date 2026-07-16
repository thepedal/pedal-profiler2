#nullable disable
using System;
using System.Threading;

namespace WDE.PedalProfiler2.FR
{
    // ─────────────────────────────────────────────────────────────────────────
    // Flight Recorder — always-on ring tape. Phase 1.
    //
    // Design invariants (see integration guide):
    //   * Everything written from the audio/fill thread is alloc-free and
    //     lock-free — same discipline as PP2's existing OtherMs ring (§9.2).
    //   * One QPC clock (Stopwatch.GetTimestamp / Stopwatch.Frequency) across
    //     every stream, so the three tapes merge onto one time axis at freeze.
    //   * The analysed window always ends at mark+postRoll, i.e. strictly in
    //     the past relative to the snapshot, so torn newest-slot writes are
    //     never inside the window that gets read.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Per-chunk timing. Written from Work() at ~chunk cadence (~250/s).</summary>
    public struct ChunkRec
    {
        public long  Qpc;
        public float ElapsedMs;   // wall-clock this chunk
        public float OtherMs;     // host/unaccounted portion (existing PP2 metric)
    }

    /// <summary>Discrete event. Phase 1 populates CC from MidiControlChange;
    /// phase 2 adds note/param via tick-poll with SrcId = machine bit index.</summary>
    public struct EvtRec
    {
        public long   Qpc;
        public ushort SrcId;   // Kind==CC → SRC_MIDI; else machine bit index
        public byte   Kind;    // EvtKind.*
        public byte   Chan;    // MIDI channel (CC), else 0
        public ushort Ctrl;    // CC number, else param index
        public int    Value;
    }

    /// <summary>Active-set snapshot. Written from the UI tick (~30/s), NOT the
    /// audio thread — populating this needs the machine list, which is only
    /// safe to touch off the audio thread (Core §21).</summary>
    public struct CostRec
    {
        public long  Qpc;
        public ulong ActiveMask;   // bit b set = machine at bit b was active
    }

    /// <summary>Marks the QPC at which the machine→bit IndexMap generation
    /// changed. Checked at freeze to flag mid-window graph edits.</summary>
    public struct GraphMarkRec
    {
        public long Qpc;
        public int  Generation;
    }

    public static class EvtKind
    {
        public const byte CC    = 1;   // MIDI control change
        public const byte NOTE  = 2;   // reserved — phase 2
        public const byte PARAM = 3;   // reserved — phase 2
        public const byte PRESET= 4;   // reserved — phase 2
    }

    public static class FrConst
    {
        public const ushort SRC_MIDI = 0xFFFF;   // sentinel: event came from MIDI, not a machine
        public const int    MAX_BITS  = 64;      // one ulong mask; widen to ulong[] if a session churns past 64
    }

    /// <summary>
    /// Fixed-capacity power-of-two ring of value-type records. Single monotonic
    /// write index; last-write-wins. Write() is audio-thread safe (alloc-free,
    /// lock-free). SnapshotAll() copies the whole backing array and must run off
    /// the audio thread.
    /// </summary>
    public sealed class Ring<T> where T : struct
    {
        private readonly T[]  _buf;
        private readonly int  _mask;
        private long _widx;   // next slot to write; only ever incremented

        public Ring(int capacityPow2)
        {
            if (capacityPow2 < 2 || (capacityPow2 & (capacityPow2 - 1)) != 0)
                throw new ArgumentException("capacity must be a power of two >= 2", nameof(capacityPow2));
            _buf  = new T[capacityPow2];
            _mask = capacityPow2 - 1;
        }

        public int Capacity => _buf.Length;

        /// <summary>AUDIO THREAD. Alloc-free, lock-free. The multi-field struct
        /// store can tear against a concurrent reader, but see the class remarks:
        /// the read window is always in the settled past.</summary>
        public void Write(in T rec)
        {
            long i = Interlocked.Increment(ref _widx) - 1;
            _buf[(int)(i & _mask)] = rec;
        }

        /// <summary>UI THREAD. Returns a private copy of the whole ring and the
        /// write index observed at snapshot time. Callers filter by timestamp;
        /// the slot at (widx-1) is the one most likely mid-write and, being the
        /// newest, is always newer than any analysed window anyway.</summary>
        public T[] SnapshotAll(out long widxAtSnapshot)
        {
            widxAtSnapshot = Volatile.Read(ref _widx);
            var copy = new T[_buf.Length];
            Array.Copy(_buf, copy, _buf.Length);
            return copy;
        }
    }
}
