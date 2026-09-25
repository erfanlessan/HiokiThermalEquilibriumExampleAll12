// Batch thermal-characterisation runner for the Hioki LR8400 data logger.
// Full narrative walkthrough: see PROGRAM_GUIDE.md in the repository root.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using GAVIMDataProcessing.Instrumentation;
using HiokiThermalEquilibriumExample;

return await RunAsync(args);

// Entry point for the whole program. Parses CLI options, connects to the
// instruments, then runs one heating/cooling cycle per device in the plan.
static async Task<int> RunAsync(string[] args)
{
    Options options;
    try
    {
        options = Options.Parse(args);
    }
    catch (Exception exception)
    {
        // Bad arguments: print the error plus usage, then exit without touching hardware.
        Console.Error.WriteLine(exception.Message);
        Options.PrintUsage();
        return 2;
    }

    if (options.ShowHelp)
    {
        Options.PrintUsage();
        return 0;
    }

    // --- LR8400 channel map -------------------------------------------------
    // Unit 1 (CH1_1..CH1_12): one thermocouple per heated device.
    // Unit 2 (CH2_1..CH2_6): phase-voltage sensing, one per physical position.
    // CH2_7: ambient reference thermocouple. CH2_8: thermistor voltage input.
    string[] equilibriumChannels = Enumerable.Range(1, 12).Select(n => $"CH1_{n}").ToArray();
    string[] voltageChannels = [.. Enumerable.Range(1, 6).Select(n => $"CH2_{n}"), "CH2_8"];
    const string ambientChannel = "CH2_7";
    string[] temperatureChannels = [.. equilibriumChannels, ambientChannel];
    string[] recordingChannels = [.. equilibriumChannels, .. voltageChannels, ambientChannel];
    // Friendly column headings used in the CSV export and SVG plot legends.
    Dictionary<string, string> channelLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CH2_1"] = "CH2_1_UU_V",
        ["CH2_2"] = "CH2_2_UL_V",
        ["CH2_3"] = "CH2_3_VU_V",
        ["CH2_4"] = "CH2_4_VL_V",
        ["CH2_5"] = "CH2_5_WU_V",
        ["CH2_6"] = "CH2_6_WL_V",
        [ambientChannel] = "CH2_7_Ambient_degC",
        ["CH2_8"] = "CH2_8_Thermistor_V"
    };

    TimeSpan sampleInterval = TimeSpan.FromMilliseconds(100);
    TimeSpan livePollInterval = TimeSpan.FromSeconds(1);
    string outputRoot = Path.GetFullPath(options.OutputDirectory);
    Directory.CreateDirectory(outputRoot);
    // These three files are overwritten continuously while a run is in progress,
    // so a browser tab open on live-dashboard.html shows near-real-time status.
    string liveTemperaturePath = Path.Combine(outputRoot, "live-temperatures.svg");
    string liveVoltagePath = Path.Combine(outputRoot, "live-voltages.svg");
    string liveStatusPath = Path.Combine(outputRoot, "live-status.txt");
    WriteLiveDashboard(Path.Combine(outputRoot, "live-dashboard.html"));

    // The run plan is either all twelve devices or a single device (see Options.BuildRunPlan).
    IReadOnlyList<DeviceRun> plan = options.BuildRunPlan();
    // Empty when no --scope1/--scope2 were supplied; every RTH-related block below
    // is skipped in that case, so the program works fine without any scope attached.
    IReadOnlyList<RthScopeDefinition> scopeDefinitions = options.BuildScopeDefinitions();
    RthCaptureSettings scopeSettings = new(
        options.ScopeTimeRangeMilliseconds / 1000.0,
        options.ScopeTriggerReferencePercent,
        TimeSpan.FromMicroseconds(options.ScopeOutputIntervalMicroseconds),
        TimeSpan.FromMilliseconds(options.ScopePreTriggerFillMilliseconds),
        TimeSpan.FromMilliseconds(options.ScopePostTriggerWaitMilliseconds));

    // Ctrl+C requests a graceful stop instead of killing the process outright,
    // so the finally block below still gets a chance to switch the heater off.
    using CancellationTokenSource cancellation = new();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
        Console.WriteLine("Stopping safely; press Ctrl+C again only if an instrument is unresponsive.");
    };

    bool measurementStarted = false;
    RthScopeCoordinator? scopes = null;
    await using Lr8400Client logger = new();
    await using ArduinoHeaterController heater = new(options.ArduinoPort, options.ArduinoBaudRate);

    try
    {
        // Every code path below this point can leave heater outputs energised if it
        // throws, which is why the finally block unconditionally commands all-off.
        Console.WriteLine($"Opening Arduino on {options.ArduinoPort} at {options.ArduinoBaudRate} baud ...");
        await heater.OpenAsync(cancellation.Token);
        Console.WriteLine("Arduino connected; all heater outputs are OFF.");

        Console.WriteLine($"Connecting to {options.Host}:{options.Port} ...");
        string identity = await logger.ConnectAsync(options.Host, options.Port, cancellation.Token);
        Console.WriteLine($"Connected: {identity}");

        if (!options.PreserveInputSettings)
        {
            // Put every analogue input into a known state before recording starts.
            // CH1_1..CH1_12 + CH2_7 (ambient) are thermocouples; CH2_1..CH2_6 and
            // CH2_8 are voltage inputs. CH2_8 gets its own call because it uses a
            // different voltage range (4 V) than the phase-voltage channels (2 V).
            Console.WriteLine(
                $"Configuring CH1_1..CH1_12 and CH2_7 as type-{options.ThermocoupleType} " +
                $"thermocouples on the {options.TemperatureRangeC:0} °C range.");
            await logger.ConfigureThermocoupleChannelsAsync(
                temperatureChannels,
                options.ThermocoupleType,
                options.TemperatureRangeC,
                internalReferenceJunctionCompensation: true,
                enableDisconnectionDetection: true,
                cancellationToken: cancellation.Token);

            Console.WriteLine("Configuring CH2_1..CH2_6 as voltage inputs on the 2 V range, scaling OFF.");
            await logger.ConfigureVoltageChannelsAsync(
                Enumerable.Range(1,6).Select(n=> $"CH2_{n}").ToArray(),
                voltageRangeV: 2,
                cancellationToken: cancellation.Token);

            Console.WriteLine("Configuring CH2_8 as a voltage input on the X V range, scaling OFF");
            await logger.ConfigureVoltageChannelsAsync(
                ["CH2_8"],
                voltageRangeV: 4,
                cancellationToken: cancellation.Token);

            await logger.SendAsync(":UNIT:FILTER 50HZ", cancellation.Token);
        }
        else
        {
            // --preserve-input-settings: trust whatever channel configuration the
            // LR8400 already has (useful when it was set up once and left alone).
            Console.WriteLine("Preserving the LR8400 input modes, sensors, ranges and RJC settings.");
        }

        if (scopeDefinitions.Count > 0)
        {
            // Optional: connect and arm one or two R&S Scope Rider RTH instruments
            // that capture the fast electrical transient at heater switch-off.
            // See RthScopeCapture.cs for the SCPI-level implementation.
            Console.WriteLine($"Connecting to {scopeDefinitions.Count} configured RTH scope(s) ...");
            scopes = await RthScopeCoordinator.ConnectAvailableAsync(
                scopeDefinitions, Console.WriteLine, cancellation.Token);
            await scopes.ConfigureAllAsync(scopeSettings, cancellation.Token);
            Console.WriteLine(
                $"Configured {scopes.Count} RTH scope(s): negative-edge single shot, " +
                $"{options.ScopeTimeRangeMilliseconds:0.###} ms record, " +
                $"reference {options.ScopeTriggerReferencePercent}%, 200 mV/div and 1:1 probes.");
            foreach ((string scopeName, RthAcquisitionInfo info) in scopes.GetAcquisitionInfo())
            {
                Console.WriteLine(
                    $"{scopeName}: acquisition sample rate={FormatRate(info.AcquisitionSampleRateSamplesPerSecond)}, " +
                    $"waveform point rate={FormatRate(info.WaveformPointRatePerSecond)}, " +
                    $"interval={info.WaveformSampleIntervalSeconds * 1e6:0.###} us, " +
                    $"timebase={info.TimePerDivisionSeconds * 1e3:0.###} ms/div.");
            }
        }

        Console.WriteLine($"Output root: {outputRoot}");
        Console.WriteLine($"Run plan: {string.Join(", ", plan.Select(item => item.FolderName))}");

        // ---------------------------------------------------------------------
        // Main batch loop: one heating + cooling cycle per device in the plan.
        // Each iteration is broken into three phases:
        //   1. Heating   - energise the device until it reaches hot equilibrium.
        //   2. Cooling   - keep recording until the device is back near ambient.
        //   3. Export    - download the LR8400 recording and save CSV/SVG files.
        // ---------------------------------------------------------------------
        for (int runIndex = 0; runIndex < plan.Count; runIndex++)
        {
            DeviceRun run = plan[runIndex];
            string runDirectory = Path.Combine(outputRoot, run.FolderName);
            Directory.CreateDirectory(runDirectory);
            List<TemperatureSnapshot> liveHistory = [];
            SvgPlotter.WritePlaceholder(liveTemperaturePath, $"{run.FolderName}: preparing temperature acquisition");
            SvgPlotter.WritePlaceholder(liveVoltagePath, $"{run.FolderName}: preparing voltage acquisition");
            WriteStatus(liveStatusPath, $"Run {runIndex + 1}/{plan.Count}: {run.DisplayName} - preparing");

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"\n=== Run {runIndex + 1}/{plan.Count}: {run.DisplayName} ({run.FolderName}) ===");
            Console.ResetColor();

            // Start a fresh continuous recording for this device before touching
            // the heater relays, so the very first heater-on transient is captured.
            await heater.AllOffAsync(CancellationToken.None);
            await logger.ConfigureContinuousAcquisitionAsync(
                new Lr8400ContinuousAcquisitionRequest
                {
                    Channels = recordingChannels,
                    SamplingInterval = sampleInterval,
                    ClearPreviousRecording = true,
                    DisableInstrumentAutoSave = true,
                    DisableUnselectedAnalogueStorage = true
                },
                cancellation.Token);
            await logger.StartAsync(cancellation.Token);
            measurementStarted = true;
            await WaitForRecordingStateAsync(logger, TimeSpan.FromSeconds(3), cancellation.Token);

            // Select the Arduino relay routing for this device (see HeaterRouting.Build)
            // and switch heating on. The monitor-relay flag is currently disabled
            // (MonitorRelaySelection.None) for every device - see DeviceRun.All below.
            ArduinoOutput route = HeaterRouting.Build(run.Device, run.MonitorRelay);
            Console.WriteLine(
                $"Selecting {run.Device}; automatic monitor mode={run.MonitorRelay}; " +
                $"Arduino route mask={(uint)route}. Heating remains OFF.");
            await heater.ConfigureDeviceAsync(run.Device, run.MonitorRelay, cancellation.Token);
            await Task.Delay(TimeSpan.FromSeconds(options.RelaySettleSeconds), cancellation.Token);
            await heater.SetHeatingEnabledAsync(true, cancellation.Token);
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(
                $"HEATING ON: {run.DisplayName}. Maximum heating time " +
                $"{options.MaximumHeatingMinutes:0.#} minutes.");
            Console.ResetColor();
            WriteStatus(liveStatusPath, $"Run {runIndex + 1}/{plan.Count}: {run.DisplayName} - heating");

            // --- Heating phase: poll every second until the device-temperature
            // channels are stable (EquilibriumDetector) AND the trigger channel has
            // reached the configured hot threshold.
            EquilibriumDetector hotDetector = NewEquilibriumDetector(equilibriumChannels, options);
            Stopwatch heatingTimer = Stopwatch.StartNew();
            Stopwatch displayTimer = Stopwatch.StartNew();
            Stopwatch plotTimer = Stopwatch.StartNew();
            bool lowTemperatureWarningShown = false;
            long triggerPointIndex = -1;
            DateTimeOffset triggerTimeUtc = default;

            while (true)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                TemperatureSnapshot snapshot = await ReadSnapshotAsync(logger, recordingChannels, cancellation.Token);
                liveHistory.Add(snapshot);
                hotDetector.Add(snapshot);
                EquilibriumResult equilibrium = hotDetector.Evaluate();
                KeyValuePair<string, double> triggerReading = ResolveTriggerTemperature(
                    snapshot, equilibriumChannels, options.TriggerTemperatureChannel);
                double triggerTemperature = triggerReading.Value;

                // Safety net: never let a device heat for longer than --max-heating-min,
                // even if it somehow never reports a stable equilibrium.
                if (heatingTimer.Elapsed >= TimeSpan.FromMinutes(options.MaximumHeatingMinutes))
                {
                    await heater.AllOffAsync(CancellationToken.None);
                    throw new TimeoutException(
                        $"Maximum heating time was reached during {run.FolderName}. All outputs are OFF.");
                }

                // Hard overtemperature cutoff (--safety-max-c), independent of equilibrium logic.
                CheckSafetyLimit(snapshot, temperatureChannels, options.SafetyMaximumTemperatureC);

                // Periodic console status line (every ~10 s) so a human watching the
                // terminal can see progress without flooding the log every second.
                if (displayTimer.Elapsed >= TimeSpan.FromSeconds(10))
                {
                    string stability = equilibrium.IsEquilibrium
                        ? "EQUILIBRIUM"
                        : $"not stable ({equilibrium.ObservedWindow.TotalMinutes:0.0} min)";
                    Console.WriteLine(
                        $"{snapshot.TimestampUtc:O}  {triggerReading.Key}={triggerTemperature:F3} °C  " +
                        $"ambient={snapshot.Values[ambientChannel]:F3} °C  {stability}  " +
                        $"span={equilibrium.WorstSpanC:0.###} °C  " +
                        $"slope={equilibrium.WorstAbsoluteSlopeCPerMinute:0.####} °C/min");
                    displayTimer.Restart();
                }

                // Periodic SVG refresh for the live-dashboard.html page.
                if (plotTimer.Elapsed >= TimeSpan.FromSeconds(options.LivePlotRefreshSeconds))
                {
                    WriteLivePlots(liveTemperaturePath, liveVoltagePath, liveHistory,
                        temperatureChannels, voltageChannels, channelLabels, $"{run.FolderName} - heating");
                    plotTimer.Restart();
                }

                // Temperatures are stable but haven't reached the hot threshold yet -
                // warn once so it's obvious heating is intentionally continuing.
                if (equilibrium.IsEquilibrium && triggerTemperature < options.TriggerTemperatureC)
                {
                    if (!lowTemperatureWarningShown)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine(
                            $"WARNING: all device-temperature channels are steady, but " +
                            $"{triggerReading.Key}={triggerTemperature:F3} °C is below " +
                            $"{options.TriggerTemperatureC:F1} °C. Heating continues.");
                        Console.ResetColor();
                        lowTemperatureWarningShown = true;
                    }
                }
                else if (!equilibrium.IsEquilibrium)
                {
                    lowTemperatureWarningShown = false;
                }

                // Hot equilibrium reached: arm any connected scopes, record the exact
                // LR8400 sample index, then physically switch the heater off. The RTH
                // scopes trigger on the real electrical edge, not on this LAN command,
                // so the arm-then-switch-off ordering here just needs to happen before
                // the edge occurs, with enough pre-trigger memory already filled.
                if (equilibrium.IsEquilibrium && triggerTemperature >= options.TriggerTemperatureC)
                {
                    bool scopesArmed = false;
                    if (scopes is not null)
                    {
                        try
                        {
                            await scopes.ArmAllAsync(cancellation.Token);
                            scopesArmed = true;
                            Console.WriteLine(
                                $"Armed {scopes.Count} RTH scope(s); filling " +
                                $"{scopeSettings.PreTriggerFill.TotalMilliseconds:0} ms of pre-trigger memory.");
                            await Task.Delay(scopeSettings.PreTriggerFill, cancellation.Token);
                        }
                        catch (Exception exception)
                        {
                            // A scope failing to arm must never block the heater shutdown itself.
                            Console.Error.WriteLine(
                                "WARNING: RTH arming failed; heater shutdown still proceeds. " + exception.Message);
                        }
                    }

                    Lr8400StoredBounds bounds = await logger.GetStoredBoundsAsync(cancellation.Token);
                    if (bounds.Count <= 0)
                    {
                        throw new InvalidOperationException("The LR8400 reports no stored samples at heater switch-off.");
                    }

                    triggerPointIndex = bounds.EndPointIndexExclusive - 1;
                    triggerTimeUtc = DateTimeOffset.UtcNow;
                    await heater.SetHeatingEnabledAsync(false, CancellationToken.None);
                    Console.WriteLine("HEATING OFF: the RTH sees the actual electrical disconnection edge.");
                    WriteStatus(liveStatusPath, $"Run {runIndex + 1}/{plan.Count}: {run.DisplayName} - cooling");

                    if (scopesArmed)
                    {
                        await ExportScopeCaptureAsync(scopes!, Path.Combine(runDirectory, "rth-captures"),
                            scopeSettings, cancellation.Token);
                    }

                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine(
                        $"HOT EQUILIBRIUM: {triggerReading.Key}={triggerTemperature:F3} °C at " +
                        $"{triggerTimeUtc:O}; LR8400 point {triggerPointIndex:N0}. Cooling to ambient.");
                    Console.ResetColor();
                    break;
                }

                await Task.Delay(livePollInterval, cancellation.Token);
            }

            // --- Cooling phase: keep recording (heater already off) until the
            // device has been stable for the equilibrium window AND every device
            // channel is within --ambient-tolerance-c of the ambient channel.
            EquilibriumDetector coolingDetector = NewEquilibriumDetector(temperatureChannels, options);
            Stopwatch coolingTimer = Stopwatch.StartNew();
            displayTimer.Restart();
            plotTimer.Restart();

            while (true)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                TemperatureSnapshot snapshot = await ReadSnapshotAsync(logger, recordingChannels, cancellation.Token);
                liveHistory.Add(snapshot);
                coolingDetector.Add(snapshot);
                EquilibriumResult coolingEquilibrium = coolingDetector.Evaluate();
                double ambient = snapshot.Values[ambientChannel];
                // Which device channel is currently furthest from ambient, and by how much.
                KeyValuePair<string, double> worstAmbientDifference = equilibriumChannels
                    .Select(channel => new KeyValuePair<string, double>(channel,
                        Math.Abs(snapshot.Values[channel] - ambient)))
                    .MaxBy(item => item.Value);
                bool minimumCoolingElapsed =
                    coolingTimer.Elapsed >= TimeSpan.FromMinutes(options.MinimumCoolingMinutes);
                bool ambientReached = worstAmbientDifference.Value <= options.AmbientToleranceC;

                CheckSafetyLimit(snapshot, temperatureChannels, options.SafetyMaximumTemperatureC);
                // Safety net: never wait longer than --max-cooling-min for ambient to be reached.
                if (coolingTimer.Elapsed >= TimeSpan.FromMinutes(options.MaximumCoolingMinutes))
                {
                    throw new TimeoutException(
                        $"Maximum cooling time was reached during {run.FolderName}; " +
                        $"worst ambient difference is {worstAmbientDifference.Key}=" +
                        $"{worstAmbientDifference.Value:F3} °C. Batch stopped before energising another device.");
                }

                if (displayTimer.Elapsed >= TimeSpan.FromSeconds(10))
                {
                    Console.WriteLine(
                        $"Cooling {coolingTimer.Elapsed.TotalMinutes:0.0} min: ambient={ambient:F3} °C, " +
                        $"worst delta={worstAmbientDifference.Key} {worstAmbientDifference.Value:F3} °C, " +
                        $"stable={(coolingEquilibrium.IsEquilibrium ? "yes" : "no")}");
                    displayTimer.Restart();
                }

                if (plotTimer.Elapsed >= TimeSpan.FromSeconds(options.LivePlotRefreshSeconds))
                {
                    WriteLivePlots(liveTemperaturePath, liveVoltagePath, liveHistory,
                        temperatureChannels, voltageChannels, channelLabels, $"{run.FolderName} - cooling");
                    plotTimer.Restart();
                }

                // The program never advances on a fixed timer alone - it only proceeds
                // once the minimum cooling time has passed AND temperatures are both
                // stable and close enough to ambient.
                if (minimumCoolingElapsed && coolingEquilibrium.IsEquilibrium && ambientReached)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine(
                        $"AMBIENT EQUILIBRIUM: all temperature channels stable and within " +
                        $"{options.AmbientToleranceC:F2} °C of CH2_7 after " +
                        $"{coolingTimer.Elapsed.TotalMinutes:0.0} minutes.");
                    Console.ResetColor();
                    break;
                }

                await Task.Delay(livePollInterval, cancellation.Token);
            }

            // --- Export phase: stop the LR8400, download the full recording for
            // this run, then write the CSV and the final (non-live) SVG plots.
            Console.WriteLine("Stopping LR8400 and waiting for storage to finish.");
            await logger.StopAndWaitForIdleAsync(TimeSpan.FromSeconds(30), cancellation.Token);
            measurementStarted = false;

            Lr8400StoredBounds finalBounds = await logger.GetStoredBoundsAsync(cancellation.Token);
            int pointCount = checked((int)finalBounds.Count);
            if (pointCount <= 0)
            {
                throw new InvalidOperationException("No recorded LR8400 samples remain in memory.");
            }

            Console.WriteLine($"Downloading {pointCount:N0} points per channel for {run.FolderName}.");
            Progress<Lr8400DownloadProgress> progress = new(item =>
                Console.Write($"\rDownload {item.Fraction:P1}   "));
            Lr8400Recording recording = await logger.DownloadStoredRangeAsync(
                recordingChannels, sampleInterval, finalBounds.FirstPointIndex, pointCount,
                triggerPointIndex, progress, cancellation.Token);
            Console.WriteLine();

            string csvPath = Path.Combine(runDirectory, "lr8400-all-channels.csv");
            string temperaturePlotPath = Path.Combine(runDirectory, "temperatures.svg");
            string voltagePlotPath = Path.Combine(runDirectory, "voltages.svg");
            await recording.SaveCsvAsync(csvPath, overwrite: false, channelHeadings: channelLabels,
                cancellationToken: cancellation.Token);
            SvgPlotter.WriteRecording(temperaturePlotPath, recording, temperatureChannels, channelLabels,
                $"{run.FolderName} temperatures - heater off {triggerTimeUtc:yyyy-MM-dd HH:mm:ss} UTC",
                "Temperature (°C)");
            SvgPlotter.WriteRecording(voltagePlotPath, recording, voltageChannels, channelLabels,
                $"{run.FolderName} voltages - heater off {triggerTimeUtc:yyyy-MM-dd HH:mm:ss} UTC",
                "Voltage (V)");
            WriteLivePlots(liveTemperaturePath, liveVoltagePath, liveHistory,
                temperatureChannels, voltageChannels, channelLabels, $"{run.FolderName} - complete");
            File.Copy(liveTemperaturePath, Path.Combine(runDirectory, "temperatures-live.svg"), true);
            File.Copy(liveVoltagePath, Path.Combine(runDirectory, "voltages-live.svg"), true);
            WriteStatus(liveStatusPath, $"Run {runIndex + 1}/{plan.Count}: {run.DisplayName} - complete");
            Console.WriteLine($"Saved: {runDirectory}");

            if (runIndex + 1 < plan.Count)
            {
                SvgPlotter.WritePlaceholder(liveTemperaturePath, "Previous run archived; preparing next device");
                SvgPlotter.WritePlaceholder(liveVoltagePath, "Previous run archived; preparing next device");
            }
        }

        await heater.AllOffAsync(CancellationToken.None);
        WriteStatus(liveStatusPath, $"COMPLETE: {plan.Count} device characterisation(s) saved");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\nBatch complete. Results: {outputRoot}");
        Console.ResetColor();
        return 0;
    }
    catch (OperationCanceledException)
    {
        // Reached when the user pressed Ctrl+C (see the CancelKeyPress handler above).
        Console.Error.WriteLine("Acquisition cancelled.");
        WriteStatus(liveStatusPath, "CANCELLED - heater shutdown requested");
        return 3;
    }
    catch (Exception exception)
    {
        // Any other failure (instrument error, timeout, safety cutoff, ...).
        Console.Error.WriteLine($"ERROR: {exception.Message}");
        WriteStatus(liveStatusPath, "ERROR: " + exception.Message);
        return 1;
    }
    finally
    {
        // This block always runs, on the success path, an exception, or a
        // cancellation, so the heater is never left energised when the process exits.
        if (heater.IsOpen)
        {
            try
            {
                await heater.AllOffAsync(CancellationToken.None);
                Console.WriteLine("Arduino heater outputs are OFF.");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"WARNING: could not confirm Arduino all-off command: {exception.Message}");
            }
        }

        if (measurementStarted)
        {
            try
            {
                await logger.StopAndWaitForIdleAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                Console.WriteLine("The LR8400 was stopped; recorded data remains in instrument memory.");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Could not stop the LR8400 cleanly: {exception.Message}");
            }
        }

        scopes?.Dispose();
    }
}

