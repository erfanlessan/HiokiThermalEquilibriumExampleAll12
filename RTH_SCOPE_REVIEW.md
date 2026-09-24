# RTH scope integration review

This review compares the supplied legacy scope source with the supplied
R&S Scope Rider RTH user/SCPI manual, edition 06.

## Capture sequence implemented

1. The LR8400 continues its 10 Hz temperature recording and evaluates thermal
   equilibrium across all 13 channels.
2. When all channels are stable and `CH1_1` is at least the configured
   temperature, every connected RTH is placed in single-shot mode and started.
3. The program waits long enough to fill the configured pre-trigger portion.
4. It records the current LR8400 point and sends the Arduino heater-off command.
5. Each RTH triggers on the measured electrical falling edge, not on LAN command
   timing.
6. After the complete post-trigger portion, each scope is stopped and exported
   independently. A failed second scope does not prevent use of the first.
7. The LR8400 continues collecting the 20-minute cooling record.

## Relevant RTH commands

| Purpose | Command | Manual location |
|---|---|---:|
| 500 ms full record | `TIMebase:RANGe 0.5` | p. 195 |
| Trigger in screen centre | `TIMebase:REFerence 50` | p. 195 |
| No additional time shift | `TIMebase:HORizontal:POSition 0` | p. 195 |
| Sample acquisition | `ACQuire:MODE SAMPle` | p. 196 |
| Acquisition sample rate | `ACQuire:POINts:ARATe?` | p. 196 |
| One real-trigger waveform | `TRIGger:MODE SINGle` | pp. 198-199 |
| Edge trigger | `TRIGger:TYPE EDGE` | p. 199 |
| Falling edge | `TRIGger:EDGE:SLOPe NEGative` | p. 201 |
| Trigger hysteresis | `TRIGger:MNR ON` | p. 201 |
| Start acquisition | `RUN` | p. 196 |
| Freeze acquisition | `STOP` | p. 196 |
| Query actual interval | `ACQuire:RESolution?` | p. 198 |
| Query point count | `ACQuire:POINts:VALue?` | p. 197 |
| CSV waveform export | `EXPort:WAVeform:*`, `MMEMory:DATA?` | pp. 303-304 |

At 500 ms total and a 50% reference, the waveform contains 250 ms before and
250 ms after the trigger. The 300 ms pre-fill delay provides a 50 ms margin. The
350 ms post-trigger wait provides a 100 ms margin before `STOP` and export.
The controller sets every RTH analogue input to a 1:1 voltage probe and
200 mV/div, and prints the queried acquisition rate, waveform point interval and
50 ms/div timebase after configuration.

## 125 us interval

The manual exposes `ACQuire:RESolution?` and `ACQuire:POINts:VALue?` as queries,
not settings. It therefore does not provide a documented way to request exactly
125 us between stored scope samples. The implementation preserves the native
RTH CSV, checks the actual interval, and makes a second CSV on an exact 125 us
time grid. Resampling is refused when the native interval is coarser than
125 us, because interpolation cannot recover missing transient detail.

## Trigger level

The scope trigger level is in volts on `C1`-`C4` (or the appropriate digital
threshold when using `D0`-`D7` with option RTH-B1). It is not the 30 deg C LR8400
temperature criterion. The equilibrium detector determines **when to arm**;
the RTH falling edge determines the electrical heater-disconnection instant.

For an analogue current-sense or control signal, choose a level that satisfies:

```text
disconnected baseline + noise margin < trigger level
trigger level < lowest expected heating-state value - noise margin
```

Use at least several peak-to-peak noise amplitudes as margin and confirm the
level with a short bench capture. `TRIGger:MNR ON` adds hysteresis, but it does
not compensate for a trigger level placed inside the steady-state noise band.

## Problems in the supplied legacy methods

| Legacy behaviour | Issue | Revised behaviour |
|---|---|---|
| `TRIG:MODE AUTO` | Can create timeout triggers without the heater edge | `TRIGger:MODE SINGle` |
| No edge-slope command | Reset/default direction is positive | Explicit `NEGative` |
| `TIMebase:SCALe 1` | Ten-second record, much longer than the transient requirement | 500 ms full record |
| `TIMebase:HORizontal:POSition 10` | Parameter is seconds, not percent | Position 0 plus reference 50% |
| History segments | Unnecessary for one heater-off transient; also requires RTH-K15 | One single-shot waveform |
| Exactly two instrument fields | Second scope is mandatory | Collection of successfully connected scopes |
| `CHANnel<n>:DATA?;*OPC?` | Mixes a binary result and an ASCII query result | Documented file export, separate commands |
| `FORMat:DATA`, `FORMat:BORDer`, `CHANnel<n>:DATA?` | Not documented in the supplied manual | Not used |
| `ACQuire:POINts:PRESelect MAX` | Not documented in the supplied manual | Query actual resolution and point count |

The RTH manual's command-sequencing appendix also recommends separate program
messages when order matters. Configuration commands are consequently sent one
per message.

## Still absent from the uploaded legacy source

The uploaded `.csproj` references several source files that were not uploaded,
including the base `RTH1004Controller.cs`, `ScopeSetupServiceDual.cs`,
`WindowParse.cs`, `WindowParse.DataMatrixFlatten.cs`, and the `DataProcessing`
partial files. Those are required to rebuild the old project itself. The new
RTH implementation is self-contained and does not depend on those missing
partials.

## First bench test

1. Disconnect the power stage or use a safe dummy load.
2. Confirm that 1:1 probes and 200 mV/div are suitable for the connected signals,
   and check the offsets and safe voltage category on the instruments.
3. Run with one scope and a conservatively placed trigger level.
4. Confirm that the native CSV time column spans about -250 ms to +250 ms and
   that the falling edge is near time zero.
5. Compare the queried native interval printed by the program with the native
   CSV header and inspect the generated 125 us CSV.
6. Only then connect the second scope and finally the real heater hardware.
