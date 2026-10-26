using AnyCAD.Foundation;

namespace AnyCAM.Milling.Engine
{
    public enum WorkZeroPreset
    {
        BboxMinCorner,
        TopCenter,
        BottomCenter,
        BboxCenter,
        Custom,
    }

    /// <summary>工件坐标系（MCS）零点：相对「对齐后零件」坐标，G 代码输出会减去该原点。</summary>
    public sealed class WorkCoordinateSetup
    {
        public double OriginX { get; private set; }
        public double OriginY { get; private set; }
        public double OriginZ { get; private set; }
        public WorkZeroPreset Preset { get; private set; } = WorkZeroPreset.BboxMinCorner;

        public void SetOrigin(double x, double y, double z)
        {
            OriginX = x;
            OriginY = y;
            OriginZ = z;
            Preset = WorkZeroPreset.Custom;
        }

        public void ApplyPreset(WorkZeroPreset preset, TopoShape? part)
        {
            Preset = preset;
            if (part == null || preset == WorkZeroPreset.Custom)
                return;

            var bounds = part.GetBBox();
            if (bounds.IsVoid())
                return;

            var min = bounds.CornerMin();
            var max = bounds.CornerMax();
            var cx = (min.X() + max.X()) * 0.5;
            var cy = (min.Y() + max.Y()) * 0.5;
            var cz = (min.Z() + max.Z()) * 0.5;

            switch (preset)
            {
                case WorkZeroPreset.BboxMinCorner:
                    OriginX = min.X();
                    OriginY = min.Y();
                    OriginZ = min.Z();
                    break;
                case WorkZeroPreset.TopCenter:
                    OriginX = cx;
                    OriginY = cy;
                    OriginZ = max.Z();
                    break;
                case WorkZeroPreset.BottomCenter:
                    OriginX = cx;
                    OriginY = cy;
                    OriginZ = min.Z();
                    break;
                case WorkZeroPreset.BboxCenter:
                    OriginX = cx;
                    OriginY = cy;
                    OriginZ = cz;
                    break;
            }
        }

        public string Describe() =>
            $"X {OriginX:F2} · Y {OriginY:F2} · Z {OriginZ:F2}";

        public string PresetLabel(WorkZeroPreset preset) => preset switch
        {
            WorkZeroPreset.BboxMinCorner => "包围盒最小角 (0,0,0)",
            WorkZeroPreset.TopCenter => "顶面中心",
            WorkZeroPreset.BottomCenter => "底面中心",
            WorkZeroPreset.BboxCenter => "包围盒几何中心",
            _ => "自定义",
        };
    }
}