// Builds an equilibrium detector configured from the shared CLI options
// (window length, max span, max slope) for a given set of channels.
static EquilibriumDetector NewEquilibriumDetector(IReadOnlyList<string> channels, Options options) =>
    new(channels, TimeSpan.FromMinutes(options.EquilibriumWindowMinutes),
        options.MaximumSpanC, options.MaximumSlopeCPerMinute);

// Picks the temperature used to decide "is this device hot enough yet?".
// "HOTTEST" scans every equilibrium channel and picks the highest reading;
// otherwise it reads one specific configured channel (e.g. CH1_1).
static KeyValuePair<string, double> ResolveTriggerTemperature(
    TemperatureSnapshot snapshot,
    IReadOnlyList<string> equilibriumChannels,
    string configuredChannel) =>
    configuredChannel.Equals("HOTTEST", StringComparison.OrdinalIgnoreCase)
        ? equilibriumChannels
            .Select(channel => new KeyValuePair<string, double>(channel, snapshot.Values[channel]))
            .MaxBy(item => item.Value)
        : new KeyValuePair<string, double>(configuredChannel, snapshot.Values[configuredChannel]);

// Hard overtemperature cutoff. No-op unless --safety-max-c was supplied.
// Throws if any temperature channel is at or above the limit, which unwinds
// straight into the finally block above and switches the heater off.
static void CheckSafetyLimit(
    TemperatureSnapshot snapshot,
    IReadOnlyList<string> temperatureChannels,
    double? safetyMaximumTemperatureC)
{
    if (safetyMaximumTemperatureC is not double limit) return;
    KeyValuePair<string, double> hottest = temperatureChannels
        .Select(channel => new KeyValuePair<string, double>(channel, snapshot.Values[channel]))
        .MaxBy(item => item.Value);
    if (hottest.Value >= limit)
    {
        throw new InvalidOperationException(
            $"Safety limit reached: {hottest.Key}={hottest.Value:F3} °C (limit {limit:F3} °C). " +
            "The finally block will command all Arduino outputs OFF.");
    }
}

