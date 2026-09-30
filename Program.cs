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
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(args.Length > 0 ? args[0] : null));
        }
    }
}
