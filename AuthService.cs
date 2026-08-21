using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace DarkVisualsLauncher1.Security
{
    internal sealed class LoginResult
    {
        public bool Success { get; init; }
        public string Role { get; init; } = "User";
        public string ErrorMessage { get; init; } = "";

        // Короткоживущий подписанный токен от сервера. Кладётся в session.json
        // для мода (см. LicenseClient) — без него /api/verify не пройдёт.
        public string SessionToken { get; init; } = "";
    }

    /// <summary>
    /// Вся сетевая логика логина. Раньше жила прямо в MainWindow.xaml.cs —
    /// но этот класс у тебя стоит в Obfuscar.xml как SkipType (из-за XAML-биндингов),
    /// поэтому имена методов/переменных там оставались читаемыми в декомпиляции.
    /// AuthService не связан с XAML — переименовывается и обфусцируется полностью.
    /// </summary>
    internal sealed class AuthService
    {
        private readonly HttpClient _http;
        private readonly string _serverBaseUrl;

        public AuthService(HttpClient http, string serverBaseUrl)
        {
            _http = http;
            _serverBaseUrl = serverBaseUrl;
        }

        public async Task<LoginResult> LoginAsync(string login, string password, string hwid)
        {
            try
            {
                var response = await _http.PostAsJsonAsync(
                    $"{_serverBaseUrl}/api/login",
                    new { username = login, password, hwid });

                var result = await response.Content.ReadFromJsonAsync<ServerLoginResponse>();

                if (result != null && result.Success)
                {
                    return new LoginResult
                    {
                        Success = true,
                        Role = string.IsNullOrWhiteSpace(result.Role) ? "User" : result.Role,
                        SessionToken = result.SessionToken ?? ""
                    };
                }

                return new LoginResult
                {
                    Success = false,
                    ErrorMessage = result?.Message ?? "Неверный логин или пароль!"
                };
            }
            catch (Exception)
            {
                return new LoginResult
                {
                    Success = false,
                    ErrorMessage = "Сервер недоступен.\nПроверьте интернет или попробуйте позже."
                };
            }
        }

        private sealed class ServerLoginResponse
        {
            [JsonPropertyName("success")]
            public bool Success { get; set; }

            [JsonPropertyName("message")]
            public string? Message { get; set; }

            [JsonPropertyName("role")]
            public string? Role { get; set; }

            // НУЖНО ДОБАВИТЬ на сервере в /api/login: подписанный (например JWT)
            // токен с {login, hwid, exp}. Без него шаг с /api/verify в моде не заработает.
            [JsonPropertyName("sessionToken")]
            public string? SessionToken { get; set; }
        }
    }
}
