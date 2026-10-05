using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BatPlayer.Services;
using BatPlayer.Services.Spotify;

namespace BatPlayer.Views;

/// <summary>
/// Окно входа в Spotify — OAuth 2.0 Authorization Code Flow с PKCE.
///
/// Последовательность:
/// 1. Client ID: поле в этом окне всегда редактируемо; значение хранится в
///    spotify_client.json. Без своего Client ID Spotify отклоняет авторизацию
///    с «client_id: Invalid» — инструкция, где его взять, есть в окне.
/// 2. Генерация code_verifier и code_challenge
/// 3. Открытие браузера для авторизации (accounts.spotify.com/authorize)
/// 4. Запуск локального HTTP сервера на localhost:8888
/// 5. Ожидание callback от Spotify с authorization code
/// 6. Обмен code на access_token + refresh_token
/// 7. Сохранение токенов в spotify_auth.json
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
        // Поле всегда открыто и предзаполнено сохранённым значением —
        // Client ID можно вписать или изменить перед каждым подключением.
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

        // 0. Client ID: сохраняем текущее значение поля (можно менять каждый раз).
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
            // 1. Генерация PKCE параметров
            var (authUrl, codeVerifier) = SpotifyService.GenerateAuthUrl();
            _codeVerifier = codeVerifier;

            // 2. Запуск локального HTTP сервера
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

            // 3. Открытие браузера
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

            // 4. Ожидание callback
            var context = await _httpListener.GetContextAsync();

            // Получаем параметры из query string
            var code = context.Request.QueryString["code"];
            var error = context.Request.QueryString["error"];

            // Отправляем ответ браузеру
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

            // 5. Обмен code на токены
            StatusText.Text = "Exchanging code for token...";
            var success = await _spotify.ExchangeCodeForTokenAsync(code, _codeVerifier, _cts.Token);

            if (!success)
            {
                Logger.Error("Spotify token exchange failed");
                StatusText.Text = "Failed to exchange code for token. Most often the Client ID is wrong or the redirect URI http://localhost:8888/callback is not registered in the Spotify Dashboard app — fix it above and try again.";
                ConnectButton.IsEnabled = true;
                return;
            }

            // 6. Успех!
            _finished = true;
            StatusText.Text = "✓ Connected successfully!";
            Logger.Info("Spotify authorization successful");

            DialogResult = true;
            Close();
        }
        catch (InvalidOperationException ex)
        {
            // Client ID не настроен — поле и так всегда открыто, подсказываем текстом.
            Logger.Warn($"Spotify login: {ex.Message}");
            StatusText.Text = ex.Message;
            ClientIdBox.Focus();
            ConnectButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            // Окно закрыто пользователем
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
