using System.Windows;
using System.Windows.Media;

namespace ADConsole.Dialogs
{
    /// <summary>对话框通用 UI 帮助方法</summary>
    internal static class UiHelper
    {
        /// <summary>
        /// 从应用资源里取程序图标赋给窗口；资源缺失时静默忽略（返回 false），
        /// 保证图标加载失败不会影响对话框打开。
        /// </summary>
        public static bool TrySetAppIcon(Window window)
        {
            if (window == null) return false;
            try
            {
                object res = Application.Current == null
                    ? null
                    : Application.Current.TryFindResource("AppIcon");
                ImageSource icon = res as ImageSource;
                if (icon != null)
                {
                    window.Icon = icon;
                    return true;
                }
            }
            catch
            {
                // 图标加载失败不影响功能
            }
            return false;
        }
    }
}
