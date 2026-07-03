using System;
using System.Collections.Generic;
using System.Diagnostics;
using AnyCAD.Foundation;
using AnyTubeNet.Models;

namespace AnyTubeNet.Visualization;

public sealed class PathAnimator
{
    private const double AlignmentStepAngle = Math.PI / 180 * 10;
    private const double SimulationTimeStep = 0.033;
    private const double DefaultCuttingSpeed = 50.0;
    private const double DefaultRapidSpeed = 100.0;
    private const double DefaultApproachSpeed = 80.0;
    private const double DefaultRetractSpeed = 80.0;
    private const double MaxCuttingFrameStepDistance = 20.0;
    private const float MaxFrameMilliseconds = 100.0f;
    private const double ApproachDistance = 20.0;
    private const double RetractDistance = 20.0;
    private const double StationY = 0.0;

    private static readonly Vector3d WorldYAxis = new(0, 1, 0);

    private readonly IReadOnlyList<SceneNode> _movingNodes;
    private readonly SceneNode _laserHeadNode;
    private readonly IReadOnlyList<SimulationFrame> _frames;
    private readonly double _laserHeadHeight;

    private int _frameIndex;
    private double _frameAccumulatorMilliseconds;
    private long _lastTickTimestamp;
    private bool _hasTickTimestamp;

    public bool IsPlaying { get; private set; }
    public double Speed { get; set; } = 1.0;
    public double Progress => _frames.Count <= 1 ? 0 : (double)_frameIndex / (_frames.Count - 1);

    public PathAnimator(AnyTubeScene scene)
    {
        _movingNodes = scene.MovingNodes;
        _laserHeadNode = scene.LaserHeadNode;
        _laserHeadHeight = scene.LaserHeadHeight;
        _frames = BuildFrames(scene.PathGroups, scene.FirstPoint);

        Reset();
    }

    public void Play()
    {
        if (_frames.Count == 0)
            return;

        if (_frameIndex >= _frames.Count - 1)
            Reset();

        ResetCallbackTime();
        IsPlaying = true;
    }

    public void Pause()
    {
        IsPlaying = false;
        ResetCallbackTime();
    }

    public void Reset()
    {
        _frameIndex = 0;
        _frameAccumulatorMilliseconds = 0;
        IsPlaying = false;
        ResetCallbackTime();

        if (_frames.Count > 0)
            ApplyFrame(_frames[0]);
        else
        {
            ApplyTransform(Matrix4d.makeTranslation(0, 0, 0));
            ApplyLaserHead(new Vector3d(0, 0, ApproachDistance));
        }
    }

    public bool Tick(float callbackTime)
    {
        if (!IsPlaying || float.IsNaN(callbackTime) || float.IsInfinity(callbackTime))
            return false;

        var elapsedMilliseconds = ResolveElapsedMilliseconds();
        if (elapsedMilliseconds <= 0)
            return false;

        elapsedMilliseconds = Math.Min(elapsedMilliseconds, MaxFrameMilliseconds);
        _frameAccumulatorMilliseconds += elapsedMilliseconds * Math.Max(0.05, Speed);

        var frameDurationMilliseconds = SimulationTimeStep * 1000.0;
        var changed = false;
        while (_frameAccumulatorMilliseconds >= frameDurationMilliseconds)
        {
            _frameAccumulatorMilliseconds -= frameDurationMilliseconds;
            if (_frameIndex >= _frames.Count - 1)
            {
                IsPlaying = false;
                ResetCallbackTime();
                break;
            }

            _frameIndex++;
            ApplyFrame(_frames[_frameIndex]);
            changed = true;
        }

        return changed;
    }

    private void ResetCallbackTime()
    {
        _lastTickTimestamp = 0;
        _hasTickTimestamp = false;
    }

    private static double TimestampToMilliseconds(long timestampDelta)
    {
        return timestampDelta * 1000.0 / Stopwatch.Frequency;
    }

