using System.Net;
// ReSharper disable InconsistentNaming

namespace Udp2Tcp2UdpGameProxy;

public static class Program
{
    public static async Task Main(string[] args)
    {
        Console.Title = "异源导转 - UDP转TCP转UDP工具";

        if (args.Length < 1)
        {
            Console.WriteLine("""
                              使用方法:
                              服务端模式 (运行在VPS):
                                Udp2Tcp2UdpGameProxy.exe server <TCP监听IP> <TCP监听端口> <UDP游戏服务器IP> <UDP游戏服务器端口>
                                示例: Udp2Tcp2UdpGameProxy.exe server 0.0.0.0 7778 127.0.0.1 7777
                                
                              客户端模式 (运行在玩家本地):
                                Udp2Tcp2UdpGameProxy.exe client <本地UDP监听IP> <本地UDP监听端口> <VPS的TCP IP> <VPS的TCP端口>
                                示例: Udp2Tcp2UdpGameProxy.exe client 127.0.0.1 7777 your-vps-ip 7778
                                
                              使用流程:
                              1. 在VPS上运行服务端模式
                              2. 玩家本地运行客户端模式
                              3. 游戏客户端连接到本地UDP端口(如127.0.0.1:7777)
                              """);
            return;
        }

        var mode = args[0].ToLower();
        
        using var cancellationTokenSource = new CancellationTokenSource();
        
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellationTokenSource.Cancel();
        };

        try
        {
            switch (mode)
            {
                case "server" when args.Length >= 5:
                {
                    var tcpListenIp = IPAddress.Parse(args[1]);
                    var tcpListenPort = int.Parse(args[2]);
                    var udpGameServerIp = IPAddress.Parse(args[3]);
                    var udpGameServerPort = int.Parse(args[4]);

                    var tcpListenEndPoint = new IPEndPoint(tcpListenIp, tcpListenPort);
                    var udpGameServerEndPoint = new IPEndPoint(udpGameServerIp, udpGameServerPort);

                    using var server = new TcpToUdpServer(tcpListenEndPoint, udpGameServerEndPoint);
                    await server.StartAsync(cancellationTokenSource.Token);
                    break;
                }
                case "client" when args.Length >= 5:
                {
                    var udpLocalIp = IPAddress.Parse(args[1]);
                    var udpLocalPort = int.Parse(args[2]);
                    var tcpRemoteIp = IPAddress.Parse(args[3]);
                    var tcpRemotePort = int.Parse(args[4]);

                    var udpLocalEndPoint = new IPEndPoint(udpLocalIp, udpLocalPort);
                    var tcpRemoteEndPoint = new IPEndPoint(tcpRemoteIp, tcpRemotePort);

                    using var client = new UdpToTcpClient(udpLocalEndPoint, tcpRemoteEndPoint);
                    await client.StartAsync(cancellationTokenSource.Token);
                    break;
                }
                default:
                    Console.WriteLine("参数错误");
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("应用程序已停止");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"启动失败: {ex.Message}");
            Console.WriteLine($"错误详情: {ex}");
        }
        
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}