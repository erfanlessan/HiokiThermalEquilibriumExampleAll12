# Program.cs — Beginner's Guide

This document walks through `Program.cs` in more depth than the in-code
comments. It assumes working knowledge of C# (loops, async/await, records,
LINQ) but no prior knowledge of this project, the Hioki LR8400, or the
electronics being tested.

If you just want to run the program, read `README.md` instead — this file
is about *how the code works*, not *how to use it*.

## 1. What this program actually does

It automates a repeated test cycle on up to twelve power-semiconductor
devices (six IGBTs and six diodes/FRDs, arranged as U/V/W phases × upper/lower
position). For each device, in order, the program:

1. **Heats** the device (via an Arduino-controlled relay board) until its
   temperature has stopped changing ("hot equilibrium").
2. **Switches the heater off** at that exact moment, optionally triggering
   one or two oscilloscopes to capture the electrical switch-off transient.
3. **Cools** the device, continuing to record, until it's back near room
   temperature.
4. **Saves** everything — a CSV of every recorded channel, SVG plots, and
   (if scopes were used) their waveform captures — into its own folder.
5. Moves on to the next device, or finishes if this was the last one.

Three pieces of hardware are involved:

| Hardware | Role | Represented by |
|---|---|---|
| Hioki LR8400 | 10 Hz data logger recording all temperature/voltage channels | `Lr8400Client` (in `HiokiLr8400.cs`, not part of this repo — linked in from a sibling folder) |
| Arduino + relay board | Switches heating current to one device at a time | `ArduinoHeaterController` (`ArduinoHeaterController.cs`) |
| R&S Scope Rider RTH (optional) | Captures the fast electrical edge at switch-off | `RthScopeCoordinator` (`RthScopeCapture.cs`) |

The RTH scopes are entirely optional — if you don't pass `--scope1`/`--scope2`
on the command line, every scope-related block in `Program.cs` is skipped and
the program runs fine without one connected.

## 2. The channel map

The LR8400 has two "units" of analogue inputs. This program uses them like this:

| Channels | Configured as | Purpose |
|---|---|---|
| `CH1_1` – `CH1_12` | Thermocouples | One per heated device position — these are the "device temperature" readings the equilibrium logic watches. |
| `CH2_1` – `CH2_6` | Voltage inputs, 2 V range | Phase-voltage sensing at each of the six physical positions (UU, UL, VU, VL, WU, WL). |
| `CH2_7` | Thermocouple | **Ambient/room reference temperature.** Despite living on the "voltage" unit, it's configured as a thermocouple. Used to decide when a device has cooled back down enough to safely start the next one. |
| `CH2_8` | Voltage input, 4 V range | Device thermistor voltage monitoring (added separately from the other six voltage channels because it uses a different range). |

In the code, these are built as small string arrays near the top of `RunAsync`:

```csharp
string[] equilibriumChannels = ...  // CH1_1..CH1_12
string[] voltageChannels = ...      // CH2_1..CH2_6 + CH2_8
const string ambientChannel = "CH2_7";
string[] temperatureChannels = [.. equilibriumChannels, ambientChannel];      // all 13 temperature readings
string[] recordingChannels = [.. equilibriumChannels, .. voltageChannels, ambientChannel]; // everything recorded
```

`recordingChannels` is what actually gets configured on the LR8400 and
downloaded — it's the union of everything above. The other three lists
(`equilibriumChannels`, `temperatureChannels`, `voltageChannels`) are
*subsets* used to control which channels a particular piece of logic looks at
(e.g. only device temperatures feed the "is it hot enough yet?" check, not
the ambient channel or the voltages).

## 3. Program flow, step by step

### 3.1 Startup (before the per-device loop)

1. `Options.Parse(args)` turns the command line into a strongly-typed
   `Options` record. Any bad argument throws, which `RunAsync` catches and
   turns into a usage message (exit code 2).
2. The channel arrays and labels above are built.
3. Output paths are set up under `--output` (default: a timestamped folder
   under `HiokiRuns`), and `live-dashboard.html` is written once — this is a
   small self-refreshing HTML page you can leave open in a browser to watch
   temperature/voltage plots update roughly every `--plot-refresh-s` seconds.
