using System.Diagnostics;

namespace OSFR.Linux.LauncherDemo;

public partial class MainWindow
{
    private const string LocalManifestPrefix = "https://127.0.0.1:8443/";
    private Process? _localManifestHostProcess;

    private void StartLocalManifestHost()
    {
        if (_localManifestHostProcess is { HasExited: false })
            return;

        try
        {
            Directory.CreateDirectory(LocalManifestHostDirectory);
            WriteLocalServerManifest();
            WriteLocalManifestPythonHost();

            var certPath = Path.Combine(LocalManifestHostDirectory, "cert.crt");
            var keyPath = Path.Combine(LocalManifestHostDirectory, "key.pem");

            if (!File.Exists(certPath) || !File.Exists(keyPath))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var testCert = Path.Combine(home, "sanctuary-manifest-test", "cert.crt");
                var testKey = Path.Combine(home, "sanctuary-manifest-test", "key.pem");

                if (File.Exists(testCert) && File.Exists(testKey))
                {
                    File.Copy(testCert, certPath, true);
                    File.Copy(testKey, keyPath, true);
                }
            }

            if (!File.Exists(certPath) || !File.Exists(keyPath))
                throw new FileNotFoundException("Local HTTPS certificate was not found. The launcher expected the already-trusted local test certificate.");

            if (!CommandExists("python3"))
                throw new InvalidOperationException("python3 is required for the local HTTPS manifest host.");

            var scriptPath = Path.Combine(LocalManifestHostDirectory, "serve-local.py");
            var startInfo = new ProcessStartInfo
            {
                FileName = "python3",
                WorkingDirectory = LocalManifestHostDirectory,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add(certPath);
            startInfo.ArgumentList.Add(keyPath);

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start())
                throw new InvalidOperationException("Could not start the local manifest host.");

            if (process.WaitForExit(300))
            {
                var exitCode = process.ExitCode;
                process.Dispose();
                throw new InvalidOperationException($"Local manifest host exited immediately with code {exitCode}. Port 8443 may already be in use.");
            }

            process.Exited += (_, _) =>
            {
                try { process.Dispose(); } catch { }
                if (ReferenceEquals(_localManifestHostProcess, process))
                    _localManifestHostProcess = null;
            };

            _localManifestHostProcess = process;
        }
        catch (Exception ex)
        {
            SetLocalServerStatus("MANIFEST HOST FAILED", Bad, $"Could not start {LocalManifestPrefix}: {ex.Message}");
        }
    }

    private void WriteLocalServerManifest()
    {
        var xml = """
<ServerManifest version="2">
  <Name>Local Sanctuary</Name>
  <Description>Sanctuary server running locally on this computer.</Description>
  <WebApiUrl>https://127.0.0.1:8443/</WebApiUrl>
  <LoginServer>127.0.0.1:20042</LoginServer>
  <LogoUrl>servericon.png</LogoUrl>
</ServerManifest>
""";
        File.WriteAllText(Path.Combine(LocalManifestHostDirectory, "servermanifest.xml"), xml);
    }

    private void WriteLocalManifestPythonHost()
    {
        var script = """
import http.server
import ssl
import sys
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent
CERT = sys.argv[1]
KEY = sys.argv[2]
WEBAPI = "http://127.0.0.1:20040"

class Handler(http.server.SimpleHTTPRequestHandler):
    directory = str(ROOT)

    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory=str(ROOT), **kwargs)

    def read_request_body(self):
        transfer_encoding = self.headers.get("Transfer-Encoding", "").lower()
        if "chunked" in transfer_encoding:
            body = bytearray()
            while True:
                line = self.rfile.readline().strip()
                if not line:
                    continue
                chunk_size = int(line.split(b";", 1)[0], 16)
                if chunk_size == 0:
                    while True:
                        trailer = self.rfile.readline()
                        if trailer in (b"\r\n", b"\n", b""):
                            break
                    break
                body.extend(self.rfile.read(chunk_size))
                self.rfile.read(2)
            return bytes(body)

        length = int(self.headers.get("Content-Length", "0"))
        return self.rfile.read(length) if length else b""

    def do_POST(self):
        if self.path not in ("/register", "/login"):
            self.send_error(404)
            return

        body = self.read_request_body()
        print(f"Proxying {self.path}: {len(body)} request bytes", flush=True)

        request = urllib.request.Request(
            WEBAPI + self.path,
            data=body,
            method="POST",
            headers={"Content-Type": self.headers.get("Content-Type", "application/json")},
        )

        try:
            with urllib.request.urlopen(request, timeout=15) as response:
                payload = response.read()
                self.send_response(response.status)
                self.send_header("Content-Type", response.headers.get("Content-Type", "application/json"))
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)
        except urllib.error.HTTPError as error:
            payload = error.read()
            self.send_response(error.code)
            self.send_header("Content-Type", error.headers.get("Content-Type", "application/json"))
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)
        except Exception as error:
            payload = ("Local Sanctuary WebAPI proxy failed: " + str(error)).encode("utf-8")
            self.send_response(502)
            self.send_header("Content-Type", "text/plain; charset=utf-8")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)

server = http.server.ThreadingHTTPServer(("127.0.0.1", 8443), Handler)
ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
ctx.load_cert_chain(CERT, KEY)
server.socket = ctx.wrap_socket(server.socket, server_side=True)
print("Local Sanctuary manifest host running at https://127.0.0.1:8443/", flush=True)
server.serve_forever()
""";

        File.WriteAllText(Path.Combine(LocalManifestHostDirectory, "serve-local.py"), script);
    }
}
