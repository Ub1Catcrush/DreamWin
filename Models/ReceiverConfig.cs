namespace DreamWin.Models;

public class ReceiverConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "My Receiver";
    public string Host { get; set; } = "192.168.1.100";
    public int Port { get; set; } = 80;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool UseHttps { get; set; } = false;
    public int StreamingPort { get; set; } = 8001;
    public bool IsDefault { get; set; } = false;

    public string BaseUrl => $"{(UseHttps ? "https" : "http")}://{Host}:{Port}";
    public string StreamBaseUrl => $"http://{Host}:{StreamingPort}";
}
