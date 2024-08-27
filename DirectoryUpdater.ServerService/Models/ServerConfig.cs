namespace DirectoryUpdater.ServerService.Models;

public class ServerConfig
{
    public string BaseDirectory { get; set; } = string.Empty;
    public int Port { get; set; }
    public int RateLimitMilliseconds { get; set; }
    public List<string> ExcludedPaths { get; set; } = default!;
}