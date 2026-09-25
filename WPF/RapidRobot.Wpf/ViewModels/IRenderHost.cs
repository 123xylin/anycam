using AnyCAD.Foundation;

namespace RapidRobot.Wpf.ViewModels
{
    /// <summary>
    /// 渲染桥接接口：ViewModel 不直接持有 AnyCAD RenderControl（指令式 WPF 控件），
    /// 所有需要渲染的请求通过本接口由 View 层实现，保持 ViewModel 可独立测试。
    /// </summary>
    public interface IRenderHost
    {
        bool IsReady { get; }
        void ClearAll();
        void ShowSceneNode(SceneNode node);
        void RequestDraw(EnumUpdateFlags flags);
        void ZoomAll();
        void SetStandardView();
        void ShowCoordinateGrid(bool visible);
        void SetEditor(Editor editor);
        void ClearEditor();
    }
}
