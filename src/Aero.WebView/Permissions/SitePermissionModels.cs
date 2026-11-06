using Microsoft.Web.WebView2.Core;
using System.Collections.Generic;

namespace Aero.WebView.Permissions;

public enum SitePermissionValue
{
    Ask = 0,
    Allow = 1,
    Block = 2
}

public sealed record SitePermissionSnapshot(
    string Origin,
    IReadOnlyDictionary<CoreWebView2PermissionKind, SitePermissionValue> States)
{
    public SitePermissionValue GetState(CoreWebView2PermissionKind permissionKind) =>
        States.TryGetValue(permissionKind, out SitePermissionValue state)
            ? state
            : SitePermissionValue.Ask;
}
