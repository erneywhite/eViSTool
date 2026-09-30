using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace eViSTool.Core.Server.Remote;

/// <summary>
/// Удалённое управление серверным профилем: агент принимает подключения из сети на своём порту, по TLS и со своим ключом
/// (не тем, что у окна на этой же машине: сменить удалённый ключ — значит отключить всех, кто подключён снаружи, не трогая окно).
/// Файл agents/&lt;профиль&gt;.remote.json; сертификат — agents/&lt;профиль&gt;.remote.pfx.
/// </summary>
public sealed record RemoteSettings
{
    public bool Enabled { get; init; }

    /// <summary>Порт выбирается случайно один раз при первом включении и дальше не меняется — чтобы код подключения оставался прежним.</summary>
    public int Port { get; init; }

    /// <summary>Ключ удалённого подключения.</summary>
    public string Key { get; init; } = "";

    /// <summary>Адрес, который попадает в код подключения (IP в локальной сети, внешний IP или домен). Пусто — первый адрес машины.</summary>
    public string? Host { get; init; }
}

public static class RemoteAccess
{
    public const int MinPort = 20000;
    public const int MaxPort = 60000;

    public static string FileFor(string profileId, string? agentsDir = null) =>
        Path.Combine(agentsDir ?? AgentProtocol.DefaultAgentsDir, $"{profileId}.remote.json");

    public static string CertFile(string profileId, string? agentsDir = null) =>
        Path.Combine(agentsDir ?? AgentProtocol.DefaultAgentsDir, $"{profileId}.remote.pfx");

    public static RemoteSettings Load(string profileId, string? agentsDir = null)
    {
        var path = FileFor(profileId, agentsDir);
        try
        {
            return File.Exists(path) ? JsonConvert.DeserializeObject<RemoteSettings>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new();
        }
    }

    public static void Save(string profileId, RemoteSettings settings, string? agentsDir = null)
    {
        var path = FileFor(profileId, agentsDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonConvert.SerializeObject(settings, Formatting.Indented));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Включить: при первом включении — случайный свободный порт, ключ и сертификат; дальше они те же.</summary>
    public static RemoteSettings Enable(string profileId, string? agentsDir = null)
    {
        var s = Load(profileId, agentsDir);
        s = s with
        {
            Enabled = true,
            Port = s.Port is >= MinPort and <= MaxPort ? s.Port : FreePort(),
            Key = s.Key.Length >= 32 ? s.Key : NewKey(),
        };
        EnsureCertificate(profileId, agentsDir).Dispose();
        Save(profileId, s, agentsDir);
        return s;
    }

    public static RemoteSettings Disable(string profileId, string? agentsDir = null)
    {
        var s = Load(profileId, agentsDir) with { Enabled = false };
        Save(profileId, s, agentsDir);
        return s;
    }

    /// <summary>Новый ключ: прежний код подключения перестаёт работать.</summary>
    public static RemoteSettings RegenerateKey(string profileId, string? agentsDir = null)
    {
        var s = Load(profileId, agentsDir) with { Key = NewKey() };
        Save(profileId, s, agentsDir);
        return s;
    }

    public static string NewKey() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>Случайный свободный порт в диапазоне: у каждой установки свой, общего для всех шаблона нет.</summary>
    public static int FreePort()
    {
        for (var i = 0; i < 50; i++)
        {
            var port = RandomNumberGenerator.GetInt32(MinPort, MaxPort + 1);
            try
            {
                // проба — только на локальном адресе: слушать все адреса ради проверки значило бы вызвать окно брандмауэра
                var probe = new TcpListener(IPAddress.Loopback, port);
                probe.Start();
                probe.Stop();
                return port;
            }
            catch (SocketException)
            {
                // занят — следующий
            }
        }
        throw new InvalidOperationException("no free port");
    }

    /// <summary>Сертификат агента: создаётся один раз (самоподписанный), дальше читается из файла.</summary>
    public static X509Certificate2 EnsureCertificate(string profileId, string? agentsDir = null)
    {
        var path = CertFile(profileId, agentsDir);
        if (File.Exists(path))
        {
            try { return X509CertificateLoader.LoadPkcs12FromFile(path, null); }
            catch (CryptographicException) { /* испорчен — создаём заново */ }
        }

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=eViSTool agent", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // проверка подлинности сервера
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, created.Export(X509ContentType.Pfx));
        return X509CertificateLoader.LoadPkcs12FromFile(path, null);
    }

    /// <summary>Отпечаток сертификата (SHA-256, hex) — по нему клиент узнаёт «свой» агент.</summary>
    public static string Fingerprint(X509Certificate2 cert) => Convert.ToHexStringLower(SHA256.HashData(cert.RawData));

    /// <summary>IPv4-адреса машины в сетях, которые сейчас работают: сначала локальные (192.168…, 10…, 172.16–31…).</summary>
    public static IReadOnlyList<string> LocalAddresses()
    {
        var result = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            result.AddRange(nic.GetIPProperties().UnicastAddresses
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254.")));
        }
        return [.. result.Distinct().OrderBy(a => IsPrivate(a) ? 0 : 1).Select(a => a.ToString())];
    }

    private static bool IsPrivate(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    internal static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}

/// <summary>
/// Код подключения: всё, что нужно клиенту, одной строкой — адрес, порт, ключ и отпечаток сертификата агента.
/// Это секрет (в нём ключ): показывать его стоит только по нажатию.
/// </summary>
public sealed record ConnectionCode(string Host, int Port, string Key, string Fingerprint)
{
    public const string Prefix = "evis1.";

    public override string ToString()
    {
        var json = new JObject { ["h"] = Host, ["p"] = Port, ["k"] = Key, ["f"] = Fingerprint }.ToString(Formatting.None);
        return Prefix + RemoteAccess.Base64Url(Encoding.UTF8.GetBytes(json));
    }

    public static bool TryParse(string? text, out ConnectionCode code)
    {
        code = null!;
        text = text?.Trim();
        if (text is null || !text.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        try
        {
            var o = JObject.Parse(Encoding.UTF8.GetString(RemoteAccess.FromBase64Url(text[Prefix.Length..])));
            var host = o.Value<string>("h");
            var port = o.Value<int?>("p");
            var key = o.Value<string>("k");
            var fingerprint = o.Value<string>("f");
            if (string.IsNullOrWhiteSpace(host) || port is not (> 0 and < 65536) || string.IsNullOrEmpty(key) || fingerprint is not { Length: 64 })
                return false;
            code = new ConnectionCode(host.Trim(), port.Value, key, fingerprint.ToLowerInvariant());
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return false;
        }
    }
}
