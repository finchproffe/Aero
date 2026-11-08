// Aero Vault. Vault is Powered by Aero.
// Copyright (c) 2026 finchproffe. Licensed under the Mozilla Public License 2.0.
// Under MPL-2.0 section 3.4 this notice must stay in every copy and fork of this file.
using Aero.App.Security;
using Aero.App.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text;

namespace Aero.App;

public sealed partial class MainWindow
{
    private static readonly TimeSpan RevealRequestTimeout = TimeSpan.FromSeconds(60);

    private async void ViewSavedPasswordsButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowSavedSignInsAsync();
    }

    private async Task ShowSavedSignInsAsync()
    {
        ViewSavedPasswordsButton.IsEnabled = false;
        PasswordsStatusText.Text = AeroLocalizer.Get("Vault.ListWaiting");
        List<PasswordBox> secretFields = [];
        List<CancellationTokenSource> revealOperations = [];
        StackPanel? entriesPanel = null;

        try
        {
            IntPtr windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            using PasswordMetadataListLease entries =
                await _passwordVaultService.GetPasswordMetadataListJitAsync(windowHandle);

            entriesPanel = new StackPanel { Spacing = 8 };
            if (entries.Entries.Count == 0)
            {
                entriesPanel.Children.Add(new TextBlock
                {
                    Text = AeroLocalizer.Get("Vault.Empty"),
                    Foreground = GetMutedTextBrush(),
                    TextWrapping = TextWrapping.Wrap
                });
            }
            else
            {
                foreach (PasswordMetadataEntryLease entry in entries.Entries)
                {
                    string origin = Encoding.UTF8.GetString(entry.OriginUtf8.Span);
                    string username = Encoding.UTF8.GetString(entry.UsernameUtf8.Span);
                    entriesPanel.Children.Add(BuildSavedSignInRow(
                        origin,
                        username,
                        entriesPanel,
                        windowHandle,
                        secretFields,
                        revealOperations));
                }
            }

            ScrollViewer scrollViewer = new()
            {
                MaxHeight = 520,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = entriesPanel
            };
            ContentDialog dialog = new()
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = AeroLocalizer.Get("Vault.DialogTitle"),
                Content = scrollViewer,
                PrimaryButtonText = AeroLocalizer.Get("Common.Close"),
                DefaultButton = ContentDialogButton.Primary
            };

            await dialog.ShowAsync();
            PasswordsStatusText.Text = AeroLocalizer.Get("Vault.Closed");
        }
        catch (PasswordVaultVerificationException exception)
        {
            PasswordsStatusText.Text = exception.Message;
        }
        catch (Exception exception)
        {
            PasswordsStatusText.Text =
                AeroLocalizer.Format("Vault.OpenFailed", exception.Message);
        }
        finally
        {
            foreach (CancellationTokenSource operation in revealOperations.ToArray())
            {
                operation.Cancel();
                operation.Dispose();
            }
            revealOperations.Clear();

            foreach (PasswordBox secretField in secretFields)
            {
                secretField.PasswordRevealMode = PasswordRevealMode.Hidden;
                secretField.Password = string.Empty;
            }
            secretFields.Clear();
            entriesPanel?.Children.Clear();
            await RefreshPasswordVaultCountAsync();
        }
    }

    private Border BuildSavedSignInRow(
        string origin,
        string username,
        StackPanel parent,
        IntPtr windowHandle,
        List<PasswordBox> secretFields,
        List<CancellationTokenSource> revealOperations)
    {
        Grid grid = new() { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        StackPanel text = new() { Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = origin,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        text.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(username)
                ? AeroLocalizer.Get("Vault.NoUsername")
                : username,
            Foreground = GetMutedTextBrush(),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        Grid passwordRow = new() { ColumnSpacing = 6 };
        passwordRow.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        passwordRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        const string mask = "••••••••••••";
        PasswordBox passwordField = new()
        {
            Password = string.Empty,
            PlaceholderText = mask,
            PasswordRevealMode = PasswordRevealMode.Hidden,
            IsHitTestVisible = false,
            IsTabStop = false,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        secretFields.Add(passwordField);
        passwordRow.Children.Add(passwordField);

        Button revealButton = new()
        {
            Content = AeroLocalizer.Get("Common.Show"),
            Padding = new Thickness(9, 3, 9, 3)
        };
        Grid.SetColumn(revealButton, 1);

        CancellationTokenSource? revealOperation = null;

        TextBlock revealStatus = new()
        {
            Visibility = Visibility.Collapsed,
            Foreground = GetMutedTextBrush(),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };

        void SetRevealStatus(string? message)
        {
            revealStatus.Text = message ?? string.Empty;
            revealStatus.Visibility = string.IsNullOrWhiteSpace(message)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        void ResetRevealButton()
        {
            // The actual password is never assigned to this Aero.App PasswordBox.
            // It is displayed only by the Broker's separate WinUI TextBox.
            passwordField.PasswordRevealMode = PasswordRevealMode.Hidden;
            passwordField.Password = string.Empty;
            revealButton.Content = AeroLocalizer.Get("Common.Show");
        }

        revealButton.Click += async (_, _) =>
        {
            if (revealOperation is not null)
            {
                return;
            }

            revealButton.IsEnabled = false;
            revealButton.Content = AeroLocalizer.Get("Vault.RevealWaiting");
            SetRevealStatus(AeroLocalizer.Get("Vault.RevealWaiting"));

            // This is only a UI state marker. The PasswordBox remains empty: the
            // password is displayed only inside the Broker-owned WinUI window.
            passwordField.PasswordRevealMode = PasswordRevealMode.Visible;

            CancellationTokenSource operation = new(RevealRequestTimeout);
            revealOperation = operation;
            revealOperations.Add(operation);

            try
            {
                // Reveal crosses the PasswordVaultService boundary and is displayed
                // by a Broker-owned WinUI window. Aero.App receives only a signed
                // completion acknowledgement and never receives password bytes.
                bool completed = await _passwordVaultService.GetPasswordForRevealAsync(
                    origin,
                    windowHandle,
                    operation.Token);

                if (!operation.IsCancellationRequested)
                {
                    string resultMessage = completed
                        ? AeroLocalizer.Get("Vault.RevealCompleted")
                        : AeroLocalizer.Get("Vault.RevealNotFound");
                    PasswordsStatusText.Text = resultMessage;
                    SetRevealStatus(resultMessage);
                }
            }
            catch (OperationCanceledException)
            {
                // Closing the dialog or deleting the entry cancels the one-shot Broker.
            }
            catch (PasswordVaultVerificationException exception)
            {
                PasswordsStatusText.Text = exception.Message;
                SetRevealStatus(exception.Message);
            }
            catch (PasswordBrokerException exception)
            {
                string message = exception.Failure switch
                {
                    PasswordBrokerFailure.UserModeProtectionUnavailable =>
                        exception.Message,
                    PasswordBrokerFailure.VerificationCancelled =>
                        AeroLocalizer.Get("Vault.HelloNotCompleted"),
                    PasswordBrokerFailure.BrokerUnavailable =>
                        string.IsNullOrWhiteSpace(exception.Message)
                            ? AeroLocalizer.Get("Vault.BrokerUnavailable")
                            : $"{AeroLocalizer.Get("Vault.BrokerUnavailable")}\n{exception.Message}",
                    _ => exception.Message
                };
                PasswordsStatusText.Text = message;
                SetRevealStatus(message);
            }
            catch (Exception exception)
            {
                string message = AeroLocalizer.Format("Vault.OpenFailed", exception.Message);
                PasswordsStatusText.Text = message;
                SetRevealStatus(message);
            }
            finally
            {
                ResetRevealButton();
                revealButton.IsEnabled = true;
                if (ReferenceEquals(revealOperation, operation))
                {
                    revealOperation = null;
                }
                revealOperations.Remove(operation);
                operation.Dispose();
            }
        };
        passwordRow.Children.Add(revealButton);
        text.Children.Add(passwordRow);

        text.Children.Add(revealStatus);

        grid.Children.Add(text);

        Button deleteButton = new()
        {
            Content = AeroLocalizer.Get("Common.Delete"),
            Tag = origin,
            IsEnabled = !_isPrivateWindow,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(deleteButton, 1);
        grid.Children.Add(deleteButton);

        Border row = new()
        {
            Padding = new Thickness(10),
            Background = GetTransparentBrush(),
            BorderBrush = GetSoftBorderBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Child = grid
        };

        deleteButton.Click += async (_, _) =>
        {
            deleteButton.IsEnabled = false;
            revealOperation?.Cancel();
            ResetRevealButton();

            try
            {
                bool deleted = await _passwordVaultService.DeletePasswordAsync(
                    origin,
                    windowHandle);
                if (deleted)
                {
                    passwordField.PasswordRevealMode = PasswordRevealMode.Hidden;
                    passwordField.Password = string.Empty;
                    secretFields.Remove(passwordField);
                    parent.Children.Remove(row);
                    PasswordsStatusText.Text = AeroLocalizer.Format("Vault.Removed", origin);
                    await RefreshPasswordVaultCountAsync();
                }
                else
                {
                    deleteButton.IsEnabled = true;
                }
            }
            catch (Exception exception)
            {
                deleteButton.IsEnabled = true;
                PasswordsStatusText.Text =
                    AeroLocalizer.Format("Vault.RemoveFailed", exception.Message);
            }
        };

        return row;
    }
}
