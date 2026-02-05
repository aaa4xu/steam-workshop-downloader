using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

/// <summary>
/// Reads a text file containing one workshop ID per line for batch mode.
/// </summary>
internal static class WorkshopIdListReader
{
    public static List<ulong> ReadIds(string path)
    {
        var ids = new List<ulong>();
        var seen = new HashSet<ulong>();

        var lines = File.ReadAllLines(path);
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (raw.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            if (!ulong.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                Console.WriteLine($"Skipping invalid workshop id on line {i + 1}: {raw}");
                continue;
            }

            if (seen.Add(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }
}
