using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace hideChat2
{
    /// <summary>
    /// Listens for incoming connections on Hidden Service port with encryption
    /// Peer connects to my .onion address
    /// </summary>
    public class PeerListener
    {
        private readonly int _listenPort;
        private readonly Action<string> _onMessageReceived;
        private TcpListener _listener;
        private NetworkStream _peerStream;
        private NetworkProtocol _protocol;
        private bool _running;

        public bool IsConnected => _peerStream != null;

        public event EventHandler PeerConnecting;  // TCP accepted, key exchange in progress
        public event EventHandler PeerConnected;
        public event EventHandler PeerDisconnected;
        public event EventHandler TypingIndicatorReceived;
        public event EventHandler ReadReceiptReceived;

        public PeerListener(int listenPort, Action<string> onMessageReceived)
        {
            _listenPort = listenPort;
            _onMessageReceived = onMessageReceived;
        }

        public void Start(CancellationToken ct)
        {
            _running = true;
            _listener = new TcpListener(IPAddress.Loopback, _listenPort);
            _listener.Start();

            _ = Task.Run(() => AcceptLoopAsync(ct), ct);
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (_running && !ct.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync();

                    // Notify UI that someone is connecting (before key exchange)
                    PeerConnecting?.Invoke(this, EventArgs.Empty);

                    // Close previous connection before replacing (new peer takes over)
                    var oldProtocol = _protocol;
                    var oldStream = _peerStream;
                    oldProtocol?.Dispose();
                    oldStream?.Close();

                    _peerStream = client.GetStream();
                    _protocol = new NetworkProtocol(_peerStream);

                    // Handshake (key exchange + mutual ack)
                    await PerformHandshakeAsync(ct);

                    PeerConnected?.Invoke(this, EventArgs.Empty);
                    _ = Task.Run(() => ReceiveLoopAsync(_peerStream, _protocol, ct), ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    if (_running)
                        System.Diagnostics.Debug.WriteLine($"[PeerListener Error] {ex.Message}");

                    // Clean up failed connection
                    _protocol?.Dispose();
                    _peerStream?.Close();
                    _peerStream = null;
                    _protocol = null;
                }
            }
        }

        /// <summary>
        /// Staggered 4-step handshake (listener initiates).
        /// T1: Send own key  →  T4: Receive connector key
        /// T5: Send Ack      →  T8: Receive connector Ack → both confirmed
        /// Wrapped in a 30-second timeout so a dead connection never blocks the accept loop.
        /// </summary>
        private async Task PerformHandshakeAsync(CancellationToken ct)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(60));
                var token = cts.Token;

                // T1: Send our public key first (listener always initiates)
                await _protocol.SendKeyExchangeAsync(token);

                // T4: Receive connector's public key → DeriveSharedSecret called inside
                var (type1, _) = await _protocol.ReceiveFrameAsync(token);
                if (type1 != NetworkProtocol.MessageType.KeyExchange)
                    throw new InvalidOperationException("Expected KeyExchange frame");

                // T5: Confirm we received their key
                await _protocol.SendConnectionAckAsync(token);

                // T8: Wait for connector's confirmation → both sides are mutually verified
                var (type2, _) = await _protocol.ReceiveFrameAsync(token);
                if (type2 != NetworkProtocol.MessageType.ConnectionAck)
                    throw new InvalidOperationException("Expected ConnectionAck frame");
            }
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
                System.Diagnostics.Debug.WriteLine($"[PeerListener Receive Error] {ex.Message}");
            }
            finally
            {
                // Only fire disconnect event if this loop owns the current active stream
                if (object.ReferenceEquals(_peerStream, stream))
                {
                    PeerDisconnected?.Invoke(this, EventArgs.Empty);
                    _peerStream = null;
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

        public void Stop()
        {
            _running = false;
            _listener?.Stop();
            _protocol?.Dispose();
            _peerStream?.Close();
            _peerStream = null;
            _protocol = null;
        }
    }
}
