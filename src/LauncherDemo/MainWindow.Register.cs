using System.Net;
using System.Net.Http.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace OSFR.Linux.LauncherDemo;

public partial class MainWindow
{
    private Control BuildRegistrationPanel(SavedServerListItem selected, Action backToSignIn)
    {
        var usernameBox = new TextBox
        {
            Watermark = "Username"
        };

        var passwordBox = new TextBox
        {
            Watermark = "Password",
            PasswordChar = '●'
        };

        var confirmPasswordBox = new TextBox
        {
            Watermark = "Confirm password",
            PasswordChar = '●'
        };

        var statusText = new TextBlock
        {
            Text = "Create an account for this server. Your credentials are sent only to this server's HTTPS registration endpoint.",
            Foreground = Muted,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        };

        var createButton = new Button
        {
            Content = "CREATE ACCOUNT",
            Classes = { "primary" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height = 48
        };

        var backButton = new Button
        {
            Content = "BACK TO SIGN IN",
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(14, 8)
        };

        var form = new StackPanel
        {
            Spacing = 13,
            Children =
            {
                new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#111B14")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#31533C")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(14),
                    Child = new TextBlock
                    {
                        Text = $"Registering on {selected.DisplayName}",
                        Foreground = Good,
                        FontWeight = FontWeight.SemiBold
                    }
                },
                new StackPanel
                {
                    Spacing = 5,
                    Children =
                    {
                        new TextBlock { Text = "Username", Foreground = Brushes.LightGray, FontSize = 11 },
                        usernameBox,
                        new TextBlock
                        {
                            Text = "3–50 characters. Letters, numbers, underscores, and dots only.",
                            Foreground = new SolidColorBrush(Color.Parse("#777777")),
                            FontSize = 10,
                            TextWrapping = TextWrapping.Wrap
                        }
                    }
                },
                new StackPanel
                {
                    Spacing = 5,
                    Children =
                    {
                        new TextBlock { Text = "Password", Foreground = Brushes.LightGray, FontSize = 11 },
                        passwordBox
                    }
                },
                new StackPanel
                {
                    Spacing = 5,
                    Children =
                    {
                        new TextBlock { Text = "Confirm password", Foreground = Brushes.LightGray, FontSize = 11 },
                        confirmPasswordBox,
                        new TextBlock
                        {
                            Text = "6–100 ASCII characters. Both password fields must match.",
                            Foreground = new SolidColorBrush(Color.Parse("#777777")),
                            FontSize = 10,
                            TextWrapping = TextWrapping.Wrap
                        }
                    }
                },
                statusText,
                createButton,
                backButton
            }
        };

        backButton.Click += (_, _) => backToSignIn();

        createButton.Click += async (_, _) =>
        {
            var username = usernameBox.Text?.Trim() ?? string.Empty;
            var password = passwordBox.Text ?? string.Empty;
            var confirmPassword = confirmPasswordBox.Text ?? string.Empty;

            if (!ValidateRegistration(username, password, confirmPassword, out var validationError))
            {
                statusText.Text = validationError;
                statusText.Foreground = Bad;
                return;
            }

            createButton.IsEnabled = false;
            usernameBox.IsEnabled = false;
            passwordBox.IsEnabled = false;
            confirmPasswordBox.IsEnabled = false;
            statusText.Text = "Creating account…";
            statusText.Foreground = Muted;

            try
            {
                var result = await RegisterAccountAsync(selected.Url, username, password);
                statusText.Text = result.Message;
                statusText.Foreground = result.Success ? Good : Bad;

                if (result.Success)
                {
                    UsernameBox.Text = username;
                    PasswordBox.Text = password;
                    usernameBox.Text = string.Empty;
                    passwordBox.Text = string.Empty;
                    confirmPasswordBox.Text = string.Empty;
                    createButton.Content = "ACCOUNT CREATED";
                }
            }
            finally
            {
                usernameBox.IsEnabled = true;
                passwordBox.IsEnabled = true;
                confirmPasswordBox.IsEnabled = true;
                createButton.IsEnabled = true;
            }
        };

        return form;
    }

    private static bool ValidateRegistration(string username, string password, string confirmPassword, out string error)
    {
        error = string.Empty;

        if (username.Length is < 3 or > 50)
        {
            error = "Username must be between 3 and 50 characters long.";
            return false;
        }

        if (username.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '.')))
        {
            error = "Username can only contain letters, numbers, underscores, and dots.";
            return false;
        }

        if (password.Length is < 6 or > 100)
        {
            error = "Password must be between 6 and 100 characters long.";
            return false;
        }

        if (password.Any(ch => ch > 0x7F))
        {
            error = "Password can only contain ASCII characters.";
            return false;
        }

        if (!string.Equals(password, confirmPassword, StringComparison.Ordinal))
        {
            error = "Passwords do not match.";
            return false;
        }

        return true;
    }

    private async Task<RegistrationResult> RegisterAccountAsync(string serverUrl, string username, string password)
    {
        if (!TryNormalizeServerUrl(serverUrl, out var serverBaseUri, out var normalizeError))
            return new RegistrationResult(false, normalizeError);

        try
        {
            var manifestXml = await DownloadTextLimitedAsync(new Uri(serverBaseUri, "servermanifest.xml"), MaxManifestBytes);
            var manifest = ParseServerManifest(manifestXml);

            if (!TryNormalizeHttpsBaseUrl(manifest.WebApiUrl, out var apiBaseUri))
                return new RegistrationResult(false, "Registration is blocked because this server's Web API is not HTTPS.");

            using var response = await _httpClient.PostAsJsonAsync(
                new Uri(apiBaseUri, "register"),
                new RegisterRequest(username, password));

            if (response.StatusCode == HttpStatusCode.Conflict)
                return new RegistrationResult(false, "That username is already registered on this server.");

            if (!response.IsSuccessStatusCode)
                return new RegistrationResult(false, $"Registration failed: {(int)response.StatusCode} {response.ReasonPhrase}.");

            return new RegistrationResult(true, $"Account created on {manifest.Name}. You can sign in now.");
        }
        catch (Exception ex)
        {
            return new RegistrationResult(false, $"Registration failed: {ex.Message}");
        }
    }

    private sealed record RegisterRequest(string Username, string Password);
    private sealed record RegistrationResult(bool Success, string Message);
}