// Refreshes the two live SVG files that live-dashboard.html polls and displays.
static void WriteLivePlots(
    string temperaturePath,
    string voltagePath,
    IReadOnlyList<TemperatureSnapshot> history,
    IReadOnlyList<string> temperatureChannels,
    IReadOnlyList<string> voltageChannels,
    IReadOnlyDictionary<string, string> labels,
    string title)
{
    SvgPlotter.WriteLive(temperaturePath, history, temperatureChannels, labels,
        title + " temperatures", "Temperature (°C)");
    SvgPlotter.WriteLive(voltagePath, history, voltageChannels, labels,
        title + " voltages", "Voltage (V)");
}

// Waits out the configured post-trigger window, then stops and downloads the
// waveform from every armed RTH scope. Failures here are logged as warnings
// only - a scope export problem must never abort the LR8400 cooling phase.
static async Task ExportScopeCaptureAsync(
    RthScopeCoordinator scopes,
    string outputDirectory,
    RthCaptureSettings settings,
    CancellationToken cancellationToken)
{
    try
    {
        await Task.Delay(settings.PostTriggerWait, cancellationToken);
        await scopes.StopAllAsync(cancellationToken);
        IReadOnlyList<RthCaptureResult> captures = await scopes.ExportAllAsync(
            outputDirectory, settings, cancellationToken);
        foreach (RthCaptureResult capture in captures)
        {
            Console.WriteLine(
                $"{capture.ScopeName}: {capture.RecordLength:N0} native points at " +
                $"{capture.NativeSampleIntervalSeconds * 1e6:0.###} us; CSV={capture.NativeCsvPath}");
            if (capture.ResampledCsvPath is not null)
                Console.WriteLine($"{capture.ScopeName}: normalised CSV={capture.ResampledCsvPath}");
            if (capture.PlotPath is not null)
                Console.WriteLine($"{capture.ScopeName}: plot={capture.PlotPath}");
            if (capture.ResamplingWarning is not null)
                Console.Error.WriteLine($"WARNING: {capture.ScopeName}: {capture.ResamplingWarning}");
        }
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(
            "WARNING: RTH capture/export failed; LR8400 cooling acquisition continues. " + exception.Message);
    }
}

