using System.Globalization;
using System.Text;
using GAVIMDataProcessing.Instrumentation;

namespace HiokiThermalEquilibriumExample;

internal static class SvgPlotter
{
    private static readonly string[] Colours =
    [
        "#0072B2", "#D55E00", "#009E73", "#CC79A7", "#E69F00",
        "#56B4E9", "#F0E442", "#6A3D9A", "#1B9E77", "#E7298A",
        "#66A61E", "#E6AB02", "#7570B3"
    ];

    public static void WriteRecording(string path, Lr8400Recording recording, string title)
        => WriteRecording(path, recording, recording.Channels, null, title, "Temperature (°C)");

    public static void WriteRecording(
        string path,
        Lr8400Recording recording,
        IReadOnlyList<string> channels,
        IReadOnlyDictionary<string, string>? displayLabels,
        string title,
        string yAxisLabel = "Temperature (°C)")
    {
        double[] times = Enumerable.Range(0, recording.PointCount)
            .Select(recording.TimeFromTriggerSeconds)
            .ToArray();
        Write(
            path,
            times,
            channels,
            recording.ChannelValues,
            title,
            "Time from event (s)",
            displayLabels,
            yAxisLabel);
    }

    public static void WriteLive(
        string path,
        IReadOnlyList<TemperatureSnapshot> snapshots,
        IReadOnlyList<string> channels,
        string title)
        => WriteLive(path, snapshots, channels, null, title, "Temperature (°C)");

    public static void WriteLive(
        string path,
        IReadOnlyList<TemperatureSnapshot> snapshots,
        IReadOnlyList<string> channels,
        IReadOnlyDictionary<string, string>? displayLabels,
        string title,
        string yAxisLabel)
    {
        if (snapshots.Count < 2)
        {
            return;
        }

        DateTimeOffset origin = snapshots[0].TimestampUtc;
        double[] times = snapshots.Select(item => (item.TimestampUtc - origin).TotalSeconds).ToArray();
        Dictionary<string, double[]> values = channels.ToDictionary(
            channel => channel,
            channel => snapshots.Select(item => item.Values[channel]).ToArray(),
            StringComparer.OrdinalIgnoreCase);
        Write(path, times, channels, values, title, "Elapsed monitoring time (s)", displayLabels, yAxisLabel);
    }

    public static void WriteSeries(
        string path,
        double[] times,
        IReadOnlyList<string> channels,
        IReadOnlyDictionary<string, double[]> values,
        string title,
        string xAxisLabel,
        string yAxisLabel) =>
        Write(path, times, channels, values, title, xAxisLabel, null, yAxisLabel);

