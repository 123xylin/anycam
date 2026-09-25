using AnyCAD.Foundation;
using AnyCAD.Robot;
using AnyCAD.Simulate;
using RapidRobot;
using System;

namespace RapidRobot.Wpf
{
    internal sealed class RobotWorkspaceSession : IDisposable
    {
        private RobotBody? body;
        private RobotControler? controller;
        private CollisionDemoModel? collision;
        private bool disposed;

        internal RobotBody? Body => body;
        internal RobotControler? Controller => controller;
        internal CollisionDemoModel? Collision => collision;
        internal Func<string, CollisionTriangleMesh>? LoadLinkMesh { get; private set; }
        internal GroupSceneNode? CollisionPreview => collision?.PreviewRoot;

        internal event Action? Changed;

        internal void Load(RobotLoadResult loaded)
        {
            ThrowIfDisposed();
            if (loaded?.Body == null || loaded.LoadLinkMesh == null ||
                loaded.Body.GetArmCount() == 0)
            {
                loaded?.Body?.Dispose();
                throw new InvalidOperationException("The robot does not contain a usable Arm.");
            }

            ReleaseCore();
            body = loaded.Body;
            LoadLinkMesh = loaded.LoadLinkMesh;
            controller = new RobotControler(body);
            controller.ModelChanged += OnModelChanged;
            controller.SelectedArmChanged += OnSelectedArmChanged;
            collision = new CollisionDemoModel(body);
            collision.AddObstacle();
            Changed?.Invoke();
        }

        internal void Release()
        {
            ThrowIfDisposed();
            ReleaseCore();
        }

        private void OnModelChanged()
        {
            collision?.UpdateLinkTransforms();
            Changed?.Invoke();
        }

        private void OnSelectedArmChanged()
        {
            Changed?.Invoke();
        }

        private void ReleaseCore()
        {
            if (controller != null)
            {
                controller.ModelChanged -= OnModelChanged;
                controller.SelectedArmChanged -= OnSelectedArmChanged;
                controller = null;
            }

            collision?.PreviewRoot.Clear();
            collision = null;
            LoadLinkMesh = null;
            var oldBody = body;
            body = null;
            oldBody?.Dispose();
            Changed?.Invoke();
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(RobotWorkspaceSession));
        }

        public void Dispose()
        {
            if (disposed)
                return;
            ReleaseCore();
            disposed = true;
        }
    }
}
