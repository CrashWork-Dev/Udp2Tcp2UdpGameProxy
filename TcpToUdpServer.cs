using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
// ReSharper disable InconsistentNaming

namespace Udp2Tcp2UdpGameProxy;

public class TcpToUdpServer(IPEndPoint listenEndPoint, IPEndPoint gameServerEndPoint) : IDisposable
{
    private readonly IPEndPoint _tcpListenEndPoint = listenEndPoint ?? throw new ArgumentNullException(nameof(listenEndPoint));
    private readonly IPEndPoint _udpGameServerEndPoint = gameServerEndPoint ?? throw new ArgumentNullException(nameof(gameServerEndPoint));
    private TcpListener? _tcpListener;
    private UdpClient? _udpClient;
    
    private readonly ConcurrentDictionary<string, ClientConnection> _clientConnections = new();
    private bool _disposed;
    
    private Timer? _cleanupTimer;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(1);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!_disposed)
            try
            {
                _tcpListener = new TcpListener(_tcpListenEndPoint);
                var udpLocalEndPoint = new IPEndPoint(IPAddress.Any, 0);
                _udpClient = new UdpClient(udpLocalEndPoint);

                _tcpListener.Start();
                
                _cleanupTimer = new Timer(CleanupDeadConnections, null, CleanupInterval, CleanupInterval);

                Console.WriteLine("TCP To UDP 服务端启动");
                Console.WriteLine($"TCP监听: {_tcpListenEndPoint}");
                Console.WriteLine($"UDP游戏服务器: {_udpGameServerEndPoint}");
                Console.WriteLine($"UDP本地绑定: {_udpClient.Client.LocalEndPoint}");
                Console.WriteLine("等待客户端连接...\n");

                var udpTask = HandleUdpResponsesAsync(cancellationToken);
                var tcpTask = AcceptTcpConnectionsAsync(cancellationToken);

                try
                {
                    await Task.WhenAny(udpTask, tcpTask);
                }
                finally
                {
                    var allTasks = new[] { udpTask, tcpTask };
                    await Task.WhenAll(allTasks.Select(async t =>
                    {
                        try
                        {
                            await t.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                        }
                        catch
                        {
                            // ignored
                        }
                    }));
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("服务端已停止");
            }
            finally
            {
                await CleanupAsync();
            }
        else
            throw new ObjectDisposedException(nameof(TcpToUdpServer));
    }

    private async Task AcceptTcpConnectionsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _tcpListener != null)
        {
            try
            {
                var tcpClient = await _tcpListener.AcceptTcpClientAsync(cancellationToken);
                var clientKey = tcpClient.Client.RemoteEndPoint?.ToString() ?? Guid.NewGuid().ToString();
                
                var clientConnection = new ClientConnection(tcpClient, clientKey);
                _clientConnections[clientKey] = clientConnection;
                
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 客户端连接: {clientKey}");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await HandleTcpClientAsync(clientConnection, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 客户端处理异常 {clientKey}: {ex.Message}");
                    }
                    finally
                    {
                        _clientConnections.TryRemove(clientKey, out _);
                        await clientConnection.DisposeAsync();
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 客户端断开: {clientKey}");
                    }
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 接受连接错误: {ex.Message}");
            }
        }
    }

    private async Task HandleTcpClientAsync(ClientConnection clientConnection, CancellationToken cancellationToken)
    {
        var tcpClient = clientConnection.TcpClient;
        var clientKey = clientConnection.ClientKey;
        try
        {
            var stream = tcpClient.GetStream();
            var lengthBuffer = new byte[4];

            while (!cancellationToken.IsCancellationRequested && tcpClient.Connected)
            {
                // 读取客户端信息长度
                await ReadExactAsync(stream, lengthBuffer, 4, cancellationToken);
                var clientInfoLength = BitConverter.ToInt32(lengthBuffer, 0);

                if (clientInfoLength <= 0 || clientInfoLength > 1024)
                    throw new InvalidDataException($"无效的客户端信息长度: {clientInfoLength}");

                // 读取客户端信息
                var clientInfoBytes = new byte[clientInfoLength];
                await ReadExactAsync(stream, clientInfoBytes, clientInfoLength, cancellationToken);
                var clientInfo = System.Text.Encoding.UTF8.GetString(clientInfoBytes);

                // 更新客户端映射关系
                clientConnection.UpdateClientInfo(clientInfo);

                // 读取数据长度
                await ReadExactAsync(stream, lengthBuffer, 4, cancellationToken);
                var dataLength = BitConverter.ToInt32(lengthBuffer, 0);

                switch (dataLength)
                {
                    case < 0:
                    case > 65507:
                        throw new InvalidDataException($"无效的数据长度: {dataLength}");
                    // 读取并转发数据
                    case <= 0:
                        continue;
                }

                var data = new byte[dataLength];
                await ReadExactAsync(stream, data, dataLength, cancellationToken);

                // 转发到UDP游戏服务器
                await _udpClient!.SendAsync(data, data.Length, _udpGameServerEndPoint);
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] TCP->UDP: {clientInfo} -> 游戏服务器 ({data.Length} bytes)");
            }
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 处理客户端错误 {clientKey}: {ex.Message}");
        }
    }

    private async Task HandleUdpResponsesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _udpClient != null)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync(cancellationToken);
                var data = result.Buffer;
                var sourceEndPoint = result.RemoteEndPoint;

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] UDP响应: {sourceEndPoint} -> 准备转发 ({data.Length} bytes)");

                var forwardedCount = 0;
                var deadConnections = new List<string>();
                
                foreach (var (clientKey, clientConnection) in _clientConnections)
                {
                    if (!clientConnection.IsConnected)
                    {
                        deadConnections.Add(clientKey);
                        continue;
                    }

                    var originalClientInfo = clientConnection.GetClientInfo();
                    if (string.IsNullOrEmpty(originalClientInfo)) continue;
                    try
                    {
                        await SendResponseToClient(clientConnection, originalClientInfo, data, cancellationToken);
                        forwardedCount++;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 发送响应失败 {clientKey}: {ex.Message}");
                        deadConnections.Add(clientKey);
                    }
                }
                
                foreach (var deadKey in deadConnections)
                {
                    if (_clientConnections.TryRemove(deadKey, out var deadConnection))
                    {
                        await deadConnection.DisposeAsync();
                    }
                }

                Console.WriteLine(forwardedCount > 0
                    ? $"[{DateTime.Now:HH:mm:ss}] UDP->TCP: 游戏服务器 -> {forwardedCount}个客户端 ({data.Length} bytes)"
                    : $"[{DateTime.Now:HH:mm:ss}] 警告: 没有活跃的客户端接收响应");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] UDP响应处理错误: {ex.Message}");
                
                if (ex.Message.Contains("Bind") || ex is ObjectDisposedException)
                {
                    await RecreateUdpClientAsync();
                }
            }
        }
    }

    private async Task SendResponseToClient(ClientConnection clientConnection, string originalClientInfo, byte[] data, CancellationToken cancellationToken)
    {
        var clientInfoBytes = System.Text.Encoding.UTF8.GetBytes(originalClientInfo);
        var clientInfoLength = BitConverter.GetBytes(clientInfoBytes.Length);
        var dataLength = BitConverter.GetBytes(data.Length);

        var stream = clientConnection.TcpClient.GetStream();
        await stream.WriteAsync(clientInfoLength.AsMemory(0, 4), cancellationToken);
        await stream.WriteAsync(clientInfoBytes.AsMemory(), cancellationToken);
        await stream.WriteAsync(dataLength.AsMemory(0, 4), cancellationToken);
        await stream.WriteAsync(data.AsMemory(), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private async Task RecreateUdpClientAsync()
    {
        try
        {
            _udpClient?.Close();
            _udpClient?.Dispose();
            
            var udpLocalEndPoint = new IPEndPoint(IPAddress.Any, 0);
            _udpClient = new UdpClient(udpLocalEndPoint);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] UDP客户端重新创建: {_udpClient.Client.LocalEndPoint}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 重新创建UDP客户端失败: {ex.Message}");
        }
    }

    private void CleanupDeadConnections(object? state)
    {
        var deadConnections = new List<string>();
        
        foreach (var (clientKey, clientConnection) in _clientConnections)
        {
            if (!clientConnection.IsConnected)
            {
                deadConnections.Add(clientKey);
            }
        }

        foreach (var deadKey in deadConnections)
        {
            if (!_clientConnections.TryRemove(deadKey, out var deadConnection)) continue;
            _ = Task.Run(async () => await deadConnection.DisposeAsync());
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 清理死连接: {deadKey}");
        }
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, count - totalRead), cancellationToken);
            if (read == 0) throw new EndOfStreamException("连接已关闭");
            totalRead += read;
        }
    }

    private async Task CleanupAsync()
    {
        _cleanupTimer?.Dispose();
        
        _tcpListener?.Stop();
        
        _udpClient?.Close();
        _udpClient?.Dispose();
        
        var allConnections = _clientConnections.Values.ToList();
        _clientConnections.Clear();
        
        foreach (var connection in allConnections)
        {
            await connection.DisposeAsync();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        
        _disposed = true;
        CleanupAsync().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }
}