    public static void WritePlaceholder(string path, string title)
    {
        const int width = 1600;
        const int height = 900;
        string svg =
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\">" +
            "<rect width=\"100%\" height=\"100%\" fill=\"white\"/>" +
            $"<text x=\"{width / 2}\" y=\"{height / 2}\" text-anchor=\"middle\" " +
            $"font-family=\"Arial,sans-serif\" font-size=\"28\">{Escape(title)}</text></svg>";
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory());
        File.WriteAllText(fullPath, svg, new UTF8Encoding(false));
    }

    private static void Write(
        string path,
        double[] times,
        IReadOnlyList<string> channels,
        IReadOnlyDictionary<string, double[]> values,
        string title,
        string xAxisLabel,
        IReadOnlyDictionary<string, string>? displayLabels,
        string yAxisLabel)
    {
        const int width = 1600;
        const int height = 900;
        const int left = 100;
        const int right = 300;
        const int top = 75;
        const int bottom = 85;
        int plotWidth = width - left - right;
        int plotHeight = height - top - bottom;

        int[] indices = DownsampleIndices(times.Length, 2400);
        IEnumerable<double> finiteValues = channels
            .SelectMany(channel => indices.Select(index => values[channel][index]))
            .Where(value => double.IsFinite(value) && Math.Abs(value) < 1_000_000);

        if (!finiteValues.Any())
        {
            return;
        }

        double minX = times.First();
        double maxX = times.Last();
        if (maxX <= minX)
        {
            maxX = minX + 1;
        }

        double minY = finiteValues.Min();
        double maxY = finiteValues.Max();
        double padding = Math.Max(0.5, (maxY - minY) * 0.05);
        minY -= padding;
        maxY += padding;

        double X(double value) => left + (value - minX) / (maxX - minX) * plotWidth;
        double Y(double value) => top + (maxY - value) / (maxY - minY) * plotHeight;
        string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        StringBuilder svg = new();
        svg.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\">");
        svg.AppendLine("<rect width=\"100%\" height=\"100%\" fill=\"white\"/>");
        svg.AppendLine($"<text x=\"{left}\" y=\"38\" font-family=\"Arial,sans-serif\" font-size=\"25\" font-weight=\"bold\">{Escape(title)}</text>");

        for (int tick = 0; tick <= 8; tick++)
        {
            double fraction = tick / 8.0;
            double xValue = minX + fraction * (maxX - minX);
            double x = left + fraction * plotWidth;
            svg.AppendLine($"<line x1=\"{F(x)}\" y1=\"{top}\" x2=\"{F(x)}\" y2=\"{top + plotHeight}\" stroke=\"#e5e7eb\"/>");
            svg.AppendLine($"<text x=\"{F(x)}\" y=\"{top + plotHeight + 28}\" text-anchor=\"middle\" font-family=\"Arial,sans-serif\" font-size=\"13\">{F(xValue)}</text>");

            double yValue = minY + fraction * (maxY - minY);
            double y = Y(yValue);
            svg.AppendLine($"<line x1=\"{left}\" y1=\"{F(y)}\" x2=\"{left + plotWidth}\" y2=\"{F(y)}\" stroke=\"#e5e7eb\"/>");
            svg.AppendLine($"<text x=\"{left - 12}\" y=\"{F(y + 5)}\" text-anchor=\"end\" font-family=\"Arial,sans-serif\" font-size=\"13\">{F(yValue)}</text>");
        }

        svg.AppendLine($"<rect x=\"{left}\" y=\"{top}\" width=\"{plotWidth}\" height=\"{plotHeight}\" fill=\"none\" stroke=\"#111827\"/>");

        if (minX < 0 && maxX > 0)
        {
            double triggerX = X(0);
            svg.AppendLine($"<line x1=\"{F(triggerX)}\" y1=\"{top}\" x2=\"{F(triggerX)}\" y2=\"{top + plotHeight}\" stroke=\"#c00000\" stroke-width=\"2\" stroke-dasharray=\"8 5\"/>");
        }

        for (int channelIndex = 0; channelIndex < channels.Count; channelIndex++)
        {
            string channel = channels[channelIndex];
            StringBuilder points = new();
            foreach (int index in indices)
            {
                double value = values[channel][index];
                if (double.IsFinite(value) && Math.Abs(value) < 1_000_000)
                {
                    points.Append(F(X(times[index]))).Append(',').Append(F(Y(value))).Append(' ');
                }
            }

            string colour = Colours[channelIndex % Colours.Length];
            string displayLabel = displayLabels is not null &&
                                  displayLabels.TryGetValue(channel, out string? configuredLabel)
                ? configuredLabel
                : channel;
            svg.AppendLine($"<polyline points=\"{points}\" fill=\"none\" stroke=\"{colour}\" stroke-width=\"1.5\"/>");
            int legendY = top + 22 + channelIndex * 28;
            svg.AppendLine($"<line x1=\"{left + plotWidth + 35}\" y1=\"{legendY}\" x2=\"{left + plotWidth + 70}\" y2=\"{legendY}\" stroke=\"{colour}\" stroke-width=\"3\"/>");
            svg.AppendLine($"<text x=\"{left + plotWidth + 80}\" y=\"{legendY + 5}\" font-family=\"Arial,sans-serif\" font-size=\"15\">{Escape(displayLabel)}</text>");
        }

        svg.AppendLine($"<text x=\"{left + plotWidth / 2}\" y=\"{height - 22}\" text-anchor=\"middle\" font-family=\"Arial,sans-serif\" font-size=\"16\">{Escape(xAxisLabel)}</text>");
        svg.AppendLine($"<text x=\"24\" y=\"{top + plotHeight / 2}\" transform=\"rotate(-90 24 {top + plotHeight / 2})\" text-anchor=\"middle\" font-family=\"Arial,sans-serif\" font-size=\"16\">{Escape(yAxisLabel)}</text>");
        svg.AppendLine("</svg>");

        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory());
        File.WriteAllText(fullPath, svg.ToString(), new UTF8Encoding(false));
    }

    private static int[] DownsampleIndices(int count, int maximumPoints)
    {
        if (count <= maximumPoints)
        {
            return Enumerable.Range(0, count).ToArray();
        }

        return Enumerable.Range(0, maximumPoints)
            .Select(index => (int)Math.Round(index * (count - 1.0) / (maximumPoints - 1.0)))
            .Distinct()
            .ToArray();
    }

    private static string Escape(string value) =>
        value.Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
}
