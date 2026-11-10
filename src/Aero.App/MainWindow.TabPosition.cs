using Aero.App.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Aero.App;

// Moves the tab strip between the title bar, a horizontal dock (below the toolbar or at the
// bottom of the window) and a vertical sidebar. Outside the title bar the title bar shrinks to
// a caption strip, so the window stays draggable and the caption buttons keep their place.
public sealed partial class MainWindow
{
    private const double CaptionStripHeight = 32;

    private bool IsTabStripInTitleBar => _settings.TabPosition == TabStripPosition.Top;

    private bool IsVerticalTabStrip =>
        _settings.TabPosition is TabStripPosition.Left or TabStripPosition.Right;

    private double ConfiguredVerticalTabWidth => Math.Clamp(_settings.VerticalTabWidth, 160, 420);

    private void ApplyTabStripPosition()
    {
        TabStripPosition position = _settings.TabPosition;
        bool vertical = IsVerticalTabStrip;
        double barHeight = ConfiguredTabBarHeight;

        MoveTabStripTo(position switch
        {
            TabStripPosition.Top => TitleBarSurface,
            TabStripPosition.BelowToolbar or TabStripPosition.Bottom => HorizontalTabDock,
            _ => VerticalTabDock
        });

        Orientation orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        TabStripHost.Orientation = orientation;
        TabStripPanel.Orientation = orientation;
        TabStripHost.Margin = vertical
            ? new Thickness(0)
            : new Thickness(Math.Clamp(_settings.TabStripPadding, 0, 40), 0, 0, 0);
        AddTabButton.HorizontalAlignment = HorizontalAlignment.Left;
        TabScrollViewer.HorizontalAlignment = vertical ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        TabScrollViewer.HorizontalScrollMode = vertical ? ScrollMode.Disabled : ScrollMode.Enabled;
        TabScrollViewer.HorizontalScrollBarVisibility = vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
        TabScrollViewer.VerticalScrollMode = vertical ? ScrollMode.Enabled : ScrollMode.Disabled;
        TabScrollViewer.VerticalScrollBarVisibility = vertical ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;

        bool shown = !_isFullscreen;
        TitleBarRow.Height = new GridLength(!shown ? 0 : IsTabStripInTitleBar ? barHeight : CaptionStripHeight);
        DockedTabRow.Height = new GridLength(shown && position == TabStripPosition.BelowToolbar ? barHeight : 0);
        BottomTabRow.Height = new GridLength(shown && position == TabStripPosition.Bottom ? barHeight : 0);

        bool horizontalDock = position is TabStripPosition.BelowToolbar or TabStripPosition.Bottom;
        HorizontalTabDock.Visibility = shown && horizontalDock ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetRow(HorizontalTabDock, position == TabStripPosition.Bottom ? 6 : 2);
        HorizontalTabDock.BorderThickness = position == TabStripPosition.Bottom
            ? new Thickness(0, 1, 0, 0)
            : new Thickness(0, 0, 0, 1);

        double sideWidth = ConfiguredVerticalTabWidth;
        bool sidebar = shown && vertical;
        VerticalTabDock.Visibility = sidebar ? Visibility.Visible : Visibility.Collapsed;
        VerticalTabDock.Width = sideWidth;
        VerticalTabDock.HorizontalAlignment = position == TabStripPosition.Right
            ? HorizontalAlignment.Right
            : HorizontalAlignment.Left;
        VerticalTabDock.BorderThickness = position == TabStripPosition.Right
            ? new Thickness(1, 0, 0, 0)
            : new Thickness(0, 0, 1, 0);
        // Styles with a content inset show the page as a card inside the window frame.
        double inset = _isFullscreen ? 0 : CurrentStyleGeometry.ContentInset;
        double bottomInset = position == TabStripPosition.Bottom ? 0 : inset;
        BrowserHost.Margin = !sidebar
            ? new Thickness(inset, 0, inset, bottomInset)
            : position == TabStripPosition.Right
                ? new Thickness(inset, 0, sideWidth, bottomInset)
                : new Thickness(sideWidth, 0, inset, bottomInset);

        HorizontalTabDock.Background = TitleBarSurface.Background;
        VerticalTabDock.Background = TitleBarSurface.Background;
        if (IsTabStripInTitleBar)
        {
            UpdateTitleBarLayout(); // replaces the sidebar/dock viewport left from the previous position
        }
        else
        {
            UpdateTabStripViewport();
        }
    }

    // Tracked here because TabScrollViewer.Parent is not reliable for this; null = title bar (XAML).
    private FrameworkElement? _tabStripHost;

    private void MoveTabStripTo(FrameworkElement host)
    {
        FrameworkElement current = _tabStripHost ?? TitleBarSurface;
        if (current == host)
        {
            return;
        }

        if (current is Border oldDock)
        {
            oldDock.Child = null;
        }
        else
        {
            TitleBarSurface.Children.Remove(TabScrollViewer);
        }

        if (host is Border dock)
        {
            dock.Child = TabScrollViewer;
        }
        else
        {
            // Back into the title bar, in front of the drag region like in MainWindow.xaml.
            Grid.SetColumn(TabScrollViewer, 2);
            TitleBarSurface.Children.Insert(TitleBarSurface.Children.IndexOf(TitleBarDragRegion), TabScrollViewer);
        }

        _tabStripHost = host;
    }

    // The title bar computes the viewport for the top position; the docks have their own width.
    private void UpdateTabStripViewport()
    {
        if (IsTabStripInTitleBar)
        {
            return;
        }

        TabScrollViewer.MaxWidth = IsVerticalTabStrip
            ? Math.Max(0, ConfiguredVerticalTabWidth - VerticalTabDock.Padding.Left - VerticalTabDock.Padding.Right)
            : Math.Max(0, RootGrid.ActualWidth - 8);
    }
}
