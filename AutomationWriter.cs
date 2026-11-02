using System.Globalization;
using System.Text;

namespace ASD;

/// <summary>
/// Turns an <see cref="AutoFile"/> back into .automation text (the editor saves with this).
/// Every value is written on one line; comments of a hand-written file are not kept when the editor saves.
/// The text always parses back to the same cells and arrows (the self-test checks that with --roundtrip).
/// </summary>
internal static class AutomationWriter
{
    private static string One(string? s) => (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();

    public static string ToText(AutoFile f)
    {
        var sb = new StringBuilder();
        if (One(f.Name).Length > 0) sb.Append("automation: ").Append(One(f.Name)).Append('\n').Append('\n');

        for (var i = 0; i < f.Cells.Count; i++)
        {
            var c = f.Cells[i];
            if (i > 0) sb.Append('\n');
            sb.Append("cell ").Append(c.Id).Append(":\n");

            void Line(string key, string value) => sb.Append("  ").Append(key).Append(": ").Append(value).Append('\n');

            if (One(c.Title).Length > 0) Line("title", One(c.Title));
            if (One(c.Note).Length > 0) Line("note", One(c.Note));
            if (c.X >= 0 && c.Y >= 0) Line("at", c.X.ToString(CultureInfo.InvariantCulture) + " " + c.Y.ToString(CultureInfo.InvariantCulture));

            switch (c.Step)
            {
                case StepKind.Run: Line("run", One(c.Command)); break;
                case StepKind.Http:
                    Line("http", c.HttpMethod.ToUpperInvariant() + " " + One(c.HttpUrl));
                    foreach (var h in c.Headers)
                        if (One(h.Key).Length > 0) Line("header", One(h.Key) + ": " + One(h.Value));
                    if (One(c.Body).Length > 0) Line("body", One(c.Body));
                    break;
                case StepKind.Notify:
                    Line("notify", One(c.Command));
                    if (c.Level != "info") Line("level", c.Level);
                    break;
                case StepKind.Wait: Line("wait", c.WaitSeconds.ToString("0.##", CultureInfo.InvariantCulture)); break;
            }

            if (c.TimeoutSeconds > 0) Line("timeout", c.TimeoutSeconds.ToString(CultureInfo.InvariantCulture));
            if (c.Join != JoinMode.Settle) Line("join", c.Join.ToString().ToLowerInvariant());

            foreach (var a in c.Out)
            {
                var word = a.Kind switch { ArrowKind.Success => "success", ArrowKind.Failure => "failure", _ => "always" };
                var head = a.When == null
                    ? word
                    : a.Kind == ArrowKind.Always ? "when " + One(a.When) : word + " when " + One(a.When);
                sb.Append("  ").Append(head).Append(" -> ").Append(a.To).Append('\n');
            }
        }
        return sb.ToString();
    }

    /// <summary>A copy made by writing and re-reading (so it is validated too). Null if the copy does not parse.</summary>
    public static AutoFile? Clone(AutoFile f)
    {
        var copy = AutomationParser.Parse(ToText(f));
        return copy.Ok ? copy : null;
    }
}
