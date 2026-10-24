using AnyCAM.Milling.Engine;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Windows.Threading;

namespace AnyCAM.Milling.ViewModels
{
    /// <summary>
    /// 渲染桥接：ViewModel 不直接持有 WPF 的 RenderControl（指令式控件），
    /// 所有需要三维的操作都经这个接口由 View 层实现，ViewModel 才能独立测试。
    /// </summary>
    public interface IRenderHost
    {
        bool IsReady { get; }
        //! 底层渲染视图；用于把几何体包装成场景节点
        IRenderView View { get; }
        void ClearAll();
        void ShowSceneNode(SceneNode node);
        //! 从场景摘掉一个节点；换工序时要把旧的刀路组摘掉，否则会一直堆着
        void RemoveSceneNode(SceneNode node);
        void RequestDraw(EnumUpdateFlags flags);
        void ZoomAll();
        void SetStandardView();
        void ShowCoordinateGrid(bool visible);
        //! 视口底色。三维场景由引擎自己清屏，不跟随 WPF 的 Background
        void SetBackgroundColor(Vector4 color);
    }

    /// <summary>
    /// 主窗口 ViewModel。
    /// <para>
    /// 全部加工状态由一个只吃 Craft/Kernel 的 <see cref="CamSession"/> 承载，
    /// 不碰文档、Element、Schema、Undo。参数改动直接写回引擎对象并重算，
    /// 所以没有「应用」按钮，只有一个「生成刀路」。
    /// </para>
    /// </summary>
    public partial class MainWindowViewModel : ObservableObject, IDisposable
    {
        private readonly CamSession _session = new();
        private readonly MillSimulator _simulator;
        private readonly IRenderHost _render;
        private bool _suppressRefresh;

        public MainWindowViewModel(IRenderHost render)
        {
            _render = render;
            _simulator = new MillSimulator(_session);
            foreach (var tool in _session.Tools)
                ToolOptions.Add(tool.Name);
            BuildInitialTree();
            foreach (var root in TreeRoots)
                HookVisibility(root);
        }

        // ==================== 树 ====================

        public ObservableCollection<TreeNode> TreeRoots { get; } = new();

        [ObservableProperty]
        private TreeNode? _selectedNode;

        private void BuildInitialTree()
        {
            _session.LoadPart();

            // 模型分组：零件 + 毛坯
            var model = new TreeNode(TreeNodeKind.Group, "模型");
            model.Children.Add(new TreeNode(TreeNodeKind.Part, "示例零件",
                "120 × 120 × 40 mm"));
            // 毛坯默认不显示：它比零件大一圈、把零件和刀路整个包住，
            // 默认打开会让视口里只剩一个灰方块；需要核对余量时再点眼睛打开。
            model.Children.Add(new TreeNode(TreeNodeKind.Stock, "毛坯 · 方块",
                DescribeStock())
            {
                IsVisible = false,
            });

            // 坐标系：默认 MCS，随模型装载
            var coords = new TreeNode(TreeNodeKind.Group, "坐标系");
            coords.Children.Add(new TreeNode(TreeNodeKind.Coordinate, "MCS1", "已对齐工件"));

            // 刀库：多个刀具挂同一个父节点，工序在属性面板里换刀
            var tools = new TreeNode(TreeNodeKind.Group, "刀具");
            foreach (var tool in _session.Tools)
                tools.Children.Add(new TreeNode(TreeNodeKind.Tool, tool.Name, tool.Summary));

            var strategies = new TreeNode(TreeNodeKind.Group, "加工策略");
            // 粗加工：顶面清面 + 方槽开粗
            AddOperationRow(strategies, "面铣");
            AddOperationRow(strategies, "2D挖槽");
            // 开孔：四角 φ10 盲孔啄钻（属粗加工，先于精修，避免精修完的壁被翻毛刺）
            AddOperationRow(strategies, "钻孔");
            // 精加工：外形贯通精修 + 槽壁分层精修，两特征互不重复
            AddOperationRow(strategies, "轮廓");
            AddOperationRow(strategies, "槽壁精加工");

            TreeRoots.Add(model);
            TreeRoots.Add(coords);
            TreeRoots.Add(tools);
            TreeRoots.Add(strategies);

            SelectedNode = strategies.Children.Count > 0 ? strategies.Children[0] : model.Children[0];
        }

