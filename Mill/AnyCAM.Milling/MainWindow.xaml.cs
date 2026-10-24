using AnyCAD.Foundation;
using AnyCAD.WPF;
using AnyCAM.Milling.ViewModels;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace AnyCAM.Milling
{
    /// <summary>
    /// View 层：只保留渲染控件生命周期与指令式渲染桥接（<see cref="IRenderHost"/>），
    /// 全部业务状态与命令都在 <see cref="MainWindowViewModel"/>。
    /// </summary>
    public partial class MainWindow : Window, IRenderHost
    {
        private readonly RenderControl _renderView;
        private readonly MainWindowViewModel _viewModel;

        public MainWindow()
        {
            InitializeComponent();

            _renderView = new RenderControl();
            _renderView.ViewerReady += OnViewerReady;
            ViewportHost.Children.Add(_renderView);

            _viewModel = new MainWindowViewModel(this);
            DataContext = _viewModel;

            Loaded += (_, _) => _viewModel.SyncPanelFromSelection();
            SourceInitialized += (_, _) => EnableDarkTitleBar();
        }

        // ==================== 系统标题栏深色 ====================

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>
        /// 把系统标题栏与界面 chrome 统一：DWMWA_USE_IMMERSIVE_DARK_MODE 保证图标深色化，
        /// DWMWA_CAPTION_COLOR 直接把标题栏刷成 ChromeBrush（#101827），
        /// Win10 2004+ 用属性 20/35，旧版本回退属性 19。
        /// </summary>
        private void EnableDarkTitleBar()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            foreach (var attr in new[] { 20, 19 })
            {
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, attr, ref on, sizeof(int)) == 0)
                    break;
            }

            // COLORREF = 0x00BBGGRR，ChromeBrush #101827 → R=0x10 G=0x18 B=0x27
            int captionColor = 0x00271810;
            DwmSetWindowAttribute(hwnd, 35, ref captionColor, sizeof(int));
        }

        // ==================== IRenderHost ====================

        public bool IsReady { get; private set; }

        public IRenderView View => _renderView;

        private bool CanRender => IsReady && _renderView != null;

        public void ClearAll()
        {
            if (CanRender)
                _renderView.ClearAll();
        }

        public void ShowSceneNode(SceneNode node)
        {
            if (CanRender)
                _renderView.ShowSceneNode(node);
        }

        public void RemoveSceneNode(SceneNode node)
        {
            if (CanRender && node != null)
                _renderView.Scene.RemoveNode(node);
        }

        public void RequestDraw(EnumUpdateFlags flags)
        {
            if (CanRender)
                _renderView.RequestDraw(flags);
        }

        public void ZoomAll()
        {
            if (!CanRender)
                return;

            // 先切等轴测视角再看全部：ZoomToExtend 只按包围盒算距离，不修正视线方向，
            // 若相机正在正上方俯视，up=(0,1,0) 与视线平行会得到退化视图。
            _renderView.SetStandardView(EnumStandardView.View3D, false);
            // ratio 是包围盒留白系数：默认 1.1 会让模型顶到画面边缘，取大一些留出边距。
            _renderView.ZoomAll(2.2f);
        }

        public void SetStandardView()
        {
            if (CanRender)
                _renderView.SetStandardView(EnumStandardView.View3D, false);
        }

        public void ShowCoordinateGrid(bool visible)
        {
            if (CanRender)
                _renderView.ShowCoordinateGrid(visible);
        }

        public void SetBackgroundColor(Vector4 color)
        {
            if (CanRender)
                _renderView.Viewer.SetBackgroundColor(color);
        }

        // ==================== 事件 ====================

        private void OnViewerReady()
        {
            IsReady = true;
            _viewModel.OnViewerReady();

            // ViewerReady 可能在视口布局完成前就触发：此时 ActualWidth/Height 还是 0，
            // 引擎按极小尺寸算出的 ZoomToExtend 会把模型放大到占满视口。
            // 等布局稳定后再取一次景。
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!CanRender)
                    return;

                ZoomAll();
                _renderView.RequestDraw(EnumUpdateFlags.Scene);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void CamTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is TreeNode node)
                _viewModel.SelectedNode = node;

            _viewModel.SyncPanelFromSelection();
        }

        private void ZoomAll_Click(object sender, RoutedEventArgs e) => ZoomAll();

        private void ToggleGrid_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.ShowGrid = !_viewModel.ShowGrid;
            ShowCoordinateGrid(_viewModel.ShowGrid);
        }

        private void StandardView_Click(object sender, RoutedEventArgs e) => SetStandardView();

        protected override void OnClosed(EventArgs e)
        {
            _viewModel.Dispose();
            base.OnClosed(e);
        }
    }
}
