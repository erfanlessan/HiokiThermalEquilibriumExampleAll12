# How Equilibrium Detection Works

This explains the algorithm behind "has this device's temperature stopped
changing?" — implemented in `EquilibriumDetector.cs` and used twice per
device run in `Program.cs` (once to decide when to stop heating, once to
decide when to stop cooling).

The short version: **equilibrium means "stable for a while," not "equal to
something."** It's a sliding-window stability test, not a comparison between
sensors or against a fixed value.

## 1. The two building blocks: span and slope

Every time `Evaluate()` runs, it looks at the last `--window-min` minutes of
readings (default **5 minutes**) for each channel it's watching, and computes
two numbers:

- **Span** — the highest reading minus the lowest reading seen anywhere in
  the window. A channel that's still trending up or down, or just noisy,
  will have a large span; a channel that's settled will have a small one.
- **Slope** — a straight-line (least-squares) best fit through all the
  points in the window, expressed in °C per minute. This catches slow,
  steady drift that might not show up as a big span yet — a channel could
  be climbing smoothly by 0.05 °C/minute for the whole window and still have
  a fairly small span, but a nonzero slope reveals it's still moving.

Equilibrium is declared only when **every** watched channel's span is at or
under `--max-span-c` (default **0.2 °C**) **and** every channel's slope
magnitude is at or under `--max-slope-c-per-min` (default **0.02 °C/min**).
A single channel failing either test fails the whole group — this is a
conjunction (AND) across every channel, not an average.

## 2. Worked example

Say `--window-min` is 5 and a device channel logs these readings (one every
minute, to keep the numbers simple) over its most recent 5-minute window:

| Minute | Reading (°C) |
|---|---|
| 0 | 79.91 |
| 1 | 79.95 |
| 2 | 79.98 |
| 3 | 80.02 |
| 4 | 80.04 |

- **Span** = 80.04 − 79.91 = **0.13 °C** → under the default 0.2 °C limit. ✅
- **Slope** ≈ (80.04 − 79.91) / 5 minutes ≈ **0.026 °C/min** → *over* the
  default 0.02 °C/min limit. ❌

Even though the span passes, this channel is still climbing steadily enough
that the slope check fails it — the whole `Evaluate()` call returns
`IsEquilibrium = false`, with this channel reported as `LimitingChannel`.
This is exactly the scenario the slope check exists for: a channel that
looks "close enough" on span alone but hasn't actually leveled off yet.

Contrast that with a device that's genuinely settled:

| Minute | Reading (°C) |
|---|---|
| 0 | 80.02 |
| 1 | 79.98 |
| 2 | 80.05 |
| 3 | 79.97 |
| 4 | 80.03 |

- **Span** = 80.05 − 79.97 = **0.08 °C** → passes.
- **Slope** ≈ **0.002 °C/min** (the ups and downs roughly cancel out over
  the window) → passes.

This channel passes both tests. If every other watched channel does too,
`Evaluate()` returns `IsEquilibrium = true`.

## 3. Why both a span check *and* a slope check?

Each one alone has a blind spot the other covers:

- **Span alone** would be fooled by a channel that's climbing slowly and
  linearly — at any given moment its span within a short window might still
  look small, even though it's clearly still heating up over a longer view.
- **Slope alone** would be fooled by a channel that's just noisy — bouncing
  up and down randomly with no net trend has a near-zero slope (the ups and
  downs cancel out in the least-squares fit) even though it's not remotely
  "settled" moment to moment.

Requiring both closes each other's gap: a channel has to be both *not
trending* and *not bouncing around* to count as stable.

## 4. What "the window" actually means in code

`EquilibriumDetector` keeps a `Queue<TemperatureSnapshot>` as its rolling
history:

```csharp
public void Add(TemperatureSnapshot snapshot)
{
    _history.Enqueue(snapshot);
    DateTimeOffset cutoff = snapshot.TimestampUtc - Window;
    while (_history.Count > 0 && _history.Peek().TimestampUtc < cutoff)
    {
        _history.Dequeue();
    }
}
```

