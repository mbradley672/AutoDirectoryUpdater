using System.Net;
using System.Net.WebSockets;
using System.Text;
using DirectoryUpdater.ServerService.Models;
using Newtonsoft.Json;

namespace DirectoryUpdater.ServerService
{
    public class Worker(ILogger<Worker> logger) : BackgroundService
    {
        private FileSystemWatcher _fileWatcher = null!;
        private ServerConfig? _config;
        private DateTime _lastEventTime = DateTime.MinValue;
        private HttpListener _server = null!;
        private Task _serverTask = null!;

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            LoadConfig();
            InitializeFileWatcher();
            StartWebServer();

            while (!stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
                await Task.Delay(1000, stoppingToken);
            }
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            StopWebServer();
            return base.StopAsync(cancellationToken);
        }

        private void LoadConfig()
        {
            var configContent = File.ReadAllText("serverConfig.json");
            _config = JsonConvert.DeserializeObject<ServerConfig>(configContent);
        }

        private void InitializeFileWatcher()
        {
            if (_config == null) return;
            _fileWatcher = new FileSystemWatcher(_config.BaseDirectory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
            };

            _fileWatcher.Changed += OnChanged;
            _fileWatcher.Created += OnChanged;
            _fileWatcher.Deleted += OnDeleted;
            _fileWatcher.Renamed += OnRenamed;

            _fileWatcher.EnableRaisingEvents = true;

            logger.LogInformation($"Monitoring directory: {_config.BaseDirectory}");
        }

        private void StartWebServer()
        {
            _server = new HttpListener();
            if (_config == null) return;
            _server.Prefixes.Add($"http://localhost:{_config.Port}/");
            _server.Start();

            _serverTask = Task.Run(async () =>
            {
                while (_server.IsListening)
                {
                    var context = await _server.GetContextAsync();
                    if (context.Request.IsWebSocketRequest)
                    {
                        var wsContext = await context.AcceptWebSocketAsync(null);
                        _ = Task.Run(() => HandleWebSocket(wsContext.WebSocket));
                    }
                    else
                    {
                        HandleHttpRequest(context);
                    }
                }
            });

            logger.LogInformation($"Web server started on port {_config.Port}");
        }

        private void StopWebServer()
        {
            _server.Stop();
            _server.Close();
        }

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            if ((DateTime.Now - _lastEventTime).TotalMilliseconds < _config.RateLimitMilliseconds)
                return;

            if (ShouldExclude(e.FullPath))
                return;

            _lastEventTime = DateTime.Now;
            BroadcastMessage($"UPDATE|{e.FullPath}");
            logger.LogInformation($"File {e.ChangeType}: {e.FullPath}");
        }

        private void OnDeleted(object sender, FileSystemEventArgs e)
        {
            if (ShouldExclude(e.FullPath))
                return;

            BroadcastMessage($"DELETE|{e.FullPath}");
            logger.LogInformation($"File Deleted: {e.FullPath}");
        }

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            if (ShouldExclude(e.FullPath))
                return;

            BroadcastMessage($"RENAME|{e.OldFullPath}|{e.FullPath}");
            logger.LogInformation($"File Renamed: {e.OldFullPath} to {e.FullPath}");
        }

        private bool ShouldExclude(string path)
        {
            return _config != null && _config.ExcludedPaths.Any(excludedPath => path.StartsWith(excludedPath, StringComparison.OrdinalIgnoreCase));
        }


        private async void BroadcastMessage(string message)
        {
            var buffer = Encoding.UTF8.GetBytes(message);
            foreach (var client in Clients.Where(client => client.State == WebSocketState.Open))
            {
                await client.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }

        private static readonly HashSet<WebSocket> Clients = [];

        private static async Task HandleWebSocket(WebSocket socket)
        {
            Clients.Add(socket);
            var buffer = new byte[1024 * 4];

            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                }
            }

            Clients.Remove(socket);
        }

        private void HandleHttpRequest(HttpListenerContext context)
        {
            if (context.Request.Url != null)
            {
                var filePath = context.Request.Url.LocalPath.TrimStart('/').Replace("/", @"\");
                if (_config != null)
                {
                    var fullPath = Path.Combine(_config.BaseDirectory, filePath);

                    logger.LogInformation($"HTTP request for file: {fullPath}");

                    if (File.Exists(fullPath))
                    {
                        context.Response.ContentType = "application/octet-stream";
                        context.Response.AddHeader("Content-Disposition", $"attachment; filename={Path.GetFileName(fullPath)}");
                        context.Response.ContentLength64 = new FileInfo(fullPath).Length;

                        using (var fileStream = File.OpenRead(fullPath))
                        {
                            fileStream.CopyTo(context.Response.OutputStream);
                        }

                        context.Response.StatusCode = (int)HttpStatusCode.OK;
                        logger.LogInformation($"File served: {fullPath}");
                    }
                    else
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        logger.LogWarning($"File not found: {fullPath}");
                    }
                }
            }

            context.Response.Close();
        }
    }
}
