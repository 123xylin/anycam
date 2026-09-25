using AnyCAD.Foundation;
using AnyCAD.WPF;
using RapidRobot.Wpf.ViewModels;
using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace RapidRobot.Wpf
{
    /// <summary>
    /// View 层：只保留渲染控件生命周期与指令式渲染桥接（IRenderHost），
    /// 全部业务状态与命令位于 MainWindowViewModel。
    /// </summary>
    public partial class MainWindow : Window, IRenderHost
    {
        private readonly RenderControl renderView;
        private readonly MainWindowViewModel viewModel;
        private readonly DispatcherTimer playbackTimer;

        public MainWindow()
        {
            InitializeComponent();
            renderView = new RenderControl();
            renderView.ViewerReady += RenderViewOnViewerReady;
            ViewportHost.Content = renderView;
            ViewportBorder.SizeChanged += OnViewportBorderSizeChanged;
            LocationChanged += OnWindowLocationChanged;
            Activated += OnWindowActivated;
            Deactivated += OnWindowDeactivated;
            viewModel = new MainWindowViewModel(this);
            DataContext = viewModel;
            playbackTimer = new DispatcherTimer(DispatcherPriority.Render) {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            playbackTimer.Tick += PlaybackTimerOnTick;
            playbackTimer.Start();
        }

        // ==================== IRenderHost ====================

        public bool IsReady { get; private set; }

        private bool renderReleased;

        private bool CanRender => !renderReleased && renderView != null;

        public void ClearAll()
        {
            if (CanRender)
                renderView.ClearAll();
        }

        public void ShowSceneNode(SceneNode node)
        {
            if (CanRender)
                renderView.ShowSceneNode(node);
        }

        public void RequestDraw(EnumUpdateFlags flags)
        {
            if (CanRender)
                renderView.RequestDraw(flags);
        }

        public void ZoomAll()
        {
            if (CanRender)
                renderView.ZoomAll();
        }

        public void SetStandardView()
        {
            if (CanRender)
                renderView.Viewer.SetStandardView(EnumStandardView.DefaultView, false);
        }

        public void ShowCoordinateGrid(bool visible)
        {
            if (CanRender)
                renderView.ShowCoordinateGrid(visible);
        }

        public void SetEditor(Editor editor)
        {
            if (CanRender)
                renderView.SetEditor(editor);
        }

        public void ClearEditor()
        {
            if (CanRender)
                renderView.ClearEditor();
        }

        private void OnViewportBorderSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (ViewportOverlayRoot == null)
                return;
            ViewportOverlayRoot.Width = ViewportBorder.ActualWidth;
            ViewportOverlayRoot.Height = ViewportBorder.ActualHeight;
            // 尺寸变化时 PlacementTarget 的左上角屏幕坐标可能也变了（如窗口最大化），
            // 强制 Popup 基于 PlacementTarget 重新计算位置。
            RefreshPopupPosition();
        }

        // WPF Popup 在宿主窗口移动时不自动跟随（只跟踪布局变化，不跟踪屏幕坐标变化）。
        // 通过微调 HorizontalOffset/VerticalOffset 触发内部 UpdatePosition() 重算，
        // 使 Popup 重新对齐 PlacementTarget（ViewportBorder）的当前屏幕位置。
        private void OnWindowLocationChanged(object? sender, EventArgs e)
        {
            RefreshPopupPosition();
        }

        private void RefreshPopupPosition()
        {
            if (ViewportOverlayPopup == null)
                return;
            double h = ViewportOverlayPopup.HorizontalOffset;
            double v = ViewportOverlayPopup.VerticalOffset;
            ViewportOverlayPopup.HorizontalOffset = h + 0.1;
            ViewportOverlayPopup.VerticalOffset = v + 0.1;
            ViewportOverlayPopup.HorizontalOffset = h;
            ViewportOverlayPopup.VerticalOffset = v;
        }

        // 主窗口失活时隐藏浮层（Popup 是独立 HWND，不跟随窗口激活状态置顶）；
        // 重新激活时恢复显示，位置由 Placement=Relative 自动对齐 ViewportBorder。
        private void OnWindowActivated(object? sender, EventArgs e)
        {
            if (ViewportOverlayPopup != null && !ViewportOverlayPopup.IsOpen)
                ViewportOverlayPopup.IsOpen = true;
        }

        private void OnWindowDeactivated(object? sender, EventArgs e)
        {
            if (ViewportOverlayPopup != null && ViewportOverlayPopup.IsOpen)
                ViewportOverlayPopup.IsOpen = false;
        }

        // ==================== 渲染初始化 ====================

        private void RenderViewOnViewerReady()
        {
            if (IsReady)
                return;
            IsReady = true;

            CoordinateWidgetSetting cws = new CoordinateWidgetSetting(renderView.Viewer);
            cws.SetCoordinateWidget(EnumViewCoordinateType.Sphere);
            //renderView.SetBackgroundColor(0.035f, 0.055f, 0.09f, 1.0f);
            renderView.Camera.SetProjectionType(EnumProjectionType.Orthographic);
            renderView.SceneManager.SetViewScale(1);
            viewModel.OnViewerReady();
        }

        private void PlaybackTimerOnTick(object? sender, EventArgs e) => viewModel.OnPlaybackTick();

        // ==================== 生命周期 ====================

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            if (shutdownComplete || e.Cancel)
                return;
            e.Cancel = true;
            if (shutdownStarted)
                return;
            shutdownStarted = true;
            playbackTimer.Stop();
            IsEnabled = false;
            _ = ShutdownAsync();
        }

        private bool shutdownStarted;
        private bool shutdownComplete;

        private async Task ShutdownAsync()
        {
            try
            {
                await viewModel.ShutdownAsync();
            }
            catch (Exception error)
            {
                System.Diagnostics.Debug.WriteLine("Shutdown: " + error.Message);
            }
            finally
            {
                viewModel.Dispose();
                shutdownComplete = true;
                _ = Dispatcher.BeginInvoke(new Action(Close));
            }
        }

        // 窗口真正关闭（视觉树已卸载、RenderControl 已销毁）后置位，
        // 此后任何残留回调访问渲染控件一律安全跳过。
        protected override void OnClosed(EventArgs e)
        {
            renderReleased = true;
            base.OnClosed(e);
        }
    }
}
