# PP2 Flight Recorder — Phase 1 integration & validation

Drop-in recorder subsystem for **Flight Recorder mode inside Pedal Profiler2**.
Three source files compile into the existing `PedalProfiler2.csproj`; no new
project, no new machine.

- `FlightRecorderRings.cs` — lock-free ring + record structs (audio-thread tape)
- `FlightRecorderIndexMap.cs` — machine→bit map, no-reuse, generation markers
- `FlightRecorder.cs` — mark/freeze, baseline + association, timeline CSV, summary

## Deployment (mandatory — verify PP2's csproj already complies)

These files carry no build settings of their own; they inherit
`PedalProfiler2.csproj`. That csproj **must** contain, in a `<PropertyGroup>`:

```xml
<DebugType>none</DebugType>
<DebugSymbols>false</DebugSymbols>
<GenerateDependencyFile>false</GenerateDependencyFile>
```

.NET 10+ (`net10.0-windows`), deploy the **.dll only** to the relevant
`C:\Program Files\ReBuzz` gear dir. If touching PP2's csproj for this feature
and any of the three are missing, add them (project rule).

## Wiring — five call sites

Construct once (e.g. in PP2's machine ctor or first-init), giving it PP2's
existing §9 file+clipboard delivery and its §9.10 run-context reader:

```csharp
_fr = new FlightRecorder(
    deliver:    (name, content) => Pp2Deliver(name, content),   // your file+clipboard path
    runContext: () => BuildRunContext());                       // fill the RunContext struct
```

Then hang the hooks off points that already exist:

| call | thread | where | note |
|------|--------|-------|------|
| `_fr.OnChunk(elapsedMs, otherMs)` | audio | `Work()`, beside the OtherMs ring write | alloc-free |
| `_fr.OnMidiCC(ctrl, ch, val)` | audio | `MidiControlChange(...)` | fires per CC (PeerCtrl §10) |
| `_fr.OnCostSample(activeMachines)` | UI | UI tick | pass the set PP2 already computes for spike attribution |
| `_fr.OnUiTick()` | UI | UI tick, **after** `OnCostSample` | performs the freeze once post-roll accrues |
| `_fr.OnGraphChanged(machines)` | UI | song machine add/remove/rename | rebuilds the bit map |

Trigger from wherever the mark comes from:

```csharp
// GUI "Mark glitch" button (UI thread):
_fr.RequestMark(isAuto: false);

// or a reserved footswitch CC inside MidiControlChange (audio thread) — record AND mark:
_fr.OnMidiCC(ctrl, channel, value);
if (ctrl == MARK_CC) _fr.RequestMark(isAuto: false);
```

`RequestMark` only stamps a QPC and returns; the actual copy/analysis happens
on the next qualifying `OnUiTick`. Never call `Freeze` from the audio thread.

## What phase 1 does and doesn't see

- **Sees, directly:** per-chunk timing, incoming MIDI CC, and the active-machine
  set at UI-tick rate.
- **Does not see yet:** notes and pattern/automation param writes to *other*
  machines — those go straight to target setters and PP2 isn't in that path.
  That's phase 2 (tick-poll, PeerCtrl §9 pattern) and it inherits the Tracker
  §1.1 same-row collision blind spot, so it will be tick-resolution only.
- **Trigger:** manual only in phase 1. The latency-free auto-trigger needs a host
  deadline/underflow signal (§15.4) — phase 3. Manual marks carry an irreducible
  150–600 ms reaction smear, which is exactly why the causal slice is a *band*
  offset back from the mark, not a point.

## Outputs

Two artifacts per freeze, both via your deliver callback:

- `pp2_fr_<hhmmss>.csv` — the merged timeline for this mark (one row per
  chunk/event/cost sample on a single `t_ms` axis, `t=0` at the mark, negatives
  before). Self-describing header carries run context, `ci`, `fillthread`,
  `graph_changed_in_window`, and a `# bit N = name` legend so it decodes with no
  external state.
- `pp2_fr_assoc.txt` — the **cumulative** association table across all marks this
  session (overwritten each freeze). Sorted by onset-z then lift.

### Timeline CSV columns

```
t_ms,kind,elapsed_ms,other_ms,over,src,chan,ctrl,value_or_active_hex
```

- `kind=chunk` → `elapsed_ms`,`other_ms` set; `over=1` if `elapsed>1.5×budget`
  (annotation only — do NOT read it as a glitch on `ci=1` songs, §15.2).
- `kind=cc`/`note`/`param`/`preset` → `src`,`chan`,`ctrl`,`value` set.
- `kind=cost` → `value_or_active_hex` = 16-hex active-machine bitmask; decode via
  the header legend.

## Reading the association table

```
machine                 base%   band%   lift   onset   z     note
Pedal FazeR              8.1    75.0     9.3    0.75    6.1   ← disproportionate onset near glitches
Infector (native)      61.4    83.3     1.4    0.17    0.9
Pedal Comp             96.0   100.0     1.0    0.00   -0.2   ← always on; uninformative
```

- **onset + z is the signal.** `z` = how far this machine's inactive→active rate
  in the band exceeds what its *own* baseline dynamics predict. A busy machine
  has high `band%` for free; `z` corrects for that. Lead with it.
- **lift ≈ 1 = along for the ride** (§6.1). High `band%` with lift ≈ 1 is a
  bystander, not a cause.
- **Small mark counts lie** (§6.4's phantom periodicity is the cautionary tale).
  `z` from 3 marks is meaningless. Mark many.

## Phase-1 validation protocol — the decision gate

Before trusting this on a real glitch, prove the manual-mark signal separates on
a **known** cause. This also tunes your personal reaction band.

1. Load a busy song (the perf-handoff 33-Hallverb song is a good stress case).
   Note which machine is steadily-on (e.g. Pedal Comp) as your negative control.
2. Add a planted cause: automate one otherwise-cheap machine to spike hard on a
   known schedule (heavy param sweep, or hammer one CC into it) so it produces
   an **audible** artifact each time.
3. Play. Each time you hear the artifact, hit Mark. Collect ≥15–20 marks.
4. Read `pp2_fr_assoc.txt`.

**Pass:** the planted machine tops the onset/z column (z ≳ 3, onset ≳ 0.5) while
the steadily-on control sits at lift ≈ 1, z ≈ 0. → the signal is real; proceed to
phase 2 (poll-based note/param) and put error bars front-and-centre.

**Fail (smeared):** the planted machine doesn't separate — onset spread thin, z
low. First **retune the band**: if your reaction is slow, widen/shift
`ReactLoSec`/`ReactHiSec` (default 0.15–0.60 s) and re-run. If it still won't
separate after tuning, the manual-mark reaction smear is too large for your
setup → skip phase 2 polish and go straight to the phase-3 host deadline-trigger,
which is latency-free and uses the tight auto band instead.

Either outcome is a clean decision. Don't hand-tune past a genuine smear —
that's how the §6.4 / §15.2 traps get you.

## Tunables (defaults chosen for glitch-hunting)

| field | default | meaning |
|-------|---------|---------|
| `PreRollSec` / `PostRollSec` | 10 / 3 | window kept around each mark |
| `ReactLoSec` / `ReactHiSec` | 0.15 / 0.60 | manual-mark causal band (pre-mark) |
| `AutoTightSec` | 0.05 | phase-3 auto-trigger tight slice |
| `OverFactor` | 1.5 | `elapsed > 1.5×budget` → `over` annotation |

Ring depths: chunk 64k (~256 s @ 250/s), events 16k, cost 4k. Resize in
`FlightRecorder.cs` if a session needs longer history.

## Known limits / assumptions baked in

- **≤64 machines per session.** No-reuse burns bits on removal; a session that
  churns past 64 sets `map_exhausted=1` and drops the overflow. Widen the mask to
  `ulong[]` if that bites.
- **fill-thread labelling.** Defaults assume fill-thread OFF. With it ON the
  chunk period is fill-cadence, not ASIO (Core §41); the `over` column and
  `budget_ms` should be read as cadence-relative. The header carries `fillthread`
  so a plot can flag it.
- **Build coupling:** none in the recorder — it takes `RunContext` pre-filled by
  PP2, so 1827/1834 reflection differences stay PP2's problem, not this module's.
