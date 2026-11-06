using Aero.App.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;

namespace Aero.App;

public sealed partial class MainWindow
{
    private int ConfiguredTabAnimationDuration =>
        Math.Clamp(_settings.TabAnimationDuration, 0, 600);

    private double ConfiguredInactiveTabScale =>
        Math.Clamp(_settings.TabInactiveScalePercent, 85, 100) / 100d;

    private Brush BuildConfiguredTabBrush(
        bool isActive,
        bool isPointerOver,
        bool isPointerPressed)
    {
        bool isDark = IsDarkTheme();
        Color themeColor = isPointerPressed
            ? GetChromeColor("AeroPressedBrush")
            : isActive
                ? GetChromeColor("AeroActiveTabBrush")
                : isPointerOver
                    ? GetChromeColor("AeroTabHoverBrush")
                    : Color.FromArgb(0, 0, 0, 0);

        if (_settings.TabSurfaceStyle == TabSurfaceStyle.Theme || _chromeAccessibilitySettings.HighContrast)
        {
            return new SolidColorBrush(themeColor);
        }

        Color baseColor = isActive
            ? ParseConfiguredColor(_settings.TabActiveColor, Color.FromArgb(255, 42, 48, 54))
            : isPointerOver || isPointerPressed
                ? ParseConfiguredColor(_settings.TabHoverColor, Color.FromArgb(255, 34, 40, 45))
                : ParseConfiguredColor(_settings.TabInactiveColor, Color.FromArgb(255, 23, 33, 43));
        int opacity = isActive
            ? _settings.TabActiveOpacity
            : isPointerOver || isPointerPressed
                ? _settings.TabHoverOpacity
                : _settings.TabInactiveOpacity;

        if (isPointerPressed)
        {
            baseColor = BlendColors(
                baseColor,
                isDark ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 0, 0, 0),
                0.10);
        }

        Color accent = GetConfiguredAccentColor();
        if (_settings.TabSurfaceStyle == TabSurfaceStyle.Accent)
        {
            double accentMix = isActive ? 0.52 : isPointerOver ? 0.30 : 0.14;
            baseColor = BlendColors(baseColor, accent, accentMix);
        }

