using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WXDataStudio.App.Services;

public sealed class LegacySc1PageDecryptService
{
    private const int PageSize = 1024;
    private const int ReserveSize = 16;
    private const int KdfIterations = 4000;

    public bool MatchesPassword(string databasePath, string password)
    {
        if (string.IsNullOrEmpty(password) || !File.Exists(databasePath)) return false;
        using var stream = File.OpenRead(databasePath);
        if (stream.Length < PageSize) return false;
        var page = new byte[PageSize];
        if (stream.Read(page, 0, page.Length) != page.Length) return false;
        var key = DeriveKey(password, page.AsSpan(0, 16));
        return LooksLikeSqliteFirstPage(DecryptPage(page, key, true));
    }

    public bool MatchesRawKey(string databasePath, string rawKeyHex)
    {
        if (!TryParseRawKey(rawKeyHex, out var key) || !File.Exists(databasePath)) return false;
        using var stream = File.OpenRead(databasePath);
        if (stream.Length < PageSize) return false;
        var page = new byte[PageSize];
        if (stream.Read(page, 0, page.Length) != page.Length) return false;
        return LooksLikeSqliteFirstPage(DecryptPage(page, key, true));
    }

    public Task<string> DecryptWithPasswordAsync(string databasePath, string password, string outputPath)
    {
        var first = ReadFirstPage(databasePath);
        var key = DeriveKey(password, first.AsSpan(0, 16));
        if (!LooksLikeSqliteFirstPage(DecryptPage(first, key, true)))
            throw new InvalidDataException("Password does not match the legacy WeChat SQLCipher1 profile.");
        return DecryptAsync(databasePath, key, outputPath);
    }

    public Task<string> DecryptWithRawKeyAsync(string databasePath, string rawKeyHex, string outputPath)
    {
        if (!TryParseRawKey(rawKeyHex, out var key))
            throw new ArgumentException("Raw AES key must contain exactly 64 hexadecimal characters.", nameof(rawKeyHex));
        var first = ReadFirstPage(databasePath);
        if (!LooksLikeSqliteFirstPage(DecryptPage(first, key, true)))
            throw new InvalidDataException("Raw AES key does not match the legacy WeChat SQLCipher1 profile.");
        return DecryptAsync(databasePath, key, outputPath);
    }

    private static async Task<string> DecryptAsync(string databasePath, byte[] key, string outputPath)
    {
        var input = await File.ReadAllBytesAsync(databasePath);
        if (input.Length < PageSize || input.Length % PageSize != 0)
            throw new InvalidDataException("Encrypted database size is not aligned to the expected 1024-byte page size.");

        var output = new byte[input.Length];
        var header = Encoding.ASCII.GetBytes("SQLite format 3\0");
        var pageCount = input.Length / PageSize;

        for (var index = 0; index < pageCount; index++)
        {
            var offset = index * PageSize;
            var page = input.AsSpan(offset, PageSize);
            var plain = DecryptPage(page, key, index == 0)
                ?? throw new InvalidDataException($"Unable to decrypt database page {index + 1}.");

            if (index == 0)
            {
                header.CopyTo(output.AsSpan(offset, header.Length));
                plain.CopyTo(output.AsSpan(offset + 16, plain.Length));
            }
            else
            {
                plain.CopyTo(output.AsSpan(offset, plain.Length));
            }
        }

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(outputPath, output);
        return outputPath;
    }

    private static byte[] ReadFirstPage(string databasePath)
    {
        using var stream = File.OpenRead(databasePath);
        if (stream.Length < PageSize)
            throw new InvalidDataException("Encrypted database is smaller than one 1024-byte page.");
        var page = new byte[PageSize];
        stream.ReadExactly(page);
        return page;
    }

    private static byte[] DeriveKey(string password, ReadOnlySpan<byte> salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            KdfIterations,
            HashAlgorithmName.SHA1,
            32);

    private static byte[]? DecryptPage(ReadOnlySpan<byte> page, byte[] key, bool firstPage)
    {
        var cipherStart = firstPage ? 16 : 0;
        var cipherLength = PageSize - ReserveSize - cipherStart;
        if (cipherLength <= 0 || cipherLength % 16 != 0) return null;

        var iv = page.Slice(PageSize - ReserveSize, ReserveSize).ToArray();
        var cipher = page.Slice(cipherStart, cipherLength).ToArray();
        try
        {
            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.BlockSize = 128;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            aes.Key = key;
            aes.IV = iv;
            using var decryptor = aes.CreateDecryptor();
            return decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static bool LooksLikeSqliteFirstPage(byte[]? decrypted)
    {
        if (decrypted is null || decrypted.Length < 8) return false;
        return decrypted[5] == 64 && decrypted[6] == 32 && decrypted[7] == 32;
    }

    private static bool TryParseRawKey(string value, out byte[] key)
    {
        var hex = new string((value ?? "").Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 64)
        {
            key = Array.Empty<byte>();
            return false;
        }

        try
        {
            key = Convert.FromHexString(hex);
            return key.Length == 32;
        }
        catch (FormatException)
        {
            key = Array.Empty<byte>();
            return false;
        }
    }
}