        private string DescribeStock()
        {
            var stock = _session.Stock;
            if (stock == null)
                return "未生成";

            return $"{stock.GetSizeX():F0} × {stock.GetSizeY():F0} × {stock.GetSizeZ():F0} mm";
        }

        private TreeNode AddOperationRow(TreeNode parent, string strategy)
        {
            var entry = _session.AddOperation(strategy);
            var node = new TreeNode(TreeNodeKind.Operation, entry.Name, StrategyMeta(strategy))
            {
                Entry = entry,
                StrategyMeta = StrategyMeta(strategy)
            };
            parent.Children.Add(node);
            return node;
        }

        private static string StrategyMeta(string strategy) => strategy switch
        {
            "面铣" => "顶面清面 · 往复",
            "2D挖槽" => "型腔 · 环切",
            "轮廓" => "外形 · 贯通",
            "槽壁精加工" => "槽壁 · 分层",
            "钻孔" => "四角 · 啄钻",
            _ => "精修 · 分层",
        };

        /// <summary>「新建策略」按顺序补进的策略；五种策略轮着来</summary>
        private static readonly string[] StrategyPool = { "轮廓", "槽壁精加工", "面铣", "2D挖槽", "钻孔" };

        private int _strategyCursor;

        [RelayCommand]
        private void AddStrategy()
        {
            var group = TreeRoots.FirstOrDefault(r => r.Kind == TreeNodeKind.Group && r.Title == "加工策略");
            if (group == null)
                return;

            var strategy = StrategyPool[_strategyCursor++ % StrategyPool.Length];
            var node = AddOperationRow(group, strategy);
            HookVisibility(node);
            StatusMessage = $"已添加 {node.Title}";
        }

        [RelayCommand]
        private void AddTool()
        {
            // 刀库编辑暂未接引擎
            StatusMessage = "添加刀具：功能暂未实现";
        }

        partial void OnSelectedNodeChanged(TreeNode? value)
        {
            if (value == null)
                return;

            foreach (var root in TreeRoots)
                ClearSelection(root, value);

            RefreshPanelHeader();
            StatusMessage = $"已选中：{value.Title}";

            // 换工序就要换刀路：不重画的话视口会一直停在上一条工序的路径上
            ShowSelectedToolpath();
        }

        private static void ClearSelection(TreeNode node, TreeNode keep)
        {
            if (!ReferenceEquals(node, keep))
                node.IsSelected = false;

            foreach (var child in node.Children)
                ClearSelection(child, keep);
        }

        // ==================== 右侧面板头部 ====================

        [ObservableProperty]
        private string _panelTitle = "操作参数";

        [ObservableProperty]
        private string _panelStatusColor = "#64748B";

        private void RefreshPanelHeader()
        {
            var node = SelectedNode;
            if (node == null)
                return;

            if (node.Kind == TreeNodeKind.Operation && node.Entry != null)
            {
                PanelTitle = node.Entry.Name;
                PanelStatusColor = node.StatusForeground;
            }
            else
            {
                PanelTitle = node.Title;
                PanelStatusColor = "#64748B";
            }

            RefreshPanelBody();
        }

        /// <summary>面板正文随对象类型切换：只有工序行才显示加工参数四个标签页</summary>
        public bool PanelIsOperation => SelectedNode?.Kind == TreeNodeKind.Operation;

        /// <summary>非工序对象的只读信息行；Label 为空表示整行说明（跨两列）</summary>
        public sealed record FieldItem(string Label, string Value)
        {
            public bool IsFullRow => string.IsNullOrEmpty(Label);
        }

        public ObservableCollection<FieldItem> PanelFields { get; } = new();

        /// <summary>非工序对象时面板底部的一句话说明</summary>
        [ObservableProperty]
        private string _panelHint = string.Empty;

