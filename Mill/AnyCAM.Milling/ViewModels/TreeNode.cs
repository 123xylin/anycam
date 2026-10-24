using AnyCAM.Milling.Engine;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace AnyCAM.Milling.ViewModels
{
    /// <summary>树节点的种类，决定右侧面板显示什么</summary>
    public enum TreeNodeKind
    {
        Group,
        Part,
        Stock,
        Coordinate,
        Tool,
        Operation
    }

    /// <summary>
    /// CAM 树节点。层级是：模型 / 坐标系 / 刀具 / 加工策略，
    /// 每个分组下有若干叶节点；策略行还带状态徽标和显隐眼睛。
    /// </summary>
    public partial class TreeNode : ObservableObject
    {
        public TreeNode(TreeNodeKind kind, string title, string? meta = null)
        {
            Kind = kind;
            Title = title;
            Meta = meta;
        }

        public TreeNodeKind Kind { get; }

        [ObservableProperty]
        private string _title;

        /// <summary>行内副标题</summary>
        [ObservableProperty]
        private string? _meta;

        /// <summary>分组是否展开</summary>
        [ObservableProperty]
        private bool _isExpanded = true;

        [ObservableProperty]
        private bool _isSelected;

        /// <summary>
        /// 行级显隐开关：模型/毛坯/工序行有眼睛，其余行没有。
        /// 状态变化只上报给订阅者（ViewModel 拿去控制场景节点），节点自己不碰场景。
        /// </summary>
        public event Action<TreeNode, bool>? VisibilityChanged;

        [ObservableProperty]
        private bool _isVisible = true;

        partial void OnIsVisibleChanged(bool value)
        {
            OnPropertyChanged(nameof(RowForeground));
            VisibilityChanged?.Invoke(this, value);
        }

        /// <summary>只有这三类行显示眼睛开关</summary>
        public bool HasVisibility
            => Kind is TreeNodeKind.Part or TreeNodeKind.Stock or TreeNodeKind.Operation;

        /// <summary>
        /// 隐藏时整行压暗：眼睛图标只有 12px，光看图标分辨不出当前状态，
        /// 行文字一起变灰才能一眼看出这一层不参与显示。
        /// </summary>
        public string RowForeground => IsVisible ? "#DCE4EF" : "#5A6B82";

        /// <summary>对应的引擎工序；非策略行为 null</summary>
        public OperationEntry? Entry { get; init; }

        /// <summary>策略行的刀路颜色圆点：与视口里该工序刀路同色，粗暖/精冷/孔紫</summary>
        public string PathColorHex
            => Kind == TreeNodeKind.Operation && Entry != null
                ? CamSession.GetPathColorHex(Entry.Strategy)
                : string.Empty;

        /// <summary>只有策略行显示颜色圆点</summary>
        public bool HasPathColor => Kind == TreeNodeKind.Operation && Entry != null;

        public ObservableCollection<TreeNode> Children { get; } = new();

        // ==================== 策略行徽标 ====================

        /// <summary>状态徽标图标：紧凑圆形徽章只放一个字符，完整含义看悬停提示（StatusText）</summary>
        public string StatusIcon => Entry?.State switch
        {
            OperationState.Computed => "✓",
            OperationState.Dirty => "⟳",
            OperationState.Failed => "✕",
            _ => "○",
        };

        /// <summary>完整状态文字，用作图标徽章的 ToolTip</summary>
        public string StatusText => Entry?.State switch
        {
            OperationState.Computed => "已计算",
            OperationState.Dirty => "待计算",
            OperationState.Failed => "失败",
            _ => "未生成",
        };

        public string StatusBackground => Entry?.State switch
        {
            OperationState.Computed => "#143524",
            OperationState.Dirty => "#3A2A10",
            OperationState.Failed => "#3A1618",
            _ => "#232D3D",
        };

        public string StatusForeground => Entry?.State switch
        {
            OperationState.Computed => "#22C55E",
            OperationState.Dirty => "#F59E0B",
            OperationState.Failed => "#EF4444",
            _ => "#64748B",
        };

        /// <summary>只有策略行显示徽标</summary>
        public bool HasStatus => Kind == TreeNodeKind.Operation;

        /// <summary>
        /// 策略分组行的行内命令（新建策略 / 全部计算）是否可见。
        /// 命令挂在分组标题后面比放在顶部工具栏更贴近使用场景。
        /// </summary>
        public bool ShowStrategyActions => Kind == TreeNodeKind.Group && Title == "加工策略";

        /// <summary>刀具分组行的行内命令（添加刀具）是否可见</summary>
        public bool ShowToolActions => Kind == TreeNodeKind.Group && Title == "刀具";

        /// <summary>分组节点才画展开箭头；WPF 默认对空集合也画箭头，这里显式控制</summary>
        public bool IsGroup => Kind == TreeNodeKind.Group;


        /// <summary>
        /// 行内副标题：策略显示工艺摘要，其余显示规格。
        /// 策略行不再复述「未生成 / 待计算 / 失败」——那是右侧徽标的职责，
        /// 同一句话在一行里出现两次既占地方又读不出重点。
        /// </summary>
        public string InlineMeta => Kind == TreeNodeKind.Operation
            ? Entry?.State == OperationState.Computed
                ? $"{Entry.CommandCount} 指令"   // 「已计算」是徽标的职责，副标题不重复
                : StrategyMeta
            : Meta ?? string.Empty;

        /// <summary>策略的工艺摘要，如「型腔 · 环切」；非策略行为空</summary>
        public string StrategyMeta { get; init; } = string.Empty;

        /// <summary>徽标随工序状态变化，供 ViewModel 主动刷新</summary>
        public void RefreshStatus()
        {
            OnPropertyChanged(nameof(StatusIcon));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusBackground));
            OnPropertyChanged(nameof(StatusForeground));
            OnPropertyChanged(nameof(HasStatus));
            OnPropertyChanged(nameof(InlineMeta));
        }
    }
}
