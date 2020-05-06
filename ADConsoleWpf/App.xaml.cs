using System;
using System.Windows;
using ADConsole.Services;

namespace ADConsole
{
    /// <summary>
    /// 应用启动逻辑：
    ///   1) 读取本机保存的凭据（DPAPI 加密）
    ///   2) 若勾选了"自动登录"且凭据校验通过，直接进入主窗口
    ///   3) 否则显示登录框
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 先用显式关闭模式，避免登录框关闭时整个应用提前退出
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            StoredCredential saved = CredentialStore.Load();
            string autoLoginError = null;

            if (saved != null && saved.AutoLogin && !string.IsNullOrEmpty(saved.Password))
            {
                try
                {
                    if (PermissionService.ValidateCredential(saved.Domain, saved.UserName, saved.Password))
                    {
                        ShowMainWindow(saved);
                        return;
                    }
                    autoLoginError = "自动登录失败：保存的用户名或密码已失效，请重新输入。";
                    // 失败时不沿用失效密码，避免反复失败
                    saved.Password = string.Empty;
                }
                catch (Exception ex)
                {
                    autoLoginError = "自动登录失败：" + ex.Message;
                    saved.Password = string.Empty;
                }
            }

            LoginWindow login = new LoginWindow(saved, autoLoginError);
            bool? ok = login.ShowDialog();

            if (ok == true)
            {
                ShowMainWindow(login.ResultCredential);
            }
            else
            {
                Shutdown();
            }
        }

        private void ShowMainWindow(StoredCredential cred)
        {
            MainWindow window = new MainWindow(cred);
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
        }
    }
}
