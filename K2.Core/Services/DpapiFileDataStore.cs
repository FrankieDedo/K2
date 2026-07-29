using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Google.Apis.Util.Store;

namespace K2.Core.Services;

/// <summary>
/// Drop-in replacement for <c>Google.Apis.Util.Store.FileDataStore</c> (the default
/// <c>GoogleWebAuthorizationBroker.AuthorizeAsync</c> token cache), which writes the OAuth
/// access/refresh token as plain JSON under <see cref="YouTubeStore.TokenCacheDir"/>. This
/// version DPAPI-encrypts (<see cref="DataProtectionScope.CurrentUser"/>) the serialized value
/// before it touches disk. Uses <c>System.Text.Json</c> rather than Google's own
/// <c>NewtonsoftJsonSerializer</c> — fine here because K2 both writes and reads every value
/// through this same store, so no external tool needs to parse the on-disk shape.
/// </summary>
public sealed class DpapiFileDataStore : IDataStore
{
    private readonly string _folder;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("K2.Core.DpapiFileDataStore.v1");

    public DpapiFileDataStore(string folder)
    {
        _folder = folder;
        Directory.CreateDirectory(_folder);
    }

    public Task StoreAsync<T>(string key, T value)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(value);
        byte[] cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(PathFor(key, typeof(T)), cipher);
        return Task.CompletedTask;
    }

    public Task DeleteAsync<T>(string key)
    {
        string path = PathFor(key, typeof(T));
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<T> GetAsync<T>(string key)
    {
        string path = PathFor(key, typeof(T));
        if (!File.Exists(path)) return Task.FromResult<T>(default!);

        byte[] cipher = File.ReadAllBytes(path);
        byte[] plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
        string json = Encoding.UTF8.GetString(plain);
        return Task.FromResult(System.Text.Json.JsonSerializer.Deserialize<T>(json)!);
    }

    public Task ClearAsync()
    {
        if (Directory.Exists(_folder))
            foreach (var file in Directory.GetFiles(_folder)) File.Delete(file);
        return Task.CompletedTask;
    }

    /// <summary>Same key-to-filename scheme as Google's own <c>FileDataStore.GenerateStoredKey</c>
    /// (type full name + key), sanitized for the filesystem.</summary>
    private string PathFor(string key, Type type)
    {
        string stored = $"{type.FullName}-{key}";
        foreach (char c in Path.GetInvalidFileNameChars())
            stored = stored.Replace(c, '_');
        return Path.Combine(_folder, stored);
    }
}
