namespace HiokiThermalEquilibriumExample;

internal sealed record TemperatureSnapshot(
    DateTimeOffset TimestampUtc,
    IReadOnlyDictionary<string, double> Values);

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
internal sealed class EquilibriumDetector
{
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

    public void Add(TemperatureSnapshot snapshot)
    {
        _history.Enqueue(snapshot);
        DateTimeOffset cutoff = snapshot.TimestampUtc - Window;
        while (_history.Count > 0 && _history.Peek().TimestampUtc < cutoff)
        {
            _history.Dequeue();
        }
    }

    public EquilibriumResult Evaluate()
    {
        if (_history.Count < 3)
        {
            return new EquilibriumResult(false, TimeSpan.Zero, double.NaN, double.NaN, null);
        }

        TemperatureSnapshot[] samples = _history.ToArray();
        TimeSpan observed = samples[^1].TimestampUtc - samples[0].TimestampUtc;
        if (observed < Window - TimeSpan.FromSeconds(2))
        {
            return new EquilibriumResult(false, observed, double.NaN, double.NaN, null);
        }

        double worstSpan = 0;
        double worstSlope = 0;
        string? limitingChannel = null;

        foreach (string channel in _channels)
        {
            double[] values = new double[samples.Length];
            double[] timeSeconds = new double[samples.Length];
            DateTimeOffset origin = samples[0].TimestampUtc;

            for (int index = 0; index < samples.Length; index++)
            {
                if (!samples[index].Values.TryGetValue(channel, out double value) ||
                    !double.IsFinite(value) ||
                    Math.Abs(value) > 1_000_000)
                {
                    return new EquilibriumResult(false, observed, double.NaN, double.NaN, channel);
                }

                values[index] = value;
                timeSeconds[index] = (samples[index].TimestampUtc - origin).TotalSeconds;
            }

            double span = values.Max() - values.Min();
            double slopePerMinute = Math.Abs(LeastSquaresSlope(timeSeconds, values) * 60.0);

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
