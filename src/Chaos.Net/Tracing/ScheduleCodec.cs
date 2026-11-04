using System.Globalization;
using System.Text;

namespace ChaosNet.Tracing;

/// <summary>
/// Compact text encoding of a decision sequence: space-separated tokens with run-length compression.
/// <c>3</c> schedules op3, <c>t</c> fires the clock, <c>b1</c>/<c>b0</c> are booleans, <c>i4</c> is an integer,
/// and <c>2*5</c> repeats a token five times. Example: <c>0 1*3 2 t b1 i4</c>.
/// </summary>
public static class ScheduleCodec
{
    /// <summary>Encodes decisions.</summary>
    public static string Encode(IReadOnlyList<Decision> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        var sb = new StringBuilder(decisions.Count * 2);
        int i = 0;
        while (i < decisions.Count)
        {
            Decision d = decisions[i];
            int run = 1;
            while (i + run < decisions.Count && decisions[i + run] == d)
            {
                run++;
            }

            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(d.ToString());
            if (run > 1)
            {
                sb.Append('*').Append(run.ToString(CultureInfo.InvariantCulture));
            }

            i += run;
        }

        return sb.ToString();
    }

    /// <summary>Decodes a string produced by <see cref="Encode"/>.</summary>
    /// <exception cref="FormatException">The text is not a valid schedule.</exception>
    public static IReadOnlyList<Decision> Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new List<Decision>();
        foreach (string raw in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string token = raw;
            int count = 1;
            int star = token.IndexOf('*', StringComparison.Ordinal);
            if (star >= 0)
            {
                count = int.Parse(token.AsSpan(star + 1), NumberStyles.None, CultureInfo.InvariantCulture);
                token = token[..star];
            }

            Decision d = token switch
            {
                "t" => new Decision(DecisionKind.Schedule, Scheduling.SchedulableOperation.ClockId),
                "b0" => new Decision(DecisionKind.Boolean, 0),
                "b1" => new Decision(DecisionKind.Boolean, 1),
                _ when token.StartsWith('i') => new Decision(DecisionKind.Integer, int.Parse(token.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture)),
                _ => new Decision(DecisionKind.Schedule, int.Parse(token, NumberStyles.None, CultureInfo.InvariantCulture)),
            };

            for (int k = 0; k < count; k++)
            {
                result.Add(d);
            }
        }

        return result;
    }

    /// <summary>Computes a stable 64-bit FNV-1a hash of a decision sequence (used to count distinct schedules).</summary>
    public static ulong Hash(IReadOnlyList<Decision> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        ulong h = 14695981039346656037UL;
        for (int i = 0; i < decisions.Count; i++)
        {
            h = unchecked((h ^ (byte)decisions[i].Kind) * 1099511628211UL);
            h = unchecked((h ^ (uint)decisions[i].Value) * 1099511628211UL);
        }

        return h;
    }
}