    private double ResolveElapsedMilliseconds()
    {
        var now = Stopwatch.GetTimestamp();
        if (!_hasTickTimestamp)
        {
            _lastTickTimestamp = now;
            _hasTickTimestamp = true;
            return 0;
        }

        var elapsedMilliseconds = TimestampToMilliseconds(now - _lastTickTimestamp);
        _lastTickTimestamp = now;
        return elapsedMilliseconds;
    }

    private static IReadOnlyList<SimulationFrame> BuildFrames(
        IReadOnlyList<TubePathGroup> pathGroups,
        Vector3d firstScenePoint)
    {
        var frames = new List<SimulationFrame>();
        var context = new SimulationContext();
        var currentCut = new List<TubePathSegment>();
        var pendingRapid = new List<TubePathSegment>();
        var firstSafeSample = new PathSample(
            new Vector3d(firstScenePoint.x, StationY, firstScenePoint.z),
            new Vector3d(0, 0, -1));
        var previousSafeSample = firstSafeSample;

        foreach (var group in pathGroups)
        {
            foreach (var segment in group.Segments)
            {
                if (segment.IsMachining)
                {
                    if (currentCut.Count == 0)
                    {
                        var nextStart = SampleStart(segment);
                        AppendSafeTransition(frames, context, pendingRapid, previousSafeSample, nextStart);
                        pendingRapid.Clear();
                        AppendToolOffsetFramesAtAngle(
                            frames,
                            context,
                            nextStart,
                            context.Angle,
                            ApproachDistance,
                            0.0,
                            DefaultApproachSpeed);
                    }

                    currentCut.Add(segment);
                    continue;
                }

                FlushCutPath(frames, context, currentCut, ref previousSafeSample);

                if (segment.IsRapid)
                    pendingRapid.Add(segment);
            }

            FlushCutPath(frames, context, currentCut, ref previousSafeSample);
        }

        return frames;
    }

    private static void AppendSafeTransition(
        List<SimulationFrame> frames,
        SimulationContext context,
        List<TubePathSegment> pendingRapid,
        PathSample previousSafeSample,
        PathSample nextStart)
    {
        if (pendingRapid.Count > 0)
        {
            var rapidStart = SampleStart(pendingRapid[0]);
            AppendAlignmentFrames(frames, context, rapidStart, ApproachDistance);

            foreach (var rapidSegment in pendingRapid)
                AppendCuttingFrames(frames, context, rapidSegment, ApproachDistance, DefaultRapidSpeed);

            previousSafeSample = SampleEnd(pendingRapid[^1]);
        }

        if (frames.Count == 0 || NeedsAlignment(context, previousSafeSample, nextStart))
            AppendAlignmentFrames(frames, context, nextStart, ApproachDistance);
    }

    private static void FlushCutPath(
        List<SimulationFrame> frames,
        SimulationContext context,
        List<TubePathSegment> currentCut,
        ref PathSample previousSafeSample)
    {
        if (currentCut.Count == 0)
            return;

        foreach (var segment in currentCut)
            AppendCuttingFrames(frames, context, segment, 0.0, DefaultCuttingSpeed);

        var pathEnd = SampleEnd(currentCut[^1]);
        AppendToolOffsetFramesAtAngle(
            frames,
            context,
            pathEnd,
            CuttingRotationAngle(pathEnd.Direction),
            0.0,
            RetractDistance,
            DefaultRetractSpeed);

        previousSafeSample = pathEnd;
        currentCut.Clear();
    }

    private static bool NeedsAlignment(SimulationContext context, PathSample current, PathSample target)
    {
        var targetAngle = NearestEquivalentAngle(CuttingRotationAngle(target.Direction), context.Angle);
        var targetYOffset = ComputeYOffsetAtAngle(target.Point, targetAngle);
        return Math.Abs(targetAngle - context.Angle) > 1.0e-9
            || Math.Abs(targetYOffset - context.YOffset) > 1.0e-9
            || current.Point.distanceTo(target.Point) > 1.0e-9;
    }

