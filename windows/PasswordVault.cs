using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TalkPasswordFill;

internal sealed class PasswordVault
{
    private readonly string path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TalkPasswordFill", "preview.dpapi");

    internal bool Exists => File.Exists(path);

    internal void Save(string password)
    {
        var plaintext = Encoding.Unicode.GetBytes(password);
        string staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] protectedData = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(staging, protectedData);
            File.Move(staging, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (File.Exists(staging)) File.Delete(staging);
        }
    }

    internal char[] Read()
    {
        byte[] plaintext = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try
        {
            var characters = new char[Encoding.Unicode.GetCharCount(plaintext) + 1];
            Encoding.Unicode.GetChars(plaintext, characters);
            if (characters.Length < 2 || characters.AsSpan(0, characters.Length - 1).Contains('\0'))
            {
                Array.Clear(characters);
                throw new CryptographicException("Invalid stored credential.");
            }
            return characters;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal void Delete() => File.Delete(path);
}
