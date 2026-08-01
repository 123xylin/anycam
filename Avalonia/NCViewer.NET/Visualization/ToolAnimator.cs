using System;
using System.Collections.Generic;
using System.Diagnostics;
using AnyCAD.Foundation;
using NcViewerNet.Models;

namespace NcViewerNet.Visualization;

public sealed class ToolAnimator
{
    private const float MaxFrameMilliseconds = 100.0f;
    private const double DefaultDurationSeconds = 30.0;

    private readonly SceneNode _toolNode;
    private readonly double _toolHeight;
    private readonly IReadOnlyList<NcSegment> _segments;
    private readonly double[] _cumulative;
    private readonly double _totalDistance;
    private readonly double _unitsPerSecond;

    private double _currentDistance;
    private long _lastTickTimestamp;
    private bool _hasTickTimestamp;

    public bool IsPlaying { get; private set; }
    public double Speed { get; set; } = 1.0;
    public double Progress => _totalDistance <= 0 ? 0 : _currentDistance / _totalDistance;

    public ToolAnimator(NcScene scene)
    {
        _toolNode = scene.ToolNode;
        _toolHeight = scene.ToolHeight;
        _segments = scene.Segments;
        _cumulative = scene.Cumulative;
        _totalDistance = scene.TotalDistance;
        _unitsPerSecond = _totalDistance > 0 ? _totalDistance / DefaultDurationSeconds : 1.0;

        Reset();
    }

    public void Play()
    {
        if (_totalDistance <= 0)
            return;

        if (_currentDistance >= _totalDistance - 1e-9)
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
        _currentDistance = 0;
        IsPlaying = false;
        ResetCallbackTime();
        ApplyTool(PositionAt(0));
    }

    public bool Tick(float callbackTime)
    {
        if (!IsPlaying || float.IsNaN(callbackTime) || float.IsInfinity(callbackTime))
            return false;

        var elapsedMilliseconds = ResolveElapsedMilliseconds();
        if (elapsedMilliseconds <= 0)
            return false;

        elapsedMilliseconds = Math.Min(elapsedMilliseconds, MaxFrameMilliseconds);
        var effectiveSpeed = Math.Max(0.05, Speed);
        _currentDistance += (elapsedMilliseconds / 1000.0) * _unitsPerSecond * effectiveSpeed;

        if (_currentDistance >= _totalDistance)
        {
            _currentDistance = _totalDistance;
            IsPlaying = false;
            ResetCallbackTime();
        }

        ApplyTool(PositionAt(_currentDistance));
        return true;
    }

    private Vector3d PositionAt(double distance)
    {
        if (_segments.Count == 0)
            return new Vector3d(0, 0, 0);

        if (distance <= 0)
            return _segments[0].Start;

        if (distance >= _totalDistance)
            return _segments[^1].End;

        var index = Array.BinarySearch(_cumulative, distance);
        if (index < 0)
            index = ~index;

        var segmentIndex = index - 1;
        if (segmentIndex < 0)
            segmentIndex = 0;
        if (segmentIndex >= _segments.Count)
            segmentIndex = _segments.Count - 1;

        var segment = _segments[segmentIndex];
        var segmentLength = _cumulative[segmentIndex + 1] - _cumulative[segmentIndex];
        var ratio = segmentLength > 1e-9
            ? (distance - _cumulative[segmentIndex]) / segmentLength
            : 0.0;

        return segment.Start + (segment.End - segment.Start) * ratio;
    }

    private void ApplyTool(Vector3d tip)
    {
        NcSceneBuilder.PlaceTool(_toolNode, tip, _toolHeight);
    }

    private void ResetCallbackTime()
    {
        _lastTickTimestamp = 0;
        _hasTickTimestamp = false;
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

        var elapsed = (now - _lastTickTimestamp) * 1000.0 / Stopwatch.Frequency;
        _lastTickTimestamp = now;
        return elapsed;
    }
}
