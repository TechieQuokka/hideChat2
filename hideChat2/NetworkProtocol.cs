using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace hideChat2
{
    /// <summary>
    /// Network protocol for encrypted P2P communication
    /// Frame format: [Type(1)][Length(4)][Data(variable)]
    /// </summary>
    public class NetworkProtocol : IDisposable
    {
        private readonly NetworkStream _stream;
        private readonly CryptoHelper _crypto;

        public enum MessageType : byte
        {
            KeyExchange = 0x01,      // Public key exchange
            EncryptedMessage = 0x02, // Encrypted chat message
            TypingIndicator = 0x03,  // Typing indicator (future)
            ReadReceipt = 0x04       // Read receipt (future)
        }

        public NetworkProtocol(NetworkStream stream)
        {
            _stream = stream;
            _crypto = new CryptoHelper();
        }

        /// <summary>
        /// Send key exchange message
        /// </summary>
        public async Task SendKeyExchangeAsync(CancellationToken ct = default)
        {
            await SendFrameAsync(MessageType.KeyExchange, _crypto.PublicKey, ct);
        }

        /// <summary>
        /// Send encrypted message
        /// </summary>
        public async Task SendMessageAsync(string message, CancellationToken ct = default)
        {
            if (!_crypto.IsInitialized)
                throw new InvalidOperationException("Encryption not initialized. Exchange keys first.");

            byte[] encryptedData = _crypto.Encrypt(message);
            await SendFrameAsync(MessageType.EncryptedMessage, encryptedData, ct);
        }

        /// <summary>
        /// Send typing indicator
        /// </summary>
        public async Task SendTypingIndicatorAsync(CancellationToken ct = default)
        {
            await SendFrameAsync(MessageType.TypingIndicator, new byte[0], ct);
        }

        /// <summary>
        /// Send read receipt
        /// </summary>
        public async Task SendReadReceiptAsync(CancellationToken ct = default)
        {
            await SendFrameAsync(MessageType.ReadReceipt, new byte[0], ct);
        }

        /// <summary>
        /// Receive and process next frame
        /// Returns: (MessageType, DecryptedMessage or null)
        /// </summary>
        public async Task<(MessageType type, string message)> ReceiveFrameAsync(CancellationToken ct = default)
        {
            // Read frame header: Type(1) + Length(4)
            byte[] header = new byte[5];
            await ReadExactAsync(header, ct);

            MessageType type = (MessageType)header[0];
            int length = BitConverter.ToInt32(header, 1);

            if (length < 0 || length > 10 * 1024 * 1024) // Max 10MB
                throw new InvalidDataException($"Invalid frame length: {length}");

            // Read frame data
            byte[] data = new byte[length];
            if (length > 0)
                await ReadExactAsync(data, ct);

            // Process based on type
            switch (type)
            {
                case MessageType.KeyExchange:
                    _crypto.DeriveSharedSecret(data);
                    return (type, null);

                case MessageType.EncryptedMessage:
                    string decrypted = _crypto.Decrypt(data);
                    return (type, decrypted);

                case MessageType.TypingIndicator:
                case MessageType.ReadReceipt:
                    // Future implementation
                    return (type, null);

                default:
                    throw new InvalidDataException($"Unknown message type: {type}");
            }
        }

        /// <summary>
        /// Send frame: [Type(1)][Length(4)][Data]
        /// </summary>
        private async Task SendFrameAsync(MessageType type, byte[] data, CancellationToken ct)
        {
            // Build frame
            byte[] frame = new byte[5 + data.Length];
            frame[0] = (byte)type;
            BitConverter.GetBytes(data.Length).CopyTo(frame, 1);
            data.CopyTo(frame, 5);

            // Send
            await _stream.WriteAsync(frame, 0, frame.Length, ct);
            await _stream.FlushAsync(ct);
        }

        /// <summary>
        /// Read exact number of bytes from stream
        /// </summary>
        private async Task ReadExactAsync(byte[] buffer, CancellationToken ct)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = await _stream.ReadAsync(buffer, offset, buffer.Length - offset, ct);
                if (read == 0)
                    throw new IOException("Connection closed unexpectedly");
                offset += read;
            }
        }

        public void Dispose()
        {
            _crypto?.Dispose();
        }
    }
}
