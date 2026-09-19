using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WXDataStudio.App.Services;

public sealed record Sc1DecryptResult(
    string OutputPath,
    int PageCount,
    int WalFrameCount,
    bool WalApplied,
    string? WalWarning);

/// <summary>
/// Reads and writes the legacy WeChat SQLCipher1 page format used by
/// <c>EnMicroMsg.db</c> (1024-byte pages, 16-byte reserve, PBKDF2-HMAC-SHA1
/// 4000 iterations, no HMAC, per-page CBC IV stored in the page reserve).
///
/// The page decryptor alone was not enough: WeChat keeps the newest messages in
/// the write-ahead log, so a snapshot that ignores <c>-wal</c> shows less than
/// WeChat does. <see cref="DecryptWithPasswordAsync"/> folds the WAL frames into
/// the page image, and the same page layout is used in reverse by the restore
/// pipeline.
/// </summary>
public sealed class LegacySc1DatabaseService
{
    public const int PageSize = 1024;
    public const int ReserveSize = 16;
    public const int KdfIterations = 4000;

    private static readonly byte[] SqliteMagic = "SQLite format 3\0"u8.ToArray();

    public bool MatchesPassword(string databasePath, string password)
    {
        if (string.IsNullOrEmpty(password) || !File.Exists(databasePath)) return false;
        var page = ReadFirstPage(databasePath);
        if (page is null) return false;
        var key = DeriveKey(password, page.AsSpan(0, 16));
        return LooksLikeSqliteFirstPage(DecryptPage(page, key, firstPage: true));
    }

    public bool MatchesRawKey(string databasePath, string rawKeyHex)
    {
        if (!TryParseRawKey(rawKeyHex, out var key) || !File.Exists(databasePath)) return false;
        var page = ReadFirstPage(databasePath);
        if (page is null) return false;
        return LooksLikeSqliteFirstPage(DecryptPage(page, key, firstPage: true));
    }

    public Task<Sc1DecryptResult> DecryptWithPasswordAsync(
        string databasePath, string password, string outputPath, bool foldWal = true)
    {
        var first = ReadFirstPage(databasePath)
            ?? throw new InvalidDataException("Encrypted database is smaller than one page.");
        var key = DeriveKey(password, first.AsSpan(0, 16));
        if (!LooksLikeSqliteFirstPage(DecryptPage(first, key, firstPage: true)))
            throw new InvalidDataException("Password does not match the legacy WeChat SQLCipher1 profile.");
        return DecryptCoreAsync(databasePath, key, outputPath, foldWal);
    }

    public Task<Sc1DecryptResult> DecryptWithRawKeyAsync(
        string databasePath, string rawKeyHex, string outputPath, bool foldWal = true)
    {
        if (!TryParseRawKey(rawKeyHex, out var key))
            throw new ArgumentException(
                "Raw AES key must contain exactly 64 hexadecimal characters.", nameof(rawKeyHex));
        var first = ReadFirstPage(databasePath)
            ?? throw new InvalidDataException("Encrypted database is smaller than one page.");
        if (!LooksLikeSqliteFirstPage(DecryptPage(first, key, firstPage: true)))
            throw new InvalidDataException("Raw AES key does not match the legacy WeChat SQLCipher1 profile.");
        return DecryptCoreAsync(databasePath, key, outputPath, foldWal);
    }

    /// <summary>
    /// Re-encrypts a plaintext page image with the key of
    /// <paramref name="sourceEncryptedPath"/>. The salt of the source database is
    /// reused so the phone keeps deriving the same key from the same passphrase.
    /// </summary>
    public async Task<string> EncryptWithPasswordAsync(
        string plaintextPath, string sourceEncryptedPath, string password, string outputPath)
    {
        var source = ReadFirstPage(sourceEncryptedPath)
            ?? throw new InvalidDataException("Source encrypted database is smaller than one page.");
        var key = DeriveKey(password, source.AsSpan(0, 16));
        return await EncryptCoreAsync(plaintextPath, source.AsSpan(0, 16).ToArray(), key, outputPath);
    }

