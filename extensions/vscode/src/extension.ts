import * as vscode from 'vscode';
import * as net from 'net';
import * as path from 'path';
import { ChildProcess, spawn } from 'child_process';

let serverProcess: ChildProcess | undefined;
let panel: vscode.WebviewPanel | undefined;
let outputChannel: vscode.OutputChannel;

export function activate(context: vscode.ExtensionContext) {
  outputChannel = vscode.window.createOutputChannel('Spark Editor');

  const openEditorCommand = vscode.commands.registerCommand('spark.openEditor', async () => {
    await openSparkEditor(context);
  });

  context.subscriptions.push(openEditorCommand);
  context.subscriptions.push({ dispose: () => stopServer() });

  outputChannel.appendLine('Spark Editor extension activated.');
}

export function deactivate() {
  stopServer();
  panel?.dispose();
}

async function openSparkEditor(context: vscode.ExtensionContext): Promise<void> {
  // If panel already exists, reveal it
  if (panel) {
    panel.reveal(vscode.ViewColumn.One);
    return;
  }

  // Find App_Data directories in the workspace
  const appDataPaths = await findAppDataPaths();
  if (appDataPaths.length === 0) {
    vscode.window.showErrorMessage(
      'No App_Data directories with programUnits.json found in the workspace. ' +
      'Open a MintPlayer.Spark project first.'
    );
    return;
  }

  outputChannel.appendLine(`Found App_Data paths: ${appDataPaths.join(', ')}`);

  // Find an available port
  const port = await getAvailablePort();
  outputChannel.appendLine(`Using port: ${port}`);

  // Start the SparkEditor server
  const started = await startServer(context, port, appDataPaths);
  if (!started) {
    return;
  }

  // Create the webview panel
  panel = vscode.window.createWebviewPanel(
    'sparkEditor',
    'Spark Editor',
    vscode.ViewColumn.One,
    {
      enableScripts: true,
      retainContextWhenHidden: true,
      localResourceRoots: [],
      portMapping: [{ webviewPort: port, extensionHostPort: port }],
    }
  );

  // Handle remote development: use asExternalUri for port forwarding
  const localUri = vscode.Uri.parse(`http://localhost:${port}`);
  const externalUri = await vscode.env.asExternalUri(localUri);

  panel.webview.html = getWebviewContent(externalUri.toString());

  panel.onDidDispose(() => {
    panel = undefined;
    stopServer();
  }, null, context.subscriptions);
}

async function findAppDataPaths(): Promise<string[]> {
  const pattern = '**/App_Data/programUnits.json';
  const files = await vscode.workspace.findFiles(pattern, '**/node_modules/**', 10);

  return files.map(f => {
    const dir = path.dirname(f.fsPath);
    return dir;
  });
}

function getAvailablePort(): Promise<number> {
  return new Promise((resolve, reject) => {
    const server = net.createServer();
    server.listen(0, '127.0.0.1', () => {
      const address = server.address();
      if (address && typeof address === 'object') {
        const port = address.port;
        server.close(() => resolve(port));
      } else {
        server.close(() => reject(new Error('Could not determine port')));
      }
    });
    server.on('error', reject);
  });
}

