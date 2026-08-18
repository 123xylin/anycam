using AnyCAD.Foundation;
using AnyCAD.Simulate;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace RapidRobot
{
    public partial class RobotIndicatorForm : Form
    {
        private readonly AnyCAD.Robot.RobotControler mRobot;
        private readonly DataGridView mJointGrid = new DataGridView();

        public delegate void UpdateView();
        public UpdateView OnUpdate;

        public RobotIndicatorForm(AnyCAD.Robot.RobotControler robot)
        {
            mRobot = robot;
            InitializeComponent();
            ConfigureDynamicControls();
            PopulateArms();
            mRobot.ModelChanged += RobotModelChanged;
            RefreshRobotState(true);
        }

        private void ConfigureDynamicControls()
        {
            groupBox1.Visible = false;
            groupBox2.Visible = false;
            button3.Visible = false;
            buttonSave.Visible = false;
            timer1.Enabled = false;

            numericX.ReadOnly = true;
            numericY.ReadOnly = true;
            numericZ.ReadOnly = true;
            numericUpDownSpeed.DecimalPlaces = 3;
            numericUpDownSpeed.Minimum = 0.001M;
            numericUpDownSpeed.Maximum = 1000M;
            numericUpDownSpeed.Value = 1M;

            mJointGrid.Location = groupBox1.Location;
            mJointGrid.Size = new Size(groupBox2.Width, groupBox2.Bottom - groupBox1.Top);
            mJointGrid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            mJointGrid.AllowUserToAddRows = false;
            mJointGrid.AllowUserToDeleteRows = false;
            mJointGrid.AllowUserToResizeRows = false;
            mJointGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            mJointGrid.BackgroundColor = SystemColors.Window;
            mJointGrid.EditMode = DataGridViewEditMode.EditProgrammatically;
            mJointGrid.MultiSelect = false;
            mJointGrid.RowHeadersVisible = false;
            mJointGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            mJointGrid.Columns.Add("Index", "#");
            mJointGrid.Columns.Add("Name", "Joint");
            mJointGrid.Columns.Add("Type", "Type");
            mJointGrid.Columns.Add("Value", "Position");
            mJointGrid.Columns.Add("Unit", "Unit");
            mJointGrid.Columns.Add(new DataGridViewButtonColumn { Name = "Negative", HeaderText = "Jog -" });
            mJointGrid.Columns.Add(new DataGridViewButtonColumn { Name = "Positive", HeaderText = "Jog +" });
            mJointGrid.Columns[0].FillWeight = 35;
            mJointGrid.Columns[1].FillWeight = 120;
            mJointGrid.Columns[2].FillWeight = 75;
            mJointGrid.Columns[3].FillWeight = 75;
            mJointGrid.Columns[4].FillWeight = 40;
            mJointGrid.Columns[5].FillWeight = 55;
            mJointGrid.Columns[6].FillWeight = 55;
            mJointGrid.CellContentClick += JointGridCellContentClick;
            Controls.Add(mJointGrid);

            comboBox1.SelectedIndexChanged += ArmSelectionChanged;
        }

        private void PopulateArms()
        {
            comboBox1.Items.Clear();
            for (uint armIndex = 0; armIndex < mRobot.ArmCount; ++armIndex)
            {
                var name = mRobot.GetArmName(armIndex);
                comboBox1.Items.Add(string.IsNullOrWhiteSpace(name) ? $"Arm {armIndex}" : name);
            }

            if (comboBox1.Items.Count > 0)
                comboBox1.SelectedIndex = (int)mRobot.SelectedArmIndex;
        }

        private void ArmSelectionChanged(object sender, EventArgs e)
        {
            if (comboBox1.SelectedIndex < 0 || !mRobot.SelectArm((uint)comboBox1.SelectedIndex))
                return;

            RebuildJointRows();
            RefreshRobotState(true);
        }

        private void RebuildJointRows()
        {
            mJointGrid.Rows.Clear();
            var armIndex = mRobot.SelectedArmIndex;
            var jointCount = mRobot.GetJointCount(armIndex);
            for (uint pathOffset = 0; pathOffset < jointCount; ++pathOffset)
            {
                var type = mRobot.GetJointType(armIndex, pathOffset);
                var name = mRobot.GetJointName(armIndex, pathOffset);
                if (string.IsNullOrWhiteSpace(name))
                    name = $"Joint {pathOffset}";

                var unit = type == EnumRobotJointType.Revolute ? "deg" :
                    type == EnumRobotJointType.Prismatic ? "mm" : string.Empty;
                var rowIndex = mJointGrid.Rows.Add(pathOffset, name, type, string.Empty, unit,
                    type == EnumRobotJointType.Fixed ? "—" : "-",
                    type == EnumRobotJointType.Fixed ? "—" : "+");
                mJointGrid.Rows[rowIndex].Tag = pathOffset;
                if (type == EnumRobotJointType.Fixed)
                    mJointGrid.Rows[rowIndex].DefaultCellStyle.ForeColor = SystemColors.GrayText;
            }
        }

        private void JointGridCellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || (e.ColumnIndex != 5 && e.ColumnIndex != 6))
                return;

            var pathOffset = (uint)mJointGrid.Rows[e.RowIndex].Tag;
            var type = mRobot.GetJointType(mRobot.SelectedArmIndex, pathOffset);
            if (type == EnumRobotJointType.Fixed)
                return;

            var step = (double)numericUpDownSpeed.Value;
            if (type == EnumRobotJointType.Revolute)
                step *= Math.PI / 180.0;
            if (e.ColumnIndex == 5)
                step = -step;

            mRobot.AddJointPosition(mRobot.SelectedArmIndex, pathOffset, step);
        }

        private void RobotModelChanged()
        {
            if (IsDisposed)
                return;

            if (InvokeRequired)
                BeginInvoke(new Action(() => RefreshRobotState(true)));
            else
                RefreshRobotState(true);
        }

        private void RefreshRobotState(bool addTrackingPoint)
        {
            var armIndex = mRobot.SelectedArmIndex;
            var jointCount = mRobot.GetJointCount(armIndex);
            if (mJointGrid.Rows.Count != jointCount)
                RebuildJointRows();

            for (uint pathOffset = 0; pathOffset < jointCount; ++pathOffset)
            {
                var type = mRobot.GetJointType(armIndex, pathOffset);
                var value = mRobot.GetJointPosition(armIndex, pathOffset);
                if (type == EnumRobotJointType.Revolute)
                    value *= 180.0 / Math.PI;
                mJointGrid.Rows[(int)pathOffset].Cells[3].Value = double.IsFinite(value) ? $"{value:N3}" : "—";
            }

            using var transform = mRobot.GetArmTipWorldTransform(armIndex);
            var position = new Vector3(0);
            position.applyMatrix4d(transform);
            labelX.Text = $"{position.x:N3}";
            labelY.Text = $"{position.y:N3}";
            labelZ.Text = $"{position.z:N3}";
            numericX.Value = ClampDecimal(position.x, numericX.Minimum, numericX.Maximum);
            numericY.Value = ClampDecimal(position.y, numericY.Minimum, numericY.Maximum);
            numericZ.Value = ClampDecimal(position.z, numericZ.Minimum, numericZ.Maximum);

            if (addTrackingPoint)
                mRobot.AddTrackingPoint(position);
            OnUpdate?.Invoke();
        }

        private static decimal ClampDecimal(double value, decimal minimum, decimal maximum)
        {
            if (!double.IsFinite(value))
                return 0;
            var decimalValue = (decimal)value;
            return Math.Clamp(decimalValue, minimum, maximum);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            mRobot.ModelChanged -= RobotModelChanged;
            base.OnFormClosed(e);
        }

        private void checkBox1_CheckedChanged(object sender, EventArgs e)
        {
            mRobot.ShowTracking(((CheckBox)sender).Checked);
        }

        private void buttonReset_Click(object sender, EventArgs e)
        {
            mRobot.Reset();
        }

        // Kept because the legacy designer still wires these hidden controls.
        private void buttonPositive_MouseDown(object sender, MouseEventArgs e) { }
        private void buttonNegitive_MouseDown(object sender, MouseEventArgs e) { }
        private void buttonA_MouseUp(object sender, MouseEventArgs e) { }
        private void timer1_Tick(object sender, EventArgs e) { }
        private void numeric_ValueChanged(object sender, EventArgs e) { }
        private void button3_Click(object sender, EventArgs e) { }
        private void buttonSave_Click(object sender, EventArgs e) { }
    }
}