        /// <summary>按选中对象类型重建面板正文</summary>
        private void RefreshPanelBody()
        {
            OnPropertyChanged(nameof(PanelIsOperation));

            PanelFields.Clear();
            PanelHint = string.Empty;

            var node = SelectedNode;
            if (node == null)
                return;

            switch (node.Kind)
            {
                case TreeNodeKind.Part:
                    PanelFields.Add(new FieldItem("尺寸", "120 × 120 × 40"));
                    PanelFields.Add(new FieldItem("槽", "48 × 48 × 20"));
                    PanelFields.Add(new FieldItem("孔", "φ10 × 20，四角"));
                    break;

                case TreeNodeKind.Stock:
                    var stock = _session.Stock;
                    PanelFields.Add(new FieldItem("尺寸", stock == null
                        ? "未生成"
                        : $"{stock.GetSizeX():F0} × {stock.GetSizeY():F0} × {stock.GetSizeZ():F0} mm"));
                    PanelFields.Add(new FieldItem("对刀点", "工件上表面中心"));
                    PanelHint = "毛坯比零件大一圈，默认隐藏；核对余量时在树上点亮眼睛。";
                    break;

                case TreeNodeKind.Tool when node.Title is { } title:
                    AddToolFields(title);
                    break;

                case TreeNodeKind.Coordinate:
                    PanelFields.Add(new FieldItem("名称", "MCS1"));
                    PanelFields.Add(new FieldItem("对刀点", "工件上表面中心"));
                    PanelHint = "默认加工坐标系随模型装载，暂不可编辑。";
                    break;

                case TreeNodeKind.Group when node.Title == "加工策略":
                    PanelHint = "选中一条策略行查看与编辑它的加工参数。";
                    break;

                case TreeNodeKind.Group when node.Title == "刀具":
                    PanelHint = "刀库里的刀分给各道工序使用：选中一把查看参数，或在工序里换刀。";
                    break;

                case TreeNodeKind.Group when node.Title == "模型":
                    PanelHint = "模型与毛坯显隐用行上的眼睛开关。";
                    break;

                case TreeNodeKind.Group when node.Title == "坐标系":
                    PanelHint = "加工坐标系决定刀路的原点与方向。";
                    break;
            }
        }

        /// <summary>刀具行的只读参数：引擎里的 Tool 是唯一数据源</summary>
        private void AddToolFields(string title)
        {
            var entry = _session.Tools.FirstOrDefault(t => t.Name == title);
            if (entry == null)
                return;

            var tool = entry.Tool;
            var corner = tool.GetCornerRadius();
            var tip = tool.GetTipAngle();

            PanelFields.Add(new FieldItem("刀柄", entry.Summary));
            PanelFields.Add(new FieldItem("直径", $"φ{tool.GetDiameter():F0} mm"));
            if (corner > 0.001)
            {
                var shape = Math.Abs(corner * 2 - tool.GetDiameter()) < 0.01 ? "球头" : "圆鼻";
                PanelFields.Add(new FieldItem("圆角", $"R{corner:F0}（{shape}）"));
            }
            if (tip > 0.001)
                PanelFields.Add(new FieldItem("顶角", $"{tip:F0}°"));
            PanelFields.Add(new FieldItem("刃长 / 总长", $"{tool.GetFluteLength():F0} / {tool.GetLength():F0} mm"));
            PanelFields.Add(new FieldItem("刃数", $"{tool.GetFluteCount()}"));
            PanelFields.Add(new FieldItem("转速", $"{entry.Controller.GetSpindleSpeed():F0} rpm · {(entry.Controller.GetSpindleClockwise() ? "正转" : "反转")}"));
            PanelFields.Add(new FieldItem("进给", $"水平 {tool.GetHorizFeed():F0} / 垂直 {tool.GetVertFeed():F0} mm/min"));
            PanelHint = "进给与转速由该刀的控制器持有：改某道工序的进给不会影响用其他刀的工序。";
        }

        /// <summary>当前选中的策略行；非策略行为 null</summary>
        public OperationEntry? SelectedEntry => SelectedNode?.Entry;

        // ==================== 参数（即时生效） ====================

        // ---- 加工 ----

        [ObservableProperty] private double _stockToLeave;
        [ObservableProperty] private double _stepOver = 4.0;
        [ObservableProperty] private double _stepDown = 1.0;
        [ObservableProperty] private double _startDepth;
        [ObservableProperty] private double _finalDepth;
        [ObservableProperty] private double _safeHeight;

        // ---- 刀具 ----

