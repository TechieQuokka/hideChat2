using System;

namespace hideChat2
{
    /// <summary>
    /// Application configuration constants
    /// </summary>
    public static class Config
    {
        // Application settings
        public const int MAX_VISIBLE_MESSAGES = 10;  // Maximum messages before auto-deletion
        public const int MAX_MESSAGE_LENGTH = 280;   // Twitter-style message length

        // Connection settings
        public const int IDLE_WARNING_SECONDS = 480;  // 8 minutes - warn user
        public const int CONNECTION_TIMEOUT_SECONDS = 600;  // 10 minutes - connection dies
        public const int DISCONNECT_DELAY_SECONDS = 3;  // Delay before closing app after disconnect

        // Tor settings
        public const int DEFAULT_BASE_PORT = 9050;  // Default base port
        public const int MIN_PORT = 9050;  // Minimum port range
        public const int MAX_PORT = 9150;  // Maximum port range
        public const int HIDDEN_SERVICE_PORT = 9999;  // Standard hidden service port
        public const int TOR_BOOTSTRAP_TIMEOUT_SECONDS = 300;  // 5 minutes timeout per attempt
        public const int TOR_RETRY_MAX_ATTEMPTS = 3;  // Maximum retry attempts
        public const int TOR_RETRY_DELAY_SECONDS = 5;  // Delay between retries

        // UI settings
        public const int WINDOW_WIDTH = 500;
        public const int WINDOW_HEIGHT = 600;
        public const string WINDOW_TITLE = "Alpha";

        // Tor configuration template
        // Parameters: {0}=SocksPort, {1}=ControlPort, {2}=DataDirectory, {3}=HiddenServiceDir,
        //             {4}=HiddenServicePort, {5}=ServicePort, {6}=GeoIPFile, {7}=GeoIPv6File
        public const string TORRC_TEMPLATE = @"SocksPort {0}
ControlPort {1}
DataDirectory {2}
HiddenServiceDir {3}
HiddenServicePort {4} 127.0.0.1:{5}
GeoIPFile {6}
GeoIPv6File {7}
LearnCircuitBuildTimeout 1
CircuitBuildTimeout 15
NumEntryGuards 3
KeepalivePeriod 60
NewCircuitPeriod 30
Log notice stdout
";

        /// <summary>
        /// Get a random available port in the configured range
        /// Checks that base port, base+1 (control), and base+2 (service) are all available
        /// </summary>
        public static int GetRandomAvailablePort()
        {
            var random = new Random();
            int maxAttempts = 50;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                int basePort = random.Next(MIN_PORT, MAX_PORT + 1 - 2); // -2 to ensure base+2 is within range

                // Check if all 3 ports are available
                if (IsPortAvailable(basePort) &&
                    IsPortAvailable(basePort + 1) &&
                    IsPortAvailable(basePort + 2))
                {
                    return basePort;
                }
            }

            // Fallback: return random port and hope for the best
            return random.Next(MIN_PORT, MAX_PORT + 1 - 2);
        }

        public static bool IsPortAvailable(int port)
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            try
            {
                listener.Start();
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { listener.Stop(); } catch { }
            }
        }
    }
}