    private static void AppendCuttingFrames(
        List<SimulationFrame> frames,
        SimulationContext context,
        TubePathSegment segment,
        double toolOffset,
        double cuttingSpeed)
    {
        var distance = segment.Length;
        var maxStepDistance = Math.Min(NormalizeCuttingSpeed(cuttingSpeed) * SimulationTimeStep, MaxCuttingFrameStepDistance);
        var steps = Math.Max(1, (int)Math.Ceiling(distance / maxStepDistance));
        for (var i = 1; i <= steps; i++)
        {
            var ratio = (double)i / steps;
            AppendCuttingFrame(frames, context, InterpolateSample(segment, ratio), toolOffset);
        }
    }

    private static void AppendToolOffsetFramesAtAngle(
        List<SimulationFrame> frames,
        SimulationContext context,
        PathSample localPoint,
        double angle,
        double startOffset,
        double endOffset,
        double speed)
    {
        var targetAngle = NearestEquivalentAngle(angle, context.Angle);
        var distance = Math.Abs(endOffset - startOffset);
        var maxStepDistance = NormalizeToolOffsetSpeed(speed) * SimulationTimeStep;
        var steps = Math.Max(1, (int)Math.Ceiling(distance / maxStepDistance));
        for (var i = 1; i <= steps; i++)
        {
            var ratio = (double)i / steps;
            var toolOffset = startOffset + (endOffset - startOffset) * ratio;
            AppendCuttingFrameAtAngle(frames, context, localPoint, targetAngle, toolOffset);
        }
    }

    private static void AppendAlignmentFrames(
        List<SimulationFrame> frames,
        SimulationContext context,
        PathSample localPoint,
        double toolOffset)
    {
        var targetAngle = NearestEquivalentAngle(CuttingRotationAngle(localPoint.Direction), context.Angle);
        var targetYOffset = ComputeYOffsetAtAngle(localPoint.Point, targetAngle);
        var targetTubeTransform = MakeTubeTransform(targetAngle, targetYOffset);
        var targetToolLocation = ToolLocationAt(localPoint.Point, targetTubeTransform, toolOffset);
        var startToolLocation = frames.Count == 0 ? targetToolLocation : frames[^1].ToolLocation;
        var angleDistance = Math.Abs(targetAngle - context.Angle);
        var yDistance = Math.Abs(targetYOffset - context.YOffset);
        var toolDistance = startToolLocation.distanceTo(targetToolLocation);
        var speed = frames.Count == 0 ? NormalizeCuttingSpeed(DefaultCuttingSpeed) : NormalizeRapidSpeed(DefaultRapidSpeed);
        var maxStepDistance = speed * SimulationTimeStep;

        var angleSteps = (int)Math.Ceiling(angleDistance / AlignmentStepAngle);
        var moveSteps = (int)Math.Ceiling(Math.Max(yDistance, toolDistance) / maxStepDistance);
        var steps = Math.Max(1, Math.Max(angleSteps, moveSteps));
        var startIndex = frames.Count == 0 ? 0 : 1;
        var startAngle = context.Angle;
        var startYOffset = context.YOffset;
        for (var i = startIndex; i <= steps; i++)
        {
            var ratio = (double)i / steps;
            var angle = startAngle + (targetAngle - startAngle) * ratio;
            var yOffset = startYOffset + (targetYOffset - startYOffset) * ratio;
            var toolLocation = startToolLocation + (targetToolLocation - startToolLocation) * ratio;
            AppendFrameAtState(frames, context, angle, yOffset, toolLocation);
        }
    }

    private static void AppendCuttingFrame(
        List<SimulationFrame> frames,
        SimulationContext context,
        PathSample localPoint,
        double toolOffset)
    {
        AppendCuttingFrameAtAngle(frames, context, localPoint, CuttingRotationAngle(localPoint.Direction), toolOffset);
    }

    private static void AppendCuttingFrameAtAngle(
        List<SimulationFrame> frames,
        SimulationContext context,
        PathSample localPoint,
        double angle,
        double toolOffset)
    {
        var targetAngle = NearestEquivalentAngle(angle, context.Angle);
        var yOffset = ComputeYOffsetAtAngle(localPoint.Point, targetAngle);
        var tubeTransform = MakeTubeTransform(targetAngle, yOffset);
        AppendFrameAtState(frames, context, targetAngle, yOffset, ToolLocationAt(localPoint.Point, tubeTransform, toolOffset));
    }

