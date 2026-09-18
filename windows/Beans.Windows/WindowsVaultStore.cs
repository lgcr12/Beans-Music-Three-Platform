using System.Security.Cryptography;
using System.Text.Json;

namespace Beans.Windows;

internal sealed class WindowsVaultStore
{
    private readonly string _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeansMusic");

    public void Save<T>(string name, T value)
    {
        Directory.CreateDirectory(_directory);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(value, Beans.Core.JsonOptions.Default);
        var protectedBytes = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
        CryptographicOperations.ZeroMemory(plaintext);
        File.WriteAllBytes(Path.Combine(_directory, name + ".vault"), protectedBytes);
    }

    public T? Load<T>(string name)
    {
        var path = Path.Combine(_directory, name + ".vault");
        if (!File.Exists(path)) return default;
        var plaintext = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<T>(plaintext, Beans.Core.JsonOptions.Default); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public void Remove(string name)
    {
        var path = Path.Combine(_directory, name + ".vault");
        if (File.Exists(path)) File.Delete(path);
    }
}
