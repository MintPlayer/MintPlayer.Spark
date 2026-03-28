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
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Find App_Data paths in the solution
                var appDataPaths = FindAppDataPaths();
                if (appDataPaths.Length == 0)
                {
                    StatusText.Text = "No App_Data directories with programUnits.json found in the solution.";
                    return;
                }

                // Find available port
                _port = GetAvailablePort();

                // Find and start the SparkEditor
                var dllPath = FindSparkEditorDll();
                if (dllPath == null)
                {
                    StatusText.Text = "SparkEditor.dll not found. Build the SparkEditor project first.";
                    return;
                }

                StatusText.Text = string.Format("Starting Spark Editor on port {0}...", _port);

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
                await WaitForServerReadyAsync();

                // Initialize WebView2 and navigate
                StatusText.Visibility = Visibility.Collapsed;

                var userDataFolder = Path.Combine(Path.GetTempPath(), "SparkEditor", "WebView2");
                var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await WebView.EnsureCoreWebView2Async(environment);
                WebView.CoreWebView2.Navigate(string.Format("http://localhost:{0}", _port));
            }
            catch (Exception ex)
            {
                StatusText.Text = string.Format("Error: {0}", ex.Message);
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_serverProcess != null && !_serverProcess.HasExited)
            {
                try
                {
                    _serverProcess.Kill();
                }
                catch
                {
                    // Process may have already exited
                }
                _serverProcess.Dispose();
                _serverProcess = null;
            }
        }

        private async Task WaitForServerReadyAsync()
        {
            using (var client = new HttpClient())
            {
                for (int i = 0; i < 30; i++)
                {
                    try
                    {
                        var response = await client.GetAsync(string.Format("http://localhost:{0}/spark/types", _port));
                        if (response.IsSuccessStatusCode) return;
                    }
                    catch
                    {
                        // Server not ready yet
                    }
                    await Task.Delay(500);
                }
            }
        }

        private static string[] FindAppDataPaths()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
                if (dte == null || string.IsNullOrEmpty(dte.Solution?.FullName)) return new string[0];

                var solutionDir = Path.GetDirectoryName(dte.Solution.FullName);
                if (solutionDir == null) return new string[0];

                return Directory.GetDirectories(solutionDir, "App_Data", SearchOption.AllDirectories)
                    .Where(d => File.Exists(Path.Combine(d, "programUnits.json")))
                    .Where(d => !d.Contains("node_modules") && !d.Contains("bin") && !d.Contains("obj"))
                    .ToArray();
            }
            catch
            {
                return new string[0];
            }
        }

        private static string FindSparkEditorDll()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
                if (dte == null || string.IsNullOrEmpty(dte.Solution?.FullName)) return null;

                var solutionDir = Path.GetDirectoryName(dte.Solution.FullName);
                if (solutionDir == null) return null;

                // Look for SparkEditor in the solution's output
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
