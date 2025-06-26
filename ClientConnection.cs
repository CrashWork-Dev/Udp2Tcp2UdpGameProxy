using System.Net.Sockets;

namespace Udp2Tcp2UdpGameProxy;

internal class ClientConnection(TcpClient client, string clientKey) : IAsyncDisposable
{
    public TcpClient TcpClient { get; } = client ?? throw new ArgumentNullException(nameof(client));
    public string ClientKey { get; } = clientKey ?? throw new ArgumentNullException(nameof(clientKey));

    private string _clientInfo = string.Empty;
    private readonly object _lock = new();
    private volatile bool _disposed;

    public bool IsConnected => !_disposed && TcpClient.Connected;

    public void UpdateClientInfo(string clientInfo)
    {
        lock (_lock)
        {
            _clientInfo = clientInfo ?? string.Empty;
        }
    }

    public string GetClientInfo()
    {
        lock (_lock)
        {
            return _clientInfo;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        
        _disposed = true;
        
        try
        {
            TcpClient.Close();
            TcpClient.Dispose();
        }
        catch
        {
            // ignored
        }

        GC.SuppressFinalize(this);
    }
}