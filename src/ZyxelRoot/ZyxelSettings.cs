using System.Text.Json;

namespace ZyxelRoot;

/// <summary>
/// Connection settings for a Zyxel FWA505 (or compatible ZCFG device).
/// Precedence: constructor/CLI values &gt; <c>ZYXEL_*</c>
/// environment variables &gt; the <c>Zyxel</c> section of an appsettings.json file.
/// </summary>
public sealed class ZyxelSettings
{
    /// <summary>Router address (default 192.168.1.1).</summary>
    public string Host { get; set; } = "192.168.1.1";

    /// <summary>SSH port of the admin (uid 21) login (default 22).</summary>
    public int SshPort { get; set; } = 22;

    /// <summary>SSH / web UI user name (default "admin").</summary>
    public string Username { get; set; } = "admin";

    /// <summary>SSH / web UI password.</summary>
    public string Password { get; set; } = "";

    /// <summary>Optional distinct web UI user (defaults to <see cref="Username"/>).</summary>
    public string? WebUsername { get; set; }

    /// <summary>Optional distinct web UI password (defaults to <see cref="Password"/>).</summary>
    public string? WebPassword { get; set; }

    /// <summary>
    /// SSH user name for the direct root login (config key
    /// <c>Zyxel:RootUsername</c>; default "root").
    /// </summary>
    public string RootUsername { get; set; } = "root";

    /// <summary>
    /// Password for the direct root SSH login (config key
    /// <c>Zyxel:RootPassword</c>). When set, <see cref="PersistentRootShell"/>
    /// connects straight over SSH as root — no daemon, no breakout — and falls
    /// back to the root-telnetd bootstrap path only when this login fails
    /// (e.g. firmware without the root account).
    /// </summary>
    public string RootPassword { get; set; } = "";

    /// <summary>
    /// ZySH breakout payload (config key <c>Zyxel:Exploit</c>).
    /// Loaded from the configuration — there is deliberately no built-in default.
    /// </summary>
    public string Exploit { get; set; } = "";

    /// <summary>
    /// Text to wait for in the pty after the breakout payload is sent (config key
    /// <c>Zyxel:ExploitConfirm</c>). No built-in default.
    /// </summary>
    public string ExploitConfirm { get; set; } = "";

    /// <summary>
    /// ZCFG DAL object id targeted by the web root injection (config key
    /// <c>Zyxel:DalOid</c>). No built-in default.
    /// </summary>
    public string DalOid { get; set; } = "";

    /// <summary>
    /// Payload field that carries the injected command (config key
    /// <c>Zyxel:DalCommandField</c>). No built-in default.
    /// </summary>
    public string DalCommandField { get; set; } = "";

    /// <summary>
    /// Format applied to the injected command before it is stored in
    /// <see cref="DalCommandField"/>; <c>{0}</c> is replaced by the command (config key
    /// <c>Zyxel:DalCommandWrap</c>). No built-in default.
    /// </summary>
    public string DalCommandWrap { get; set; } = "";

    /// <summary>
    /// Base payload template for the web root injection (config key
    /// <c>Zyxel:DalPayload</c>, a JSON object without the command field). At runtime the
    /// wrapped command is merged into it under <see cref="DalCommandField"/>.
    /// No built-in default.
    /// </summary>
    public Dictionary<string, JsonElement>? DalPayload { get; set; }

    /// <summary>TCP port of the unauthenticated root telnetd (default 4444).</summary>
    public int RootPort { get; set; } = 4444;

    /// <summary>Path of the appsettings.json this instance was loaded from, if any.</summary>
    public string? ConfigPath { get; set; }

    public string EffectiveWebUser => WebUsername ?? Username;

    public string EffectiveWebPassword => WebPassword ?? Password;

    /// <summary>Default location used by the Python harness on this workstation.</summary>
    public const string DefaultConfigPath = "appsettings.json";

