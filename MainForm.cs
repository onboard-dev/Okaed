using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Okaed
{
    public class MainForm : Form
    {
        const string AppName = "Okaed";
        static string AppTitleWithVersion
        {
            get { return AppName + " Version " + AppVersion.Value; }
        }
        static readonly Color HighlightColor = Color.FromArgb(255, 235, 110);

        [DllImport("user32.dll")]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        const int WM_HOTKEY = 0x0312;
        const uint MOD_CONTROL = 0x0002;
        const uint MOD_SHIFT = 0x0004;
        const int HOTKEY_ID = 0xB001;
        const uint VK_O = 0x4F;
        bool hotkeyRegistered;

        int defaultWinW = 800, defaultWinH = 600; // トリプルクリックで戻すサイズ(論理ピクセル)

        readonly Settings settings = new Settings();
        float scale = 1f;

        EditorBox editor;
        SplitContainer split;
        WebBrowser web;
        ToolStrip tool;
        MenuStrip menu;
        StatusStrip status;

        ToolStripTextBox txtFind, txtReplace;
        ToolStripButton btnPrev, btnNext, btnRegex, btnReplace, btnReplaceAll, btnWrap;
        ToolStripButton btnDate, btnTime, btnInvisibles, btnHalf, btnFull;
        ToolStripComboBox cmbFont, cmbSize;
        ToolStripStatusLabel lblPos, lblMsg, lblCount;
        ToolStripDropDownButton ddEnc, ddEol;
        ToolStripMenuItem miPreview, miEncSjis, miEncUtf8, miEolCrlf, miEolLf;
        ToolStripMenuItem miNumColorOn, miLetterColorOn, miSymbolColorOn;
        ToolStripMenuItem mRecent;

        readonly Timer tmrMatch = new Timer();
        readonly Timer tmrPreview = new Timer();
        readonly Timer tmrPoll = new Timer();
        readonly Timer tmrAutosave = new Timer();

        NotifyIcon trayIcon;

        // ---- 数値/英字/記号の自動着色 ----
        bool colorNumbersOn = true;
        bool colorLettersOn = true;
        bool symbolColorOn = true;
        Color numberColor = Color.FromArgb(0, 90, 200);
        Color letterColor = Color.FromArgb(0, 130, 90);

        // ---- テーマ(ライト/ダーク、それぞれ背景・文字・数値・英字・記号の色をカスタマイズ可能) ----
        class ThemeColors
        {
            public Color Back, Fore, Number, Letter, Invisible;
            public ThemeColors(Color back, Color fore, Color number, Color letter, Color invisible)
            { Back = back; Fore = fore; Number = number; Letter = letter; Invisible = invisible; }
        }
        // VSCode 既定配色を参考にしたライト/ダークのデフォルト値
        ThemeColors lightTheme = new ThemeColors(
            Color.FromArgb(255, 255, 255), Color.FromArgb(0, 0, 0),
            Color.FromArgb(9, 134, 88), Color.FromArgb(0, 16, 128), Color.FromArgb(70, 140, 210));
        ThemeColors darkTheme = new ThemeColors(
            Color.FromArgb(30, 30, 30), Color.FromArgb(212, 212, 212),
            Color.FromArgb(181, 206, 168), Color.FromArgb(156, 220, 254), Color.FromArgb(160, 160, 170));
        bool darkMode = false;
        ThemeColors CurTheme { get { return darkMode ? darkTheme : lightTheme; } }
        ToolStripMenuItem miThemeLight, miThemeDark;

        // ---- ファイル履歴(最近使ったファイル、最大30件) ----
        readonly List<string> mru = new List<string>();
        const int MruMax = 30;

        // ---- 自動保存(テンポラリフォルダへの複製のみ。保存先の実ファイルには触れない) ----
        string autosavePath;
        string autosaveLastText;
        static readonly string AutosaveDir = Path.Combine(Path.GetTempPath(), "Okaed", "autosave");

        string filePath;
        FileEnc fileEnc = FileEnc.Utf8Bom;
        string eol = "\r\n";
        bool metaDirty;          // 文字コード/改行の変更
        bool lastDirtyShown = true;

        readonly List<int> hlStarts = new List<int>();
        readonly List<int> hlLens = new List<int>();
        bool needPaint;
        EditorBox.POINT lastScroll;
        Size lastClient;
        string regexKey;
        Regex regexCache;

        string previewFile;
        string previewBaseDir;
        bool webReady;
        string lastHtml;
        double lastRatio = -1;

        readonly HashSet<string> fontNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public MainForm(string openPath)
        {
            using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f;
            Text = AppTitleWithVersion;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { Icon = SystemIcons.Application; }
            KeyPreview = true;
            BuildUi();
            InitTray();
            LoadSettings();

            tmrMatch.Interval = 200;
            tmrMatch.Tick += delegate { tmrMatch.Stop(); RecomputeMatches(); };
            tmrPreview.Interval = 350;
            tmrPreview.Tick += delegate { tmrPreview.Stop(); UpdatePreviewNow(); };
            tmrPoll.Interval = 80;
            tmrPoll.Tick += delegate { Poll(); };
            tmrPoll.Start();
            tmrAutosave.Interval = 20000;
            tmrAutosave.Tick += delegate { WriteAutosave(); };
            tmrAutosave.Start();

            if (!string.IsNullOrEmpty(openPath) && File.Exists(openPath)) OpenFile(openPath);
            else NewFile();

            OfferAutosaveRecovery();
        }

        int S(int px) { return (int)Math.Round(px * scale); }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { hotkeyRegistered = RegisterHotKey(Handle, HOTKEY_ID, MOD_CONTROL | MOD_SHIFT, VK_O); }
            catch { hotkeyRegistered = false; }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
            {
                RestoreFromTray();
                return;
            }
            base.WndProc(ref m);
        }

        // ======================= UI 構築 =======================

        void BuildUi()
        {
            // ---- エディタとプレビュー ----
            editor = new EditorBox();
            editor.Dock = DockStyle.Fill;
            editor.AllowDrop = true;
            editor.TextChanged += delegate { OnEditorTextChanged(); };
            editor.SelectionChanged += delegate { UpdatePos(); };
            editor.DragEnter += OnDragEnter;
            editor.DragDrop += OnDragDrop;
            editor.ContextMenuStrip = BuildContextMenu();
            editor.TripleClick += delegate { ResetWindowToDefaultSize(); };

            split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Vertical;
            split.Panel2Collapsed = true;
            split.SplitterWidth = S(5);
            split.Panel1.Controls.Add(editor);
            split.SplitterMoved += delegate { if (!split.Panel2Collapsed) settings.Set("SplitRatio", (float)split.SplitterDistance / Math.Max(1, split.Width)); };

            // ---- メニュー(ファイル操作など最小限)----
            menu = new MenuStrip();
            ToolStripMenuItem mFile = new ToolStripMenuItem("ファイル(&F)");
            mFile.DropDownItems.Add(MI("新規(&N)", Keys.Control | Keys.N, delegate { if (ConfirmDiscard()) NewFile(); }));
            mFile.DropDownItems.Add(MI("開く(&O)...", Keys.Control | Keys.O, delegate { OpenDialog(); }));
            mFile.DropDownItems.Add(MI("上書き保存(&S)", Keys.Control | Keys.S, delegate { Save(); }));
            mFile.DropDownItems.Add(MI("名前を付けて保存(&A)...", Keys.Control | Keys.Shift | Keys.S, delegate { SaveAs(); }));
            mFile.DropDownItems.Add(new ToolStripSeparator());
            mRecent = new ToolStripMenuItem("最近使ったファイル(&R)");
            mFile.DropDownItems.Add(mRecent);
            mFile.DropDownItems.Add(new ToolStripSeparator());
            mFile.DropDownItems.Add(MI("終了(&X)", Keys.None, delegate { Close(); }));

            ToolStripMenuItem mEdit = new ToolStripMenuItem("編集(&E)");
            ToolStripMenuItem miUndo = MI("元に戻す(&U)", Keys.None, delegate { editor.DoUndo(); });
            miUndo.ShortcutKeyDisplayString = "Ctrl+Z";
            ToolStripMenuItem miRedo = MI("やり直し(&R)", Keys.None, delegate { editor.DoRedo(); });
            miRedo.ShortcutKeyDisplayString = "Ctrl+Y";
            mEdit.DropDownItems.Add(miUndo);
            mEdit.DropDownItems.Add(miRedo);
            mEdit.DropDownItems.Add(new ToolStripSeparator());
            mEdit.DropDownItems.Add(MI("検索(&F)", Keys.Control | Keys.F, delegate { FocusFind(); }));
            mEdit.DropDownItems.Add(MI("置換(&H)", Keys.Control | Keys.H, delegate { FocusReplace(); }));
            mEdit.DropDownItems.Add(MI("次を検索(&N)", Keys.F3, delegate { Find(true); }));
            mEdit.DropDownItems.Add(MI("前を検索(&P)", Keys.Shift | Keys.F3, delegate { Find(false); }));
            mEdit.DropDownOpening += delegate { miUndo.Enabled = editor.CanUndoEdit; miRedo.Enabled = editor.CanRedoEdit; };

            ToolStripMenuItem mView = new ToolStripMenuItem("表示(&V)");
            miPreview = MI("Markdown プレビュー(&P)", Keys.F12, delegate { SetPreview(!miPreview.Checked); });
            mView.DropDownItems.Add(miPreview);
            mView.DropDownItems.Add(new ToolStripSeparator());

            mView.DropDownItems.Add(new ToolStripMenuItem("トリプルクリックで現在のサイズに変更する", null, delegate { SetTripleClickSizeToCurrent(); }));
            mView.DropDownItems.Add(new ToolStripSeparator());

            ToolStripMenuItem mTheme = new ToolStripMenuItem("テーマ");
            miThemeLight = new ToolStripMenuItem("ライト", null, delegate { SetDarkMode(false); });
            miThemeDark = new ToolStripMenuItem("ダーク", null, delegate { SetDarkMode(true); });
            mTheme.DropDownItems.Add(miThemeLight);
            mTheme.DropDownItems.Add(miThemeDark);
            mView.DropDownItems.Add(mTheme);
            mView.DropDownItems.Add(new ToolStripSeparator());

            ToolStripMenuItem mNumColor = new ToolStripMenuItem("数値の色");
            miNumColorOn = new ToolStripMenuItem("テーマの色を使う", null, delegate { colorNumbersOn = !colorNumbersOn; miNumColorOn.Checked = colorNumbersOn; needPaint = true; SaveSettings(); });
            ToolStripMenuItem miNumColorPick = new ToolStripMenuItem("色を選択...", null, delegate { Color c; if (PickColor(CurTheme.Number, out c)) { CurTheme.Number = c; ApplyTheme(); SaveSettings(); } });
            mNumColor.DropDownItems.Add(miNumColorOn);
            mNumColor.DropDownItems.Add(miNumColorPick);
            mView.DropDownItems.Add(mNumColor);

            ToolStripMenuItem mLetterColor = new ToolStripMenuItem("英字の色");
            miLetterColorOn = new ToolStripMenuItem("テーマの色を使う", null, delegate { colorLettersOn = !colorLettersOn; miLetterColorOn.Checked = colorLettersOn; needPaint = true; SaveSettings(); });
            ToolStripMenuItem miLetterColorPick = new ToolStripMenuItem("色を選択...", null, delegate { Color c; if (PickColor(CurTheme.Letter, out c)) { CurTheme.Letter = c; ApplyTheme(); SaveSettings(); } });
            mLetterColor.DropDownItems.Add(miLetterColorOn);
            mLetterColor.DropDownItems.Add(miLetterColorPick);
            mView.DropDownItems.Add(mLetterColor);

            ToolStripMenuItem mSymbolColor = new ToolStripMenuItem("記号の色(空白可視化)");
            miSymbolColorOn = new ToolStripMenuItem("テーマの色を使う", null, delegate { symbolColorOn = !symbolColorOn; miSymbolColorOn.Checked = symbolColorOn; ApplyTheme(); SaveSettings(); });
            ToolStripMenuItem miSymbolColorPick = new ToolStripMenuItem("色を選択...", null, delegate { Color c; if (PickColor(CurTheme.Invisible, out c)) { CurTheme.Invisible = c; ApplyTheme(); SaveSettings(); } });
            mSymbolColor.DropDownItems.Add(miSymbolColorOn);
            mSymbolColor.DropDownItems.Add(miSymbolColorPick);
            mView.DropDownItems.Add(mSymbolColor);

            menu.Items.Add(mFile);
            menu.Items.Add(mEdit);
            menu.Items.Add(mView);

            // ---- ツールバー:検索 / 置換 / 折り返し / フォント ----
            tool = new ToolStrip();
            tool.GripStyle = ToolStripGripStyle.Hidden;
            tool.Padding = new Padding(S(4), S(2), S(4), S(2));
            tool.CanOverflow = true;

            txtFind = new ToolStripTextBox();
            txtFind.AutoSize = false;
            txtFind.Width = S(95);
            txtFind.ToolTipText = "検索(Enter: 次 / Shift+Enter: 前 / Esc: 本文へ)";
            txtFind.TextChanged += delegate { regexKey = null; tmrMatch.Stop(); tmrMatch.Start(); };
            txtFind.KeyDown += OnFindKeyDown;

            btnPrev = TB("▲", "前を検索 (Shift+F3)", delegate { Find(false); });
            btnNext = TB("▼", "次を検索 (F3)", delegate { Find(true); });
            btnRegex = TB(".*", "正規表現", delegate { regexKey = null; RecomputeMatches(); });
            btnRegex.CheckOnClick = true;

            txtReplace = new ToolStripTextBox();
            txtReplace.AutoSize = false;
            txtReplace.Width = S(75);
            txtReplace.ToolTipText = "置換後の文字列(正規表現時は $1 や \\n \\t が使えます / Enter: 置換)";
            txtReplace.KeyDown += OnReplaceKeyDown;
            btnReplace = TB("▶", "置換", delegate { ReplaceOne(); });
            btnReplaceAll = TB("⏩", "すべて置換(Ctrl+Z で一括で戻せます)", delegate { ReplaceAll(); });

            btnWrap = TB("↩", "行の折り返し", delegate { SetWrap(btnWrap.Checked); });
            btnWrap.CheckOnClick = true;

            btnDate = TB("📅", "現在の日付を挿入 (yyMMdd)", delegate { InsertDate(); });
            btnTime = TB("🕒", "現在の時刻を挿入 (HH:mm)", delegate { InsertTime(); });

            btnInvisibles = TB("空白可視化", "半角/全角スペース・タブ・改行を可視化", delegate { SetInvisibles(btnInvisibles.Checked); });
            btnInvisibles.CheckOnClick = true;

            btnHalf = TB("半角化", "選択範囲を半角に変換(全角数字も半角化)", delegate { ConvertWidth(false); });
            btnFull = TB("全角化", "選択範囲を全角に変換(半角数字も全角化)", delegate { ConvertWidth(true); });

            cmbFont = new ToolStripComboBox();
            cmbFont.AutoSize = false;
            cmbFont.Width = S(113);
            cmbFont.DropDownStyle = ComboBoxStyle.DropDown;
            cmbFont.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            cmbFont.AutoCompleteSource = AutoCompleteSource.ListItems;
            cmbFont.ToolTipText = "フォント";
            cmbFont.MaxDropDownItems = 20;
            using (InstalledFontCollection fc = new InstalledFontCollection())
            {
                List<string> jp = new List<string>(), other = new List<string>();
                foreach (FontFamily ff in fc.Families)
                {
                    if (ff.Name.StartsWith("@")) continue;
                    fontNames.Add(ff.Name);
                    (IsJapaneseName(ff.Name) ? jp : other).Add(ff.Name);
                }
                foreach (string n in jp) cmbFont.Items.Add(n);
                foreach (string n in other) cmbFont.Items.Add(n);
            }
            cmbFont.SelectedIndexChanged += delegate { ApplyFont(); };
            cmbFont.KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ApplyFont(); editor.Focus(); } };
            cmbFont.Leave += delegate { ApplyFont(); };

            cmbSize = new ToolStripComboBox();
            cmbSize.AutoSize = false;
            cmbSize.Width = S(55);
            cmbSize.DropDownStyle = ComboBoxStyle.DropDown;
            cmbSize.ToolTipText = "文字サイズ";
            foreach (int sz in new int[] { 8, 9, 10, 10, 11, 12, 13, 14, 16, 18, 20, 24, 28, 32, 36, 48 })
                if (!cmbSize.Items.Contains(sz.ToString())) cmbSize.Items.Add(sz.ToString());
            cmbSize.SelectedIndexChanged += delegate { ApplyFont(); };
            cmbSize.KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ApplyFont(); editor.Focus(); } };
            cmbSize.Leave += delegate { ApplyFont(); };

            tool.Items.Add(new ToolStripLabel("検索"));
            tool.Items.Add(txtFind);
            tool.Items.Add(btnPrev);
            tool.Items.Add(btnNext);
            tool.Items.Add(btnRegex);
            tool.Items.Add(new ToolStripSeparator());
            tool.Items.Add(new ToolStripLabel("置換"));
            tool.Items.Add(txtReplace);
            tool.Items.Add(btnReplace);
            tool.Items.Add(btnReplaceAll);
            tool.Items.Add(new ToolStripSeparator());
            tool.Items.Add(btnWrap);
            tool.Items.Add(btnInvisibles);
            tool.Items.Add(new ToolStripSeparator());
            tool.Items.Add(btnDate);
            tool.Items.Add(btnTime);
            tool.Items.Add(new ToolStripSeparator());
            tool.Items.Add(btnHalf);
            tool.Items.Add(btnFull);
            tool.Items.Add(new ToolStripSeparator());
            tool.Items.Add(cmbFont);
            tool.Items.Add(cmbSize);

            // ---- ステータスバー ----
            status = new StatusStrip();
            lblPos = new ToolStripStatusLabel("1 行, 1 桁");
            lblCount = new ToolStripStatusLabel("");
            lblMsg = new ToolStripStatusLabel("");
            lblMsg.Spring = true;
            lblMsg.TextAlign = ContentAlignment.MiddleLeft;
            ddEnc = new ToolStripDropDownButton("UTF-8 (BOM)");
            ddEnc.ToolTipText = "保存時の文字コード";
            miEncSjis = new ToolStripMenuItem("Shift_JIS", null, delegate { SetEncoding(FileEnc.Sjis); });
            miEncUtf8 = new ToolStripMenuItem("UTF-8 (BOM)", null, delegate { SetEncoding(FileEnc.Utf8Bom); });
            ddEnc.DropDownItems.Add(miEncSjis);
            ddEnc.DropDownItems.Add(miEncUtf8);
            ddEol = new ToolStripDropDownButton("CRLF");
            ddEol.ToolTipText = "保存時の改行コード";
            miEolCrlf = new ToolStripMenuItem("CRLF (Windows)", null, delegate { SetEol("\r\n"); });
            miEolLf = new ToolStripMenuItem("LF (Unix)", null, delegate { SetEol("\n"); });
            ddEol.DropDownItems.Add(miEolCrlf);
            ddEol.DropDownItems.Add(miEolLf);
            status.Items.Add(lblMsg);
            status.Items.Add(lblCount);
            status.Items.Add(lblPos);
            status.Items.Add(ddEol);
            status.Items.Add(ddEnc);

            // Dock は後に追加したものが外側
            Controls.Add(split);
            Controls.Add(status);
            Controls.Add(tool);
            Controls.Add(menu);
            MainMenuStrip = menu;

            AllowDrop = true;
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
        }

        static bool IsJapaneseName(string n)
        {
            foreach (char c in n) if (c >= 0x3000) return true;
            return n.StartsWith("MS ") || n.StartsWith("BIZ ") || n.StartsWith("Yu ") || n.StartsWith("Meiryo") || n.StartsWith("UD ");
        }

        ToolStripMenuItem MI(string text, Keys keys, EventHandler h)
        {
            ToolStripMenuItem m = new ToolStripMenuItem(text, null, h);
            if (keys != Keys.None) m.ShortcutKeys = keys;
            return m;
        }

        ToolStripButton TB(string text, string tip, EventHandler h)
        {
            ToolStripButton b = new ToolStripButton(text, null, h);
            b.DisplayStyle = ToolStripItemDisplayStyle.Text;
            b.ToolTipText = tip;
            return b;
        }

        ContextMenuStrip BuildContextMenu()
        {
            ContextMenuStrip cm = new ContextMenuStrip();
            ToolStripMenuItem u = new ToolStripMenuItem("元に戻す", null, delegate { editor.DoUndo(); });
            ToolStripMenuItem r = new ToolStripMenuItem("やり直し", null, delegate { editor.DoRedo(); });
            ToolStripMenuItem x = new ToolStripMenuItem("切り取り", null, delegate { editor.CutPlain(); });
            ToolStripMenuItem c = new ToolStripMenuItem("コピー", null, delegate { editor.CopyPlain(); });
            ToolStripMenuItem v = new ToolStripMenuItem("貼り付け", null, delegate { editor.PastePlain(); });
            ToolStripMenuItem d = new ToolStripMenuItem("削除", null, delegate { if (editor.SelectionLength > 0) editor.ReplaceRange(editor.SelectionStart, editor.SelectionLength, ""); });
            ToolStripMenuItem a = new ToolStripMenuItem("すべて選択", null, delegate { editor.SelectAll(); });
            u.ShortcutKeyDisplayString = "Ctrl+Z"; r.ShortcutKeyDisplayString = "Ctrl+Y";
            x.ShortcutKeyDisplayString = "Ctrl+X"; c.ShortcutKeyDisplayString = "Ctrl+C";
            v.ShortcutKeyDisplayString = "Ctrl+V"; a.ShortcutKeyDisplayString = "Ctrl+A";
            cm.Items.AddRange(new ToolStripItem[] { u, r, new ToolStripSeparator(), x, c, v, d, new ToolStripSeparator(), a });
            cm.Opening += delegate
            {
                bool sel = editor.SelectionLength > 0;
                u.Enabled = editor.CanUndoEdit; r.Enabled = editor.CanRedoEdit;
                x.Enabled = sel; c.Enabled = sel; d.Enabled = sel;
            };
            return cm;
        }

        // ======================= トレイ / 最小化 =======================

        void InitTray()
        {
            trayIcon = new NotifyIcon();
            trayIcon.Icon = Icon;
            trayIcon.Text = AppName;
            trayIcon.Visible = false;
            trayIcon.DoubleClick += delegate { RestoreFromTray(); };
            ContextMenuStrip cm = new ContextMenuStrip();
            cm.Items.Add("開く", null, delegate { RestoreFromTray(); });
            cm.Items.Add(new ToolStripSeparator());
            cm.Items.Add("終了", null, delegate { RestoreFromTray(); Close(); });
            trayIcon.ContextMenuStrip = cm;
        }

        void MinimizeToTray()
        {
            if (trayIcon == null) return;
            trayIcon.Visible = true;
            Hide();
            ShowInTaskbar = false;
            if (!settings.GetBool("SeenTrayTip", false))
            {
                trayIcon.ShowBalloonTip(4000, AppName, "タスクトレイに入りました。Ctrl+Shift+O または通知領域のアイコンから元に戻せます。", ToolTipIcon.Info);
                settings.Set("SeenTrayTip", true);
                settings.Save();
            }
        }

        void RestoreFromTray()
        {
            if (trayIcon != null) trayIcon.Visible = false;
            ShowInTaskbar = true;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
            editor.Focus();
        }

        bool PickColor(Color current, out Color result)
        {
            result = current;
            using (ColorDialog d = new ColorDialog())
            {
                d.Color = current;
                d.FullOpen = true;
                if (d.ShowDialog(this) == DialogResult.OK) { result = d.Color; return true; }
            }
            return false;
        }

        void SetInvisibles(bool on)
        {
            editor.ShowInvisibles = on;
        }

        void SetDarkMode(bool dark)
        {
            darkMode = dark;
            ApplyTheme();
            SaveSettings();
        }

        /// <summary>現在のテーマ(ライト/ダーク)の色を、エディタと自動着色に反映する</summary>
        void ApplyTheme()
        {
            ThemeColors t = CurTheme;
            numberColor = t.Number;
            letterColor = t.Letter;
            if (editor != null)
            {
                editor.BackColor = t.Back;
                editor.ForeColor = t.Fore;
                editor.InvisibleColor = symbolColorOn ? t.Invisible : t.Fore;
            }
            if (miThemeLight != null) { miThemeLight.Checked = !darkMode; miThemeDark.Checked = darkMode; }
            if (miSymbolColorOn != null) miSymbolColorOn.Checked = symbolColorOn;
            needPaint = true;
            if (editor != null) editor.Invalidate();
        }

        void InsertDate()
        {
            string s = DateTime.Now.ToString("yyMMdd", CultureInfo.InvariantCulture);
            editor.ReplaceRange(editor.SelectionStart, editor.SelectionLength, s);
            editor.Select(editor.SelectionStart, 0);
            editor.Focus();
        }

        void InsertTime()
        {
            string s = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
            editor.ReplaceRange(editor.SelectionStart, editor.SelectionLength, s);
            editor.Select(editor.SelectionStart, 0);
            editor.Focus();
        }

        /// <summary>選択範囲を半角/全角に変換する(全角数字/半角数字も対象)</summary>
        void ConvertWidth(bool toFull)
        {
            int ss = editor.SelectionStart, sl = editor.SelectionLength;
            if (sl == 0) { Msg("変換する範囲を選択してください"); return; }
            string src = editor.SelectedPlainText;
            string dst = toFull ? HalfFullWidth.ToFull(src) : HalfFullWidth.ToHalf(src);
            if (dst == src) { Msg("変換の必要はありませんでした"); return; }
            editor.ReplaceRange(ss, sl, dst);
            editor.Select(ss, dst.Length);
            Msg((toFull ? "全角化しました" : "半角化しました") + "(Ctrl+Z で元に戻せます)");
        }

        // ======================= ファイル履歴(MRU) =======================

        void AddToMru(string path)
        {
            for (int i = mru.Count - 1; i >= 0; i--)
                if (string.Equals(mru[i], path, StringComparison.OrdinalIgnoreCase)) mru.RemoveAt(i);
            mru.Insert(0, path);
            while (mru.Count > MruMax) mru.RemoveAt(mru.Count - 1);
            settings.Set("MRU", string.Join("\u001F", mru.ToArray()));
            settings.Save();
            RebuildMruMenu();
        }

        void RebuildMruMenu()
        {
            mRecent.DropDownItems.Clear();
            if (mru.Count == 0)
            {
                ToolStripMenuItem none = new ToolStripMenuItem("(履歴なし)");
                none.Enabled = false;
                mRecent.DropDownItems.Add(none);
                return;
            }
            for (int i = 0; i < mru.Count; i++)
            {
                string path = mru[i];
                ToolStripMenuItem it = new ToolStripMenuItem((i + 1) + "  " + Path.GetFileName(path));
                it.ToolTipText = path;
                it.Tag = path;
                it.Click += delegate (object s, EventArgs e)
                {
                    string p = (string)((ToolStripMenuItem)s).Tag;
                    OpenFromMru(p);
                };
                mRecent.DropDownItems.Add(it);
            }
            mRecent.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem clear = new ToolStripMenuItem("履歴をクリア");
            clear.Click += delegate { mru.Clear(); settings.Set("MRU", ""); settings.Save(); RebuildMruMenu(); };
            mRecent.DropDownItems.Add(clear);
        }

        void OpenFromMru(string path)
        {
            if (!File.Exists(path))
            {
                MessageBox.Show(this, "ファイルが見つかりません:\n" + path, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                mru.Remove(path);
                settings.Set("MRU", string.Join("\u001F", mru.ToArray()));
                settings.Save();
                RebuildMruMenu();
                return;
            }
            if (ConfirmDiscard()) OpenFile(path);
        }

        // ======================= 自動保存(テンポラリフォルダへの複製) =======================

        static string SanitizeFileName(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0) sb.Append('_');
                else sb.Append(c);
            }
            return sb.Length == 0 ? "無題" : sb.ToString();
        }

        /// <summary>編集中の文書の「身元」が変わった(新規/別ファイルを開いた/保存した)ときに呼ぶ</summary>
        void ResetAutosaveIdentity() { ResetAutosaveIdentity(filePath); }

        void ResetAutosaveIdentity(string basedOnPath)
        {
            DeleteAutosaveFile();
            string baseName = basedOnPath == null ? "無題" : Path.GetFileNameWithoutExtension(basedOnPath);
            string unique = Process.GetCurrentProcess().Id + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            autosavePath = Path.Combine(AutosaveDir, SanitizeFileName(baseName) + "_" + unique + ".txt");
            autosaveLastText = null;
        }

        void WriteAutosave()
        {
            try
            {
                if (!editor.IsDirty) return;
                if (autosavePath == null) return;
                string text = editor.PlainText;
                if (text == autosaveLastText) return;
                if (text.Length == 0) return;
                Directory.CreateDirectory(AutosaveDir);
                TextFile.Save(autosavePath, text, fileEnc, eol);
                autosaveLastText = text;
            }
            catch { }
        }

        void DeleteAutosaveFile()
        {
            try { if (autosavePath != null && File.Exists(autosavePath)) File.Delete(autosavePath); } catch { }
            autosavePath = null;
        }

        /// <summary>前回セッションが異常終了して残っていた自動保存ファイルがあれば復元を提案する</summary>
        void OfferAutosaveRecovery()
        {
            try
            {
                if (!Directory.Exists(AutosaveDir)) return;
                string[] files = Directory.GetFiles(AutosaveDir, "*.txt");
                if (files.Length == 0) return;
                string newest = files[0];
                DateTime newestTime = File.GetLastWriteTimeUtc(newest);
                foreach (string f in files)
                {
                    DateTime t = File.GetLastWriteTimeUtc(f);
                    if (t > newestTime) { newest = f; newestTime = t; }
                }
                DialogResult r = MessageBox.Show(this,
                    "前回のセッションで保存されなかった内容が見つかりました。\n(" + Path.GetFileName(newest) + ")\n\n復元しますか?",
                    AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r == DialogResult.Yes && ConfirmDiscard())
                {
                    FileEnc enc; string e; string note;
                    string text = TextFile.Load(newest, FileEnc.Utf8Bom, out enc, out e, out note);
                    filePath = null;
                    fileEnc = enc;
                    eol = e;
                    metaDirty = true;
                    editor.LoadText(text);
                    ResetAutosaveIdentity();
                    UpdateEncUi();
                    UpdateTitle(true);
                    Msg("前回の内容を復元しました。内容を確認して保存してください。");
                }
                foreach (string f in files) { try { File.Delete(f); } catch { } }
            }
            catch { }
        }

        // ======================= 設定 =======================

        void LoadSettings()
        {
            string defFont = "MS ゴシック";
            foreach (string cand in new string[] { "BIZ UDゴシック", "BIZ UDGothic", "MS ゴシック", "MS Gothic", "Consolas" })
                if (fontNames.Contains(cand)) { defFont = cand; break; }
            string font = settings.Get("Font", defFont);
            if (!fontNames.Contains(font)) font = defFont;
            float size = settings.GetFloat("FontSize", 12f);
            cmbFont.Text = font;
            cmbSize.Text = size.ToString(CultureInfo.InvariantCulture);
            ApplyFont();

            btnWrap.Checked = settings.GetBool("Wrap", false);
            SetWrap(btnWrap.Checked);
            btnRegex.Checked = settings.GetBool("Regex", false);

            btnInvisibles.Checked = settings.GetBool("Invisibles", false);
            SetInvisibles(btnInvisibles.Checked);

            colorNumbersOn = settings.GetBool("ColorNumbersOn", true);
            colorLettersOn = settings.GetBool("ColorLettersOn", true);
            symbolColorOn = settings.GetBool("SymbolColorOn", true);
            miNumColorOn.Checked = colorNumbersOn;
            miLetterColorOn.Checked = colorLettersOn;
            miSymbolColorOn.Checked = symbolColorOn;

            // テーマ(ライト/ダーク)ごとの色。旧バージョンの単一の NumberColor/LetterColor があれば
            // ライトテーマの初期値として引き継ぐ。
            lightTheme.Back = Color.FromArgb(settings.GetInt("Light.Back", lightTheme.Back.ToArgb()));
            lightTheme.Fore = Color.FromArgb(settings.GetInt("Light.Fore", lightTheme.Fore.ToArgb()));
            lightTheme.Number = Color.FromArgb(settings.GetInt("Light.Number", settings.GetInt("NumberColor", lightTheme.Number.ToArgb())));
            lightTheme.Letter = Color.FromArgb(settings.GetInt("Light.Letter", settings.GetInt("LetterColor", lightTheme.Letter.ToArgb())));
            lightTheme.Invisible = Color.FromArgb(settings.GetInt("Light.Invisible", lightTheme.Invisible.ToArgb()));

            darkTheme.Back = Color.FromArgb(settings.GetInt("Dark.Back", darkTheme.Back.ToArgb()));
            darkTheme.Fore = Color.FromArgb(settings.GetInt("Dark.Fore", darkTheme.Fore.ToArgb()));
            darkTheme.Number = Color.FromArgb(settings.GetInt("Dark.Number", darkTheme.Number.ToArgb()));
            darkTheme.Letter = Color.FromArgb(settings.GetInt("Dark.Letter", darkTheme.Letter.ToArgb()));
            darkTheme.Invisible = Color.FromArgb(settings.GetInt("Dark.Invisible", darkTheme.Invisible.ToArgb()));

            darkMode = settings.GetBool("DarkMode", false);
            ApplyTheme();

            mru.Clear();
            foreach (string p in settings.Get("MRU", "").Split('\u001F'))
                if (p.Length > 0 && File.Exists(p)) mru.Add(p);
            RebuildMruMenu();

            defaultWinW = settings.GetInt("TripleClickW", 800);
            defaultWinH = settings.GetInt("TripleClickH", 600);

            int w = settings.GetInt("W", S(1100)), h = settings.GetInt("H", S(720));
            int x = settings.GetInt("X", int.MinValue), y = settings.GetInt("Y", int.MinValue);
            Size = new Size(Math.Max(400, w), Math.Max(300, h));
            if (x != int.MinValue && y != int.MinValue)
            {
                Rectangle r = new Rectangle(x, y, Width, Height);
                foreach (Screen sc in Screen.AllScreens)
                {
                    if (sc.WorkingArea.IntersectsWith(r)) { StartPosition = FormStartPosition.Manual; Location = new Point(x, y); break; }
                }
            }
            if (settings.GetBool("Max", false)) WindowState = FormWindowState.Maximized;
        }

        /// <summary>タイトルバーのトリプルクリックで呼ばれる: ウィンドウを既定サイズに戻す</summary>
        void ResetWindowToDefaultSize()
        {
            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            Size = new Size(S(defaultWinW), S(defaultWinH));
        }

        /// <summary>表示メニュー「トリプルクリックで現在のサイズに変更する」: 現在のウィンドウサイズを既定値として保存</summary>
        void SetTripleClickSizeToCurrent()
        {
            Size sz = WindowState == FormWindowState.Normal ? Size : RestoreBounds.Size;
            defaultWinW = Math.Max(200, (int)Math.Round(sz.Width / scale));
            defaultWinH = Math.Max(150, (int)Math.Round(sz.Height / scale));
            settings.Set("TripleClickW", defaultWinW);
            settings.Set("TripleClickH", defaultWinH);
            settings.Save();
        }

        void SaveSettings()
        {
            settings.Set("Font", editor.Font.Name);
            settings.Set("FontSize", editor.Font.Size);
            settings.Set("Wrap", btnWrap.Checked);
            settings.Set("Regex", btnRegex.Checked);
            settings.Set("Invisibles", btnInvisibles.Checked);
            settings.Set("ColorNumbersOn", colorNumbersOn);
            settings.Set("ColorLettersOn", colorLettersOn);
            settings.Set("SymbolColorOn", symbolColorOn);
            settings.Set("DarkMode", darkMode);
            settings.Set("Light.Back", lightTheme.Back.ToArgb());
            settings.Set("Light.Fore", lightTheme.Fore.ToArgb());
            settings.Set("Light.Number", lightTheme.Number.ToArgb());
            settings.Set("Light.Letter", lightTheme.Letter.ToArgb());
            settings.Set("Light.Invisible", lightTheme.Invisible.ToArgb());
            settings.Set("Dark.Back", darkTheme.Back.ToArgb());
            settings.Set("Dark.Fore", darkTheme.Fore.ToArgb());
            settings.Set("Dark.Number", darkTheme.Number.ToArgb());
            settings.Set("Dark.Letter", darkTheme.Letter.ToArgb());
            settings.Set("Dark.Invisible", darkTheme.Invisible.ToArgb());
            settings.Set("Max", WindowState == FormWindowState.Maximized);
            Rectangle b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            settings.Set("X", b.X); settings.Set("Y", b.Y); settings.Set("W", b.Width); settings.Set("H", b.Height);
            settings.Save();
        }

        void ApplyFont()
        {
            string name = cmbFont.Text.Trim();
            float size;
            if (!fontNames.Contains(name)) return;
            if (!float.TryParse(cmbSize.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out size)) return;
            if (size < 4 || size > 200) return;
            if (editor.Font.Name == name && Math.Abs(editor.Font.Size - size) < 0.01f) return;
            editor.SetEditorFont(new Font(name, size));
            needPaint = true;
            if (web != null && split != null && !split.Panel2Collapsed) NavigateTemplate();
        }

        void SetWrap(bool on)
        {
            editor.WrapEnabled = on;
            needPaint = true;
        }

        // ======================= ファイル =======================

        void NewFile()
        {
            filePath = null;
            fileEnc = FileEnc.Utf8Bom;
            eol = "\r\n";
            metaDirty = false;
            editor.LoadText("");
            lastHtml = null;
            ResetAutosaveIdentity();
            UpdateEncUi();
            UpdateTitle(true);
            SetPreview(false);
            Msg("");
        }

        void OpenDialog()
        {
            if (!ConfirmDiscard()) return;
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "テキスト/Markdown (*.txt;*.md;*.markdown;*.csv;*.log;*.ini)|*.txt;*.md;*.markdown;*.csv;*.log;*.ini|すべてのファイル (*.*)|*.*";
                if (filePath != null) d.InitialDirectory = Path.GetDirectoryName(filePath);
                if (d.ShowDialog(this) == DialogResult.OK) OpenFile(d.FileName);
            }
        }

        void OpenFile(string path)
        {
            try
            {
                FileInfo fi = new FileInfo(path);
                if (fi.Length > 200L * 1024 * 1024)
                {
                    MessageBox.Show(this, "ファイルが大きすぎます(200MB 超)。", AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                FileEnc enc; string e; string note;
                string text = TextFile.Load(path, FileEnc.Utf8Bom, out enc, out e, out note);
                if (text.IndexOf('\0') >= 0)
                {
                    if (MessageBox.Show(this, "バイナリファイルの可能性があります。開きますか?\n(NUL 文字は空白に置き換えます)", AppName,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    text = text.Replace('\0', ' ');
                }
                Cursor = Cursors.WaitCursor;
                filePath = Path.GetFullPath(path);
                fileEnc = enc;
                eol = e;
                metaDirty = note != null;
                editor.LoadText(text);
                lastHtml = null;
                ResetAutosaveIdentity();
                UpdateEncUi();
                UpdateTitle(true);
                Msg(note ?? "");
                regexKey = null;
                RecomputeMatches();
                SetPreview(IsMarkdownPath(filePath));
                AddToMru(filePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "開けませんでした。\n" + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        static bool IsMarkdownPath(string p)
        {
            string ext = (Path.GetExtension(p) ?? "").ToLowerInvariant();
            return ext == ".md" || ext == ".markdown" || ext == ".mkd" || ext == ".mdown";
        }

        bool Save()
        {
            if (filePath == null) return SaveAs();
            return WriteTo(filePath);
        }

        bool SaveAs()
        {
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "テキスト (*.txt)|*.txt|Markdown (*.md)|*.md|すべてのファイル (*.*)|*.*";
                if (filePath != null)
                {
                    d.InitialDirectory = Path.GetDirectoryName(filePath);
                    d.FileName = Path.GetFileName(filePath);
                    d.FilterIndex = IsMarkdownPath(filePath) ? 2 : (Path.GetExtension(filePath).ToLowerInvariant() == ".txt" ? 1 : 3);
                }
                if (d.ShowDialog(this) != DialogResult.OK) return false;
                string oldDir = filePath == null ? null : Path.GetDirectoryName(filePath);
                if (!WriteTo(d.FileName)) return false;
                filePath = Path.GetFullPath(d.FileName);
                UpdateTitle(true);
                if (web != null && !string.Equals(oldDir, Path.GetDirectoryName(filePath), StringComparison.OrdinalIgnoreCase)) NavigateTemplate();
                return true;
            }
        }

        bool WriteTo(string path)
        {
            string text = editor.PlainText;
            if (fileEnc == FileEnc.Sjis)
            {
                int bad = TextFile.FindUnencodable(text);
                if (bad >= 0)
                {
                    editor.Select(bad, char.IsHighSurrogate(text[bad]) && bad + 1 < text.Length ? 2 : 1);
                    editor.ScrollToCaret();
                    DialogResult r = MessageBox.Show(this,
                        "Shift_JIS で表せない文字があります(選択中の文字)。\n\n" +
                        "[はい] UTF-8 (BOM) に切り替えて保存\n[いいえ] そのまま保存(該当文字は ? になります)\n[キャンセル] 保存しない",
                        AppName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                    if (r == DialogResult.Cancel) return false;
                    if (r == DialogResult.Yes) { fileEnc = FileEnc.Utf8Bom; UpdateEncUi(); }
                }
            }
            try
            {
                TextFile.Save(path, text, fileEnc, eol);
                editor.MarkSaved();
                metaDirty = false;
                UpdateTitle(true);
                ResetAutosaveIdentity(path);
                AddToMru(Path.GetFullPath(path));
                Msg("保存しました(" + TextFile.EncName(fileEnc) + " / " + (eol == "\n" ? "LF" : "CRLF") + ")");
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存できませんでした。\n" + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        bool IsDirty { get { return editor.IsDirty || metaDirty; } }

        bool ConfirmDiscard()
        {
            if (!IsDirty) return true;
            string name = filePath == null ? "無題" : Path.GetFileName(filePath);
            DialogResult r = MessageBox.Show(this, name + " への変更を保存しますか?", AppName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (r == DialogResult.Cancel) return false;
            if (r == DialogResult.Yes) return Save();
            return true;
        }

        void SetEncoding(FileEnc e)
        {
            if (fileEnc == e) return;
            fileEnc = e;
            metaDirty = true;
            UpdateEncUi();
            UpdateTitle(true);
        }

        void SetEol(string e)
        {
            if (eol == e) return;
            eol = e;
            metaDirty = true;
            UpdateEncUi();
            UpdateTitle(true);
        }

        void UpdateEncUi()
        {
            ddEnc.Text = TextFile.EncName(fileEnc);
            miEncSjis.Checked = fileEnc == FileEnc.Sjis;
            miEncUtf8.Checked = fileEnc == FileEnc.Utf8Bom;
            ddEol.Text = eol == "\n" ? "LF" : "CRLF";
            miEolCrlf.Checked = eol != "\n";
            miEolLf.Checked = eol == "\n";
        }

        void UpdateTitle(bool force)
        {
            bool d = IsDirty;
            if (!force && d == lastDirtyShown) return;
            lastDirtyShown = d;
            string name = filePath == null ? "無題" : Path.GetFileName(filePath);
            Text = (d ? "* " : "") + name + " - " + AppTitleWithVersion;
        }

        void OnDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        void OnDragDrop(object sender, DragEventArgs e)
        {
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;
            string f = files[0];
            BeginInvoke(new MethodInvoker(delegate
            {
                if (File.Exists(f) && ConfirmDiscard()) OpenFile(f);
            }));
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!ConfirmDiscard()) { e.Cancel = true; return; }
            SaveSettings();
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { if (previewFile != null && File.Exists(previewFile)) File.Delete(previewFile); } catch { }
            DeleteAutosaveFile();
            if (hotkeyRegistered) { try { UnregisterHotKey(Handle, HOTKEY_ID); } catch { } }
            if (trayIcon != null) { trayIcon.Visible = false; trayIcon.Dispose(); }
            base.OnFormClosed(e);
        }

        // ======================= 編集状態 =======================

        void OnEditorTextChanged()
        {
            tmrMatch.Stop(); tmrMatch.Start();
            if (!split.Panel2Collapsed) { tmrPreview.Stop(); tmrPreview.Start(); }
            UpdateTitle(false);
            UpdatePos();
        }

        void UpdatePos()
        {
            string t = editor.PlainText;
            int pos = Math.Min(editor.SelectionStart, t.Length);
            int line = 1, ls = 0;
            for (int i = 0; i < pos; i++) if (t[i] == '\n') { line++; ls = i + 1; }
            string s = line + " 行, " + (pos - ls + 1) + " 桁";
            if (editor.SelectionLength > 0) s += " (" + editor.SelectionLength + " 文字選択)";
            lblPos.Text = s;
        }

        void Msg(string s) { lblMsg.Text = s; }

        void Poll()
        {
            UpdateTitle(false);
            EditorBox.POINT sp = editor.ScrollPos;
            Size cs = editor.ClientSize;
            bool moved = sp.X != lastScroll.X || sp.Y != lastScroll.Y || cs != lastClient;
            if (moved || needPaint)
            {
                if (editor.CanPaintNow)
                {
                    editor.PaintHighlights(hlStarts, hlLens, HighlightColor, colorNumbersOn, numberColor, colorLettersOn, letterColor);
                    needPaint = false;
                    lastScroll = editor.ScrollPos;
                    lastClient = cs;
                }
                else needPaint = true;
            }
            if (moved) SyncPreviewScroll();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                if (txtFind.Focused || txtReplace.Focused || cmbFont.Focused || cmbSize.Focused)
                {
                    editor.Focus();
                    return true;
                }
                MinimizeToTray();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ======================= 検索 / 置換 =======================

        void FocusFind()
        {
            string sel = editor.SelectedPlainText;
            if (sel.Length > 0 && sel.Length < 300 && sel.IndexOf('\n') < 0)
                txtFind.Text = btnRegex.Checked ? Regex.Escape(sel) : sel;
            txtFind.Focus();
            txtFind.SelectAll();
        }

        void FocusReplace()
        {
            if (txtFind.Text.Length == 0) { FocusFind(); return; }
            txtReplace.Focus();
            txtReplace.SelectAll();
        }

        void OnFindKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Find(!e.Shift); }
            else if (e.KeyCode == Keys.A && e.Control) { e.SuppressKeyPress = true; txtFind.SelectAll(); }
        }

        void OnReplaceKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; if (e.Control) ReplaceAll(); else ReplaceOne(); }
            else if (e.KeyCode == Keys.A && e.Control) { e.SuppressKeyPress = true; txtReplace.SelectAll(); }
        }

        /// <summary>入力欄から Regex を作る。無効な正規表現なら null(欄を赤くする)</summary>
        Regex GetRegex()
        {
            string pat = txtFind.Text;
            string key = (btnRegex.Checked ? "R" : "L") + pat;
            if (key == regexKey) return regexCache;
            regexKey = key;
            regexCache = null;
            txtFind.BackColor = SystemColors.Window;
            txtFind.ToolTipText = "検索(Enter: 次 / Shift+Enter: 前 / Esc: 本文へ)";
            if (pat.Length == 0) return null;
            RegexOptions o = RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
            try
            {
                regexCache = new Regex(btnRegex.Checked ? pat : Regex.Escape(pat), o, TimeSpan.FromSeconds(3));
            }
            catch (ArgumentException ex)
            {
                txtFind.BackColor = Color.FromArgb(255, 210, 210);
                txtFind.ToolTipText = "正規表現エラー: " + ex.Message;
                Msg("正規表現エラー: " + ex.Message);
            }
            return regexCache;
        }

        void RecomputeMatches()
        {
            hlStarts.Clear();
            hlLens.Clear();
            Regex re = GetRegex();
            if (re != null)
            {
                try
                {
                    Match m = re.Match(editor.PlainText);
                    while (m.Success && hlStarts.Count < 300000)
                    {
                        if (m.Length > 0) { hlStarts.Add(m.Index); hlLens.Add(m.Length); }
                        m = m.NextMatch();
                    }
                }
                catch (RegexMatchTimeoutException) { Msg("検索がタイムアウトしました(正規表現を見直してください)"); }
            }
            UpdateCount();
            needPaint = true;
            Poll();
        }

        void UpdateCount()
        {
            if (txtFind.Text.Length == 0 || regexCache == null) { lblCount.Text = ""; return; }
            if (hlStarts.Count == 0) { lblCount.Text = "一致なし"; return; }
            int idx = hlStarts.BinarySearch(editor.SelectionStart);
            bool onMatch = idx >= 0 && hlLens[idx] == editor.SelectionLength;
            lblCount.Text = (onMatch ? (idx + 1) + " / " : "") + hlStarts.Count + (hlStarts.Count >= 300000 ? "+" : "") + " 件";
        }

        bool Find(bool forward)
        {
            if (txtFind.Text.Length == 0) { FocusFind(); return false; }
            Regex re = GetRegex();
            if (re == null) return false;
            string text = editor.PlainText;
            int ss = editor.SelectionStart, sl = editor.SelectionLength;
            Match hit = null;
            bool wrapped = false;
            try
            {
                if (forward)
                {
                    int from = Math.Min(ss + sl, text.Length);
                    Match m = re.Match(text, from);
                    while (m.Success && m.Length == 0) m = m.NextMatch();
                    if (!m.Success)
                    {
                        m = re.Match(text, 0);
                        while (m.Success && m.Length == 0) m = m.NextMatch();
                        wrapped = true;
                    }
                    if (m.Success) hit = m;
                }
                else
                {
                    Match best = null, lastAll = null;
                    Match m = re.Match(text);
                    while (m.Success)
                    {
                        if (m.Length > 0)
                        {
                            if (m.Index < ss) best = m;
                            else if (best != null) break;
                            lastAll = m;
                        }
                        m = m.NextMatch();
                    }
                    if (best != null) hit = best;
                    else if (lastAll != null) { hit = lastAll; wrapped = true; }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                Msg("検索がタイムアウトしました");
                return false;
            }

            if (hit == null)
            {
                Msg("「" + txtFind.Text + "」は見つかりません");
                System.Media.SystemSounds.Beep.Play();
                UpdateCount();
                return false;
            }
            editor.Select(hit.Index, hit.Length);
            editor.ScrollToCaret();
            Msg(wrapped ? (forward ? "末尾に達したので先頭から検索しました" : "先頭に達したので末尾から検索しました") : "");
            UpdateCount();
            return true;
        }

        string ReplacementFor(Match m)
        {
            string rep = txtReplace.Text;
            if (!btnRegex.Checked) return rep;
            // \n \t \\ を展開してから $1 等を展開
            StringBuilder sb = new StringBuilder(rep.Length);
            for (int i = 0; i < rep.Length; i++)
            {
                char c = rep[i];
                if (c == '\\' && i + 1 < rep.Length)
                {
                    char n = rep[i + 1];
                    if (n == 'n') { sb.Append('\n'); i++; continue; }
                    if (n == 't') { sb.Append('\t'); i++; continue; }
                    if (n == '\\') { sb.Append('\\'); i++; continue; }
                }
                sb.Append(c);
            }
            return m.Result(sb.ToString());
        }

        void ReplaceOne()
        {
            Regex re = GetRegex();
            if (re == null) { FocusFind(); return; }
            string text = editor.PlainText;
            int ss = editor.SelectionStart, sl = editor.SelectionLength;
            try
            {
                if (sl > 0)
                {
                    Match m = re.Match(text, ss);
                    if (m.Success && m.Index == ss && m.Length == sl)
                    {
                        string rep = ReplacementFor(m);
                        editor.ReplaceRange(ss, sl, rep);
                        editor.Select(ss + rep.Length, 0);
                    }
                }
            }
            catch (RegexMatchTimeoutException) { Msg("検索がタイムアウトしました"); return; }
            Find(true);
        }

        void ReplaceAll()
        {
            Regex re = GetRegex();
            if (re == null) { FocusFind(); return; }
            string text = editor.PlainText;
            int count = 0;
            string result;
            try
            {
                result = re.Replace(text, delegate (Match m) { count++; return ReplacementFor(m); });
            }
            catch (RegexMatchTimeoutException) { Msg("置換がタイムアウトしました"); return; }
            catch (ArgumentException ex) { Msg("置換文字列エラー: " + ex.Message); return; }
            if (count == 0) { Msg("「" + txtFind.Text + "」は見つかりません"); return; }
            if (result == text) { Msg(count + " 件一致しましたが変更はありません"); return; }
            Cursor = Cursors.WaitCursor;
            try { editor.ReplaceAll(result, editor.SelectionStart); }
            finally { Cursor = Cursors.Default; }
            Msg(count + " 件置換しました(Ctrl+Z で元に戻せます)");
        }

        // ======================= Markdown プレビュー =======================

        void SetPreview(bool on)
        {
            miPreview.Checked = on;
            if (!on)
            {
                split.Panel2Collapsed = true;
                return;
            }
            bool wasCollapsed = split.Panel2Collapsed;
            split.Panel2Collapsed = false;
            if (wasCollapsed)
            {
                float ratio = settings.GetFloat("SplitRatio", 0.5f);
                if (ratio < 0.15f || ratio > 0.85f) ratio = 0.5f;
                try { split.SplitterDistance = (int)(split.Width * ratio); } catch { }
            }
            if (web == null)
            {
                web = new WebBrowser();
                web.Dock = DockStyle.Fill;
                web.ScriptErrorsSuppressed = true;
                web.AllowWebBrowserDrop = false;
                web.IsWebBrowserContextMenuEnabled = true;
                web.WebBrowserShortcutsEnabled = true;
                web.Navigating += OnWebNavigating;
                web.DocumentCompleted += delegate
                {
                    webReady = true;
                    lastHtml = null;
                    lastRatio = -1;
                    UpdatePreviewNow();
                };
                split.Panel2.Controls.Add(web);
                NavigateTemplate();
            }
            else
            {
                string dir = filePath == null ? null : Path.GetDirectoryName(filePath);
                if (!string.Equals(dir, previewBaseDir, StringComparison.OrdinalIgnoreCase)) NavigateTemplate();
                else UpdatePreviewNow();
            }
        }

        void NavigateTemplate()
        {
            webReady = false;
            string dir = filePath == null ? null : Path.GetDirectoryName(filePath);
            previewBaseDir = dir;
            if (previewFile == null)
                previewFile = Path.Combine(Path.GetTempPath(), "okaed_preview_" + Process.GetCurrentProcess().Id + ".html");
            string baseTag = "";
            if (dir != null)
            {
                string u = new Uri(dir.EndsWith("\\") ? dir : dir + "\\").AbsoluteUri;
                baseTag = "<base href=\"" + Markdown.Esc(u) + "\">";
            }
            float bodyFontPx = editor != null ? editor.Font.Size * 96f / 72f * scale : 15f;
            string html = "<!DOCTYPE html>\n<html><head><meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\">" +
                          "<meta charset=\"utf-8\">" + baseTag + "<style>" + PreviewCss(bodyFontPx) + "</style></head>" +
                          "<body><div id=\"content\"></div></body></html>";
            try
            {
                File.WriteAllText(previewFile, html, new UTF8Encoding(true));
                web.Navigate(previewFile);
            }
            catch (Exception ex) { Msg("プレビューを表示できません: " + ex.Message); }
        }

        void OnWebNavigating(object sender, WebBrowserNavigatingEventArgs e)
        {
            Uri u = e.Url;
            if (u == null) return;
            if (u.IsFile && previewFile != null &&
                string.Equals(Path.GetFullPath(u.LocalPath), previewFile, StringComparison.OrdinalIgnoreCase)) return;
            if (u.AbsoluteUri == "about:blank") return;
            e.Cancel = true;
            // リンクは既定のブラウザ等で開く
            if (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeMailto)
            {
                try { Process.Start(u.AbsoluteUri); } catch { }
            }
            else if (u.IsFile && File.Exists(u.LocalPath))
            {
                string p = u.LocalPath;
                BeginInvoke(new MethodInvoker(delegate
                {
                    string ext = (Path.GetExtension(p) ?? "").ToLowerInvariant();
                    if ((IsMarkdownPath(p) || ext == ".txt") && ConfirmDiscard()) OpenFile(p);
                }));
            }
        }

        void UpdatePreviewNow()
        {
            if (web == null || !webReady || split.Panel2Collapsed) return;
            HtmlDocument d = web.Document;
            if (d == null) return;
            HtmlElement c = d.GetElementById("content");
            if (c == null) return;
            string html = Markdown.ToHtml(editor.PlainText);
            if (html == lastHtml) return;
            lastHtml = html;
            HtmlElement root = RootElement(d);
            int top = root != null ? root.ScrollTop : 0;
            c.InnerHtml = html;
            if (root != null) root.ScrollTop = top;
            lastRatio = -1;
            SyncPreviewScroll();
        }

        static HtmlElement RootElement(HtmlDocument d)
        {
            HtmlElementCollection col = d.GetElementsByTagName("html");
            return col.Count > 0 ? col[0] : d.Body;
        }

        void SyncPreviewScroll()
        {
            if (web == null || !webReady || split.Panel2Collapsed) return;
            double r = editor.VerticalRatio;
            if (Math.Abs(r - lastRatio) < 0.0005) return;
            lastRatio = r;
            try
            {
                HtmlDocument d = web.Document;
                if (d == null) return;
                HtmlElement root = RootElement(d);
                if (root == null) return;
                int max = root.ScrollRectangle.Height - root.ClientRectangle.Height;
                if (max > 0) root.ScrollTop = (int)(max * r);
            }
            catch { }
        }

        static string PreviewCss(float bodyFontPx)
        {
            return
            "html{background:#fff}" +
            "body{font-family:'Yu Gothic UI','Meiryo UI','Meiryo',sans-serif;font-size:" + bodyFontPx.ToString("0.##", CultureInfo.InvariantCulture) + "px;line-height:1.7;color:#1f2328;margin:0;padding:16px 24px 40px;word-wrap:break-word}" +
            "h1,h2,h3,h4,h5,h6{margin:1.2em 0 .5em;line-height:1.3;font-weight:600}" +
            "h1{font-size:1.9em;border-bottom:1px solid #d8dee4;padding-bottom:.25em}" +
            "h2{font-size:1.5em;border-bottom:1px solid #d8dee4;padding-bottom:.25em}" +
            "h3{font-size:1.25em}h4{font-size:1.05em}h5,h6{font-size:.95em;color:#59636e}" +
            "p,ul,ol,blockquote,pre,table{margin:0 0 1em}" +
            "ul,ol{padding-left:2em}li{margin:.2em 0}li.task{list-style:none;margin-left:-1.4em}" +
            "a{color:#0969da;text-decoration:none}a:hover{text-decoration:underline}" +
            "code{font-family:'BIZ UDGothic','MS Gothic',Consolas,monospace;font-size:.9em;background:#eff1f3;padding:.15em .35em;border-radius:4px}" +
            "pre{background:#f6f8fa;padding:12px 14px;overflow:auto;border-radius:6px;line-height:1.5}" +
            "pre code{background:none;padding:0;font-size:.88em}" +
            "blockquote{margin-left:0;padding:0 1em;color:#59636e;border-left:4px solid #d0d7de}" +
            "table{border-collapse:collapse}th,td{border:1px solid #d0d7de;padding:5px 12px}th{background:#f6f8fa;font-weight:600}" +
            "tr:nth-child(even) td{background:#fafbfc}" +
            "hr{border:0;border-top:2px solid #d8dee4;margin:1.5em 0}" +
            "img{max-width:100%}del{color:#8c959f}";
        }
    }
}
