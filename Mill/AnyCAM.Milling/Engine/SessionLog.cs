using System;
using System.IO;
using System.Threading;

namespace AnyCAM.Milling.Engine
{
    /// <summary>
    /// 应用会话日志（与 AnyCAD 的 anycad.log 互补）。
    /// 托管异常写 anycam-app.log / anycam-crash.log；原生闪退时请看 Execute 前最后一条 LastAction。
    /// </summary>
    internal static class SessionLog
    {
        private static readonly object Gate = new();

        private static string AppLogPath => System.IO.Path.Combine(AppContext.BaseDirectory, "anycam-app.log");
        private static string CrashLogPath => System.IO.Path.Combine(AppContext.BaseDirectory, "anycam-crash.log");

        /// <summary>当前正在执行的操作，便于对照闪退前最后一步。</summary>
        public static string? LastAction { get; private set; }

        public static void Info(string message) => Write("INFO", message);

        public static void Warn(string message) => Write("WARN", message);

        public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

        public static void Crash(string kind, Exception? ex = null)
        {
            Write("CRASH", kind, ex, toCrashFile: true);
            Write("CRASH", kind, ex, toCrashFile: false);
        }

        public static void SetAction(string action)
        {
            LastAction = action;
            Info("Action: " + action);
        }

        public static void ClearAction() => LastAction = null;

        private static void Write(string level, string message, Exception? ex = null, bool toCrashFile = false)
        {
            try
            {
                lock (Gate)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ");
                    sb.Append('[').Append(level).Append("] ");
                    sb.Append(message);
                    if (!string.IsNullOrEmpty(LastAction) && level is "ERROR" or "CRASH" or "WARN")
                        sb.Append(" | LastAction=").Append(LastAction);
                    sb.AppendLine();
                    if (ex != null)
                    {
                        sb.AppendLine(ex.ToString());
                        if (ex.StackTrace != null)
                            sb.AppendLine(ex.StackTrace);
                    }

                    sb.AppendLine("---");
                    var text = sb.ToString();
                    File.AppendAllText(toCrashFile ? CrashLogPath : AppLogPath, text);
                }
            }
            catch
            {
                // 日志失败不能拖垮主流程
            }
        }
    }
}
