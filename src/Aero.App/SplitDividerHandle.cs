using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Aero.App;

/// <summary>Split View divider; exists only because ProtectedCursor is settable from a subclass.</summary>
public sealed partial class SplitDividerHandle : Grid
{
    public SplitDividerHandle()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }
}
