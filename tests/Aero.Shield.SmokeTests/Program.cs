using Aero.Shield;

RequestBlocker blocker = new();

Assert(
    RequestBlocker.ShouldBlockFastLane(
        new Uri("https://www.google-analytics.com/collect?v=1"),
        "example.com",
        ShieldResourceContext.XmlHttpRequest),
    "The built-in fast lane did not block a known third-party tracker.");

Assert(
    !RequestBlocker.ShouldBlockFastLane(
        new Uri("https://www.google-analytics.com/collect?v=1"),
        "google-analytics.com",
        ShieldResourceContext.XmlHttpRequest),
    "The fast lane must not block the same site as a third-party request.");

Assert(
    !RequestBlocker.ShouldBlockFastLane(
        new Uri("https://challenges.cloudflare.com/turnstile/v0/api.js"),
        "example.com",
        ShieldResourceContext.Script),
    "Browser verification resources must remain allowed.");

Assert(
    !RequestBlocker.ShouldBlockFastLane(
        new Uri("https://doubleclick.net/"),
        "example.com",
        ShieldResourceContext.Document),
    "Top-level documents must never be blocked by the native request engine.");

Uri strictCandidate = new("https://cdn.example.net/pixel/track.gif");
Assert(
    !blocker.ShouldBlock(
        strictCandidate,
        "example.com",
        ShieldResourceContext.Image,
        ShieldLevel.Standard),
    "Standard mode unexpectedly applied a strict URL heuristic.");
Assert(
    blocker.ShouldBlock(
        strictCandidate,
        "example.com",
        ShieldResourceContext.Image,
        ShieldLevel.Strict),
    "Strict mode did not apply its URL heuristic.");

Assert(
    RequestBlocker.IsBrowserVerificationResource(
        new Uri("https://example.com/cdn-cgi/challenge-platform/h/g/orchestrate/jsch/v1")),
    "Cloudflare challenge paths must remain allow-listed.");

Console.WriteLine("Aero.Shield smoke tests passed.");
return 0;

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
