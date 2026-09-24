using System.Globalization;
using RohdeSchwarz.RsInstrument;

namespace HiokiThermalEquilibriumExample;

internal sealed record RthScopeDefinition(
    string Name,
    string Resource,
    string TriggerSource,
    double TriggerLevelV,
    IReadOnlyList<int> AnalogueChannels);

internal sealed record RthCaptureSettings(
    double TimeRangeSeconds,
    int TriggerReferencePercent,
    TimeSpan TargetOutputInterval,
    TimeSpan PreTriggerFill,
    TimeSpan PostTriggerWait);

internal sealed record RthCaptureResult(
    string ScopeName,
    string Identity,
    string NativeCsvPath,
    string? ResampledCsvPath,
    string? PlotPath,
    double NativeSampleIntervalSeconds,
    long RecordLength,
    string? ResamplingWarning);

internal sealed record RthAcquisitionInfo(
    double AcquisitionSampleRateSamplesPerSecond,
    double WaveformSampleIntervalSeconds,
    double TimeRangeSeconds)
{
    public double WaveformPointRatePerSecond =>
        WaveformSampleIntervalSeconds > 0 ? 1.0 / WaveformSampleIntervalSeconds : double.NaN;

    public double TimePerDivisionSeconds => TimeRangeSeconds / 10.0;
}

/// <summary>
/// Controls a Scope Rider RTH using only commands documented in the supplied
/// RTH user/SCPI manual. Waveforms are transferred using the documented
/// instrument-side CSV export route rather than the undocumented CHANnel:DATA?
/// command used by the legacy application.
/// </summary>
internal sealed class RthScopeCapture : IDisposable
{
    private readonly RsInstrument _instrument;
    private bool _disposed;

    public RthScopeCapture(RthScopeDefinition definition)
    {
        Definition = definition;
        ValidateDefinition(definition);

        _instrument = new RsInstrument(
            definition.Resource,
            idQuery: true,
            resetDevice: false,
            optionString:
                "SelectVisa=RsVisa,OpcWaitMode=OpcQuery,StbInErrorCheck=false," +
                "OpcTimeout=60000,VisaTimeout=60000");

        Identity = _instrument.QueryString("*IDN?").Trim();
    }

    public RthScopeDefinition Definition { get; }
    public string Identity { get; }
    public RthAcquisitionInfo? AcquisitionInfo { get; private set; }

    public void Configure(RthCaptureSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateSettings(settings);

        // Commands are deliberately sent as separate program messages. The RTH
        // manual warns that settings in a combined message are not guaranteed to
        // be serviced in textual order.
        _instrument.WriteString("STOP");
        _instrument.WriteString("*CLS");
        _instrument.WriteString("ACQuire:MODE SAMPle");
        _instrument.WriteString(FormattableString.Invariant(
            $"TIMebase:RANGe {settings.TimeRangeSeconds:R}"));
        _instrument.WriteString(FormattableString.Invariant(
            $"TIMebase:REFerence {settings.TriggerReferencePercent}"));
        _instrument.WriteString("TIMebase:HORizontal:POSition 0");

        HashSet<int> activeChannels = Definition.AnalogueChannels.ToHashSet();
        if (TryGetAnalogueTriggerChannel(Definition.TriggerSource, out int triggerChannel))
        {
            activeChannels.Add(triggerChannel);
        }

        for (int channel = 1; channel <= 4; channel++)
        {
            _instrument.WriteString($"CHANnel{channel}:PROBe V1TO1");
            _instrument.WriteString($"CHANnel{channel}:SCALe 0.2");
            _instrument.WriteString(
                $"CHANnel{channel}:STATe {(activeChannels.Contains(channel) ? "ON" : "OFF")}");
        }

        _instrument.WriteString("TRIGger:MODE SINGle");
        _instrument.WriteString("TRIGger:TYPE EDGE");
        _instrument.WriteString($"TRIGger:SOURce {Definition.TriggerSource}");
        _instrument.WriteString(string.Create(
            CultureInfo.InvariantCulture,
            $"TRIGger:LEVel{GetTriggerLevelSuffix(Definition.TriggerSource)}:VALue {Definition.TriggerLevelV:R}"));
        _instrument.WriteString("TRIGger:EDGE:SLOPe NEGative");
        _instrument.WriteString("TRIGger:MNR ON");

        // Synchronise only the finite setup work. RUN is not OPC-synchronised,
        // because it is expected to wait for the real falling-edge event.
        _instrument.QueryOpc(10_000);

        AcquisitionInfo = new RthAcquisitionInfo(
            _instrument.QueryDouble("ACQuire:POINts:ARATe?"),
            _instrument.QueryDouble("ACQuire:RESolution?"),
            _instrument.QueryDouble("TIMebase:RANGe?"));
    }

