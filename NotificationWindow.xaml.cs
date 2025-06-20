using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace WebSocketClientWPF
{
    public partial class NotificationWindow : Window
    {
        private DispatcherTimer _closeTimer;
        private const int DisplayTime = 5000; // 5秒

        public NotificationWindow(string title, string message)
        {
            InitializeComponent();

            // 设置内容
            txtTitle.Text = title;
            txtMessage.Text = message;
            txtTime.Text = DateTime.Now.ToString("HH:mm:ss");

            // 设置窗口位置（右下角）
            Loaded += NotificationWindow_Loaded;

            // 设置动画
            Opacity = 0;

            // 设置自动关闭定时器
            _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DisplayTime) };
            _closeTimer.Tick += (s, e) => CloseWithAnimation();
            _closeTimer.Start();
        }

        private void NotificationWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 计算右下角位置
            var desktopWorkingArea = SystemParameters.WorkArea;
            Left = desktopWorkingArea.Right - ActualWidth - 10;
            Top = desktopWorkingArea.Bottom - ActualHeight - 10;

            // 淡入动画
            DoubleAnimation fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.3));
            BeginAnimation(OpacityProperty, fadeIn);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            CloseWithAnimation();
        }

        private void CloseWithAnimation()
        {
            _closeTimer.Stop();

            // 淡出动画
            DoubleAnimation fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.3));
            fadeOut.Completed += (s, e) => Close();
            BeginAnimation(OpacityProperty, fadeOut);
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }
    }
}