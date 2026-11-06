namespace ASD;

public static class TimeTools
{
    private static string Plural(long n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")}";

    public static string Human(TimeSpan span)
    {
        var s = Math.Abs(span.TotalSeconds);
        if (s < 60) return Plural((long)s, "second");
        if (s < 3600) return Plural((long)(s / 60), "minute");
        if (s < 172800) return Plural((long)(s / 3600), "hour");
        var days = s / 86400;
        if (days < 60) return Plural((long)days, "day");
        if (days < 730) return Plural((long)(days / 30), "month");
        return Plural((long)(days / 365), "year");
    }

    public static string Relative(DateTimeOffset when, DateTimeOffset now)
    {
        var d = when - now;
        if (Math.Abs(d.TotalSeconds) < 1) return "now";
        return d > TimeSpan.Zero ? "in " + Human(d) : Human(d) + " ago";
    }
}
