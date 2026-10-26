using System;
using System.IO;
using AnyCAD.Foundation;

namespace AnyCAM.Milling.Engine
{
    /// <summary>
    /// 命令行探针：不建界面，只跑引擎链路，用于排查工序失败这类没有 UI 反馈的问题。
    /// 用法：AnyCAM.Milling.exe --probe
    /// </summary>
    internal static class EngineProbe
    {
        public static bool WantsProbe(string[] args) => TryRunCli(args, out _);

        /// <summary>CLI 自检：--probe 或 --open-step &lt;file&gt;</summary>
        public static bool TryRunCli(string[] args, out int exitCode)
        {
            exitCode = 0;
            for (var i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--probe", StringComparison.OrdinalIgnoreCase))
                {
                    Run();
                    return true;
                }

                if (string.Equals(args[i], "--open-step", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    exitCode = RunOpenStep(args[i + 1]) ? 0 : 1;
                    return true;
                }

                if (string.Equals(args[i], "--analyze-holes", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    exitCode = RunAnalyzeHoles(args[i + 1]) ? 0 : 1;
                    return true;
                }
            }

            return false;
        }

        static bool RunOpenStep(string path)
        {
            GlobalInstance.EnableD3DWPF = false;
            GlobalInstance.Initialize();
            ExchangeInstance.Initialize();
            var shape = PartIO.Open(path);
            if (shape == null)
            {
                Console.WriteLine("open failed");
                return false;
            }

            var box = shape.GetBBox();
            Console.WriteLine($"ok bbox {box.CornerMin().X():F1},{box.CornerMin().Y():F1},{box.CornerMin().Z():F1} -> " +
                              $"{box.CornerMax().X():F1},{box.CornerMax().Y():F1},{box.CornerMax().Z():F1}");
            return !box.IsVoid();
        }

        static bool RunAnalyzeHoles(string path)
        {
            GlobalInstance.EnableD3DWPF = false;
            GlobalInstance.Initialize();
            ExchangeInstance.Initialize();
            var shape = PartIO.Open(path);
            if (shape == null)
            {
                Console.WriteLine("open failed");
                return false;
            }

            var report = HoleRecognizer.Analyze(shape);
            Console.WriteLine(report.ToSummaryText());
            return report.Groups.Count > 0;
        }

        public static void Run()
        {
            var log = new StreamWriter("probe.log", false) { AutoFlush = true };
            void Say(string text)
            {
                log.WriteLine(text);
                Console.WriteLine(text);
            }

            try
            {
                GlobalInstance.EnableD3DWPF = false;
                GlobalInstance.Initialize();
                ExchangeInstance.Initialize();

                var session = new CamSession();
                session.LoadPart();
                Say($"part ok: {session.Part != null}");
                if (session.Part != null)
                {
                    var box = session.Part.GetBBox();
                    Say($"bbox: {box.CornerMin().X():F1},{box.CornerMin().Y():F1},{box.CornerMin().Z():F1} -> " +
                        $"{box.CornerMax().X():F1},{box.CornerMax().Y():F1},{box.CornerMax().Z():F1}");
                }

                foreach (var name in new[] { "面铣", "2D挖槽", "钻孔", "轮廓", "槽壁精加工" })
                {
                    var entry = session.AddOperation(name);
                    var ok = session.Compute(entry, out var message);

                    if (entry.Operation is AreaBasedOperation area)
                    {
                        var baseList = area.GetBaseGeometryList();
                        Say($"[{name}] baseGeom={baseList?.Count ?? -1}");
                        if (baseList != null)
                        {
                            for (var i = 0; i < baseList.Count && i < 8; i++)
                            {
                                var b = baseList[i].GetBBox();
                                Say($"    #{i} {b.CornerMin().X():F1},{b.CornerMin().Y():F1},{b.CornerMin().Z():F1}" +
                                    $" -> {b.CornerMax().X():F1},{b.CornerMax().Y():F1},{b.CornerMax().Z():F1}");
                            }
                        }
                    }

                    if (entry.Operation is HoleOperation hole)
                    {
                        Say($"[钻孔] holes={hole.GetHoles()?.Count ?? -1}");
                    }

                    var path = entry.Operation?.GetToolpath();
                    if (path != null)
                    {
                        var zMin = double.MaxValue; var zMax = double.MinValue;
                        var xMin = double.MaxValue; var yMin = double.MaxValue;
                        var xMax = double.MinValue; var yMax = double.MinValue;
                        foreach (var seg in path.GetSegments())
                        {
                            var sb2 = seg.GetBBox();
                            if (sb2.IsVoid())
                                continue;
                            zMin = Math.Min(zMin, sb2.CornerMin().Z()); zMax = Math.Max(zMax, sb2.CornerMax().Z());
                            xMin = Math.Min(xMin, sb2.CornerMin().X()); xMax = Math.Max(xMax, sb2.CornerMax().X());
                            yMin = Math.Min(yMin, sb2.CornerMin().Y()); yMax = Math.Max(yMax, sb2.CornerMax().Y());
                        }
                        Say($"    toolpath bbox Z {zMin:F2} .. {zMax:F2}, XY {xMin:F1},{yMin:F1} -> {xMax:F1},{yMax:F1}");

                        // 逐层深度分布：从 G 代码文本统计 G1 行里出现过的 Z，确认每层都落到预期深度
                        var zHistogram = new SortedDictionary<double, int>();
                        foreach (var line in path.ToGCode().Replace("\r\n", "\n").Split('\n'))
                        {
                            if (!line.StartsWith("G1 ", StringComparison.Ordinal))
                                continue;
                            var zi = line.IndexOf(" Z", StringComparison.Ordinal);
                            if (zi < 0)
                                continue;
                            var token = line.Substring(zi + 2).Split(' ')[0];
                            if (!double.TryParse(token, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var z))
                                continue;
                            var key = Math.Round(z, 2);
                            zHistogram[key] = zHistogram.TryGetValue(key, out var n) ? n + 1 : 1;
                        }
                        Say("    cut Z levels: " + string.Join(", ",
                            zHistogram.Select(kv => $"{kv.Key:F2}x{kv.Value}")));
                    }

                    var depths = entry.Operation?.GetDepthParamsRef();
                    if (depths != null)
                        Say($"    depths start={depths.GetStartDepth():F2} final={depths.GetFinalDepth():F2}");

                    Say($"[{name}] ok={ok} {message}");
                }
            }
            catch (Exception ex)
            {
                Say($"EXCEPTION: {ex}");
            }
            finally
            {
                log.Flush();
                log.Dispose();
            }
        }
    }
}
