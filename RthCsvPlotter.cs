using System.Globalization;

namespace HiokiThermalEquilibriumExample;

internal static class RthCsvPlotter
{
    public static void Write(string csvPath, string svgPath, string title)
    {
        string[] lines = File.ReadLines(csvPath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        if (lines.Length < 3)
        {
            throw new InvalidDataException("The normalised RTH CSV has too few rows to plot.");
        }

        string[] headings = ParseCsvLine(lines[0]);
        if (headings.Length < 2)
        {
            throw new InvalidDataException("The normalised RTH CSV has no signal columns.");
        }

        List<double[]> rows = [];
        foreach (string line in lines.Skip(1))
        {
            string[] fields = ParseCsvLine(line);
            if (fields.Length != headings.Length)
            {
                continue;
            }

            double[] row = new double[fields.Length];
            bool valid = true;
            for (int column = 0; column < fields.Length; column++)
            {
                if (!double.TryParse(fields[column], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out row[column]) || !double.IsFinite(row[column]))
                {
                    valid = false;
                    break;
                }
            }

            if (valid) rows.Add(row);
        }

        if (rows.Count < 2)
        {
            throw new InvalidDataException("The normalised RTH CSV has no plottable waveform rows.");
        }

        double[] times = rows.Select(row => row[0]).ToArray();
        string[] channels = headings.Skip(1).ToArray();
        Dictionary<string, double[]> values = new(StringComparer.OrdinalIgnoreCase);
        for (int column = 1; column < headings.Length; column++)
        {
            string heading = string.IsNullOrWhiteSpace(headings[column])
                ? $"Signal_{column}"
                : headings[column];
            if (values.ContainsKey(heading)) heading += $"_{column}";
            values[heading] = rows.Select(row => row[column]).ToArray();
            channels[column - 1] = heading;
        }

        SvgPlotter.WriteSeries(svgPath, times, channels, values, title, "Time from trigger (s)", "Voltage (V)");
    }

    private static string[] ParseCsvLine(string line)
    {
        List<string> fields = [];
        System.Text.StringBuilder current = new();
        bool quoted = false;
        for (int index = 0; index < line.Length; index++)
        {
            char character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }

        fields.Add(current.ToString());
        return fields.ToArray();
    }
}
