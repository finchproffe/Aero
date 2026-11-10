using Aero.App.Data;
using Aero.App.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;

namespace Aero.App;

public sealed partial class MainWindow
{
    private void AeroMenuFlyout_Opened(object sender, object e)
    {
        ProfileSwitcherCard.Visibility = Visibility.Collapsed;
        ProfileMenuChevron.Glyph = "\uE70D";
        RefreshProfileSwitcher();
    }

    private void RefreshProfileSwitcher()
    {
        ProfileToolbarIconText.Text = _browserProfile.Icon;
        ProfileMenuIconText.Text = _browserProfile.Icon;
        bool hasPhoto = BrowserProfileManager.GetAvatarPath(_browserProfile) is not null;
        MenuButton.Content = hasPhoto ? BuildProfileAvatarVisual(_browserProfile, 22, 16) : ProfileToolbarIconText;
        ProfileMenuIconHost.Child = hasPhoto ? BuildProfileAvatarVisual(_browserProfile, 32, 17) : ProfileMenuIconText;
        ProfileMenuNameText.Text = _browserProfile.Name;
        ProfileMenuSubtitleText.Text = _isPrivateWindow
            ? AeroLocalizer.Get("Profile.PrivateSwitch")
            : _browserProfile.IsGuest
                ? AeroLocalizer.Get("Profile.GuestSwitch")
                : AeroLocalizer.Get("Profile.Switch");
        ToolTipService.SetToolTip(
            MenuButton,
            AeroLocalizer.Format("Profile.MenuAutomation", _browserProfile.Name));
        AutomationProperties.SetName(
            MenuButton,
            AeroLocalizer.Format("Profile.MenuAutomation", _browserProfile.Name));

        ProfileSwitcherItemsPanel.Children.Clear();
        foreach (BrowserProfile profile in BrowserProfileManager.ListProfiles())
        {
            ProfileSwitcherItemsPanel.Children.Add(BuildProfileSwitcherButton(profile));
        }
    }

