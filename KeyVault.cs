using System.Security.Cryptography;
using System.Text.Json;
using Windows.Security.Credentials;

namespace ASD;

/// <summary>One stored key. Everything except <see cref="Secret"/> is plain metadata (so cards can be listed and searched without unlocking).</summary>
internal sealed class KeyEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Service { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public string Notes { get; set; } = "";
    public bool Favorite { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Modified { get; set; } = DateTime.Now;
    public DateTime? Expires { get; set; }

    /// <summary>base64(nonce | tag | ciphertext): AES-256-GCM with the vault key, AAD = entry id.</summary>
    public string Secret { get; set; } = "";

    public KeyEntry Clone()
    {
        var c = (KeyEntry)MemberwiseClone();
        c.Tags = new List<string>(Tags);
        return c;
    }
}

/// <summary>
/// Encrypted store for API keys (%LocalAppData%\ASD\keys.vault).
///
///  • A random 256-bit vault key (DEK) encrypts every secret with AES-256-GCM.
///  • The DEK is stored wrapped twice: once with a key derived from the master password
///    (PBKDF2-SHA256, 600 000 iterations) and optionally once with a key derived from a
///    Windows Hello signature. Either one unlocks the vault; changing the password never
///    re-encrypts the secrets.
///  • The DEK lives in memory only while unlocked, and is zeroed on lock / idle timeout.
/// </summary>
internal static class KeyVault
{
    private const int Iterations = 600_000;
    private const string HelloName = "ASD.KeysStore";

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ASD", "keys.vault");

    private static readonly byte[] AadPassword = Encoding.UTF8.GetBytes("ASD.keys.dek.password");
    private static readonly byte[] AadHello = Encoding.UTF8.GetBytes("ASD.keys.dek.hello");
    private static readonly byte[] HelloChallenge = SHA256.HashData(Encoding.UTF8.GetBytes("ASD.keys.hello.challenge.v1"));
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static readonly object Gate = new();
    private static VaultFile? _file;
    private static byte[]? _dek;
    private static DateTime _lastUse;
    private static Timer? _timer;

    /// <summary>Raised (on a background thread) after the vault locks, manually or by the idle timer.</summary>
    public static event Action? Locked;

    internal sealed class VaultFile
    {
        public int Version { get; set; } = 1;
        public string Salt { get; set; } = "";
        public int Iterations { get; set; } = KeyVault.Iterations;
        public string PasswordWrap { get; set; } = "";
        public string? HelloWrap { get; set; }
        public List<KeyEntry> Entries { get; set; } = new();
    }

    // ───────── state ─────────
    public static bool Exists => File.Exists(FilePath);

    public static bool IsUnlocked { get { lock (Gate) return _dek != null; } }

    public static bool HelloEnabled { get { lock (Gate) return Exists && Load().HelloWrap != null; } }

    public static int AutoLockMinutes
        => int.TryParse(SettingsStore.Get("keys.autolock", "5"), out var m) && m > 0 ? m : 5;

    public static void Touch() { lock (Gate) _lastUse = DateTime.UtcNow; }