async function startServer(
  context: vscode.ExtensionContext,
  port: number,
  appDataPaths: string[]
): Promise<boolean> {
  // Build the command arguments
  const args: string[] = [];
  for (const appDataPath of appDataPaths) {
    args.push('--target-app-data', appDataPath);
  }
  args.push('--port', port.toString());

  // Try to find the SparkEditor DLL
  const dllPath = await findSparkEditorDll(context);
  if (!dllPath) {
    vscode.window.showErrorMessage(
      'Could not find SparkEditor.dll. Make sure the Spark Editor is built or installed as a dotnet tool.'
    );
    return false;
  }

  outputChannel.appendLine(`Starting SparkEditor: dotnet ${dllPath} ${args.join(' ')}`);

  return new Promise((resolve) => {
    serverProcess = spawn('dotnet', [dllPath, ...args], {
      stdio: ['ignore', 'pipe', 'pipe'],
    });

    let resolved = false;
    const timeout = setTimeout(() => {
      if (!resolved) {
        resolved = true;
        outputChannel.appendLine('SparkEditor server started (timeout - assuming ready).');
        resolve(true);
      }
    }, 10000);

    serverProcess.stdout?.on('data', (data: Buffer) => {
      const output = data.toString();
      outputChannel.appendLine(`[SparkEditor] ${output.trim()}`);

      // Wait for the server to report it's listening
      if (!resolved && output.includes('listening on')) {
        resolved = true;
        clearTimeout(timeout);
        outputChannel.appendLine('SparkEditor server is ready.');
        resolve(true);
      }
    });

    serverProcess.stderr?.on('data', (data: Buffer) => {
      outputChannel.appendLine(`[SparkEditor ERR] ${data.toString().trim()}`);
    });

    serverProcess.on('error', (err) => {
      outputChannel.appendLine(`Failed to start SparkEditor: ${err.message}`);
      if (!resolved) {
        resolved = true;
        clearTimeout(timeout);
        vscode.window.showErrorMessage(`Failed to start Spark Editor: ${err.message}`);
        resolve(false);
      }
    });

    serverProcess.on('exit', (code) => {
      outputChannel.appendLine(`SparkEditor process exited with code ${code}`);
      serverProcess = undefined;
      if (!resolved) {
        resolved = true;
        clearTimeout(timeout);
        resolve(false);
      }
    });
  });
}

async function findSparkEditorDll(context: vscode.ExtensionContext): Promise<string | undefined> {
  // Strategy 1: Look for the DLL in the extension's bundled directory
  const bundledPath = path.join(context.extensionPath, 'server', 'SparkEditor.dll');
  try {
    await vscode.workspace.fs.stat(vscode.Uri.file(bundledPath));
    return bundledPath;
  } catch {
    // Not found
  }

  // Strategy 2: Look for it in the workspace (development mode)
  const workspaceFolders = vscode.workspace.workspaceFolders;
  if (workspaceFolders) {
    for (const folder of workspaceFolders) {
      const devPath = path.join(
        folder.uri.fsPath,
        'SparkEditor', 'SparkEditor', 'bin', 'Debug', 'net10.0', 'SparkEditor.dll'
      );
      try {
        await vscode.workspace.fs.stat(vscode.Uri.file(devPath));
        return devPath;
      } catch {
        // Not found
      }
    }
  }

  // Strategy 3: Look for dotnet tool installation
  // The user might have installed it as: dotnet tool install -g MintPlayer.Spark.Editor
  // In that case, we'd use 'dotnet spark-editor' command instead of 'dotnet SparkEditor.dll'
  // For now, return undefined and let the caller handle it.

  return undefined;
}

function stopServer(): void {
  if (serverProcess) {
    outputChannel.appendLine('Stopping SparkEditor server...');
    serverProcess.kill();
    serverProcess = undefined;
  }
}

function getWebviewContent(serverUrl: string): string {
  return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>Spark Editor</title>
  <style>
    html, body {
      margin: 0;
      padding: 0;
      width: 100%;
      height: 100%;
      overflow: hidden;
      background: #1e1e1e;
    }
    iframe {
      border: none;
      width: 100%;
      height: 100%;
    }
    .loading {
      display: flex;
      align-items: center;
      justify-content: center;
      height: 100%;
      color: #ccc;
      font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
      font-size: 14px;
    }
  </style>
</head>
<body>
  <div class="loading" id="loading">Loading Spark Editor...</div>
  <iframe id="editor" src="${serverUrl}" style="display:none"
          onload="document.getElementById('loading').style.display='none'; this.style.display='block';"
          sandbox="allow-scripts allow-same-origin allow-forms allow-popups allow-modals">
  </iframe>
</body>
</html>`;
}
