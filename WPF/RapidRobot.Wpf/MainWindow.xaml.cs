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

        public void ClearAll() => renderView.ClearAll();

        public void ShowSceneNode(SceneNode node) => renderView.ShowSceneNode(node);

        public void RequestDraw(EnumUpdateFlags flags) => renderView.RequestDraw(flags);

        public void ZoomAll() => renderView.ZoomAll();

        public void SetStandardView() =>
            renderView.Viewer.SetStandardView(EnumStandardView.DefaultView, false);

        public void ShowCoordinateGrid(bool visible) => renderView.ShowCoordinateGrid(visible);

        public void SetEditor(Editor editor) => renderView.SetEditor(editor);

        public void ClearEditor() => renderView.ClearEditor();

        // ==================== 渲染初始化 ====================

        private void RenderViewOnViewerReady()
        {
            if (IsReady)
                return;
            IsReady = true;
            renderView.SetBackgroundColor(0.035f, 0.055f, 0.09f, 1.0f);
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
                playbackTimer.Stop();
                viewModel.Dispose();
                renderView.Dispose();
                shutdownComplete = true;
                _ = Dispatcher.BeginInvoke(new Action(Close));
            }
        }
    }
}
