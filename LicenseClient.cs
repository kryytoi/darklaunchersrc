using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace DarkVisualsLauncher1.Security
{
    internal sealed class ModDownloadResult
    {
        public string JarPath { get; init; } = "";
    }

    /// <summary>
    /// Скачивание зашифрованного darkvisuals.enc, расшифровка в память,
    /// водяной знак — плюс НОВОЕ: запись session.json в run/darkvisuals/.
    ///
    /// Раньше на этом всё заканчивалось: расшифрованный jar просто лежал
    /// в mods/ и работал вечно на любой машине без единого обращения к
    /// серверу — скопировать его другу было достаточно, чтобы обойти всю
    /// защиту. Теперь мод сам (LicenseGuard.java на Java-стороне) читает
    /// session.json при каждом запуске игры и переспрашивает /api/verify.
    /// Скопированный jar без валидного session.json от ТВОЕГО лаунчера
    /// просто не активирует модули.
    /// </summary>
    internal sealed class LicenseClient
    {
        private readonly HttpClient _http;
        private readonly string _serverBaseUrl;

        public LicenseClient(HttpClient http, string serverBaseUrl)
        {
            _http = http;
            _serverBaseUrl = serverBaseUrl;
        }

        public async Task<ModDownloadResult> DownloadProtectedModAsync(
            string login,
            string hwid,
            string sessionToken,
            string modsFolder,
            string mcRunDirectory,
            Action<string> reportStatus)
        {
            reportStatus("Проверка лицензии...");

            HttpResponseMessage? resp = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, $"{_serverBaseUrl}/api/mod-key");
                    req.Content = JsonContent.Create(new { login, hwid, sessionToken });
                    resp = await _http.SendAsync(req);
                    break;
                }
                catch (HttpRequestException) when (attempt < 3)
                {
                    reportStatus($"Сервер просыпается... попытка {attempt + 1}/3");
                    await Task.Delay(4000);
                }
            }
            if (resp == null)
                throw new Exception("Сервер лицензий недоступен. Проверь интернет и попробуй ещё раз через минуту.");

            if (!resp.IsSuccessStatusCode)
            {
                string reason = $"Лицензия не подтверждена (mod-key вернул {(int)resp.StatusCode}).";
                try
                {
                    var errInfo = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
                    if (!string.IsNullOrWhiteSpace(errInfo?.Error)) reason = errInfo.Error;
                }
                catch { }
                throw new Exception(reason);
            }

            var keyInfo = await resp.Content.ReadFromJsonAsync<KeyResponse>();
            byte[] aesKey = Convert.FromBase64String(keyInfo!.KeyBase64);
            byte[] aesIv = Convert.FromBase64String(keyInfo.IvBase64);

            reportStatus("Загрузка: darkvisuals");
            byte[] encrypted;
            using (var modResp = await _http.GetAsync(keyInfo.ModUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                modResp.EnsureSuccessStatusCode();
                encrypted = await modResp.Content.ReadAsByteArrayAsync();

                long? expectedLength = modResp.Content.Headers.ContentLength;
                if (expectedLength.HasValue && expectedLength.Value != encrypted.Length)
                {
                    throw new InvalidDataException(
                        $"Файл darkvisuals.enc скачан не полностью: получено {encrypted.Length} байт, " +
                        $"ожидалось {expectedLength.Value}. Проверь интернет-соединение и попробуй снова.");
                }
            }

            byte[] jarBytes = DecryptAes(encrypted, aesKey, aesIv);
            jarBytes = AddWatermark(jarBytes, $"{login}|{hwid}|{DateTime.UtcNow:O}");

            string jarPath = Path.Combine(modsFolder, "darkvisuals.jar");
            await WriteJarWithRetryAsync(jarPath, jarBytes);


            WriteSessionFile(mcRunDirectory, login, hwid, sessionToken, keyInfo.SessionTtlSeconds);

            return new ModDownloadResult { JarPath = jarPath };
        }

        private static async Task WriteJarWithRetryAsync(string jarPath, byte[] jarBytes)
        {
            const int maxAttempts = 5;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    // Если файл остался от прошлой сессии с Hidden|System (или ReadOnly) —
                    // снимаем атрибуты перед перезаписью, иначе WriteAllBytesAsync
                    // падает с UnauthorizedAccessException, даже если файл никем не занят.
                    if (File.Exists(jarPath))
                        File.SetAttributes(jarPath, FileAttributes.Normal);

                    await File.WriteAllBytesAsync(jarPath, jarBytes);
                    return; // успех
                }
                catch (Exception ex) when (
                    (ex is UnauthorizedAccessException || ex is IOException)
                    && attempt < maxAttempts)
                {
                    // Файл ещё занят предыдущим java.exe/антивирусом — ждём и пробуем снова,
                    // вместо того чтобы сразу падать в краш-диалог.
                    await Task.Delay(500);
                }
            }

            // Последняя попытка — пусть бросает как есть, чтобы пользователь
            // увидел настоящую причину, если дело не в блокировке файла.
            if (File.Exists(jarPath))
                File.SetAttributes(jarPath, FileAttributes.Normal);
            await File.WriteAllBytesAsync(jarPath, jarBytes);
        }

        public async Task DownloadFabricApiAsync(string modsFolder, Action<string> reportStatus)
        {
            reportStatus("Загрузка: fabric-api.jar");
            string apiUrl = "https://github.com/FabricMC/fabric-api/releases/download/0.112.0+1.21.4/fabric-api-0.112.0+1.21.4.jar";
            byte[] apiBytes = await _http.GetByteArrayAsync(apiUrl);
            await File.WriteAllBytesAsync(Path.Combine(modsFolder, "fabric-api.jar"), apiBytes);
        }

        private static void WriteSessionFile(string mcRunDirectory, string login, string hwid, string sessionToken, int ttlSeconds)
        {
            try
            {
                string dir = Path.Combine(mcRunDirectory, "darkvisuals");
                Directory.CreateDirectory(dir);

                var payload = new
                {
                    login,
                    hwid,
                    sessionToken,
                    issuedAtUtc = DateTime.UtcNow,
                    expiresAtUtc = DateTime.UtcNow.AddSeconds(ttlSeconds > 0 ? ttlSeconds : 3600)
                };

                File.WriteAllText(Path.Combine(dir, "session.json"), JsonSerializer.Serialize(payload));
            }
            catch
            {
                // Если не удалось записать — мод не найдёт session.json и сам
                // откажется активироваться (fail-closed, а не fail-open).
            }
        }

        private static byte[] DecryptAes(byte[] data, byte[] key, byte[] iv)
        {
            if (data == null || data.Length == 0)
                throw new InvalidDataException("Зашифрованный файл мода пуст (0 байт) — похоже, скачивание не удалось.");

            if (data.Length % 16 != 0)
                throw new InvalidDataException(
                    $"Зашифрованный файл мода повреждён или скачан не полностью (размер {data.Length} байт не кратен 16).");

            if (key == null || (key.Length != 16 && key.Length != 24 && key.Length != 32))
                throw new InvalidDataException($"Некорректный AES-ключ от сервера лицензий (длина {key?.Length ?? 0} байт, ожидалось 16/24/32).");

            if (iv == null || iv.Length != 16)
                throw new InvalidDataException($"Некорректный IV от сервера лицензий (длина {iv?.Length ?? 0} байт, ожидалось 16).");

            try
            {
                using var aes = Aes.Create();
                aes.Key = key; aes.IV = iv;
                using var dec = aes.CreateDecryptor();
                return dec.TransformFinalBlock(data, 0, data.Length);
            }
            catch (CryptographicException)
            {
                throw new InvalidDataException(
                    "Не удалось расшифровать файл мода: ключ/IV не подходят к скачанному файлу. " +
                    "Похоже на рассинхрон между сервером лицензий и файлом darkvisuals.enc, либо файл повреждён при скачивании.");
            }
        }

        private static byte[] AddWatermark(byte[] jar, string mark)
        {
            byte[] comment = System.Text.Encoding.UTF8.GetBytes("DV:" + mark);
            if (comment.Length > ushort.MaxValue) return jar;

            int eocd = -1;
            int stop = Math.Max(0, jar.Length - 22 - 65535);
            for (int i = jar.Length - 22; i >= stop; i--)
            {
                if (jar[i] == 0x50 && jar[i + 1] == 0x4B && jar[i + 2] == 0x05 && jar[i + 3] == 0x06)
                {
                    eocd = i;
                    break;
                }
            }
            if (eocd < 0) return jar;

            int endOfEocd = eocd + 22;
            var result = new byte[endOfEocd + comment.Length];
            Buffer.BlockCopy(jar, 0, result, 0, endOfEocd);
            result[eocd + 20] = (byte)(comment.Length & 0xFF);
            result[eocd + 21] = (byte)((comment.Length >> 8) & 0xFF);
            Buffer.BlockCopy(comment, 0, result, endOfEocd, comment.Length);
            return result;
        }

        private sealed class KeyResponse
        {
            [JsonPropertyName("KeyBase64")]
            public string KeyBase64 { get; set; } = "";

            [JsonPropertyName("IvBase64")]
            public string IvBase64 { get; set; } = "";

            [JsonPropertyName("ModUrl")]
            public string ModUrl { get; set; } = "";

            // НУЖНО ДОБАВИТЬ на сервере в /api/mod-key: сколько секунд
            // session.json валиден на клиенте до принудительной переспросы.
            [JsonPropertyName("SessionTtlSeconds")]
            public int SessionTtlSeconds { get; set; } = 3600;
        }

        private sealed class ErrorResponse
        {
            [JsonPropertyName("error")]
            public string? Error { get; set; }
        }
    }
}