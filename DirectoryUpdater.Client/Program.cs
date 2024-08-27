using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.IO;
using System.Net.Http;
using Newtonsoft.Json;

class Program
{
    static ClientConfig? _config;

    static async Task Main(string[] args)
    {
        LoadConfig();

        using var client = new ClientWebSocket();
        if (_config != null) await client.ConnectAsync(new Uri(_config.ServerUri), CancellationToken.None);

        Console.WriteLine("Connected to server.");

        var buffer = new byte[1024 * 4];
        while (client.State == WebSocketState.Open)
        {
            var result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            var message = Encoding.UTF8.GetString(buffer, 0, result.Count);

            var parts = message.Split('|');
            if (parts.Length > 1)
            {
                var command = parts[0];
                var filePath = parts[1];

                switch (command)
                {
                    case "UPDATE":
                        await DownloadFile(filePath);
                        break;
                    case "DELETE":
                        DeleteFile(filePath);
                        break;
                }
            }

            // Disconnect after processing the message.
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Finished", CancellationToken.None);
            }
        }

        Console.WriteLine("Disconnected from server.");
    }

    private static void LoadConfig()
    {
        var configContent = File.ReadAllText("clientConfig.json");
        _config = JsonConvert.DeserializeObject<ClientConfig>(configContent);
    }

    static async Task DownloadFile(string filePath)
    {
        using var httpClient = new HttpClient();
        if (_config != null)
        {
            var response = await httpClient.GetAsync($"{_config.ServerUri.TrimEnd('/')}/{Uri.EscapeDataString(filePath)}");
            if (response.IsSuccessStatusCode)
            {
                var fileContent = await response.Content.ReadAsByteArrayAsync();
                await File.WriteAllBytesAsync(filePath, fileContent);
                Console.WriteLine($"File downloaded: {filePath}");
            }
        }
    }

    static void DeleteFile(string filePath)
    {
        if (!File.Exists(filePath)) return;
        File.Delete(filePath);
        Console.WriteLine($"File deleted: {filePath}");
    }
}
