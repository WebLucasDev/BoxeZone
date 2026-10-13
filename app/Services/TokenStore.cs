using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;
using Windows.Storage.Streams;

namespace BoxeZone.Services
{
    /// <summary>
    /// Provider tokens, encrypted for the current Windows user (DPAPI through
    /// DataProtectionProvider), one file per key in %LocalAppData%\BoxeZone\tokens.
    /// Values are never written in plain text and never logged.
    /// </summary>
    public sealed class TokenStore
    {
        private const string ProtectionDescriptor = "LOCAL=user";

        private readonly string _folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BoxeZone", "tokens");

        public async Task<string?> ReadAsync(string key)
        {
            string path = GetPath(key);
            if (!File.Exists(path))
            {
                return null;
            }

            byte[] encrypted = await File.ReadAllBytesAsync(path);

            // Unprotect takes the descriptor from the data itself.
            IBuffer decrypted = await new DataProtectionProvider().UnprotectAsync(encrypted.AsBuffer());
            return CryptographicBuffer.ConvertBinaryToString(BinaryStringEncoding.Utf8, decrypted);
        }

        public async Task WriteAsync(string key, string value)
        {
            IBuffer plain = CryptographicBuffer.ConvertStringToBinary(value, BinaryStringEncoding.Utf8);
            IBuffer encrypted = await new DataProtectionProvider(ProtectionDescriptor).ProtectAsync(plain);

            // Write aside, then swap: the PC going off mid-write leaves the old value, not a broken one.
            Directory.CreateDirectory(_folder);
            string path = GetPath(key);
            string temporary = path + ".tmp";
            await File.WriteAllBytesAsync(temporary, encrypted.ToArray());
            File.Move(temporary, path, overwrite: true);
        }

        public void Delete(string key)
        {
            string path = GetPath(key);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        public void DeleteAll(string keyPrefix)
        {
            if (!Directory.Exists(_folder))
            {
                return;
            }

            foreach (string path in Directory.EnumerateFiles(_folder, ToFileName(keyPrefix) + "*"))
            {
                File.Delete(path);
            }
        }

        private string GetPath(string key) => Path.Combine(_folder, ToFileName(key) + ".bin");

        private static string ToFileName(string key)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            return new string(key.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }
    }
}