// Reads one "live" (not-yet-downloaded) sample for every requested channel.
// The LR8400 API returns live values per unit (1 or 2), so this groups the
// requested channels by unit number first and issues one query per unit.
static async Task<TemperatureSnapshot> ReadSnapshotAsync(
    Lr8400Client logger,
    IReadOnlyList<string> channels,
    CancellationToken cancellationToken)
{
    Dictionary<string, double> selected = new(StringComparer.OrdinalIgnoreCase);
    int[] units = channels.Select(channel =>
    {
        int separator = channel.IndexOf('_');
        if (!channel.StartsWith("CH", StringComparison.OrdinalIgnoreCase) || separator <= 2 ||
            !int.TryParse(channel.AsSpan(2, separator - 2), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int unitNumber))
            throw new ArgumentException($"Unsupported live analogue channel name: {channel}");
        return unitNumber;
    }).Distinct().OrderBy(unit => unit).ToArray();

    foreach (int unit in units)
    {
        IReadOnlyDictionary<string, double> unitValues = await logger.ReadUnitLiveValuesAsync(
            unit, latchValues: false, cancellationToken);
        foreach (string channel in channels.Where(
                     channel => channel.StartsWith($"CH{unit}_", StringComparison.OrdinalIgnoreCase)))
        {
            if (!unitValues.TryGetValue(channel, out double value))
                throw new InvalidDataException($"The LR8400 live response did not contain {channel}.");
            selected.Add(channel, value);
        }
    }

    return new TemperatureSnapshot(DateTimeOffset.UtcNow, selected);
}

