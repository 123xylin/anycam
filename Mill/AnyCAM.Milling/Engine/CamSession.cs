using AnyCAD.Foundation;

using System;
using System.Collections.Generic;
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

        public string Name { get; }
        public string Strategy { get; set; }

        /// <summary>本工序用的刀；名字对应 <see cref="ToolEntry.Name"/>，可在界面上换</summary>
        public string ToolName { get; set; } = string.Empty;

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
        private TopoShape? _part;
        /// <summary>当前零件是否为内置示例零件（只有它知道孔位，才能给等高填孔）</summary>
        private bool _partIsTestPiece;
        /// <summary>填实钻孔后的等高实体，按零件缓存</summary>
        private TopoShape? _waterlineSolid;

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
            _part = shape ?? BuildTestPart();
            _partIsTestPiece = shape == null;
            // 零件换了，填孔实体必须跟着重算
            _waterlineSolid = null;
            RebuildStock();
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

        // ==================== 工序 ====================

        public OperationEntry AddOperation(string strategyName)
        {
            var entry = new OperationEntry($"OP{_operations.Count + 1} · {strategyName}", strategyName)
            {
                // 按策略给默认刀；界面上可以再换
                ToolName = DefaultToolName(strategyName),
            };
            var op = CreateOperation(strategyName);
            ApplyDefaultParams(op, strategyName);
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
        public string DescribeGeometry(string strategy)
        {
            if (_part == null)
                return "未加载零件";

            return strategy switch
            {
                // 面铣自筛朝上的加工面并判凸台
                "面铣" => "顶面（自动识别）",
                // 挖槽取型腔底面：低于顶面的最高水平面
                "2D挖槽" => DescribePocketBottom(),
                // 轮廓沿腔底边界走
                "轮廓" => "槽底轮廓",
                // 槽壁精加工沿腔壁分层
                "槽壁精加工" => "槽壁",
                // 钻孔由圆柱面/圆边识别孔位
                "钻孔" => "孔位（圆柱面自动识别）",
                _ => "整体",
            };
        }

        private string DescribePocketBottom()
        {
            var bounds = _part!.GetBBox();
            if (bounds.IsVoid())
                return "整体";

            var face = PickBaseShape(new Pocket2D(), _part);
            if (ReferenceEquals(face, _part))
                return "整体";

            var faceBounds = face.GetBBox();
            var w = faceBounds.CornerMax().X() - faceBounds.CornerMin().X();
            var h = faceBounds.CornerMax().Y() - faceBounds.CornerMin().Y();
            return $"槽底面 ×1（{w:F0} × {h:F0} @ Z{faceBounds.CornerMax().Z():F0}）";
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
        private void ApplyDefaultParams(PathOperation op, string strategyName)
        {
            var (startDepth, finalDepth) = RecommendDepths(op, strategyName);

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
        private (double Start, double Final) RecommendDepths(PathOperation op, string strategyName)
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
                    // 到型腔底：挖槽的基准面就是 PickBaseShape 选出的腔底
                    if (op is Pocket2D && _part != null)
                    {
                        var bottom = PickBaseShape(op, _part);
                        if (!ReferenceEquals(bottom, _part))
                        {
                            var bottomBounds = bottom.GetBBox();
                            if (!bottomBounds.IsVoid())
                                return (partTop, bottomBounds.CornerMax().Z());
                        }
                    }
                    return (partTop, partTop - 2.0);

                case "轮廓":
                    // 贯通到零件底，毛坯侧面的余量才会被带走
                    return (stockTop, partBottom);

                case "槽壁精加工":
                {
                    // 只修槽壁：顶从零件顶起，底到槽底（与挖槽同一块基准面）
                    if (_part != null)
                    {
                        var bottom = PickBaseShape(new Pocket2D(), _part);
                        if (!ReferenceEquals(bottom, _part))
                        {
                            var bottomBounds = bottom.GetBBox();
                            if (!bottomBounds.IsVoid())
                                return (partTop, bottomBounds.CornerMax().Z());
                        }
                    }
                    return (partTop, partTop - TestSlotDepth);
                }

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

            var bounds = _part.GetBBox();
            if (bounds.IsVoid())
                return partTop - 20.0;

            var tolerance = Math.Max((bounds.CornerMax().Z() - bounds.CornerMin().Z()) * 0.01, 1e-3);
            var faces = _part.GetChildren(EnumTopoShapeType.Topo_FACE);
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

                // 水平面、低于零件顶
                if (faceBounds.CornerMax().Z() - faceBounds.CornerMin().Z() > tolerance)
                    continue;

                var z = faceBounds.CornerMax().Z();
                if (z >= partTop - tolerance)
                    continue;

                // 孔底尺寸小：XY 跨度远小于腔底
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

            try
            {
                PrepareOperation(op, entry);

                // 用本工序配的刀：重算进给并写入工序，各刀的进给互不干扰
                GetTool(entry.ToolName).Controller.ApplyToUpdated(op);

                if (!op.Execute())
                {
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
                return true;
            }
            catch (Exception ex)
            {
                // 引擎抛异常不能让界面崩，转成徽标上的失败态
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
                    area.SetBaseGeometryList(CollectBaseShapes(op, _part));

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
                // 槽壁精修只修槽壁：喂「腔体体积」这块独立实体，截面就只剩槽环，
                // Side=Inside 让刀心收进腔内贴壁（外形侧壁归「轮廓」管，不再重复）。
                if (entry.Strategy == "槽壁精加工" && _partIsTestPiece)
                {
                    waterline.AddSolid(BuildSlotSolid());
                    waterline.SetSide(EnumProfileSide.Inside);
                }
                else
                {
                    // 外部模型没有腔体信息：退回整件（孔已填实），外形+腔壁一起修
                    var solid = GetWaterlineSolid();
                    if (solid != null)
                        waterline.AddSolid(solid);
                }
                waterline.SetIgnoreOuterAbove(false);
            }

            if (op is HoleOperation hole && _part != null)
            {
                // 整件交给孔位识别：圆柱面/圆边都会被收成孔位（含同位去重）
                hole.ClearHoles();
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
        private static TopoShapeList CollectBaseShapes(PathOperation op, TopoShape part)
        {
            var list = new TopoShapeList();

            if (op is not Pocket2D && op is not FaceMilling)
            {
                list.Add(part);
                return list;
            }

            if (op is Pocket2D)
            {
                list.Add(PickBaseShape(op, part));
                return list;
            }

            // 面铣：只取「最高的一层水平面」作为加工面。
            // 不能把全部水平面都喂进去：面铣用 MakeCompound(面表) 的包围盒当加工区域，
            // 同时用最低面的 Z 当「加工面高度」判凸台；混入底面（Z0）和孔底（Z20）后，
            // 加工面基准被拉到最低处，区域还会被这些面上的孔洞切成槽底环，
            // 结果是往复盘只铺在槽内、顶面余量没动。
            var faces = part.GetChildren(EnumTopoShapeType.Topo_FACE);
            if (faces == null || faces.Count == 0)
            {
                list.Add(part);
                return list;
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

                // 水平面判定：Z 跨度近似 0
                if (faceBounds.CornerMax().Z() - faceBounds.CornerMin().Z() > tolerance)
                    continue;

                // 只认最高一层（零件顶面）；槽底、底面、孔底都不算
                if (topZ - faceBounds.CornerMax().Z() > tolerance)
                    continue;

                list.Add(face);
            }

            if (list.Count == 0)
                list.Add(part);

            return list;
        }

        /// <summary>挖槽的加工基准：型腔底面（见方法内注释）</summary>
        private static TopoShape PickBaseShape(PathOperation op, TopoShape part)
        {
            if (op is not Pocket2D)
                return part;

            var bounds = part.GetBBox();
            if (bounds.IsVoid())
                return part;

            var faces = part.GetChildren(EnumTopoShapeType.Topo_FACE);
            if (faces == null || faces.Count == 0)
                return part;

            // 挖槽要型腔底面：取「低于零件顶面的最高水平面」，
            // 顶面自身不是腔底（示例零件顶面是开口的方槽上沿）。
            // 水平面用 Z 跨度判定，平面足够时等于 0，网格面留 1% 余量。
            //
            // 同一高度上可能有多个候选（示例零件的四个盲孔孔底与槽底同高），
            // 只比高度先到先得会选到 φ10 的孔底，刀路因此缩成一个小圈。
            // 型腔底一定是同高度里面积最大的那块，所以同高度时改比包围盒面积。
            var tolerance = Math.Max((bounds.CornerMax().Z() - bounds.CornerMin().Z()) * 0.01, 1e-3);
            var topZ = bounds.CornerMax().Z();
            TopoShape? best = null;
            var bestZ = double.NegativeInfinity;
            var bestArea = 0.0;

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
                if (z >= topZ - tolerance)
                    continue;   // 顶面跳过

                // 高于当前最优就换，同高时只认面积更大的
                if (z < bestZ - tolerance)
                    continue;

                var area = (faceBounds.CornerMax().X() - faceBounds.CornerMin().X())
                         * (faceBounds.CornerMax().Y() - faceBounds.CornerMin().Y());

                if (z > bestZ + tolerance || area > bestArea)
                {
                    best = face;
                    bestZ = z;
                    bestArea = area;
                }
            }

            return best ?? part;
        }

        /// <summary>批量计算待算工序；单条失败不中断，全部算完后汇总失败信息</summary>
        public bool ComputeAll(out string message)
        {
            var sb = new StringBuilder();
            var ok = true;

            foreach (var entry in _operations)
            {
                if (entry.State == OperationState.Computed)
                    continue;

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
            return post.Process(toolpath, ctx);
        }

        // ==================== 视口显示 ====================

        /// <summary>把零件显示到视口</summary>
        public BrepSceneNode? ShowPart(IRenderView view)
        {
            if (_part == null)
                return null;

            return ShowShapeInScene(view, _part, PartColor);
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

            return BrepSceneNode.Create(shape, color, true);
        }

        /// <summary>造显示节点并入场景，用于零件/毛坯这类不会被整体清掉的顶层对象</summary>
        private static BrepSceneNode? ShowShapeInScene(IRenderView view, TopoShape shape, Vector3 color)
        {
            var node = ShowShape(shape, color);
            if (node != null)
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
