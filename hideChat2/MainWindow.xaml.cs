using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace hideChat2
{
    public partial class MainWindow : Window
    {
        // Tor v3 hidden service address: 56 base32 chars + ".onion"
        private static readonly Regex _onionRegex =
            new Regex(@"^[a-z2-7]{56}\.onion$", RegexOptions.Compiled);

        private TorManager _torManager;
        private PeerListener _listener;
        private PeerConnector _connector;
        private CancellationTokenSource _cts;
        private volatile bool _isConnected = false;
        private int _currentBasePort;
        private System.Windows.Threading.DispatcherTimer _typingTimer;
        private System.Windows.Threading.DispatcherTimer _typingIndicatorHideTimer;

        public MainWindow()
        {
            InitializeComponent();
            _cts = new CancellationTokenSource();
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;

            // Initialize typing indicator timers
            _typingTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1000) // Send typing indicator 1s after last keystroke
            };
            _typingTimer.Tick += TypingTimer_Tick;

            _typingIndicatorHideTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3) // Hide typing indicator after 3s
            };
            _typingIndicatorHideTimer.Tick += TypingIndicatorHideTimer_Tick;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            WindowHelper.RemoveIcon(this);
            _currentBasePort = App.BasePort;
            BasePortTextBox.Text = _currentBasePort.ToString();
        }

        private async void StartTorButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!int.TryParse(BasePortTextBox.Text, out int basePort) ||
                    basePort < Config.MIN_PORT || basePort > Config.MAX_PORT - 2)
                {
                    MessageBox.Show($"올바른 포트를 입력하세요 ({Config.MIN_PORT}-{Config.MAX_PORT - 2})",
                        "오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Check if ports are available
                if (!Config.IsPortAvailable(basePort) ||
                    !Config.IsPortAvailable(basePort + 1) ||
                    !Config.IsPortAvailable(basePort + 2))
                {
                    var result = MessageBox.Show(
                        $"포트 {basePort}, {basePort + 1}, {basePort + 2} 중 일부가 사용 중입니다.\n\n" +
                        $"계속하시겠습니까? (충돌 가능성 있음)",
                        "포트 사용 중", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                    if (result == MessageBoxResult.No)
                        return;
                }

                _currentBasePort = basePort;

                // Hide port config, show progress
                PortConfigPanel.Visibility = Visibility.Collapsed;
                BootstrapProgressPanel.Visibility = Visibility.Visible;

                // Update port info on bootstrap screen
                BootstrapPortInfo.Text = $"포트: {basePort} (SOCKS: {basePort}, Control: {basePort + 1}, Service: {basePort + 2})";

                await StartTorAsync(_currentBasePort);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                MessageBox.Show($"오류: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void CleanStartButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!int.TryParse(BasePortTextBox.Text, out int basePort) ||
                    basePort < Config.MIN_PORT || basePort > Config.MAX_PORT - 2)
                {
                    MessageBox.Show($"올바른 포트를 입력하세요 ({Config.MIN_PORT}-{Config.MAX_PORT - 2})",
                        "오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _currentBasePort = basePort;

                // Delete tordata directory
                var tordataPath = System.IO.Path.GetFullPath($"tordata_{basePort}");
                try
                {
                    if (System.IO.Directory.Exists(tordataPath))
                    {
                        System.IO.Directory.Delete(tordataPath, true);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"tordata 폴더 삭제 실패: {ex.Message}\n계속 진행합니다.",
                        "경고", MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                // Hide port config, show progress
                PortConfigPanel.Visibility = Visibility.Collapsed;
                BootstrapProgressPanel.Visibility = Visibility.Visible;

                // Update port info on bootstrap screen
                BootstrapPortInfo.Text = $"포트: {basePort} (SOCKS: {basePort}, Control: {basePort + 1}, Service: {basePort + 2})";

                await StartTorAsync(_currentBasePort);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                MessageBox.Show($"오류: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ChangePortButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var input = Microsoft.VisualBasic.Interaction.InputBox(
                    "새 베이스 포트를 입력하세요:",
                    "포트 변경",
                    _currentBasePort.ToString());

                if (string.IsNullOrEmpty(input))
                    return;

                if (!int.TryParse(input, out int newPort) ||
                    newPort < Config.MIN_PORT || newPort > Config.MAX_PORT - 2)
                {
                    MessageBox.Show($"올바른 포트를 입력하세요 ({Config.MIN_PORT}-{Config.MAX_PORT - 2})",
                        "오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (newPort == _currentBasePort)
                    return;

                // Restart Tor with new port
                _currentBasePort = newPort;
                await RestartTorAsync();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                MessageBox.Show($"오류: {ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task RestartTorAsync()
        {
            // Show bootstrap overlay
            WaitingOverlay.Visibility = Visibility.Collapsed;
            BootstrapOverlay.Visibility = Visibility.Visible;
            PortConfigPanel.Visibility = Visibility.Collapsed;
            BootstrapProgressPanel.Visibility = Visibility.Visible;

            // Stop existing services
            _listener?.Stop();
            _connector?.Disconnect();
            _torManager?.Dispose();

            // Reset state
            _isConnected = false;
            ChatStackPanel.Children.Clear();

            // Restart Tor
            await StartTorAsync(_currentBasePort);
        }

        private async Task StartTorAsync(int basePort)
        {
            int attempt = 0;
            Exception lastError = null;

            while (attempt < Config.TOR_RETRY_MAX_ATTEMPTS)
            {
                attempt++;

                try
                {
                    // Update status
                    Dispatcher.Invoke(() =>
                    {
                        BootstrapStatus.Text = attempt > 1
                            ? $"재시도 중... ({attempt}/{Config.TOR_RETRY_MAX_ATTEMPTS})"
                            : "시작 중...";
                    });

                    _torManager = new TorManager(basePort);
                    _torManager.BootstrapProgressChanged += TorManager_BootstrapProgressChanged;

                    await _torManager.StartAsync(_cts.Token);

                    // Tor started successfully
                    Dispatcher.Invoke(() =>
                    {
                        MyAddressTextBox.Text = _torManager.OnionAddress;
                        CurrentPortTextBlock.Text = $"베이스: {basePort} (SOCKS: {basePort}, Control: {basePort + 1}, Service: {basePort + 2})";
                        BootstrapOverlay.Visibility = Visibility.Collapsed;
                        WaitingOverlay.Visibility = Visibility.Visible;
                    });

                    // Start PeerListener
                    _listener = new PeerListener(_torManager.ServicePort, OnMessageReceived);
                    _listener.PeerConnecting += Listener_PeerConnecting;
                    _listener.PeerConnected += Listener_PeerConnected;
                    _listener.PeerDisconnected += Listener_PeerDisconnected;
                    _listener.TypingIndicatorReceived += OnTypingIndicatorReceived;
                    _listener.ReadReceiptReceived += OnReadReceiptReceived;
                    _listener.Start(_cts.Token);

                    return; // Success!
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    _torManager?.Dispose();
                    _torManager = null;

                    if (attempt < Config.TOR_RETRY_MAX_ATTEMPTS)
                    {
                        // Wait before retry
                        await Task.Delay(Config.TOR_RETRY_DELAY_SECONDS * 1000, _cts.Token);
                    }
                }
            }

            // All attempts failed
            MessageBox.Show($"Tor 시작 실패 ({Config.TOR_RETRY_MAX_ATTEMPTS}회 시도): {lastError?.Message}",
                "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }

        private void TorManager_BootstrapProgressChanged(object sender, BootstrapEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                BootstrapProgressBar.Value = e.Progress;
                BootstrapStatus.Text = e.Status;
            });
        }

        private void CopyAddressButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(MyAddressTextBox.Text);
                StatusTextBlock.Text = "상태: 주소 복사됨";
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"상태: 복사 실패 - {ex.Message}";
            }
        }

        private void PeerAddressTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ConnectButton_Click(sender, e);
            }
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            var peerAddress = PeerAddressTextBox.Text.Trim();
            if (string.IsNullOrEmpty(peerAddress))
            {
                StatusTextBlock.Text = "상태: 주소를 입력하세요";
                return;
            }

            if (!_onionRegex.IsMatch(peerAddress))
            {
                StatusTextBlock.Text = "상태: 올바른 v3 .onion 주소를 입력하세요 (56자)";
                return;
            }

            try
            {
                StatusTextBlock.Text = "상태: 연결 시도 중...";
                ConnectButton.IsEnabled = false;

                // Clean up previous connector if exists
                if (_connector != null)
                {
                    _connector.Disconnect();
                    _connector = null;
                }

                _connector = new PeerConnector(_torManager.SocksPort, OnMessageReceived);
                _connector.Connected += Connector_Connected;
                _connector.Disconnected += Connector_Disconnected;
                _connector.TypingIndicatorReceived += OnTypingIndicatorReceived;
                _connector.ReadReceiptReceived += OnReadReceiptReceived;

                await _connector.ConnectAsync(peerAddress, Config.HIDDEN_SERVICE_PORT, _cts.Token);
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"상태: 연결 실패 - {ex.Message}";
                ConnectButton.IsEnabled = true;

                // Clean up failed connector
                if (_connector != null)
                {
                    _connector.Disconnect();
                    _connector = null;
                }
            }
        }

        private void Listener_PeerConnecting(object sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                StatusTextBlock.Text = "상태: 연결 요청이 들어오고 있습니다...";
            });
        }

        private void Listener_PeerConnected(object sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                _isConnected = true;
                WaitingOverlay.Visibility = Visibility.Collapsed;
                ChatPanel.Visibility = Visibility.Visible;
                AppendStatusMessage("상대방이 연결되었습니다");
            });
        }

        private void Connector_Connected(object sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                _isConnected = true;
                WaitingOverlay.Visibility = Visibility.Collapsed;
                ChatPanel.Visibility = Visibility.Visible;
                AppendStatusMessage("연결 성공");
            });
        }

        private void Listener_PeerDisconnected(object sender, EventArgs e)
        {
            HandleDisconnection();
        }

        private void Connector_Disconnected(object sender, EventArgs e)
        {
            HandleDisconnection();
        }

        private void HandleDisconnection()
        {
            Dispatcher.Invoke(() =>
            {
                _isConnected = false;
                AppendStatusMessage("연결이 끊어졌습니다");
                MessageInputTextBox.IsEnabled = false;

                // Use DispatcherTimer to delay close without blocking UI thread.
                // Dispatcher.Invoke(async () => ...) is broken: Invoke returns at the
                // first await, so the async continuation runs unobserved.
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(Config.DISCONNECT_DELAY_SECONDS)
                };
                timer.Tick += (s, _) =>
                {
                    ((System.Windows.Threading.DispatcherTimer)s).Stop();
                    Close();
                };
                timer.Start();
            });
        }

        private void OnMessageReceived(string message)
        {
            Dispatcher.Invoke(() =>
            {
                AppendChatMessage(message, isMyMessage: false);
            });
        }

        private void MessageInputTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            CharCountTextBlock.Text = $"{MessageInputTextBox.Text.Length}/{Config.MAX_MESSAGE_LENGTH}";

            // Send typing indicator (debounced)
            if (_isConnected && !string.IsNullOrEmpty(MessageInputTextBox.Text))
            {
                _typingTimer.Stop();
                _typingTimer.Start();
            }
        }

        private async void MessageInputTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(MessageInputTextBox.Text))
            {
                if (!_isConnected)
                {
                    AppendStatusMessage("연결되지 않았습니다");
                    e.Handled = true;
                    return;
                }

                string message = MessageInputTextBox.Text.Trim();
                MessageInputTextBox.Clear();

                // Send first; display only on success
                try
                {
                    if (_connector?.IsConnected == true)
                        await _connector.SendAsync(message, _cts.Token);
                    else if (_listener?.IsConnected == true)
                        await _listener.SendAsync(message, _cts.Token);
                    else
                        throw new InvalidOperationException("연결이 없습니다");

                    AppendChatMessage(message, isMyMessage: true);
                }
                catch (Exception ex)
                {
                    AppendStatusMessage($"전송 실패: {ex.Message}");
                }

                e.Handled = true;
            }
        }

        private void AppendChatMessage(string message, bool isMyMessage)
        {
            // Create message bubble (KakaoTalk style)
            var bubble = new Border
            {
                Background = isMyMessage
                    ? new SolidColorBrush(Color.FromRgb(253, 229, 0))   // #FDE500 Yellow for my messages
                    : new SolidColorBrush(Color.FromRgb(255, 255, 255)), // White for peer
                CornerRadius = isMyMessage
                    ? new CornerRadius(15, 15, 5, 15)   // My message: tail at bottom-right
                    : new CornerRadius(15, 15, 15, 5),  // Peer message: tail at bottom-left
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(10, 5, 10, 5),
                HorizontalAlignment = isMyMessage ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                MaxWidth = 300
            };

            var textBlock = new TextBlock
            {
                Text = message,
                FontFamily = new FontFamily("Malgun Gothic"),
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(0, 0, 0)),
                TextWrapping = TextWrapping.Wrap
            };

            bubble.Child = textBlock;
            ChatStackPanel.Children.Add(bubble);

            // Auto-remove old messages
            if (ChatStackPanel.Children.Count > Config.MAX_VISIBLE_MESSAGES)
            {
                ChatStackPanel.Children.RemoveAt(0);
            }
        }

        private void AppendStatusMessage(string message)
        {
            var textBlock = new TextBlock
            {
                Text = $"[{message}]",
                FontFamily = new FontFamily("Malgun Gothic"),
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(128, 128, 128)),
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 10, 0, 10),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            ChatStackPanel.Children.Add(textBlock);

            // Keep total count within limit (status messages count toward the limit too)
            if (ChatStackPanel.Children.Count > Config.MAX_VISIBLE_MESSAGES)
            {
                ChatStackPanel.Children.RemoveAt(0);
            }
        }

        private async void TypingTimer_Tick(object sender, EventArgs e)
        {
            _typingTimer.Stop();

            try
            {
                if (_connector?.IsConnected == true)
                {
                    await _connector.SendTypingIndicatorAsync(_cts.Token);
                }
                else if (_listener?.IsConnected == true)
                {
                    await _listener.SendTypingIndicatorAsync(_cts.Token);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Typing Indicator Send Error] {ex.Message}");
            }
        }

        private void OnTypingIndicatorReceived(object sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                TypingIndicator.Visibility = Visibility.Visible;

                // Reset hide timer
                _typingIndicatorHideTimer.Stop();
                _typingIndicatorHideTimer.Start();
            });
        }

        private void TypingIndicatorHideTimer_Tick(object sender, EventArgs e)
        {
            _typingIndicatorHideTimer.Stop();
            TypingIndicator.Visibility = Visibility.Collapsed;
        }

        private void OnReadReceiptReceived(object sender, EventArgs e)
        {
            // Future: Update message status to "읽음"
            System.Diagnostics.Debug.WriteLine("[Read Receipt] Peer has read the message");
        }

        private void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _cts?.Cancel();
            _listener?.Stop();
            _connector?.Disconnect();
            _torManager?.Dispose();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            WindowHelper.RemoveIcon(this);
        }
    }
}
