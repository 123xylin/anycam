using AnyCAD.Foundation;

namespace AnyCAD.Robot.Studio
{
    public class RobotTargetEditor : NodeFrameEditor
    {
        private readonly RobotControler mRobot;

        public RobotTargetEditor(SceneNode target, RobotControler robot)
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
