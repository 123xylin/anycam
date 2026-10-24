using AnyCAD.Foundation;
using AnyCAD.Manufacturing;
using AnyCAM.Milling.Engine;
using System.Reflection;

// 引擎侧探针：不走界面，直接建会话、算刀路、打印中间状态。
// 界面只能看到「几条指令」，看不出是哪一环断了，所以这类排查用它。
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--scene")
            return ProbeScene();

        using var session = new CamSession();
        session.LoadPart();
        session.RebuildStock();

        Console.WriteLine($"零件包围盒 {session.Part?.GetBBox()}");
        Console.WriteLine($"毛坯 {session.Stock?.GetStockType()} bbox={session.Stock?.GetBounds()}");

        var faceMilling = session.AddOperation("面铣");
        var pocket = session.AddOperation("2D挖槽");

        Report(session, faceMilling);
        Report(session, pocket);

        return 0;
    }

    /// <summary>
    /// 探毛坯线框的渲染链路：过滤器取什么、节点里到底有哪些图元。
    /// 界面只能看到一个灰方块，分不清是「面没过滤掉」还是「过滤器设了但没生效」。
    /// </summary>
    private static int ProbeScene()
    {
        Console.WriteLine("EnumShapeFilter 取值：");
        foreach (var name in new[] { "Zero", "Edge", "Face", "EdgeFace", "VertexEdgeFace" })
        {
            var field = typeof(EnumShapeFilter).GetField(name);
            Console.WriteLine($"  {name} = {(field == null ? "<缺失>" : field.GetValue(null))}");
        }

        DumpMembers(typeof(BrepSceneNode), "透明度/材质/显示");

        using var session = new CamSession();
        session.LoadPart();
        session.RebuildStock();

        var shape = session.Stock?.GetShape();
        Console.WriteLine($"\n毛坯面数 = {shape?.GetChildren(EnumTopoShapeType.Topo_FACE)?.Count}");

        var node = BrepSceneNode.Create(shape, ColorTable.Hex(0xFFFFFF), true);
        if (node == null)
        {
            Console.WriteLine("节点创建失败");
            return 1;
        }

        Console.WriteLine($"默认过滤器 = {node.GetDisplayFilter()}");
        node.SetDisplayFilter(EnumShapeFilter.Edge);
        Console.WriteLine($"设置后过滤器 = {node.GetDisplayFilter()}");
        return 0;
    }

    private static void DumpMembers(Type type, string keyword)
    {
        Console.WriteLine($"\n{type.Name} 里含「{keyword}」的公开成员：");
        foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance))
        {
            if (member.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || member.Name.Contains("Opacity", StringComparison.OrdinalIgnoreCase)
                || member.Name.Contains("Material", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  {member.MemberType} {member.Name}");
            }
        }
    }

    private static void Report(CamSession session, OperationEntry entry)
    {
        Console.WriteLine();
        Console.WriteLine($"===== {entry.Name} =====");

        var ok = session.Compute(entry, out var message);
        Console.WriteLine($"Compute -> {ok} : {message}");

        if (entry.Operation is not PathOperation op)
        {
            Console.WriteLine("工序类型不是 PathOperation");
            return;
        }

        var depth = op.GetDepthParams();
        Console.WriteLine($"深度 Start={depth.GetStartDepth():F3} Final={depth.GetFinalDepth():F3} " +
                          $"Safe={depth.GetSafeHeight():F3} StepDown={depth.GetStepDown():F3}");

        if (op is AreaBasedOperation area)
        {
            Console.WriteLine($"刀具半径={area.GetToolRadius():F3} 步距={area.GetStepover():F3}");

            var bases = area.GetBaseGeometryList();
            Console.WriteLine($"BaseGeometryList 数量={Count(bases)}");
            if (bases != null)
            {
                var index = 0;
                foreach (var shape in bases)
                {
                    Console.WriteLine($"  [{index++}] {shape?.GetShapeType()} bbox={shape?.GetBBox()}");
                }
            }
        }

        if (op is MillingAreaOperation milling)
            Console.WriteLine($"边界={milling.GetBoundaryShape()} 清边={milling.GetClearEdges()}");

        var depths = op.GetDepthParams().GetDepths();
        var levels = string.Join(", ", depths.Select(d => d.ToString("F3")));
        Console.WriteLine($"切层={depths.Count} [{levels}]");

        var toolpath = op.GetToolpath();
        Console.WriteLine($"Toolpath {(toolpath == null ? "为 null" : $"指令数={toolpath.GetSize()} 长度={toolpath.GetLength():F3}")}");
    }

    private static int Count(object? list)
    {
        if (list is System.Collections.ICollection collection)
            return collection.Count;

        try
        {
            var count = list?.GetType().GetMethod("GetSize")?.Invoke(list, null);
            return count is int value ? value : -1;
        }
        catch
        {
            return -1;
        }
    }
}