    // ───────── create / unlock / lock ─────────
    public static void Create(string password)
    {
        var dek = RandomNumberGenerator.GetBytes(32);
        var salt = RandomNumberGenerator.GetBytes(16);
        var kek = Derive(password, salt, Iterations);
        try
        {
            var file = new VaultFile
            {
                Salt = Convert.ToBase64String(salt),
                Iterations = Iterations,
                PasswordWrap = Seal(kek, dek, AadPassword),
            };
            lock (Gate)
            {
                if (File.Exists(FilePath)) throw new InvalidOperationException("A vault already exists.");
                _file = file;
                Save();
                SetDek(dek);
            }
        }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    /// <summary>Blocking (PBKDF2): call from a background thread.</summary>
    public static bool UnlockWithPassword(string password)
    {
        VaultFile file;
        lock (Gate) file = Load();
        var kek = Derive(password, Convert.FromBase64String(file.Salt), file.Iterations);
        try
        {
            var dek = Open(kek, file.PasswordWrap, AadPassword);
            lock (Gate) SetDek(dek);
            return true;
        }
        catch (CryptographicException)
        {
            Thread.Sleep(600);   // slows down guessing
            return false;
        }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    public static void Lock()
    {
        bool was;
        lock (Gate)
        {
            was = _dek != null;
            if (_dek != null) CryptographicOperations.ZeroMemory(_dek);
            _dek = null;
            _timer?.Dispose();
            _timer = null;
        }
        if (was) Locked?.Invoke();
    }

    /// <summary>Verifies <paramref name="current"/> and re-wraps the vault key under <paramref name="next"/>. Blocking.</summary>
    public static bool ChangePassword(string current, string next)
    {
        VaultFile file;
        byte[] dek;
        lock (Gate) { file = Load(); dek = (byte[])RequireDek().Clone(); }
        try
        {
            var oldKek = Derive(current, Convert.FromBase64String(file.Salt), file.Iterations);
            try { Open(oldKek, file.PasswordWrap, AadPassword); }
            catch (CryptographicException) { Thread.Sleep(600); return false; }
            finally { CryptographicOperations.ZeroMemory(oldKek); }

            var salt = RandomNumberGenerator.GetBytes(16);
            var newKek = Derive(next, salt, Iterations);
            try
            {
                lock (Gate)
                {
                    file.Salt = Convert.ToBase64String(salt);
                    file.Iterations = Iterations;
                    file.PasswordWrap = Seal(newKek, dek, AadPassword);
                    Save();
                }
            }
            finally { CryptographicOperations.ZeroMemory(newKek); }
            return true;
        }
        finally { CryptographicOperations.ZeroMemory(dek); }
    }

    // ───────── entries ─────────
    public static List<KeyEntry> All()
    {
        lock (Gate) return Exists ? Load().Entries.Select(e => e.Clone()).ToList() : new List<KeyEntry>();
    }

    public static KeyEntry? Find(string id)
    {
        lock (Gate) return Exists ? Load().Entries.FirstOrDefault(e => e.Id == id)?.Clone() : null;
    }

    /// <summary>Adds or updates an entry. <paramref name="newSecret"/> (needs the vault unlocked) replaces the stored value; null keeps it.</summary>
    public static void Upsert(KeyEntry entry, string? newSecret)
    {
        lock (Gate)
        {
            var file = Load();
            var existing = file.Entries.FirstOrDefault(e => e.Id == entry.Id);
            string secret = existing?.Secret ?? "";
            if (!string.IsNullOrEmpty(newSecret))
                secret = Seal(RequireDek(), Encoding.UTF8.GetBytes(newSecret), SecretAad(entry.Id));
            if (secret.Length == 0) throw new InvalidOperationException("A key needs a value.");

            var stored = entry.Clone();
            stored.Secret = secret;
            stored.Modified = DateTime.Now;
            if (existing == null) file.Entries.Add(stored);
            else file.Entries[file.Entries.IndexOf(existing)] = stored;
            Save();
            Touch();
        }
    }

    public static void Delete(string id)
    {
        lock (Gate)
        {
            var file = Load();
            file.Entries.RemoveAll(e => e.Id == id);
            Save();
        }
    }

    public static void SetFavorite(string id, bool on)
    {
        lock (Gate)
        {
            var e = Load().Entries.FirstOrDefault(x => x.Id == id);
            if (e == null) return;
            e.Favorite = on;
            Save();
        }
    }

    public static string Reveal(string id)
    {
        lock (Gate)
        {
            var dek = RequireDek();
            var e = Load().Entries.FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException("That key no longer exists.");
            Touch();
            return Encoding.UTF8.GetString(Open(dek, e.Secret, SecretAad(id)));
        }
    }

    public static string? TryReveal(string id)
    {
        try { return Reveal(id); } catch { return null; }
    }

    // ───────── Windows Hello ─────────
    public static async Task<bool> IsHelloSupportedAsync()
    {
        try { return await KeyCredentialManager.IsSupportedAsync(); }
        catch { return false; }
    }

    /// <summary>Needs the vault unlocked. Creates a Hello key and stores the vault key wrapped by it.</summary>
    public static async Task EnableHelloAsync()
    {
        byte[] dek;
        lock (Gate) dek = (byte[])RequireDek().Clone();   // own copy: an idle lock during the prompt must not zero it under us
        try
        {
            var created = await KeyCredentialManager.RequestCreateAsync(HelloName, KeyCredentialCreationOption.ReplaceExisting);
            if (created.Status != KeyCredentialStatus.Success)
                throw new InvalidOperationException($"Windows Hello could not be set up ({created.Status}).");

            var kek = await HelloKekAsync(created.Credential)
                      ?? throw new InvalidOperationException("Windows Hello did not confirm. Nothing was changed.");
            try
            {
                lock (Gate)
                {
                    Load().HelloWrap = Seal(kek, dek, AadHello);
                    Save();
                }
            }
            finally { CryptographicOperations.ZeroMemory(kek); }
        }
        finally { CryptographicOperations.ZeroMemory(dek); }
    }

    public static async Task<bool> UnlockWithHelloAsync()
    {
        try
        {
            string? wrap;
            lock (Gate) wrap = Load().HelloWrap;
            if (wrap == null) return false;

            var opened = await KeyCredentialManager.OpenAsync(HelloName);
            if (opened.Status != KeyCredentialStatus.Success) return false;

            var kek = await HelloKekAsync(opened.Credential);
            if (kek == null) return false;
            try
            {
                var dek = Open(kek, wrap, AadHello);
                lock (Gate) SetDek(dek);
                return true;
            }
            finally { CryptographicOperations.ZeroMemory(kek); }
        }
        catch { return false; }   // cancelled, no TPM/PIN, key removed, wrong user …
    }

    public static async Task DisableHelloAsync()
    {
        lock (Gate)
        {
            Load().HelloWrap = null;
            Save();
        }
        try { await KeyCredentialManager.DeleteAsync(HelloName); } catch { /* already gone */ }
    }

    /// <summary>RSA PKCS#1 v1.5 signatures are deterministic: signing a fixed challenge always yields the same bytes, so a hash of them is a stable wrapping key that only this Windows Hello key can reproduce.</summary>
    private static async Task<byte[]?> HelloKekAsync(KeyCredential credential)
    {
        var signed = await credential.RequestSignAsync(Windows.Security.Cryptography.CryptographicBuffer.CreateFromByteArray(HelloChallenge));
        if (signed.Status != KeyCredentialStatus.Success) return null;
        Windows.Security.Cryptography.CryptographicBuffer.CopyToByteArray(signed.Result, out var signature);
        return SHA256.HashData(signature);
    }

    // ───────── internals ─────────
    private static VaultFile Load()
    {
        if (_file != null) return _file;
        if (!File.Exists(FilePath)) throw new InvalidOperationException("The vault has not been created yet.");
        _file = JsonSerializer.Deserialize<VaultFile>(File.ReadAllText(FilePath), Json)
                ?? throw new InvalidDataException("The vault file is empty or damaged.");
        return _file;
    }

    private static void Save()
    {
        var file = _file ?? throw new InvalidOperationException("No vault loaded.");
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, Json));
        if (File.Exists(FilePath)) File.Replace(tmp, FilePath, FilePath + ".bak");
        else File.Move(tmp, FilePath);
    }