        /// <summary>刀库下拉：树上「刀具」分组的所有刀</summary>
        public ObservableCollection<string> ToolOptions { get; } = new();

        /// <summary>选中工序当前配的刀；切换即改工序并标记重算</summary>
        [ObservableProperty]
        private string? _selectedToolName;

        /// <summary>本工序吃进引擎的加工面描述，界面借此体现各策略数据差异</summary>
        [ObservableProperty]
        private string _geometrySummary = "整体";

        [ObservableProperty] private double _toolDiameter = 8;
        [ObservableProperty] private double _toolCornerRadius = 4;
        [ObservableProperty] private double _toolFluteLength = 20;
        [ObservableProperty] private double _toolLength = 45;
        [ObservableProperty] private int _toolFluteCount = 4;
        [ObservableProperty] private int _toolNumber = 1;

        // ---- 进给 ----

        [ObservableProperty] private double _spindleSpeed = 12000;
        [ObservableProperty] private double _cutFeed = 1200;
        [ObservableProperty] private double _plungeFeed = 300;
        [ObservableProperty] private double _rapidFeed = 6000;
        [ObservableProperty] private int _coolantIndex;

        public ObservableCollection<string> CoolantOptions { get; } = new() { "关闭", "雾冷", "液冷" };

        partial void OnStockToLeaveChanged(double value) => ApplyArea(p => p.SetStockToLeave(value));
        partial void OnStepOverChanged(double value) => ApplyArea(p => p.SetStepover(value));
        partial void OnStepDownChanged(double value) => ApplyArea(p => p.SetStepdown(value));

        partial void OnStartDepthChanged(double value) => ApplyDepths();
        partial void OnFinalDepthChanged(double value) => ApplyDepths();
        partial void OnSafeHeightChanged(double value) => ApplyDepths();

        partial void OnToolDiameterChanged(double value) => ApplyTool(t => t.SetDiameter(value));
        partial void OnToolCornerRadiusChanged(double value) => ApplyTool(t => t.SetCornerRadius(value));
        partial void OnToolFluteLengthChanged(double value) => ApplyTool(t => t.SetFluteLength(value));
        partial void OnToolLengthChanged(double value) => ApplyTool(t => t.SetLength(value));
        partial void OnToolFluteCountChanged(int value) => ApplyTool(t => t.SetFluteCount(value));

        partial void OnSpindleSpeedChanged(double value)
        {
            _session.ToolController.SetSpindleSpeed(value);
            MarkDirty();
        }

        partial void OnCutFeedChanged(double value) => ApplyTool(t => t.SetHorizFeed(value));
        partial void OnPlungeFeedChanged(double value) => ApplyTool(t => t.SetVertFeed(value));
        partial void OnRapidFeedChanged(double value) => _session.ToolController.SetHorizRapid(value);

        partial void OnToolNumberChanged(int value) => _session.ToolController.SetToolNumber(value);

        partial void OnSelectedToolNameChanged(string? value)
        {
            if (_suppressRefresh || value == null)
                return;

            var entry = SelectedEntry;
            if (entry == null || entry.ToolName == value)
                return;

            entry.ToolName = value;
            _session.SelectedTool = _session.GetTool(value);
            MarkDirty();
            SelectedNode?.RefreshStatus();
            RefreshPanelHeader();
            SyncPanelFromSelection();
            StatusMessage = $"{entry.Name} 已换 {value}";
        }

        partial void OnCoolantIndexChanged(int value)
        {
            _session.ToolController.SetCoolant(value switch
            {
                1 => EnumPostCoolant.Mist,
                2 => EnumPostCoolant.Flood,
                _ => EnumPostCoolant.Off,
            });
            MarkDirty();
        }

        private void ApplyArea(Action<AreaBasedOperation> apply)
        {
            if (_suppressRefresh)
                return;

            if (SelectedEntry?.Operation is AreaBasedOperation area)
                apply(area);

            MarkDirty();
        }

        private void ApplyDepths()
        {
            if (_suppressRefresh)
                return;

            if (SelectedEntry?.Operation is not PathOperation op)
                return;

            var depths = op.GetDepthParamsRef();
            depths.SetStartDepth(StartDepth);
            depths.SetFinalDepth(FinalDepth);
            depths.SetSafeHeight(SafeHeight);
            MarkDirty();
        }

