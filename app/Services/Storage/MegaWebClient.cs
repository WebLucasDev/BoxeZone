using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CG.Web.MegaApiClient;

namespace BoxeZone.Services.Storage
{
    /// <summary>
    /// MegaApiClient's HTTP client, plus a correct answer to MEGA's hashcash challenge (a small
    /// proof of work MEGA asks for before some logins). The library's own solver (1.10.5) reads
    /// the wrong bytes of the digest, so MEGA rejects every answer and the login never ends
    /// (gpailler/MegaApiClient#253). The challenge is answered here and never reaches the library.
    /// </summary>
    internal sealed partial class MegaWebClient(int responseTimeoutMs) : IWebClient
    {
        // A right answer is accepted at once; a few tries cover a challenge that changed meanwhile.
        private const int MaxHashcashTries = 3;

        private readonly WebClient _inner = new(responseTimeoutMs);

        public int BufferSize
        {
            get => _inner.BufferSize;
            set => _inner.BufferSize = value;
        }

        public string PostRequestJson(Uri url, string jsonData) => PostRequestJson(url, jsonData, null!);

        public string PostRequestJson(Uri url, string jsonData, string hashcash)
        {
            string result = _inner.PostRequestJson(url, jsonData, hashcash);
            for (int i = 0; i < MaxHashcashTries; i++)
            {
                // The library's client turns "402 + X-Hashcash" into [-27, 'challenge'].
                if (HashcashResult().Match(result) is not { Success: true } match)
                {
                    return result;
                }

                result = _inner.PostRequestJson(url, jsonData, Solve(match.Groups[1].Value));
            }

            throw new InvalidDataException("MEGA did not accept the hashcash answer.");
        }

        public string PostRequestRaw(Uri url, Stream dataStream) => _inner.PostRequestRaw(url, dataStream);

        public Stream PostRequestRawAsStream(Uri url, Stream dataStream) => _inner.PostRequestRawAsStream(url, dataStream);

        public Stream GetRequestRaw(Uri url) => _inner.GetRequestRaw(url);

        /// <summary>
        /// "1:easiness:time:token" -> "1:token:prefix". As in hashcash.cpp of MEGA's SDK: the 48-byte
        /// token repeated 262144 times after a 4-byte prefix; the first 4 bytes of its SHA-256, read
        /// big-endian, must not pass the threshold.
        /// </summary>
        private static string Solve(string challenge)
        {
            string[] parts = challenge.Split(':');
            if (parts.Length < 4 || parts[0] != "1")
            {
                throw new InvalidDataException("Unknown MEGA hashcash challenge.");
            }

            int easiness = int.Parse(parts[1]);
            uint threshold = (uint)((((easiness & 63) << 1) + 1) << ((easiness >> 6) * 7 + 3));
            byte[] token = FromBase64Url(parts[3]);

            const int Repeats = 262144;
            byte[] buffer = new byte[4 + Repeats * token.Length];
            for (int i = 0; i < Repeats; i++)
            {
                token.CopyTo(buffer, 4 + i * token.Length);
            }

            Span<byte> hash = stackalloc byte[32];
            while (true)
            {
                SHA256.HashData(buffer, hash);
                if (BinaryPrimitives.ReadUInt32BigEndian(hash) <= threshold)
                {
                    return $"1:{parts[3]}:{ToBase64Url(buffer.AsSpan(0, 4))}";
                }

                // Next prefix: the first 4 bytes as a little-endian counter.
                BinaryPrimitives.WriteUInt32LittleEndian(buffer, BinaryPrimitives.ReadUInt32LittleEndian(buffer) + 1);
            }
        }

        private static byte[] FromBase64Url(string value)
        {
            string base64 = value.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '='));
        }

        private static string ToBase64Url(ReadOnlySpan<byte> bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        [GeneratedRegex(@"^\s*\[\s*-27\s*,\s*'([^']+)'\s*\]\s*$")]
        private static partial Regex HashcashResult();
    }
}