    private static byte[] RequireDek() => _dek ?? throw new InvalidOperationException("The vault is locked.");

    private static void SetDek(byte[] dek)
    {
        if (_dek != null) CryptographicOperations.ZeroMemory(_dek);
        _dek = dek;
        _lastUse = DateTime.UtcNow;
        _timer?.Dispose();
        _timer = new Timer(_ => Tick(), null, 10_000, 10_000);
    }

    private static void Tick()
    {
        bool expired;
        lock (Gate) expired = _dek != null && DateTime.UtcNow - _lastUse > TimeSpan.FromMinutes(AutoLockMinutes);
        if (expired) Lock();
    }

    private static byte[] SecretAad(string id) => Encoding.UTF8.GetBytes("ASD.keys.secret:" + id);

    private static byte[] Derive(string password, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);

    private static string Seal(byte[] key, byte[] plain, byte[] aad)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(key, 16)) gcm.Encrypt(nonce, plain, cipher, tag, aad);
        var blob = new byte[12 + 16 + cipher.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, 12);
        Buffer.BlockCopy(tag, 0, blob, 12, 16);
        Buffer.BlockCopy(cipher, 0, blob, 28, cipher.Length);
        return Convert.ToBase64String(blob);
    }

    private static byte[] Open(byte[] key, string base64, byte[] aad)
    {
        var blob = Convert.FromBase64String(base64);
        if (blob.Length < 28) throw new CryptographicException("Damaged data.");
        var nonce = blob.AsSpan(0, 12);
        var tag = blob.AsSpan(12, 16);
        var cipher = blob.AsSpan(28);
        var plain = new byte[cipher.Length];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(nonce, cipher, tag, plain, aad);   // throws CryptographicException on a wrong key / tampering
        return plain;
    }
}
