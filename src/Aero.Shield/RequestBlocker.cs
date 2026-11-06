using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text;

namespace Aero.Shield;

/// <summary>
/// Aero Shield's native filter engine. It parses a practical subset of the
/// Adblock Plus filter syntax used by EasyList/EasyPrivacy and keeps the hot
/// request path allocation-light. Unsupported advanced scriptlet operators are
/// ignored rather than guessed.
/// </summary>
public sealed class RequestBlocker
{
    private static readonly string[] BuiltInBlockedHosts =
    [
        "2mdn.net",
        "adform.net",
        "adnxs.com",
        "ads-twitter.com",
        "adsrvr.org",
        "amazon-adsystem.com",
        "app-measurement.com",
        "appsflyer.com",
        "casalemedia.com",
        "clarity.ms",
        "connect.facebook.net",
        "criteo.com",
        "criteo.net",
        "demdex.net",
        "doubleclick.net",
        "google-analytics.com",
        "googleadservices.com",
        "googlesyndication.com",
        "googletagmanager.com",
        "hotjar.com",
        "hotjar.io",
        "mathtag.com",
        "mixpanel.com",
        "moatads.com",
        "newrelic.com",
        "nr-data.net",
        "omtrdc.net",
        "openx.net",
        "outbrain.com",
        "pubmatic.com",
        "quantserve.com",
        "rubiconproject.com",
        "scorecardresearch.com",
        "segment.com",
        "segment.io",
        "smartadserver.com",
        "taboola.com",
        "yieldmo.com"
    ];