Every time `Program.cs` reads a new sample (roughly once a second), it calls
`Add()`. The new snapshot goes on the back of the queue, and anything older
than `Window` gets dropped off the front — so at any moment, `_history`
holds "the readings from the last `Window` minutes," no more and no less.

`Evaluate()` won't declare equilibrium until that window is actually full:

```csharp
if (observed < Window - TimeSpan.FromSeconds(2))
{
    return new EquilibriumResult(false, observed, double.NaN, double.NaN, null);
}
```

This matters at the very start of a run: with only, say, 90 seconds of data
collected so far, it would be meaningless to claim "stable for 5 minutes,"
so the detector reports "not equilibrium yet" regardless of how flat those
90 seconds looked. The `TimeSpan.FromSeconds(2)` slack just absorbs the
normal jitter of polling roughly once a second — it's not a meaningful
tolerance on its own.

## 5. Two independent detectors per device run

`Program.cs` creates a *fresh* `EquilibriumDetector` for each phase of each
device, rather than reusing one across the whole run:

```csharp
EquilibriumDetector hotDetector = NewEquilibriumDetector(equilibriumChannels, options);
// ... heating loop uses hotDetector ...

EquilibriumDetector coolingDetector = NewEquilibriumDetector(temperatureChannels, options);
// ... cooling loop uses coolingDetector ...
```

They differ in which channels they watch:

- **`hotDetector`** watches only the 12 device-temperature channels
  (`equilibriumChannels`, `CH1_1`–`CH1_12`) — the ambient reading isn't part
  of the "is the device stable?" question during heating.
- **`coolingDetector`** watches all 13 temperature channels
  (`temperatureChannels`) — device channels *and* the ambient reference —
  because the cooling phase cares whether the whole system, including the
  room-temperature baseline, has settled.

Both are governed by the same `--window-min`, `--max-span-c` and
`--max-slope-c-per-min` settings; only the channel list differs.

## 6. "Equilibrium" is necessary but not sufficient to move on

`EquilibriumDetector.Evaluate()` only ever answers one question: *is
everything it's watching currently stable?* The two loops in `Program.cs`
each combine that answer with one or two extra conditions before actually
changing phase:

**Ending the heating phase** (`Program.cs`):
```csharp
if (equilibrium.IsEquilibrium && triggerTemperature >= options.TriggerTemperatureC)
```
Stability alone isn't enough — a device sitting stably at room temperature
before heating even starts would otherwise "pass." The trigger-temperature
check (`--trigger-c`, default 30 °C) makes sure it's also actually hot.

**Ending the cooling phase** (`Program.cs`):
```csharp
if (minimumCoolingElapsed && coolingEquilibrium.IsEquilibrium && ambientReached)
```
Here stability is combined with a minimum elapsed time (`--min-cooling-min`)
and a closeness-to-ambient check (`--ambient-tolerance-c`) — a device could
in principle be "stable" while still sitting well above room temperature
(e.g. stuck at a plateau partway through cooling), so `ambientReached` makes
sure it's actually cooled down, not just stopped changing for a moment.

## 7. Tuning the sensitivity

All four numbers are CLI flags, so you can loosen or tighten the test
without touching code:

| Flag | Default | Effect of increasing it |
|---|---|---|
| `--window-min` | 5 | Requires stability over a longer period; slower to trigger, less prone to reacting to a brief lull. |
| `--max-span-c` | 0.2 | Tolerates noisier/wider-swinging readings as "stable." |
| `--max-slope-c-per-min` | 0.02 | Tolerates a channel that's still drifting somewhat as "stable." |
| `--trigger-c` | 30 | Requires the device to reach a higher temperature before heating stops (heating-phase only). |
| `--ambient-tolerance-c` | 1 | Allows the next device to start while still further from room temperature (cooling-phase only). |

Loosening any of the first three makes equilibrium easier (faster) to reach,
at the cost of accepting a less strictly "settled" reading as good enough.