    /// <summary>
    /// Loads settings from <paramref name="configPath"/> (or <c>ZYXEL_CONFIG</c>, or
    /// <see cref="DefaultConfigPath"/>), then applies <c>ZYXEL_*</c> environment overrides.
    /// Missing files are ignored rather than fatal; a present-but-invalid file throws.
    /// </summary>
    public static ZyxelSettings Load(string? configPath = null)
    {
        var settings = new ZyxelSettings();

        string? path = configPath
            ?? Environment.GetEnvironmentVariable("ZYXEL_CONFIG")
            ?? (File.Exists(DefaultConfigPath) ? DefaultConfigPath : null);

        if (path is not null)
        {
            if (!File.Exists(path))
                throw new ZyxelRootException($"config file not found: {path}");

            // File.ReadAllText strips a UTF-8 BOM (appsettings.json is written with one).
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("Zyxel", out var z) &&
                z.ValueKind == JsonValueKind.Object)
            {
                if (z.TryGetProperty("Host", out var v) && v.ValueKind == JsonValueKind.String)
                    settings.Host = v.GetString()!;
                if (z.TryGetProperty("Port", out v) && v.ValueKind == JsonValueKind.Number)
                    settings.SshPort = v.GetInt32();
                if (z.TryGetProperty("Username", out v) && v.ValueKind == JsonValueKind.String)
                    settings.Username = v.GetString()!;
                if (z.TryGetProperty("Password", out v) && v.ValueKind == JsonValueKind.String)
                    settings.Password = v.GetString()!;
                if (z.TryGetProperty("Exploit", out v) && v.ValueKind == JsonValueKind.String)
                    settings.Exploit = v.GetString()!;
                if (z.TryGetProperty("ExploitConfirm", out v) && v.ValueKind == JsonValueKind.String)
                    settings.ExploitConfirm = v.GetString()!;
                if (z.TryGetProperty("DalOid", out v) && v.ValueKind == JsonValueKind.String)
                    settings.DalOid = v.GetString()!;
                if (z.TryGetProperty("DalCommandField", out v) && v.ValueKind == JsonValueKind.String)
                    settings.DalCommandField = v.GetString()!;
                if (z.TryGetProperty("DalCommandWrap", out v) && v.ValueKind == JsonValueKind.String)
                    settings.DalCommandWrap = v.GetString()!;
                if (z.TryGetProperty("DalPayload", out v) && v.ValueKind == JsonValueKind.Object)
                {
                    var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    foreach (JsonProperty member in v.EnumerateObject())
                        payload[member.Name] = member.Value.Clone();
                    settings.DalPayload = payload;
                }
                if (z.TryGetProperty("RootUsername", out v) && v.ValueKind == JsonValueKind.String)
                    settings.RootUsername = v.GetString()!;
                if (z.TryGetProperty("RootPassword", out v) && v.ValueKind == JsonValueKind.String)
                    settings.RootPassword = v.GetString()!;
            }
            settings.ConfigPath = path;
        }

        settings.ApplyEnvironment();
        return settings;
    }

    /// <summary>Applies <c>ZYXEL_HOST/PORT/USER/PASS/EXPLOIT/ROOT_PORT</c> overrides.</summary>
    public ZyxelSettings ApplyEnvironment()
    {
        var host = Environment.GetEnvironmentVariable("ZYXEL_HOST");
        if (!string.IsNullOrEmpty(host)) Host = host;

        var port = Environment.GetEnvironmentVariable("ZYXEL_PORT");
        if (!string.IsNullOrEmpty(port) && int.TryParse(port, out var p)) SshPort = p;

        var user = Environment.GetEnvironmentVariable("ZYXEL_USER");
        if (!string.IsNullOrEmpty(user)) Username = user;

        var pass = Environment.GetEnvironmentVariable("ZYXEL_PASS");
        if (!string.IsNullOrEmpty(pass)) Password = pass;

        // The Python harness runs unicode_escape over this value; PowerShell/users
        // typically pass a literal "\n" in the variable, so decode the common escapes.
        var exploit = Environment.GetEnvironmentVariable("ZYXEL_EXPLOIT");
        if (!string.IsNullOrEmpty(exploit)) Exploit = UnescapeExploit(exploit);

        var rootPort = Environment.GetEnvironmentVariable("ZYXEL_ROOT_PORT");
        if (!string.IsNullOrEmpty(rootPort) && int.TryParse(rootPort, out var rp)) RootPort = rp;

        var rootUser = Environment.GetEnvironmentVariable("ZYXEL_ROOT_USER");
        if (!string.IsNullOrEmpty(rootUser)) RootUsername = rootUser;

        var rootPass = Environment.GetEnvironmentVariable("ZYXEL_ROOT_PASS");
        if (!string.IsNullOrEmpty(rootPass)) RootPassword = rootPass;

        return this;
    }

    /// <summary>Decodes the backslash escapes accepted in <c>ZYXEL_EXPLOIT</c>.</summary>
    public static string UnescapeExploit(string value) =>
        value.Replace("\\r", "\r").Replace("\\n", "\n").Replace("\\t", "\t");

    /// <summary>Returns a validation error, or <c>null</c> when the settings are usable.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Host)) return "Host is empty";
        if (string.IsNullOrWhiteSpace(Username)) return "Username is empty";
        if (string.IsNullOrEmpty(Password)) return "Password is empty";
        if (string.IsNullOrEmpty(Exploit)) return "Exploit is empty";
        if (SshPort is <= 0 or > 65535) return "SshPort out of range";
        if (RootPort is <= 0 or > 65535) return "RootPort out of range";
        return null;
    }
}
