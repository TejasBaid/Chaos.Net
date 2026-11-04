using System.Text;

namespace ChaosNet.Tracing;

/// <summary>One scheduling step of a recorded execution.</summary>
/// <param name="Step">One-based step number.</param>
/// <param name="OperationId">The operation that ran, or <see cref="Scheduling.SchedulableOperation.ClockId"/> for the clock.</param>
/// <param name="Events">Events that happened during the step (lock acquisitions, register reads, logs, random choices, ...).</param>
public sealed record TimelineStep(int Step, int OperationId, IReadOnlyList<string> Events);

/// <summary>Lifetime and label of an operation in a recorded execution.</summary>
/// <param name="Id">Operation id.</param>
/// <param name="Label">Label.</param>
/// <param name="CreatedAtStep">Step during which the operation was created (0 for the root).</param>
/// <param name="CompletedAtStep">Step during which its task completed, or <see langword="null"/> if it never did.</param>
public sealed record OperationInfo(int Id, string Label, int CreatedAtStep, int? CompletedAtStep);

/// <summary>Renders an execution as a lane-per-operation timeline.</summary>
public static class TimelineRenderer
{
    private const int LaneWidth = 5;

    /// <summary>Renders a timeline.</summary>
    /// <param name="steps">Recorded steps.</param>
    /// <param name="operations">Operations of the execution.</param>
    /// <param name="footer">Optional text appended at the end (for example the bug).</param>
    /// <param name="maxSteps">Maximum number of steps to print; the middle of longer executions is elided.</param>
    public static string Render(IReadOnlyList<TimelineStep> steps, IReadOnlyList<OperationInfo> operations, string? footer = null, int maxSteps = 400)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(operations);

        bool clockUsed = steps.Any(s => s.OperationId == Scheduling.SchedulableOperation.ClockId);
        var lanes = operations.Select(o => o.Id).ToList();
        if (clockUsed)
        {
            lanes.Add(Scheduling.SchedulableOperation.ClockId);
        }

        var sb = new StringBuilder();
        sb.AppendLine("Operations:");
        foreach (OperationInfo op in operations)
        {
            string life = op.CompletedAtStep is { } c ? $"steps {op.CreatedAtStep}..{c}" : $"from step {op.CreatedAtStep}, never completed";
            sb.Append("  ").Append(LaneName(op.Id).PadRight(6)).Append(op.Label).Append("  (").Append(life).AppendLine(")");
        }

        if (clockUsed)
        {
            sb.AppendLine("  clk   virtual clock (advances time and fires the next timer)");
        }

        sb.AppendLine();
        sb.Append(" step |");
        foreach (int lane in lanes)
        {
            sb.Append(Center(LaneName(lane), LaneWidth));
        }

        sb.AppendLine("| events");
        sb.Append("------+").Append(new string('-', LaneWidth * lanes.Count)).AppendLine("+-------");

        var byId = operations.ToDictionary(o => o.Id);
        int head = steps.Count <= maxSteps ? steps.Count : maxSteps / 4;
        int tailStart = steps.Count <= maxSteps ? steps.Count : steps.Count - (maxSteps - head);
        for (int i = 0; i < steps.Count; i++)
        {
            if (i == head && head < tailStart)
            {
                sb.Append("  ... ").Append(tailStart - head).AppendLine(" steps elided ...");
                i = tailStart - 1;
                continue;
            }

            TimelineStep step = steps[i];
            sb.Append(step.Step.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(5)).Append(" |");
            foreach (int lane in lanes)
            {
                string cell;
                if (lane == step.OperationId)
                {
                    cell = "#";
                }
                else if (lane != Scheduling.SchedulableOperation.ClockId && byId.TryGetValue(lane, out OperationInfo? info)
                    && info.CreatedAtStep < step.Step && (info.CompletedAtStep is not { } done || done >= step.Step))
                {
                    cell = ":";
                }
                else
                {
                    cell = " ";
                }

                sb.Append(Center(cell, LaneWidth));
            }

            sb.Append("| ");
            sb.AppendLine(step.Events.Count == 0 ? string.Empty : string.Join("; ", step.Events));
        }

        sb.AppendLine();
        sb.AppendLine("Legend: # = ran this step, : = alive (created, not completed)");
        if (footer is not null)
        {
            sb.AppendLine(footer);
        }

        return sb.ToString();
    }

    private static string LaneName(int id) => id == Scheduling.SchedulableOperation.ClockId ? "clk" : $"op{id}";

    private static string Center(string text, int width)
    {
        if (text.Length >= width)
        {
            return text[..width];
        }

        int left = (width - text.Length) / 2;
        return new string(' ', left) + text + new string(' ', width - text.Length - left);
    }
}
