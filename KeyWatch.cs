namespace ASD;

/// <summary>Looks at the expiry dates in the Keys Store (they are plain metadata, so no unlock is needed) and posts a warning for keys that are close to expiring or have expired.</summary>
internal static class KeyWatch
{
    private static readonly HashSet<string> Announced = new(StringComparer.OrdinalIgnoreCase);   // this session only

    public static void Check()
    {
        try
        {
            if (!KeyVault.Exists) return;
            var days = int.TryParse(SettingsStore.Get("keys.expiryDays", "7"), out var d) ? Math.Clamp(d, 1, 365) : 7;
            var hide = SettingsStore.Get("notify.hideNames", "off") == "on";
            var dismissed = Notifier.Dismissed();

            foreach (var e in KeyVault.All())
            {
                if (e.Expires is not DateTime exp) continue;
                var left = (exp.Date - DateTime.Today).Days;
                if (left > days) continue;

                var expired = left < 0;
                var token = $"{e.Id}|{exp:yyyy-MM-dd}|{(expired ? "expired" : "soon")}";
                if (dismissed.Contains(token) || !Announced.Add(token)) continue;

                var when = expired ? $"Expired on {exp:yyyy-MM-dd}"
                         : left == 0 ? "Expires today"
                         : left == 1 ? "Expires tomorrow"
                         : $"Expires in {left} days ({exp:yyyy-MM-dd})";
                Notifier.Post(expired ? NoteKind.Error : NoteKind.Warning,
                    e.Name, $"Keys Store · {when}", "keys", token,
                    hide ? (expired ? "A key has expired" : "A key is about to expire") : null,
                    hide ? "Open the Keys Store to see which one." : null);
            }
        }
        catch { /* a warning must never break the app */ }
    }
}
