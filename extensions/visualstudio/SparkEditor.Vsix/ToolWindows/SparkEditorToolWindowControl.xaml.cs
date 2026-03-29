using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace SparkEditor.Vsix
{
    public partial class SparkEditorToolWindowControl : UserControl
    {
        private System.Diagnostics.Process _serverProcess;
        private int _port;

        public SparkEditorToolWindowControl()
        {
            InitializeComponent();
            Unloaded += OnUnloaded;
        }

        private async void OnStartClick(object sender, RoutedEventArgs e)
        {
            StartButton.IsEnabled = false;

            try
            {
                // Find App_Data paths in the solution
                var appDataPaths = FindAppDataPaths();
                if (appDataPaths.Length == 0)
                {
                    StatusText.Text = "No App_Data directories with programUnits.json found. Open a Spark solution first.";
                    StartButton.IsEnabled = true;
                    return;
                }

                StatusText.Text = string.Format("Found {0} App_Data path(s). Starting server...", appDataPaths.Length);

                // Find available port
                _port = GetAvailablePort();

                // Find the SparkEditor DLL
                var dllPath = FindSparkEditorDll();
                if (dllPath == null)
                {
                    StatusText.Text = "SparkEditor.dll not found. Build the SparkEditor project first.";
                    StartButton.IsEnabled = true;
                    return;
                }

                StatusText.Text = string.Format("Starting on port {0}...", _port);

                // Build arguments
                var args = string.Join(" ", appDataPaths.Select(p => string.Format("--target-app-data \"{0}\"", p)));
                args += string.Format(" --port {0}", _port);

                // Start server process
                _serverProcess = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "dotnet",
                        Arguments = string.Format("\"{0}\" {1}", dllPath, args),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    }
                };

                _serverProcess.Start();

                // Wait for server to be ready
                StatusText.Text = "Waiting for server to be ready...";
                var ready = await WaitForServerReadyAsync();

                if (!ready)
                {
                    StatusText.Text = "Server did not start in time. Check the Output window for errors.";
                    StartButton.IsEnabled = true;
                    return;
                }

                // Initialize WebView2 and navigate
                StatusText.Text = string.Format("Connected on port {0}", _port);

                var userDataFolder = Path.Combine(Path.GetTempPath(), "SparkEditor", "WebView2");
                Directory.CreateDirectory(userDataFolder);

                var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await WebView.EnsureCoreWebView2Async(environment);

                WebView.Visibility = Visibility.Visible;
                WebView.CoreWebView2.Navigate(string.Format("http://localhost:{0}", _port));
            }
            catch (Exception ex)
            {
                StatusText.Text = string.Format("Error: {0}", ex.Message);
                StartButton.IsEnabled = true;
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            StopServer();
        }

        private void StopServer()
        {
            if (_serverProcess != null && !_serverProcess.HasExited)
            {
                try { _serverProcess.Kill(); } catch { }
                _serverProcess.Dispose();
                _serverProcess = null;
            }
        }

        private async Task<bool> WaitForServerReadyAsync()
        {
            using (var client = new HttpClient())
            {
                for (int i = 0; i < 30; i++)
                {
                    try
                    {
                        var response = await client.GetAsync(string.Format("http://localhost:{0}/spark/types", _port));
                        if (response.IsSuccessStatusCode) return true;
                    }
                    catch
                    {
                        // Server not ready yet
                    }

                    // Check if process crashed
                    if (_serverProcess != null && _serverProcess.HasExited)
                    {
                        return false;
                    }

                    await Task.Delay(1000);
                }
            }
            return false;
        }

        private static string[] FindAppDataPaths()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
                if (dte == null || string.IsNullOrEmpty(dte.Solution?.FullName))
                    return new string[0];

                var solutionDir = Path.GetDirectoryName(dte.Solution.FullName);
                if (solutionDir == null) return new string[0];

                return Directory.GetDirectories(solutionDir, "App_Data", SearchOption.AllDirectories)
                    .Where(d => File.Exists(Path.Combine(d, "programUnits.json")))
                    .Where(d => !d.Contains("node_modules") && !d.Contains("\\bin\\") && !d.Contains("\\obj\\"))
                    .ToArray();
            }
            catch
            {
                return new string[0];
            }
        }

        private static string FindSparkEditorDll()
        {
            // Look in a well-known location relative to the MintPlayer.Spark repo
            var candidates = new[]
            {
                @"C:\Repos\MintPlayer.Spark\SparkEditor\SparkEditor\bin\Debug\net10.0\SparkEditor.dll",
                @"C:\Repos\MintPlayer.Spark\SparkEditor\SparkEditor\bin\Release\net10.0\SparkEditor.dll",
            };

            foreach (var path in candidates)
            {
                if (File.Exists(path)) return path;
            }

            // Fallback: search the solution directory
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
                if (dte?.Solution?.FullName == null) return null;

                var solutionDir = Path.GetDirectoryName(dte.Solution.FullName);
                if (solutionDir == null) return null;

                return Directory.GetFiles(solutionDir, "SparkEditor.dll", SearchOption.AllDirectories)
                    .Where(f => f.Contains("bin"))
                    .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private static int GetAvailablePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