4. A `CancellationTokenSource` is created and wired to Ctrl+C, so pressing it
   requests a graceful stop (see §4) rather than abruptly killing the process
   mid-heat.
5. The Arduino and LR8400 are opened/connected.
6. Unless `--preserve-input-settings` was passed, every analogue channel is
   configured: thermocouples on `CH1_1..CH1_12` + `CH2_7`, voltage inputs on
   `CH2_1..CH2_6` (2 V) and `CH2_8` (4 V, configured separately since it uses
   a different range).
7. If any `--scope1`/`--scope2` arguments were given, the RTH scope(s) are
   connected and configured here too.

### 3.2 The per-device loop

`Program.cs` loops over `plan` (either all twelve devices, or the single one
requested with `--device`). Each iteration is three phases:

**Heating phase** — the `while (true)` loop that starts after
`"HEATING ON"` is logged:
- Every ~1 second, it reads one live snapshot of every recording channel
  (`ReadSnapshotAsync`) and feeds it into an `EquilibriumDetector` (see §5)
  that's watching only the 12 device-temperature channels.
- It also resolves a single "trigger temperature" — either the single
  hottest device channel (`HOTTEST`, the default for `--all-devices`) or one
  specific channel (default `CH1_1` for a single-device run).
- Loop exits (moves to cooling) once **both** are true: the equilibrium
  detector says the device temperatures have been stable for the whole
  observation window, **and** the trigger temperature is at or above
  `--trigger-c` (default 30 °C). If temperatures are stable but still below
  that threshold, it logs one warning and keeps heating rather than stopping
  early.
- At that moment: any connected RTH scopes are armed and given time to fill
  their pre-trigger memory, the current LR8400 sample index is recorded, and
  *then* the Arduino heater output is switched off. The scope itself triggers
  on the real electrical edge it sees on its input, not on this software
  command — the ordering here just needs to have the scope's memory already
  filling before the edge happens.
- Two independent timeouts protect this phase: `--max-heating-min` (a
  program-side clock) and `--safety-max-c` (an absolute temperature ceiling,
  checked every iteration via `CheckSafetyLimit`).

**Cooling phase** — same polling pattern, but now watching all 13
temperature channels (device channels **and** ambient), and computing how far
the *worst* device channel currently is from the ambient reading. The loop
exits once all of these are true: `--min-cooling-min` has elapsed, the
temperatures have been stable for the equilibrium window, and the worst
device-to-ambient gap is within `--ambient-tolerance-c`. `--max-cooling-min`
is the safety timeout here, mirroring `--max-heating-min` above — if a device
never reaches ambient equilibrium (e.g. a sensor fault), the batch stops
rather than energising the next device on top of a device that's still hot.

**Export phase** — the LR8400 is stopped, its full stored recording for this
run is downloaded, and three kinds of output are written into
`<output>/<run-folder>/`:
- `lr8400-all-channels.csv` — every recorded channel, one row per 100 ms sample.
- `temperatures.svg` / `voltages.svg` — static plots of the full recording.
- `rth-captures/` (only if a scope was armed) — the oscilloscope's own
  waveform CSV(s) and plot, from `ExportScopeCaptureAsync`.

The live SVGs and status text are refreshed one more time before moving to
the next device, so the dashboard always reflects the most recently
completed (or in-progress) run.

### 3.3 Shutdown

Whether the batch finishes normally, is cancelled with Ctrl+C, or an
exception is thrown partway through, the `finally` block at the bottom of
`RunAsync` always runs. It unconditionally commands the Arduino to switch all
heater outputs off, and — if a recording was in progress — stops the LR8400
cleanly. This is the single most important safety property of the program:
**no matter how it exits, the heater does not stay on.**

## 4. Cancellation (Ctrl+C)

`Console.CancelKeyPress` is wired near the top of `RunAsync`. The first
Ctrl+C sets `eventArgs.Cancel = true` (which stops the .NET runtime from
killing the process immediately) and cancels the shared `CancellationToken`.
Every `await` in the main loops checks that token, so the current iteration
finishes cleanly and the code unwinds through the `finally` block — heater
off, LR8400 stopped. A second Ctrl+C would bypass this and force-kill the
process, which is why the console message says to only do that if an
instrument seems unresponsive.

