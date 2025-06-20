# WebSocketClientWPF

## 项目简介

WebSocketClientWPF 是一个基于 WPF (.NET 9, C# 13) 的 WebSocket 客户端应用，支持自动重连、心跳检测、消息通知等功能。适用于需要与 WebSocket 服务端进行实时通信的桌面场景。

## 功能特性

- 支持 WebSocket 连接、断开、自动重连
- 心跳检测与超时自动重连
- 消息实时显示与本地通知弹窗
- 通过 HTTP 绑定 client_id
- 配置文件化，支持多环境切换
- 线程安全的 UI 更新
- 支持最大重连次数限制与指数退避

## 环境要求

- Windows 10/11
- .NET 9 SDK
- Visual Studio 2022 及以上
- 需配置 `appsettings.json`

## 快速开始

### 1. 克隆项目
```
git clone https://github.com/huqian520/webscoket-WPF.git
```

### 2. 配置参数

在项目根目录下编辑 `appsettings.json`：
```json
{
  "AppSettings": {
    "WebSocketUrl": "ws://your.websocket.server/ws",
    "BindUrl": "http://your.api.server/bind",
    "SecretId": "your-secret-id"
  }
}
```

- `WebSocketUrl`：WebSocket 服务端地址
- `BindUrl`：绑定 client_id 的 HTTP API 地址
- `SecretId`：鉴权密钥

### 3. 编译与运行

- 用 Visual Studio 2022 打开解决方案
- 还原 NuGet 包
- 编译并运行

## 使用说明

- **连接/断开**：点击"连接"按钮建立 WebSocket 连接，再次点击可断开
- **消息显示**：收到的消息会显示在主界面列表
- **通知弹窗**：重要消息会以弹窗形式提醒
- **清空消息**：点击"清空"按钮可清除消息列表
- **自动重连**：断线后自动重试，最大重连次数可配置

## 主要技术点

- `System.Net.WebSockets.ClientWebSocket` 实现 WebSocket 通信
- `DispatcherTimer` 实现心跳检测
- `System.Text.Json` 处理 JSON 消息
- `HttpClient` 实现 HTTP 绑定
- WPF MVVM 部分思想，线程安全 UI 操作

## 常见问题

- **连接失败/重连失败**：请检查 WebSocket 服务端地址、网络连通性及配置文件参数
- **绑定失败**：请检查 BindUrl 和 SecretId 是否正确
- **心跳丢失频繁**：请检查服务端心跳机制和网络状况

## 许可证

本项目遵循 MIT License。

## 联系方式

如有问题或建议，请提交 Issue 或联系作者。