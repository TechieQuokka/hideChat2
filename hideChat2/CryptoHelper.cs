using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace hideChat2
{
    /// <summary>
    /// ECDH + AES-256-CBC + HMAC-SHA256 encryption
    /// Perfect Forward Secrecy through ephemeral key exchange
    /// </summary>
    public class CryptoHelper : IDisposable
    {
        private ECDiffieHellmanCng _ecdh;
        private byte[] _sharedSecret;
        private byte[] _aesKey;
        private byte[] _hmacKey;

        public byte[] PublicKey { get; private set; }
        public bool IsInitialized => _sharedSecret != null;

        /// <summary>
        /// Generate ECDH key pair
        /// </summary>
        public CryptoHelper()
        {
            _ecdh = new ECDiffieHellmanCng(256); // P-256 curve
            _ecdh.KeyDerivationFunction = ECDiffieHellmanKeyDerivationFunction.Hash;
            _ecdh.HashAlgorithm = CngAlgorithm.Sha256;

            // Export public key
            PublicKey = _ecdh.PublicKey.ToByteArray();
        }

        /// <summary>
        /// Derive shared secret from peer's public key
        /// </summary>
        public void DeriveSharedSecret(byte[] peerPublicKey)
        {
            // Import peer's public key
            var peerEcdh = ECDiffieHellmanCngPublicKey.FromByteArray(peerPublicKey, CngKeyBlobFormat.EccPublicBlob);

            // Derive shared secret (32 bytes)
            _sharedSecret = _ecdh.DeriveKeyMaterial(peerEcdh);

            // Split shared secret: first 32 bytes for AES, next 32 for HMAC
            using (var sha512 = SHA512.Create())
            {
                var extendedKey = sha512.ComputeHash(_sharedSecret);
                _aesKey = new byte[32];  // AES-256 key
                _hmacKey = new byte[32]; // HMAC key

                Array.Copy(extendedKey, 0, _aesKey, 0, 32);
                Array.Copy(extendedKey, 32, _hmacKey, 0, 32);
            }
        }

        /// <summary>
        /// Encrypt message with AES-256-CBC + HMAC-SHA256 (Encrypt-then-MAC)
        /// Format: [IV(16)][Ciphertext][HMAC(32)]
        /// </summary>
        public byte[] Encrypt(string plaintext)
        {
            if (!IsInitialized)
                throw new InvalidOperationException("Shared secret not initialized. Call DeriveSharedSecret first.");

            byte[] plaintextBytes = Encoding.UTF8.GetBytes(plaintext);

            using (var aes = Aes.Create())
            {
                aes.Key = _aesKey;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.GenerateIV();

                byte[] iv = aes.IV;
                byte[] ciphertext;

                // Encrypt
                using (var encryptor = aes.CreateEncryptor())
                using (var ms = new MemoryStream())
                using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                {
                    cs.Write(plaintextBytes, 0, plaintextBytes.Length);
                    cs.FlushFinalBlock();
                    ciphertext = ms.ToArray();
                }

                // Compute HMAC over IV + Ciphertext
                byte[] dataToMac = new byte[iv.Length + ciphertext.Length];
                Array.Copy(iv, 0, dataToMac, 0, iv.Length);
                Array.Copy(ciphertext, 0, dataToMac, iv.Length, ciphertext.Length);

                byte[] hmac;
                using (var hmacSha256 = new HMACSHA256(_hmacKey))
                {
                    hmac = hmacSha256.ComputeHash(dataToMac);
                }

                // Result: IV + Ciphertext + HMAC
                byte[] result = new byte[iv.Length + ciphertext.Length + hmac.Length];
                Array.Copy(iv, 0, result, 0, iv.Length);
                Array.Copy(ciphertext, 0, result, iv.Length, ciphertext.Length);
                Array.Copy(hmac, 0, result, iv.Length + ciphertext.Length, hmac.Length);

                return result;
            }
        }

        /// <summary>
        /// Decrypt message with AES-256-CBC + HMAC-SHA256 verification
        /// </summary>
        public string Decrypt(byte[] encryptedData)
        {
            if (!IsInitialized)
                throw new InvalidOperationException("Shared secret not initialized. Call DeriveSharedSecret first.");

            if (encryptedData.Length < 16 + 32) // Minimum: IV(16) + HMAC(32)
                throw new ArgumentException("Invalid encrypted data length");

            // Extract components
            int ivLength = 16;
            int hmacLength = 32;
            int ciphertextLength = encryptedData.Length - ivLength - hmacLength;

            byte[] iv = new byte[ivLength];
            byte[] ciphertext = new byte[ciphertextLength];
            byte[] receivedHmac = new byte[hmacLength];

            Array.Copy(encryptedData, 0, iv, 0, ivLength);
            Array.Copy(encryptedData, ivLength, ciphertext, 0, ciphertextLength);
            Array.Copy(encryptedData, ivLength + ciphertextLength, receivedHmac, 0, hmacLength);

            // Verify HMAC
            byte[] dataToMac = new byte[iv.Length + ciphertext.Length];
            Array.Copy(iv, 0, dataToMac, 0, iv.Length);
            Array.Copy(ciphertext, 0, dataToMac, iv.Length, ciphertext.Length);

            byte[] computedHmac;
            using (var hmacSha256 = new HMACSHA256(_hmacKey))
            {
                computedHmac = hmacSha256.ComputeHash(dataToMac);
            }

            // Constant-time HMAC comparison
            if (!ConstantTimeEquals(receivedHmac, computedHmac))
                throw new CryptographicException("HMAC verification failed - message may be tampered");

            // Decrypt
            using (var aes = Aes.Create())
            {
                aes.Key = _aesKey;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using (var decryptor = aes.CreateDecryptor())
                using (var ms = new MemoryStream(ciphertext))
                using (var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read))
                using (var reader = new StreamReader(cs, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        /// <summary>
        /// Constant-time byte array comparison (prevent timing attacks)
        /// </summary>
        private bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
                return false;

            int diff = 0;
            for (int i = 0; i < a.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }
            return diff == 0;
        }

        public void Dispose()
        {
            _ecdh?.Dispose();

            // Clear sensitive data
            if (_sharedSecret != null)
                Array.Clear(_sharedSecret, 0, _sharedSecret.Length);
            if (_aesKey != null)
                Array.Clear(_aesKey, 0, _aesKey.Length);
            if (_hmacKey != null)
                Array.Clear(_hmacKey, 0, _hmacKey.Length);
        }
    }
}
