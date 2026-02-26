using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace hideChat2
{
    /// <summary>
    /// Connects to peer's .onion address through SOCKS5 with encryption
    /// </summary>
    public class PeerConnector
    {
        private readonly int _socksPort;
        private readonly Action<string> _onMessageReceived;
        private NetworkStream _stream;
        private NetworkProtocol _protocol;

        public bool IsConnected => _stream != null;

        public event EventHandler Connected;
        public event EventHandler Disconnected;
        public event EventHandler TypingIndicatorReceived;
        public event EventHandler ReadReceiptReceived;

        public PeerConnector(int socksPort, Action<string> onMessageReceived)
        {
            _socksPort = socksPort;
            _onMessageReceived = onMessageReceived;
        }

        public async Task ConnectAsync(string onionAddress, int remotePort, CancellationToken ct = default)
        {
            // Connect through SOCKS5
            _stream = await Socks5Client.ConnectAsync(
                "127.0.0.1", _socksPort,
                onionAddress, remotePort,
                ct);

            _protocol = new NetworkProtocol(_stream);

            // Key exchange
            await PerformKeyExchangeAsync(ct);

            Connected?.Invoke(this, EventArgs.Empty);

            // Start receive loop (capture current stream/protocol to avoid field mutation races)
            _ = Task.Run(() => ReceiveLoopAsync(_stream, _protocol, ct), ct);
        }

        /// <summary>
        /// Perform ECDH key exchange
        /// Protocol: Both sides send public key, then derive shared secret
        /// </summary>
        private async Task PerformKeyExchangeAsync(CancellationToken ct)
        {
            // Send our public key
            await _protocol.SendKeyExchangeAsync(ct);

            // Receive peer's public key
            var (type, _) = await _protocol.ReceiveFrameAsync(ct);
            if (type != NetworkProtocol.MessageType.KeyExchange)
                throw new InvalidOperationException("Expected key exchange message");

            // Encryption now initialized
        }

        private async Task ReceiveLoopAsync(NetworkStream stream, NetworkProtocol protocol, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var (type, message) = await protocol.ReceiveFrameAsync(ct);

                    switch (type)
                    {
                        case NetworkProtocol.MessageType.EncryptedMessage:
                            if (message != null)
                            {
                                _onMessageReceived(message);
                                // Auto-send read receipt
                                await protocol.SendReadReceiptAsync(ct);
                            }
                            break;

                        case NetworkProtocol.MessageType.TypingIndicator:
                            TypingIndicatorReceived?.Invoke(this, EventArgs.Empty);
                            break;

                        case NetworkProtocol.MessageType.ReadReceipt:
                            ReadReceiptReceived?.Invoke(this, EventArgs.Empty);
                            break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PeerConnector Receive Error] {ex.Message}");
            }
            finally
            {
                // Only fire disconnect event if this loop owns the current active stream
                if (object.ReferenceEquals(_stream, stream))
                {
                    Disconnected?.Invoke(this, EventArgs.Empty);
                    _stream = null;
                }
            }
        }

        public async Task SendAsync(string message, CancellationToken ct = default)
        {
            if (_protocol == null)
                throw new InvalidOperationException("Not connected");

            await _protocol.SendMessageAsync(message, ct);
        }

        public async Task SendTypingIndicatorAsync(CancellationToken ct = default)
        {
            if (_protocol == null)
                throw new InvalidOperationException("Not connected");

            await _protocol.SendTypingIndicatorAsync(ct);
        }

        public void Disconnect()
        {
            _protocol?.Dispose();
            _stream?.Close();
            _stream = null;
            _protocol = null;
        }
    }
}
