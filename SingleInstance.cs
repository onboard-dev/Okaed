using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Okaed
{
    /// <summary>
    /// 単一インスタンス化。すでに Okaed のウィンドウがあれば、開くファイルをそのウィンドウへ
    /// WM_COPYDATA で渡して、このプロセスは何も表示せずに終わる。
    /// ウィンドウの目印は SetProp で付けたプロパティ(ウィンドウのクラス名やタイトルに頼らない)。
    /// Explorer で複数ファイルを選んで開くと同時にいくつも起動するので、名前付き Mutex で順番待ちさせる。
    /// </summary>
    static class SingleInstance
    {
        const string PropName = "Okaed.MainWindow";
        const int WM_COPYDATA = 0x004A;
        const int CopyDataId = 0x4F4B4144;           // 'OKAD'
        const uint SMTO_ABORTIFHUNG = 0x0002;
        const int SW_RESTORE = 9;

        /// <summary>この引数で起動すると、既存ウィンドウを探さず新しいウィンドウを開く</summary>
        public const string NewWindowArg = "/new";

        [StructLayout(LayoutKind.Sequential)]
        struct COPYDATASTRUCT
        {
            public IntPtr dwData;
            public int cbData;
            public IntPtr lpData;
        }

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetProp(IntPtr hWnd, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool SetProp(IntPtr hWnd, string name, IntPtr data);
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")]
        static extern bool AllowSetForegroundWindow(uint pid);
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")]
        static extern bool IsWindowEnabled(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, ref COPYDATASTRUCT lParam,
            uint flags, uint timeoutMs, out IntPtr result);

        static Mutex startupMutex;
        static bool registered;

        /// <summary>
        /// 既存ウィンドウへファイルを渡せたら true(このプロセスは終了してよい)。
        /// false なら自分がウィンドウを開く。その場合、ウィンドウを表示し終わるまで Mutex を持ち続ける
        /// (同時に起動したほかのプロセスは、そのあとでこちらのウィンドウを見つける)。
        /// </summary>
        public static bool TryHandOff(string[] args)
        {
            try
            {
                bool createdNew;
                startupMutex = new Mutex(true, "Local\\Okaed.SingleInstance." + Environment.UserName, out createdNew);
                if (!createdNew)
                {
                    try { startupMutex.WaitOne(10000); }
                    catch (AbandonedMutexException) { }     // 前のプロセスが異常終了していても、所有権は得られている
                }
            }
            catch { startupMutex = null; }

            IntPtr hwnd = FindExisting();
            if (hwnd == IntPtr.Zero) return false;

            // 相対パスは、このプロセスのカレントフォルダ基準でフルパスにしてから渡す
            StringBuilder sb = new StringBuilder();
            if (args != null)
                foreach (string a in args)
                {
                    if (string.IsNullOrEmpty(a)) continue;
                    string full;
                    try { full = Path.GetFullPath(a); } catch { continue; }
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(full);
                }

            // 受け手(別プロセス)がウィンドウを前面に出せるように許可しておく
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            try { AllowSetForegroundWindow(pid); } catch { }

            if (!Send(hwnd, sb.ToString())) return false;   // 応答がなければ自分で開く

            try
            {
                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
            }
            catch { }
            ReleaseMutex();
            return true;
        }

        /// <summary>起動時のファイルを開き終えたら呼ぶ。目印を付けて、待っているプロセスを通す
        /// (このあと自動保存の復元確認などを表示中に届いたファイルは、閉じられてから開く)</summary>
        public static void Register(IntPtr hwnd)
        {
            registered = true;
            Mark(hwnd);
            ReleaseMutex();
        }

        /// <summary>ハンドルが作り直されたとき(タスクトレイ格納などで起こる)に目印を付け直す</summary>
        public static void OnHandleCreated(IntPtr hwnd)
        {
            if (registered) Mark(hwnd);
        }

        /// <summary>自分以外の Okaed のウィンドウがあるか(自動保存の復元を、ほかのウィンドウの作業中ファイルで出さないため)</summary>
        public static bool OtherWindowExists()
        {
            return FindExisting() != IntPtr.Zero;
        }

        /// <summary>ウィンドウが入力を受け付ける状態か(メッセージボックスなどモーダル表示中は false)</summary>
        public static bool IsEnabled(IntPtr hwnd)
        {
            try { return IsWindowEnabled(hwnd); } catch { return true; }
        }

        /// <summary>WM_COPYDATA なら中身(改行区切りのパス)を取り出して true</summary>
        public static bool TryRead(ref Message m, out string[] paths)
        {
            paths = null;
            if (m.Msg != WM_COPYDATA || m.LParam == IntPtr.Zero) return false;
            COPYDATASTRUCT cds = (COPYDATASTRUCT)Marshal.PtrToStructure(m.LParam, typeof(COPYDATASTRUCT));
            if (cds.dwData.ToInt64() != CopyDataId) return false;
            string s = cds.cbData > 0 && cds.lpData != IntPtr.Zero ? Marshal.PtrToStringUni(cds.lpData, cds.cbData / 2) : "";
            List<string> list = new List<string>();
            foreach (string p in s.Split('\n'))
                if (p.Length > 0) list.Add(p);
            paths = list.ToArray();
            m.Result = new IntPtr(1);
            return true;
        }

        // ---------------------------------------------------------------

        static void Mark(IntPtr hwnd)
        {
            try { SetProp(hwnd, PropName, new IntPtr(1)); } catch { }
        }

        static void ReleaseMutex()
        {
            if (startupMutex == null) return;
            try { startupMutex.ReleaseMutex(); } catch { }
            try { startupMutex.Close(); } catch { }
            startupMutex = null;
        }

        /// <summary>目印の付いたウィンドウを探す(EnumWindows は手前のウィンドウから順に返す)</summary>
        static IntPtr FindExisting()
        {
            IntPtr found = IntPtr.Zero;
            uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            try
            {
                EnumWindows(delegate (IntPtr h, IntPtr l)
                {
                    if (GetProp(h, PropName) == IntPtr.Zero) return true;
                    uint pid;
                    GetWindowThreadProcessId(h, out pid);
                    if (pid == myPid) return true;
                    found = h;
                    return false;
                }, IntPtr.Zero);
            }
            catch { }
            return found;
        }

        static bool Send(IntPtr hwnd, string text)
        {
            IntPtr buf = Marshal.StringToHGlobalUni(text);
            try
            {
                COPYDATASTRUCT cds = new COPYDATASTRUCT();
                cds.dwData = new IntPtr(CopyDataId);
                cds.cbData = text.Length * 2;
                cds.lpData = buf;
                IntPtr result;
                IntPtr ok = SendMessageTimeout(hwnd, WM_COPYDATA, IntPtr.Zero, ref cds, SMTO_ABORTIFHUNG, 5000, out result);
                return ok != IntPtr.Zero && result.ToInt64() == 1;
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }

    public partial class MainForm
    {
        readonly List<string> remotePaths = new List<string>();
        System.Windows.Forms.Timer tmrRemote;

        /// <summary>ほかの Okaed から渡されたファイルを受け取る(WndProc から呼ぶ)。処理したら true</summary>
        bool HandleRemoteOpen(ref Message m)
        {
            string[] paths;
            if (!SingleInstance.TryRead(ref m, out paths)) return false;
            remotePaths.AddRange(paths);
            // 送り手を待たせないよう、開く処理はメッセージを返してから行う
            BeginInvoke(new MethodInvoker(ProcessRemoteOpen));
            return true;
        }

        void ProcessRemoteOpen()
        {
            if (IsDisposed) return;
            // メッセージボックスなどを表示中なら、閉じられるまで待つ
            if (!SingleInstance.IsEnabled(Handle))
            {
                if (tmrRemote == null)
                {
                    tmrRemote = new System.Windows.Forms.Timer();
                    tmrRemote.Interval = 300;
                    tmrRemote.Tick += delegate { tmrRemote.Stop(); ProcessRemoteOpen(); };
                }
                tmrRemote.Start();
                return;
            }
            string[] paths = remotePaths.ToArray();
            remotePaths.Clear();
            RestoreFromTray();
            foreach (string p in paths)
                if (File.Exists(p)) OpenFile(p);
        }

        /// <summary>ファイルメニュー「新しいウィンドウ」: 既存ウィンドウに合流しない Okaed を起動する</summary>
        void OpenNewWindow()
        {
            try { System.Diagnostics.Process.Start(Application.ExecutablePath, SingleInstance.NewWindowArg); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "新しいウィンドウを開けませんでした。\n" + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
