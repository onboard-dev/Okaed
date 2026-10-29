using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Okaed
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            try { if (Environment.OSVersion.Version.Major >= 6) SetProcessDPIAware(); } catch { }
#if NETCOREAPP
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
#endif
            // 単一インスタンス: すでにウィンドウがあれば、ファイルをそちらのタブで開いて終わる
            bool newWindow = args.Length > 0 && string.Equals(args[0], SingleInstance.NewWindowArg, StringComparison.OrdinalIgnoreCase);
            if (newWindow)
            {
                string[] rest = new string[args.Length - 1];
                Array.Copy(args, 1, rest, 0, rest.Length);
                args = rest;
            }
            else if (SingleInstance.TryHandOff(args))
            {
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(args));
        }
    }
}
