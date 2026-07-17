#nullable disable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace WDE.PedalProfiler2
{
    // ─────────────────────────────────────────────────────────────────────────
    // Stage-2 PP2 side. Polls ReBuzzCore's stall-snapshot ring (populated by the
    // engine's StallRecorder at each ASIO deadline miss), classifies each snapshot,
    // and writes a cumulative pp2_fr_stall.txt. The classifier is the verbatim port
    // of the logic validated against the adversarial fake source (SLOW_MACHINE /
    // OVERLOAD / STARVE / IPC, with honest abstention on mixed/under-sampled data).
    //
    // Everything is reflection-based: PP2 has no compile reference to ReBuzz's
    // StallSnapshotDto, so it reads fields by name (handles cached on first use),
    // exactly as it already reads DeadlineMissCount.
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class StallDrain
    {
        bool _resolved, _dtoResolved;
        PropertyInfo _countProp, _ringProp, _armedProp;
        FieldInfo _fOverrun, _fWorstM, _fWorstD, _fOver, _fTotal, _fAny, _fIpcM, _fIpcU, _fAth, _fGcAge;

        long _lastCount = -1;   // -1 → seed on first poll (skip pre-arm backlog)

        int _n;
        readonly Dictionary<string, int> _cls = new();
        readonly Dictionary<string, Dictionary<string, int>> _culprit = new();

        const int DWELL_FLOOR = 1500, IPC_FLOOR = 3000, GC_TIGHT_MS = 50;
        const double DOMINATE = 0.5;

        void Resolve(object buzz)
        {
            var t = buzz.GetType();
            _countProp = t.GetProperty("StallSnapshotCount");
            _ringProp  = t.GetProperty("StallSnapshotRing");
            _armedProp = t.GetProperty("StallCaptureArmed");
            _resolved = true;
        }

        void ResolveDto(object dto)
        {
            var t = dto.GetType();
            _fOverrun = t.GetField("OverrunUs");
            _fWorstM  = t.GetField("WorstMachine");
            _fWorstD  = t.GetField("WorstDwellUs");
            _fOver    = t.GetField("WorksOverFloor");
            _fTotal   = t.GetField("TotalBusyUs");
            _fAny     = t.GetField("AnyWorkRan");
            _fIpcM    = t.GetField("IpcWaitMachine");
            _fIpcU    = t.GetField("IpcWaitUs");
            _fAth     = t.GetField("AudioThreads");
            _fGcAge   = t.GetField("GcAgeMs");
            _dtoResolved = _fWorstD != null;
        }

        /// <summary>Mirror the arm flag to the engine (tie to PP2's enable toggle).</summary>
        public void SetArmed(object buzz, bool armed)
        {
            try
            {
                if (buzz == null) return;
                if (!_resolved) Resolve(buzz);
                _armedProp?.SetValue(buzz, armed);
            }
            catch { }
        }

        /// <summary>Drain new snapshots, accumulate, and deliver pp2_fr_stall.txt.</summary>
        public void Poll(object buzz, Action<string, string> deliver)
        {
            try
            {
                if (buzz == null) return;
                if (!_resolved) Resolve(buzz);
                if (_countProp == null || _ringProp == null) return;

                long count = Convert.ToInt64(_countProp.GetValue(buzz));
                if (_lastCount < 0) { _lastCount = count; return; }   // seed
                if (count <= _lastCount) return;

                if (_ringProp.GetValue(buzz) is not Array ring || ring.Length == 0) { _lastCount = count; return; }
                int len = ring.Length;
                long from = Math.Max(_lastCount, count - len);        // cap: only slots still in the ring
                for (long i = from; i < count; i++)
                {
                    var dto = ring.GetValue((int)(i % len));
                    if (dto == null) continue;
                    if (!_dtoResolved) ResolveDto(dto);
                    if (!_dtoResolved) continue;
                    Accumulate(dto);
                }
                _lastCount = count;
                deliver?.Invoke("pp2_fr_stall.txt", Render());
            }
            catch { }
        }

        int GI(FieldInfo f, object dto) { try { return Convert.ToInt32(f.GetValue(dto)); } catch { return 0; } }
        string GS(FieldInfo f, object dto) { try { return f.GetValue(dto) as string; } catch { return null; } }

        void Accumulate(object dto)
        {
            int overrun = GI(_fOverrun, dto), worstD = GI(_fWorstD, dto), over = GI(_fOver, dto),
                total = GI(_fTotal, dto), any = GI(_fAny, dto), ipcU = GI(_fIpcU, dto), ath = GI(_fAth, dto),
                gcAge = _fGcAge != null ? GI(_fGcAge, dto) : -1;
            string worstM = GS(_fWorstM, dto), ipcM = GS(_fIpcM, dto);
            if (ath <= 0) ath = 8;

            string cls; string culprit = null;
            if (ipcU >= IPC_FLOOR && ipcU >= DOMINATE * Math.Max(overrun, 1)) { cls = "IPC"; culprit = ipcM; }
            else if (gcAge >= 0 && gcAge <= GC_TIGHT_MS) cls = "GC_PAUSE";   // miss lands INSIDE a Gen2 pause
            else if (any == 0) cls = "STARVE";
            else if (worstD >= DWELL_FLOOR && worstD >= DOMINATE * Math.Max(total, 1)) { cls = "SLOW_MACHINE"; culprit = worstM; }
            else if (over * 2 >= ath && total >= DWELL_FLOOR) cls = "OVERLOAD";
            else cls = "AMBIGUOUS";

            _n++;
            _cls[cls] = _cls.TryGetValue(cls, out var c) ? c + 1 : 1;
            if (culprit != null)
            {
                if (!_culprit.TryGetValue(cls, out var cc)) { cc = new(); _culprit[cls] = cc; }
                cc[culprit] = cc.TryGetValue(culprit, out var v) ? v + 1 : 1;
            }
        }

        (string name, int count) TopCulprit(string cls)
        {
            if (!_culprit.TryGetValue(cls, out var cc) || cc.Count == 0) return (null, 0);
            string bn = null; int bv = 0;
            foreach (var kv in cc) if (kv.Value > bv) { bn = kv.Key; bv = kv.Value; }
            return (bn, bv);
        }

        string Render()
        {
            var sb = new StringBuilder();
            sb.Append("── FLIGHT RECORDER STALL ─── (").Append(_n).Append(" misses, engine snapshot v1)\n");
            if (_n == 0) { sb.Append("  no snapshots yet.\n"); return sb.ToString(); }

            var order = new List<string>(_cls.Keys);
            order.Sort((a, b) => _cls[b].CompareTo(_cls[a]));
            sb.Append("  class            count  share   top culprit (share in class)\n");
            foreach (var c in order)
            {
                int cnt = _cls[c]; var (cm, cc) = TopCulprit(c);
                string tag = (c == "SLOW_MACHINE" || c == "IPC") && cm != null ? $"{cm} ({cc * 100 / cnt}%)" : "—";
                sb.Append("  ").Append(c.PadRight(14)).Append(' ')
                  .Append(cnt.ToString().PadLeft(5)).Append("  ")
                  .Append(((cnt * 100 / _n) + "%").PadLeft(5)).Append("   ").Append(tag).Append('\n');
            }

            string top = order[0]; int tn = _cls[top]; double dom = (double)tn / _n;
            sb.Append('\n');
            if (dom < 0.5)
                sb.Append("  VERDICT: no dominant cause — top ").Append(top).Append(" only ")
                  .Append((int)(dom * 100)).Append("%. Mixed/under-sampled — collect more.\n");
            else if (top == "SLOW_MACHINE" || top == "IPC")
            {
                var (cm, cc) = TopCulprit(top);
                if (cm != null && (double)cc / tn >= 0.5)
                {
                    string why = top == "SLOW_MACHINE" ? "its Work() runs/blocks long on the audio thread"
                                                       : "native IPC round-trip stalls the worker (Core §21)";
                    sb.Append("  VERDICT: ").Append(top).Append(" — '").Append(cm).Append("' in ")
                      .Append((int)(dom * 100)).Append("% of misses (").Append(cc * 100 / tn).Append("% in class). ")
                      .Append(why).Append(".\n");
                }
                else
                    sb.Append("  VERDICT: ").Append(top).Append(" dominant (").Append((int)(dom * 100))
                      .Append("%) but culprit machine spread.\n");
            }
            else
            {
                string d = top == "OVERLOAD" ? "workers genuinely busy — real DSP overload, not a stall"
                         : top == "STARVE"   ? "no work ran in-window — dispatch/fill-thread starvation"
                         : top == "GC_PAUSE" ? "misses land inside Gen2 pauses (<=50ms) — allocation-rate; kill DSP-path allocs (perf-sweep A1/A2)"
                         : "doesn't fit a class — field set may be insufficient";
                sb.Append("  VERDICT: ").Append(top).Append(" (").Append((int)(dom * 100)).Append("%) — ").Append(d).Append(".\n");
            }
            sb.Append("  (each miss = one windowed snapshot; small N misleads — aim for 50+.)\n");
            return sb.ToString();
        }
    }
}
