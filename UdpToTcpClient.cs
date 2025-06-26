using System.Net;
using System.Net.Sockets;
// ReSharper disable InconsistentNaming

namespace Udp2Tcp2UdpGameProxy;

internal class UdpToTcpClient(IPEndPoint localEndPoint, IPEndPoint remoteEndPoint) : IDisposable
{
    private readonly IPEndPoint _udpLocalEndPoint = localEndPoint ?? throw new ArgumentNullException(nameof(localEndPoint));
    private readonly IPEndPoint _tcpRemoteEndPoint = remoteEndPoint ?? throw new ArgumentNullException(nameof(remoteEndPoint));
    private UdpClient? _udpServer;
    private TcpClient? _tcpClient;
    private NetworkStream? _tcpStream;
    private bool _disposed;
    
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(3);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!_disposed)
            try
            {
                _udpServer = new UdpClient(_udpLocalEndPoint);

                Console.WriteLine("UDP To TCP 客户端启动");
                Console.WriteLine($"本地UDP监听: {_udpLocalEndPoint}");
                Console.WriteLine($"远程TCP服务器: {_tcpRemoteEndPoint}");
                Console.WriteLine("等待游戏客户端连接...\n");

                await ConnectToTcpServerAsync(cancellationToken);
                
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var token = linkedCts.Token;

                var tasks = new[]
                {
                    ReceiveUdpPacketsAsync(token),
                    ReceiveTcpDataAsync(token)
                };

                try
                {
                    await Task.WhenAny(tasks);
                }
                finally
                {
                    await linkedCts.CancelAsync();
                    await Task.WhenAll(tasks.Select(async t =>
                    {
                        try
                        {
                            await t.WaitAsync(TimeSpan.FromSeconds(5), token);
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
                Console.WriteLine("客户端已停止");
            }
            finally
            {
                await CleanupAsync();
            }
        else
            throw new ObjectDisposedException(nameof(UdpToTcpClient));
    }

    private async Task ConnectToTcpServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await DisposeTcpConnectionAsync();
                
                _tcpClient = new TcpClient();
                
                using var timeoutCts = new CancellationTokenSource(ConnectionTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                
                await _tcpClient.ConnectAsync(_tcpRemoteEndPoint.Address, _tcpRemoteEndPoint.Port, linkedCts.Token);
                _tcpStream = _tcpClient.GetStream();
                
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 已连接到TCP服务器: {_tcpRemoteEndPoint}");
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 连接TCP服务器失败: {ex.Message}，{ReconnectDelay.TotalSeconds}秒后重试...");
                
                await DisposeTcpConnectionAsync();
                
                try
                {
                    await Task.Delay(ReconnectDelay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task ReceiveUdpPacketsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_udpServer == null) break;
                
                var result = await _udpServer.ReceiveAsync(cancellationToken);
                var clientEndPoint = result.RemoteEndPoint;
                var data = result.Buffer;

                if (_tcpStream != null && _tcpClient?.Connected == true)
                {
                    await SendToTcpAsync(clientEndPoint, data, cancellationToken);
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] UDP->TCP: {clientEndPoint} ({data.Length} bytes)");
                }
                else
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] TCP连接断开，尝试重连...");
                    await ConnectToTcpServerAsync(cancellationToken);
                }
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
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] UDP接收错误: {ex.Message}");
            }
        }
    }

    private async Task SendToTcpAsync(IPEndPoint clientEndPoint, byte[] data, CancellationToken cancellationToken)
    {
        var clientInfo = $"{clientEndPoint.Address}:{clientEndPoint.Port}";
        var clientInfoBytes = System.Text.Encoding.UTF8.GetBytes(clientInfo);
        var clientInfoLength = BitConverter.GetBytes(clientInfoBytes.Length);
        var dataLength = BitConverter.GetBytes(data.Length);
        
        await _tcpStream!.WriteAsync(clientInfoLength.AsMemory(0, 4), cancellationToken);
        await _tcpStream.WriteAsync(clientInfoBytes.AsMemory(), cancellationToken);
        await _tcpStream.WriteAsync(dataLength.AsMemory(0, 4), cancellationToken);
        await _tcpStream.WriteAsync(data.AsMemory(), cancellationToken);
        await _tcpStream.FlushAsync(cancellationToken);
    }

    private async Task ReceiveTcpDataAsync(CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[4];
        
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_tcpStream == null || _tcpClient?.Connected != true)
                {
                    await Task.Delay(1000, cancellationToken);
                    continue;
                }
                await ProcessTcpMessageAsync(lengthBuffer, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (EndOfStreamException)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] TCP连接被远程关闭");
                await ConnectToTcpServerAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] TCP接收错误: {ex.Message}");
                await ConnectToTcpServerAsync(cancellationToken);
            }
        }
    }

    private async Task ProcessTcpMessageAsync(byte[] lengthBuffer, CancellationToken cancellationToken)
    {
        await ReadExactAsync(_tcpStream!, lengthBuffer, 4, cancellationToken);
        var clientInfoLength = BitConverter.ToInt32(lengthBuffer, 0);

        if (clientInfoLength is <= 0 or > 1024)
            throw new InvalidDataException($"无效的客户端信息长度: {clientInfoLength}");
        
        var clientInfoBytes = new byte[clientInfoLength];
        await ReadExactAsync(_tcpStream!, clientInfoBytes, clientInfoLength, cancellationToken);
        var clientInfo = System.Text.Encoding.UTF8.GetString(clientInfoBytes);
        
        await ReadExactAsync(_tcpStream!, lengthBuffer, 4, cancellationToken);
        var dataLength = BitConverter.ToInt32(lengthBuffer, 0);

        switch (dataLength)
        {
            case < 0 or > 65507:
                throw new InvalidDataException($"无效的数据长度: {dataLength}");
            case > 0:
            {
                var data = new byte[dataLength];
                await ReadExactAsync(_tcpStream!, data, dataLength, cancellationToken);

                var parts = clientInfo.Split(':');
                if (parts.Length == 2 && 
                    IPAddress.TryParse(parts[0], out var ip) && 
                    int.TryParse(parts[1], out var port))
                {
                    var targetEndPoint = new IPEndPoint(ip, port);
                    await _udpServer!.SendAsync(data, data.Length, targetEndPoint);
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] TCP->UDP: {targetEndPoint} ({data.Length} bytes)");
                }

                break;
            }
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

    private async Task DisposeTcpConnectionAsync()
    {
        if (_tcpStream != null)
        {
            try
            {
                await _tcpStream.DisposeAsync();
            }
            catch
            {
                // ignored
            }
            _tcpStream = null;
        }

        if (_tcpClient != null)
        {
            try
            {
                _tcpClient.Close();
                _tcpClient.Dispose();
            }
            catch
            {
                // ignored
            }
            _tcpClient = null;
        }
    }

    private async Task CleanupAsync()
    {
        await DisposeTcpConnectionAsync();
        
        if (_udpServer != null)
        {
            try
            {
                _udpServer.Close();
                _udpServer.Dispose();
            }
            catch
            {
                // ignored
            }
            _udpServer = null;
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