# Pedal Profiler2

A single-machine focused profiler for [ReBuzz](https://github.com/Buzztracker/ReBuzz).

Companion to **Pedal Profiler** (v1, the global dashboard). Where v1 shows everything in the song at low resolution, v2 picks **one machine** and shows it in depth.

## What you get

A separate, resizable inspector window with:

- **Machine selector** at the top (dropdown + prev/next buttons). Pick any machine in the song. Selection persists across song save/load via `MachineState` — reopen the song and your last-inspected machine comes back.
- **Buffer info line** at the top of the Cost panel: `Buffer: 5.33 ms (256 spl @ 48.0 kHz · 64-buf avg)`. This is the budget every cost number is measured against — every audio buffer must finish in this much wall-clock time or you drop samples.
- **Cost panel** — three numbers, each in milliseconds per buffer with the percentage of buffer budget alongside:
  - **Engine** — *live, continuous, non-invasive.* Read directly from ReBuzz's own `MachinePerformanceData.PerformanceCount` / `SampleCount` counters via reflection. Delta-sampled every UI tick and exponentially smoothed (~1-second time constant). This is the actual DSP cost of the machine as the engine measures it. No muting required, no perturbation of playback.
  - **Solo** — snapshot via mute-all-others. What the machine costs *on its own* if everything else is silent. Includes residual host overhead. A sub-line shows the peak single-buffer cost and the median of the last 5 solo readings.
  - **Marginal** — snapshot via mute-target. What muting *this machine in the current mix* would save. Differential with the constant overhead cancelled out.
- **Sparkline** of the measured cost over time, alongside Measure Solo and Measure Marginal buttons.
- **Connection map** — one-hop upstream and downstream neighbors as clickable chips. Click any neighbor to switch focus to it (navigation through the signal chain).
- **Spike attribution** — for the last 8 captured xruns, how many of them were happening *while this machine was active*. A machine showing "active during 7 of last 8 spikes (87%)" is a strong suspect — combined with the engine cost (which tells you whether that "active during all spikes" is plausibly causal) it triangulates the culprit. **Expand the spike list** for per-spike detail: timestamp, BPM, spike duration, and which machines were active at that moment (selected machine highlighted in red, row tinted when present).
- **Parameter table** — every global parameter with its current value and a "writes per second" rate. Rows flash when the value changes, so you can see what's being automated.
- **Activity** — type tag (GEN / FX / CTRL) and track count.
- **Profile All Machines** — two modes:
  - **Profile All (Engine)** — instant snapshot of every non-control machine using engine-reported counters. ~1 second total, no muting. Preferred.
  - **Profile All (Solo)** — invasive sweep using solo measurement. ~2 s per machine. Use when engine data is unavailable or when you want the "with-overhead" picture.
  Both produce a sorted bar chart scaled against the buffer budget; rows are clickable to focus.
- **Diagnostics — "Dump Internals (DC)" button** — walks `IBuzz`, the selected machine's `MachineCore`, and a parameter's `ParameterCore` via reflection and dumps every reachable public + non-public property and field to the ReBuzz Debug Console (which it also opens automatically). Useful for mapping what engine internals are accessible without touching live state — read-only, no modifications. Helps decide what's worth exposing in future iterations.
- **Global status footer** — overall CPU %, dropouts, spikes, so you don't lose situational awareness while focused on one machine.

## Solo vs Marginal — which number do you want?

These two cost measurements answer different questions, and the gap between them is informative on its own.

**Solo** tells you what the machine costs to render in isolation. Everything else gets muted, the machine runs alone, and you read the resulting "other-machines time" from the audio thread. The number includes the residual cost of the still-running-but-muted machines and host overhead, so it's not the machine's true cost in isolation — it's "what the audio buffer spends on work when this is the only unmuted machine".

**Marginal** tells you what muting this machine *in the current mix* would save. Captures the current cost, mutes the target only, captures again, subtracts. The differential cancels out the constant overhead, so it's a truer "what does this machine contribute to the running song".

If solo is much higher than marginal, the bulk of the solo number is host/muted-machines overhead, not the target. If solo ≈ marginal, the target really is most of the work being done. Either way, **marginal is the number to optimize against** — that's the CPU you'd actually reclaim by bypassing or pre-rendering this machine.

## How it works

It's a control machine (`void Work()`), so ReBuzz calls it first in every audio buffer, before all generators and effects. The gap between this `Work()` ending and the next `Work()` starting is precisely the time every other machine in the song consumed. That gives global CPU measurement for free, same as v1.

The two new tricks v2 adds:

**Spike attribution.** When a spike is captured (other-machines time exceeds 150% of the rolling baseline period), the audio thread also stamps in a snapshot of which machines were active at that moment. The active-machines list is published from the UI thread every 100 ms by enumerating `Song.Machines` and filtering out muted entries, so the audio thread doesn't have to touch the (UI-only) machine list. Over time, a machine that's active during most spikes is statistically the likely culprit.

**Solo and Marginal measurement.** A shared UI-thread state machine: save all current mute states, mutate to the target configuration (mute-all-others for Solo, mute-only-target for Marginal), wait 1.5 s for the averaging accumulators to settle, snapshot the relevant numbers, restore mute states. Total cycle ~2.5 s per measurement.

## Measurement noise — what to expect

A single Solo reading typically varies ±5–15% run to run because of OS scheduling, GC pauses, momentary CPU pressure, and the relatively short 1.5 s averaging window. The displayed median of the last 5 readings is much more stable than the latest — when you need a hard number, press Measure Solo five times and read the median, not the latest.

The numbers are wall-clock time, not CPU cycles. A 0.5 ms OS preemption during a 1.5 s measurement window will show up as ~0.03 ms of inflated cost. This makes the measurement realistic for dropout prediction (the soundcard cares about wall-clock deadlines) but noisier than a true profiler.

## Coexistence with v1

v2 is a separate machine and DLL — `Pedal Profiler2.NET.dll`. You can run both v1 and v2 in the same song. v1 gives you the at-a-glance dashboard; v2 lets you drill into one machine. The only interaction is that v2's measurement state machines temporarily perturb the global CPU readings while they run (because they're actively muting machines). Both profilers' numbers return to normal once the measurement completes.

