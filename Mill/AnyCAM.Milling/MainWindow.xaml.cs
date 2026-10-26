using AnyCAD.Foundation;
using AnyCAD.WPF;
using AnyCAM.Milling.ViewModels;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace AnyCAM.Milling
{
    /// <summary>
    /// WPF <see cref="RenderControl"/> + OpenGL/SDL 后端（本机勿开 EnableD3DWPF，见 App 启动注释）。
    /// </summary>
    public partial class MainWindow : Window, IRenderHost
    {
        private const int MaxBootstrapAttempts = 240;
        private int _bootstrapAttempts;
        private readonly MainWindowViewModel _viewModel;

        public MainWindow()
        {
            InitializeComponent();

            RenderView.Focusable = false;
            RenderView.IsHitTestVisible = false;
            WireRenderInputGuards(RenderView);
            RenderView.PreviewMouseLeftButtonDown += RenderView_PreviewMouseLeftButtonDown;

            _viewModel = new MainWindowViewModel(this);
            DataContext = _viewModel;

            Loaded += (_, _) => _viewModel.SyncPanelFromSelection();
            SourceInitialized += (_, _) => EnableDarkTitleBar();

            PreviewKeyUp += (_, e) =>
            {
                if (e.Key == Key.Escape && _viewModel.IsPickingTopFace)
                {
                    _viewModel.CancelPickTopFace();
                    e.Handled = true;
                    return;
                }

                if (!CanAcceptRenderInput && IsEventFromViewport(e.OriginalSource as DependencyObject))
                    e.Handled = true;
            };

            StateChanged += (_, _) =>
            {
                if (WindowState is WindowState.Maximized or WindowState.Normal)
                    RenderView.ForceUpdate();
            };
        }

        private void RenderView_ViewerReady()
        {
            Dispatcher.BeginInvoke(BootstrapViewport, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void WireRenderInputGuards(RenderControl view)
        {
            view.PreviewKeyDown += GuardRenderInput;
            view.PreviewKeyUp += GuardRenderInput;
            view.PreviewMouseDown += GuardRenderInput;
            view.PreviewMouseUp += GuardRenderInput;
            view.PreviewMouseMove += GuardRenderInput;
            view.PreviewMouseWheel += GuardRenderInput;
            view.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler(GuardRenderInput), true);
            view.AddHandler(UIElement.MouseUpEvent, new MouseButtonEventHandler(GuardRenderInput), true);
            view.AddHandler(UIElement.MouseDownEvent, new MouseButtonEventHandler(GuardRenderInput), true);
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private void EnableDarkTitleBar()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            foreach (var attr in new[] { 20, 19 })
            {
                int on = 1;
                if (DwmSetWindowAttribute(hwnd, attr, ref on, sizeof(int)) == 0)
                    break;
            }

            int captionColor = 0x00271810;
            DwmSetWindowAttribute(hwnd, 35, ref captionColor, sizeof(int));
        }

        public bool IsReady { get; private set; }

        private bool HasViewportSize =>
            RenderView.ActualWidth > 1 && RenderView.ActualHeight > 1;

        private bool CanAcceptRenderInput =>
            IsReady && RenderView.Viewer != null && HasViewportSize;

        public IRenderView View => RenderView;

        private bool CanRender => IsReady && RenderView != null;

        public void ClearAll()
        {
            if (CanRender)
                RenderView.ClearAll();
        }

        public void ShowSceneNode(SceneNode node)
        {
            if (CanRender)
                RenderView.ShowSceneNode(node);
        }

        public void RemoveSceneNode(SceneNode node)
        {
            if (!CanRender || node == null)
                return;

            RenderView.RemoveSceneNode(node.GetUuid());
        }

        public void RequestDraw(EnumUpdateFlags flags)
        {
            if (CanRender)
                RenderView.RequestDraw(flags);
        }

        public void ZoomAll()
        {
            if (!CanRender)
                return;

            RenderView.SetStandardView(EnumStandardView.View3D, false);
            RenderView.ZoomAll(2.2f);
            RenderView.ForceUpdate();
        }

        public void SetStandardView()
        {
            if (CanRender)
                RenderView.SetStandardView(EnumStandardView.View3D, false);
        }

        public void ShowCoordinateGrid(bool visible)
        {
            if (CanRender)
                RenderView.ShowCoordinateGrid(visible);
        }

        public void SetBackgroundColor(Vector4 color)
        {
            var viewer = RenderView.Viewer;
            if (viewer != null)
                viewer.SetBackgroundColor(color);
        }

        public void FitViewDeferred()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!CanRender)
                    return;

                ZoomAll();
                RenderView.RequestDraw(EnumUpdateFlags.Scene);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        public void SetFacePickMode(bool enabled)
        {
            if (!CanRender)
                return;

            RenderView.ClearPickFilters();
            if (enabled)
            {
                RenderView.AddPickFilter(EnumShapeFilter.LeafNode);
                RenderView.AddPickFilter(EnumShapeFilter.VertexEdgeFace);
                RenderView.ViewContext?.PushCursor("cross");
            }
            else
            {
                RenderView.ViewContext?.PopCursor();
            }

            RenderView.RequestDraw(EnumUpdateFlags.Selection);
        }

        public bool TryPickFace(MouseButtonEventArgs e, out TopoShape? face)
        {
            face = null;
            if (!CanAcceptRenderInput || RenderView.ViewContext == null)
                return false;

            var pos = RenderView.GetMousePosition(e);
            var x = (int)Math.Round(pos.X);
            var y = (int)Math.Round(pos.Y);

            var ctx = RenderView.ViewContext;
            var engine = SnapShapeEngine.Get(ctx);
            if (engine == null)
                return false;

            var picked = engine.PickFace(ctx, x, y);
            if (picked == null || picked.IsNull())
                return false;

            face = engine.ParseSubShape(ctx, picked);
            return face != null;
        }

        private void RenderView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_viewModel.IsPickingTopFace)
                return;

            if (_viewModel.TryApplyPickedTopFace(e))
                e.Handled = true;
        }

        private void BootstrapViewport()
        {
            if (++_bootstrapAttempts > MaxBootstrapAttempts)
                return;

            if (RenderView.Viewer == null || RenderView.Scene == null)
            {
                Dispatcher.BeginInvoke(BootstrapViewport, System.Windows.Threading.DispatcherPriority.Background);
                return;
            }

            if (!HasViewportSize)
            {
                Dispatcher.BeginInvoke(BootstrapViewport, System.Windows.Threading.DispatcherPriority.Loaded);
                return;
            }

            IsReady = true;
            RenderView.Focusable = true;
            RenderView.IsHitTestVisible = true;
            _viewModel.OnViewerReady();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!CanRender)
                    return;

                ZoomAll();
                RenderView.RequestDraw(EnumUpdateFlags.Scene);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void CamTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is ViewModels.TreeNode node)
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

        private void GuardRenderInput(object sender, RoutedEventArgs e)
        {
            if (!CanAcceptRenderInput)
                e.Handled = true;
        }

        private static bool IsEventFromViewport(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is RenderControl)
                    return true;
                source = VisualTreeHelper.GetParent(source);
            }

            return false;
        }

        protected override void OnClosed(EventArgs e)
        {
            IsReady = false;
            RenderView.Focusable = false;
            RenderView.IsHitTestVisible = false;
            _viewModel.Dispose();
            base.OnClosed(e);
        }
    }
}