// Polls :STATUS? until the LR8400 confirms it is actually recording after
// START, or throws if it doesn't get there within the timeout.
static async Task WaitForRecordingStateAsync(
    Lr8400Client logger, TimeSpan timeout, CancellationToken cancellationToken)
{
    Stopwatch stopwatch = Stopwatch.StartNew();
    Lr8400Status lastStatus = Lr8400Status.Idle;
    while (stopwatch.Elapsed < timeout)
    {
        lastStatus = await logger.ReadStatusAsync(cancellationToken);
        if ((lastStatus & (Lr8400Status.Starting | Lr8400Status.Storing)) != 0) return;
        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
    }

    throw new InvalidOperationException(
        $"The LR8400 did not enter a recording state after START (last status: {lastStatus}).");
}

// Formats a samples-per-second value with the most readable unit (Sa/s, kSa/s, ...).
static string FormatRate(double rate) => rate switch
{
    >= 1e9 => $"{rate / 1e9:0.###} GSa/s",
    >= 1e6 => $"{rate / 1e6:0.###} MSa/s",
    >= 1e3 => $"{rate / 1e3:0.###} kSa/s",
    > 0 => $"{rate:0.###} Sa/s",
    _ => "unavailable"
};

static void WriteStatus(string path, string status) =>
    File.WriteAllText(path, $"{DateTimeOffset.UtcNow:O}  {status}\n", new UTF8Encoding(false));

