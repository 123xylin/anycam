using AnyCAD.Foundation;
using System;

namespace Rapid.Robot
{
    internal class RobotTargetEditor : NodeFrameEidtor
    {
        IkSolver mSolver;
        RobotArm mRobotArm;
        public RobotTargetEditor(AxWidget target, RobotArm arm)
            : base(target, TransformWidget.Create(0.2f), "Robot.MoveTarget")
        {

            SetWidgetCenter(0);
            mRobotArm = arm;

            mSolver = IkSolver.Create("LMA");
            mSolver.Initialize(mRobotArm);
        }

        public override bool PreviewTransform(ViewContext ctx, Matrix4d trf)
        {
            if (!mSolver.MoveToFrame(trf))
                return false;

            for (uint ii = 1; ii < mRobotArm.GetJointCount(); ++ii)
            {
                var v = mSolver.GetValue(ii) * 180 / Math.PI;
                mRobotArm.SetValue(ii, v);
            }

            mRobotArm.UpdateFrames();

            ctx.RequestUpdate(EnumUpdateFlags.Scene);

            return base.PreviewTransform(ctx, trf);
        }
    }
}
