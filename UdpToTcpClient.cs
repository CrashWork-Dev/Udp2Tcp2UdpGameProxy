using System.Net;
using System.Net.Sockets;

namespace Udp2Tcp2UdpGameProxy;

internal class UdpToTcpClient(IPEndPoint udpLocalEndPoint, IPEndPoint tcpRemoteEndPoint)
{
    private UdpClient? _udpServer;
    private TcpClient? _tcpClient;
    private NetworkStream? _tcpStream;
    private CancellationTokenSource? _cancellationTokenSource;

    public async Task StartAsync()
    {
        _cancellationTokenSource = new CancellationTokenSource();
        _udpServer = new UdpClient(udpLocalEndPoint);

        Console.WriteLine("UDP To TCP 客户端启动");
        Console.WriteLine($"本地UDP监听: {udpLocalEndPoint}");
        Console.WriteLine($"远程TCP服务器: {tcpRemoteEndPoint}");
        Console.WriteLine("等待游戏客户端连接...\n");

        try
        {
            await ConnectToTcpServerAsync();
            await Task.WhenAll(
                ReceiveUdpPacketsAsync(_cancellationTokenSource.Token),
                ReceiveTcpDataAsync(_cancellationTokenSource.Token)
            );
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("客户端已停止");
        }
    }

    private async Task ConnectToTcpServerAsync()
    {
        while (_cancellationTokenSource?.IsCancellationRequested == false)
        {
            try
            {
                _tcpClient = new TcpClient();
                await _tcpClient.ConnectAsync(tcpRemoteEndPoint.Address, tcpRemoteEndPoint.Port);
                _tcpStream = _tcpClient.GetStream();
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 已连接到TCP服务器: {tcpRemoteEndPoint}");
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 连接TCP服务器失败: {ex.Message}，3秒后重试...");
                await Task.Delay(3000);
            }
        }
    }

    private async Task ReceiveUdpPacketsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await _udpServer!.ReceiveAsync(cancellationToken);
                var clientEndPoint = result.RemoteEndPoint;
                var data = result.Buffer;

                // 构造包头：客户端IP:端口信息
                var clientInfo = $"{clientEndPoint.Address}:{clientEndPoint.Port}";
                var clientInfoBytes = System.Text.Encoding.UTF8.GetBytes(clientInfo);
                var clientInfoLength = BitConverter.GetBytes(clientInfoBytes.Length);
                var dataLength = BitConverter.GetBytes(data.Length);

                // 发送格式：[ClientInfoLength:4][ClientInfo][DataLength:4][Data]
                if (_tcpStream != null && _tcpClient?.Connected == true)
                {
                    await _tcpStream.WriteAsync(clientInfoLength.AsMemory(0, 4), cancellationToken);
                    await _tcpStream.WriteAsync(clientInfoBytes, cancellationToken);
                    await _tcpStream.WriteAsync(dataLength.AsMemory(0, 4), cancellationToken);
                    await _tcpStream.WriteAsync(data, cancellationToken);
                    await _tcpStream.FlushAsync(cancellationToken);

                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] UDP->TCP: {clientEndPoint} ({data.Length} bytes)");
                }
                else
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] TCP连接断开，尝试重连...");
                    await ConnectToTcpServerAsync();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] UDP接收错误: {ex.Message}");
                if (ex is ObjectDisposedException) break;
            }
        }
    }

    private async Task ReceiveTcpDataAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_tcpStream == null || _tcpClient?.Connected != true)
                {
                    await Task.Delay(1000, cancellationToken);
                    continue;
                }

                // 读取客户端信息长度
                var clientInfoLengthBytes = new byte[4];
                await ReadExactAsync(_tcpStream, clientInfoLengthBytes, 4, cancellationToken);
                var clientInfoLength = BitConverter.ToInt32(clientInfoLengthBytes, 0);

                // 读取客户端信息
                var clientInfoBytes = new byte[clientInfoLength];
                await ReadExactAsync(_tcpStream, clientInfoBytes, clientInfoLength, cancellationToken);
                var clientInfo = System.Text.Encoding.UTF8.GetString(clientInfoBytes);

                // 读取数据长度
                var dataLengthBytes = new byte[4];
                await ReadExactAsync(_tcpStream, dataLengthBytes, 4, cancellationToken);
                var dataLength = BitConverter.ToInt32(dataLengthBytes, 0);

                // 读取数据
                var data = new byte[dataLength];
                await ReadExactAsync(_tcpStream, data, dataLength, cancellationToken);

                // 解析客户端地址并转发UDP数据
                var parts = clientInfo.Split(':');
                if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) ||
                    !int.TryParse(parts[1], out var port)) continue;
                var targetEndPoint = new IPEndPoint(ip, port);
                await _udpServer!.SendAsync(data, data.Length, targetEndPoint);
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] TCP->UDP: {targetEndPoint} ({data.Length} bytes)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] TCP接收错误: {ex.Message}");
                if (ex is ObjectDisposedException) break;
                    
                await ConnectToTcpServerAsync();
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
}