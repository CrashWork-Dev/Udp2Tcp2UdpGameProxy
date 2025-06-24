# 异源导转 —— UDP游戏客户端以TCP连接UDP游戏服务端工具
此工具用于解决我的朋友/客户买了我家BGP的VPS，但是VPS封锁了UDP连接所以玩不了大部分UDP协议的联机游戏的问题。

工作流程 : **玩家游戏客户端(UDP) → 本地客户端程序(UDP转TCP) → VPS服务端程序(TCP转UDP) → UDP游戏服务器**

## 使用方法(以泰拉瑞亚7777为例)

### 服务端程序（VPS上运行）
监听TCP 7778端口，转发到本地UDP游戏服务器7777端口

Windows : `./Udp2Tcp2UdpGameProxy.exe server 0.0.0.0 7778 127.0.0.1 7777`

Linux : `./Udp2Tcp2UdpGameProxy server 0.0.0.0 7778 127.0.0.1 7777`

### 客户端程序（玩家本地运行）
本地监听UDP 7777端口，连接到VPS的TCP 7778端口

Windows : `Udp2Tcp2UdpGameProxy.exe client 127.0.0.1 7777 your-vps-ip 7778`

Linux : `Udp2Tcp2UdpGameProxy client 127.0.0.1 7777 your-vps-ip 7778`

游戏连接服务器到 : `127.0.01:7777`