        private void ApplyTool(Action<Tool> apply)
        {
            if (_suppressRefresh)
                return;

            var tool = _session.ToolController.GetTool();
            if (tool != null)
                apply(tool);

            MarkDirty();
        }

        private void MarkDirty()
        {
            var node = SelectedNode;
            if (node?.Entry == null)
                return;

            node.Entry.MarkDirty();
            node.RefreshStatus();
            RefreshPanelHeader();
        }

        /// <summary>选中策略时把它的参数回填到面板，期间不触发写回</summary>
        public void SyncPanelFromSelection()
        {
            var entry = SelectedEntry;
            if (entry?.Operation is not PathOperation op)
                return;

            _suppressRefresh = true;
            try
            {
                // 本工序的刀：刀具页签编辑的就是它
                _session.SelectedTool = _session.GetTool(entry.ToolName);
                SelectedToolName = entry.ToolName;
                GeometrySummary = _session.DescribeGeometry(entry.Strategy);

                var depths = op.GetDepthParamsRef();
                StartDepth = depths.GetStartDepth();
                FinalDepth = depths.GetFinalDepth();
                SafeHeight = depths.GetSafeHeight();

                var tool = _session.ToolController.GetTool();
                if (tool != null)
                {
                    ToolDiameter = tool.GetDiameter();
                    ToolCornerRadius = tool.GetCornerRadius();
                    ToolFluteLength = tool.GetFluteLength();
                    ToolLength = tool.GetLength();
                    ToolFluteCount = tool.GetFluteCount();
                }

                if (op is AreaBasedOperation area)
                {
                    StockToLeave = area.GetStockToLeave();
                    StepOver = area.GetStepover();
                    StepDown = area.GetStepdown();
                }
            }
            finally
            {
                _suppressRefresh = false;
            }
        }

        // ==================== 命令 ====================

        [ObservableProperty]
        private string _statusMessage = "就绪";

        [ObservableProperty]
        private string _statusStats = string.Empty;

        [RelayCommand]
        private void GenerateToolpath()
        {
            var entry = SelectedEntry;
            if (entry == null)
            {
                StatusMessage = "请先在左侧选中一条加工策略";
                return;
            }

            var ok = _session.Compute(entry, out var message);
            SelectedNode?.RefreshStatus();
            RefreshPanelHeader();
            StatusMessage = message;

            if (ok)
            {
                RefreshStats();
                RefreshToolpathGroup(entry);
            }
        }

        [RelayCommand]
        private void ComputeAll()
        {
            var ok = _session.ComputeAll(out var message);

            foreach (var root in TreeRoots)
                RefreshStatusRecursive(root);

            StatusMessage = string.IsNullOrEmpty(message) ? "没有待计算的策略" : message;

            // 无论是否全部成功都要刷新：只要有工序算出来了，就得让它上屏。
            // 挂在 ok 上会让部分失败时整批刀路都不显示，看起来像「只有第一道工序生效」。
            RefreshStats();
            RefreshPanelHeader();

            // 只重建重算过的工序：其余组的缓存刀路原样保留
            foreach (var opEntry in _session.Operations)
            {
                if (opEntry.State == OperationState.Computed)
                    RefreshToolpathGroup(opEntry);
            }
        }

        private static void RefreshStatusRecursive(TreeNode node)
        {
            node.RefreshStatus();
            foreach (var child in node.Children)
                RefreshStatusRecursive(child);
        }

        private void RefreshStats()
        {
            var total = 0.0;
            var paths = 0;
            foreach (var entry in _session.Operations)
            {
                if (entry.State != OperationState.Computed)
                    continue;

                total += entry.CycleSeconds;
                paths += entry.CommandCount;
            }

            StatusStats = paths == 0
                ? string.Empty
                : $"{paths} 条指令 · 预计 {CamSession.FormatDuration(total)}";
        }