    private static readonly FrozenSet<string> BuiltInBlockedHostSet =
        BuiltInBlockedHosts.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] BrowserVerificationHosts =
    [
        "challenges.cloudflare.com",
        "hcaptcha.com",
        "recaptcha.net",
        "arkoselabs.com",
        "funcaptcha.com",
        "geetest.com",
        "geevisit.com",
        "datadome.co",
        "incapsula.com",
        "imperva.com",
        "perimeterx.net",
        "px-cdn.net"
    ];

    private static readonly string[] StrictHostTokens =
    [
        ".adservice.",
        ".adserver.",
        ".analytics.",
        ".beacon.",
        ".metrics.",
        ".pixel.",
        ".telemetry.",
        ".tracking."
    ];

    private static readonly string[] StrictUrlTokens =
    [
        "/adserver/",
        "/ads/",
        "/analytics/",
        "/beacon/",
        "/pixel/",
        "/telemetry/",
        "/tracking/",
        "collect?v=",
        "event.gif?",
        "pixel.gif?"
    ];

    private readonly ShieldFilterStore _store = new();
    private int _refreshInProgress;
    private readonly object _snapshotGate = new();
    private FilterListBundle _cachedBundle;
    private FilterSnapshot _snapshot = FilterSnapshot.CreateBuiltIn(BuiltInBlockedHosts);
    private bool _initialized;
    private bool _isUpdating;
    private string? _lastError;

    public event EventHandler? FiltersChanged;

    public int NetworkRuleCount => Volatile.Read(ref _snapshot).NetworkRuleCount;

    public static int FastRuleCount => BuiltInBlockedHosts.Length;

    public int CosmeticRuleCount => Volatile.Read(ref _snapshot).CosmeticRuleCount;

    public DateTimeOffset? LastUpdatedUtc => Volatile.Read(ref _snapshot).LastUpdatedUtc;

    public bool IsUpdating => _isUpdating;

    public string? LastError => _lastError;

    public async Task InitializeAsync(
        IEnumerable<string>? customRules,
        bool autoUpdate,
        CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        _cachedBundle = _store.LoadCached();
        ReplaceSnapshot(_cachedBundle, customRules);
        _initialized = true;

        if (autoUpdate && ShieldFilterStore.ShouldRefresh(_cachedBundle.UpdatedAtUtc))
        {
            _ = RefreshAsync(customRules, cancellationToken);
        }

        await Task.CompletedTask;
    }

    public async Task RefreshAsync(
        IEnumerable<string>? customRules,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _refreshInProgress, 1, 0) != 0)
        {
            return;
        }

        try
        {
            _isUpdating = true;
            _lastError = null;
            RaiseFiltersChanged();

            FilterListBundle downloaded = await _store.RefreshAsync(cancellationToken);
            _cachedBundle = downloaded;
            ReplaceSnapshot(downloaded, customRules);
        }
        catch (OperationCanceledException)
        {
            // Closing Aero should cancel quietly.
        }
        catch (Exception exception)
        {
            _lastError = exception.Message;
            RaiseFiltersChanged();
        }
        finally
        {
            _isUpdating = false;
            RaiseFiltersChanged();
            Volatile.Write(ref _refreshInProgress, 0);
        }
    }

    public void ApplyCustomRules(IEnumerable<string>? customRules)
    {
        ReplaceSnapshot(_cachedBundle, customRules);
    }

    public static bool ShouldBlockFastLane(
        Uri requestUri,
        string? normalizedDocumentHost,
        ShieldResourceContext resourceContext)
    {
        if (!IsHttpUri(requestUri) ||
            resourceContext == ShieldResourceContext.Document ||
            IsBrowserVerificationResource(requestUri))
        {
            return false;
        }

        string requestHost = NormalizeHost(requestUri.IdnHost);
        bool thirdParty = !string.IsNullOrEmpty(normalizedDocumentHost) &&
            !SameSite(requestHost, normalizedDocumentHost);

        if (!thirdParty)
        {
            return false;
        }

        return IsBuiltInBlockedHost(requestHost);
    }

    public bool ShouldBlock(
        Uri requestUri,
        string? normalizedDocumentHost,
        ShieldResourceContext resourceContext,
        ShieldLevel mode)
    {
        if (!IsHttpUri(requestUri) ||
            resourceContext == ShieldResourceContext.Document ||
            IsBrowserVerificationResource(requestUri))
        {
            return false;
        }

        FilterSnapshot snapshot = Volatile.Read(ref _snapshot);
        string requestHost = NormalizeHost(requestUri.IdnHost);
        string requestUrl = requestUri.AbsoluteUri;
        bool thirdParty = !string.IsNullOrEmpty(normalizedDocumentHost) &&
            !SameSite(requestHost, normalizedDocumentHost);
        ResourceMask resourceMask = ToResourceMask(resourceContext);

        if (snapshot.MatchesAllowRule(requestUri, requestHost, requestUrl, thirdParty, resourceMask))
        {
            return false;
        }

        if (snapshot.MatchesBlockRule(requestUri, requestHost, requestUrl, thirdParty, resourceMask))
        {
            return true;
        }

        if (mode != ShieldLevel.Strict || !thirdParty)
        {
            return false;
        }

        string dottedHost = "." + requestHost + ".";
        foreach (string token in StrictHostTokens)
        {
            if (dottedHost.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (resourceContext is ShieldResourceContext.Script or
            ShieldResourceContext.Image or
            ShieldResourceContext.XmlHttpRequest or
            ShieldResourceContext.Fetch)
        {
            foreach (string token in StrictUrlTokens)
            {
                if (requestUrl.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public string BuildCosmeticInjectionScript(string host, ShieldLevel mode)
    {
        FilterSnapshot snapshot = Volatile.Read(ref _snapshot);
        return snapshot.GetCosmeticInjectionScript(NormalizeHost(host), mode);
    }

    private void ReplaceSnapshot(FilterListBundle bundle, IEnumerable<string>? customRules)
    {
        FilterSnapshot parsed = FilterParser.Parse(
            bundle.EnumerateLists(),
            customRules,
            BuiltInBlockedHosts,
            bundle.UpdatedAtUtc);

        lock (_snapshotGate)
        {
            Volatile.Write(ref _snapshot, parsed);
        }

        RaiseFiltersChanged();
    }

    private void RaiseFiltersChanged() => FiltersChanged?.Invoke(this, EventArgs.Empty);

    private static bool IsBuiltInBlockedHost(string host)
    {
        if (BuiltInBlockedHostSet.Contains(host))
        {
            return true;
        }

        int labelEnd = host.IndexOf('.');
        while (labelEnd >= 0 && labelEnd + 1 < host.Length)
        {
            string parent = host[(labelEnd + 1)..];
            if (BuiltInBlockedHostSet.Contains(parent))
            {
                return true;
            }

            labelEnd = host.IndexOf('.', labelEnd + 1);
        }

        return false;
    }

    public static bool IsBrowserVerificationResource(Uri uri)
    {
        if (!IsHttpUri(uri))
        {
            return false;
        }

        string host = NormalizeHost(uri.IdnHost);
        foreach (string verificationHost in BrowserVerificationHosts)
        {
            if (host.Equals(verificationHost, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith('.' + verificationHost, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        string path = uri.AbsolutePath;
        return path.StartsWith("/cdn-cgi/challenge-platform", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/cdn-cgi/challenge/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/cdn-cgi/turnstile", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/captcha/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/recaptcha/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/arkose/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/challenge/", StringComparison.OrdinalIgnoreCase) &&
                (host.Contains("cloudflare", StringComparison.OrdinalIgnoreCase) ||
                 host.Contains("datadome", StringComparison.OrdinalIgnoreCase) ||
                 host.Contains("imperva", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsHttpUri(Uri uri) =>
        uri.IsAbsoluteUri &&
        (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
         uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeHost(string host) => host.Trim().TrimEnd('.').ToLowerInvariant();

    private static bool SameSite(string leftHost, string rightHost)
    {
        if (leftHost.Equals(rightHost, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string leftSite = RegistrableSite(leftHost);
        string rightSite = RegistrableSite(rightHost);
        return leftSite.Equals(rightSite, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly ConcurrentDictionary<string, string> RegistrableSiteCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static string RegistrableSite(string host) =>
        RegistrableSiteCache.GetOrAdd(host, static currentHost =>
        {
            string[] labels = currentHost.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (labels.Length <= 2)
            {
                return currentHost;
            }

            // A small pragmatic exception set avoids obvious co.uk/com.au mistakes.
            string suffix = labels[^2] + "." + labels[^1];
            if (labels.Length >= 3 && suffix is "co.uk" or "org.uk" or "com.au" or "co.jp" or "com.br")
            {
                return labels[^3] + "." + suffix;
            }

            return suffix;
        });

    private static ResourceMask ToResourceMask(ShieldResourceContext context) => context switch
    {
        ShieldResourceContext.Script => ResourceMask.Script,
        ShieldResourceContext.Image => ResourceMask.Image,
        ShieldResourceContext.Stylesheet => ResourceMask.Stylesheet,
        ShieldResourceContext.Font => ResourceMask.Font,
        ShieldResourceContext.Media => ResourceMask.Media,
        ShieldResourceContext.XmlHttpRequest => ResourceMask.XmlHttpRequest,
        ShieldResourceContext.Fetch => ResourceMask.XmlHttpRequest,
        ShieldResourceContext.Document => ResourceMask.Document,
        _ => ResourceMask.Other
    };

    [Flags]
    private enum ResourceMask
    {
        None = 0,
        Document = 1 << 0,
        Script = 1 << 1,
        Image = 1 << 2,
        Stylesheet = 1 << 3,
        Font = 1 << 4,
        Media = 1 << 5,
        XmlHttpRequest = 1 << 6,
        Subdocument = 1 << 7,
        Other = 1 << 8,
        All = Document | Script | Image | Stylesheet | Font | Media | XmlHttpRequest | Subdocument | Other
    }

    private sealed class FilterSnapshot
    {
        private readonly HashSet<string> _blockedDomains;
        private readonly HashSet<string> _allowedDomains;
        private readonly Dictionary<string, List<NetworkRule>> _domainBlockRules;
        private readonly Dictionary<string, List<NetworkRule>> _domainAllowRules;
        private readonly Dictionary<string, List<NetworkRule>> _genericBlockRules;
        private readonly Dictionary<string, List<NetworkRule>> _genericAllowRules;
        private readonly string[] _genericCosmeticSelectors;
        private readonly Dictionary<string, string[]> _domainCosmeticSelectors;
        private readonly Dictionary<string, HashSet<string>> _domainCosmeticExceptions;
        private readonly ConcurrentDictionary<string, string[]> _cosmeticCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _cosmeticScriptCache = new(StringComparer.OrdinalIgnoreCase);

        public FilterSnapshot(
            HashSet<string> blockedDomains,
            HashSet<string> allowedDomains,
            Dictionary<string, List<NetworkRule>> domainBlockRules,
            Dictionary<string, List<NetworkRule>> domainAllowRules,
            Dictionary<string, List<NetworkRule>> genericBlockRules,
            Dictionary<string, List<NetworkRule>> genericAllowRules,
            string[] genericCosmeticSelectors,
            Dictionary<string, string[]> domainCosmeticSelectors,
            Dictionary<string, HashSet<string>> domainCosmeticExceptions,
            int networkRuleCount,
            int cosmeticRuleCount,
            DateTimeOffset? lastUpdatedUtc)
        {
            _blockedDomains = blockedDomains;
            _allowedDomains = allowedDomains;
            _domainBlockRules = domainBlockRules;
            _domainAllowRules = domainAllowRules;
            _genericBlockRules = genericBlockRules;
            _genericAllowRules = genericAllowRules;
            _genericCosmeticSelectors = genericCosmeticSelectors;
            _domainCosmeticSelectors = domainCosmeticSelectors;
            _domainCosmeticExceptions = domainCosmeticExceptions;
            NetworkRuleCount = networkRuleCount;
            CosmeticRuleCount = cosmeticRuleCount;
            LastUpdatedUtc = lastUpdatedUtc;
        }

        public int NetworkRuleCount { get; }

        public int CosmeticRuleCount { get; }

        public DateTimeOffset? LastUpdatedUtc { get; }

        public static FilterSnapshot CreateBuiltIn(IEnumerable<string> builtInHosts) => new(
            new HashSet<string>(builtInHosts, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, List<NetworkRule>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, List<NetworkRule>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, List<NetworkRule>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, List<NetworkRule>>(StringComparer.OrdinalIgnoreCase),
            [],
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase),
            builtInHosts.Count(),
            0,
            null);

        public bool MatchesAllowRule(
            Uri requestUri,
            string requestHost,
            string requestUrl,
            bool thirdParty,
            ResourceMask resourceMask) =>
            HostOrParentInSet(_allowedDomains, requestHost) ||
            MatchesDomainRules(_domainAllowRules, requestHost, requestUrl, thirdParty, resourceMask) ||
            MatchesGenericRules(_genericAllowRules, requestUrl, thirdParty, resourceMask);

        public bool MatchesBlockRule(
            Uri requestUri,
            string requestHost,
            string requestUrl,
            bool thirdParty,
            ResourceMask resourceMask) =>
            HostOrParentInSet(_blockedDomains, requestHost) ||
            MatchesDomainRules(_domainBlockRules, requestHost, requestUrl, thirdParty, resourceMask) ||
            MatchesGenericRules(_genericBlockRules, requestUrl, thirdParty, resourceMask);

        public string[] GetCosmeticSelectors(string host, ShieldLevel mode)
        {
            string cacheKey = mode + ":" + host;
            return _cosmeticCache.GetOrAdd(cacheKey, _ => BuildCosmeticSelectors(host, mode));
        }

        public string GetCosmeticInjectionScript(string host, ShieldLevel mode)
        {
            string cacheKey = mode + ":" + host;
            return _cosmeticScriptCache.GetOrAdd(cacheKey, _ => BuildCosmeticInjectionScript(host, mode));
        }

        private string BuildCosmeticInjectionScript(string host, ShieldLevel mode)
        {
            string[] selectors = GetCosmeticSelectors(host, mode);
            if (selectors.Length == 0)
            {
                return string.Empty;
            }

            StringBuilder css = new(Math.Min(selectors.Length * 58, 250_000));
            foreach (string selector in selectors)
            {
                css.Append(selector)
                    .Append("{display:none!important;visibility:hidden!important;}");
            }

            string encodedCss = System.Text.Json.JsonSerializer.Serialize(css.ToString());
            return $$"""
(() => {
    const styleId = 'aero-shield-cosmetic-style';
    let style = document.getElementById(styleId);
    if (!style) {
        style = document.createElement('style');
        style.id = styleId;
        (document.head || document.documentElement).appendChild(style);
    }
    if (style.textContent !== {{encodedCss}}) style.textContent = {{encodedCss}};
})();
""";
        }

        private string[] BuildCosmeticSelectors(string host, ShieldLevel mode)
        {
            HashSet<string> selectors = new(StringComparer.Ordinal);
            int genericLimit = mode == ShieldLevel.Strict ? 2_500 : 1_500;

            foreach (string selector in _genericCosmeticSelectors.Take(genericLimit))
            {
                selectors.Add(selector);
            }

            HashSet<string> exceptions = new(StringComparer.Ordinal);
            foreach (string candidate in EnumerateHostAndParents(host))
            {
                if (_domainCosmeticSelectors.TryGetValue(candidate, out string[]? domainSelectors))
                {
                    foreach (string selector in domainSelectors)
                    {
                        selectors.Add(selector);
                    }
                }

                if (_domainCosmeticExceptions.TryGetValue(candidate, out HashSet<string>? domainExceptions))
                {
                    exceptions.UnionWith(domainExceptions);
                }
            }

            selectors.ExceptWith(exceptions);
            return selectors.Take(3_000).ToArray();
        }

        private static bool MatchesDomainRules(
            Dictionary<string, List<NetworkRule>> rulesByDomain,
            string host,
            string url,
            bool thirdParty,
            ResourceMask resourceMask)
        {
            foreach (string candidate in EnumerateHostAndParents(host))
            {
                if (!rulesByDomain.TryGetValue(candidate, out List<NetworkRule>? rules))
                {
                    continue;
                }

                foreach (NetworkRule rule in rules)
                {
                    if (rule.Matches(url, thirdParty, resourceMask))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool MatchesGenericRules(
            Dictionary<string, List<NetworkRule>> indexedRules,
            string url,
            bool thirdParty,
            ResourceMask resourceMask)
        {
            if (indexedRules.Count == 0 || url.Length < 3)
            {
                return false;
            }

            // This method runs for nearly every subresource. The previous code
            // lower-cased the full URL and allocated a new three-character string
            // at every position. .NET's alternate dictionary lookup lets Aero
            // probe the same index directly with ReadOnlySpan<char>, eliminating
            // those hot-path allocations without changing any filter semantics.
            Dictionary<string, List<NetworkRule>>.AlternateLookup<ReadOnlySpan<char>> lookup =
                indexedRules.GetAlternateLookup<ReadOnlySpan<char>>();
            ReadOnlySpan<char> urlSpan = url.AsSpan();
            for (int index = 0; index <= urlSpan.Length - 3; index++)
            {
                if (!lookup.TryGetValue(urlSpan.Slice(index, 3), out List<NetworkRule>? rules))
                {
                    continue;
                }

                foreach (NetworkRule rule in rules)
                {
                    if (rule.Matches(url, thirdParty, resourceMask))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool HostOrParentInSet(HashSet<string> set, string host)
        {
            foreach (string candidate in EnumerateHostAndParents(host))
            {
                if (set.Contains(candidate))
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<string> EnumerateHostAndParents(string host)
        {
            string candidate = host;
            while (candidate.Length > 0)
            {
                yield return candidate;
                int dot = candidate.IndexOf('.');
                if (dot < 0 || dot == candidate.Length - 1)
                {
                    yield break;
                }

                candidate = candidate[(dot + 1)..];
            }
        }
    }

    private sealed class NetworkRule
    {
        private readonly string[] _segments;

        public NetworkRule(
            string originalPattern,
            bool leftAnchored,
            bool rightAnchored,
            bool thirdPartyOnly,
            bool firstPartyOnly,
            ResourceMask includedResources,
            ResourceMask excludedResources)
        {
            OriginalPattern = originalPattern.ToLowerInvariant();
            LeftAnchored = leftAnchored;
            RightAnchored = rightAnchored;
            ThirdPartyOnly = thirdPartyOnly;
            FirstPartyOnly = firstPartyOnly;
            IncludedResources = includedResources;
            ExcludedResources = excludedResources;
            _segments = OriginalPattern
                .Replace('^', '*')
                .Split('*', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            IndexKey = FindIndexKey(_segments);
        }

        public string OriginalPattern { get; }

        public string IndexKey { get; }

        private bool LeftAnchored { get; }

        private bool RightAnchored { get; }

        private bool ThirdPartyOnly { get; }

        private bool FirstPartyOnly { get; }

        private ResourceMask IncludedResources { get; }

        private ResourceMask ExcludedResources { get; }

        public bool Matches(string url, bool thirdParty, ResourceMask resourceMask)
        {
            if (ThirdPartyOnly && !thirdParty || FirstPartyOnly && thirdParty)
            {
                return false;
            }

            if (IncludedResources != ResourceMask.None &&
                (IncludedResources & resourceMask) == ResourceMask.None)
            {
                return false;
            }

            if ((ExcludedResources & resourceMask) != ResourceMask.None)
            {
                return false;
            }

            if (_segments.Length == 0)
            {
                return false;
            }

            int position = 0;
            for (int segmentIndex = 0; segmentIndex < _segments.Length; segmentIndex++)
            {
                string segment = _segments[segmentIndex];
                int found = url.IndexOf(segment, position, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                {
                    return false;
                }

                if (segmentIndex == 0 && LeftAnchored && found != 0)
                {
                    return false;
                }

                position = found + segment.Length;
            }

            return !RightAnchored || position == url.Length;
        }

        private static string FindIndexKey(IEnumerable<string> segments)
        {
            string longest = segments
                .Where(segment => segment.Length >= 3)
                .OrderByDescending(segment => segment.Length)
                .FirstOrDefault() ?? string.Empty;

            return longest.Length >= 3 ? longest[..3] : string.Empty;
        }
    }

    private static class FilterParser
    {
        private const int MaximumGenericNetworkRules = 3_000;
        private const int MaximumGenericCosmeticRules = 3_000;

        public static FilterSnapshot Parse(
            IEnumerable<string> lists,
            IEnumerable<string>? customRules,
            IEnumerable<string> builtInHosts,
            DateTimeOffset? updatedAtUtc)
        {
            HashSet<string> blockedDomains = new(builtInHosts, StringComparer.OrdinalIgnoreCase);
            HashSet<string> allowedDomains = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<NetworkRule>> domainBlockRules = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<NetworkRule>> domainAllowRules = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<NetworkRule>> genericBlockRules = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<NetworkRule>> genericAllowRules = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> genericCosmeticSelectors = new(StringComparer.Ordinal);
            Dictionary<string, HashSet<string>> domainCosmeticSelectors = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, HashSet<string>> domainCosmeticExceptions = new(StringComparer.OrdinalIgnoreCase);
            int networkRuleCount = blockedDomains.Count;
            int genericNetworkCount = 0;

            foreach (string text in lists.Append(string.Join(Environment.NewLine, customRules ?? [])))
            {
                using StringReader reader = new(text ?? string.Empty);
                while (reader.ReadLine() is string line)
                {
                    string ruleText = line.Trim();
                    if (ruleText.Length < 2 || ruleText[0] is '!' or '[')
                    {
                        continue;
                    }

                    if (TryParseCosmeticRule(
                        ruleText,
                        genericCosmeticSelectors,
                        domainCosmeticSelectors,
                        domainCosmeticExceptions))
                    {
                        continue;
                    }

                    if (TryParseNetworkRule(
                        ruleText,
                        blockedDomains,
                        allowedDomains,
                        domainBlockRules,
                        domainAllowRules,
                        genericBlockRules,
                        genericAllowRules,
                        ref genericNetworkCount))
                    {
                        networkRuleCount++;
                    }
                }
            }

            string[] genericCosmetics = genericCosmeticSelectors
                .Take(MaximumGenericCosmeticRules)
                .ToArray();

            return new FilterSnapshot(
                blockedDomains,
                allowedDomains,
                domainBlockRules,
                domainAllowRules,
                genericBlockRules,
                genericAllowRules,
                genericCosmetics,
                domainCosmeticSelectors.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.ToArray(),
                    StringComparer.OrdinalIgnoreCase),
                domainCosmeticExceptions,
                networkRuleCount,
                genericCosmetics.Length + domainCosmeticSelectors.Sum(pair => pair.Value.Count),
                updatedAtUtc);
        }

        private static bool TryParseCosmeticRule(
            string text,
            HashSet<string> genericSelectors,
            Dictionary<string, HashSet<string>> domainSelectors,
            Dictionary<string, HashSet<string>> domainExceptions)
        {
            int exceptionMarker = text.IndexOf("#@#", StringComparison.Ordinal);
            bool isException = exceptionMarker >= 0;
            int marker = isException
                ? exceptionMarker
                : text.IndexOf("##", StringComparison.Ordinal);

            if (marker < 0)
            {
                return false;
            }

            string domainPart = text[..marker].Trim();
            string selector = text[(marker + (isException ? 3 : 2))..].Trim();
            if (!IsSafeCssSelector(selector))
            {
                return true;
            }

            if (domainPart.Length == 0)
            {
                if (!isException)
                {
                    genericSelectors.Add(selector);
                }
                return true;
            }

            foreach (string domainToken in domainPart.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (domainToken.StartsWith('~') || domainToken.Contains('*'))
                {
                    continue;
                }

                string domain = domainToken.Trim().TrimEnd('.').ToLowerInvariant();
                if (domain.Length == 0)
                {
                    continue;
                }

                Dictionary<string, HashSet<string>> target = isException
                    ? domainExceptions
                    : domainSelectors;
                if (!target.TryGetValue(domain, out HashSet<string>? selectors))
                {
                    selectors = new HashSet<string>(StringComparer.Ordinal);
                    target[domain] = selectors;
                }
                selectors.Add(selector);
            }

            return true;
        }

        private static bool IsSafeCssSelector(string selector)
        {
            if (selector.Length is < 1 or > 320 ||
                selector.Contains('{') ||
                selector.Contains('}') ||
                selector.Contains("script:", StringComparison.OrdinalIgnoreCase) ||
                selector.Contains(":has(", StringComparison.OrdinalIgnoreCase) ||
                selector.Contains(":matches-css", StringComparison.OrdinalIgnoreCase) ||
                selector.Contains(":xpath", StringComparison.OrdinalIgnoreCase) ||
                selector.Contains("+js(", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private static bool TryParseNetworkRule(
            string text,
            HashSet<string> blockedDomains,
            HashSet<string> allowedDomains,
            Dictionary<string, List<NetworkRule>> domainBlockRules,
            Dictionary<string, List<NetworkRule>> domainAllowRules,
            Dictionary<string, List<NetworkRule>> genericBlockRules,
            Dictionary<string, List<NetworkRule>> genericAllowRules,
            ref int genericNetworkCount)
        {
            bool isAllow = text.StartsWith("@@", StringComparison.Ordinal);
            if (isAllow)
            {
                text = text[2..];
            }

            string options = string.Empty;
            int optionIndex = text.LastIndexOf('$');
            if (optionIndex > 0)
            {
                options = text[(optionIndex + 1)..];
                text = text[..optionIndex];
            }

            if (text.Length < 3 ||
                options.Contains("badfilter", StringComparison.OrdinalIgnoreCase) ||
                options.Contains("removeparam", StringComparison.OrdinalIgnoreCase) ||
                options.Contains("redirect", StringComparison.OrdinalIgnoreCase) ||
                options.Contains("csp", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!ParseOptions(
                options,
                out bool thirdPartyOnly,
                out bool firstPartyOnly,
                out ResourceMask includedResources,
                out ResourceMask excludedResources))
            {
                return false;
            }

            if (text.StartsWith("||", StringComparison.Ordinal))
            {
                string remainder = text[2..];
                int domainEnd = IndexOfDomainEnd(remainder);
                string domain = (domainEnd < 0 ? remainder : remainder[..domainEnd])
                    .Trim()
                    .TrimEnd('.')
                    .ToLowerInvariant();

                if (!LooksLikeDomain(domain))
                {
                    return false;
                }

                string tail = domainEnd < 0 ? string.Empty : remainder[domainEnd..];
                bool hasConstraints = thirdPartyOnly ||
                    firstPartyOnly ||
                    includedResources != ResourceMask.None ||
                    excludedResources != ResourceMask.None;

                if ((tail.Length == 0 || tail == "^" || tail == "*") && !hasConstraints)
                {
                    (isAllow ? allowedDomains : blockedDomains).Add(domain);
                    return true;
                }

                string constrainedTail = tail.Length == 0 ? "^" : tail;
                NetworkRule domainRule = new(
                    domain + constrainedTail,
                    leftAnchored: false,
                    rightAnchored: false,
                    thirdPartyOnly,
                    firstPartyOnly,
                    includedResources,
                    excludedResources);
                AddDomainRule(isAllow ? domainAllowRules : domainBlockRules, domain, domainRule);
                return true;
            }

            bool leftAnchored = text.StartsWith('|');
            bool rightAnchored = text.EndsWith('|');
            string pattern = text.Trim('|');
            NetworkRule rule = new(
                pattern,
                leftAnchored,
                rightAnchored,
                thirdPartyOnly,
                firstPartyOnly,
                includedResources,
                excludedResources);

            if (rule.IndexKey.Length < 3 || genericNetworkCount >= MaximumGenericNetworkRules)
            {
                return false;
            }

            AddGenericRule(isAllow ? genericAllowRules : genericBlockRules, rule);
            genericNetworkCount++;
            return true;
        }

        private static int IndexOfDomainEnd(string value)
        {
            for (int index = 0; index < value.Length; index++)
            {
                if (value[index] is '^' or '/' or '?' or '*' or '|')
                {
                    return index;
                }
            }

            return -1;
        }

        private static bool LooksLikeDomain(string value) =>
            value.Length >= 3 &&
            value.Contains('.') &&
            value.All(character => char.IsLetterOrDigit(character) || character is '.' or '-');

        private static bool ParseOptions(
            string options,
            out bool thirdPartyOnly,
            out bool firstPartyOnly,
            out ResourceMask includedResources,
            out ResourceMask excludedResources)
        {
            thirdPartyOnly = false;
            firstPartyOnly = false;
            includedResources = ResourceMask.None;
            excludedResources = ResourceMask.None;

            foreach (string rawOption in options.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                bool excluded = rawOption.StartsWith('~');
                string option = excluded ? rawOption[1..] : rawOption;

                if (option.Equals("third-party", StringComparison.OrdinalIgnoreCase))
                {
                    if (excluded)
                    {
                        firstPartyOnly = true;
                    }
                    else
                    {
                        thirdPartyOnly = true;
                    }
                    continue;
                }

                ResourceMask resource = option.ToLowerInvariant() switch
                {
                    "document" => ResourceMask.Document,
                    "subdocument" => ResourceMask.Subdocument,
                    "script" => ResourceMask.Script,
                    "image" => ResourceMask.Image,
                    "stylesheet" => ResourceMask.Stylesheet,
                    "font" => ResourceMask.Font,
                    "media" => ResourceMask.Media,
                    "xmlhttprequest" => ResourceMask.XmlHttpRequest,
                    "xhr" => ResourceMask.XmlHttpRequest,
                    "ping" => ResourceMask.Other,
                    "websocket" => ResourceMask.Other,
                    "other" => ResourceMask.Other,
                    _ => ResourceMask.None
                };

                if (resource != ResourceMask.None)
                {
                    if (excluded)
                    {
                        excludedResources |= resource;
                    }
                    else
                    {
                        includedResources |= resource;
                    }
                    continue;
                }

                // These modifiers require behavior Aero Shield deliberately does
                // not emulate. Ignoring them would broaden the rule and could break
                // unrelated sites, so the complete rule is skipped instead.
                if (option.Contains('=') ||
                    option.Equals("popup", StringComparison.OrdinalIgnoreCase) ||
                    option.Equals("popunder", StringComparison.OrdinalIgnoreCase) ||
                    option.Equals("elemhide", StringComparison.OrdinalIgnoreCase) ||
                    option.Equals("generichide", StringComparison.OrdinalIgnoreCase) ||
                    option.Equals("specifichide", StringComparison.OrdinalIgnoreCase) ||
                    option.Equals("genericblock", StringComparison.OrdinalIgnoreCase) ||
                    option.Equals("object", StringComparison.OrdinalIgnoreCase) ||
                    option.Equals("object-subrequest", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                // Harmless priority/casing hints can be ignored safely.
                if (!option.Equals("important", StringComparison.OrdinalIgnoreCase) &&
                    !option.Equals("match-case", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private static void AddDomainRule(
            Dictionary<string, List<NetworkRule>> target,
            string domain,
            NetworkRule rule)
        {
            if (!target.TryGetValue(domain, out List<NetworkRule>? rules))
            {
                rules = [];
                target[domain] = rules;
            }
            rules.Add(rule);
        }

        private static void AddGenericRule(
            Dictionary<string, List<NetworkRule>> target,
            NetworkRule rule)
        {
            if (!target.TryGetValue(rule.IndexKey, out List<NetworkRule>? rules))
            {
                rules = [];
                target[rule.IndexKey] = rules;
            }
            rules.Add(rule);
        }
    }
}
