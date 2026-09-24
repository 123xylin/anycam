using AnyCAD.Foundation;

namespace Rapid.Robot
{
    internal class RobotTargetEditor : NodeFrameEditor
    {
        private readonly AnyCAD.Robot.RobotControler mRobot;

        public RobotTargetEditor(SceneNode target, AnyCAD.Robot.RobotControler robot)
            : base(target, TransformWidget.Create(20.0f), "Robot.MoveTarget")
        {
            mRobot = robot;
            SetWidgetCenter(0);
        }

        public override bool PreviewTransform(ViewContext ctx, Matrix4d transform)
        {
            if (!mRobot.TryMoveSelectedArmTip(transform))
                return false;

            ctx.RequestUpdate(EnumUpdateFlags.Scene);
            return base.PreviewTransform(ctx, transform);
        }
    }
}
