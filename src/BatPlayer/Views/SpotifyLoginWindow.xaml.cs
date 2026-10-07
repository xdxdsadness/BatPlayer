using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BatPlayer.Services;
using BatPlayer.Services.Spotify;

namespace BatPlayer.Views;

/// <summary>
/// Spotify login window — OAuth 2.0 Authorization Code Flow with PKCE.
///
/// Sequence:
/// 1. Client ID: the field in this window is always editable; the value is stored in
///    spotify_client.json. Without a valid Client ID Spotify rejects authorization
///    with "client_id: Invalid" — the window explains where to get one.
/// 2. Generate code_verifier and code_challenge
/// 3. Open the browser for authorization (accounts.spotify.com/authorize)
/// 4. Start a local HTTP server on localhost:8888
/// 5. Wait for Spotify's callback with the authorization code
/// 6. Exchange the code for access_token + refresh_token
/// 7. Save the tokens to spotify_auth.json
/// </summary>
public partial class SpotifyLoginWindow : Window
{
    private readonly SpotifyService _spotify;
    private HttpListener? _httpListener;
    private readonly CancellationTokenSource _cts = new();
    private string? _codeVerifier;
    private bool _finished;

    public SpotifyLoginWindow(SpotifyService spotify)
    {
        InitializeComponent();
        _spotify = spotify;
        // The field is always open and pre-filled with the saved value —
        // the Client ID can be entered or changed before each connection.
        ClientIdBox.Text = SpotifyService.GetClientId();
        Closed += (_, _) =>
        {
            _cts.Cancel();
            _httpListener?.Stop();
            _httpListener?.Close();
        };
    }

    private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
            DragMove();
    }

    private void OpenDashboard_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://developer.spotify.com/dashboard",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Spotify login: failed to open dashboard URL");
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_finished) return;

        // 0. Client ID: save the current field value (can be changed each time).
        var clientId = ClientIdBox.Text.Trim();
        if (clientId.Length == 0)
        {
            StatusText.Text = "Enter your Spotify Client ID first — see the steps above.";
            ClientIdBox.Focus();
            return;
        }
        SpotifyService.SaveClientId(clientId);

        ConnectButton.IsEnabled = false;
        StatusText.Text = "Starting local server...";

        try
        {
            // 1. Generate PKCE parameters
            var (authUrl, codeVerifier) = SpotifyService.GenerateAuthUrl();
            _codeVerifier = codeVerifier;

            // 2. Start the local HTTP server
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add("http://localhost:8888/");

            try
            {
                _httpListener.Start();
            }
            catch (HttpListenerException ex)
            {
                Logger.Error(ex, "Spotify login: failed to start HTTP server on port 8888");
                StatusText.Text = $"Error: Port 8888 is already in use. Please close any other application using this port.";
                ConnectButton.IsEnabled = true;
                return;
            }

            StatusText.Text = "Opening browser for authorization...";

            // 3. Open the browser
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = authUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Spotify login: failed to open browser");
                StatusText.Text = $"Failed to open browser. Please navigate to:\n{authUrl}";
            }

            StatusText.Text = "Waiting for authorization...";

            // 4. Wait for the callback
            var context = await _httpListener.GetContextAsync();

            // Read the parameters from the query string
            var code = context.Request.QueryString["code"];
            var error = context.Request.QueryString["error"];

            // Send the response to the browser
            var response = context.Response;
            string responseString;

            if (!string.IsNullOrEmpty(error))
            {
                responseString = $@"
                    <html>
                    <head><title>Spotify Authorization Failed</title></head>
                    <body style='font-family: Arial; text-align: center; padding: 50px;'>
                        <h1 style='color: #ff0000;'>Authorization Failed</h1>
                        <p>Error: {WebUtility.HtmlEncode(error)}</p>
                        <p>You can close this window.</p>
                    </body>
                    </html>";
                response.StatusCode = 400;
            }
            else if (string.IsNullOrEmpty(code))
            {
                responseString = @"
                    <html>
                    <head><title>Spotify Authorization</title></head>
                    <body style='font-family: Arial; text-align: center; padding: 50px;'>
                        <h1 style='color: #ff0000;'>Invalid Response</h1>
                        <p>No authorization code received.</p>
                        <p>You can close this window.</p>
                    </body>
                    </html>";
                response.StatusCode = 400;
            }
            else
            {
                responseString = @"
                    <html>
                    <head><title>Spotify Authorization Successful</title></head>
                    <body style='font-family: Arial; text-align: center; padding: 50px; background: #1DB954; color: white;'>
                        <h1>✓ Authorization Successful!</h1>
                        <p>You can now close this window and return to BatPlayer.</p>
                    </body>
                    </html>";
                response.StatusCode = 200;
            }

            var buffer = System.Text.Encoding.UTF8.GetBytes(responseString);
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.OutputStream.Close();

            _httpListener.Stop();
            _httpListener.Close();

            if (!string.IsNullOrEmpty(error))
            {
                Logger.Warn($"Spotify authorization denied: {error}");
                StatusText.Text = $"Authorization denied: {error}";
                ConnectButton.IsEnabled = true;
                return;
            }

            if (string.IsNullOrEmpty(code))
            {
                Logger.Warn("Spotify authorization: no code received");
                StatusText.Text = "No authorization code received. Please try again.";
                ConnectButton.IsEnabled = true;
                return;
            }

            // 5. Exchange the code for tokens
            StatusText.Text = "Exchanging code for token...";
            var success = await _spotify.ExchangeCodeForTokenAsync(code, _codeVerifier, _cts.Token);

            if (!success)
            {
                Logger.Error("Spotify token exchange failed");
                StatusText.Text = "Failed to exchange code for token. Most often the Client ID is wrong or the redirect URI http://localhost:8888/callback is not registered in the Spotify Dashboard app — fix it above and try again.";
                ConnectButton.IsEnabled = true;
                return;
            }

            // 6. Success!
            _finished = true;
            StatusText.Text = "✓ Connected successfully!";
            Logger.Info("Spotify authorization successful");

            DialogResult = true;
            Close();
        }
        catch (InvalidOperationException ex)
        {
            // Client ID not configured — the field is always open anyway; the error text guides the user.
            Logger.Warn($"Spotify login: {ex.Message}");
            StatusText.Text = ex.Message;
            ClientIdBox.Focus();
            ConnectButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            // Window closed by the user
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Spotify login failed");
            StatusText.Text = $"Error: {ex.Message}";
            ConnectButton.IsEnabled = true;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
