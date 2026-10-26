using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AnyCAM.Milling.Engine
{
    /// <summary>将 G 代码坐标平移到以 MCS 零点为程序原点（减去 Origin）。</summary>
    internal static partial class GcodeWorkZeroTransform
    {
        private static readonly Regex AxisWord = AxisWordRegex();

        public static string Apply(string gcode, WorkCoordinateSetup workZero)
        {
            if (string.IsNullOrEmpty(gcode))
                return gcode;

            if (Math.Abs(workZero.OriginX) < 1e-9 &&
                Math.Abs(workZero.OriginY) < 1e-9 &&
                Math.Abs(workZero.OriginZ) < 1e-9)
                return gcode;

            var lines = gcode.Replace("\r\n", "\n").Split('\n');
            var sb = new StringBuilder(gcode.Length);

            foreach (var line in lines)
            {
                sb.AppendLine(TransformLine(line, workZero));
            }

            return sb.ToString().TrimEnd();
        }

        private static string TransformLine(string line, WorkCoordinateSetup zero)
        {
            return AxisWord.Replace(line, match =>
            {
                var axis = match.Groups[1].Value[0];
                if (!double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
                    !double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
                    return match.Value;

                var shifted = axis switch
                {
                    'X' or 'x' => value - zero.OriginX,
                    'Y' or 'y' => value - zero.OriginY,
                    'Z' or 'z' => value - zero.OriginZ,
                    _ => value,
                };

                return $"{char.ToUpperInvariant(axis)}{shifted.ToString("0.###", CultureInfo.InvariantCulture)}";
            });
        }

        [GeneratedRegex(@"\b([XYZ])([-+]?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex AxisWordRegex();
    }
}
