using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace hideChat2
{
    /// <summary>
    /// Manages Tor process and Hidden Service
    /// Port rules: base=9050 → SOCKS=base, Control=base+1, Service=base+2
    /// </summary>
    public class TorManager : IDisposable
    {
        private readonly int _socksPort;
        private readonly int _controlPort;
        private readonly int _servicePort;
        private readonly string _dataDir;
        private readonly string _torExePath;
        private Process _torProcess;
        private volatile bool _bootstrapDone = false;

        public int SocksPort => _socksPort;
        public int ServicePort => _servicePort;
        public string OnionAddress { get; private set; }
        public int BootstrapProgress { get; private set; }
        public string BootstrapStatus { get; private set; }

        public event EventHandler<BootstrapEventArgs> BootstrapProgressChanged;

        public TorManager(int basePort, string torExePath = null)
        {
            _socksPort = basePort;
            _controlPort = basePort + 1;
            _servicePort = basePort + 2;
            _dataDir = Path.GetFullPath($"tordata_{basePort}");
            _torExePath = torExePath ?? GetTorPath();
        }

        private static string GetTorPath()
        {
            var appDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            var path = Path.Combine(appDir, "Resources", "tor.exe");
            if (!File.Exists(path))
                throw new FileNotFoundException($"tor.exe not found: {path}");
            return path;
        }

        public async Task StartAsync(CancellationToken ct = default)
        {
            var hsDir = Path.Combine(_dataDir, "hidden_service");
            Directory.CreateDirectory(_dataDir);
            Directory.CreateDirectory(hsDir);

            var torrcPath = Path.Combine(_dataDir, "torrc");

            // GeoIP files from Resources folder alongside the exe
            var appDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            var geoipFile = Path.Combine(appDir, "Resources", "geoip").Replace("\\", "/");
            var geoip6File = Path.Combine(appDir, "Resources", "geoip6").Replace("\\", "/");

            // Generate torrc from template
            var torrc = string.Format(Config.TORRC_TEMPLATE,
                _socksPort,
                _controlPort,
                _dataDir.Replace("\\", "/"),
                hsDir.Replace("\\", "/"),
                Config.HIDDEN_SERVICE_PORT,
                _servicePort,
                geoipFile,
                geoip6File);

            File.WriteAllText(torrcPath, torrc);

            _torProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _torExePath,
                    Arguments = $"-f \"{torrcPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };

            // stdout real-time output
            _torProcess.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;

                Debug.WriteLine($"[Tor:{_socksPort}] {e.Data}");

                // Parse Bootstrapped lines
                if (e.Data.Contains("Bootstrapped"))
                {
                    var idx = e.Data.IndexOf("Bootstrapped");
                    var bootstrapLine = e.Data.Substring(idx);

                    // Extract percentage
                    var percentIdx = bootstrapLine.IndexOf("Bootstrapped ") + "Bootstrapped ".Length;
                    var percentEnd = bootstrapLine.IndexOf("%", percentIdx);
                    if (percentEnd > percentIdx &&
                        int.TryParse(bootstrapLine.Substring(percentIdx, percentEnd - percentIdx), out int percent))
                    {
                        BootstrapProgress = percent;
                        BootstrapStatus = bootstrapLine;
                        BootstrapProgressChanged?.Invoke(this, new BootstrapEventArgs
                        {
                            Progress = percent,
                            Status = bootstrapLine
                        });

                        if (percent >= 100)
                            _bootstrapDone = true;
                    }
                }
            };

            // stderr output
            _torProcess.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                Debug.WriteLine($"[Tor:{_socksPort} ERROR] {e.Data}");
            };

            _torProcess.Start();
            _torProcess.BeginOutputReadLine();
            _torProcess.BeginErrorReadLine();

            Debug.WriteLine($"[Tor] Starting... (SOCKS:{_socksPort} Control:{_controlPort} Service:{_servicePort})");

            // Wait for bootstrap completion
            await WaitForBootstrapAsync(ct);

            // Read .onion address
            var hostnameFile = Path.Combine(_dataDir, "hidden_service", "hostname");
            for (int i = 0; i < 30; i++)
            {
                if (File.Exists(hostnameFile))
                {
                    OnionAddress = File.ReadAllText(hostnameFile).Trim();
                    break;
                }
                await Task.Delay(1000, ct);
            }

            if (OnionAddress == null)
                throw new Exception("Failed to retrieve Hidden Service .onion address");
        }

        private async Task WaitForBootstrapAsync(CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddSeconds(Config.TOR_BOOTSTRAP_TIMEOUT_SECONDS);
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                if (_bootstrapDone) return;
                await Task.Delay(500, ct);
            }
            throw new TimeoutException($"Tor bootstrap timeout ({Config.TOR_BOOTSTRAP_TIMEOUT_SECONDS} seconds)");
        }

        public void Stop()
        {
            try { _torProcess?.Kill(); } catch { }
            _torProcess = null;
        }

        public void Dispose() => Stop();
    }

    public class BootstrapEventArgs : EventArgs
    {
        public int Progress { get; set; }
        public string Status { get; set; }
    }
}
