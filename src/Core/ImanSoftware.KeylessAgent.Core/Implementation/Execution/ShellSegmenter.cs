namespace ImanSoftware.KeylessAgent.Core.Implementation.Execution;

/// <summary>
/// Splits a command line the way the shell would run it — on <c>&amp;&amp;</c>, <c>||</c>,
/// <c>;</c> and <c>|</c> while respecting quotes — so a separator inside a quoted string never
/// becomes a segment boundary. Shared by the risk classifier and the deny-list policy so both
/// see identical segments.
/// </summary>
internal static class ShellSegmenter
{
    public static string[] Split(string commandLine)
    {
        var segments = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuote = false;
        char quote = default;

        foreach (var c in commandLine)
        {
            if (inQuote)
            {
                current.Append(c);
                if (c == quote)
                    inQuote = false;
                continue;
            }

            if (c is '"' or '\'')
            {
                inQuote = true;
                current.Append(c);
                continue;
            }

            if (c == ';' || c == '|')
            {
                segments.Add(current.ToString());
                current.Clear();
                continue;
            }

            if (c == '&')
            {
                // '&&' separates; a single '&' (background) stays in the segment.
                current.Append(c);
                continue;
            }

            current.Append(c);
        }

        segments.Add(current.ToString());

        var final = new List<string>();
        foreach (var segment in segments)
        {
            // split '&&' and '||' pairs
            var parts = System.Text.RegularExpressions.Regex.Split(
                segment, "&&|\\|\\|", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
            final.AddRange(parts.Select(p => p.Trim()).Where(p => p.Length > 0));
        }

        return final.ToArray();
    }
}