        byte alpha = (byte)Math.Clamp((int)Math.Round(opacity * 2.55), 0, 255);
        Color transparentBase = Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B);

        if (_settings.TabSurfaceStyle == TabSurfaceStyle.Gradient)
        {
            LinearGradientBrush gradient = new()
            {
                StartPoint = new Windows.Foundation.Point(0, 0),
                EndPoint = new Windows.Foundation.Point(1, 1)
            };
            gradient.GradientStops.Add(new GradientStop
            {
                Color = transparentBase,
                Offset = 0
            });
            Color second = BlendColors(baseColor, accent, isActive ? 0.48 : 0.24);
            gradient.GradientStops.Add(new GradientStop
            {
                Color = Color.FromArgb(alpha, second.R, second.G, second.B),
                Offset = 1
            });
            return gradient;
        }

        if (_settings.TabSurfaceStyle == TabSurfaceStyle.Acrylic)
        {
            return new AcrylicBrush
            {
                TintColor = baseColor,
                TintOpacity = Math.Clamp(opacity / 100d * (isActive ? 0.72 : 0.56), 0.05, 0.82),
                TintLuminosityOpacity = isActive ? 0.62 : 0.42,
                FallbackColor = Color.FromArgb(
                    (byte)Math.Clamp(Math.Max(72, (int)alpha), 0, 255),
                    baseColor.R,
                    baseColor.G,
                    baseColor.B)
            };
        }

        return new SolidColorBrush(transparentBase);
    }

    private CornerRadius GetConfiguredTabCornerRadius(bool isActive, double height)
    {
        return _settings.TabShape switch
        {
            TabShapeStyle.Pill => new CornerRadius(Math.Max(1, height / 2)),
            TabShapeStyle.Square => new CornerRadius(0),
            TabShapeStyle.Rounded => new CornerRadius(Math.Clamp(_settings.TabCornerRadius, 6, 30)),
            TabShapeStyle.Custom => new CornerRadius(Math.Clamp(_settings.TabCornerRadius, 0, 30)),
            _ => isActive
                ? new CornerRadius(Math.Clamp(_settings.TabCornerRadius, 0, 12), Math.Clamp(_settings.TabCornerRadius, 0, 12), 0, 0)
                : new CornerRadius(Math.Clamp(_settings.TabCornerRadius, 0, 12))
        };
    }

    private bool ShouldShowTabCloseButton(BrowserTabState state, bool isActive, bool isPinned)
    {
        if (isPinned)
        {
            return false;
        }

        return _settings.TabCloseButtonMode switch
        {
            TabCloseButtonMode.Always => true,
            TabCloseButtonMode.Never => false,
            TabCloseButtonMode.ActiveOnly => isActive,
            _ => isActive || state.IsPointerOver
        };
    }

    private void ApplyTabIndicatorLayout(BrowserTabState state, bool isActive)
    {
        int thickness = Math.Clamp(_settings.TabIndicatorThickness, 1, 6);
        state.ActiveIndicator.Height = thickness;
        state.ActiveIndicator.Margin = new Thickness(
            Math.Max(7, _settings.TabHorizontalPadding + 4),
            0,
            Math.Max(7, _settings.TabHorizontalPadding + 4),
            0);
        state.ActiveIndicator.VerticalAlignment =
            _settings.TabActiveIndicator == TabActiveIndicatorStyle.Bottom
                ? VerticalAlignment.Bottom
                : VerticalAlignment.Top;
        state.ActiveIndicator.Visibility =
            _settings.TabActiveIndicator is TabActiveIndicatorStyle.Top or TabActiveIndicatorStyle.Bottom
                ? Visibility.Visible
                : Visibility.Collapsed;

        if (state.ActiveIndicator.Visibility == Visibility.Collapsed)
        {
            state.ActiveIndicator.Opacity = 0;
        }
        else if (_settings.TabAnimation == TabAnimationStyle.None || _settings.ReduceMotion)
        {
            state.ActiveIndicator.Opacity = isActive ? 1 : 0;
            state.ActiveIndicatorTransform.ScaleX = isActive ? 1 : 0.55;
        }
    }

    private void ApplyTabHeaderMetrics(BrowserTabState state, bool isActive, bool isPinned)
    {
        double iconSize = Math.Clamp(_settings.TabIconSize, 10, 24);
        state.Icon.FontSize = Math.Clamp(iconSize - 5, 8, 19);
        state.FaviconImage.Width = iconSize;
        state.FaviconImage.Height = iconSize;
        state.AudioButton.Width = Math.Max(18, iconSize + 2);
        state.AudioButton.Height = Math.Max(18, iconSize + 2);
        state.AudioIcon.FontSize = Math.Clamp(iconSize - 4, 9, 15);
        state.IconColumn.Width = new GridLength(Math.Max(iconSize, state.AudioButton.Width));
        state.CloseColumn.Width = new GridLength(
            isPinned || _settings.TabCloseButtonMode == TabCloseButtonMode.Never ? 0 : 22);
        state.TitleText.FontSize = Math.Clamp(_settings.TabFontSize, 9, 18);
        state.TitleText.TextAlignment = _settings.TabTextAlignment == TabTextAlignmentMode.Center
            ? TextAlignment.Center
            : TextAlignment.Left;
        state.TitleText.HorizontalAlignment = _settings.TabTextAlignment == TabTextAlignmentMode.Center
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Left;
        state.TitleText.FontWeight = _settings.TabBoldActiveText && isActive
            ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal;

        int horizontalPadding = Math.Clamp(_settings.TabHorizontalPadding, 2, 28);
        state.Header.Padding = isPinned
            ? new Thickness(0)
            : new Thickness(horizontalPadding, 0, Math.Max(3, horizontalPadding - 4), 0);
        bool connected = _settings.TabShape == TabShapeStyle.Connected;
        state.Header.Margin = new Thickness(0);
        // Connected tabs sit on the toolbar edge; free-standing shapes stay centred in the bar.
        state.Header.VerticalAlignment = connected ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        state.Header.CornerRadius = GetConfiguredTabCornerRadius(isActive, state.Header.Height);
        ApplyTabIndicatorLayout(state, isActive);
    }

    private void ApplyConfiguredTabBorder(BrowserTabState state, bool isActive, bool isPointerOver)
    {
        int thickness = Math.Clamp(_settings.TabBorderThickness, 0, 4);
        bool outlineIndicator =
            _settings.TabActiveIndicator == TabActiveIndicatorStyle.Outline && isActive;
        if (outlineIndicator && thickness == 0)
        {
            thickness = 1;
        }

        if (thickness == 0)
        {
            state.Header.BorderThickness = new Thickness(0);
            state.Header.BorderBrush = GetTransparentBrush();
            return;
        }

        Color baseColor = outlineIndicator
            ? GetConfiguredAccentColor()
            : IsDarkTheme()
                ? Color.FromArgb(255, 58, 66, 72)
                : Color.FromArgb(255, 200, 208, 214);
        int configuredOpacity = Math.Clamp(_settings.TabBorderOpacity, 0, 100);
        int effectiveOpacity = isActive || outlineIndicator
            ? configuredOpacity
            : isPointerOver
                ? configuredOpacity / 2
                : 0;
        Color border = Color.FromArgb(
            (byte)Math.Clamp((int)Math.Round(effectiveOpacity * 2.55), 0, 255),
            baseColor.R,
            baseColor.G,
            baseColor.B);
        state.Header.BorderBrush = new SolidColorBrush(border);
        state.Header.BorderThickness = effectiveOpacity <= 0
            ? new Thickness(0)
            : _settings.TabShape == TabShapeStyle.Connected && isActive
                ? new Thickness(thickness, thickness, thickness, 0)
                : new Thickness(thickness);
    }

    private double GetTabTargetScale(bool isActive, bool isPointerOver)
    {
        if (isActive)
        {
            return 1;
        }

        double inactive = ConfiguredInactiveTabScale;
        return isPointerOver ? Math.Min(1, inactive + 0.015) : inactive;
    }

    private double GetTabTargetTranslateY(bool isActive, bool isPointerOver)
    {
        if (isActive)
        {
            return 0;
        }

        // Resting tabs stay on the baseline; a constant 1px offset made every switch jitter.
        return isPointerOver ? -Math.Clamp(_settings.TabHoverLift, 0, 8) : 0;
    }

    private EasingFunctionBase BuildTabEasing(bool entering)
    {
        if (_settings.TabAnimation == TabAnimationStyle.Bounce)
        {
            return new BackEase
            {
                Amplitude = entering ? 0.32 : 0.18,
                EasingMode = EasingMode.EaseOut
            };
        }

        return DecelerateEase();
    }

    private void AnimateTabDouble(
        DependencyObject target,
        string property,
        double from,
        double to,
        int durationMilliseconds,
        bool entering = false,
        Action? completed = null)
    {
        if (_settings.ReduceMotion ||
            _settings.TabAnimation == TabAnimationStyle.None ||
            durationMilliseconds <= 0)
        {
            switch (target)
            {
                case CompositeTransform transform when property == "ScaleX":
                    transform.ScaleX = to;
                    break;
                case CompositeTransform transform when property == "ScaleY":
                    transform.ScaleY = to;
                    break;
                case CompositeTransform transform when property == "TranslateX":
                    transform.TranslateX = to;
                    break;
                case CompositeTransform transform when property == "TranslateY":
                    transform.TranslateY = to;
                    break;
                case ScaleTransform scale when property == "ScaleX":
                    scale.ScaleX = to;
                    break;
                case FrameworkElement element when property == "Width":
                    element.Width = to;
                    break;
            }
            completed?.Invoke();
            return;
        }

        DoubleAnimation animation = new()
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(
                GetFrameAlignedAnimationDuration(durationMilliseconds))),
            EasingFunction = BuildTabEasing(entering),
            // Width is a layout property; without this the close animation never runs and neighbours snap.
            EnableDependentAnimation = property == "Width"
        };
        Storyboard storyboard = new();
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
        if (completed is not null)
        {
            storyboard.Completed += (_, _) => completed();
        }
        storyboard.Begin();
    }

    private AcrylicBrush BuildTabBarAcrylicBrush(Color baseColor)
    {
        bool strong = _settings.TabBarBlur == TabBarBlurStyle.Strong;
        return new AcrylicBrush
        {
            TintColor = baseColor,
            TintOpacity = strong ? 0.28 : 0.48,
            TintLuminosityOpacity = strong ? 0.34 : 0.58,
            FallbackColor = Color.FromArgb(245, baseColor.R, baseColor.G, baseColor.B)
        };
    }
}