    public async Task<string> EncryptWithRawKeyAsync(
        string plaintextPath, string sourceEncryptedPath, string rawKeyHex, string outputPath)
    {
        if (!TryParseRawKey(rawKeyHex, out var key))
            throw new ArgumentException(
                "Raw AES key must contain exactly 64 hexadecimal characters.", nameof(rawKeyHex));
        var source = ReadFirstPage(sourceEncryptedPath)
            ?? throw new InvalidDataException("Source encrypted database is smaller than one page.");
        return await EncryptCoreAsync(plaintextPath, source.AsSpan(0, 16).ToArray(), key, outputPath);
    }

    /// <summary>Decrypts one page image and folds any committed WAL frames on top of it.</summary>
    private static async Task<Sc1DecryptResult> DecryptCoreAsync(
        string databasePath, byte[] key, string outputPath, bool foldWal)
    {
        var length = new FileInfo(databasePath).Length;
        if (length < PageSize || length % PageSize != 0)
            throw new InvalidDataException(
                "Encrypted database size is not aligned to the expected 1024-byte page size.");

        var pageCount = (int)(length / PageSize);
        var pages = new Dictionary<int, byte[]>();
        await using (var input = new FileStream(
            databasePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: PageSize * 16, FileOptions.SequentialScan))
        {
            var encryptedPage = new byte[PageSize];
            for (var index = 0; index < pageCount; index++)
            {
                await input.ReadExactlyAsync(encryptedPage);
                pages[index + 1] = DecryptPage(encryptedPage, key, index == 0)
                    ?? throw new InvalidDataException($"Unable to decrypt database page {index + 1}.");
            }
        }

        var mainOnlyPages = new Dictionary<int, byte[]>(pages);
        var expectedPages = pageCount;
        var walFrames = 0;
        var walApplied = false;
        string? walWarning = null;
        if (foldWal)
        {
            var walPath = databasePath + "-wal";
            if (File.Exists(walPath) && new FileInfo(walPath).Length > 0)
            {
                var wal = await File.ReadAllBytesAsync(walPath);
                walFrames = Math.Max(0, (wal.Length - 32) / (24 + PageSize));
                try
                {
                    expectedPages = ApplyWal(pages, wal, expectedPages, key);
                    walApplied = true;
                }
                catch (Exception ex)
                {
                    // A write-ahead log we cannot fold must not hide the main
                    // database: fall back to the main page image and report it.
                    walWarning = ex.Message;
                    expectedPages = pageCount;
                }
            }
        }

        var path = await WritePagesAsync(CollectPages(pages, expectedPages), outputPath);
        DeleteStaleWalFiles(outputPath);

        if (walApplied)
        {
            // SQLite itself decides whether the folded image is usable. A page
            // image it rejects is replaced by the main database alone.
            var problem = await IntegrityProblemAsync(outputPath);
            if (problem is not null)
            {
                path = await WritePagesAsync(CollectPages(mainOnlyPages, pageCount), outputPath);
                DeleteStaleWalFiles(outputPath);
                walApplied = false;
                expectedPages = pageCount;
                walWarning = "Write-ahead log fold failed SQLite integrity check: " + problem;
            }
        }

        return new Sc1DecryptResult(path, expectedPages, walFrames, walApplied, walWarning);
    }

    private static List<byte[]> CollectPages(Dictionary<int, byte[]> pages, int pageCount)
    {
        var output = new List<byte[]>(pageCount);
        for (var page = 1; page <= pageCount; page++)
        {
            if (!pages.TryGetValue(page, out var content))
                throw new InvalidDataException(
                    $"Page {page} is missing after applying the write-ahead log.");
            output.Add(content);
        }
        return output;
    }

    /// <summary>Runs SQLite's own integrity check; returns null when the image is healthy.</summary>
    private static async Task<string?> IntegrityProblemAsync(string path)
    {
        try
        {
            var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                Pooling = false
            };
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(builder.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            var result = Convert.ToString(await command.ExecuteScalarAsync()) ?? "";
            return result.Equals("ok", StringComparison.OrdinalIgnoreCase) ? null : result;
        }
        catch (Exception ex)
        {
            return ex.GetType().Name + ": " + ex.Message;
        }
    }

