# Hioki LR8400 twelve-device thermal-characterisation runner

This .NET 8 console program controls the Hioki LR8400, the supplied Arduino
relay/current-source controller, and one or two R&S Scope Rider RTH instruments.
It can run all twelve IGBT/FRD characterisations unattended while keeping the
heater shutdown path fail-safe.

## Measurement channels

The LR8400 records every selected channel at 10 Hz (100 ms):

| Channel group | Configuration |
|---|---|
| `CH1_1` to `CH1_13` | Device thermocouples; all participate in hot equilibrium |
| `CH2_1` | UU voltage |
| `CH2_2` | UL voltage |
| `CH2_3` | VU voltage |
| `CH2_4` | VL voltage |
| `CH2_5` | WU voltage |
| `CH2_6` | WL voltage |
| `CH2_7` | Ambient thermocouple |

Unless `--preserve-input-settings` is used, the six voltage channels are put in
voltage mode with scaling off and the **2 V range**. The LR8400 manual lists
that range with approximately -3.2768 V to +3.2767 V usable before over-range,
so it accommodates either-polarity signals whose magnitude is below 2 V. The
temperature channels default to type K, 100 °C range, internal RJC and
disconnection detection.

## Batch order and Arduino modes

`--all-devices` runs this fixed order:

| Run folder | Heated device | Automatically selected monitor flag |
|---|---|---|
| `01_UU_IGBT` | U upper IGBT | IGBT |
| `02_UU_FRD` | U upper diode/FRD | Diode |
| `03_UL_IGBT` | U lower IGBT | IGBT |
| `04_UL_FRD` | U lower diode/FRD | Diode |
| `05_VU_IGBT` | V upper IGBT | IGBT |
| `06_VU_FRD` | V upper diode/FRD | Diode |
| `07_VL_IGBT` | V lower IGBT | IGBT |
| `08_VL_FRD` | V lower diode/FRD | Diode |
| `09_WU_IGBT` | W upper IGBT | IGBT |
| `10_WU_FRD` | W upper diode/FRD | Diode |
| `11_WL_IGBT` | W lower IGBT | IGBT |
| `12_WL_FRD` | W lower diode/FRD | Diode |

The route masks are based on `PowerRelayConfig` from the supplied legacy code.
Verify them against the physical relay wiring before allowing an unattended
batch to energise hardware.

For each device the program:

1. sends Arduino `S0`, clears LR8400 memory, and starts a fresh 10 Hz recording;
2. selects the device route and the correct IGBT/diode monitor flag;
3. waits for relay settling, then enables the current source;
4. waits until all 13 device temperatures satisfy the equilibrium test and the
   trigger-temperature condition is met;
5. arms every connected RTH, fills its pre-trigger memory, then switches the
   heater off to create the real falling edge;
6. saves each RTH native CSV, 125 µs normalised CSV, and SVG;
7. keeps the LR8400 recording until at least 20 minutes have passed **and** all
   temperature channels are stable and within 1 °C of ambient;
8. stops and downloads all retained LR8400 samples, then saves CSV and SVGs;
9. resets the live plots before proceeding to the next device.

The program never advances merely because a fixed cooling delay expired. If
ambient equilibrium is not reached within `--max-cooling-min`, it stops the
batch without energising the next device.

## Equilibrium rules

The default operational definition is a five-minute window in which every
applicable temperature channel has:

- a maximum-minus-minimum span no greater than 0.2 °C; and
- an absolute least-squares slope no greater than 0.02 °C/min.

For hot equilibrium the batch additionally requires the hottest `CH1_*`
temperature to be at least 30 °C. This `HOTTEST` default avoids assuming which
thermocouple corresponds to each of the twelve devices. If the physical sensor
mapping calls for a fixed channel, use `--trigger-channel CH1_#`.

For cooling equilibrium, the same stability test includes the ambient channel,
and every `CH1_*` temperature must be within `--ambient-tolerance-c` of `CH2_7`.
These are example engineering criteria; validate them for the assembly, thermal
time constants and thermocouple uncertainty.

## Run all twelve devices

PowerShell example:

```powershell
dotnet run --project .\HiokiThermalEquilibriumExample.csproj -- `
  --host 192.168.1.101 `
  --arduino-port COM9 `
  --all-devices `
  --safety-max-c 80 `
  --scope1 "TCPIP::192.168.1.100::INSTR" `
  --scope1-trigger-source C4 `
  --scope1-trigger-level-v 0.95 `
  --scope-channels 1,2,3,4
```

Add a second scope independently:

```text
--scope2 TCPIP::192.168.3.100::INSTR --scope2-trigger-source C4 --scope2-trigger-level-v 0.91
```

If two RTH resources are configured and one cannot connect, the program keeps
using the available scope. If none can connect, it aborts before heating.

The default RTH record is 500 ms at 50% trigger reference: 250 ms before and
250 ms after the heater-off edge, or 50 ms/div. All four analogue inputs use
1:1 probes and 200 mV/div. The program prints the queried acquisition sample
rate, waveform point rate, sample interval and time/div after configuration.

The RTH trigger level is an electrical voltage, not a temperature. Choose it
from measured steady-state and disconnected levels with sufficient noise
margin. Noise rejection is enabled and the slope is negative.

## Quick one-device trial

```powershell
dotnet run --project .\HiokiThermalEquilibriumExample.csproj -- `
  --host 192.168.1.101 `
  --arduino-port COM9 `
  --device IgbtUUpper `
  --window-min 0.25 `
  --min-cooling-min 0 `
  --max-heating-min 10 `
  --max-cooling-min 10 `
  --safety-max-c 45
```

The shortened equilibrium window is only a communications/plot trial; it is
not a valid thermal-characterisation setting.

## Live dashboard and output

Open `live-dashboard.html` in the batch output folder. It refreshes these fixed
files every five seconds:

- `live-temperatures.svg`
- `live-voltages.svg`
- `live-status.txt`

The HTML itself never changes. At the end of each device run the live plots are
copied into that run's folder, final 10 Hz plots are generated from downloaded
LR8400 memory, and the root live plots are replaced with placeholders before
the next device begins.

Each device folder contains:

- `lr8400-all-channels.csv`
- `temperatures.svg` and `voltages.svg` from the 10 Hz logger data
- `temperatures-live.svg` and `voltages-live.svg` from one-second monitoring
- `rth-captures/RTH*-native.csv`
- `rth-captures/RTH*-125us.csv`
- `rth-captures/RTH*-125us.svg`

`TimeFromTrigger_s = 0` in the LR8400 CSV corresponds to heater switch-off.
The CSV covers the full retained device run, from just before heating until
ambient equilibrium. If LR8400 internal memory wraps during an unusually long
run, only the instrument's retained interval can be downloaded.

## Important options

```text
--trigger-channel HOTTEST
--trigger-c 30
--window-min 5
--max-span-c 0.2
--max-slope-c-per-min 0.02
--ambient-tolerance-c 1
--min-cooling-min 20
--max-cooling-min 180
--max-heating-min 120
--safety-max-c 80
--plot-refresh-s 5
--output HiokiRuns\MyBatch
```

Use a new output directory for each batch. CSV saving is intentionally
non-overwriting so an existing characterisation cannot be silently replaced.

## Safety

Every normal completion, exception and Ctrl+C path attempts to send Arduino
`S0`. Heating and cooling both have software timeouts. A software shutdown is
not a substitute for an independent hardware overtemperature interlock,
current limit, emergency stop and Arduino communications watchdog.
