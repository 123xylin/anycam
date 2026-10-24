using System.Diagnostics;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace AnyCAM.Test.Ui;

/// <summary>
/// 界面自动化命令行：按控件名字驱动 AnyCAM.Milling 主窗口，用于冒烟验证与截图取证。
/// <para>
/// 之所以用 UI Automation 而不是模拟鼠标：坐标点击在 DPI 缩放、窗口位置变化下极易失效，
/// 而按控件名定位能直接命中 WPF 元素，且不需要在业务工程里开测试钩子。
/// </para>
/// </summary>
internal static class Program
{
    private const string ProcessName = "AnyCAM";

    private static int Main(string[] args)
    {
        // 默认只操作已经跑起来的实例：自动新起一个会让「上一步算好的结果」凭空消失，
        // 截图和断言就会对不上。确实要冷启动时显式加 --launch。
        var launch = args.Contains("--launch");
        args = args.Where(a => a != "--launch").ToArray();

        var app = AppWindow.Find(ProcessName);
        if (app == null && launch)
            app = AppWindow.Launch(ProcessName);

        if (app == null)
        {
            Console.WriteLine($"未找到运行中的 {ProcessName}.Milling（需要冷启动请加 --launch）");
            return 2;
        }

        using var _ = app;

        app.BringToFront();
        var root = AutomationElement.FromHandle(app.Handle);
        if (root == null)
        {
            Console.WriteLine("无法获取 UI Automation 根元素");
            return 3;
        }

        var command = args.Length > 0 ? args[0] : "--help";

        // 显式指定被测程序：仓库里有多份 AnyCAM.Milling.exe（例如探针工程的输出目录），
        // 逐级向上搜索可能命中旧副本，布局验证就会一直看到老界面。
        var exeIndex = Array.FindIndex(args, a => a == "--exe");
        if (exeIndex >= 0 && exeIndex + 1 < args.Length)
        {
            AppWindow.StartupPath = args[exeIndex + 1];
            args = args.Where((_, i) => i != exeIndex && i != exeIndex + 1).ToArray();
        }

        try
        {
            return command switch
            {
                "--list" => ListButtons(root),
                "--dump" => Dump(root),
                "--tree" => ListItems(root, ControlType.TreeItem),
                "--tabs" => ListItems(root, ControlType.TabItem),
                "click" => Click(root, Need(args, 1, "click <按钮名>")),
                "tap" => Tap(Need(args, 1, "tap <x,y>")),
                "toggle" => Toggle(root, Need(args, 1, "toggle <开关名>")),
                "select" => Select(root, ControlType.TreeItem, Need(args, 1, "select <节点名>")),
                "tab" => Select(root, ControlType.TabItem, Need(args, 1, "tab <标签名>")),
                "shot" => Shot(app, args.Length > 1 ? args[1] : null),
                _ => Help(),
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"失败：{ex.GetType().Name} {ex.Message}");
            return 4;
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            用法：anycam.ui <命令> [参数]

              --list               列出全部按钮
              --tree               列出加工浏览器节点
              --tabs               列出参数面板标签
              click <按钮名>        点击按钮，如 click 生成刀路
              toggle <开关名>       切换复选框，如 toggle 显示毛坯
              select <节点名>       选中加工浏览器节点，如 select OP2
              tab <标签名>          切换参数标签，如 tab NC
              shot [文件路径]       截图主窗口；省略路径则存到 Test/Temp

            加 --launch 可在没有运行实例时冷启动被测程序；默认只操作已运行的窗口。

            所有命令都会先把主窗口提到前台，执行后按需等待界面刷新。
            """);
        return 0;
    }

    private static string Need(string[] args, int index, string usage)
    {
        if (args.Length > index)
            return args[index];

        throw new ArgumentException($"缺少参数，用法：{usage}");
    }

    private static IEnumerable<AutomationElement> Find(AutomationElement root, ControlType type)
    {
        var condition = new PropertyCondition(AutomationElement.ControlTypeProperty, type);
        // 用 TreeWalker 逐个子节点走：FindAll 会在遍历中途元素被重建时抛 ElementNotAvailable，
        // 整批结果一起丢失（表现为「明明屏上有这个按钮，列表里却没有」）。
        var children = root.FindAll(TreeScope.Children, Condition.TrueCondition);
        foreach (AutomationElement child in children)
        {
            var found = child.Current.ControlType;
            if (found == type)
                yield return child;

            IEnumerable<AutomationElement> nested;
            try
            {
                nested = Find(child, type);
            }
            catch (ElementNotAvailableException)
            {
                continue;
            }

            foreach (var item in nested)
                yield return item;
        }
    }

    private static int ListButtons(AutomationElement root)
    {
        var index = 0;
        foreach (var button in Find(root, ControlType.Button))
        {
            var name = string.IsNullOrEmpty(button.Current.Name) ? "<无标题>" : button.Current.Name;
            Console.WriteLine($"[{index++}] {name}  enabled={button.Current.IsEnabled}");
        }

        return 0;
    }

    private static int ListItems(AutomationElement root, ControlType type)
    {
        foreach (var item in Find(root, type))
            Console.WriteLine($"{type.ProgrammaticName} '{item.Current.Name}' selected={IsSelected(item)}");

        return 0;
    }

    /// <summary>把自动化树按缩进打出来：排查「屏上有控件、--list 却找不到」时用。</summary>
    private static int Dump(AutomationElement root)
    {
        Walk(root, 0);
        return 0;

        static void Walk(AutomationElement element, int depth)
        {
            if (depth > 14)
                return;

            AutomationElementCollection children;
            try
            {
                children = element.FindAll(TreeScope.Children, Condition.TrueCondition);
            }
            catch (ElementNotAvailableException)
            {
                return;
            }

            foreach (AutomationElement child in children)
            {
                string name, type;
                System.Windows.Rect rect;
                try
                {
                    name = child.Current.Name;
                    type = child.Current.ControlType.ProgrammaticName;
                    rect = child.Current.BoundingRectangle;
                }
                catch (ElementNotAvailableException)
                {
                    continue;
                }

                Console.WriteLine($"{new string(' ', depth * 2)}{type} '{name}' {(int)rect.Width}x{(int)rect.Height} @{(int)rect.Left},{(int)rect.Top}");
                Walk(child, depth + 1);
            }
        }
    }

    private static bool IsSelected(AutomationElement element)
    {
        return element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)
               && ((SelectionItemPattern)pattern).Current.IsSelected;
    }

    private static int Click(AutomationElement root, string name)
    {
        foreach (var button in Find(root, ControlType.Button))
        {
            if (button.Current.Name != name)
                continue;

            if (!button.Current.IsEnabled)
            {
                Console.WriteLine($"按钮「{name}」当前不可用");
                return 1;
            }

            ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            // 刀路计算走引擎同步调用，界面刷新要留出余量
            Thread.Sleep(2500);
            Console.WriteLine($"已点击「{name}」");
            return 0;
        }

        Console.WriteLine($"未找到按钮「{name}」");
        return 1;
    }

    private static int Toggle(AutomationElement root, string name)
    {
        // ToggleButton 在 UI Automation 里报的是 Button 而不是 CheckBox，
        // 所以按名字找完后要用 TogglePattern 判断它是不是开关。
        var condition = new PropertyCondition(AutomationElement.NameProperty, name);
        foreach (AutomationElement element in root.FindAll(TreeScope.Descendants, condition))
        {
            if (!element.TryGetCurrentPattern(TogglePattern.Pattern, out var found))
                continue;

            var pattern = (TogglePattern)found;
            pattern.Toggle();
            Thread.Sleep(600);
            Console.WriteLine($"已切换「{name}」→ {pattern.Current.ToggleState}");
            return 0;
        }

        Console.WriteLine($"未找到开关「{name}」");
        return 1;
    }

    /// <summary>
    /// 按屏幕坐标点击：tap x,y。
    /// <para>
    /// TabControl 内容区不进自动化树（自绘样式下的 TabItem 没有任何子元素），
    /// 面板内按钮只能靠 --dump 打出来的坐标兜底，所以保留这条通道。
    /// </para>
    /// </summary>
    private static int Tap(string arg)
    {
        var parts = arg.Split(',');
        if (parts.Length != 2
            || !int.TryParse(parts[0].Trim(), out var x)
            || !int.TryParse(parts[1].Trim(), out var y))
        {
            Console.WriteLine("用法：tap <x,y>   （屏幕像素坐标，来自 --dump 输出）");
            return 2;
        }

        SetCursorPos(x, y);
        Thread.Sleep(120);
        mouse_event(MouseEventLeftDown, x, y, 0, 0);
        Thread.Sleep(60);
        mouse_event(MouseEventLeftUp, x, y, 0, 0);
        Thread.Sleep(600);
        Console.WriteLine($"已点击 ({x},{y})");
        return 0;
    }

    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);

    private static int Select(AutomationElement root, ControlType type, string name)
    {
        foreach (var item in Find(root, type))
        {
            if (item.Current.Name != name)
                continue;

            ((SelectionItemPattern)item.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            Thread.Sleep(800);
            Console.WriteLine($"已选中「{name}」");
            return 0;
        }

        Console.WriteLine($"未找到「{name}」，先用 --tree / --tabs 查看可用名称");
        return 1;
    }

    private static int Shot(AppWindow app, string? path)
    {
        // 默认落到 Test/Temp：截图属于验证产物，不该混进源码目录
        var target = path ?? Path.Combine(OutputDirectory, $"shot-{DateTime.Now:HHmmss}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);

        // 截图前等一帧，确保窗口重绘完成
        Thread.Sleep(500);
        using var bitmap = new Bitmap(app.Width, app.Height);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(new Point(app.Left, app.Top), Point.Empty, new Size(app.Width, app.Height));

        bitmap.Save(target, ImageFormat.Png);
        Console.WriteLine($"已保存 {Path.GetFullPath(target)} ({app.Width}x{app.Height})");
        return 0;
    }

    /// <summary>截图目录：从输出目录向上找到 Test 目录，再取其下的 Temp。</summary>
    private static string OutputDirectory
    {
        get
        {
            var directory = new DirectoryInfo(
                AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            while (directory != null && !directory.Name.Equals("Test", StringComparison.OrdinalIgnoreCase))
                directory = directory.Parent;

            var root = directory?.FullName ?? AppContext.BaseDirectory;
            return Path.Combine(root, "Temp");
        }
    }
}

/// <summary>目标窗口句柄与屏幕矩形；用完必须释放，否则句柄集合会泄漏。</summary>
internal sealed class AppWindow : IDisposable
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT value);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);

    private const int DwmExtendedFrameBounds = 9;
    private const int SwRestore = 9;
    private const uint SwpNoZOrder = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private AppWindow(IntPtr handle) => Handle = handle;

    /// <summary>由 --exe 指定的被测程序路径；为空时按输出目录逐级回溯</summary>
    public static string? StartupPath { get; set; }

    public IntPtr Handle { get; }

    public int Left { get; private set; }

    public int Top { get; private set; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public static AppWindow? Find(string processNamePrefix)
    {
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (!process.ProcessName.StartsWith(processNamePrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (process.MainWindowHandle == IntPtr.Zero)
                    continue;

                return new AppWindow(process.MainWindowHandle);
            }
            catch (InvalidOperationException)
            {
                // 进程在枚举过程中退出，跳过
            }
        }

        return null;
    }

    /// <summary>
    /// 启动被测程序并等它出主窗口。
    /// 从输出目录向上回溯找 exe，避免把路径写死到某一层输出目录。
    /// </summary>
    public static AppWindow? Launch(string processNamePrefix)
    {
        var exe = StartupPath ?? Locate($"{processNamePrefix}.Milling.exe");
        // 先按输出目录回溯，找不到再从 Test 目录的上一级找被测工程：AppContext 在影子加载下不一定可靠
        exe ??= LocateByProjectFolder(processNamePrefix);
        if (exe == null)
            return null;

        Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe) });

        for (var i = 0; i < 40; i++)
        {
            Thread.Sleep(250);
            var window = Find(processNamePrefix);
            if (window != null)
            {
                // 首帧布局与取景要等窗口真正可见
                Thread.Sleep(1500);
                return window;
            }
        }

        return null;
    }

    /// <summary>
    /// 自输出目录逐级向上找文件。
    /// AppContext.BaseDirectory 末尾带分隔符，直接取 Parent 会跳过一层，所以先 TrimEnd 再构造。
    /// </summary>
    private static string? Locate(string fileName)
    {
        var directory = new DirectoryInfo(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>兜底：从 Test 目录的上一级找被测工程的产品目录。</summary>
    private static string? LocateByProjectFolder(string processNamePrefix)
    {
        var directory = new DirectoryInfo(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        while (directory != null && !directory.Name.Equals("Test", StringComparison.OrdinalIgnoreCase))
            directory = directory.Parent;

        if (directory?.Parent == null)
            return null;

        var product = Path.Combine(directory.Parent.FullName, $"{processNamePrefix}.Milling", "bin", "Debug", "net8.0-windows");
        var exe = Path.Combine(product, $"{processNamePrefix}.Milling.exe");
        return File.Exists(exe) ? exe : null;
    }

    public void BringToFront()
    {
        ShowWindow(Handle, SwRestore);
        // 不收 Z 序，避免全屏压住用户正在看的其它窗口
        SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoZOrder);
        SetForegroundWindow(Handle);
        Thread.Sleep(600);

        // DWM 的扩展边界是物理像素；GetWindowRect 在 DPI 虚拟化下会给出逻辑值，
        // 与 CopyFromScreen 使用的屏幕坐标不一致，截图会错位。
        if (DwmGetWindowAttribute(Handle, DwmExtendedFrameBounds, out var rect, Marshal.SizeOf<RECT>()) != 0)
            GetWindowRect(Handle, out rect);

        Left = rect.Left;
        Top = rect.Top;
        Width = rect.Right - rect.Left;
        Height = rect.Bottom - rect.Top;
    }

    public void Dispose()
    {
    }
}
