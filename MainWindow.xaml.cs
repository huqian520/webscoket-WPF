using Microsoft.Extensions.Configuration;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace WebSocketClientWPF
{
    public class AppConfig
    {
        public  string WebSocketUrl { get; set; }
        public  string BindUrl { get; set; }

        public  string SecretId { get; set; }
    }

    public partial class MainWindow : Window
    {
        private ClientWebSocket _webSocket;
        private CancellationTokenSource _cancellationTokenSource;
        private bool _isConnected = false;
        private bool _isUserDisconnect = false;
        private bool _isClosing = false;
        private bool _isReconnecting = false; // 重连状态标志

        private ObservableCollection<NotificationWindow> _activeNotifications = new ObservableCollection<NotificationWindow>();

        private SolidColorBrush _connectedBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(46, 204, 113));
        private SolidColorBrush _disconnectedBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(231, 76, 60));
        private SolidColorBrush _reconnectingBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(241, 196, 15));

        private HttpClient _httpClient = new HttpClient();
        private object _notificationsLock = new object();
        private object _reconnectLock = new object();
        private object _heartbeatLock = new object();

        // 心跳检测相关字段
        private DispatcherTimer _heartbeatTimer;
        private DateTime _lastHeartbeatTime;
        private const int HeartbeatCheckInterval = 60000; // 60秒
        private const int HeartbeatTimeout = 90000; // 90秒（1.5倍心跳间隔）
        private const string HeartbeatMessageType = "@heart@";

        // 重连相关字段
        private string _url = "";
        private string _key = "";
        private string _bindUrl = "";
        private int _reconnectAttempts = 0;
        private const int MaxReconnectAttempts = 10; // 最大重连尝试次数

        public MainWindow()
        {
            InitializeComponent();
            InitializeHeartbeatTimer();
            UpdateConnectionStatus(ConnectionStatus.Disconnected);
            Title = "WebSocket 客户端";
          //  ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;

            // 在启动时加载配置
            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false);

            IConfiguration config = builder.Build();

            // 绑定到类
            AppConfig settings = config.GetSection("AppSettings").Get<AppConfig>();
            if (settings == null)
            {
                System.Windows.MessageBox.Show("配置文件缺少 AppSettings 节点或格式错误", "配置错误", MessageBoxButton.OK, MessageBoxImage.Error);
                System.Windows.Application.Current.Shutdown();
                return;
            }
            _bindUrl = settings.BindUrl ?? "";
            _url = settings.WebSocketUrl ?? "";
            _key = settings.SecretId ?? "";

        }

        private enum ConnectionStatus
        {
            Connected,
            Disconnected,
            Reconnecting
        }

        private void InitializeHeartbeatTimer()
        {
            _heartbeatTimer = new DispatcherTimer();
            _heartbeatTimer.Interval = TimeSpan.FromMilliseconds(HeartbeatCheckInterval);
            _heartbeatTimer.Tick += HeartbeatTimer_Tick;
        }

        private void UpdateConnectionStatus(ConnectionStatus status)
        {
            Dispatcher.Invoke(() =>
            {
                switch (status)
                {
                    case ConnectionStatus.Connected:
                        statusIndicator.Fill = _connectedBrush;
                        btnConnect.Content = "断开连接";
                        txtConnectionStatus.Text = "已连接";
                        txtConnectionStatus.Foreground = _connectedBrush;
                        break;
                    case ConnectionStatus.Disconnected:
                        statusIndicator.Fill = _disconnectedBrush;
                        btnConnect.Content = "连接";
                        txtConnectionStatus.Text = "未连接";
                        txtConnectionStatus.Foreground = _disconnectedBrush;
                        break;
                    case ConnectionStatus.Reconnecting:
                        statusIndicator.Fill = _reconnectingBrush;
                        btnConnect.Content = "停止重连";
                        txtConnectionStatus.Text = $"重连中 ({_reconnectAttempts}/{MaxReconnectAttempts})";
                        txtConnectionStatus.Foreground = _reconnectingBrush;
                        break;
                }
            });
        }

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            if (_isConnected || _isReconnecting)
            {
                _isUserDisconnect = true;
                await Disconnect();
                return;
            }

            _isUserDisconnect = false;
            await Connect();
        }

        private async Task Connect()
        {
            if (_isConnected) return;

            try
            {
                // 如果已经在重连中，不要重复启动连接过程
                lock (_reconnectLock)
                {
                    if (_isReconnecting) return;
                    _isReconnecting = true;
                }

                // 重置重连尝试次数
                _reconnectAttempts = 0;

                // 清理现有连接
                await CleanupConnection();

                // 创建新的WebSocket实例
                _webSocket = new ClientWebSocket();
                _cancellationTokenSource = new CancellationTokenSource();


                UpdateConnectionStatus(ConnectionStatus.Reconnecting);
                AddMessage($"正在连接到: {_url}");

                try
                {
                    // 使用带超时的连接
                    await ConnectWithTimeout(_url, 10000); // 10秒连接超时

                    _isConnected = true;
                    _isReconnecting = false;

                    // 更新心跳时间
                    lock (_heartbeatLock)
                    {
                        _lastHeartbeatTime = DateTime.Now;
                    }

                    _heartbeatTimer.Start(); // 启动心跳检测
                    UpdateConnectionStatus(ConnectionStatus.Connected);
                    AddMessage("连接成功，等待消息...");

                    // 启动接收消息任务
                    _ = Task.Run(() => ReceiveMessages(_cancellationTokenSource.Token));
                }
                catch (Exception ex)
                {
                    AddMessage($"连接失败: {ex.Message}");
                    ShowNotification("连接错误", ex.Message);

                    // 首次连接失败，启动自动重连流程
                    await AutoReconnect();
                }
            }
            catch (Exception ex)
            {
                AddMessage($"连接初始化失败: {ex.Message}");
                ShowNotification("连接错误", ex.Message);
                _isReconnecting = false;
                UpdateConnectionStatus(ConnectionStatus.Disconnected);
            }
        }

        private async Task ConnectWithTimeout(string url, int timeoutMs)
        {
            var connectTask = _webSocket.ConnectAsync(new Uri(url), _cancellationTokenSource.Token);
            var delayTask = Task.Delay(timeoutMs, _cancellationTokenSource.Token);

            var completedTask = await Task.WhenAny(connectTask, delayTask);

            if (completedTask == delayTask)
            {
                // 连接超时
                throw new TimeoutException($"连接超时 ({timeoutMs}ms)");
            }

            // 确保连接任务完成
            await connectTask;
        }

        private async Task ReceiveMessages(CancellationToken token)
        {
            var buffer = new byte[4096];
            var messageBuilder = new StringBuilder();

            try
            {
                while (!token.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
                {
                    var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), token);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        AddMessage("服务器主动关闭连接");
                        await HandleDisconnection(false);
                        return;
                    }

                    string received = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    messageBuilder.Append(received);

                    if (result.EndOfMessage)
                    {
                        string fullMessage = messageBuilder.ToString();
                        messageBuilder.Clear();

                        ProcessMessage(fullMessage);

                        // 解析order的message
                        var row = JsonNode.Parse(fullMessage);
                        if (row["type"].ToString() == "order") {
                            Dispatcher.Invoke(() => {
                                AddMessage(row["message"].ToString());
                                ShowNotification("新消息", row["message"].ToString());
                            });
                        }
                    }
                }
            }
            catch (WebSocketException wsEx)
            {
                if (!token.IsCancellationRequested)
                {
                    Dispatcher.Invoke(() => {
                        AddMessage($"WebSocket错误: {wsEx.Message}");
                        ShowNotification("连接错误", wsEx.Message);
                    });

                    await HandleDisconnection(false);
                }
            }
            catch (OperationCanceledException)
            {
                // 用户取消操作，正常退出
                AddMessage("接收操作已取消");
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    Dispatcher.Invoke(() => {
                        AddMessage($"接收错误: {ex.Message}");
                        ShowNotification("接收错误", ex.Message);
                    });
                    await HandleDisconnection(false);
                }
            }
        }

        private void ProcessMessage(string message)
        {
            try
            {
                var jsonDoc = JsonDocument.Parse(message);
                var root = jsonDoc.RootElement;

                if (root.TryGetProperty("type", out var typeElement) &&
                    typeElement.ValueKind == JsonValueKind.String)
                {
                    string messageType = typeElement.GetString();

                    // 检测心跳消息
                    if (messageType == HeartbeatMessageType)
                    {
                        // 使用锁保护心跳状态更新
                        lock (_heartbeatLock)
                        {
                            _lastHeartbeatTime = DateTime.Now;
                        }

                        // 使用Dispatcher安全添加消息
                        //Dispatcher.Invoke(() => AddMessage("收到心跳消息"));
                        return;
                    }

                    if (messageType == "init" &&
                        root.TryGetProperty("client_id", out var clientIdElement) &&
                        clientIdElement.ValueKind == JsonValueKind.String)
                    {
                        string clientId = clientIdElement.GetString();

                        _ = Task.Run(() => SendBindRequest(clientId));
                    }
                }
            }
            catch (JsonException)
            {
                // 忽略非JSON消息
            }
            catch (Exception ex)
            {
                // 使用Dispatcher安全添加错误消息
                Dispatcher.Invoke(() => {
                    AddMessage($"消息处理错误: {ex.Message}");
                    ShowNotification("处理错误", ex.Message);
                });
            }
        }

        private async Task SendBindRequest(string clientId)
        {
            try
            {
                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("client_id", clientId),
                    new KeyValuePair<string, string>("secret_id", _key)
                });

                var response = await _httpClient.PostAsync(_bindUrl, content);

                // 使用Dispatcher安全更新UI
                _ = Dispatcher.Invoke(async () =>
                {
                    if (response.IsSuccessStatusCode)
                    {
                        var responseContent = await response.Content.ReadAsStringAsync();
                        var jsonNode = JsonNode.Parse(responseContent);
                        AddMessage(jsonNode["message"].ToString());
                        if ((int)jsonNode["status"] != 1)
                        {
                            // 手动断开连接
                            await HandleDisconnection(true);
                        }
                    }
                    else
                    {
                        AddMessage($"绑定请求失败: {response.StatusCode}");
                        ShowNotification("绑定失败", $"状态码: {response.StatusCode}");
                    }
                });
            }
            catch (Exception ex)
            {
                // 使用Dispatcher安全添加错误消息
                Dispatcher.Invoke(() => {
                    AddMessage($"绑定请求错误: {ex.Message}");
                    ShowNotification("绑定错误", ex.Message);
                });
            }
        }

        private void AddMessage(string message)
        {
            Dispatcher.Invoke(() =>
            {
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                lstMessages.Items.Add(new { Time = timestamp, Content = message });
                lstMessages.ScrollIntoView(lstMessages.Items[lstMessages.Items.Count - 1]);
            });
        }

        private async Task HandleDisconnection(bool isUserAction)
        {
            // 重要：确保只处理一次断开连接
            if (!_isConnected && !_isReconnecting) return;

            try
            {
                AddMessage("连接已断开");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AddMessage failed: {ex.Message}");
            }

            // 确保清理连接总是执行
            try
            {
                await CleanupConnection();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CleanupConnection failed: {ex.Message}");
                try
                {
                    AddMessage($"清理连接时出错: {ex.Message}");
                }
                catch { }
            }

            // 如果不是用户主动断开，尝试自动重连
            if (!isUserAction && !_isUserDisconnect && !_isClosing)
            {
                try
                {
                    await AutoReconnect();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"AutoReconnect failed: {ex.Message}");
                    try
                    {
                        AddMessage($"自动重连失败: {ex.Message}");
                    }
                    catch { }
                }
            }
        }

        private async Task AutoReconnect()
        {
            try
            {
                // 确保不是首次连接尝试
                if (_reconnectAttempts == 0)
                {
                    _reconnectAttempts = 1;
                }

                while (_reconnectAttempts < MaxReconnectAttempts && !_isClosing && !_isUserDisconnect)
                {
                    // 指数退避等待
                    int delay = (int)Math.Min(2000 * Math.Pow(2, _reconnectAttempts - 1), 30000);

                    Dispatcher.Invoke(() => {
                        UpdateConnectionStatus(ConnectionStatus.Reconnecting);
                        AddMessage($"等待 {delay}ms 后尝试重连 ({_reconnectAttempts}/{MaxReconnectAttempts})...");
                    });

                    await Task.Delay(delay);

                    _reconnectAttempts++;

                    try
                    {
                        // 清理之前的连接
                        await CleanupConnection();

                        // 创建新的WebSocket实例
                        _webSocket = new ClientWebSocket();
                        _cancellationTokenSource = new CancellationTokenSource();


                        Dispatcher.Invoke(() => {
                            AddMessage($"尝试重连 ({_reconnectAttempts}/{MaxReconnectAttempts})...");
                        });

                        // 使用带超时的连接
                        await ConnectWithTimeout(_url, 10000); // 10秒连接超时

                        _isConnected = true;
                        _isReconnecting = false;

                        // 更新心跳时间
                        lock (_heartbeatLock)
                        {
                            _lastHeartbeatTime = DateTime.Now;
                        }

                        _heartbeatTimer.Start(); // 启动心跳检测

                        Dispatcher.Invoke(() => {
                            UpdateConnectionStatus(ConnectionStatus.Connected);
                            AddMessage("重连成功，等待消息...");
                        });

                        // 启动接收消息任务
                        _ = Task.Run(() => ReceiveMessages(_cancellationTokenSource.Token));

                        break; // 连接成功，退出重连循环
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() => {
                            AddMessage($"重连失败: {ex.Message}");
                        });

                        // 继续下一次重连尝试
                    }
                }

                if (!_isConnected && !_isClosing)
                {
                    Dispatcher.Invoke(() => {
                        AddMessage($"已达到最大重连次数({MaxReconnectAttempts})，停止重连");
                        ShowNotification("重连失败", "已达到最大重连次数，请检查网络连接");
                        UpdateConnectionStatus(ConnectionStatus.Disconnected);
                    });
                }
            }
            finally
            {
                if (!_isConnected)
                {
                    _isReconnecting = false;
                    Dispatcher.Invoke(() => {
                        UpdateConnectionStatus(ConnectionStatus.Disconnected);
                    });
                }
            }
        }

        private async Task CleanupConnection()
        {
            try
            {
                _isConnected = false;
                _heartbeatTimer.Stop(); // 停止心跳检测

                // 1. 先取消所有操作
                _cancellationTokenSource?.Cancel();

                // 2. 关闭WebSocket连接
                if (_webSocket != null)
                {
                    try
                    {
                        // 检查WebSocket状态
                        if (_webSocket.State == WebSocketState.Open ||
                            _webSocket.State == WebSocketState.Connecting)
                        {
                            // 使用独立的CancellationToken，避免被已取消的令牌影响
                            using (var cts = new CancellationTokenSource(3000)) // 3秒超时
                            {
                                await _webSocket.CloseAsync(
                                    WebSocketCloseStatus.NormalClosure,
                                    "Client closing",
                                    cts.Token);
                            }
                        }
                    }
                    catch (WebSocketException wsEx)
                    {
                        Debug.WriteLine($"WebSocket关闭错误: {wsEx.Message}");
                    }
                    catch (OperationCanceledException)
                    {
                        Debug.WriteLine("WebSocket关闭操作超时");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"WebSocket关闭异常: {ex.Message}");
                    }
                    finally
                    {
                        try
                        {
                            _webSocket.Dispose();
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"WebSocket释放错误: {ex.Message}");
                        }
                        _webSocket = null;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CleanupConnection错误: {ex.Message}");
            }
            finally
            {
                // 3. 释放CancellationTokenSource
                try
                {
                    _cancellationTokenSource?.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"CancellationTokenSource释放错误: {ex.Message}");
                }
                finally
                {
                    _cancellationTokenSource = null;
                }

                // 4. 更新UI状态
                try
                {
                    UpdateConnectionStatus(ConnectionStatus.Disconnected);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"更新连接状态错误: {ex.Message}");
                }
            }
        }

        private async Task Disconnect()
        {
            _isUserDisconnect = true;
            await CleanupConnection();
        }

        private void HeartbeatTimer_Tick(object sender, EventArgs e)
        {
            if (!_isConnected || _isReconnecting) return;

            DateTime lastHeartbeat;

            // 使用锁安全获取心跳时间
            lock (_heartbeatLock)
            {
                lastHeartbeat = _lastHeartbeatTime;
            }

            // 计算自上次心跳以来的时间
            var timeSinceLastHeartbeat = DateTime.Now - lastHeartbeat;

            if (timeSinceLastHeartbeat.TotalMilliseconds > HeartbeatTimeout)
            {
                // 心跳超时，触发自动重连
                AddMessage($"心跳丢失 ({timeSinceLastHeartbeat.TotalSeconds:F0}秒)，尝试重新连接...");
                _ = Task.Run(async () => {
                    await HandleDisconnection(false);
                });
            }
            else
            {
                // 心跳正常，显示状态信息
                var timeRemaining = TimeSpan.FromMilliseconds(HeartbeatTimeout) - timeSinceLastHeartbeat;

                // 使用Dispatcher安全更新UI
                Dispatcher.Invoke(() => {
                    txtConnectionStatus.Text = $"已连接 (心跳: {timeRemaining.TotalSeconds:F0}秒)";
                });
            }
        }

        private void ShowNotification(string title, string message)
        {
            // 使用Dispatcher安全创建通知
            Dispatcher.Invoke(() =>
            {
                lock (_notificationsLock)
                {
                    for (int i = _activeNotifications.Count - 1; i >= 0; i--)
                    {
                        if (!_activeNotifications[i].IsVisible)
                        {
                            _activeNotifications.RemoveAt(i);
                        }
                    }
                }

                var notification = new NotificationWindow(title, message);

                lock (_notificationsLock)
                {
                    _activeNotifications.Add(notification);
                }

                notification.Closed += (s, e) =>
                {
                    lock (_notificationsLock)
                    {
                        _activeNotifications.Remove(notification);
                    }
                };

                notification.Show();
            });
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            lstMessages.Items.Clear();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_isClosing) return;
            _isClosing = true;

            e.Cancel = true; // 延迟关闭直到清理完成

            _isUserDisconnect = true;

            // 确保关闭过程不会卡死
            Task.Run(async () =>
            {
                try
                {
                    await Disconnect();

                    // ...其他清理操作...
                }
                finally
                {
                    // 确保所有资源释放后关闭窗口
                    Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            this.Close();
                        }
                        catch { }
                    });
                }
            });
        }
    }
}