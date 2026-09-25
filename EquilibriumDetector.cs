namespace HiokiThermalEquilibriumExample;

// One point-in-time reading of every channel this program is tracking.
internal sealed record TemperatureSnapshot(
    DateTimeOffset TimestampUtc,
    IReadOnlyDictionary<string, double> Values);

// The verdict from one Evaluate() call. WorstSpanC/WorstAbsoluteSlopeCPerMinute
// and LimitingChannel describe whichever channel was closest to failing (or did
// fail) the stability test, which is what the console status lines in
// Program.cs display even while IsEquilibrium is still false.
internal sealed record EquilibriumResult(
    bool IsEquilibrium,
    TimeSpan ObservedWindow,
    double WorstSpanC,
    double WorstAbsoluteSlopeCPerMinute,
    string? LimitingChannel);

/// <summary>
/// Operational steady-state test: every channel must remain within MaxSpanC and
/// have an absolute least-squares slope no greater than MaxSlopeCPerMinute for
/// the whole Window. This tests temporal stability, not equality between sensors.
/// </summary>
// See EQUILIBRIUM_DETECTION_GUIDE.md for a full walkthrough with worked examples.
internal sealed class EquilibriumDetector
{
    // Rolling buffer of every snapshot still inside the trailing Window.
    private readonly Queue<TemperatureSnapshot> _history = new();
    private readonly IReadOnlyList<string> _channels;

    public EquilibriumDetector(
        IReadOnlyList<string> channels,
        TimeSpan window,
        double maxSpanC,
        double maxSlopeCPerMinute)
    {
        _channels = channels;
        Window = window;
        MaxSpanC = maxSpanC;
        MaxSlopeCPerMinute = maxSlopeCPerMinute;
    }

    public TimeSpan Window { get; }
    public double MaxSpanC { get; }
    public double MaxSlopeCPerMinute { get; }

    // Adds one new snapshot and evicts anything older than Window from the
    // front of the queue, so _history always holds roughly "the last Window
    // worth of readings" - never a single sample more than needed.
    public void Add(TemperatureSnapshot snapshot)
    {
        _history.Enqueue(snapshot);
        DateTimeOffset cutoff = snapshot.TimestampUtc - Window;
        while (_history.Count > 0 && _history.Peek().TimestampUtc < cutoff)
        {
            _history.Dequeue();
        }
    }

    // Decides whether every tracked channel has been stable for the whole
    // window. Returns false (never throws) for "not enough data yet", "a
    // channel's reading is missing/invalid", or "a channel failed the
    // span/slope test" - the caller (Program.cs) just polls this every
    // iteration until it returns true.
    public EquilibriumResult Evaluate()
    {
        // Too few points to fit a meaningful trend line yet.
        if (_history.Count < 3)
        {
            return new EquilibriumResult(false, TimeSpan.Zero, double.NaN, double.NaN, null);
        }

        TemperatureSnapshot[] samples = _history.ToArray();
        TimeSpan observed = samples[^1].TimestampUtc - samples[0].TimestampUtc;
        // The buffer only fills up to Window over time - don't declare
        // equilibrium off a shorter window just because a couple of samples
        // haven't arrived yet (the 2 s slack absorbs normal polling jitter).
        if (observed < Window - TimeSpan.FromSeconds(2))
        {
            return new EquilibriumResult(false, observed, double.NaN, double.NaN, null);
        }

        double worstSpan = 0;
        double worstSlope = 0;
        string? limitingChannel = null;

        // Check every channel independently; the group only passes once none
        // of them fail, so a single misbehaving channel blocks the whole result.
        foreach (string channel in _channels)
        {
            double[] values = new double[samples.Length];
            double[] timeSeconds = new double[samples.Length];
            DateTimeOffset origin = samples[0].TimestampUtc;

            for (int index = 0; index < samples.Length; index++)
            {
                // A missing, non-finite, or wildly out-of-range reading (e.g. a
                // disconnected thermocouple) fails the check immediately rather
                // than letting garbage data feed the slope/span calculation.
                if (!samples[index].Values.TryGetValue(channel, out double value) ||
                    !double.IsFinite(value) ||
                    Math.Abs(value) > 1_000_000)
                {
                    return new EquilibriumResult(false, observed, double.NaN, double.NaN, channel);
                }

                values[index] = value;
                timeSeconds[index] = (samples[index].TimestampUtc - origin).TotalSeconds;
            }

            // Span: how far the reading has wandered within the window.
            double span = values.Max() - values.Min();
            // Slope: the window's linear trend, in °C/minute, regardless of direction.
            double slopePerMinute = Math.Abs(LeastSquaresSlope(timeSeconds, values) * 60.0);

            // Track which channel is currently furthest from passing, purely
            // for the "LimitingChannel" reported back to the caller's logging.
            if (span > worstSpan || slopePerMinute > worstSlope)
            {
                limitingChannel = channel;
            }

            worstSpan = Math.Max(worstSpan, span);
            worstSlope = Math.Max(worstSlope, slopePerMinute);

            if (span > MaxSpanC || slopePerMinute > MaxSlopeCPerMinute)
            {
                return new EquilibriumResult(false, observed, worstSpan, worstSlope, limitingChannel);
            }
        }

        return new EquilibriumResult(true, observed, worstSpan, worstSlope, limitingChannel);
    }

    // Ordinary least-squares slope (dy/dx) of y over x. Returns +infinity for
    // the degenerate case of every x value being identical (a zero-width
    // window), which would otherwise divide by zero - that case can't
    // actually occur given the Window-length check in Evaluate() above, but
    // the guard keeps this method safe to call on its own.
    private static double LeastSquaresSlope(double[] x, double[] y)
    {
        double meanX = x.Average();
        double meanY = y.Average();
        double numerator = 0;
        double denominator = 0;

        for (int index = 0; index < x.Length; index++)
        {
            double dx = x[index] - meanX;
            numerator += dx * (y[index] - meanY);
            denominator += dx * dx;
        }

        return denominator <= 0 ? double.PositiveInfinity : numerator / denominator;
    }
}
