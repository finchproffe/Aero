using Aero.WebView;

int failures = 0;
Run("bind-detach", BindDetach);
Run("duplicate-bind", DuplicateBind);
Run("rollback", Rollback);
Run("launch-guard", LaunchGuard);

return failures == 0 ? 0 : 1;

void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
    }
}

static void BindDetach()
{
    FakeSource source = new();
    using WebViewEventBinder binder = new();
    int calls = 0;
    EventHandler handler = (_, _) => calls++;

    binder.Bind(source, source, bindings =>
        bindings.Add(
            () => source.Changed += handler,
            () => source.Changed -= handler));

    source.Raise();
    Assert(calls == 1, "The handler must be attached exactly once.");
    binder.Detach();
    source.Raise();
    Assert(calls == 1, "The handler must be detached.");
}

static void DuplicateBind()
{
    FakeSource source = new();
    using WebViewEventBinder binder = new();
    binder.Bind(source, source, _ => { });

    bool threw = false;
    try
    {
        binder.Bind(source, source, _ => { });
    }
    catch (InvalidOperationException)
    {
        threw = true;
    }

    Assert(threw, "Binding the same controller twice must fail closed.");
}

static void Rollback()
{
    FakeSource source = new();
    using WebViewEventBinder binder = new();
    int calls = 0;
    EventHandler handler = (_, _) => calls++;

    bool threw = false;
    try
    {
        binder.Bind(source, source, bindings =>
        {
            bindings.Add(
                () => source.Changed += handler,
                () => source.Changed -= handler);
            bindings.Add(
                () => throw new InvalidOperationException("simulated attach failure"),
                static () => { });
        });
    }
    catch (InvalidOperationException)
    {
        threw = true;
    }

    Assert(threw, "The simulated attachment failure must surface.");
    source.Raise();
    Assert(calls == 0, "Previously attached handlers must be rolled back.");
    Assert(!binder.IsBound, "A failed bind must not leave a bound state.");
}

static void LaunchGuard()
{
    Assert(WebViewLaunchGuard.FindDangerousSwitch("--remote-debugging-port=9222") is not null, "CDP port was not detected.");
    Assert(WebViewLaunchGuard.FindDangerousSwitch("--lang=ru -remote-debugging-pipe") is not null, "Single-dash CDP pipe was not detected.");
    Assert(WebViewLaunchGuard.FindDangerousSwitch("\"/REMOTE-DEBUGGING-PORT=1\"") is not null, "Quoted slash/uppercase switch was not detected.");
    Assert(WebViewLaunchGuard.FindDangerousSwitch("--load-extension=C:\\evil") is not null, "Extension side-loading was not detected.");
    Assert(WebViewLaunchGuard.FindDangerousSwitch("--lang=ru --force-dark-mode") is null, "Harmless switches were flagged.");
    Assert(WebViewLaunchGuard.FindDangerousSwitch("remote-debugging-port") is null, "A bare word without a switch prefix was flagged.");

    Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--remote-debugging-port=9222");
    IReadOnlyList<string> removed = WebViewLaunchGuard.ScrubLoaderEnvironment();
    Assert(removed.Contains("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"), "Injected loader arguments were not reported.");
    Assert(Environment.GetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS") is null, "Injected loader arguments survived.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class FakeSource
{
    public event EventHandler? Changed;

    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