// Writes the static HTML shell that auto-refreshes the two live SVG images
// and the status text every 5 seconds. Written once at startup.
static void WriteLiveDashboard(string path)
{
    const string html = """
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Thermal characterisation live dashboard</title>
        <style>body{font:16px system-ui,sans-serif;margin:1rem;background:#f3f4f6;color:#111827}h1{margin:0 0 .4rem}#status{margin-bottom:1rem;font-family:ui-monospace,monospace}img{display:block;width:100%;max-width:1600px;margin:0 auto 1rem;background:#fff;border:1px solid #d1d5db}</style>
        </head><body><h1>Thermal characterisation</h1><div id="status">Waiting for acquisition status...</div>
        <img id="temperatures" src="live-temperatures.svg" alt="Live temperatures">
        <img id="voltages" src="live-voltages.svg" alt="Live voltages">
        <script>async function refresh(){const s=Date.now();document.getElementById('temperatures').src='live-temperatures.svg?'+s;document.getElementById('voltages').src='live-voltages.svg?'+s;try{document.getElementById('status').textContent=await(await fetch('live-status.txt?'+s)).text()}catch(_){}}setInterval(refresh,5000);refresh();</script>
        </body></html>
        """;
    File.WriteAllText(path, html, new UTF8Encoding(false));
}

// One entry per physical device position/type. "All" is the fixed batch order
// used by --all-devices; ForDevice looks up the entry for a single --device run.
file sealed record DeviceRun(
    string FolderName, string DisplayName, HeaterDevice Device, MonitorRelaySelection MonitorRelay)
{
    /// <summary>
    ///  IReadOnlyList sets up the relays to be turned on.
    /// </summary>
    public static IReadOnlyList<DeviceRun> All { get; } =
    [
        new("01_UU_IGBT", "U upper IGBT", HeaterDevice.IgbtUUpper, MonitorRelaySelection.None),
        new("02_UU_FRD", "U upper diode/FRD", HeaterDevice.DiodeUUpper, MonitorRelaySelection.None),
        new("03_UL_IGBT", "U lower IGBT", HeaterDevice.IgbtULower, MonitorRelaySelection.None),
        new("04_UL_FRD", "U lower diode/FRD", HeaterDevice.DiodeULower, MonitorRelaySelection.None),
        new("05_VU_IGBT", "V upper IGBT", HeaterDevice.IgbtVUpper, MonitorRelaySelection.None),
        new("06_VU_FRD", "V upper diode/FRD", HeaterDevice.DiodeVUpper, MonitorRelaySelection.None),
        new("07_VL_IGBT", "V lower IGBT", HeaterDevice.IgbtVLower, MonitorRelaySelection.None),
        new("08_VL_FRD", "V lower diode/FRD", HeaterDevice.DiodeVLower, MonitorRelaySelection.None),
        new("09_WU_IGBT", "W upper IGBT", HeaterDevice.IgbtWUpper, MonitorRelaySelection.None),
        new("10_WU_FRD", "W upper diode/FRD", HeaterDevice.DiodeWUpper, MonitorRelaySelection.None),
        new("11_WL_IGBT", "W lower IGBT", HeaterDevice.IgbtWLower, MonitorRelaySelection.None),
        new("12_WL_FRD", "W lower diode/FRD", HeaterDevice.DiodeWLower, MonitorRelaySelection.None)
    ];

    // Single-device runs reuse the same table but strip the "NN_" batch-order
    // prefix from the folder name, since there's no batch sequence to number.
    public static DeviceRun ForDevice(HeaterDevice device)
    {
        DeviceRun match = All.First(item => item.Device == device);
        return match with { FolderName = match.FolderName[3..] };
    }
}

