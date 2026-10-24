using AnyCAD.Foundation;
using AnyCAD.Manufacturing;

using System;
using System.Text;

namespace AnyCAM.Milling.Engine
{
    /// <summary>
    /// 铣削加工仿真：把全部已计算工序串成一份程序，按刀位驱动刀具切削毛坯并播放动画。
    /// <para>
    /// 刀路直连内核：每道工序一个 <see cref="MillToolpathSection"/>（工序 <see cref="Toolpath"/>
    /// + 真实刀型 + 切削痕颜色 + 快移轴速），由会话内部转成运动序列——G0/G1/圆弧解析与
    /// 钻孔固定循环（G81/82/73/83）展开都在内核完成，前端不再自写解析器。
    /// 刀具显示节点同样由内核按刀型现造，调用方无须准备任何场景节点。
    /// </para>
    /// </summary>
    public sealed class MillSimulator : IDisposable
    {
        private readonly CamSession _session;
        private MillSimulationSession? _simulation;

        public MillSimulator(CamSession session)
        {
            _session = session;
        }

        public bool IsActive => _simulation != null;

        // ==================== 启停 ====================

        /// <summary>
        /// 开始仿真：所有已计算工序按树上顺序串成一份程序。
        /// 没有已计算的工序、或毛坯缺失时返回 false 并给出原因。
        /// </summary>
        public bool Start(IRenderView view, out string message)
        {
            message = string.Empty;

            if (_simulation != null)
                return true;    // 已在播放，重复点开始无副作用

            var stockShape = _session.Stock?.GetShape();
            if (stockShape == null)
            {
                message = "尚未生成毛坯，无法仿真";
                return false;
            }

            // 1) 每道已计算工序一个工序段：切削痕按工序配色（颜色挂在段上，
            //    同一把刀在不同工序里用不同颜色），真实刀型决定切削
            var sections = new MillToolpathSectionList();
            var gcodeBuilder = new StringBuilder();
            var appended = 0;
            foreach (var entry in _session.Operations)
            {
                if (entry.State != OperationState.Computed)
                    continue;

                var toolpath = entry.Operation?.GetToolpath();
                if (toolpath == null)
                    continue;

                var toolEntry = _session.GetTool(entry.ToolName) ?? _session.Tools[0];
                var op = entry.Operation!;

                sections.Add(new MillToolpathSection
                {
                    ToolId = sections.Count + 1,
                    ToolpathSource = toolpath,
                    // 真实刀型交给切割管线：没有它只有切削痕、毛坯不削减
                    ToolParams = toolEntry.Tool,
                    CutColor = ColorForOperation(entry.Name),
                    // GetToolHorizRapid 是 mm/min；会话按轴向速度 mm/s 折算快移合成速度
                    RapidAxisSpeed = Math.Max(op.GetToolHorizRapid(), 1.0) / 60.0,
                });

                // G 代码落盘仍保留：C++ 端单测用 GCodeReader 读同一份数据对拍。
                // 注释只保留 ASCII：gpr 解析器对 >127 的字节调 isspace 会触发 Debug CRT 断言
                var asciiName = System.Text.RegularExpressions.Regex.Replace(entry.Name ?? "op", @"[^\x20-\x7E]", "");
                gcodeBuilder.AppendLine($"(op: {asciiName})");
                gcodeBuilder.AppendLine(toolpath.ToGCode());
                appended++;
            }

            // 保存成文件：C++ 端单测用 GCodeReader::ReadFile 读同一份数据对拍
            try
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "mill-sim.nc"),
                    gcodeBuilder.ToString());
            }
            catch
            {
                // 落盘失败不影响仿真
            }

            if (appended == 0)
            {
                message = "先计算刀路再仿真";
                return false;
            }

            // 2) 毛坯节点：仿真会在这个体上做切削
            var stockMaterial = MeshPhongMaterial.Create("mill.sim.stock");
            stockMaterial.SetColor(new Vector3(0.85f));
            var stock = BrepSceneNode.Create(stockShape, stockMaterial, null);
            stock?.SetChildrenPickable(false);

            // 3) 起播：刀具显示节点由内核按 ToolParams 现造，无须准备
            var simulation = new MillSimulationSession();
            simulation.SetViewer(view.Viewer);
            var started = simulation.Start(stock, sections);
            SimLog($"Start={started} err={simulation.GetLastError()} " +
                   $"{simulation.GetSnapshot().DurationMilliseconds:F0}ms sections={appended}");
            view.EnableAnimation(true);

            _simulation = simulation;
            return true;
        }

        public void Stop(IRenderView view)
        {
            if (_simulation == null)
                return;

            view.EnableAnimation(false);
            // 内核 Stop/Close 已幂等：无活动会话时直接成功，无须 try/catch 兜底
            _simulation.Stop();
            _simulation.Close();
            _simulation.SetViewer(null);
            _simulation.Dispose();
            _simulation = null;
        }

        /// <summary>暂停 / 继续播放；状态以会话查询为准，调用方不再记账</summary>
        public void TogglePause()
        {
            if (_simulation == null)
                return;

            if (_simulation.IsPaused())
                _simulation.Resume();
            else
                _simulation.Pause();
        }

        /// <summary>当前是否处于暂停态；没有会话时为 false</summary>
        public bool IsPaused => _simulation != null && _simulation.IsPaused();

        /// <summary>仿真是否已自然播完（Finished）；界面据此自动退出仿真态</summary>
        public bool IsFinished => _simulation != null && _simulation.IsFinished();

        /// <summary>播放倍率：1 = 实时</summary>
        public void SetPlaybackRate(double rate)
        {
            _simulation?.SetPlaybackRate(rate);
        }

        // ==================== 配色 ====================

        /// <summary>
        /// 工序的切削痕颜色：与工序树上的节点色一致，仿真里哪道工序切了哪里一眼可辨。
        /// </summary>
        private static Vector3 ColorForOperation(string? name)
        {
            if (string.IsNullOrEmpty(name))
                return new Vector3(1.0f, 0.3f, 0.25f);

            if (name.Contains("面铣"))
                return new Vector3(1.00f, 0.30f, 0.25f);   // 红
            if (name.Contains("挖槽"))
                return new Vector3(1.00f, 0.62f, 0.10f);   // 橙
            if (name.Contains("轮廓"))
                return new Vector3(0.29f, 0.62f, 0.91f);   // 蓝
            if (name.Contains("槽壁"))
                return new Vector3(0.20f, 0.80f, 0.80f);   // 青
            if (name.Contains("钻"))
                return new Vector3(0.68f, 0.40f, 0.95f);   // 紫

            return new Vector3(1.0f, 0.3f, 0.25f);
        }

        private static void SimLog(string text)
        {
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "sim.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} {text}\n");
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            if (_simulation == null)
                return;

            _simulation.Dispose();
            _simulation = null;
        }
    }
}
