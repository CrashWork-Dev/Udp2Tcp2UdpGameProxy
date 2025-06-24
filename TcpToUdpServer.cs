using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Udp2Tcp2UdpGameProxy;

public class TcpToUdpServer(IPEndPoint tcpListenEndPoint, IPEndPoint udpGameServerEndPoint)
{
    private TcpListener? _tcpListener;
    private UdpClient? _udpClient;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly ConcurrentDictionary<string, TcpClient> _clientConnections = new();
    private readonly ConcurrentDictionary<string, string> _clientMapping = new();

    public async Task StartAsync()
    {
        _cancellationTokenSource = new CancellationTokenSource();
        _tcpListener = new TcpListener(tcpListenEndPoint);
            
        var udpLocalEndPoint = new IPEndPoint(IPAddress.Any, 0);
        _udpClient = new UdpClient(udpLocalEndPoint);
            
        _tcpListener.Start();

        Console.WriteLine("UDP To TCP 服务端启动");
        Console.WriteLine($"TCP监听: {tcpListenEndPoint}");
        Console.WriteLine($"UDP游戏服务器: {udpGameServerEndPoint}");
        Console.WriteLine($"UDP本地绑定: {_udpClient.Client.LocalEndPoint}");
        Console.WriteLine("等待客户端连接...\n");

        _ = Task.Run(async () => await HandleUdpResponsesAsync(_cancellationTokenSource.Token), _cancellationTokenSource.Token);

        try
        {
            await AcceptTcpConnectionsAsync(_cancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("服务端已停止");
        }
    }

    private async Task AcceptTcpConnectionsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var tcpClient = await _tcpListener!.AcceptTcpClientAsync(cancellationToken);
                var clientKey = tcpClient.Client.RemoteEndPoint?.ToString() ?? Guid.NewGuid().ToString();
                    
                _clientConnections[clientKey] = tcpClient;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 客户端连接: {clientKey}");

                _ = Task.Run(async () =>
                {
                    await HandleTcpClientAsync(tcpClient, clientKey, cancellationToken);
                }, cancellationToken);
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

    private async Task HandleTcpClientAsync(TcpClient tcpClient, string clientKey, CancellationToken cancellationToken)
    {
        try
        {
            var stream = tcpClient.GetStream();

            while (!cancellationToken.IsCancellationRequested && tcpClient.Connected)
            {
                // 读取客户端信息长度
                var clientInfoLengthBytes = new byte[4];
                await ReadExactAsync(stream, clientInfoLengthBytes, 4, cancellationToken);
                var clientInfoLength = BitConverter.ToInt32(clientInfoLengthBytes, 0);

                // 读取客户端信息
                var clientInfoBytes = new byte[clientInfoLength];
                await ReadExactAsync(stream, clientInfoBytes, clientInfoLength, cancellationToken);
                var clientInfo = System.Text.Encoding.UTF8.GetString(clientInfoBytes);

                // 保存客户端映射关系
                _clientMapping[clientKey] = clientInfo;

                // 读取数据长度
                var dataLengthBytes = new byte[4];
                await ReadExactAsync(stream, dataLengthBytes, 4, cancellationToken);
                var dataLength = BitConverter.ToInt32(dataLengthBytes, 0);

                // 读取数据
                var data = new byte[dataLength];
                await ReadExactAsync(stream, data, dataLength, cancellationToken);

                // 转发到UDP游戏服务器
                await _udpClient!.SendAsync(data, data.Length, udpGameServerEndPoint);
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] TCP->UDP: {clientInfo} -> 游戏服务器 ({data.Length} bytes)");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 处理客户端错误 {clientKey}: {ex.Message}");
        }
        finally
        {
            tcpClient.Close();
            _clientConnections.TryRemove(clientKey, out _);
            _clientMapping.TryRemove(clientKey, out _);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 客户端断开: {clientKey}");
        }
    }

    private async Task HandleUdpResponsesAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await _udpClient!.ReceiveAsync(cancellationToken);
                var data = result.Buffer;
                var sourceEndPoint = result.RemoteEndPoint;

                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] UDP响应: {sourceEndPoint} -> 准备转发 ({data.Length} bytes)");
                    
                var forwardedCount = 0;
                foreach (var (clientKey, tcpClient) in _clientConnections)
                {
                    if (!tcpClient.Connected || !_clientMapping.TryGetValue(clientKey, out var originalClientInfo))
                        continue;
                    try
                    {
                        var clientInfoBytes = System.Text.Encoding.UTF8.GetBytes(originalClientInfo);
                        var clientInfoLength = BitConverter.GetBytes(clientInfoBytes.Length);
                        var dataLength = BitConverter.GetBytes(data.Length);

                        var stream = tcpClient.GetStream();
                        await stream.WriteAsync(clientInfoLength.AsMemory(0, 4), cancellationToken);
                        await stream.WriteAsync(clientInfoBytes, cancellationToken);
                        await stream.WriteAsync(dataLength.AsMemory(0, 4), cancellationToken);
                        await stream.WriteAsync(data, cancellationToken);
                        await stream.FlushAsync(cancellationToken);
                                
                        forwardedCount++;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 发送响应失败 {clientKey}: {ex.Message}");
                    }
                }

                if (forwardedCount > 0)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] UDP->TCP: 游戏服务器 -> {forwardedCount}个客户端 ({data.Length} bytes)");
                }
                else
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 警告: 没有活跃的客户端接收响应");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] UDP响应处理错误: {ex.Message}");
                    
                if (ex.Message.Contains("Bind"))
                {
                    try
                    {
                        _udpClient?.Close();
                        var udpLocalEndPoint = new IPEndPoint(IPAddress.Any, 0);
                        _udpClient = new UdpClient(udpLocalEndPoint);
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] UDP客户端重新创建: {_udpClient.Client.LocalEndPoint}");
                    }
                    catch (Exception recreateEx)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 重新创建UDP客户端失败: {recreateEx.Message}");
                    }
                }
            }
        }
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < count)
        {
            var read = await stream.ReadAsync(buffer, totalRead, count - totalRead, cancellationToken);
            if (read == 0) throw new EndOfStreamException("连接已关闭");
            totalRead += read;
        }
    }
}