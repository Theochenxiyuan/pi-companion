using System.Text;

namespace PiCompanion.Application.Evidence;

internal static class UnifiedDiffBuilder
{
    private const int MaximumInputBytes = 1024 * 1024;
    private const int MaximumLinesPerSide = 1200;
    private const int MaximumDiffCharacters = 256 * 1024;

    public static DiffBuildResult Build(byte[] before, byte[] after, string path)
    {
        if (before.AsSpan().SequenceEqual(after))
        {
            return new DiffBuildResult(string.Empty, false, false);
        }

        if (before.Length > MaximumInputBytes || after.Length > MaximumInputBytes)
        {
            return new DiffBuildResult(null, false, true);
        }

        if (!TryDecode(before, out var beforeText) || !TryDecode(after, out var afterText))
        {
            return new DiffBuildResult(null, true, false);
        }

        var oldLines = SplitLines(beforeText);
        var newLines = SplitLines(afterText);
        if (oldLines.Length > MaximumLinesPerSide || newLines.Length > MaximumLinesPerSide)
        {
            return new DiffBuildResult(null, false, true);
        }

        var matrix = new int[oldLines.Length + 1, newLines.Length + 1];
        for (var oldIndex = oldLines.Length - 1; oldIndex >= 0; oldIndex--)
        {
            for (var newIndex = newLines.Length - 1; newIndex >= 0; newIndex--)
            {
                matrix[oldIndex, newIndex] = string.Equals(oldLines[oldIndex], newLines[newIndex], StringComparison.Ordinal)
                    ? matrix[oldIndex + 1, newIndex + 1] + 1
                    : Math.Max(matrix[oldIndex + 1, newIndex], matrix[oldIndex, newIndex + 1]);
            }
        }

        var builder = new StringBuilder();
        builder.Append("--- a/").Append(path.Replace('\\', '/')).AppendLine();
        builder.Append("+++ b/").Append(path.Replace('\\', '/')).AppendLine();
        builder.Append("@@ -1,").Append(oldLines.Length).Append(" +1,").Append(newLines.Length).AppendLine(" @@");
        var left = 0;
        var right = 0;
        while (left < oldLines.Length || right < newLines.Length)
        {
            if (left < oldLines.Length && right < newLines.Length &&
                string.Equals(oldLines[left], newLines[right], StringComparison.Ordinal))
            {
                builder.Append(' ').AppendLine(oldLines[left]);
                left++;
                right++;
            }
            else if (right < newLines.Length &&
                     (left == oldLines.Length || matrix[left, right + 1] >= matrix[left + 1, right]))
            {
                builder.Append('+').AppendLine(newLines[right++]);
            }
            else
            {
                builder.Append('-').AppendLine(oldLines[left++]);
            }

            if (builder.Length > MaximumDiffCharacters)
            {
                return new DiffBuildResult(builder.ToString(0, MaximumDiffCharacters), false, true);
            }
        }

        return new DiffBuildResult(builder.ToString(), false, false);
    }

    private static bool TryDecode(byte[] bytes, out string text)
    {
        if (bytes.AsSpan(0, Math.Min(bytes.Length, 8192)).Contains((byte)0))
        {
            text = string.Empty;
            return false;
        }

        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static string[] SplitLines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
}

internal sealed record DiffBuildResult(string? Text, bool IsBinary, bool Truncated);
