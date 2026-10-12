using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace MaxClient;
internal static class SessionStore
{
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MaxNativeClient");
    private static readonly string FilePath = Path.Combine(Folder, "session.dat");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MaxNativeClient.Session.v1");
    public static Credentials? Load()
    {
        if (!File.Exists(FilePath)) return null;
        var clear = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
        try
        {
            var result = JsonSerializer.Deserialize<Credentials>(clear);
            if (result == null || !Guid.TryParse(result.DeviceId, out _) || string.IsNullOrWhiteSpace(result.Token))
                throw new InvalidDataException("Сохранённая сессия повреждена.");
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
    public static void Save(Credentials credentials)
    {
        Directory.CreateDirectory(Folder);
        var clear = JsonSerializer.SerializeToUtf8Bytes(credentials);
        try
        {
            var encrypted = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
            var temporary = FilePath + ".tmp";
            File.WriteAllBytes(temporary, encrypted);
            File.Move(temporary, FilePath, true);
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
    public static void Delete() { if (File.Exists(FilePath)) File.Delete(FilePath); }
}
