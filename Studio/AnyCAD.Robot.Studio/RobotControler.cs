using AnyCAD.Foundation;
using AnyCAD.Simulate;
using System;

namespace AnyCAD.Robot.Studio
{
    public class RobotControler
    {
        private const uint TrajectoryCapacity = 1000;

        private readonly RobotBody mRobot;
        private readonly ParticleSceneNode mTrajectory = new ParticleSceneNode(TrajectoryCapacity, ColorTable.Red, 5);
        private uint mTrajectoryIndex;
        private uint mSelectedArmIndex;

        public event Action? ModelChanged;
        public event Action? SelectedArmChanged;

        public RobotBody Robot => mRobot;
        public ParticleSceneNode Trajectory => mTrajectory;
        public uint ArmCount => mRobot.GetArmCount();
        public uint SelectedArmIndex => mSelectedArmIndex;

        public RobotControler(RobotBody robot)
        {
            mRobot = robot ?? throw new ArgumentNullException(nameof(robot));
            mTrajectory.SetVisible(true);
        }

        public bool SelectArm(uint armIndex)
        {
            using var arm = GetArm(armIndex);
            if (arm == null)
                return false;

            mSelectedArmIndex = armIndex;
            SelectedArmChanged?.Invoke();
            ModelChanged?.Invoke();
            return true;
        }

        public string GetArmName(uint armIndex)
        {
            using var arm = GetArm(armIndex);
            return arm?.GetName() ?? string.Empty;
        }

        public uint GetJointCount(uint armIndex)
        {
            using var arm = GetArm(armIndex);
            return arm?.GetJointCount() ?? 0;
        }

        public string GetJointName(uint armIndex, uint pathOffset)
        {
            using var joint = GetJoint(armIndex, pathOffset);
            return joint?.GetName() ?? string.Empty;
        }

        public EnumRobotJointType GetJointType(uint armIndex, uint pathOffset)
        {
            using var joint = GetJoint(armIndex, pathOffset);
            return joint?.GetJointType() ?? EnumRobotJointType.Fixed;
        }

        public double GetJointPosition(uint armIndex, uint pathOffset)
        {
            using var joint = GetJoint(armIndex, pathOffset);
            if (joint == null)
                return double.NaN;

            using var index = new RobotArmIndex(armIndex);
            return mRobot.GetArmJointPosition(index, pathOffset);
        }

        public double GetJointMinValue(uint armIndex, uint pathOffset)
        {
            using var joint = GetJoint(armIndex, pathOffset);
            return joint?.GetMinValue() ?? double.NaN;
        }

        public double GetJointMaxValue(uint armIndex, uint pathOffset)
        {
            using var joint = GetJoint(armIndex, pathOffset);
            return joint?.GetMaxValue() ?? double.NaN;
        }

        public bool AddJointPosition(uint armIndex, uint pathOffset, double step)
        {
            if (GetJointType(armIndex, pathOffset) == EnumRobotJointType.Fixed)
                return false;

            var value = GetJointPosition(armIndex, pathOffset);
            if (!double.IsFinite(value) || !double.IsFinite(step))
                return false;

            value += step;
            if (GetJointType(armIndex, pathOffset) != EnumRobotJointType.Continuous)
            {
                var minValue = GetJointMinValue(armIndex, pathOffset);
                var maxValue = GetJointMaxValue(armIndex, pathOffset);
                if (!double.IsFinite(minValue) || !double.IsFinite(maxValue))
                    return false;
                value = Math.Clamp(value, minValue, maxValue);
            }
            using var index = new RobotArmIndex(armIndex);
            if (!mRobot.SetArmJointPosition(index, pathOffset, value))
                return false;

            mRobot.UpdateFrames();
            ModelChanged?.Invoke();
            return true;
        }

        public Matrix4d GetArmTipWorldTransform(uint armIndex)
        {
            using var arm = GetArm(armIndex);
            if (arm == null)
                return new Matrix4d(double.NaN);

            using var index = new RobotArmIndex(armIndex);
            return mRobot.GetArmTipTransform(index, true);
        }

        public bool TryMoveSelectedArmTip(Matrix4d targetWorld)
        {
            if (targetWorld == null)
                return false;

            using var armIndex = new RobotArmIndex(mSelectedArmIndex);
            using var arm = mRobot.GetArm(armIndex);
            if (arm == null)
                return false;

            using var tipFrameIndex = arm.GetTipFrameIndex();
            if (!tipFrameIndex.IsValid())
                return false;

            using var model = mRobot.GetModel();
            using var rootLinkIndex = model.GetRootLinkIndex();
            if (!rootLinkIndex.IsValid())
                return false;

            using var rootWorld = mRobot.GetLinkTransform(rootLinkIndex, true);
            using var rootWorldInverse = rootWorld.inversed();
            using var targetInBody = rootWorldInverse.multiplied(targetWorld);
            using var target = new RobotSimulationTarget
            {
                FrameIndex = tipFrameIndex,
                TargetInBody = targetInBody
            };
            using var targets = new RobotSimulationTargetList();
            targets.Add(target);
            using var session = new RobotSimulationSession(mRobot);
            using var result = session.SolveIk(targets);
            if (result == null || !result.IsSuccess())
                return false;

            using var positions = new RobotJointPositionList();
            using (var solvedPositions = result.JointPositions)
            {
                for (var i = 0; i < solvedPositions.Count; ++i)
                {
                    using var solved = solvedPositions[i];
                    using var jointIndex = new RobotJointIndex(solved.JointIndex);
                    using var position = new RobotJointPosition
                    {
                        JointIndex = jointIndex,
                        Position = solved.Position
                    };
                    positions.Add(position);
                }
            }
            using var sourceState = session.GetSourceState();
            if (mRobot.ApplyJointPositions(positions, sourceState) != EnumRobotJointApplyStatus.Success)
                return false;

            ModelChanged?.Invoke();
            return true;
        }

        public void Reset()
        {
            mRobot.ResetToHome();
            ModelChanged?.Invoke();
        }

        public void ShowLinks(bool show)
        {
            mRobot.ShowLinks(show);
            ModelChanged?.Invoke();
        }

        public void ShowTracking(bool show)
        {
            mTrajectory.SetVisible(show);
            ModelChanged?.Invoke();
        }

        public void AddTrackingPoint(Vector3 position)
        {
            mTrajectory.SetPosition(mTrajectoryIndex, position);
            mTrajectoryIndex = (mTrajectoryIndex + 1) % TrajectoryCapacity;
            mTrajectory.UpdateBoundingBox();
            mTrajectory.RequestUpdate();
        }

        private RobotArm? GetArm(uint armIndex)
        {
            if (armIndex >= mRobot.GetArmCount())
                return null;

            using var index = new RobotArmIndex(armIndex);
            return mRobot.GetArm(index);
        }

        private RobotJointEdge? GetJoint(uint armIndex, uint pathOffset)
        {
            using var arm = GetArm(armIndex);
            if (arm == null || pathOffset >= arm.GetJointCount())
                return null;

            using var index = new RobotArmIndex(armIndex);
            using var jointIndex = mRobot.GetArmJointIndex(index, pathOffset);
            return jointIndex.IsValid() ? mRobot.GetJoint(jointIndex) : null;
        }
    }
}
