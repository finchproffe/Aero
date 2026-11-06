using Aero.App.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.CompilerServices;

namespace Aero.App;

public sealed partial class MainWindow
{
    private readonly ConditionalWeakTable<DependencyObject, LocalizedElementState>
        _localizedElements = new();

    private void ApplyLocalization()
    {
        ApplyLocalizationToTree(RootGrid);
        if (AeroMenuFlyout.Content is DependencyObject menuContent)
        {
            ApplyLocalizationToTree(menuContent);
        }

        ToolTipService.SetToolTip(TabListButton, AeroLocalizer.Get("Shell.AllTabs"));
        ToolTipService.SetToolTip(AddTabButton, AeroLocalizer.Get("Shell.NewTabShortcut"));
        ToolTipService.SetToolTip(BackButton, AeroLocalizer.Get("Shell.Back"));
        ToolTipService.SetToolTip(ForwardButton, AeroLocalizer.Get("Shell.Forward"));
        ToolTipService.SetToolTip(ReloadButton, AeroLocalizer.Get("Shell.Reload"));
        AddressBox.PlaceholderText = AeroLocalizer.Get("Shell.SearchPlaceholder");
        AutomationProperties.SetName(AddressBox, AeroLocalizer.Get("Accessibility.Address"));
        AutomationProperties.SetName(BrowserHost, AeroLocalizer.Get("Accessibility.MainContent"));
        RefreshProfileSwitcher();

        foreach (BrowserTabState tab in _tabs)
        {
            UpdateTabAccessibility(tab);
        }
    }

    // Internal pages keep stable English titles in state/session; only the display is translated.
    // Real page titles pass through untouched even if they happen to match a UI string.
    private static string DisplayTabTitle(string title) =>
        title is "New tab" or "Settings" or "Connection problem" or "Aero Settings"
            ? AeroLocalizer.TranslateUi(title)
            : title;

    private static void UpdateTabAccessibility(BrowserTabState tab)
    {
        string title = string.IsNullOrWhiteSpace(tab.Title)
            ? AeroLocalizer.Get("Shell.NewTab")
            : DisplayTabTitle(tab.Title);
        AutomationProperties.SetName(
            tab.Header,
            AeroLocalizer.Format("Accessibility.Tab", title));
        AutomationProperties.SetHelpText(
            tab.Header,
            AeroLocalizer.Get("Accessibility.TabHelp"));
        AutomationProperties.SetName(
            tab.CloseButton,
            AeroLocalizer.Format("Accessibility.TabClose", title));
    }

    private void ApplyLocalizationToTree(DependencyObject element)
    {
        LocalizedElementState state = _localizedElements.GetValue(
            element,
            static item => LocalizedElementState.Capture(item));
        state.Apply(element);
        if (element is Button button && ToolTipService.GetToolTip(button) is string tip && !string.IsNullOrWhiteSpace(tip))
        {
            AutomationProperties.SetName(button, tip);
        }

        int childCount;
        try
        {
            childCount = VisualTreeHelper.GetChildrenCount(element);
        }
        catch
        {
            return;
        }

        for (int index = 0; index < childCount; index++)
        {
            ApplyLocalizationToTree(VisualTreeHelper.GetChild(element, index));
        }
    }

    private sealed class LocalizedElementState
    {
        private string? Text { get; init; }
        private string? Content { get; init; }
        private string? Header { get; init; }
        private string? Placeholder { get; init; }
        private string? OnContent { get; init; }
        private string? OffContent { get; init; }
        private string? ToolTip { get; init; }

        public static LocalizedElementState Capture(DependencyObject element) => new()
        {
            Text = element is TextBlock text ? text.Text : null,
            Content = element is ContentControl content && content.Content is string contentText
                ? contentText
                : null,
            Header = element switch
            {
                ToggleSwitch toggle when toggle.Header is string value => value,
                ComboBox combo when combo.Header is string value => value,
                TextBox textBox when textBox.Header is string value => value,
                _ => null
            },
            Placeholder = element switch
            {
                TextBox textBox => textBox.PlaceholderText,
                PasswordBox passwordBox => passwordBox.PlaceholderText,
                _ => null
            },
            OnContent = element is ToggleSwitch onToggle && onToggle.OnContent is string onValue
                ? onValue
                : null,
            OffContent = element is ToggleSwitch offToggle && offToggle.OffContent is string offValue
                ? offValue
                : null,
            ToolTip = ToolTipService.GetToolTip(element) as string
        };

        public void Apply(DependencyObject element)
        {
            if (Text is not null && element is TextBlock text)
            {
                text.Text = AeroLocalizer.TranslateUi(Text);
            }
            if (Content is not null && element is ContentControl content)
            {
                content.Content = AeroLocalizer.TranslateUi(Content);
            }
            if (Header is not null)
            {
                switch (element)
                {
                    case ToggleSwitch toggle:
                        toggle.Header = AeroLocalizer.TranslateUi(Header);
                        break;
                    case ComboBox combo:
                        combo.Header = AeroLocalizer.TranslateUi(Header);
                        break;
                    case TextBox textBox:
                        textBox.Header = AeroLocalizer.TranslateUi(Header);
                        break;
                }
            }
            if (Placeholder is not null)
            {
                switch (element)
                {
                    case TextBox textBox:
                        textBox.PlaceholderText = AeroLocalizer.TranslateUi(Placeholder);
                        break;
                    case PasswordBox passwordBox:
                        passwordBox.PlaceholderText = AeroLocalizer.TranslateUi(Placeholder);
                        break;
                }
            }
            if (element is ToggleSwitch toggleSwitch)
            {
                if (OnContent is not null)
                {
                    toggleSwitch.OnContent = AeroLocalizer.TranslateUi(OnContent);
                }
                if (OffContent is not null)
                {
                    toggleSwitch.OffContent = AeroLocalizer.TranslateUi(OffContent);
                }
            }
            if (ToolTip is not null)
            {
                ToolTipService.SetToolTip(element, AeroLocalizer.TranslateUi(ToolTip));
            }
        }
    }
}