        /// <summary>
        /// 后处理：有选中工序就只出这一条，没有就出整份程序。
        /// 整份程序会按工序顺序拼接，每道工序保留自己的换刀与转速。
        /// </summary>
        [RelayCommand]
        private void PostProcess()
        {
            var entry = SelectedEntry;
            if (entry != null && entry.State == OperationState.Computed)
            {
                var nc = _session.PostProcess(entry);
                NcPreview = nc;
                StatusMessage = $"后处理完成（{_session.PostName} · {entry.Name}）：{nc.Length} 字符";
                return;
            }

            var all = _session.PostProcessAll(out var count);
            if (string.IsNullOrEmpty(all))
            {
                StatusMessage = "先计算再后处理";
                return;
            }

            NcPreview = all;
            StatusMessage = $"后处理完成（{_session.PostName}）：{count} 道工序 / {all.Length} 字符";
        }

        [ObservableProperty]
        private string _ncPreview = "尚未生成 NC 程序";

        // ==================== 后处理选项 ====================

        /// <summary>后处理器标识，与 <see cref="PostDescriptions"/> 同序</summary>
        public ObservableCollection<string> PostNameOptions { get; } = new(CamSession.PostNames);

        public ObservableCollection<string> PostDescriptions { get; } = new(CamSession.PostDescriptions);

        [ObservableProperty] private int _postNameIndex;

        public ObservableCollection<string> PostUnitOptions { get; } = new() { "毫米 (G21)", "英寸 (G20)" };

        [ObservableProperty] private int _postUnitIndex;

        partial void OnPostNameIndexChanged(int value)
        {
            if (value < 0 || value >= PostNameOptions.Count)
                return;

            _session.SetPostName(PostNameOptions[value]);
            // 换了后处理器，已出的程序就跟当前设置不一致了，清掉避免误用
            NcPreview = "尚未生成 NC 程序";
            StatusMessage = $"后处理器：{PostNameOptions[value]} · {PostDescriptions[value]}";
        }

        partial void OnPostUnitIndexChanged(int value)
        {
            _session.SetPostUnits(value == 1 ? EnumPostUnits.Inch : EnumPostUnits.Millimeter);
            NcPreview = "尚未生成 NC 程序";
        }

        // ==================== 视口 ====================

        [ObservableProperty]
        private bool _showGrid;

        private BrepSceneNode? _partNode;
        private BrepSceneNode? _stockNode;

        /// <summary>视口底色 #16212F，与画布同色</summary>
        private static readonly Vector4 ViewerBackground = new(0x16 / 255f, 0x21 / 255f, 0x2F / 255f, 1.0f);

        public void OnViewerReady()
        {
            if (!_render.IsReady)
                return;

            // ShowShape 内部已经执行过 AddNode，这里不能再 ShowSceneNode，
            // 否则同一节点进入场景两次（包围盒重复计入，ZoomToExtend 会算小距离）。
            _render.SetBackgroundColor(ViewerBackground);

            _partNode = _session.ShowPart(_render.View);
            _stockNode = _session.ShowStock(_render.View);

            _render.ZoomAll();

            // 显隐统一走行属性：视口就绪后按树上眼睛重放一遍（含已算工序的刀路组）
            ApplyNodeVisibility();
            StatusMessage = "三维视口已就绪";
        }

        /// <summary>每条工序的刀路组，按工序缓存：眼睛开关只 SetVisible，不重建</summary>
        private readonly Dictionary<OperationEntry, GroupSceneNode> _pathGroups = new();

        /// <summary>树上启用了眼睛的行（模型/毛坯/工序）；建树时注册</summary>
        private readonly List<TreeNode> _visibilityNodes = new();

        /// <summary>建树时挂上眼睛回调，递归覆盖后补的子节点</summary>
        private void HookVisibility(TreeNode node)
        {
            if (_visibilityNodes.Contains(node))
                return;

            node.VisibilityChanged += OnNodeVisibilityChanged;
            _visibilityNodes.Add(node);
            foreach (var child in node.Children)
                HookVisibility(child);
        }

        private void OnNodeVisibilityChanged(TreeNode node, bool visible)
        {
            ApplyNodeVisibility();
            StatusMessage = $"{node.Title} 已{(visible ? "显示" : "隐藏")}";
        }