// All parsed command-line settings, as one immutable record. See Parse() below
// for how each field is read from argv, and PrintUsage() for the full list of
// flags with their defaults.
file sealed record Options(
    string Host, int Port, string ArduinoPort, int ArduinoBaudRate, bool AllDevices,
    HeaterDevice? HeaterDevice, double RelaySettleSeconds, double MaximumHeatingMinutes,
    double MaximumCoolingMinutes, double MinimumCoolingMinutes, double AmbientToleranceC,
    double? SafetyMaximumTemperatureC, string? Scope1Resource, string Scope1TriggerSource,
    double? Scope1TriggerLevelV, string? Scope2Resource, string Scope2TriggerSource,
    double? Scope2TriggerLevelV, int[] ScopeAnalogueChannels, double ScopeTimeRangeMilliseconds,
    int ScopeTriggerReferencePercent, double ScopeOutputIntervalMicroseconds,
    double ScopePreTriggerFillMilliseconds, double ScopePostTriggerWaitMilliseconds,
    string ThermocoupleType, double TemperatureRangeC, bool PreserveInputSettings,
    string TriggerTemperatureChannel, double TriggerTemperatureC, double EquilibriumWindowMinutes,
    double MaximumSpanC, double MaximumSlopeCPerMinute, double LivePlotRefreshSeconds,
    string OutputDirectory, bool ShowHelp)
{
    // Turns argv into an Options instance. Throws ArgumentException/ArgumentOutOfRangeException
    // on any invalid combination; RunAsync catches that and prints usage instead of continuing.
    public static Options Parse(string[] args)
    {
        // First pass: collect raw "--flag value" pairs into a lookup. A handful of
        // flags (--help, --all-devices, --preserve-input-settings) are boolean
        // switches with no following value, so they're special-cased here.
        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (argument is "--help" or "-h" or "--preserve-input-settings" or "--all-devices")
            {
                values[argument] = null;
                continue;
            }

            if (!argument.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
                throw new ArgumentException($"Invalid or incomplete argument: {argument}");
            values[argument] = args[++index];
        }

        bool help = values.ContainsKey("--help") || values.ContainsKey("-h");
        string host = Get(values, "--host", help ? "127.0.0.1" : null)
            ?? throw new ArgumentException("--host is required.");
        string arduinoPort = Get(values, "--arduino-port", help ? "COM1" : null)
            ?? throw new ArgumentException("--arduino-port is required.");
        bool allDevices = values.ContainsKey("--all-devices");
        string? deviceText = Get(values, "--device", null);
        HeaterDevice? device = deviceText is null ? null : ParseHeaterDevice(deviceText);
        if (!help && allDevices == (device is not null))
            throw new ArgumentException("Supply exactly one of --all-devices or --device NAME.");

        // Which temperature reading decides "hot enough": either the single
        // hottest CH1 channel (HOTTEST, the --all-devices default) or one fixed
        // channel name. Must be within the 12 channels actually recorded.
        string triggerChannel = Get(values, "--trigger-channel", allDevices ? "HOTTEST" : "CH1_1")!
            .ToUpperInvariant();
        if (triggerChannel != "HOTTEST" &&
            !Enumerable.Range(1, 12).Select(n => $"CH1_{n}").Contains(triggerChannel))
            throw new ArgumentException("--trigger-channel must be HOTTEST or CH1_1 through CH1_12.");

        // Optional RTH scope configuration; leaving --scope1/--scope2 unset means
        // BuildScopeDefinitions() later returns an empty list and no scope is used.
        string? scope1Resource = Get(values, "--scope1", null);
        string? scope2Resource = Get(values, "--scope2", null);
        double? scope1Level = GetOptionalDouble(values, "--scope1-trigger-level-v");
        double? scope2Level = GetOptionalDouble(values, "--scope2-trigger-level-v");
        ValidateScopePair("scope1", scope1Resource, scope1Level);
        ValidateScopePair("scope2", scope2Resource, scope2Level);
        double scopeRangeMs = GetDouble(values, "--scope-range-ms", 500);
        int scopeReference = GetInt(values, "--scope-reference-percent", 50);
        if (scopeReference is not (10 or 50 or 90))
            throw new ArgumentException("--scope-reference-percent must be 10, 50 or 90.");
        // Pre/post-trigger wait defaults are derived from the scope range and
        // reference percentage, with a small safety margin added on each side.
        double defaultPreFillMs = scopeRangeMs * scopeReference / 100.0 + 50;
        double defaultPostWaitMs = scopeRangeMs * (1.0 - scopeReference / 100.0) + 100;
        string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        return new Options(
            host, GetInt(values, "--port", 8802), arduinoPort, GetInt(values, "--arduino-baud", 9600),
            allDevices, device, GetDouble(values, "--relay-settle-s", 5),
            GetDouble(values, "--max-heating-min", 120), GetDouble(values, "--max-cooling-min", 180),
            GetNonNegativeDouble(values, "--min-cooling-min", 20),
            GetDouble(values, "--ambient-tolerance-c", 1), GetOptionalDouble(values, "--safety-max-c"),
            scope1Resource, ParseScopeTriggerSource(Get(values, "--scope1-trigger-source", "C4")!), scope1Level,
            scope2Resource, ParseScopeTriggerSource(Get(values, "--scope2-trigger-source", "C4")!), scope2Level,
            ParseScopeChannels(Get(values, "--scope-channels", "1,2,3,4")!), scopeRangeMs, scopeReference,
            GetDouble(values, "--scope-output-us", 125),
            GetDouble(values, "--scope-pre-fill-ms", defaultPreFillMs),
            GetDouble(values, "--scope-post-wait-ms", defaultPostWaitMs),
            Get(values, "--sensor", "K")!, GetDouble(values, "--range", 100),
            values.ContainsKey("--preserve-input-settings"), triggerChannel,
            GetDouble(values, "--trigger-c", 30), GetDouble(values, "--window-min", 5),
            GetDouble(values, "--max-span-c", 0.2), GetDouble(values, "--max-slope-c-per-min", 0.02),
            GetDouble(values, "--plot-refresh-s", 5),
            Get(values, "--output", Path.Combine("HiokiRuns", timestamp))!, help);
    }

    // All twelve devices (batch order) or just the one --device that was requested.
    public IReadOnlyList<DeviceRun> BuildRunPlan() =>
        AllDevices ? DeviceRun.All : [DeviceRun.ForDevice(HeaterDevice!.Value)];

    // Zero, one or two RTH scope definitions, depending on which --scopeN flags
    // were supplied. An empty list disables all scope-related code in RunAsync.
    public IReadOnlyList<RthScopeDefinition> BuildScopeDefinitions()
    {
        List<RthScopeDefinition> definitions = [];
        if (Scope1Resource is not null)
            definitions.Add(new("RTH1", Scope1Resource, Scope1TriggerSource,
                Scope1TriggerLevelV!.Value, ScopeAnalogueChannels));
        if (Scope2Resource is not null)
            definitions.Add(new("RTH2", Scope2Resource, Scope2TriggerSource,
                Scope2TriggerLevelV!.Value, ScopeAnalogueChannels));
        return definitions;
    }

    public static void PrintUsage() => Console.WriteLine(
        """
        Usage (all 12 devices):
          dotnet run -- --host 192.168.1.101 --arduino-port COM9 --all-devices --safety-max-c 80

        Usage (one-device trial):
          dotnet run -- --host 192.168.1.101 --arduino-port COM9 --device IgbtUUpper --min-cooling-min 0

        Main options:
          --all-devices               Run UU/UL/VU/VL/WU/WL, IGBT then FRD at each position
          --device NAME               Run one device only (mutually exclusive with --all-devices)
          --trigger-channel HOTTEST    Hot threshold source; batch default is hottest CH1 channel
                                       Single-device default remains CH1_1; CH1_1..CH1_12 accepted
          --trigger-c 30              Minimum hot-equilibrium temperature
          --window-min 5              Equilibrium observation window
          --max-span-c 0.2            Maximum span in window, every temperature channel
          --max-slope-c-per-min 0.02  Maximum fitted slope, every temperature channel
          --ambient-tolerance-c 1     Maximum device-to-ambient difference before next run
          --min-cooling-min 20        Minimum cooling record after heater switch-off
          --max-cooling-min 180       Safe timeout before another device can be energised
          --max-heating-min 120       Safe hot-equilibrium timeout
          --safety-max-c VALUE        Strongly recommended software overtemperature cutoff
          --plot-refresh-s 5          Live SVG refresh interval
          --output PATH               Batch output root

        Instrument options:
          --port 8802                 LR8400 command port; recording is fixed at 10 Hz
          --arduino-baud 9600         Arduino serial rate
          --sensor K                  Thermocouple type
          --range 100                 Thermocouple range: 100, 500 or 2000 °C
          --preserve-input-settings   Do not configure LR8400 inputs, including voltage range

        RTH options:
          --scope1 RESOURCE --scope1-trigger-level-v V
          --scope2 RESOURCE --scope2-trigger-level-v V   (optional)
          --scope1-trigger-source C4  C1-C4 or D0-D7
          --scope2-trigger-source C4  C1-C4 or D0-D7
          --scope-channels 1,2,3,4
          --scope-range-ms 500        50 ms/div; 250 ms pre + 250 ms post at 50%
          --scope-reference-percent 50
          --scope-output-us 125
        """);

    // --- Small parsing/validation helpers used only by Parse() above ---------

    private static string? Get(IReadOnlyDictionary<string, string?> values, string name, string? fallback) =>
        values.TryGetValue(name, out string? value) ? value : fallback;
    private static int GetInt(IReadOnlyDictionary<string, string?> values, string name, int fallback) =>
        int.Parse(Get(values, name, fallback.ToString(CultureInfo.InvariantCulture))!, CultureInfo.InvariantCulture);

    // Parses a positive double option, throwing if it's missing, non-numeric,
    // non-finite, or not greater than zero.
    private static double GetDouble(IReadOnlyDictionary<string, string?> values, string name, double fallback)
    {
        double value = double.Parse(Get(values, name, fallback.ToString(CultureInfo.InvariantCulture))!,
            NumberStyles.Float, CultureInfo.InvariantCulture);
        if (!double.IsFinite(value) || value <= 0)
            throw new ArgumentOutOfRangeException(name, "Value must be finite and greater than zero.");
        return value;
    }

    // Same as GetDouble but allows zero (used for --min-cooling-min, where 0 is
    // a valid "skip the minimum wait" value for quick bench trials).
    private static double GetNonNegativeDouble(
        IReadOnlyDictionary<string, string?> values, string name, double fallback)
    {
        double value = double.Parse(Get(values, name, fallback.ToString(CultureInfo.InvariantCulture))!,
            NumberStyles.Float, CultureInfo.InvariantCulture);
        if (!double.IsFinite(value) || value < 0)
            throw new ArgumentOutOfRangeException(name, "Value must be finite and non-negative.");
        return value;
    }

    // Returns null when the flag wasn't supplied at all, instead of throwing.
    private static double? GetOptionalDouble(IReadOnlyDictionary<string, string?> values, string name)
    {
        string? text = Get(values, name, null);
        if (text is null) return null;
        double value = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(name, "Value must be finite.");
        return value;
    }

    // A scope's resource string and trigger level must be supplied together or
    // not at all, and the trigger level must fall within the instrument's ±10 V input range.
    private static void ValidateScopePair(string name, string? resource, double? triggerLevel)
    {
        if ((resource is null) != (triggerLevel is null))
            throw new ArgumentException($"--{name} and --{name}-trigger-level-v must be supplied together.");
        if (triggerLevel is double value && value is < -10 or > 10)
            throw new ArgumentOutOfRangeException($"--{name}-trigger-level-v", "Must be -10 V to +10 V.");
    }

    // A scope trigger source is either an analogue channel C1-C4 or a digital line D0-D7.
    private static string ParseScopeTriggerSource(string text)
    {
        string source = text.Trim().ToUpperInvariant();
        bool analogue = source.Length == 2 && source[0] == 'C' && source[1] is >= '1' and <= '4';
        bool digital = source.Length == 2 && source[0] == 'D' && source[1] is >= '0' and <= '7';
        if (!analogue && !digital) throw new ArgumentException("Scope trigger source must be C1-C4 or D0-D7.");
        return source;
    }

    // Parses a comma-separated list like "1,2,3,4" into distinct, sorted scope
    // analogue-channel numbers, each of which must be between 1 and 4.
    private static int[] ParseScopeChannels(string text)
    {
        int[] channels = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.Parse(value, CultureInfo.InvariantCulture)).Distinct().OrderBy(value => value).ToArray();
        if (channels.Length == 0 || channels.Any(channel => channel is < 1 or > 4))
            throw new ArgumentException("--scope-channels must contain one or more values from 1 to 4.");
        return channels;
    }

    // Accepts --device either as a 1-based index into HeaterDevice or as a
    // (punctuation/case-insensitive) name match, e.g. "igbt-u-upper" or "IgbtUUpper".
    private static HeaterDevice ParseHeaterDevice(string text)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) &&
            number >= 1 && number <= Enum.GetValues<HeaterDevice>().Length)
            return Enum.GetValues<HeaterDevice>()[number - 1];
        string normalised = NormaliseName(text);
        foreach (HeaterDevice device in Enum.GetValues<HeaterDevice>())
            if (NormaliseName(device.ToString()) == normalised) return device;
        throw new ArgumentException($"Unknown heater device '{text}'. Use --help to list valid names.");
    }

    private static string NormaliseName(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
