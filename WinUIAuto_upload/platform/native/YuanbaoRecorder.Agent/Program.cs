using System;
using System.Threading;
using System.Windows.Forms;

namespace YuanbaoRecorder.Agent
{
#if !SMOKE_TEST
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            NativeMethods.InitializeDpiAwareness();
            bool ownsMutex;
            using (var mutex = new Mutex(true, "Local\\CacheAgent.YuanbaoRecorder.Agent", out ownsMutex))
            {
                if (!ownsMutex)
                {
                    MessageBox.Show("元宝录制 Agent 已在运行，请勿重复启动。", "元宝录制 Agent");
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new RecorderForm());
            }
        }
    }
#endif
}