        /// <summary>把行级显隐落到场景节点上：模型/毛坯是单节点，工序按各自的刀路组处理</summary>
        private void ApplyNodeVisibility()
        {
            if (!_render.IsReady)
                return;

            foreach (var node in _visibilityNodes)
            {
                switch (node.Kind)
                {
                    case TreeNodeKind.Part:
                        _partNode?.SetVisible(node.IsVisible);
                        break;
                    case TreeNodeKind.Stock:
                        _stockNode?.SetVisible(node.IsVisible);
                        break;
                    case TreeNodeKind.Operation when node.Entry != null:
                        ApplyOperationVisibility(node);
                        break;
                }
            }

            _render.RequestDraw(EnumUpdateFlags.Scene);
        }

        /// <summary>单条工序的显隐：有组就 SetVisible，没组且已算过就现建（建出来按当前开关决定可见性）</summary>
        private void ApplyOperationVisibility(TreeNode node)
        {
            var entry = node.Entry!;
            if (_pathGroups.TryGetValue(entry, out var group))
            {
                group.SetVisible(node.IsVisible);
                return;
            }

            if (entry.State != OperationState.Computed)
                return;

            group = _session.ShowToolpath(_render.View, entry);
            if (group.GetCount() == 0)
                return;

            group.SetVisible(node.IsVisible);
            _render.ShowSceneNode(group);
            _pathGroups[entry] = group;
        }

        private TreeNode? FindNodeOf(OperationEntry? entry)
        {
            if (entry == null)
                return null;

            return _visibilityNodes.FirstOrDefault(n => n.Entry == entry);
        }

        private TreeNode? FindNodeOfKind(TreeNodeKind kind)
            => _visibilityNodes.FirstOrDefault(n => n.Kind == kind);

        /// <summary>
        /// 重算后刷新某条工序的刀路组：旧组摘掉重建，可见性沿用行上的眼睛开关。
        /// <para>
        /// 组按工序缓存：眼睛开关只 <c>SetVisible</c> 不重建，
        /// 「关一下再打开」不会丢缓存的刀路节点。
        /// </para>
        /// </summary>
        public void RefreshToolpathGroup(OperationEntry entry)
        {
            if (!_render.IsReady)
                return;

            if (_pathGroups.Remove(entry, out var old))
            {
                // 先清子节点再摘组：Clear 只清 m_Children，组本身还挂在场景里
                old.Clear();
                _render.RemoveSceneNode(old);
            }

            if (entry.State != OperationState.Computed)
            {
                _render.RequestDraw(EnumUpdateFlags.Scene);
                return;
            }

            var group = _session.ShowToolpath(_render.View, entry);
            if (group.GetCount() > 0)
            {
                var node = FindNodeOf(entry);
                group.SetVisible(node?.IsVisible ?? true);
                _render.ShowSceneNode(group);
                _pathGroups[entry] = group;
            }

            _render.RequestDraw(EnumUpdateFlags.Scene);
        }

        /// <summary>切换选中工序时补建缺失的刀路组；各组按各自眼睛开关保持显示</summary>
        public void ShowSelectedToolpath()
        {
            if (!_render.IsReady)
                return;

            var entry = SelectedEntry;
            if (entry != null && entry.State == OperationState.Computed && !_pathGroups.ContainsKey(entry))
            {
                // 已算过但组还没建（例如算完时视口未就绪），补建一次
                var node = FindNodeOf(entry);
                var group = _session.ShowToolpath(_render.View, entry);
                if (group.GetCount() > 0)
                {
                    group.SetVisible(node?.IsVisible ?? true);
                    _render.ShowSceneNode(group);
                    _pathGroups[entry] = group;
                }
            }

            _render.RequestDraw(EnumUpdateFlags.Scene);
        }

        public void Dispose()
        {
            _simFinishTimer?.Stop();
            if (_render.IsReady)
                StopSimulation();
            _simulator.Dispose();
            _session.Dispose();
        }

        // ==================== 仿真 ====================

        /// <summary>仿真播放中；视口浮层与仿真入口图标据此切换</summary>
        [ObservableProperty]
        private bool _isSimulating;

        /// <summary>右下角仿真入口图标：空闲 ▶，播放中 ■（此时点击=退出仿真）</summary>
        public string SimEntryIcon => IsSimulating ? "■" : "▶";

        public string SimEntryToolTip => IsSimulating ? "退出仿真" : "开始仿真";

