using AnyCAD.Foundation;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AnyCAM.Milling.Engine
{
    /// <summary>
    /// 工序状态。界面徽标颜色与它一一对应。
    /// </summary>
    public enum OperationState
    {
        /// <summary>刚创建，还没有算过 —— 灰</summary>
        NotGenerated,
        /// <summary>算过但参数改过，需要重算 —— 橙</summary>
        Dirty,
        /// <summary>已算，结果有效 —— 绿</summary>
        Computed,
        /// <summary>计算失败 —— 红</summary>
        Failed
    }

    /// <summary>
    /// 引擎侧的工序条目。
    /// <para>
    /// 刻意不继承任何文档对象：工序、刀具、毛坯全部是引擎类的直接实例，
    /// 既不进 Undo 事务，也不落盘。
    /// </para>
    /// </summary>
    public sealed class OperationEntry : IDisposable
    {
        public OperationEntry(string name, string strategy)
        {
            Name = name;
            Strategy = strategy;
        }

        public string Name { get; internal set; }
        public string Strategy { get; set; }

        /// <summary>本工序用的刀；名字对应 <see cref="ToolEntry.Name"/>，可在界面上换</summary>
        public string ToolName { get; set; } = string.Empty;

        /// <summary>按孔径分组钻孔时：本工序只加工该直径（mm）</summary>
        public double? DrillDiameterMm { get; internal set; }

        /// <summary>本钻孔工序包含的孔数（识别结果）</summary>
        public int DrillHoleCount { get; internal set; }

        /// <summary>按槽规格分组挖槽时：代表尺寸与底面列表（与 <see cref="PocketSlotGroup"/> 对应）</summary>
        public double? PocketLengthMm { get; internal set; }
        public double? PocketWidthMm { get; internal set; }
        public double? PocketDepthMm { get; internal set; }
        public double? PocketAreaMm2 { get; internal set; }
        public int PocketSlotCount { get; internal set; }
        internal List<TopoShape>? PocketBottomFaces { get; set; }

        public PathOperation? Operation { get; private set; }

        public OperationState State { get; private set; } = OperationState.NotGenerated;

        /// <summary>最近一次计算的失败原因；成功时为空</summary>
        public string? LastError { get; private set; }

        // ---- 刀路统计，供状态栏显示 ----
        public int CommandCount { get; private set; }
        public double PathLength { get; private set; }
        public double CycleSeconds { get; private set; }

        internal void Attach(PathOperation operation) => Operation = operation;

        /// <summary>参数被改动：已算结果失效，回到待计算</summary>
        public void MarkDirty()
        {
            if (State is OperationState.Computed or OperationState.Failed)
            {
                State = OperationState.Dirty;
                LastError = null;
            }
        }

        internal void MarkComputed(Toolpath? path, double cycleSeconds)
        {
            CommandCount = (int)(path?.GetSize() ?? 0);
            PathLength = path?.GetLength() ?? 0.0;
            CycleSeconds = cycleSeconds;
            State = OperationState.Computed;
            LastError = null;
        }

        internal void MarkFailed(string error)
        {
            CommandCount = 0;
            PathLength = 0.0;
            CycleSeconds = 0.0;
            State = OperationState.Failed;
            LastError = error;
        }

        public void Dispose()
        {
            Operation?.Dispose();
            Operation = null;
        }
    }

    /// <summary>
    /// 刀库里的刀：名字给树和下拉框用，Controller 持有该刀的进给与转速。
    /// 不同策略可以选不同的刀，各刀的进给互相独立。
    /// </summary>
    public sealed class ToolEntry
    {
        public ToolEntry(string name, string summary, Tool tool)
        {
            Name = name;
            Summary = summary;
            Tool = tool;
            Controller = new ToolController
            {
                // SWIG 包装器没有无参构造暴露的初始化入口时，属性逐一设
            };
            Controller.SetTool(tool);
            Controller.SetToolNumber(1);
            Controller.SetSpindleSpeed(12000);
            Controller.SetSpindleClockwise(true);
            Controller.SetCoolant(EnumPostCoolant.Flood);
            Controller.SetVertRapid(300);
            Controller.SetHorizRapid(6000);
        }

        public string Name { get; }
        public string Summary { get; }
        public Tool Tool { get; }
        public ToolController Controller { get; }
    }

    /// <summary>
    /// 全流程只依赖 Craft/Kernel 的一次加工会话。
    /// <para>
    /// 不创建 Document / Element / Schema，不包 Undo 事务。零件、毛坯、刀具、工序都是
    /// 引擎对象的直接持有者；界面改动直接写回这些对象，再调 <see cref="Compute"/>。
    /// </para>
    /// </summary>
    public sealed class CamSession : IDisposable
    {
        private readonly List<OperationEntry> _operations = new();
        private readonly List<ToolEntry> _tools = new();
        public WorkCoordinateSetup WorkZero { get; } = new();

        private IReadOnlyList<HoleDiameterGroup> _holeGroups = Array.Empty<HoleDiameterGroup>();
        private IReadOnlyList<PocketSlotGroup> _pocketGroups = Array.Empty<PocketSlotGroup>();
        private int _pocketBottomSkippedCount;
        private TopoShape? _part;
        private TopoShape? _geometryCachePart;
        private double? _cachedHoleBottomZ;
        private List<TopoShape>? _cachedPocketBottomFaces;
        /// <summary>当前零件是否为内置示例零件（只有它知道孔位，才能给等高填孔）</summary>
        private bool _partIsTestPiece;
        /// <summary>填实钻孔后的等高实体，按零件缓存</summary>
        private TopoShape? _waterlineSolid;
        /// <summary>用户指定顶面后的简述（面铣/挖槽基准）</summary>
        private string _machiningTopSummary = "顶面（自动 · +Z 最高）";

        public CamSession()
        {
            // T01 球头刀 D8：面铣 / 槽壁精加工
            var ball = new Tool(EnumToolType.EndMill);
            ball.SetDiameter(8);
            ball.SetCornerRadius(4);      // 等于半径 ⇒ 球头刀
            ball.SetFluteLength(20);
            ball.SetLength(45);
            ball.SetFluteCount(4);
            ball.SetHorizFeed(1200);
            ball.SetVertFeed(300);
            _tools.Add(new ToolEntry("T01 · 球头刀 D8", "HSK63A · 4 刃", ball));

            // T02 平底刀 D10：2D 挖槽 / 轮廓（圆角 0 即平底）
            var flat = new Tool(EnumToolType.EndMill);
            flat.SetDiameter(10);
            flat.SetCornerRadius(0);
            flat.SetFluteLength(25);
            flat.SetLength(50);
            flat.SetFluteCount(3);
            flat.SetHorizFeed(1000);
            flat.SetVertFeed(250);
            _tools.Add(new ToolEntry("T02 · 平底刀 D10", "HSK63A · 3 刃", flat));

            // T03 钻头 φ10：钻孔（尖端 140°）
            var drill = new Tool(EnumToolType.EndMill);
            drill.SetDiameter(10);
            drill.SetCornerRadius(0);
            drill.SetTipAngle(140);
            drill.SetFluteLength(30);
            drill.SetLength(60);
            drill.SetFluteCount(2);
            drill.SetHorizFeed(400);
            drill.SetVertFeed(120);
            _tools.Add(new ToolEntry("T03 · 钻头 φ10", "HSK63A · 2 刃 · 140°", drill));
        }

        /// <summary>最近一次孔识别结果（按直径分组）</summary>
        public IReadOnlyList<HoleDiameterGroup> HoleGroups => _holeGroups;

        /// <summary>最近一次槽识别结果（按深度+面积分组，每组对应一条 2D 挖槽工序）</summary>
        public IReadOnlyList<PocketSlotGroup> PocketGroups => _pocketGroups;

        /// <summary>为刀库补一把与孔径匹配的钻头；已存在同直径钻头则复用</summary>
        public ToolEntry EnsureDrillTool(double diameterMm)
        {
            foreach (var entry in _tools)
            {
                if (entry.Tool.GetTipAngle() < 1.0)
                    continue;
                if (Math.Abs(entry.Tool.GetDiameter() - diameterMm) < 0.08)
                    return entry;
            }

            var label = $"T{_tools.Count + 1:D2} · 钻头 φ{diameterMm:F0}";
            var drill = new Tool(EnumToolType.EndMill);
            drill.SetDiameter(diameterMm);
            drill.SetCornerRadius(0);
            drill.SetTipAngle(140);
            drill.SetFluteLength(Math.Max(20, diameterMm * 3));
            drill.SetLength(Math.Max(40, diameterMm * 6));
            drill.SetFluteCount(2);
            drill.SetHorizFeed(400);
            drill.SetVertFeed(120);

            var toolNumber = _tools.Count + 1;
            var created = new ToolEntry(label, $"HSK63A · 2 刃 · 140° · φ{diameterMm:F0}", drill);
            created.Controller.SetToolNumber(toolNumber);
            _tools.Add(created);
            return created;
        }

        /// <summary>
        /// 按识别到的孔径重建「钻孔」工序：每种直径一条 OP + 一把匹配钻头。
        /// 返回新建的工序条目（可能为空）。
        /// </summary>
        public IReadOnlyList<OperationEntry> RebuildDrillOperationsFromPart()
        {
            if (_part == null)
            {
                _holeGroups = Array.Empty<HoleDiameterGroup>();
            }
            else
            {
                var report = HoleRecognizer.Analyze(_part);
                _holeGroups = report.Groups;
                SessionLog.Info("孔识别 · " + report.ToSummaryText().Replace("\n", " · "));
            }

            var firstDrillIndex = _operations.FindIndex(o => o.Strategy == "钻孔");
            for (var i = _operations.Count - 1; i >= 0; i--)
            {
                if (_operations[i].Strategy != "钻孔")
                    continue;

                var removed = _operations[i];
                _operations.RemoveAt(i);
                removed.Dispose();
            }

            var insertAt = firstDrillIndex >= 0 ? firstDrillIndex : _operations.Count;
            var created = new List<OperationEntry>();

            if (_holeGroups.Count == 0)
            {
                var fallback = InsertOperation("钻孔", insertAt);
                fallback.DrillDiameterMm = null;
                fallback.DrillHoleCount = 0;
                created.Add(fallback);
                return created;
            }

            foreach (var group in _holeGroups)
            {
                var tool = EnsureDrillTool(group.DiameterMm);
                var entry = InsertOperation("钻孔", insertAt++);
                entry.DrillDiameterMm = group.DiameterMm;
                entry.DrillHoleCount = group.MachinableFromTopCount;
                entry.ToolName = tool.Name;
                entry.Name = $"OP{IndexOf(entry) + 1} · 钻孔 φ{group.DiameterMm:F0}";
                created.Add(entry);
            }

            RenumberOperationNames();
            return created;
        }

        /// <summary>
        /// 按识别到的槽规格重建「2D 挖槽」工序：深度与面积相同的一类槽合并为一条 OP。
        /// </summary>
        public IReadOnlyList<OperationEntry> RebuildPocketOperationsFromPart()
        {
            var slots = PocketSlotRecognizer.Recognize(_part);
            _pocketBottomSkippedCount = PocketSlotRecognizer.CountBottomAccess(slots);
            _pocketGroups = PocketSlotRecognizer.GroupByDepthAndArea(slots);

            var firstPocketIndex = _operations.FindIndex(o => o.Strategy == "2D挖槽");
            for (var i = _operations.Count - 1; i >= 0; i--)
            {
                if (_operations[i].Strategy != "2D挖槽")
                    continue;

                var removed = _operations[i];
                _operations.RemoveAt(i);
                removed.Dispose();
            }

            var insertAt = firstPocketIndex >= 0 ? firstPocketIndex : _operations.Count;
            var created = new List<OperationEntry>();

            if (_pocketGroups.Count == 0)
            {
                var fallback = InsertOperation("2D挖槽", insertAt);
                ClearPocketAssignment(fallback);
                created.Add(fallback);
                RenumberOperationNames();
                return created;
            }

            foreach (var group in _pocketGroups)
            {
                var entry = InsertOperation("2D挖槽", insertAt++);
                AssignPocketGroup(entry, group);
                ApplyDefaultParams(entry.Operation!, "2D挖槽", entry);
                created.Add(entry);
            }

            RenumberOperationNames();
            return created;
        }

        /// <summary>
        /// 与 <see cref="RebuildPocketOperationsFromPart"/> 共用 <see cref="_pocketGroups"/>：
        /// 每种槽规格一条槽壁精加工 OP，底面/尺寸与对应挖槽一致。
        /// </summary>
        public IReadOnlyList<OperationEntry> RebuildSlotWallOperationsFromPart()
        {
            var firstIndex = _operations.FindIndex(o => o.Strategy == "槽壁精加工");
            for (var i = _operations.Count - 1; i >= 0; i--)
            {
                if (_operations[i].Strategy != "槽壁精加工")
                    continue;

                var removed = _operations[i];
                _operations.RemoveAt(i);
                removed.Dispose();
            }

            var insertAt = firstIndex >= 0 ? firstIndex : _operations.Count;
            var created = new List<OperationEntry>();

            if (_pocketGroups.Count == 0)
            {
                var fallback = InsertOperation("槽壁精加工", insertAt);
                ClearPocketAssignment(fallback);
                created.Add(fallback);
                RenumberOperationNames();
                return created;
            }

            foreach (var group in _pocketGroups)
            {
                var entry = InsertOperation("槽壁精加工", insertAt++);
                AssignPocketGroup(entry, group);
                ApplyDefaultParams(entry.Operation!, "槽壁精加工", entry);
                created.Add(entry);
            }

            RenumberOperationNames();
            return created;
        }

        private static void AssignPocketGroup(OperationEntry entry, PocketSlotGroup group)
        {
            entry.PocketLengthMm = group.LengthMm;
            entry.PocketWidthMm = group.WidthMm;
            entry.PocketDepthMm = group.DepthMm;
            entry.PocketAreaMm2 = group.AreaMm2;
            entry.PocketSlotCount = group.Count;
            entry.PocketBottomFaces = group.BottomFaces.ToList();
        }

        private static void ClearPocketAssignment(OperationEntry entry)
        {
            entry.PocketLengthMm = null;
            entry.PocketWidthMm = null;
            entry.PocketDepthMm = null;
            entry.PocketAreaMm2 = null;
            entry.PocketSlotCount = 0;
            entry.PocketBottomFaces = null;
        }

        private int IndexOf(OperationEntry entry) => _operations.IndexOf(entry);

        private void RenumberOperationNames()
        {
            for (var i = 0; i < _operations.Count; i++)
            {
                var entry = _operations[i];
                if (entry.Strategy == "钻孔" && entry.DrillDiameterMm is double d)
                {
                    var drillGroup = _holeGroups.FirstOrDefault(g => Math.Abs(g.DiameterMm - d) < 0.08);
                    var title = drillGroup?.DrillTitle ?? $"φ{d:F0}";
                    entry.Name = $"OP{i + 1} · 钻孔 {title}";
                }
                else if (entry.Strategy == "2D挖槽" && entry.PocketSlotCount > 0 && entry.PocketDepthMm is double pocketDepth)
                {
                    var l = entry.PocketLengthMm ?? 0;
                    var w = entry.PocketWidthMm ?? 0;
                    var suffix = entry.PocketSlotCount > 1 ? $" (×{entry.PocketSlotCount})" : string.Empty;
                    entry.Name = $"OP{i + 1} · 2D挖槽 · {l:F0}×{w:F0}×{pocketDepth:F0}{suffix}";
                }
                else if (entry.Strategy == "槽壁精加工" && entry.PocketSlotCount > 0 && entry.PocketDepthMm is double wallDepth)
                {
                    var l = entry.PocketLengthMm ?? 0;
                    var w = entry.PocketWidthMm ?? 0;
                    var suffix = entry.PocketSlotCount > 1 ? $" (×{entry.PocketSlotCount})" : string.Empty;
                    entry.Name = $"OP{i + 1} · 槽壁精加工 · {l:F0}×{w:F0}×{wallDepth:F0}{suffix}";
                }
                else
                    entry.Name = $"OP{i + 1} · {entry.Strategy}";
            }
        }

        private OperationEntry InsertOperation(string strategyName, int index)
        {
            var entry = new OperationEntry($"OP{_operations.Count + 1} · {strategyName}", strategyName)
            {
                ToolName = DefaultToolName(strategyName),
            };
            var op = CreateOperation(strategyName);
            ApplyDefaultParams(op, strategyName, entry);
            entry.Attach(op);

            index = Math.Clamp(index, 0, _operations.Count);
            _operations.Insert(index, entry);
            return entry;
        }

        /// <summary>刀库：树上的「刀具」分组与工序换刀下拉都从这里来</summary>
        public IReadOnlyList<ToolEntry> Tools => _tools;

        /// <summary>按名字取刀（名字即 <see cref="ToolEntry.Name"/>）；找不到回第一把</summary>
        public ToolEntry GetTool(string? name) =>
            _tools.FirstOrDefault(t => t.Name == name) ?? _tools[0];

        /// <summary>
        /// 当前刀：选中工序的刀；没有选中工序时取第一把。
        /// 刀具页签的编辑都落在它身上。
        /// </summary>
        public ToolController ToolController => SelectedTool?.Controller ?? _tools[0].Controller;

        /// <summary>界面当前正在编辑的刀；由 VM 随选中工序同步</summary>
        public ToolEntry? SelectedTool { get; set; }

        /// <summary>毛坯；加载零件后按包围盒自动生成</summary>
        public Stock? Stock { get; private set; }

        public TopoShape? Part => _part;

        /// <summary>当前加工顶面说明（自动对齐或用户拾取面后更新）。</summary>
        public string MachiningTopSummary => _machiningTopSummary;

        public IReadOnlyList<OperationEntry> Operations => _operations;

        /// <summary>
        /// 后处理器名。取值必须是 <see cref="PostNames"/> 里的小写标识（Fanuc 即 "fanuc"），
        /// 显示用的全称来自 <see cref="PostDescriptions"/>，两者不能混用。
        /// </summary>
        public string PostName { get; private set; } = "fanuc";

        public void SetPostName(string name) => PostName = name;

        /// <summary>可用后处理器标识，即 <see cref="PostProcessor.Create"/> 接受的键</summary>
        public static IReadOnlyList<string> PostNames => new List<string>(PostProcessor.GetNames());

        /// <summary>可用后处理器说明，与 <see cref="PostNames"/> 同序</summary>
        public static IReadOnlyList<string> PostDescriptions => new List<string>(PostProcessor.GetDescriptions());

        /// <summary>输出单位；影响程序头的 G21/G20 与坐标换算</summary>
        public EnumPostUnits PostUnits { get; private set; } = EnumPostUnits.Millimeter;

        public void SetPostUnits(EnumPostUnits units) => PostUnits = units;

        // ==================== 零件与毛坯 ====================

        /// <summary>
        /// 载入零件。参为 null 时内置一块 120×120×40 的示例零件：
        /// 顶面中间开一条盲槽（验证 2D 挖槽），四角各打一个盲孔（验证钻孔/轮廓），
        /// 没有外部 STEP 时也能覆盖各族工艺。
        /// </summary>
        public void LoadPart(TopoShape? shape = null)
        {
            if (shape == null)
            {
                _part = BuildTestPart();
                _partIsTestPiece = true;
            }
            else
            {
                var aligned = AlignPartForCam(shape);
                // 大件/多面体做 Unify 会在 UI 线程卡数分钟
                _part = ShouldRunHeavyShapePrep(aligned)
                    ? PreparePartForDisplay(aligned)
                    : aligned;
                _partIsTestPiece = false;
            }

            InvalidateGeometryCache();
            WorkZero.ApplyPreset(WorkZeroPreset.BboxMinCorner, _part);
            // 零件换了，填孔实体必须跟着重算
            _waterlineSolid = null;
            _machiningTopSummary = "顶面（自动 · +Z 最高）";
            RebuildStock();
        }

        /// <summary>
        /// 将拾取到的面设为加工顶面：旋转零件使该面外法向对齐世界 +Z，再平移到包围盒最小角为原点。
        /// </summary>
        public bool TryApplyMachiningTopFace(TopoShape pickedFace, out string message)
        {
            message = string.Empty;
            if (_part == null)
            {
                message = "尚未加载零件";
                return false;
            }

            if (pickedFace == null || pickedFace.GetBBox().IsVoid())
            {
                message = "未拾取到有效面";
                return false;
            }

            if (!PartTopOrientation.TryGetOutwardNormal(pickedFace!, _part, out var normal))
            {
                message = "无法计算该面的法向，请换一面或检查模型";
                return false;
            }

            try
            {
                SessionLog.Info("ApplyMachiningTopFace · 旋转零件");
                _part = PartTopOrientation.AlignSolidTopNormalToZ(_part, normal);
                _part = TranslateMinCornerToOrigin(_part);
                if (ShouldRunHeavyShapePrep(_part))
                    _part = PreparePartForDisplay(_part);

                InvalidateGeometryCache();
                _waterlineSolid = null;
                RebuildStock();
                WorkZero.ApplyPreset(WorkZeroPreset.BboxMinCorner, _part);
                _machiningTopSummary = "顶面（已指定） · " + PartTopOrientation.DescribeTopFace(_part);
                message = _machiningTopSummary;
                return true;
            }
            catch (Exception ex)
            {
                SessionLog.Error("ApplyMachiningTopFace 失败", ex);
                message = "设定顶面失败：" + ex.Message;
                return false;
            }
        }

        public void SetWorkZeroPreset(WorkZeroPreset preset) => WorkZero.ApplyPreset(preset, _part);

        public void SetWorkZeroOrigin(double x, double y, double z) => WorkZero.SetOrigin(x, y, z);

        /// <summary>视口内 MCS 坐标轴长度（mm）。</summary>
        public float GetMcsAxisLength()
        {
            if (_part == null)
                return 80f;

            var bounds = _part.GetBBox();
            if (bounds.IsVoid())
                return 80f;

            var dx = bounds.CornerMax().X() - bounds.CornerMin().X();
            var dy = bounds.CornerMax().Y() - bounds.CornerMin().Y();
            var dz = bounds.CornerMax().Z() - bounds.CornerMin().Z();
            return (float)Math.Clamp(Math.Max(dx, Math.Max(dy, dz)) * 0.12, 40.0, 200.0);
        }

        private void InvalidateGeometryCache()
        {
            _geometryCachePart = null;
            _cachedHoleBottomZ = null;
            _cachedPocketBottomFaces = null;
        }

        /// <summary>仅对小件做 Unify；STEP 横梁等超大模型跳过以免卡死。</summary>
        private static bool ShouldRunHeavyShapePrep(TopoShape part)
        {
            var bounds = part.GetBBox();
            if (bounds.IsVoid())
                return true;

            var dx = bounds.CornerMax().X() - bounds.CornerMin().X();
            var dy = bounds.CornerMax().Y() - bounds.CornerMin().Y();
            var dz = bounds.CornerMax().Z() - bounds.CornerMin().Z();
            if (Math.Max(dx, Math.Max(dy, dz)) > 400.0)
                return false;

            var faces = part.GetChildren(EnumTopoShapeType.Topo_FACE);
            return faces == null || faces.Count <= 600;
        }

        /// <summary>
        /// 导入对齐：最长包围盒边 → +X，次长 → +Y，最短 → +Z；再平移到最小角为 (0,0,0)。
        /// </summary>
        private static TopoShape AlignPartForCam(TopoShape part)
        {
            part = OrientBoundingBoxLongestAlongX(part);
            return TranslateMinCornerToOrigin(part);
        }

        /// <summary>
        /// 绕世界轴旋转，使 AABB 满足 长→X、宽→Y、厚→Z（薄板平放、顶面朝 +Z）。
        /// </summary>
        private static TopoShape OrientBoundingBoxLongestAlongX(TopoShape part)
        {
            for (var step = 0; step < 4; step++)
            {
                if (!TryGetBoxExtents(part, out var dx, out var dy, out var dz, out var tol))
                    return part;

                var xLongest = dx + tol >= dy && dx + tol >= dz;
                var yAtLeastZ = dy + tol >= dz;
                if (xLongest && yAtLeastZ)
                    return part;

                if (!xLongest)
                {
                    part = dy >= dz - tol
                        ? RotatePart(part, 0, 0, 1, -Math.PI / 2)
                        : RotatePart(part, 0, 1, 0, Math.PI / 2);
                    continue;
                }

                part = RotatePart(part, 1, 0, 0, Math.PI / 2);
            }

            return part;
        }

        private static bool TryGetBoxExtents(
            TopoShape part,
            out double dx,
            out double dy,
            out double dz,
            out double tol)
        {
            dx = dy = dz = tol = 0;
            var bounds = part.GetBBox();
            if (bounds.IsVoid())
                return false;

            dx = bounds.CornerMax().X() - bounds.CornerMin().X();
            dy = bounds.CornerMax().Y() - bounds.CornerMin().Y();
            dz = bounds.CornerMax().Z() - bounds.CornerMin().Z();
            var scale = Math.Max(dx, Math.Max(dy, dz));
            if (scale < 1e-9)
                return false;

            tol = scale * 1e-6;
            return true;
        }

        private static TopoShape RotatePart(TopoShape part, double ax, double ay, double az, double angleRad)
        {
            var tr = new GTrsf();
            tr.SetRotation(new GAx1(new GPnt(0, 0, 0), new GDir(ax, ay, az)), angleRad);
            return TransformTool.Transform(part, tr);
        }

        private static TopoShape TranslateMinCornerToOrigin(TopoShape part)
        {
            var bounds = part.GetBBox();
            if (bounds.IsVoid())
                return part;

            var min = bounds.CornerMin();
            var tr = new GTrsf();
            tr.SetTranslation(new GVec(-min.X(), -min.Y(), -min.Z()));
            return TransformTool.Transform(part, tr);
        }

        private static TopoShape PreparePartForDisplay(TopoShape part)
        {
            var unified = BooleanTool.Unify(part, true, true, true);
            return unified ?? part;
        }

        /// <summary>内置示例零件的四角盲孔孔心；钻孔与等高填孔共用这一份数据</summary>
        private static readonly (double X, double Y)[] TestHoleCenters =
        {
            (18, 18), (102, 18), (18, 102), (102, 102),
        };

        private const double TestPartHeight = 40.0;
        private const double TestHoleRadius = 5.0;
        private const double TestHoleDepth = 20.0;
        private const double TestSlotOffset = 36.0;
        private const double TestSlotSize = 48.0;
        private const double TestSlotDepth = 20.0;

        /// <summary>内置示例零件：120×120×40，中间方槽 48×48×20，四角 φ10×20 盲孔，孔心距两边各 18</summary>
        private static TopoShape BuildTestPart()
        {
            const double height = TestPartHeight;
            var part = ShapeBuilder.MakeBox(new GAx2(new GPnt(0, 0, 0), new GDir(0, 0, 1)), 120, 120, height);

            // 中间开方槽：居中 48×48，顶面向下 20 深（顶面 Z=40）
            part = BooleanTool.Cut(part, BuildSlotSolid());

            // 四角打孔：φ10 盲孔深 20，孔心距两边各 18
            foreach (var center in TestHoleCenters)
            {
                var hole = ShapeBuilder.MakeCylinder(
                    new GAx2(new GPnt(center.X, center.Y, height - TestHoleDepth), new GDir(0, 0, 1)),
                    TestHoleRadius, TestHoleDepth, 0);
                part = BooleanTool.Cut(part, hole);
            }

            return BooleanTool.Unify(part, true, true, true);
        }

        /// <summary>槽的腔体体积（36..84 × 36..84，Z 20..40）；开槽与槽壁精修共用</summary>
        private static TopoShape BuildSlotSolid()
        {
            return ShapeBuilder.MakeBox(
                new GAx2(new GPnt(TestSlotOffset, TestSlotOffset, TestPartHeight - TestSlotDepth), new GDir(0, 0, 1)),
                TestSlotSize, TestSlotSize, TestSlotDepth);
        }

        /// <summary>
        /// 水线工序（槽壁精加工的外模回退路径）用的实体：把钻出来的孔重新填实。
        /// <para>
        /// 孔壁由钻孔保证，不该再被球刀等高沿孔壁绕一圈：球刀进不了 φ10 孔底，
        /// 会在孔底留下等于刀半径的圆角、把已钻好的孔壁刮毛，每层还多出 4 组空刀环。
        /// 填实后每层截面只剩外形与槽壁。
        /// </para>
        /// </summary>
        private TopoShape? GetWaterlineSolid()
        {
            if (_part == null)
                return null;

            // 外部模型没有孔位信息，原样交出去（要跳过孔壁得由界面提供孔位）
            if (!_partIsTestPiece)
                return _part;

            if (_waterlineSolid != null)
                return _waterlineSolid;

            var solid = _part;
            foreach (var center in TestHoleCenters)
            {
                var plug = ShapeBuilder.MakeCylinder(
                    new GAx2(new GPnt(center.X, center.Y, TestPartHeight - TestHoleDepth), new GDir(0, 0, 1)),
                    TestHoleRadius, TestHoleDepth, 0);
                solid = BooleanTool.Boolean(solid, plug, EnumBooleanType.Fuse);
            }

            _waterlineSolid = solid == null ? _part : BooleanTool.Unify(solid, true, true, true);
            return _waterlineSolid;
        }

        /// <summary>按零件包围盒重建毛坯</summary>
        public void RebuildStock(double sideOffset = 5.0, double topOffset = 2.0)
        {
            if (_part == null)
                return;

            var bounds = _part.GetBBox();
            if (bounds.IsVoid())
                return;

            Stock = Stock.FromBounds(bounds, sideOffset, topOffset, 0.0);
        }

        /// <summary>更换零件后：按新包围盒重设各工序默认深度并标记待重算。</summary>
        public void RebindAfterPartChanged()
        {
            foreach (var entry in _operations)
            {
                if (entry.Operation is not PathOperation op)
                    continue;

                ApplyDefaultParams(op, entry.Strategy, entry);
                entry.MarkDirty();
            }
        }

        // ==================== 工序 ====================

        public OperationEntry AddOperation(string strategyName)
        {
            var entry = new OperationEntry($"OP{_operations.Count + 1} · {strategyName}", strategyName)
            {
                // 按策略给默认刀；界面上可以再换
                ToolName = DefaultToolName(strategyName),
            };
            var op = CreateOperation(strategyName);
            ApplyDefaultParams(op, strategyName, entry);
            entry.Attach(op);
            _operations.Add(entry);
            return entry;
        }

        /// <summary>策略的默认配刀：球头清面/修槽壁，平底开槽/走外形，钻头打孔</summary>
        public static string DefaultToolName(string strategyName) => strategyName switch
        {
            "2D挖槽" or "轮廓" => "T02 · 平底刀 D10",
            "钻孔" => "T03 · 钻头 φ10",
            _ => "T01 · 球头刀 D8",
        };

        public void RemoveOperation(OperationEntry entry)
        {
            _operations.Remove(entry);
            entry.Dispose();
        }

        /// <summary>零件顶面高度；没有毛坯时按 0</summary>
        public double RecommendedTopDepth()
        {
            if (Stock == null)
                return 0.0;

            var box = Stock.GetBounds();
            return box.IsVoid() ? 0.0 : box.CornerMax().Z();
        }

        /// <summary>
        /// 本工序的加工面描述，给属性面板的「加工面」字段。
        /// 各策略吃进引擎的数据不同，这里把差异说清楚。
        /// </summary>
        public string DescribeGeometry(string strategy) => DescribeGeometry(null, strategy);

        public string DescribeGeometry(OperationEntry? entry, string strategy)
        {
            if (_part == null)
                return "未加载零件";

            return strategy switch
            {
                // 面铣自筛朝上的加工面并判凸台
                "面铣" => _machiningTopSummary,
                "2D挖槽" => DescribePocketBottom(entry),
                // 轮廓沿腔底边界走
                "轮廓" => "槽底轮廓",
                "槽壁精加工" => DescribePocketBottom(entry),
                "钻孔" when entry?.DrillDiameterMm is double d && entry.DrillHoleCount > 0
                    => DescribeDrillGroup(entry, d),
                "钻孔" => "孔位（圆柱面自动识别）",
                _ => "整体",
            };
        }

        private string DescribePocketBottom(OperationEntry? entry)
        {
            if (_part == null)
                return "未加载零件";

            if (entry?.PocketSlotCount > 0
                && entry.PocketLengthMm is double l
                && entry.PocketWidthMm is double w
                && entry.PocketDepthMm is double d)
            {
                var area = entry.PocketAreaMm2 ?? l * w;
                var group = _pocketGroups.FirstOrDefault(g =>
                    Math.Abs(g.DepthMm - d) < 0.6
                    && Math.Abs(g.AreaMm2 - area) < 50);
                if (group != null)
                    return PocketSlotRecognizer.FormatGroupGeometry(group, _pocketBottomSkippedCount);

                var count = entry.PocketSlotCount;
                var multi = count > 1 ? $" · 同规格 {count} 处" : string.Empty;
                return $"L{l:F0} × W{w:F0} × 深{d:F0} mm · 面积 {area:F0} mm²{multi}";
            }

            var faces = GetPocketBottomFaces(_part);
            if (faces.Count == 0)
                return "未识别到槽底（可重新导入或指定顶面）";

            var skip = _pocketBottomSkippedCount > 0
                ? $" · 已跳过背面/底面槽×{_pocketBottomSkippedCount}"
                : string.Empty;
            return $"全部识别槽 ×{faces.Count}（未分组 · 请执行槽分析重建工序）{skip}";
        }

        private string DescribeDrillGroup(OperationEntry entry, double diameterMm)
        {
            var group = _holeGroups.FirstOrDefault(g => Math.Abs(g.DiameterMm - diameterMm) < 0.08);
            var title = group?.DrillTitle ?? $"φ{diameterMm:F0}";
            var kind = group?.KindSummary;
            var skipped = group?.BottomAccessCount ?? 0;
            var skipNote = skipped > 0 ? $" · 已跳过底面孔×{skipped}" : string.Empty;

            if (string.IsNullOrWhiteSpace(kind))
                return $"{title} · {entry.DrillHoleCount} 孔（顶面入刀）{skipNote}";

            return $"{title} · {entry.DrillHoleCount} 孔 · {kind}{skipNote}";
        }

        /// <summary>
        /// 策略名到工序实例的映射，覆盖粗加工、精加工、孔加工三族。
        /// </summary>
        private static PathOperation CreateOperation(string strategyName) => strategyName switch
        {
            "面铣" => new FaceMilling(),
            "2D挖槽" => new Pocket2D(),
            "轮廓" => new ProfileOperation(),
            "槽壁精加工" => new WaterlineOperation(),
            "钻孔" => new DrillingOperation(),
            _ => new FaceMilling(),
        };

        /// <summary>新工序的默认深度与区域参数，让它在还没人改参数时也能出刀路</summary>
        /// <remarks>
        /// 深度基准按策略区分（都相对零件实际几何，而不是一刀切的「毛坯顶-2」）：
        /// 面铣从毛坯顶清到零件顶（去掉余量），挖槽到腔底，轮廓贯通到零件底，
        /// 槽壁精修到槽底，钻孔到孔底。区域清除必须给 <see cref="EnumAreaPocketMode"/>（ZigZag），
        /// 否则区域只走一圈边界、顶面和槽内的料都下不去。
        /// </remarks>
        private void ApplyDefaultParams(PathOperation op, string strategyName, OperationEntry? entry = null)
        {
            var (startDepth, finalDepth) = RecommendDepths(op, strategyName, entry);

            var depths = op.GetDepthParamsRef();
            depths.SetStartDepth(startDepth);
            depths.SetFinalDepth(finalDepth);
            depths.SetSafeHeight(startDepth + 10.0);

            // 间隙高度必须高于安全高度：区域引擎用它当快速定位的抬刀平面
            // （PathParams.Retraction = ClearanceHeight）。留 0 会让第一条 G0 定位
            // 写成「G0 X.. Y.. Z0」，刀尖直接插到零件底面高度再抬起来。
            depths.SetClearanceHeight(startDepth + 15.0);
            depths.SetStepDown(2.0);

            if (op is AreaBasedOperation area)
            {
                area.SetStepover(4.0);
                area.SetStockToLeave(0.0);

                var p = area.GetParamsRef();
                p.SetFill(EnumAreaFill.Empty);
                p.SetCoplanar(EnumAreaCoplanar.Empty);
                p.SetSectionCount(-1);

                // 区域清除的开关只能开给「整片清料」的工序：
                // 面铣清顶面余量、挖槽清腔内残料，不开只会沿边界走一圈。
                // 轮廓/槽壁按几何本来就只该走轮廓环，开了会退化成往复扫面、
                // 把整个区域重新铣一遍（1389 条指令里有 1300 条是白跑的）。
                if (strategyName is "面铣" or "2D挖槽")
                {
                    var pocket = p.GetPocketRef();
                    pocket.SetPocketMode(EnumAreaPocketMode.ZigZag);
                    pocket.SetAngle(0.0);
                }
            }

            if (op is MillingAreaOperation milling)
            {
                milling.SetBoundaryShape(EnumBoundaryShape.BoundBox);
                milling.SetClearEdges(true);
                milling.SetMinTravel(true);
            }

            if (op is DrillingOperation drill)
            {
                drill.SetPeckEnabled(true);
                drill.SetPeckDepth(5.0);
                drill.SetPeckRetract(startDepth + 2.0);
            }
        }

        /// <summary>
        /// 按策略推荐起止深度。返回 (起始深度, 最终深度)。
        /// </summary>
        private (double Start, double Final) RecommendDepths(PathOperation op, string strategyName, OperationEntry? entry = null)
        {
            var stockTop = RecommendedTopDepth();                       // 毛坯顶（零件顶 + 余量）
            var partTop = stockTop;
            var partBottom = 0.0;

            if (_part != null)
            {
                var bounds = _part.GetBBox();
                if (!bounds.IsVoid())
                {
                    partTop = bounds.CornerMax().Z();
                    partBottom = bounds.CornerMin().Z();
                }
            }

            switch (strategyName)
            {
                case "面铣":
                    // 从毛坯顶清到零件顶，正好去掉毛坯余量
                    return (stockTop, partTop);

                case "2D挖槽":
                    if (op is Pocket2D && _part != null)
                    {
                        var floors = entry != null
                            ? GetPocketFacesForEntry(entry)
                            : GetPocketBottomFaces(_part);
                        if (floors.Count > 0)
                        {
                            var floorZ = double.PositiveInfinity;
                            foreach (var bottom in floors)
                            {
                                var bottomBounds = bottom.GetBBox();
                                if (bottomBounds.IsVoid())
                                    continue;
                                floorZ = Math.Min(floorZ, bottomBounds.CornerMax().Z());
                            }

                            if (floorZ < double.PositiveInfinity)
                                return (partTop, floorZ);
                        }
                    }
                    return (partTop, partTop - 2.0);

                case "轮廓":
                    // 贯通到零件底，毛坯侧面的余量才会被带走
                    return (stockTop, partBottom);

                case "槽壁精加工":
                    if (op is WaterlineOperation && _part != null)
                    {
                        var floors = entry != null
                            ? GetPocketFacesForEntry(entry)
                            : GetPocketBottomFaces(_part);
                        if (floors.Count > 0)
                        {
                            var floorZ = double.PositiveInfinity;
                            foreach (var bottom in floors)
                            {
                                var bottomBounds = bottom.GetBBox();
                                if (bottomBounds.IsVoid())
                                    continue;
                                floorZ = Math.Min(floorZ, bottomBounds.CornerMax().Z());
                            }

                            if (floorZ < double.PositiveInfinity)
                                return (partTop, floorZ);
                        }
                    }

                    return (partTop, partTop - TestSlotDepth);

                case "钻孔":
                    // 到最浅的孔底：孔底是零件上面积很小的水平面
                    var holeBottom = FindHoleBottom(partTop, partBottom);
                    return (partTop, holeBottom);

                default:
                    return (partTop, partTop - 2.0);
            }
        }

        /// <summary>
        /// 找孔底高度：孔底是零件上尺寸很小的水平面（远小于顶面/腔底）。
        /// 没找到时退回「零件顶 - 20」（与内置示例零件的孔深一致）。
        /// </summary>
        private double FindHoleBottom(double partTop, double partBottom)
        {
            if (_part == null)
                return partTop - 20.0;

            if (!ReferenceEquals(_part, _geometryCachePart))
            {
                _geometryCachePart = _part;
                _cachedHoleBottomZ = null;
                _cachedPocketBottomFaces = null;
            }

            if (_cachedHoleBottomZ != null)
                return _cachedHoleBottomZ.Value;

            _cachedHoleBottomZ = ComputeHoleBottomZ(_part, partTop);
            return _cachedHoleBottomZ.Value;
        }

        private static double ComputeHoleBottomZ(TopoShape part, double partTop)
        {
            var bounds = part.GetBBox();
            if (bounds.IsVoid())
                return partTop - 20.0;

            var tolerance = Math.Max((bounds.CornerMax().Z() - bounds.CornerMin().Z()) * 0.01, 1e-3);
            var faces = part.GetChildren(EnumTopoShapeType.Topo_FACE);
            if (faces == null)
                return partTop - 20.0;

            double? best = null;
            foreach (var face in faces)
            {
                if (face == null)
                    continue;

                var faceBounds = face.GetBBox();
                if (faceBounds.IsVoid())
                    continue;

                if (faceBounds.CornerMax().Z() - faceBounds.CornerMin().Z() > tolerance)
                    continue;

                var z = faceBounds.CornerMax().Z();
                if (z >= partTop - tolerance)
                    continue;

                var w = faceBounds.CornerMax().X() - faceBounds.CornerMin().X();
                var h = faceBounds.CornerMax().Y() - faceBounds.CornerMin().Y();
                if (Math.Max(w, h) > 20.0)
                    continue;

                best = best == null ? z : Math.Min(best.Value, z);
            }

            return best ?? (partTop - 20.0);
        }

        /// <summary>
        /// 参数改动后重新计算。这是「参数即时生效」的落点：没有 Apply 按钮，改完就调到这。
        /// </summary>
        public bool Compute(OperationEntry entry, out string message)
        {
            message = string.Empty;

            if (_part == null)
            {
                entry.MarkFailed("尚未加载零件");
                message = "尚未加载零件";
                return false;
            }

            if (entry.Operation is not PathOperation op)
            {
                entry.MarkFailed("工序为空");
                message = "工序为空";
                return false;
            }

            if (!TryValidateCompute(entry, out message))
            {
                SessionLog.Warn(message);
                entry.MarkFailed(message);
                return false;
            }

            try
            {
                SessionLog.SetAction($"{entry.Name} · {entry.Strategy} · PrepareOperation");
                PrepareOperation(op, entry);

                // 用本工序配的刀：重算进给并写入工序，各刀的进给互不干扰
                GetTool(entry.ToolName).Controller.ApplyToUpdated(op);

                SessionLog.SetAction($"{entry.Name} · {entry.Strategy} · Execute");
                if (!op.Execute())
                {
                    SessionLog.Warn($"{entry.Name} Execute 返回 false");
                    entry.MarkFailed("刀路计算失败");
                    message = $"{entry.Name}：刀路计算失败";
                    return false;
                }

                var toolpath = op.GetToolpath();
                var hFeed = Math.Max(1.0, op.GetToolHorizFeed());
                var vFeed = Math.Max(1.0, op.GetToolVertFeed());
                var hRapid = Math.Max(1.0, op.GetToolHorizRapid());
                var vRapid = Math.Max(1.0, op.GetToolVertRapid());
                var seconds = toolpath?.GetCycleTime(hFeed, vFeed, hRapid, vRapid) ?? 0.0;

                entry.MarkComputed(toolpath, seconds);
                message = $"{entry.Name}：{entry.CommandCount} 条指令 / {entry.PathLength:F1} mm / {FormatDuration(seconds)}";
                SessionLog.Info(message);
                SessionLog.ClearAction();
                return true;
            }
            catch (Exception ex)
            {
                SessionLog.Error($"{entry.Name} Compute 异常", ex);
                entry.MarkFailed(ex.Message);
                message = $"{entry.Name}：{ex.Message}";
                return false;
            }
        }

        /// <summary>把零件与本工序的刀挂到工序上</summary>
        private void PrepareOperation(PathOperation op, OperationEntry entry)
        {
            var tool = GetTool(entry.ToolName).Tool;

            if (op is AreaBasedOperation area)
            {
                if (_part != null)
                    area.SetBaseGeometryList(CollectBaseShapes(op, _part, entry));

                area.SetToolRadius(tool.GetDiameter() / 2.0);
            }

            if (op is MillingAreaOperation milling && _part != null)
            {
                // 整件传进去，面铣才判得出哪些是凸出加工面的凸台
                milling.SetShape(_part);

                // 面铣默认不排除凸台：不打开时，凸台区域也会被算进去除体，
                // 刀路把凸台一起铣掉（毛坯上的料本来就不该动）。挖槽/轮廓不适用。
                if (op is FaceMilling)
                    milling.SetExcludeRaisedAreas(false);
            }

            if (op is WaterlineOperation waterline && _part != null)
            {
                // 水线工序吃的是 AddSolid 的实体列表，不走 BaseGeometryList。
                // 与 2D 挖槽同一组槽底：按 footprint 拉伸的腔体，Inside 只精修槽壁。
                var partTop = _part.GetBBox().CornerMax().Z();
                var pocketFloors = entry.Strategy == "槽壁精加工"
                    ? GetPocketFacesForEntry(entry)
                    : Array.Empty<TopoShape>();

                var addedPocketSolid = false;
                foreach (var bottom in pocketFloors)
                {
                    if (CreatePocketRegionHighlightBlock(bottom, partTop) is not { } block)
                        continue;

                    waterline.AddSolid(block);
                    addedPocketSolid = true;
                }

                if (!addedPocketSolid)
                {
                    if (_partIsTestPiece)
                    {
                        waterline.AddSolid(BuildSlotSolid());
                        addedPocketSolid = true;
                    }
                    else
                    {
                        var solid = GetWaterlineSolid();
                        if (solid != null)
                            waterline.AddSolid(solid);
                    }
                }

                if (addedPocketSolid || pocketFloors.Count > 0)
                    waterline.SetSide(EnumProfileSide.Inside);

                waterline.SetIgnoreOuterAbove(false);
            }

            if (op is HoleOperation hole && _part != null)
            {
                hole.ClearHoles();

                if (entry.DrillDiameterMm is double targetDiam)
                {
                    var group = _holeGroups.FirstOrDefault(g =>
                        Math.Abs(g.DiameterMm - targetDiam) < 0.08);
                    if (group != null)
                    {
                        foreach (var h in group.Holes)
                        {
                            if (!HoleRecognizer.IsMachinableFromTop(h))
                                continue;
                            hole.AddHole(h.CenterX, h.CenterY, h.DiameterMm);
                        }
                    }
                    else
                        hole.AddShape(_part);
                }
                else
                    hole.AddShape(_part);
            }
        }

        /// <summary>
        /// 取放在 BaseGeometryList 里的加工基准。
        /// <para>
        /// 挖槽只认底面：它的去除体由「底面拉伸到起始深度」生成，传整个实体会被静默跳过，
        /// 结果是刀路为空但 <c>Execute()</c> 依然返回成功，界面显示「已计算 0 条指令」。
        /// </para>
        /// <para>
        /// 面铣喂「全部朝上的水平面」：加工区域由这些面的包围盒决定，只给一个面会使
        /// 往复走刀缩在那一个面的范围里（表现为只清掉槽底、顶面余量没动）。
        /// </para>
        /// </summary>
        private TopoShapeList CollectBaseShapes(PathOperation op, TopoShape part, OperationEntry? entry = null)
        {
            var list = new TopoShapeList();

            if (op is not Pocket2D && op is not FaceMilling)
            {
                list.Add(part);
                return list;
            }

            if (op is Pocket2D)
            {
                var faces = entry != null ? GetPocketFacesForEntry(entry) : GetPocketBottomFaces(part);
                foreach (var face in faces)
                    list.Add(face);
                if (list.Count == 0)
                    list.Add(part);
                return list;
            }

            AddTopMachiningFaces(list, part);
            return list;
        }

        /// <summary>挖槽单面基准（多槽时返回面积最大的一块，供旧路径兼容）</summary>
        private TopoShape PickBaseShape(PathOperation op, TopoShape part)
        {
            if (op is not Pocket2D)
                return part;

            var faces = GetPocketBottomFaces(part);
            return faces.Count > 0 ? faces[0] : part;
        }

        private IReadOnlyList<TopoShape> GetPocketBottomFaces(TopoShape part)
        {
            if (!ReferenceEquals(part, _geometryCachePart))
            {
                _geometryCachePart = part;
                _cachedHoleBottomZ = null;
                _cachedPocketBottomFaces = null;
            }

            if (_cachedPocketBottomFaces != null)
                return _cachedPocketBottomFaces;

            _cachedPocketBottomFaces = ComputePocketBottomFaces(part);
            return _cachedPocketBottomFaces;
        }

        private static List<TopoShape> ComputePocketBottomFaces(TopoShape part) =>
            PocketSlotRecognizer.RecognizeMachinableFromTop(part).Select(s => s.BottomFace).ToList();

        private IReadOnlyList<TopoShape> GetPocketFacesForEntry(OperationEntry entry)
        {
            if (entry.PocketBottomFaces is { Count: > 0 })
                return entry.PocketBottomFaces;

            return _part == null ? Array.Empty<TopoShape>() : GetPocketBottomFaces(_part);
        }

        /// <summary>批量计算待算工序；单条失败不中断，全部算完后汇总失败信息</summary>
        public bool ComputeAll(out string message)
        {
            SessionLog.Info($"ComputeAll 开始 · 工序数 {_operations.Count}");
            var sb = new StringBuilder();
            var ok = true;

            foreach (var entry in _operations)
            {
                if (!Compute(entry, out var msg))
                {
                    // 继续算后面的工序：前面一条失败就停会让后面的刀路完全不出，
                    // 界面上看起来像「只有第一道工序生效」，问题反而更难定位
                    sb.AppendLine(msg);
                    ok = false;
                    continue;
                }

                sb.AppendLine(msg);
            }

            message = sb.ToString().TrimEnd();
            return ok;
        }

        // ==================== 后处理 ====================

        /// <summary>把已计算的工序输出成 NC 文本</summary>
        public string PostProcess(OperationEntry entry)
        {
            if (entry.Operation is not PathOperation op)
                return string.Empty;

            var toolpath = op.GetToolpath();
            if (toolpath == null)
                return string.Empty;

            return Post(toolpath, op, entry.Name);
        }

        /// <summary>
        /// 把全部已计算工序串成一份程序。
        /// <para>
        /// 每道工序都带自己的换刀、转速与安全高度，所以拼接时不能只合并刀路段，
        /// 要让每条工序走一遍完整的后处理再首尾相接：去掉后续程序的 % 与 O 号，
        /// 保留它自己的程序头注释，读起来才是一份能直接上机的单文件程序。
        /// </para>
        /// </summary>
        public string PostProcessAll(out int operationCount)
        {
            operationCount = 0;

            var programs = new List<string>();
            foreach (var entry in _operations)
            {
                if (entry.State != OperationState.Computed)
                    continue;

                var text = PostProcess(entry);
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                programs.Add(programs.Count == 0 ? text : StripProgramFrame(text));
                operationCount++;
            }

            return programs.Count == 0 ? string.Empty : string.Join(Environment.NewLine, programs);
        }

        /// <summary>去掉后续程序的 % 与 O 号，只留工序注释和刀路正文</summary>
        private static string StripProgramFrame(string text)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var kept = new List<string>(lines.Length);

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed == "%")
                    continue;

                // O 号只在文件开头出现一次，后续程序段里再出现就是重复
                if (trimmed.Length > 1 && trimmed[0] == 'O' && trimmed.Skip(1).All(char.IsDigit))
                    continue;

                kept.Add(line);
            }

            return string.Join(Environment.NewLine, kept);
        }

        private string Post(Toolpath toolpath, PathOperation op, string programName)
        {
            var post = PostProcessor.Create(PostName);
            if (post == null)
                return $"未找到后处理器：{PostName}（可用：{string.Join(", ", PostNames)}）";

            var machine = MillingMachineModel.MakeThreeAxis();
            var ctx = ToolController.BuildPostContext(op.GetDepthParams(), machine, programName);
            ctx.Units = PostUnits;
            ctx.Comments.Add($"MCS ZERO (part): X{WorkZero.OriginX:F3} Y{WorkZero.OriginY:F3} Z{WorkZero.OriginZ:F3}");
            var text = post.Process(toolpath, ctx);
            return GcodeWorkZeroTransform.Apply(text, WorkZero);
        }

        // ==================== 视口显示 ====================

        /// <summary>把零件显示到视口</summary>
        public BrepSceneNode? ShowPart(IRenderView view, bool pickable = false)
        {
            if (_part == null)
                return null;

            var node = ShowShapeInScene(view, _part, PartColor);
            if (node != null)
                node.SetPickable(pickable);

            return node;
        }

        /// <summary>
        /// 把毛坯显示到视口。
        /// 毛坯比零件大一圈，不能画成实心：它会连零件和刀路一起包在里面，
        /// 视口里只剩一个灰方块，看起来像刀路没生成——挖槽的刀路本来就整条在毛坯内部。
        /// <para>
        /// 这里画线框：面用全透明材质盖掉，边线保留。
        /// 之所以不靠 <c>SetDisplayFilter(EnumShapeFilter.Edge)</c>：过滤器只影响拾取层级，
        /// 不改变面片本身的可见性，设成 Edge 后读回也是 Edge，但面照样渲染。
        /// </para>
        /// </summary>
        public BrepSceneNode? ShowStock(IRenderView view)
        {
            var shape = Stock?.GetShape();
            if (shape == null)
                return null;

            var node = BrepSceneNode.Create(shape, StockColor, true);
            if (node == null)
                return null;
            node.SetDisplayFilter(EnumShapeFilter.Edge);
            node.SetPickable(false);

            view.Scene.AddNode(node);
            return node;
        }

        /// <summary>把某条工序的刀路显示到视口，返回的分组节点可整体清空</summary>
        public GroupSceneNode ShowToolpath(IRenderView view, OperationEntry entry)
        {
            var group = new GroupSceneNode();

            if (entry.Operation is not PathOperation op)
                return group;

            var toolpath = op.GetToolpath();
            if (toolpath == null)
                return group;

            // 每种策略一种颜色：粗加工暖色、精加工冷色、孔加工紫。
            // 多条刀路同时显示时（树上眼睛各自独立），颜色是分辨工艺族最快的线索。
            var color = GetPathColor(entry.Strategy);

            foreach (var segment in toolpath.GetSegments())
            {
                var node = ShowShape(segment, color);
                if (node != null)
                    group.AddNode(node);
            }

            return group;
        }

        /// <summary>选中工序时，在零件上叠加与策略同色的加工区域高亮。</summary>
        public GroupSceneNode BuildStrategyHighlight(OperationEntry entry)
        {
            var group = new GroupSceneNode();
            if (_part == null)
                return group;

            try
            {
                var color = GetPathColor(entry.Strategy);
                foreach (var shape in EnumerateStrategyHighlightShapes(entry))
                {
                    var node = CreateHighlightNode(shape, color, entry.Strategy);
                    if (node != null)
                        group.AddNode(node);
                }
            }
            catch (Exception)
            {
                // 高亮失败不应拖垮进程（大件二次网格化可能触发 native OOM）
            }

            return group;
        }

        /// <summary>刀路计算前校验，避免导入大件在错误基准上触发 Area 内核崩溃。</summary>
        public bool TryValidateCompute(OperationEntry entry, out string message)
        {
            message = string.Empty;
            if (_part == null)
            {
                message = "尚未加载零件";
                return false;
            }

            if (entry.Strategy is "2D挖槽" or "槽壁精加工")
            {
                var floors = GetPocketFacesForEntry(entry);
                if (floors.Count == 0)
                {
                    message =
                        $"{entry.Name}：未识别到型腔底面，挖槽/槽壁已跳过（强行计算可能导致程序退出）。";
                    return false;
                }

                foreach (var bottom in floors)
                {
                    if (!_partIsTestPiece && !IsPocketSafeForImportedPart(bottom, out var pocketReason))
                    {
                        message = $"{entry.Name}：{pocketReason}";
                        return false;
                    }
                }
            }

            if (entry.Strategy == "钻孔" && entry.DrillDiameterMm is double drillDiam)
            {
                var group = _holeGroups.FirstOrDefault(g => Math.Abs(g.DiameterMm - drillDiam) < 0.08);
                if (group != null && group.MachinableFromTopCount == 0 && group.Holes.Count > 0)
                {
                    message =
                        $"{entry.Name}：该孔径均为底面入刀盲孔（需翻面后另建工序），顶面装夹下已跳过。";
                    return false;
                }
            }

            if (entry.Strategy == "轮廓" && !_partIsTestPiece && IsLargePart())
            {
                message = $"{entry.Name}：导入大件暂不支持轮廓贯通，请跳过以免内核异常。";
                return false;
            }

            return true;
        }

        /// <summary>导入 STEP 时限制型腔尺寸/深度，避免 Area 内核在 makeSection 中崩溃。</summary>
        private bool IsPocketSafeForImportedPart(TopoShape bottom, out string reason)
        {
            reason = string.Empty;
            var faceBounds = bottom.GetBBox();
            if (faceBounds.IsVoid())
            {
                reason = "腔底无效";
                return false;
            }

            if (_part == null)
                return true;

            var partBounds = _part.GetBBox();
            if (partBounds.IsVoid())
                return true;

            var area = HorizontalAreaMm2(faceBounds.CornerMax(), faceBounds.CornerMin());
            var topFootprint = HorizontalAreaMm2(partBounds.CornerMax(), partBounds.CornerMin());
            var topZ = partBounds.CornerMax().Z();
            var floorZ = faceBounds.CornerMax().Z();
            var depth = topZ - floorZ;

            // 明显小于顶面 footprint 的型腔底：允许较大面积（真实开腔）
            if (depth >= 0.5 && depth <= 120 && area < topFootprint * 0.92)
                return true;

            if (area > 80_000)
            {
                reason = $"腔底面积 {area:F0} mm² 接近整件顶面，易触发内核崩溃";
                return false;
            }

            if (depth > 80 && area > topFootprint * 0.75)
            {
                reason = $"腔过深 ({depth:F0} mm) 且接近整件平面，已跳过";
                return false;
            }

            return true;
        }

        private static double HorizontalAreaMm2(GPnt cornerMax, GPnt cornerMin)
        {
            var dx = cornerMax.X() - cornerMin.X();
            var dy = cornerMax.Y() - cornerMin.Y();
            return Math.Max(dx, 0) * Math.Max(dy, 0);
        }

        private bool IsLargePart()
        {
            if (_part == null)
                return false;

            var bounds = _part.GetBBox();
            if (bounds.IsVoid())
                return false;

            var dx = bounds.CornerMax().X() - bounds.CornerMin().X();
            var dy = bounds.CornerMax().Y() - bounds.CornerMin().Y();
            var dz = bounds.CornerMax().Z() - bounds.CornerMin().Z();
            return Math.Max(dx, Math.Max(dy, dz)) > 400.0;
        }

        private IEnumerable<TopoShape> EnumerateStrategyHighlightShapes(OperationEntry entry)
        {
            if (_part == null)
                yield break;

            switch (entry.Strategy)
            {
                case "面铣":
                {
                    var n = 0;
                    foreach (var face in EnumerateTopMachiningFaces(_part))
                    {
                        if (++n > 48)
                            break;

                        if (CreateTopFaceHighlightSlab(face) is { } slab)
                            yield return slab;
                        else
                            yield return face;
                    }

                    yield break;
                }

                case "2D挖槽":
                {
                    var partTop = _part.GetBBox().CornerMax().Z();
                    var n = 0;
                    foreach (var bottom in GetPocketFacesForEntry(entry))
                    {
                        if (++n > 24)
                            break;

                        if (CreatePocketRegionHighlightBlock(bottom, partTop) is { } block)
                            yield return block;
                        else
                            yield return bottom;
                    }

                    if (n == 0 && IsLargePart() && CreatePartBoundsBox(_part) is { } box)
                        yield return box;
                    yield break;
                }

                case "槽壁精加工":
                {
                    var partTop = _part.GetBBox().CornerMax().Z();
                    var n = 0;
                    foreach (var bottom in GetPocketFacesForEntry(entry))
                    {
                        if (++n > 24)
                            break;

                        if (CreatePocketRegionHighlightBlock(bottom, partTop) is { } block)
                            yield return block;
                        else
                            yield return bottom;
                    }

                    if (n == 0 && IsLargePart() && CreatePartBoundsBox(_part) is { } box)
                        yield return box;
                    yield break;
                }

                case "轮廓":
                    if (CreatePartBoundsBox(_part) is { } boundsShape)
                        yield return boundsShape;
                    yield break;

                case "钻孔":
                {
                    var n = 0;
                    foreach (var marker in EnumerateDrillHighlightSolids(entry))
                    {
                        if (++n > 80)
                            break;
                        yield return marker;
                    }

                    yield break;
                }
            }
        }

        private static TopoShape? CreatePartBoundsBox(TopoShape part)
        {
            var bounds = part.GetBBox();
            if (bounds.IsVoid())
                return null;

            var min = bounds.CornerMin();
            var max = bounds.CornerMax();
            var dx = Math.Max(max.X() - min.X(), 1e-3);
            var dy = Math.Max(max.Y() - min.Y(), 1e-3);
            var dz = Math.Max(max.Z() - min.Z(), 1e-3);
            return ShapeBuilder.MakeBox(
                new GAx2(new GPnt(min.X(), min.Y(), min.Z()), new GDir(0, 0, 1)),
                dx, dy, dz);
        }

        /// <summary>挖槽选中高亮：按槽底 footprint 做一根半透明柱体（槽底 → 顶面）。</summary>
        private static TopoShape? CreatePocketRegionHighlightBlock(TopoShape bottom, double partTopZ)
        {
            var bounds = bottom.GetBBox();
            if (bounds.IsVoid())
                return null;

            var min = bounds.CornerMin();
            var max = bounds.CornerMax();
            var floorZ = max.Z();
            var dx = Math.Max(max.X() - min.X(), 0.8);
            var dy = Math.Max(max.Y() - min.Y(), 0.8);
            var height = Math.Max(partTopZ - floorZ, 0.5);
            return ShapeBuilder.MakeBox(
                new GAx2(new GPnt(min.X(), min.Y(), floorZ), new GDir(0, 0, 1)),
                dx,
                dy,
                height);
        }

        /// <summary>面铣选中高亮：顶面上一块薄色板。</summary>
        private static TopoShape? CreateTopFaceHighlightSlab(TopoShape face)
        {
            var bounds = face.GetBBox();
            if (bounds.IsVoid())
                return null;

            var min = bounds.CornerMin();
            var max = bounds.CornerMax();
            var dx = Math.Max(max.X() - min.X(), 0.8);
            var dy = Math.Max(max.Y() - min.Y(), 0.8);
            const double thickness = 1.2;
            var zBase = max.Z() - thickness;
            return ShapeBuilder.MakeBox(
                new GAx2(new GPnt(min.X(), min.Y(), zBase), new GDir(0, 0, 1)),
                dx,
                dy,
                thickness);
        }

        private IEnumerable<TopoShape> EnumerateTopMachiningFaces(TopoShape part)
        {
            var list = new TopoShapeList();
            AddTopMachiningFaces(list, part);
            for (var i = 0; i < list.Count; i++)
                yield return list[i];
        }

        private static void AddTopMachiningFaces(TopoShapeList list, TopoShape part)
        {
            var faces = part.GetChildren(EnumTopoShapeType.Topo_FACE);
            if (faces == null || faces.Count == 0)
            {
                list.Add(part);
                return;
            }

            var bounds = part.GetBBox();
            var tolerance = bounds.IsVoid()
                ? 1e-3
                : Math.Max((bounds.CornerMax().Z() - bounds.CornerMin().Z()) * 0.01, 1e-3);
            var topZ = bounds.IsVoid() ? 0.0 : bounds.CornerMax().Z();

            foreach (var face in faces)
            {
                if (face == null)
                    continue;

                var faceBounds = face.GetBBox();
                if (faceBounds.IsVoid())
                    continue;

                if (faceBounds.CornerMax().Z() - faceBounds.CornerMin().Z() > tolerance)
                    continue;

                if (topZ - faceBounds.CornerMax().Z() > tolerance)
                    continue;

                list.Add(face);
            }

            if (list.Count == 0)
                list.Add(part);
        }

        private IEnumerable<TopoShape> EnumerateDrillHighlightSolids(OperationEntry entry)
        {
            if (_part == null || entry.DrillDiameterMm is not double targetDiam)
                yield break;

            var group = _holeGroups.FirstOrDefault(g => Math.Abs(g.DiameterMm - targetDiam) < 0.08);
            if (group == null || group.Holes.Count == 0)
                yield break;

            var bounds = _part.GetBBox();
            if (bounds.IsVoid())
                yield break;

            foreach (var hole in group.Holes)
            {
                if (!HoleRecognizer.IsMachinableFromTop(hole))
                    continue;

                var radius = hole.DiameterMm * 0.5;
                if (radius <= 0)
                    continue;

                var depth = Math.Max(hole.DepthMm, 1.0);
                var floorZ = hole.FloorZ;
                yield return ShapeBuilder.MakeCylinder(
                    new GAx2(new GPnt(hole.CenterX, hole.CenterY, floorZ), new GDir(0, 0, 1)),
                    radius,
                    depth,
                    0);
            }
        }

        private static BrepSceneNode? CreateHighlightNode(TopoShape shape, Vector3 color, string strategy)
        {
            if (shape == null)
                return null;

            BrepSceneNode? node = null;
            if (strategy is "2D挖槽" or "面铣" or "钻孔" or "槽壁精加工")
                node = CreateFilledHighlightNode(shape, color, strategy);
            else
                node = BrepSceneNode.Create(shape, color, true) ?? ShowShape(shape, color);

            if (node == null)
                return null;

            node.SetPickable(false);
            if (strategy is "轮廓")
                node.SetDisplayFilter(EnumShapeFilter.Edge);

            return node;
        }

        /// <summary>半透明面填充 + 同色描边，选中工序时在零件上叠色块。</summary>
        private static BrepSceneNode? CreateFilledHighlightNode(TopoShape shape, Vector3 color, string strategy)
        {
            var deflection = ComputeMeshDeflection(shape);
            try
            {
                var faceMaterial = MeshStandardMaterial.Create($"anycam.highlight.{strategy}");
                faceMaterial.SetColor(color);
                faceMaterial.SetOpacity(0.42f);
                faceMaterial.SetTransparent(true);
                faceMaterial.SetFaceSide(EnumFaceSide.DoubleSide);

                var edgeMaterial = BasicMaterial.Create($"anycam.highlight.edge.{strategy}");
                edgeMaterial.SetColor(color);
                edgeMaterial.SetLineWidth(2);

                var node = BrepSceneNode.Create(shape, faceMaterial, edgeMaterial, deflection, true);
                if (node != null)
                    return node;
            }
            catch (Exception)
            {
                // 材质不可用时退回纯色面
            }

            return BrepSceneNode.Create(shape, color, true) ?? ShowShape(shape, color);
        }

        /// <summary>
        /// 加工阶段到刀路颜色的映射。
        /// 粗加工（大面积去除）用暖色系提示「材料还在大量去除」，
        /// 精加工（小余量修光）用冷色系，孔加工单独给紫；默认红保底。
        /// </summary>
        public static Vector3 GetPathColor(string strategy) => strategy switch
        {
            "面铣" => ColorTable.Hex(0xE8A13A),       // 粗 · 琥珀
            "2D挖槽" => ColorTable.Hex(0xE0563A),     // 粗 · 橘红
            "轮廓" => ColorTable.Hex(0x4A9DE8),       // 精 · 蓝
            "槽壁精加工" => ColorTable.Hex(0x3AC8A8), // 精 · 青
            "钻孔" => ColorTable.Hex(0xC77ADF),       // 孔 · 紫
            _ => ColorTable.Hex(0xEF5B5B),
        };

        /// <summary>取颜色的 #RRGGBB 字符串，给树上/面板做同色标记用（与 GetPathColor 保持同一映射）</summary>
        public static string GetPathColorHex(string strategy) => strategy switch
        {
            "面铣" => "#E8A13A",
            "2D挖槽" => "#E0563A",
            "轮廓" => "#4A9DE8",
            "槽壁精加工" => "#3AC8A8",
            "钻孔" => "#C77ADF",
            _ => "#EF5B5B",
        };

        /// <summary>
        /// 造一个纯色显示节点，<b>不</b>入场景。
        /// 这里不用编辑器材质：应用未初始化灯光体系，受光材质会渲染成一片黑，
        /// 只会削弱顶点色，看起来像零件没上屏。
        /// <para>
        /// 不要在这里 AddNode 到场景：刀路需要挂进一个 GroupSceneNode 才能整体清掉。
        /// 先入场景再挂组会被 <c>GroupSceneNode::AddNode</c> 以「已有父节点」静默拒绝，
        /// 节点仍留在场景根上，<c>Clear()</c> 清不掉，换工序后旧刀路一直堆着。
        /// </para>
        /// </summary>
        private static BrepSceneNode? ShowShape(TopoShape shape, Vector3 color)
        {
            if (shape == null)
                return null;

            var deflection = ComputeMeshDeflection(shape);

            // 与 AnyCAD.WPF.App MainViewModel.OnOpenModel 相同优先路径
            var node = BrepSceneNode.Create(shape, null, null, deflection, false);
            if (node != null)
                return node;

            node = BrepSceneNode.Create(shape, color, true);
            if (node != null)
                return node;

            return BrepSceneNode.Create(shape, deflection, false)
                   ?? BrepSceneNode.Create(shape);
        }

        private static double ComputeMeshDeflection(TopoShape shape)
        {
            var bounds = shape.GetBBox();
            if (bounds.IsVoid())
                return 0.5;

            var dx = bounds.CornerMax().X() - bounds.CornerMin().X();
            var dy = bounds.CornerMax().Y() - bounds.CornerMin().Y();
            var dz = bounds.CornerMax().Z() - bounds.CornerMin().Z();
            var max = Math.Max(dx, Math.Max(dy, dz));
            // 超大件用更粗网格，避免三角化占满内存/GPU
            if (max > 800.0)
                return Math.Clamp(max / 45.0, 2.0, 200.0);

            return Math.Clamp(max / 120.0, 0.05, 80.0);
        }

        /// <summary>造显示节点并入场景，用于零件/毛坯这类不会被整体清掉的顶层对象</summary>
        private static BrepSceneNode? ShowShapeInScene(IRenderView view, TopoShape shape, Vector3 color)
        {
            var node = ShowShape(shape, color);
            if (node == null)
                return null;

            if (view is AnyCAD.WPF.RenderControl wpfRender)
                wpfRender.ShowSceneNode(node);
            else
                view.Scene.AddNode(node);

            return node;
        }

        private static readonly Vector3 PartColor = ColorTable.Hex(0x7A93B8);
        private static readonly Vector3 PathColor = ColorTable.Hex(0xEF5B5B);
        private static readonly Vector3 StockColor = ColorTable.Hex(0x9FB0C6);

        public static string FormatDuration(double seconds)
        {
            if (seconds < 60)
                return $"{seconds:F0}s";

            var span = TimeSpan.FromSeconds(seconds);
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours}h{span.Minutes:D2}m"
                : $"{span.Minutes}m{span.Seconds:D2}s";
        }

        public void Dispose()
        {
            foreach (var entry in _operations)
                entry.Dispose();
            _operations.Clear();
            ToolController.Dispose();
        }
    }
}