## Building

```
dotnet build PedalProfiler2.csproj -c Release
```

Build output deploys to `C:\Program Files\ReBuzz\Gear\Generators\Pedal Profiler2.NET.dll` via the post-build `DeployToReBuzz` target. Only the DLL is produced (no `.pdb`, no `.deps.json`; ReBuzz doesn't use them). The Copy is `ContinueOnError="true"` so if ReBuzz is running and holds the DLL open, the build still succeeds — close ReBuzz and rebuild to refresh.

On next ReBuzz startup, the machine appears in the Generators browser tab as **Pedal Profiler2**.

## Parameters

| Name | Range | Default | Purpose |
|---|---|---|---|
| Window | 16–128 | 64 | Buffers per averaging window. Smaller = more responsive, noisier. Larger = smoother, slower. |

## Caveats

- **Measurements are invasive while they run** — Solo briefly mutes everything else; Marginal briefly mutes the target. Do not use during a take.
- **Spike attribution is correlational, not causal.** A machine active during all spikes might just *always* be active. Cross-check with solo + marginal on the suspect — 100% attribution paired with a meaningful marginal cost is evidence; 100% attribution paired with a tiny marginal is just "this machine is always on".
- **The active-machines list is up to 100 ms stale** at spike capture time. For typical spikes (5–50 ms inside a busy DSP graph) this is fine; for sub-100 ms toggle automation it's not.
- **Selection state is session-only.** No persistence across song save/load.
- **Don't toggle mutes manually while Profile All is running** — your toggle and its toggle will fight each other.

## License

MIT — see [LICENSE](LICENSE).
