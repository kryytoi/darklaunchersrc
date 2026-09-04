using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DarkVisualsLauncher1.Security
{
    internal sealed class ModDownloadResult
    {
        public string JarPath { get; init; } = "";
    }

    /// <summary>
    /// Скачивание зашифрованного darkvisuals.enc, расшифровка в память,
    /// водяной знак — плюс запись session.json в run/darkvisuals/.
    ///
    /// ВАЖНО (почему раньше висел "вечно загрузка darkvisuals"):
    /// HttpClient.Timeout НЕ распространяется на чтение тела ответа, когда
    /// используется HttpCompletionOption.ResponseHeadersRead. Таймаут срабатывал
    /// только до получения заголовков, а ReadAsByteArrayAsync() по зависшему
    /// коннекту ждала вечно, без исключения и без прогресса.
    /// Теперь: потоковая запись в файл, прогресс в UI, stall-таймаут
    /// (нет данных 45 сек) и общий бюджет (15 мин), поддержка кнопки "Отмена".
    /// </summary>
    internal sealed class LicenseClient
    {
        /// <summary>Нет данных из сети дольше этого времени => ошибка "зависло".</summary>
        private const int StallTimeoutMs = 45_000;

        /// <summary>Общий бюджет на скачивание одного файла.</summary>
        private static readonly TimeSpan DownloadBudget = TimeSpan.FromMinutes(15);

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
            Action<string> reportStatus,
            Action<double, long, long>? reportProgress = null,
            CancellationToken userToken = default)
        {
            reportStatus("Проверка лицензии...");

            HttpResponseMessage? resp = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, $"{_serverBaseUrl}/api/mod-key");
                    req.Content = JsonContent.Create(new { login, hwid, sessionToken });
                    resp = await _http.SendAsync(req, userToken);
                    break;
                }
                catch (HttpRequestException) when (attempt < 3)
                {
                    reportStatus($"Сервер просыпается... попытка {attempt + 1}/3");
                    await Task.Delay(4000, userToken);
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

            KeyResponse? keyInfo;
            try
            {
                keyInfo = await resp.Content.ReadFromJsonAsync<KeyResponse>();
            }
            catch (Exception ex)
            {
                throw new Exception("Сервер лицензий вернул некорректный ответ (не удалось разобрать JSON). Попробуй позже.", ex);
            }

            byte[] aesKey;
            byte[] aesIv;
            try
            {
                aesKey = Convert.FromBase64String(keyInfo!.KeyBase64);
                aesIv = Convert.FromBase64String(keyInfo.IvBase64);
            }
            catch (FormatException ex)
            {
                throw new Exception("Сервер лицензий вернул некорректный AES-ключ/IV (не Base64). Попробуй позже.", ex);
            }

            if (string.IsNullOrWhiteSpace(keyInfo.ModUrl))
                throw new Exception("Сервер лицензий не вернул адрес файла мода (ModUrl пуст).");

            // ===== Скачивание .enc в файл с прогрессом и защитой от зависаний =====
            reportStatus("Загрузка: darkvisuals");
            string tempEncPath = Path.Combine(modsFolder, "darkvisuals.enc.tmp");

            long expectedLength = 0;
            try
            {
                await using (var tempFile = new FileStream(
                                 tempEncPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    expectedLength = await DownloadWithProgressAsync(
                        keyInfo.ModUrl, tempFile,
                        (frac, received, total) =>
                        {
                            string mb = (received / 1048576.0).ToString("0.#");
                            reportStatus(total > 0
                                ? $"Загрузка: darkvisuals — {mb} МБ ({(int)(frac * 100)}%)"
                                : $"Загрузка: darkvisuals — {mb} МБ");
                            reportProgress?.Invoke(frac, received, total);
                        },
                        userToken);
                }

                if (expectedLength > 0 && new FileInfo(tempEncPath).Length != expectedLength)
                {
                    throw new InvalidDataException(
                        $"Файл darkvisuals.enc скачан не полностью: получено {new FileInfo(tempEncPath).Length} байт, " +
                        $"ожидалось {expectedLength}. Проверь интернет-соединение и попробуй снова.");
                }
            }
            catch
            {
                TryDelete(tempEncPath);
                throw;
            }

            userToken.ThrowIfCancellationRequested();
            reportStatus("Расшифровка...");

            try
            {
                byte[] encrypted = await File.ReadAllBytesAsync(tempEncPath, userToken);
                byte[] jarBytes = DecryptAes(encrypted, aesKey, aesIv);
                jarBytes = AddWatermark(jarBytes, $"{login}|{hwid}|{DateTime.UtcNow:O}");

                string jarPath = Path.Combine(modsFolder, "darkvisuals.jar");
                await WriteJarWithRetryAsync(jarPath, jarBytes, userToken);

                WriteSessionFile(mcRunDirectory, login, hwid, sessionToken, keyInfo.SessionTtlSeconds);

                return new ModDownloadResult { JarPath = jarPath };
            }
            finally
            {
                TryDelete(tempEncPath);
            }
        }

        // Зеркала fabric-api: сначала наш CDN на Vercel (открыт в РФ,
        // проверено живым запуском), потом официальные источники.
        // GitHub-релизы из России часто не отвечают 60+ секунд,
        // поэтому именно он идёт на второй позиции.
        private static readonly string[] FabricApiMirrors =
        {
            "https://darkvisuals.vercel.app/static/fabric-api-0.112.0+1.21.4.jar",
            "https://github.com/FabricMC/fabric-api/releases/download/0.112.0+1.21.4/fabric-api-0.112.0+1.21.4.jar",
            "https://cdn.modrinth.com/data/P7dR8mSH/versions/kgg9d3no/fabric-api-0.112.0%2B1.21.4.jar",
        };

        public async Task DownloadFabricApiAsync(
            string modsFolder,
            Action<string> reportStatus,
            Action<double, long, long>? reportProgress = null,
            CancellationToken userToken = default)
        {
            reportStatus("Загрузка: fabric-api.jar");

            string target = Path.Combine(modsFolder, "fabric-api.jar");
            string tmp = target + ".tmp";

            Exception? lastError = null;
            for (int i = 0; i < FabricApiMirrors.Length; i++)
            {
                string apiUrl = FabricApiMirrors[i];
                try
                {
                    await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await DownloadWithProgressAsync(apiUrl, fs,
                            (frac, received, total) => reportProgress?.Invoke(frac, received, total),
                            userToken);
                    }

                    if (File.Exists(target))
                        File.SetAttributes(target, FileAttributes.Normal);
                    File.Move(tmp, target, overwrite: true);
                    return; // успех
                }
                catch (OperationCanceledException)
                {
                    // Отмена пользователем — не прыгаем по зеркалам, выходим сразу.
                    TryDelete(tmp);
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    TryDelete(tmp);
                    reportStatus($"Зеркало {i + 1}/{FabricApiMirrors.Length} недоступно, пробуем следующее...");
                }
            }

            throw new Exception(
                "Не удалось скачать fabric-api.jar ни с одного зеркала.\n" +
                $"Последняя ошибка: {lastError?.Message}",
                lastError);
        }

        /// <summary>
        /// Скачивает url в target-поток с прогрессом.
        ///
        /// Защита от двух типов зависаний, которые раньше вешали лаунчер навсегда:
        ///  1) stall: больше StallTimeoutMs не приходит ни одного байта => TimeoutException;
        ///  2) общий бюджет: DownloadBudget на весь файл => TimeoutException.
        /// userToken — отмена пользователем (кнопка "Отмена"), пробрасывается как есть.
        /// Возвращает Content-Length (0, если сервер его не указал).
        /// </summary>
        private async Task<long> DownloadWithProgressAsync(
            string url,
            Stream target,
            Action<double, long, long> onProgress,
            CancellationToken userToken)
        {
            using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(userToken);
            budgetCts.CancelAfter(DownloadBudget);

            // Фаза заголовков (DNS/TCP/TLS/редиректы) по-прежнему подчиняется
            // общему HttpClient.Timeout (60 c) + нашему бюджету.
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, budgetCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception(
                    $"Не удалось скачать файл (сервер ответил {(int)response.StatusCode} {response.ReasonPhrase}). " +
                    "Попробуй ещё раз через минуту.");
            }

            long? total = response.Content.Headers.ContentLength;
            using var stream = await response.Content.ReadAsStreamAsync(budgetCts.Token);

            var buffer = new byte[64 * 1024];
            long received = 0;

            while (true)
            {
                userToken.ThrowIfCancellationRequested();

                // Каждый read подчиняется собственному stall-таймеру:
                // если сеть молчит дольше StallTimeoutMs — падаем с понятной ошибкой,
                // а не висим вечно (HttpClient.Timeout сюда не распространяется).
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(budgetCts.Token);
                readCts.CancelAfter(StallTimeoutMs);

                int n;
                try
                {
                    n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), readCts.Token);
                }
                catch (OperationCanceledException) when (!budgetCts.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"Скачивание зависло: больше {StallTimeoutMs / 1000} секунд не приходит ни одного байта. " +
                        "Проверь интернет-соединение (или VPN) и попробуй снова.");
                }
                catch (OperationCanceledException)
                {
                    if (userToken.IsCancellationRequested) throw; // отмена пользователем
                    throw new TimeoutException("Скачивание прервано: превышено время загрузки.");
                }

                if (n <= 0)
                    break;

                await target.WriteAsync(buffer.AsMemory(0, n), budgetCts.Token);
                received += n;
                onProgress(total.HasValue ? (double)received / total.Value : double.NaN, received, total ?? 0);
            }

            return total ?? 0;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static async Task WriteJarWithRetryAsync(string jarPath, byte[] jarBytes, CancellationToken userToken = default)
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

                    await File.WriteAllBytesAsync(jarPath, jarBytes, userToken);
                    return; // успех
                }
                catch (Exception ex) when (
                    (ex is UnauthorizedAccessException || ex is IOException)
                    && attempt < maxAttempts
                    && !userToken.IsCancellationRequested)
                {
                    // Файл ещё занят предыдущим java.exe/антивирусом — ждём и пробуем снова,
                    // вместо того чтобы сразу падать в краш-диалог.
                    await Task.Delay(500, userToken);
                }
            }

            // Последняя попытка — пусть бросает как есть, чтобы пользователь
            // увидел настоящую причину, если дело не в блокировке файла.
            if (File.Exists(jarPath))
                File.SetAttributes(jarPath, FileAttributes.Normal);
            await File.WriteAllBytesAsync(jarPath, jarBytes, userToken);
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
                    $"Зашифрованный файл мода повреждён или скачан не полностью (размер {data.Length} байт не кратен 16). " +
                    "Скорее всего, обрыв соединения при загрузке.");

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
                    "Похоже на рассинхрон между сервером лицензий и файлом darkvisuals.enc " +
                    "(например, .enc обновили, а ключ выдаётся под старую версию), либо файл повреждён при скачивании.");
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
