using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
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
            // Use current directory like torChat (proven to work)
            _dataDir = Path.GetFullPath($"tordata_{basePort}");
            _torExePath = torExePath ?? ExtractTorExecutable();
        }

        /// <summary>
        /// Extract tor.exe and geoip files from embedded resources
        /// </summary>
        private string ExtractTorExecutable()
        {
            var torDir = Config.TempTorDirectory;
            Directory.CreateDirectory(torDir);

            var torExePath = Path.Combine(torDir, "tor.exe");
            var geoipPath = Path.Combine(torDir, "geoip");
            var geoip6Path = Path.Combine(torDir, "geoip6");

            // Extract tor.exe (re-extract if file missing or tampered)
            if (!File.Exists(torExePath) || !FileMatchesEmbeddedResource(torExePath, "hideChat2.Resources.tor.exe"))
            {
                ExtractResource("hideChat2.Resources.tor.exe", torExePath);
            }

            // Extract geoip files
            if (!File.Exists(geoipPath))
            {
                ExtractResource("hideChat2.Resources.geoip", geoipPath);
            }

            if (!File.Exists(geoip6Path))
            {
                ExtractResource("hideChat2.Resources.geoip6", geoip6Path);
            }

            return torExePath;
        }

        /// <summary>
        /// Verify that an on-disk file matches its embedded resource by SHA-256 hash.
        /// Returns false on any error so the caller re-extracts defensively.
        /// </summary>
        private bool FileMatchesEmbeddedResource(string filePath, string resourceName)
        {
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                byte[] resourceHash;
                using (var resourceStream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (resourceStream == null) return false;
                    using (var sha = SHA256.Create())
                        resourceHash = sha.ComputeHash(resourceStream);
                }

                byte[] fileHash;
                using (var fileStream = File.OpenRead(filePath))
                using (var sha = SHA256.Create())
                    fileHash = sha.ComputeHash(fileStream);

                if (resourceHash.Length != fileHash.Length) return false;
                for (int i = 0; i < resourceHash.Length; i++)
                    if (resourceHash[i] != fileHash[i]) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Extract embedded resource to file
        /// </summary>
        private void ExtractResource(string resourceName, string outputPath)
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                    throw new FileNotFoundException($"Embedded resource not found: {resourceName}");

                using (var fileStream = File.Create(outputPath))
                {
                    stream.CopyTo(fileStream);
                }
            }
        }

        public async Task StartAsync(CancellationToken ct = default)
        {
            var hsDir = Path.Combine(_dataDir, "hidden_service");
            Directory.CreateDirectory(_dataDir);
            Directory.CreateDirectory(hsDir);

            var torrcPath = Path.Combine(_dataDir, "torrc");

            // GeoIP files from extracted resources
            var geoipFile = Path.Combine(Config.TempTorDirectory, "geoip").Replace("\\", "/");
            var geoip6File = Path.Combine(Config.TempTorDirectory, "geoip6").Replace("\\", "/");

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
