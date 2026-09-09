using System.Net;
using System.Text;

namespace OSFR.Linux.LauncherDemo;

public partial class MainWindow
{
    private const string LocalManifestPrefix = "http://127.0.0.1:8443/";
    private static readonly Uri LocalSanctuaryWebApi = new("http://127.0.0.1:20040/");

    private HttpListener? _localManifestListener;
    private CancellationTokenSource? _localManifestHostCancellation;

    private void StartLocalManifestHost()
    {
        if (_localManifestListener is not null)
            return;

        try
        {
            Directory.CreateDirectory(LocalManifestHostDirectory);
            WriteLocalServerManifest();

            var listener = new HttpListener();
            listener.Prefixes.Add(LocalManifestPrefix);
            listener.Start();

            _localManifestListener = listener;
            _localManifestHostCancellation = new CancellationTokenSource();
            _ = Task.Run(() => RunLocalManifestHostAsync(listener, _localManifestHostCancellation.Token));
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
  <WebApiUrl>http://127.0.0.1:8443/</WebApiUrl>
  <LoginServer>127.0.0.1:20042</LoginServer>
  <LogoUrl>servericon.png</LogoUrl>
</ServerManifest>
""";
        File.WriteAllText(Path.Combine(LocalManifestHostDirectory, "servermanifest.xml"), xml);
    }

    private async Task RunLocalManifestHostAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested || !listener.IsListening)
            {
                break;
            }
            catch
            {
                continue;
            }

            _ = Task.Run(() => HandleLocalManifestRequestAsync(context), cancellationToken);
        }
    }

    private async Task HandleLocalManifestRequestAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";

            if (context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                (path.Equals("/register", StringComparison.OrdinalIgnoreCase) ||
                 path.Equals("/login", StringComparison.OrdinalIgnoreCase)))
            {
                await ProxyLocalWebApiRequestAsync(context, path);
                return;
            }

            if (!context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                !context.Request.HttpMethod.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                context.Response.Close();
                return;
            }

            string filePath;
            if (path.Equals("/servermanifest.xml", StringComparison.OrdinalIgnoreCase))
            {
                WriteLocalServerManifest();
                filePath = Path.Combine(LocalManifestHostDirectory, "servermanifest.xml");
            }
            else if (path.Equals("/clientmanifest.xml", StringComparison.OrdinalIgnoreCase))
            {
                filePath = Path.Combine(LocalManifestHostDirectory, "clientmanifest.xml");
            }
            else if (path.Equals("/servericon.png", StringComparison.OrdinalIgnoreCase))
            {
                filePath = Path.Combine(LocalManifestHostDirectory, "servericon.png");
            }
            else if (path.StartsWith("/client/", StringComparison.OrdinalIgnoreCase))
            {
                var relativeUrl = Uri.UnescapeDataString(path[8..]);
                var relativePath = relativeUrl.Replace('/', Path.DirectorySeparatorChar);
                filePath = GetSafeClientPath(LocalManifestClientDirectory, relativePath);
            }
            else
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                context.Response.Close();
                return;
            }

            if (!File.Exists(filePath))
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                context.Response.Close();
                return;
            }

            context.Response.ContentType = Path.GetExtension(filePath).ToLowerInvariant() switch
            {
                ".xml" => "application/xml; charset=utf-8",
                ".png" => "image/png",
                ".html" => "text/html; charset=utf-8",
                ".txt" => "text/plain; charset=utf-8",
                _ => "application/octet-stream"
            };

            var info = new FileInfo(filePath);
            context.Response.ContentLength64 = info.Length;

            if (!context.Request.HttpMethod.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
            {
                await using var input = File.OpenRead(filePath);
                await input.CopyToAsync(context.Response.OutputStream);
            }

            context.Response.Close();
        }
        catch
        {
            try
            {
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                context.Response.Close();
            }
            catch
            {
            }
        }
    }

    private async Task ProxyLocalWebApiRequestAsync(HttpListenerContext context, string path)
    {
        var target = new Uri(LocalSanctuaryWebApi, path.TrimStart('/'));
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.HttpMethod), target);

        if (context.Request.HasEntityBody)
        {
            using var memory = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(memory);
            var content = new ByteArrayContent(memory.ToArray());
            if (!string.IsNullOrWhiteSpace(context.Request.ContentType))
                content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
            request.Content = content;
        }

        using var response = await _httpClient.SendAsync(request);
        var responseBytes = await response.Content.ReadAsByteArrayAsync();

        context.Response.StatusCode = (int)response.StatusCode;
        if (response.Content.Headers.ContentType is not null)
            context.Response.ContentType = response.Content.Headers.ContentType.ToString();
        context.Response.ContentLength64 = responseBytes.LongLength;

        if (responseBytes.Length > 0)
            await context.Response.OutputStream.WriteAsync(responseBytes);

        context.Response.Close();
    }
}
