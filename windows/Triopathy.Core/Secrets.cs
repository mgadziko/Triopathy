using System.Security.Cryptography;
using System.Text;

namespace Triopathy.Core;

public interface ISecretStore
{
    string? Read(string name);
    void Write(string name, string value);
    void Delete(string name);
}

public sealed class WindowsSecretStore(string? directory = null) : ISecretStore
{
    private string PathFor(string name)
    {
        if (name is not ("api-key" or "chatgpt-plan")) throw new ArgumentException("Unknown credential name.");
        return Path.Combine(directory ?? AppSettings.DataDirectory, name + ".protected");
    }
    public string? Read(string name)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var path = PathFor(name); if (!File.Exists(path)) return null;
        var clear = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(clear); } finally { CryptographicOperations.ZeroMemory(clear); }
    }
    public void Write(string name, string value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var path = PathFor(name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var clear = Encoding.UTF8.GetBytes(value);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temp, ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser)); File.Move(temp, path, true); }
        finally { CryptographicOperations.ZeroMemory(clear); if (File.Exists(temp)) File.Delete(temp); }
    }
    public void Delete(string name) { var path = PathFor(name); if (File.Exists(path)) File.Delete(path); }
}