    public void Arm()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _instrument.WriteString("STOP");
        _instrument.WriteString("TRIGger:MODE SINGle");
        _instrument.WriteString("RUN");
    }

    public void Stop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _instrument.WriteString("STOP");
    }

    public RthCaptureResult ExportCapture(
        string outputDirectory,
        RthCaptureSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Directory.CreateDirectory(outputDirectory);

        double nativeInterval = _instrument.QueryDouble("ACQuire:RESolution?");
        long recordLength = _instrument.QueryInteger("ACQuire:POINts:VALue?");
        if (!double.IsFinite(nativeInterval) || nativeInterval <= 0)
        {
            throw new InvalidDataException(
                $"{Definition.Name} returned an invalid ACQuire:RESolution? value: {nativeInterval}.");
        }

        string stem = MakeSafeFileStem(Definition.Name);
        string nativePath = Path.Combine(outputDirectory, $"{stem}-native.csv");
        string intervalLabel = settings.TargetOutputInterval.TotalMicroseconds
            .ToString("0.###", CultureInfo.InvariantCulture);
        string resampledPath = Path.Combine(outputDirectory, $"{stem}-{intervalLabel}us.csv");
        string plotPath = Path.Combine(outputDirectory, $"{stem}-{intervalLabel}us.svg");
        string instrumentPath = $"/media/SD/{stem}-{Guid.NewGuid():N}.csv";

        _instrument.WriteString($":EXPort:WAVeform:NAME '{instrumentPath}'");
        _instrument.WriteString(":EXPort:WAVeform:MULTichannel 1");
        _instrument.WriteString(":EXPort:WAVeform:INCXvalues 1");
        _instrument.WriteString(":EXPort:WAVeform:DLOGging 0");
        _instrument.WriteStringWithOpc(":EXPort:WAVeform:SAVE", 60_000);

        try
        {
            byte[] csv = _instrument.Binary.QueryData($":MMEMory:DATA? '{instrumentPath}'");
            File.WriteAllBytes(nativePath, csv);
        }
        finally
        {
            try
            {
                _instrument.WriteStringWithOpc(
                    $":MMEMory:DELete '{instrumentPath}'",
                    30_000);
            }
            catch
            {
                // Keep the acquired PC file even if deleting the temporary SD-card
                // file fails. The caller will still receive any export exception.
            }
        }

        List<string> warnings = [];
        string? normalisedPath = resampledPath;
        string? waveformPlotPath = plotPath;
        try
        {
            RthCsvResampleResult resample = RthCsvResampler.Resample(
                nativePath,
                resampledPath,
                settings.TargetOutputInterval);

            // The query is authoritative for the instrument. The CSV-derived
            // interval is retained as a useful cross-check in the warning text.
            if (nativeInterval > settings.TargetOutputInterval.TotalSeconds * 1.001)
            {
                warnings.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "Native scope interval {0:G9} s is coarser than the requested {1:G9} s; " +
                    "interpolation cannot restore missing detail. Increase the record density or shorten the time range.",
                    nativeInterval,
                    settings.TargetOutputInterval.TotalSeconds));
            }
            else if (Math.Abs(resample.SourceMedianIntervalSeconds - nativeInterval) >
                     Math.Max(nativeInterval, resample.SourceMedianIntervalSeconds) * 0.05)
            {
                warnings.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "RTH interval query returned {0:G9} s, while the exported CSV median is {1:G9} s. " +
                    "Inspect the native CSV header.",
                    nativeInterval,
                    resample.SourceMedianIntervalSeconds));
            }
        }
        catch (Exception exception)
        {
            normalisedPath = null;
            waveformPlotPath = null;
            warnings.Add(
                $"Native CSV was saved, but {settings.TargetOutputInterval.TotalMicroseconds:0.###} us " +
                $"resampling failed: {exception.Message}");
        }

        if (normalisedPath is not null)
        {
            try
            {
                RthCsvPlotter.Write(
                    normalisedPath,
                    plotPath,
                    $"{Definition.Name} heater-switch-off transient");
            }
            catch (Exception exception)
            {
                waveformPlotPath = null;
                warnings.Add($"Normalised CSV was saved, but SVG creation failed: {exception.Message}");
            }
        }

        return new RthCaptureResult(
            Definition.Name,
            Identity,
            nativePath,
            normalisedPath,
            waveformPlotPath,
            nativeInterval,
            recordLength,
            warnings.Count == 0 ? null : string.Join(" ", warnings));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _instrument.WriteString("STOP");
        }
        catch
        {
            // Best effort while disposing a possibly disconnected instrument.
        }

        _instrument.Dispose();
        _disposed = true;
    }

    private static void ValidateDefinition(RthScopeDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Resource);
        if (!double.IsFinite(definition.TriggerLevelV) ||
            definition.TriggerLevelV is < -10 or > 10)
        {
            throw new ArgumentOutOfRangeException(
                nameof(definition),
                "The RTH trigger level must be finite and between -10 V and +10 V.");
        }

        _ = GetTriggerLevelSuffix(definition.TriggerSource);
        if (definition.AnalogueChannels.Count == 0 ||
            definition.AnalogueChannels.Any(channel => channel is < 1 or > 4))
        {
            throw new ArgumentOutOfRangeException(
                nameof(definition),
                "At least one analogue capture channel from 1 through 4 is required.");
        }
    }

    private static void ValidateSettings(RthCaptureSettings settings)
    {
        if (!double.IsFinite(settings.TimeRangeSeconds) || settings.TimeRangeSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings.TimeRangeSeconds));
        }

        if (settings.TriggerReferencePercent is not (10 or 50 or 90))
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings.TriggerReferencePercent),
                "The RTH accepts trigger reference values 10, 50 or 90 percent.");
        }

        double postTriggerSeconds = settings.TimeRangeSeconds *
            (1.0 - settings.TriggerReferencePercent / 100.0);
        double preTriggerSeconds = settings.TimeRangeSeconds *
            settings.TriggerReferencePercent / 100.0;
        if (postTriggerSeconds + 1e-12 < TimeSpan.FromMilliseconds(250).TotalSeconds)
        {
            throw new ArgumentException(
                "The RTH time range/reference combination must provide at least 250 ms after the trigger.");
        }

        if (settings.PreTriggerFill.TotalSeconds < preTriggerSeconds ||
            settings.PostTriggerWait < TimeSpan.FromSeconds(postTriggerSeconds))
        {
            throw new ArgumentException(
                "Pre- and post-trigger waits must cover the corresponding portions of the configured record.");
        }
    }

    private static int GetTriggerLevelSuffix(string source)
    {
        string normalised = source.Trim().ToUpperInvariant();
        if (normalised.Length == 2 && normalised[0] == 'C' &&
            normalised[1] is >= '1' and <= '4')
        {
            return normalised[1] - '0';
        }

        if (normalised.Length == 2 && normalised[0] == 'D' &&
            normalised[1] is >= '0' and <= '7')
        {
            return 8 + normalised[1] - '0';
        }

        throw new ArgumentException(
            "RTH trigger source must be C1-C4 or D0-D7. Digital sources require option RTH-B1.",
            nameof(source));
    }

    private static bool TryGetAnalogueTriggerChannel(string source, out int channel)
    {
        string normalised = source.Trim().ToUpperInvariant();
        if (normalised.Length == 2 && normalised[0] == 'C' &&
            normalised[1] is >= '1' and <= '4')
        {
            channel = normalised[1] - '0';
            return true;
        }

        channel = 0;
        return false;
    }

    private static string MakeSafeFileStem(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string result = new(value
            .Select(character => invalid.Contains(character) ? '-' : character)
            .ToArray());
        return string.IsNullOrWhiteSpace(result) ? "rth-scope" : result;
    }
}

