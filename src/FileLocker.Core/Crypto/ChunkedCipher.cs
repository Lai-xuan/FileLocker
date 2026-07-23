using System.Buffers.Binary;
using System.Security.Cryptography;

namespace FileLocker.Core.Crypto;

/// <summary>
/// 對應規格文件效能考量：AES-GCM 本身沒有原生的串流/漸進式 API（.NET 的 AesGcm 是一次性 AEAD，
/// 一定要一次拿到完整明文/密文緩衝區），要做到「不用把整個檔案讀進記憶體」，
/// 就得自己把檔案切成一塊一塊（chunk），每一塊各自獨立做 AES-GCM 加密（各自的 nonce/tag）。
///
/// 密文串流格式（重複到串流結束）：
/// ┌────────────────────────────────────────────────────┐
/// │ 區塊明文長度 (4 bytes, big-endian)                     │
/// │ Nonce (12 bytes)                                     │
/// │ 密文 (長度 = 上面那個區塊明文長度)                        │
/// │ Auth Tag (16 bytes)                                  │
/// └────────────────────────────────────────────────────┘
///
/// 每一塊都各自驗證完整性，其中一塊被竄改，解密到那一塊就會丟出 CryptographicException，
/// 不影響已經處理過的前面幾塊（但呼叫端仍然應該把已經寫出去的部分內容視為不可信、整份丟棄）。
/// </summary>
public static class ChunkedCipher
{
    public const int DefaultChunkSizeBytes = 1024 * 1024; // 1 MB
    private const int LengthPrefixBytes = 4;

    // 長度前綴理論上最大到 2GB（Int32），但正常情況下不會有人把 chunkSizeBytes 設這麼大，
    // 這裡設一個遠大於 DefaultChunkSizeBytes、但仍然合理的上限，主要是防止讀到損毀/被竄改的長度前綴時，
    // 程式嘗試配置一個荒謬大小的陣列導致記憶體暴衝。
    private const int MaxChunkLengthBytes = 64 * 1024 * 1024; // 64 MB

    public static void EncryptStream(byte[] key, Stream plaintextInput, Stream ciphertextOutput, int chunkSizeBytes = DefaultChunkSizeBytes)
    {
        var buffer = new byte[chunkSizeBytes];
        int bytesRead;

        while ((bytesRead = ReadFully(plaintextInput, buffer, 0, buffer.Length)) > 0)
        {
            var chunkPlaintext = buffer.AsSpan(0, bytesRead);
            var (nonce, ciphertext, tag) = AesGcmCipher.Encrypt(key, chunkPlaintext);

            WriteLengthPrefix(ciphertextOutput, bytesRead);
            ciphertextOutput.Write(nonce);
            ciphertextOutput.Write(ciphertext);
            ciphertextOutput.Write(tag);
        }
    }

    /// <summary>
    /// 逐塊解密並直接寫進 plaintextOutput，呼叫端永遠不會在記憶體裡同時擁有「整份」明文——
    /// 每次只處理一個 chunk 大小（依加密時的設定，預設 1MB），用完就清掉再處理下一塊。
    /// </summary>
    public static void DecryptStream(byte[] key, Stream ciphertextInput, Stream plaintextOutput)
    {
        var lengthBuffer = new byte[LengthPrefixBytes];

        while (true)
        {
            var lengthBytesRead = ReadFully(ciphertextInput, lengthBuffer, 0, LengthPrefixBytes);
            if (lengthBytesRead == 0)
            {
                break; // 正常結束：讀不到下一個長度前綴，代表整個串流已經處理完。
            }
            if (lengthBytesRead != LengthPrefixBytes)
            {
                throw new InvalidDataException("加密內容已損毀（區塊長度前綴不完整）");
            }

            var chunkLength = BinaryPrimitives.ReadInt32BigEndian(lengthBuffer);
            if (chunkLength < 0 || chunkLength > MaxChunkLengthBytes)
            {
                throw new InvalidDataException("加密內容已損毀（區塊長度異常）");
            }

            var nonce = new byte[AesGcmCipher.NonceSizeBytes];
            if (ReadFully(ciphertextInput, nonce, 0, nonce.Length) != nonce.Length)
            {
                throw new InvalidDataException("加密內容已損毀（區塊資料不完整）");
            }

            var ciphertext = new byte[chunkLength];
            if (ReadFully(ciphertextInput, ciphertext, 0, chunkLength) != chunkLength)
            {
                throw new InvalidDataException("加密內容已損毀（區塊資料不完整）");
            }

            var tag = new byte[AesGcmCipher.TagSizeBytes];
            if (ReadFully(ciphertextInput, tag, 0, tag.Length) != tag.Length)
            {
                throw new InvalidDataException("加密內容已損毀（區塊資料不完整）");
            }

            // CryptographicException（密碼錯誤或內容被竄改）直接往外拋，交給呼叫端（LockService）處理，
            // 不在這裡吞掉——呼叫端需要知道解密失敗才能清除已經寫出去的不完整輸出檔案。
            var plaintext = AesGcmCipher.Decrypt(key, nonce, ciphertext, tag);
            plaintextOutput.Write(plaintext, 0, plaintext.Length);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static void WriteLengthPrefix(Stream stream, int length)
    {
        Span<byte> lengthBuffer = stackalloc byte[LengthPrefixBytes];
        BinaryPrimitives.WriteInt32BigEndian(lengthBuffer, length);
        stream.Write(lengthBuffer);
    }

    /// <summary>
    /// Stream.Read 不保證一次就把要求的 count 全部讀滿（尤其是網路或某些包裝過的 Stream），
    /// 這裡迴圈讀到滿足 count 或遇到串流結束為止，回傳實際讀到的位元組數。
    /// </summary>
    private static int ReadFully(Stream stream, byte[] buffer, int offset, int count)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = stream.Read(buffer, offset + totalRead, count - totalRead);
            if (read == 0)
            {
                break;
            }
            totalRead += read;
        }
        return totalRead;
    }
}