    private static void AppendFrameAtState(
        List<SimulationFrame> frames,
        SimulationContext context,
        double angle,
        double yOffset,
        Vector3d toolLocation)
    {
        frames.Add(new SimulationFrame(MakeTubeTransform(angle, yOffset), toolLocation, angle, yOffset));
        context.Angle = angle;
        context.YOffset = yOffset;
    }

    private static Matrix4d MakeTubeTransform(double angle, double yOffset)
    {
        var rotation = Matrix4d.makeRotationAxis(WorldYAxis, angle);
        var translation = Matrix4d.makeTranslation(0, yOffset, 0);
        return translation * rotation;
    }

    private static double ComputeYOffsetAtAngle(Vector3d localPoint, double angle)
    {
        var worldPoint = MakeTubeTransform(angle, 0.0) * localPoint;
        return StationY - worldPoint.y;
    }

    private static Vector3d ToolLocationAt(Vector3d localPoint, Matrix4d tubeTransform, double toolOffset)
    {
        return (tubeTransform * localPoint) + new Vector3d(0, 0, toolOffset);
    }

    private void ApplyFrame(SimulationFrame frame)
    {
        ApplyTransform(frame.TubeTransform);
        ApplyLaserHead(frame.ToolLocation);
    }

    private void ApplyTransform(Matrix4d transform)
    {
        foreach (var node in _movingNodes)
        {
            node.SetTransform(transform);
            node.RequestUpdate();
        }
    }

    private void ApplyLaserHead(Vector3d toolLocation)
    {
        _laserHeadNode.SetTransform(Matrix4d.makeTranslation(
            toolLocation.x,
            toolLocation.y,
            toolLocation.z + _laserHeadHeight / 2.0));
        _laserHeadNode.RequestUpdate();
    }

    private static double NearestEquivalentAngle(double angle, double reference)
    {
        while (angle - reference > Math.PI)
            angle -= Math.PI * 2.0;

        while (reference - angle > Math.PI)
            angle += Math.PI * 2.0;

        return angle;
    }

    private static double NormalizeCuttingSpeed(double cuttingSpeed)
    {
        return cuttingSpeed > 1.0e-9 ? cuttingSpeed : DefaultCuttingSpeed;
    }

    private static double NormalizeRapidSpeed(double rapidSpeed)
    {
        return rapidSpeed > 1.0e-9 ? rapidSpeed : DefaultRapidSpeed;
    }

    private static double NormalizeToolOffsetSpeed(double speed)
    {
        return speed > 1.0e-9 ? speed : DefaultApproachSpeed;
    }

    private static double CuttingRotationAngle(Vector3d direction)
    {
        return Math.Atan2(direction.x, -direction.z);
    }

    private static PathSample InterpolateSample(TubePathSegment segment, double ratio)
    {
        ratio = Math.Clamp(ratio, 0.0, 1.0);
        return new PathSample(segment.Interpolate(ratio), InterpolateDirection(segment.StartDirection, segment.EndDirection, ratio));
    }

    private static Vector3d InterpolateDirection(Vector3d start, Vector3d end, double ratio)
    {
        ratio = Math.Clamp(ratio, 0.0, 1.0);
        var direction = start * (1.0 - ratio) + end * ratio;
        if (direction.length() > 1.0e-9)
            return direction.normalized();

        return ratio <= 0.5 ? start : end;
    }

    private static PathSample SampleStart(TubePathSegment segment)
    {
        return new PathSample(segment.Start, segment.StartDirection);
    }

    private static PathSample SampleEnd(TubePathSegment segment)
    {
        return new PathSample(segment.End, segment.EndDirection);
    }

    private sealed class SimulationContext
    {
        public double Angle { get; set; }
        public double YOffset { get; set; }
    }

    private readonly record struct PathSample(Vector3d Point, Vector3d Direction);
    private readonly record struct SimulationFrame(Matrix4d TubeTransform, Vector3d ToolLocation, double Angle, double YOffset);
}