        partial void OnIsSimulatingChanged(bool value)
        {
            OnPropertyChanged(nameof(SimEntryIcon));
            OnPropertyChanged(nameof(SimEntryToolTip));
        }

        /// <summary>仿真暂停中；浮层上的暂停键在 ‖/▶ 间切换</summary>
        [ObservableProperty]
        private bool _isSimPaused;

        /// <summary>暂停键图标：播放中显示 ‖，暂停时显示 ▶</summary>
        public string SimPauseIcon => IsSimPaused ? "▶" : "‖";

        public string SimPauseToolTip => IsSimPaused ? "继续" : "暂停";

        /// <summary>
        /// 播完检测：仿真自然走完（Finished）时内核会话有状态可查，
        /// 轮询到即提示完成并保持仿真态——工具条不收起，切削结果留在视口，
        /// 可点工具条上的 ⟲ 重新播放。
        /// </summary>
        private readonly DispatcherTimer? _simFinishTimer = CreateSimFinishTimer();

        private static DispatcherTimer CreateSimFinishTimer()
        {
            return new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(200),
            };
        }

        partial void OnIsSimPausedChanged(bool value)
        {
            OnPropertyChanged(nameof(SimPauseIcon));
            OnPropertyChanged(nameof(SimPauseToolTip));
        }

        [ObservableProperty]
        private int _simRateIndex = 4;

        /// <summary>播放倍率选项，与 SimRates 一一对应</summary>
        public IReadOnlyList<string> SimRateOptions { get; } = new[] { "0.5×", "1×", "2×", "4×", "10×" };

        private static readonly double[] SimRates = { 0.5, 1.0, 2.0, 4.0, 10.0 };

        partial void OnSimRateIndexChanged(int value)
        {
            if (value >= 0 && value < SimRates.Length)
                _simulator.SetPlaybackRate(SimRates[value]);
        }

        [RelayCommand]
        private void ToggleSimulation()
        {
            if (_simulator.IsActive)
            {
                StopSimulation();
                return;
            }

            StartSimulation();
        }

        [RelayCommand]
        private void ReplaySimulation()
        {
            if (_simulator.IsActive)
                StopSimulation();
            StartSimulation();
        }

        private void StartSimulation()
        {
            if (!_render.IsReady)
            {
                StatusMessage = "三维视口未就绪";
                return;
            }

            if (_simulator.Start(_render.View, out var message))
            {
                IsSimulating = true;
                IsSimPaused = false;
                StatusMessage = "仿真已开始：刀具按刀位切削毛坯";
                SetSimRate();
                StartSimFinishWatch();
            }
            else
            {
                StatusMessage = message;
            }
        }

        [RelayCommand]
        private void ToggleSimPause()
        {
            if (!_simulator.IsActive)
                return;

            _simulator.TogglePause();
            IsSimPaused = _simulator.IsPaused;
            StatusMessage = _simulator.IsPaused ? "仿真已暂停" : "仿真继续";
        }

        private void StopSimulation()
        {
            _simFinishTimer?.Stop();
            if (!_simulator.IsActive)
                return;

            if (_render.IsReady)
                _simulator.Stop(_render.View);
            else
                _simulator.Dispose();

            IsSimulating = false;
            IsSimPaused = false;
            StatusMessage = "仿真已停止";
        }

        private void StartSimFinishWatch()
        {
            if (_simFinishTimer == null)
                return;
            _simFinishTimer.Tick -= OnSimFinishTick;
            _simFinishTimer.Tick += OnSimFinishTick;
            _simFinishTimer.Start();
        }

        private void OnSimFinishTick(object? sender, EventArgs e)
        {
            if (!_simulator.IsActive)
            {
                _simFinishTimer?.Stop();
                return;
            }
            if (!_simulator.IsFinished)
                return;

            _simFinishTimer?.Stop();
            // 自然播完：保持仿真态（工具条不收起），⟲ 可重新播放
            IsSimPaused = false;
            StatusMessage = "仿真播放完成，可点 ⟲ 重播";
        }

        private void SetSimRate()
        {
            if (SimRateIndex >= 0 && SimRateIndex < SimRates.Length)
                _simulator.SetPlaybackRate(SimRates[SimRateIndex]);
        }
    }
}
