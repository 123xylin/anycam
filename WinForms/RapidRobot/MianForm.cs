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
        private SimulationSdkForm mSimulationSdk;
        private CollisionDemoForm mCollisionDemo;
        private Func<string, CollisionTriangleMesh> mLoadLinkMesh;

        public MainForm()
        {
            InitializeComponent();
            var importButton = new ToolStripButton("导入 URDF");
            importButton.Click += OpenToolStripMenuItem_Click;
            var manualButton = new ToolStripDropDownButton("代码装配");
            manualButton.DropDownItems.Add("Kr500（STL）", null, CreateManualKr500_Click);
            manualButton.DropDownItems.Add("3A（BREP）", null, CreateManual3A_Click);
            menuStrip1.Items.Insert(0, importButton);
            menuStrip1.Items.Insert(1, manualButton);
            var sdkMenu = new ToolStripMenuItem("Simulate SDK");
            sdkMenu.Click += (_, _) =>
            {
                if (mRobotControler == null) return;
                mSimulationSdk?.Close();
                mSimulationSdk = new SimulationSdkForm(mRobotControler.Robot, OnRobotModelChanged);
                mSimulationSdk.Show(this);
            };
            menuStrip1.Items.Add(sdkMenu);
            var collisionMenu = new ToolStripMenuItem("Collision demo");
            collisionMenu.Click += (_, _) =>
            {
                if (mRobotControler == null || mLoadLinkMesh == null) return;
                if (mCollisionDemo != null && !mCollisionDemo.IsDisposed)
                {
                    mCollisionDemo.Activate();
                    return;
                }
                mCollisionDemo = new CollisionDemoForm(
                    mRobotControler.Robot, mLoadLinkMesh, OnRobotModelChanged);
                var form = mCollisionDemo;
                mRenderView.ShowSceneNode(form.PreviewRoot);
                mCollisionDemo.FormClosed += (_, _) => {
                    mRenderView.RemoveSceneNode(form.PreviewRoot.GetUuid());
                    if (ReferenceEquals(mCollisionDemo, form)) mCollisionDemo = null;
                };
                mCollisionDemo.Show(this);
            };
            menuStrip1.Items.Add(collisionMenu);
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
            mSimulationSdk?.Close();
            mSimulationSdk = null;
            mCollisionDemo?.Close();
            mCollisionDemo = null;
            mLoadLinkMesh = null;
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
            mCollisionDemo?.UpdateLinkTransforms();
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

            var urdfFile = dialog.FileName;
            LoadRobot(() => new RobotLoadResult(
                RobotIO.Load(new AnyCAD.Foundation.Path(urdfFile)),
                name => UrdfVisualMesh.Load(urdfFile, name)), "Failed to open URDF");
        }

        private void CreateManualKr500_Click(object sender, EventArgs e)
        {
            var modelDirectory = IOPath.Combine(AppContext.BaseDirectory, "Model", "Kr500");
            LoadRobot(() => new RobotLoadResult(
                ManualKr500Factory.Create(modelDirectory),
                name => ManualKr500Factory.LoadLinkMesh(modelDirectory, name)),
                "Failed to create Kr500 in code");
        }

        private void CreateManual3A_Click(object sender, EventArgs e)
        {
            var modelDirectory = IOPath.Combine(AppContext.BaseDirectory, "Model", "3A");
            LoadRobot(() => new RobotLoadResult(
                Manual3AFactory.Create(modelDirectory),
                name => Manual3AFactory.LoadLinkMesh(modelDirectory, name)),
                "Failed to create 3A in code");
        }

        private void LoadRobot(Func<RobotLoadResult> create, string errorTitle)
        {
            RobotLoadResult loaded;
            progressBar1.Style = ProgressBarStyle.Marquee;
            progressBar1.Visible = true;
            try
            {
                loaded = create();
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, errorTitle, MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }
            finally
            {
                progressBar1.Visible = false;
                progressBar1.Style = ProgressBarStyle.Blocks;
            }

            var robot = loaded?.Body;
            if (robot == null || loaded.LoadLinkMesh == null || robot.GetArmCount() == 0)
            {
                robot?.Dispose();
                MessageBox.Show(this, "The robot does not contain a usable Arm.", errorTitle,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            ReleaseRobotUi();
            mRenderView.SetEditor(null);
            mRenderView.ClearAll();
            mRenderView.ShowSceneNode(robot);

            mRobotControler = new AnyCAD.Robot.RobotControler(robot);
            mLoadLinkMesh = loaded.LoadLinkMesh;
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
