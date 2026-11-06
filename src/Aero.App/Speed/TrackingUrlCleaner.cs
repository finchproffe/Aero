namespace Aero.App.Speed;

public static class TrackingUrlCleaner
{
    private static readonly HashSet<string> TrackingParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "dclid",
        "fbclid",
        "gclid",
        "gbraid",
        "igshid",
        "mc_cid",
        "mc_eid",
        "msclkid",
        "oly_anon_id",
        "oly_enc_id",
        "rb_clickid",
        "s_cid",
        "twclid",
        "vero_conv",
        "vero_id",
        "wbraid",
        "wickedid",
        "yclid"
    };

    public static Uri Clean(Uri uri)
    {
        if (!uri.IsAbsoluteUri || string.IsNullOrEmpty(uri.Query))
        {
            return uri;
        }

        string rawQuery = uri.Query.TrimStart('?');
        string[] parts = rawQuery.Split('&', StringSplitOptions.RemoveEmptyEntries);
        List<string> kept = new(parts.Length);
        bool removed = false;

        foreach (string part in parts)
        {
            int equals = part.IndexOf('=');
            string rawName = equals >= 0 ? part[..equals] : part;
            string name;

            try
            {
                name = Uri.UnescapeDataString(rawName.Replace('+', ' '));
            }
            catch
            {
                kept.Add(part);
                continue;
            }

            if (name.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) ||
                TrackingParameters.Contains(name))
            {
                removed = true;
                continue;
            }

            kept.Add(part);
        }

        if (!removed)
        {
            return uri;
        }

        UriBuilder builder = new(uri)
        {
            Query = string.Join('&', kept)
        };

        return builder.Uri;
    }
}
