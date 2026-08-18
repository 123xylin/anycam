using AnyCAD.Forms;
using AnyCAD.Foundation;
using AnyCAD.Robot;
using AnyCAD.Simulate;
using Rapid.Robot;
using System;
using System.IO;
using System.Windows.Forms;
using IOPath = System.IO.Path;

namespace RapidRobot
{
    public partial class MainForm : Form
    {
        private readonly RenderControl mRenderView;
        private AnyCAD.Robot.RobotControler mRobotControler;
        private GroupSceneNode mIkTarget;
        private RobotTargetEditor mIkEditor;
        private RobotIndicatorForm mIndicatorUI;
        private bool mShowLinks = true;

        public MainForm()
        {
            InitializeComponent();
            mRenderView = new RenderControl(panel1);
            mRenderView.ViewerReady += MRenderView_ViewerReady;
            newToolStripMenuItem.Click += NewProject;
            openToolStripMenuItem.Click += OpenToolStripMenuItem_Click;
            indicatorToolStripMenuItem.Click += IndicatorToolStripMenuItem_Click;
            showLinktoolStripMenuItem1.Click += ShowLinktoolStripMenuItem1_Click;

            saveToolStripMenuItem.Visible = false;
            saveAsToolStripMenuItem.Visible = false;
            saveTemplateToolStripMenuItem.Visible = false;
            toolStripSeparator.Visible = false;
            toolStripSeparator1.Visible = false;
            toolStripSeparator2.Visible = false;
        }

        private void MRenderView_ViewerReady()
        {
            mRenderView.SceneManager.SetViewScale(1);
        }

        private void NewProject(object sender, EventArgs e)
        {
            ReleaseRobotUi();
            mRenderView.SetEditor(null);
            mRenderView.ClearAll();
            mRenderView.RequestDraw(EnumUpdateFlags.Scene);
        }

        private void ReleaseRobotUi()
        {
            if (mRobotControler != null)
            {
                mRobotControler.ModelChanged -= OnRobotModelChanged;
                mRobotControler.SelectedArmChanged -= ResetIkTargetToSelectedArm;
            }

            if (mIndicatorUI != null && !mIndicatorUI.IsDisposed)
                mIndicatorUI.Close();

            mIndicatorUI = null;
            mIkTarget = null;
            mIkEditor = null;
            mRobotControler = null;
        }

        private void OnRobotModelChanged()
        {
            mRenderView.RequestDraw(EnumUpdateFlags.Scene);
        }

        private void ResetIkTargetToSelectedArm()
        {
            if (mIkTarget == null || mRobotControler == null)
                return;

            using var tipWorld = mRobotControler.GetArmTipWorldTransform(mRobotControler.SelectedArmIndex);
            mIkTarget.SetTransform(tipWorld);
            mIkTarget.RequestUpdate();
            mIkEditor?.UpdateWidget();
            mRenderView.RequestDraw(EnumUpdateFlags.Scene);
        }

        private void OpenToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using var dialog = new OpenFileDialog
            {
                Filter = "URDF robot model (*.urdf)|*.urdf",
                Title = "Open robot URDF"
            };
            var modelDirectory = IOPath.Combine(AppContext.BaseDirectory, "Model");
            if (Directory.Exists(modelDirectory))
                dialog.InitialDirectory = modelDirectory;
            if (dialog.ShowDialog() != DialogResult.OK)
                return;

            RobotBody robot;
            progressBar1.Style = ProgressBarStyle.Marquee;
            progressBar1.Visible = true;
            try
            {
                robot = RobotIO.Load(new AnyCAD.Foundation.Path(dialog.FileName));
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, "Failed to open URDF", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }
            finally
            {
                progressBar1.Visible = false;
                progressBar1.Style = ProgressBarStyle.Blocks;
            }

            if (robot == null || robot.GetArmCount() == 0)
            {
                MessageBox.Show(this, "The URDF does not contain a usable robot Arm.", "Failed to open URDF",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            ReleaseRobotUi();
            mRenderView.SetEditor(null);
            mRenderView.ClearAll();
            mRenderView.ShowSceneNode(robot);

            mRobotControler = new AnyCAD.Robot.RobotControler(robot);
            mRobotControler.ModelChanged += OnRobotModelChanged;
            mRobotControler.SelectedArmChanged += ResetIkTargetToSelectedArm;
            mRenderView.ShowSceneNode(mRobotControler.Trajectory);

            mShowLinks = true;
            mRobotControler.ShowLinks(true);
            mRenderView.Viewer.SetStandardView(EnumStandardView.DefaultView, false);
            mRenderView.ZoomAll();
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            mRenderView.GetCamera().SetProjectionType(EnumProjectionType.Orthographic);
            mRenderView.RequestDraw(EnumUpdateFlags.Camera);
            mRenderView.ShowCoordinateGrid(true);
        }

        private void IndicatorToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (mRobotControler == null)
                return;

            if (mIndicatorUI != null && !mIndicatorUI.IsDisposed)
            {
                mIndicatorUI.Activate();
                return;
            }

            mIndicatorUI = new RobotIndicatorForm(mRobotControler)
            {
                Owner = this,
                OnUpdate = OnRobotModelChanged
            };
            mIndicatorUI.FormClosed += (_, _) => mIndicatorUI = null;
            mIndicatorUI.Show();
        }

        private void ShowLinktoolStripMenuItem1_Click(object sender, EventArgs e)
        {
            if (mRobotControler == null)
                return;

            mShowLinks = !mShowLinks;
            mRobotControler.ShowLinks(mShowLinks);
        }

        private void moveToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (mRobotControler == null)
                return;

            if (mIkTarget == null)
            {
                mIkTarget = new GroupSceneNode();
                mIkTarget.SetName("Robot IK target");
                mRenderView.ShowSceneNode(mIkTarget);
            }

            ResetIkTargetToSelectedArm();
            mIkEditor = new RobotTargetEditor(mIkTarget, mRobotControler);
            mRenderView.SetEditor(mIkEditor);
        }

        private void byNodeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            mRenderView.ViewContext.SetPickFilter(EnumShapeFilter.RootNode | EnumShapeFilter.VertexEdgeFace);
        }

        private void bySubNodeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            mRenderView.ViewContext.SetPickFilter(EnumShapeFilter.LeafNode | EnumShapeFilter.VertexEdgeFace);
        }
    }
}