    private Button BuildProfileSwitcherButton(BrowserProfile profile)
    {
        bool current = !_isPrivateWindow && !_browserProfile.IsGuest &&
            profile.Id.Equals(_browserProfile.Id, StringComparison.OrdinalIgnoreCase);
        Grid content = new() { Width = 202, ColumnSpacing = 8 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        content.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        content.Children.Add(BuildProfileAvatarVisual(profile, 24, 16));

        TextBlock name = new()
        {
            Text = profile.Name,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = current
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal
        };
        Grid.SetColumn(name, 1);
        content.Children.Add(name);

        if (current)
        {
            FontIcon check = new() { Glyph = "\uE73E", FontSize = 12 };
            Grid.SetColumn(check, 2);
            content.Children.Add(check);
        }

        Button button = new()
        {
            Content = content,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Style = (Style)Application.Current.Resources["MenuActionButtonStyle"],
            IsEnabled = !current,
            Tag = profile.Id
        };
        AutomationProperties.SetName(
            button,
            current
                ? AeroLocalizer.Format("Profile.CurrentAutomation", profile.Name)
                : AeroLocalizer.Format("Profile.SwitchAutomation", profile.Name));
        button.Click += async (_, _) =>
        {
            AeroMenuFlyout.Hide();
            await SwitchToProfileAsync(profile);
        };
        return button;
    }

    private void ProfileMenuHeaderButton_Click(object sender, RoutedEventArgs e)
    {
        bool show = ProfileSwitcherCard.Visibility != Visibility.Visible;
        ProfileSwitcherCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ProfileMenuChevron.Glyph = show ? "\uE70E" : "\uE70D";
    }

    private async void CreateProfileMenuButton_Click(object sender, RoutedEventArgs e)
    {
        AeroMenuFlyout.Hide();
        BrowserProfile? profile = await ShowCreateProfileDialogAsync();
        if (profile is not null)
        {
            await SwitchToProfileAsync(profile);
        }
    }

    private async Task<BrowserProfile?> ShowCreateProfileDialogAsync()
    {
        string selectedAvatar = BrowserProfileManager.AvailableIcons[1];
        byte[]? selectedPhoto = null;
        TextBlock selectedAvatarPreview = new()
        {
            Text = selectedAvatar,
            FontSize = 28,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Border preview = new()
        {
            Width = 54,
            Height = 54,
            Background = GetTransparentBrush(),
            BorderBrush = new SolidColorBrush(GetConfiguredAccentColor()),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(27),
            Child = selectedAvatarPreview
        };
        TextBox nameBox = new()
        {
            Header = AeroLocalizer.Get("Profile.Name"),
            PlaceholderText = AeroLocalizer.Get("Profile.NamePlaceholder"),
            MaxLength = 80
        };
        AutomationProperties.SetName(nameBox, AeroLocalizer.Get("Profile.Name"));

        Grid avatarGrid = new() { ColumnSpacing = 5, RowSpacing = 5 };
        for (int column = 0; column < 8; column++)
        {
            avatarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }
        avatarGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        avatarGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        int index = 0;
        foreach (string avatar in BrowserProfileManager.AvailableIcons)
        {
            Button avatarButton = new()
            {
                Width = 38,
                Height = 38,
                Padding = new Thickness(0),
                Content = new TextBlock
                {
                    Text = avatar,
                    FontSize = 18,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                },
                Tag = avatar
            };
            Grid.SetColumn(avatarButton, index % 8);
            Grid.SetRow(avatarButton, index / 8);
            avatarButton.Click += (_, _) =>
            {
                selectedAvatar = (string)avatarButton.Tag;
                selectedAvatarPreview.Text = selectedAvatar;
                selectedPhoto = null;
                preview.Child = selectedAvatarPreview;
            };
            AutomationProperties.SetName(
                avatarButton,
                AeroLocalizer.Format("Profile.AvatarAutomation", index + 1));
            avatarGrid.Children.Add(avatarButton);
            index++;
        }

        StackPanel content = new() { Width = 340, Spacing = 12 };
        content.Children.Add(preview);
        content.Children.Add(nameBox);
        content.Children.Add(new TextBlock
        {
            Text = AeroLocalizer.Get("Profile.ChooseAvatar"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        content.Children.Add(avatarGrid);

        Button photoButton = new()
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = "", FontSize = 14 },
                    new TextBlock { Text = AeroLocalizer.Get("Profile.UploadPhoto") }
                }
            }
        };
        photoButton.Click += async (_, _) =>
        {
            // The dialog stays open: the file picker is a separate system window.
            if (await PickProfileAvatarAsync() is byte[] png)
            {
                selectedPhoto = png;
                BitmapImage image = new();
                using (Windows.Storage.Streams.InMemoryRandomAccessStream stream = new())
                {
                    await stream.WriteAsync(png.AsBuffer());
                    stream.Seek(0);
                    await image.SetSourceAsync(stream);
                }

                preview.Child = BuildAvatarEllipse(image, 50);
            }
        };
        content.Children.Add(photoButton);

        ContentDialog dialog = new()
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = AeroLocalizer.Get("Profile.DialogTitle"),
            Content = content,
            PrimaryButtonText = AeroLocalizer.Get("Common.Create"),
            CloseButtonText = AeroLocalizer.Get("Common.Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        ContentDialogResult result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return null;
        }

        BrowserProfile created = BrowserProfileManager.Create(nameBox.Text, selectedAvatar);
        if (selectedPhoto is not null)
        {
            BrowserProfileManager.SetAvatar(created, selectedPhoto);
        }

        return created;
    }

    private async void OpenGuestProfileMenuButton_Click(object sender, RoutedEventArgs e)
    {
        AeroMenuFlyout.Hide();
        await SwitchToProfileAsync(BrowserProfileManager.CreateGuest());
    }

    private async void ManageProfilesMenuButton_Click(object sender, RoutedEventArgs e)
    {
        AeroMenuFlyout.Hide();
        OpenSettingsPane();
        await Task.Delay(180);
        try
        {
            if (ActiveTab?.Browser.CoreWebView2 is not null)
            {
                await ActiveTab.Browser.CoreWebView2.ExecuteScriptAsync("show('profiles')");
            }
        }
        catch
        {
            // The settings page can still be navigating; it remains accessible
            // from the Profiles item in its sidebar.
        }
    }
}
