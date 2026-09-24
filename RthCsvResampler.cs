using System.Globalization;
using System.Text;

namespace HiokiThermalEquilibriumExample;

internal sealed record RthCsvResampleResult(
    int SourceRows,
    int OutputRows,
    int SignalColumns,
    double SourceMedianIntervalSeconds);

internal static class RthCsvResampler
{
    public static RthCsvResampleResult Resample(
        string inputPath,
        string outputPath,
        TimeSpan targetInterval)
    {
        if (targetInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(targetInterval));
        }

        string text = File.ReadAllText(inputPath);
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        NumericBlock block = FindNumericBlock(lines);
        if (block.Rows.Count < 3)
        {
            throw new InvalidDataException("No multi-row numeric waveform block was found.");
        }

        int columnCount = block.Rows[0].Length;
        if (columnCount < 2)
        {
            throw new InvalidDataException("The exported waveform has no signal columns.");
        }

        double[] time = block.Rows.Select(row => row[0]).ToArray();
        for (int index = 1; index < time.Length; index++)
        {
            if (!(time[index] > time[index - 1]))
            {
                throw new InvalidDataException(
                    "The first numeric CSV column is not strictly increasing time.");
            }
        }

        double[] intervals = Enumerable.Range(1, time.Length - 1)
            .Select(index => time[index] - time[index - 1])
            .OrderBy(value => value)
            .ToArray();
        double medianInterval = intervals[intervals.Length / 2];
        double step = targetInterval.TotalSeconds;
        if (medianInterval > step * 1.001)
        {
            throw new InvalidDataException(FormattableString.Invariant(
                $"Native CSV interval {medianInterval:G9} s is coarser than target {step:G9} s."));
        }

        long firstGrid = checked((long)Math.Ceiling(time[0] / step - 1e-9));
        long lastGrid = checked((long)Math.Floor(time[^1] / step + 1e-9));
        if (lastGrid < firstGrid)
        {
            throw new InvalidDataException("The waveform is shorter than one target sample interval.");
        }

        string[] headings = FindHeadings(lines, block.StartLine, block.Delimiter, columnCount);
        using StreamWriter writer = new(outputPath, append: false, new UTF8Encoding(false));
        writer.WriteLine(string.Join(',', headings.Select(EscapeCsv)));

        int sourceIndex = 0;
        int outputRows = 0;
        for (long gridIndex = firstGrid; gridIndex <= lastGrid; gridIndex++)
        {
            double targetTime = gridIndex * step;
            while (sourceIndex + 1 < block.Rows.Count &&
                   block.Rows[sourceIndex + 1][0] < targetTime)
            {
                sourceIndex++;
            }

            int nextIndex = Math.Min(sourceIndex + 1, block.Rows.Count - 1);
            double[] before = block.Rows[sourceIndex];
            double[] after = block.Rows[nextIndex];
            double denominator = after[0] - before[0];
            double fraction = denominator <= 0 ? 0 : (targetTime - before[0]) / denominator;
            fraction = Math.Clamp(fraction, 0, 1);

            writer.Write(targetTime.ToString("G17", CultureInfo.InvariantCulture));
            for (int column = 1; column < columnCount; column++)
            {
                double value = before[column] + (after[column] - before[column]) * fraction;
                writer.Write(',');
                writer.Write(value.ToString("G17", CultureInfo.InvariantCulture));
            }

            writer.WriteLine();
            outputRows++;
        }

        return new RthCsvResampleResult(
            block.Rows.Count,
            outputRows,
            columnCount - 1,
            medianInterval);
    }

    private static NumericBlock FindNumericBlock(string[] lines)
    {
        NumericBlock? best = null;
        foreach (char delimiter in new[] { ',', ';', '\t' })
        {
            int currentStart = -1;
            List<double[]> currentRows = [];

            for (int lineIndex = 0; lineIndex <= lines.Length; lineIndex++)
            {
                double[]? row = null;
                bool valid = lineIndex < lines.Length &&
                    TryParseNumericRow(lines[lineIndex], delimiter, out row) &&
                    row.Length >= 2 &&
                    (currentRows.Count == 0 || row.Length == currentRows[0].Length);

                if (valid)
                {
                    if (currentStart < 0)
                    {
                        currentStart = lineIndex;
                    }

                    currentRows.Add(row!);
                    continue;
                }

                if (currentRows.Count >= 3 && (best is null || currentRows.Count > best.Rows.Count))
                {
                    best = new NumericBlock(delimiter, currentStart, [.. currentRows]);
                }

                currentStart = -1;
                currentRows.Clear();
            }
        }

        return best ?? throw new InvalidDataException(
            "Could not identify the numeric waveform rows in the RTH CSV export.");
    }

    private static bool TryParseNumericRow(
        string line,
        char delimiter,
        out double[] values)
    {
        string[] fields = line.Split(delimiter);
        values = new double[fields.Length];
        if (fields.Length < 2)
        {
            return false;
        }

        for (int index = 0; index < fields.Length; index++)
        {
            string value = fields[index].Trim().Trim('"');
            if (!double.TryParse(
                    value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out values[index]) ||
                !double.IsFinite(values[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static string[] FindHeadings(
        string[] lines,
        int dataStart,
        char delimiter,
        int columnCount)
    {
        for (int index = dataStart - 1; index >= Math.Max(0, dataStart - 12); index--)
        {
            string[] fields = lines[index].Split(delimiter)
                .Select(field => field.Trim().Trim('"'))
                .ToArray();
            if (fields.Length == columnCount &&
                fields.Any(field => field.Contains("time", StringComparison.OrdinalIgnoreCase)))
            {
                fields[0] = "Time_s";
                for (int column = 1; column < fields.Length; column++)
                {
                    if (string.IsNullOrWhiteSpace(fields[column]))
                    {
                        fields[column] = $"Signal_{column}";
                    }
                }

                return fields;
            }
        }

        return Enumerable.Range(0, columnCount)
            .Select(index => index == 0 ? "Time_s" : $"Signal_{index}")
            .ToArray();
    }

    private static string EscapeCsv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? '"' + value.Replace("\"", "\"\"") + '"'
            : value;

    private sealed record NumericBlock(
        char Delimiter,
        int StartLine,
        List<double[]> Rows);
}