    private static void DeleteStaleWalFiles(string databasePath)
    {
        // The folded page image is self-contained, so any side file left from an
        // earlier read must go: SQLite would otherwise replay it over the fold.
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var path = databasePath + suffix;
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // A locked side file only means an older handle is still open.
            }
        }
    }

    /// <summary>
    /// Overlays committed WAL frames on decrypted pages and returns the resulting
    /// database size in pages. Frame checksums are verified with SQLite's own
    /// algorithm, so frames from an older WAL generation or an uncommitted
    /// transaction are not applied.
    /// </summary>
    private static int ApplyWal(
        Dictionary<int, byte[]> pages, byte[] wal, int mainPageCount, byte[] key)
    {
        if (wal.Length < 32)
            throw new InvalidDataException("Write-ahead log header is incomplete.");
        var magic = BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(0, 4));
        var bigEndian = magic switch
        {
            0x377f0682 => true,
            0x377f0683 => false,
            _ => throw new InvalidDataException("Unrecognised write-ahead log magic.")
        };
        var pageSize = (int)BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(8, 4));
        if (pageSize != PageSize)
            throw new InvalidDataException(
                $"Write-ahead log page size {pageSize} does not match {PageSize}.");

        // WeChat's WAL keeps the standard magic, page size and frame layout but
        // does not chain checksums the way SQLite does, so the salt in the header
        // is used to discard frames left over from an earlier WAL generation.
        var salt1 = BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(16, 4));
        var salt2 = BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(20, 4));

        var frameSize = 24 + pageSize;
        var frameCount = (wal.Length - 32) / frameSize;
        var pending = new Dictionary<int, byte[]>();
        var committedSize = mainPageCount;
        var skippedGenerations = 0;
        for (var frame = 0; frame < frameCount; frame++)
        {
            var offset = 32 + frame * frameSize;
            var pageNumber = (int)BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(offset, 4));
            var committedPages =
                (int)BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(offset + 4, 4));
            if (pageNumber <= 0) continue;

            var frameSalt1 = BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(offset + 8, 4));
            var frameSalt2 = BinaryPrimitives.ReadUInt32BigEndian(wal.AsSpan(offset + 12, 4));
            if (frameSalt1 != salt1 || frameSalt2 != salt2)
            {
                skippedGenerations++;
                continue;
            }

            var framePage = wal.AsSpan(offset + 24, pageSize).ToArray();
            var plain = DecryptPage(framePage, key, firstPage: pageNumber == 1);
            if (plain is null) continue;
            pending[pageNumber] = plain;
            if (committedPages != 0) committedSize = committedPages;
        }

        if (skippedGenerations == frameCount)
            throw new InvalidDataException("Write-ahead log contains no frames for the current generation.");

        foreach (var (pageNumber, content) in pending)
            pages[pageNumber] = content;

        // A committed frame may describe a database larger than the main file.
        return Math.Max(mainPageCount, committedSize);
    }

    /// <summary>SQLite's cumulative WAL checksum over whole 32-bit words.</summary>
    private static (uint S1, uint S2) Checksum(
        ReadOnlySpan<byte> data, bool bigEndian, uint s1, uint s2)
    {
        for (var offset = 0; offset + 4 <= data.Length; offset += 4)
        {
            var word = bigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4))
                : BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
            s1 += word + s2;
            s2 += s1;
        }
        return (s1, s2);
    }

    private static async Task<string> EncryptCoreAsync(
        string plaintextPath, byte[] salt, byte[] key, string outputPath)
    {
        var length = new FileInfo(plaintextPath).Length;
        if (length < PageSize || length % PageSize != 0)
            throw new InvalidDataException("Plaintext database is not aligned to 1024-byte pages.");
        if (salt.Length != 16)
            throw new InvalidDataException("Source salt must be 16 bytes.");

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var tempPath = outputPath + ".tmp";
        if (File.Exists(tempPath)) File.Delete(tempPath);

        try
        {
            await using (var input = new FileStream(
                plaintextPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: PageSize * 16, FileOptions.SequentialScan))
            await using (var output = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: PageSize * 16, FileOptions.SequentialScan))
            {
                var page = new byte[PageSize];
                var pageNumber = 1;
                while (true)
                {
                    var filled = await ReadPageAsync(input, page);
                    if (filled == 0) break;
                    if (filled != PageSize)
                        throw new InvalidDataException(
                            "Plaintext database ends with a partial page.");
                    var encrypted = EncryptPage(page, key, salt, pageNumber == 1);
                    await output.WriteAsync(encrypted);
                    pageNumber++;
                }
                output.Flush(true);
            }
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }

        File.Move(tempPath, outputPath, overwrite: true);
        return outputPath;
    }

    private static byte[] EncryptPage(byte[] plainPage, byte[] key, byte[] salt, bool firstPage)
    {
        var cipherStart = firstPage ? 16 : 0;
        var cipherLength = PageSize - ReserveSize - cipherStart;
        if (cipherLength % 16 != 0)
            throw new InvalidDataException("Page payload is not block aligned.");

        var output = new byte[PageSize];
        if (firstPage) Buffer.BlockCopy(salt, 0, output, 0, 16);
        var iv = RandomNumberGenerator.GetBytes(ReserveSize);
        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.BlockSize = 128;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        aes.IV = iv;
        using var encryptor = aes.CreateEncryptor();
        encryptor.TransformBlock(plainPage, cipherStart, cipherLength, output, cipherStart);
        Buffer.BlockCopy(iv, 0, output, PageSize - ReserveSize, ReserveSize);
        return output;
    }

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
            var plain = new byte[PageSize];
            if (firstPage) Buffer.BlockCopy(SqliteMagic, 0, plain, 0, SqliteMagic.Length);
            decryptor.TransformBlock(cipher, 0, cipher.Length, plain, cipherStart);
            return plain;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static async Task<string> WritePagesAsync(List<byte[]> pages, string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var tempPath = outputPath + ".tmp";
        if (File.Exists(tempPath)) File.Delete(tempPath);
        try
        {
            await using (var output = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: PageSize * 16, FileOptions.SequentialScan))
            {
                foreach (var page in pages) await output.WriteAsync(page);
                output.Flush(true);
            }
        }
        catch
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }

        File.Move(tempPath, outputPath, overwrite: true);
        return outputPath;
    }

    private static async Task<int> ReadPageAsync(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total));
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private static byte[]? ReadFirstPage(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        if (stream.Length < PageSize) return null;
        var page = new byte[PageSize];
        stream.ReadExactly(page);
        return page;
    }

    public static byte[] DeriveKey(string password, ReadOnlySpan<byte> salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, KdfIterations, HashAlgorithmName.SHA1, 32);

    public static bool TryParseRawKey(string value, out byte[] key)
    {
        var hex = new string((value ?? "").Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 64)
        {
            key = Array.Empty<byte>();
            return false;
        }

        key = Convert.FromHexString(hex);
        return key.Length == 32;
    }

    /// <summary>
    /// Validates an assembled first page: the SQLite magic sits at 0..16 and the
    /// decrypted payload starts at 16, so the header fields (page size,
    /// max/min payload fractions) are read with that 16-byte offset.
    /// </summary>
    private static bool LooksLikeSqliteFirstPage(byte[]? page)
    {
        if (page is null || page.Length < PageSize) return false;
        if (!page.AsSpan(0, 16).SequenceEqual(SqliteMagic)) return false;
        return page[21] == 64 && page[22] == 32 && page[23] == 32;
    }

    /// <summary>
    /// Compares two page images while ignoring the 16-byte reserve of every page.
    /// That reserve holds the per-page IV in the encrypted file and is unused in a
    /// plaintext image, so it is the only region a round trip may normalise.
    /// </summary>
    public static async Task<bool> PayloadsMatchAsync(
        string leftPath, string rightPath, CancellationToken cancellationToken = default)
    {
        var left = new FileInfo(leftPath);
        var right = new FileInfo(rightPath);
        if (left.Length != right.Length || left.Length % PageSize != 0) return false;

        await using var leftStream = File.Open(leftPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var rightStream = File.Open(rightPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var leftPage = new byte[PageSize];
        var rightPage = new byte[PageSize];
        var pages = (int)(left.Length / PageSize);
        for (var page = 0; page < pages; page++)
        {
            await leftStream.ReadExactlyAsync(leftPage, cancellationToken);
            await rightStream.ReadExactlyAsync(rightPage, cancellationToken);
            var start = page == 0 ? 16 : 0;
            if (!leftPage.AsSpan(start, PageSize - ReserveSize - start)
                    .SequenceEqual(rightPage.AsSpan(start, PageSize - ReserveSize - start)))
                return false;
        }
        return true;
    }
}
