using System.Security.Cryptography;
using System.Text.Json;

namespace DroidMic.Networking;

/// <summary>Persists small secrets (identity key, pairing keys). Windows uses DPAPI.</summary>
public interface ISecretStore
{
    byte[]? Load(string name);
    void Save(string name, byte[] data);
    void Delete(string name);
}

/// <summary>
/// Unprotected file store for the headless receiver and tests. Files are created
/// with owner-only permissions on Unix. The desktop app uses a DPAPI-backed store.
/// </summary>
public sealed class FileSecretStore : ISecretStore
{
    private readonly string _dir;

    public FileSecretStore(string directory)
    {
        _dir = directory;
        Directory.CreateDirectory(directory);
    }

    public byte[]? Load(string name)
    {
        string p = PathFor(name);
        return File.Exists(p) ? File.ReadAllBytes(p) : null;
    }

    public void Save(string name, byte[] data)
    {
        string p = PathFor(name);
        string tmp = p + ".tmp";
        File.WriteAllBytes(tmp, data);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, p, overwrite: true);
    }

    public void Delete(string name)
    {
        string p = PathFor(name);
        if (File.Exists(p)) File.Delete(p);
    }

    private string PathFor(string name)
    {
        if (name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')) throw new ArgumentException("Bad secret name", nameof(name));
        return Path.Combine(_dir, name + ".bin");
    }
}

/// <summary>Long-term ECDSA P-256 identity of this PC (protocol §3).</summary>
public sealed class ServerIdentity : IDisposable
{
    private const string KeyName = "identity";
    private const string IdName = "server-id";

    private ServerIdentity(ECDsa key, string serverId)
    {
        Key = key;
        ServerId = serverId;
        PublicKeySpki = key.ExportSubjectPublicKeyInfo();
        Fingerprint = Protocol.HandshakeCrypto.Fingerprint(PublicKeySpki);
    }

    public ECDsa Key { get; }
    public string ServerId { get; }
    public byte[] PublicKeySpki { get; }
    public string Fingerprint { get; }

    public static ServerIdentity LoadOrCreate(ISecretStore store)
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[]? pkcs8 = store.Load(KeyName);
        byte[]? id = store.Load(IdName);
        if (pkcs8 is not null && id is not null)
        {
            try
            {
                ecdsa.ImportPkcs8PrivateKey(pkcs8, out _);
                return new ServerIdentity(ecdsa, System.Text.Encoding.ASCII.GetString(id));
            }
            catch (CryptographicException)
            {
                // Corrupt or foreign key: regenerate. Previously paired phones will see a
                // changed fingerprint and must re-pair, which is the safe outcome.
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pkcs8);
            }
        }
        ecdsa.Dispose();
        ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] fresh = ecdsa.ExportPkcs8PrivateKey();
        string serverId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        store.Save(KeyName, fresh);
        store.Save(IdName, System.Text.Encoding.ASCII.GetBytes(serverId));
        CryptographicOperations.ZeroMemory(fresh);
        return new ServerIdentity(ecdsa, serverId);
    }

    public void Dispose() => Key.Dispose();
}

public sealed record PairedDevice(string DeviceId, string Name, string KeyBase64, DateTimeOffset PairedAt, DateTimeOffset? LastSeen);

/// <summary>Thread-safe store of paired phones and their pairing keys.</summary>
public sealed class PairingStore
{
    private const string Name = "pairings";
    private readonly ISecretStore _store;
    private readonly object _lock = new();
    private List<PairedDevice> _devices;

    public PairingStore(ISecretStore store)
    {
        _store = store;
        _devices = LoadAll();
    }

    public event Action? Changed;

    public IReadOnlyList<PairedDevice> Devices
    {
        get { lock (_lock) return _devices.ToArray(); }
    }

    public byte[]? GetKey(string deviceId)
    {
        lock (_lock)
        {
            var d = _devices.FirstOrDefault(x => x.DeviceId == deviceId);
            return d is null ? null : Convert.FromBase64String(d.KeyBase64);
        }
    }

    public void Add(string deviceId, string name, byte[] key)
    {
        lock (_lock)
        {
            _devices.RemoveAll(d => d.DeviceId == deviceId);
            _devices.Add(new PairedDevice(deviceId, name, Convert.ToBase64String(key), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            SaveLocked();
        }
        Changed?.Invoke();
    }

    public void Touch(string deviceId, string name)
    {
        lock (_lock)
        {
            int i = _devices.FindIndex(d => d.DeviceId == deviceId);
            if (i < 0) return;
            _devices[i] = _devices[i] with { Name = name, LastSeen = DateTimeOffset.UtcNow };
            SaveLocked();
        }
        Changed?.Invoke();
    }

    public bool Remove(string deviceId)
    {
        bool removed;
        lock (_lock)
        {
            removed = _devices.RemoveAll(d => d.DeviceId == deviceId) > 0;
            if (removed) SaveLocked();
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    private List<PairedDevice> LoadAll()
    {
        byte[]? raw = _store.Load(Name);
        if (raw is null) return new();
        try { return JsonSerializer.Deserialize<List<PairedDevice>>(raw) ?? new(); }
        catch (JsonException) { return new(); }
    }

    private void SaveLocked() => _store.Save(Name, JsonSerializer.SerializeToUtf8Bytes(_devices));
}