internal sealed class RthScopeCoordinator : IDisposable
{
    private readonly List<RthScopeCapture> _scopes;
    private bool _disposed;

    private RthScopeCoordinator(List<RthScopeCapture> scopes)
    {
        _scopes = scopes;
    }

    public int Count => _scopes.Count;

    public IReadOnlyList<(string ScopeName, RthAcquisitionInfo Info)> GetAcquisitionInfo() =>
        _scopes
            .Where(scope => scope.AcquisitionInfo is not null)
            .Select(scope => (scope.Definition.Name, scope.AcquisitionInfo!))
            .ToArray();

    public static async Task<RthScopeCoordinator> ConnectAvailableAsync(
        IReadOnlyList<RthScopeDefinition> definitions,
        Action<string> report,
        CancellationToken cancellationToken)
    {
        Task<(RthScopeCapture? Scope, string? Error)>[] tasks = definitions
            .Select(definition => Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    return ((RthScopeCapture?)new RthScopeCapture(definition), (string?)null);
                }
                catch (Exception exception)
                {
                    return ((RthScopeCapture?)null,
                        $"{definition.Name} ({definition.Resource}) unavailable: {exception.Message}");
                }
            }, cancellationToken))
            .ToArray();

        (RthScopeCapture? Scope, string? Error)[] outcomes = await Task.WhenAll(tasks);
        List<RthScopeCapture> connected = [];
        foreach ((RthScopeCapture? scope, string? error) in outcomes)
        {
            if (scope is not null)
            {
                connected.Add(scope);
                report($"Connected {scope.Definition.Name}: {scope.Identity}");
            }
            else if (error is not null)
            {
                report("WARNING: " + error);
            }
        }

        if (connected.Count == 0)
        {
            throw new InvalidOperationException(
                "Scope capture was requested, but none of the configured RTH scopes could be connected.");
        }

        return new RthScopeCoordinator(connected);
    }

    public Task ConfigureAllAsync(
        RthCaptureSettings settings,
        CancellationToken cancellationToken) =>
        RunOnAllAsync(scope => scope.Configure(settings), "configure", cancellationToken);

    public Task ArmAllAsync(CancellationToken cancellationToken) =>
        RunOnAllAsync(scope => scope.Arm(), "arm", cancellationToken);

    public Task StopAllAsync(CancellationToken cancellationToken) =>
        RunOnAllAsync(scope => scope.Stop(), "stop", cancellationToken);

    public async Task<IReadOnlyList<RthCaptureResult>> ExportAllAsync(
        string outputDirectory,
        RthCaptureSettings settings,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Task<(RthCaptureResult? Result, RthScopeCapture Scope, Exception? Error)>[] tasks = _scopes
            .Select(scope => Task.Run(() =>
            {
                try
                {
                    return ((RthCaptureResult?)scope.ExportCapture(outputDirectory, settings), scope, (Exception?)null);
                }
                catch (Exception exception)
                {
                    return ((RthCaptureResult?)null, scope, exception);
                }
            }, cancellationToken))
            .ToArray();
        (RthCaptureResult? Result, RthScopeCapture Scope, Exception? Error)[] outcomes =
            await Task.WhenAll(tasks).ConfigureAwait(false);

        List<RthCaptureResult> results = [];
        foreach ((RthCaptureResult? result, RthScopeCapture scope, Exception? error) in outcomes)
        {
            if (result is not null)
            {
                results.Add(result);
            }
            else
            {
                Console.Error.WriteLine(
                    $"WARNING: {scope.Definition.Name} waveform export failed: {error?.Message}");
            }
        }

        if (results.Count == 0)
        {
            throw new InvalidOperationException("No connected RTH scope produced a waveform export.");
        }

        return results;
    }

    private async Task RunOnAllAsync(
        Action<RthScopeCapture> operation,
        string operationName,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Task<(RthScopeCapture Scope, Exception? Error)>[] tasks = _scopes
            .Select(scope => Task.Run(() =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    operation(scope);
                    return (scope, (Exception?)null);
                }
                catch (Exception exception)
                {
                    return (scope, exception);
                }
            }, cancellationToken))
            .ToArray();

        (RthScopeCapture Scope, Exception? Error)[] outcomes =
            await Task.WhenAll(tasks).ConfigureAwait(false);
        foreach ((RthScopeCapture scope, Exception? error) in outcomes)
        {
            if (error is null)
            {
                continue;
            }

            Console.Error.WriteLine(
                $"WARNING: {scope.Definition.Name} failed to {operationName}: {error.Message}");
            scope.Dispose();
            _scopes.Remove(scope);
        }

        if (_scopes.Count == 0)
        {
            throw new InvalidOperationException(
                $"All connected RTH scopes failed to {operationName}.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (RthScopeCapture scope in _scopes)
        {
            scope.Dispose();
        }

        _disposed = true;
    }
}