## 5. Equilibrium detection (`EquilibriumDetector.cs`)

This is the algorithm behind "has this device's temperature stopped
changing?" It's a sliding-window stability test, not a comparison between
sensors:

- It keeps a rolling queue of the last `--window-min` minutes of snapshots.
- On each `Evaluate()` call, for every channel it's watching, it computes:
  - **Span**: the difference between the highest and lowest value seen in
    the window.
  - **Slope**: the least-squares linear regression slope over the window,
    converted to °C per minute.
- It only reports equilibrium once the window is *full* (enough time has
  actually elapsed) **and** every channel's span is within `--max-span-c`
  **and** every channel's slope magnitude is within `--max-slope-c-per-min`.

Two separate `EquilibriumDetector` instances are created per device run —
one for the heating phase (watching just the 12 device channels) and a fresh
one for the cooling phase (watching all 13 temperature channels, including
ambient) — because they're evaluating different questions at different times
with independent windows.

## 6. The Arduino heater routing (`ArduinoHeaterController.cs`)

Each device is heated by closing a specific combination of relays. The
`HeaterRouting.Build(device, monitorRelay)` method looks up the right
combination of `ArduinoOutput` flag bits for the requested `HeaterDevice`,
then optionally OR's in either the `IgbtFlag` or `DiodeFlag` bit depending on
`monitorRelay`. All twelve entries in `DeviceRun.All` currently set
`MonitorRelaySelection.None`, so that flag bit is never set right now — only
the base device-routing bits (and later the `AssemblySwitch` "turn heating on"
bit) go out over serial.

The whole bit-mask is sent to the Arduino as a single decimal number in a
`S<number>\n` command, and the firmware is expected to echo back
`Received: S<number>` — if it doesn't, `SendMaskAsync` throws, which
propagates up and triggers the same safe-shutdown `finally` block.

## 7. CLI options reference

Run `dotnet run -- --help` to see the full, current list with defaults — the
`PrintUsage()` method inside the `Options` record is the single source of
truth and is kept in sync with what `Parse()` actually accepts. A few of the
less obvious ones:

- `--trigger-channel HOTTEST` — for `--all-devices` batches, `HOTTEST` picks
  whichever `CH1_*` channel is currently the highest reading, so you don't
  have to know in advance which physical thermocouple corresponds to which
  device. For a single `--device` run it defaults to the fixed `CH1_1`.
- `--min-cooling-min 20` — a *minimum*, not a fixed wait: the program still
  waits for stability and ambient-tolerance on top of this, so it can take
  longer than 20 minutes but never less (unless you set it to `0`).
- `--safety-max-c` — has no default; strongly recommended for anything other
  than a fully attended bench test, since without it the only overtemperature
  protection is the equilibrium/trigger logic itself.
- `--preserve-input-settings` — skips all LR8400 channel (re)configuration on
  startup, trusting whatever sensor/range/scaling settings are already saved
  on the instrument.

## 8. Glossary

- **Equilibrium** — in this program, "stable for a while," not "equal to
  something else." See §5.
- **Trigger channel / trigger temperature** — the single temperature reading
  used to decide when a device is "hot enough" to switch heating off.
- **Ambient tolerance** — how close (in °C) every device channel must be to
  the ambient reference channel before the next device can be energised.
- **Monitor relay flag** — an extra bit sent to the Arduino alongside the
  device routing bits, historically used to select an IGBT- or diode-specific
  monitor relay. Currently disabled (`None`) for every device.
- **RTH** — Rohde & Schwarz Scope Rider, the (optional) oscilloscope model
  used to capture the fast switch-off transient. See `RTH_SCOPE_REVIEW.md`
  for the SCPI command details.
- **Live vs. final plots** — the `live-*.svg` files are overwritten
  continuously while a run is in progress (for the dashboard); the
  per-run `temperatures.svg`/`voltages.svg` files are written once, at the
  end of each run's export phase, from the fully downloaded recording.
