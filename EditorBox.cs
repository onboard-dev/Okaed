using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Okaed
{
    /// <summary>
    /// プレーンテキスト専用に調整した RichTextBox。
    /// ・独自の Undo/Redo(ハイライト等の書式変更は履歴に残らない)
    /// ・書式付き貼り付け/コピーの抑止、書式ショートカットの無効化
    /// ・表示範囲だけを塗る高速な検索ハイライト
    /// </summary>
    public class EditorBox : RichTextBox
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr LoadLibrary(string name);
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, ref POINT l);
        [DllImport("user32.dll")]
        static extern bool GetScrollInfo(IntPtr h, int bar, ref SCROLLINFO si);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)]
        struct SCROLLINFO { public int cbSize; public uint fMask; public int nMin; public int nMax; public uint nPage; public int nPos; public int nTrackPos; }

        const int WM_USER = 0x400;
        const int WM_PAINT = 0x000F;
        const int WM_SETREDRAW = 0x0B;
        const int WM_IME_STARTCOMPOSITION = 0x010D;
        const int WM_IME_ENDCOMPOSITION = 0x010E;
        const int WM_LBUTTONDOWN = 0x0201;
        const int WM_LBUTTONDBLCLK = 0x0203;
        const int EM_SETEVENTMASK = WM_USER + 69;
        const int EM_GETEVENTMASK = WM_USER + 59;
        const int EM_SETUNDOLIMIT = WM_USER + 82;
        const int EM_SETTARGETDEVICE = WM_USER + 72;
        const int EM_SETLANGOPTIONS = WM_USER + 120;
        const int EM_GETLANGOPTIONS = WM_USER + 121;
        const int EM_GETSCROLLPOS = WM_USER + 221;
        const int EM_SETSCROLLPOS = WM_USER + 222;
        const int IMF_AUTOFONT = 0x0002;
        const int IMF_DUALFONT = 0x0080;

        static readonly bool msftedit;
        static EditorBox()
        {
            try { msftedit = LoadLibrary("msftedit.dll") != IntPtr.Zero; } catch { msftedit = false; }
        }

        class Edit
        {
            public long Id;
            public int Pos;
            public string Removed;
            public string Inserted;
            public DateTime Time;
        }

        readonly List<Edit> undo = new List<Edit>();
        readonly List<Edit> redo = new List<Edit>();
        long nextId = 1;
        long savedId = 0;
        bool noMerge;
        int suppress;
        bool composing;
        bool wrap;
        bool formatted;   // ハイライト書式が文書中に残っている可能性
        string shadow = "";

        bool showInvisibles;
        bool invisiblesPending;
        Font invisFont;
        Color invisibleColor = Color.FromArgb(70, 140, 210);
        Brush invisBrush = new SolidBrush(Color.FromArgb(220, 70, 140, 210));

        /// <summary>空白可視化の記号(半角/全角スペース・タブ・改行)の色</summary>
        public Color InvisibleColor
        {
            get { return invisibleColor; }
            set
            {
                if (invisibleColor == value) return;
                invisibleColor = value;
                if (invisBrush != null) invisBrush.Dispose();
                invisBrush = new SolidBrush(Color.FromArgb(220, value));
                if (showInvisibles) Invalidate();
            }
        }

        /// <summary>本文エリアでのトリプルクリックで発生(ウィンドウサイズのリセットなどに利用)</summary>
        public event EventHandler TripleClick;
        int leftClickCount;
        int lastLeftClickTick;
        Point lastLeftClickPos = new Point(int.MinValue, int.MinValue);

        public EditorBox()
        {
            DetectUrls = false;
            AcceptsTab = true;
            HideSelection = false;
            WordWrap = false;
            ScrollBars = RichTextBoxScrollBars.Both;
            BorderStyle = BorderStyle.None;
            ShortcutsEnabled = true;
            EnableAutoDragDrop = false;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                if (msftedit) cp.ClassName = "RICHEDIT50W";
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // ネイティブUndoは使わない(書式変更も積まれるため)
            SendMessage(Handle, EM_SETUNDOLIMIT, IntPtr.Zero, IntPtr.Zero);
            // 日本語入力時にフォントが勝手に切り替わるのを防ぐ
            int opt = (int)SendMessage(Handle, EM_GETLANGOPTIONS, IntPtr.Zero, IntPtr.Zero);
            opt &= ~(IMF_AUTOFONT | IMF_DUALFONT);
            SendMessage(Handle, EM_SETLANGOPTIONS, IntPtr.Zero, (IntPtr)opt);
            ApplyWrap();
        }

        // ---------------- テキスト ----------------

        /// <summary>改行を "\n" に正規化した全文(内部キャッシュ)</summary>
        public string PlainText { get { return shadow; } }

        string ReadText()
        {
            string t = base.Text;
            if (t.IndexOf('\r') >= 0) t = t.Replace("\r\n", "\n").Replace('\r', '\n');
            return t;
        }

        public void LoadText(string text)
        {
            suppress++;
            try
            {
                SendMessage(Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
                base.Text = text;
                base.SelectAll();
                SelectionBackColor = BackColor;
                SelectionFont = Font;
                Select(0, 0);
                formatted = false;
                shadow = ReadText();
                undo.Clear(); redo.Clear();
                savedId = 0; noMerge = true;
            }
            finally
            {
                suppress--;
                SendMessage(Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                Invalidate();
            }
            OnTextChanged(EventArgs.Empty);
        }

        public bool WrapEnabled
        {
            get { return wrap; }
            set { wrap = value; if (IsHandleCreated) ApplyWrap(); }
        }

        void ApplyWrap()
        {
            // WordWrap プロパティはハンドル再生成を伴うため、メッセージで切り替える
            SendMessage(Handle, EM_SETTARGETDEVICE, IntPtr.Zero, wrap ? IntPtr.Zero : (IntPtr)1);
        }

        public void SetEditorFont(Font f)
        {
            int ss = SelectionStart, sl = SelectionLength;
            POINT sp = ScrollPos;
            suppress++;
            IntPtr mask = SendMessage(Handle, EM_GETEVENTMASK, IntPtr.Zero, IntPtr.Zero);
            SendMessage(Handle, EM_SETEVENTMASK, IntPtr.Zero, IntPtr.Zero);
            SendMessage(Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            try
            {
                Font = f;
                base.SelectAll();
                SelectionFont = f;
                Select(ss, sl);
                if (sl == 0) SelectionFont = f;
                SendMessage(Handle, EM_SETSCROLLPOS, IntPtr.Zero, ref sp);
            }
            finally
            {
                SendMessage(Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                SendMessage(Handle, EM_SETEVENTMASK, IntPtr.Zero, mask);
                suppress--;
                Invalidate();
            }
        }

        // ---------------- Undo / Redo ----------------

        public bool IsDirty { get { return TopId != savedId; } }
        long TopId { get { return undo.Count > 0 ? undo[undo.Count - 1].Id : 0; } }
        public void MarkSaved() { RecordChange(); savedId = TopId; noMerge = true; }
        public bool CanUndoEdit { get { return undo.Count > 0; } }
        public bool CanRedoEdit { get { return redo.Count > 0; } }

        protected override void OnTextChanged(EventArgs e)
        {
            if (suppress == 0 && !composing) RecordChange();
            base.OnTextChanged(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_IME_STARTCOMPOSITION) composing = true;
            if (m.Msg == WM_LBUTTONDOWN || m.Msg == WM_LBUTTONDBLCLK) TrackLeftClickForTripleClick(m.LParam);
            base.WndProc(ref m);
            if (m.Msg == WM_IME_ENDCOMPOSITION)
            {
                composing = false;
                // 確定文字列の挿入が終わってから1回だけ記録する
                BeginInvoke(new MethodInvoker(delegate
                {
                    if (!IsDisposed && !composing && suppress == 0) RecordChange();
                }));
            }
            else if (m.Msg == WM_PAINT && showInvisibles && !invisiblesPending)
            {
                // WM_PAINT の処理中に EM_POSFROMCHAR 等を SendMessage で問い合わせると、
                // RichEdit(特に IME 変換中の内部状態)と競合することがあるため、
                // 描画は WM_PAINT の外側(メッセージループに戻った直後)まで遅らせる。
                invisiblesPending = true;
                BeginInvoke(new MethodInvoker(delegate
                {
                    invisiblesPending = false;
                    if (!IsDisposed && showInvisibles && !composing) DrawInvisibles();
                }));
            }
        }

        void TrackLeftClickForTripleClick(IntPtr lParam)
        {
            int lp = lParam.ToInt32();
            Point pos = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
            int tick = Environment.TickCount;
            Size tol = SystemInformation.DoubleClickSize;
            bool sameSpot = Math.Abs(pos.X - lastLeftClickPos.X) <= Math.Max(1, tol.Width / 2)
                          && Math.Abs(pos.Y - lastLeftClickPos.Y) <= Math.Max(1, tol.Height / 2);
            if (sameSpot && tick - lastLeftClickTick <= SystemInformation.DoubleClickTime) leftClickCount++;
            else leftClickCount = 1;
            lastLeftClickTick = tick;
            lastLeftClickPos = pos;
            if (leftClickCount >= 3)
            {
                leftClickCount = 0;
                if (TripleClick != null) BeginInvoke(new MethodInvoker(delegate { if (!IsDisposed) { EventHandler h = TripleClick; if (h != null) h(this, EventArgs.Empty); } }));
            }
        }

        /// <summary>半角/全角スペース・タブ・改行を記号で可視化する(実データは変更しない)</summary>
        public bool ShowInvisibles
        {
            get { return showInvisibles; }
            set { if (showInvisibles != value) { showInvisibles = value; Invalidate(); } }
        }

        /// <summary>指定位置付近の実際の行の高さ(px)を、フォント計量ではなく
        /// RichEdit が実際に描画している座標差から求める(DPI 拡大率に依存しない)。</summary>
        float MeasureLineHeight(int atCharIndex, string text, float dpiScale)
        {
            try
            {
                int line = GetLineFromCharIndex(atCharIndex);
                int idxThis = GetFirstCharIndexFromLine(line);
                int idxNext = GetFirstCharIndexFromLine(line + 1);
                if (idxThis >= 0 && idxNext > idxThis)
                {
                    Point pa = GetPositionFromCharIndex(idxThis);
                    Point pb = GetPositionFromCharIndex(idxNext);
                    if (pb.Y > pa.Y) return pb.Y - pa.Y;
                }
                if (idxThis > 0 && line > 0)
                {
                    int idxPrev = GetFirstCharIndexFromLine(line - 1);
                    if (idxPrev >= 0)
                    {
                        Point pa = GetPositionFromCharIndex(idxPrev);
                        Point pb = GetPositionFromCharIndex(idxThis);
                        if (pb.Y > pa.Y) return pb.Y - pa.Y;
                    }
                }
            }
            catch { }
            return Font.Height * dpiScale;
        }

        void DrawInvisibles()
        {
            if (!IsHandleCreated) return;
            string text = shadow;
            if (text.Length == 0) return;
            Rectangle client = ClientRectangle;
            int first = GetCharIndexFromPosition(new Point(0, 0));
            int last = GetCharIndexFromPosition(new Point(Math.Max(0, client.Width - 1), Math.Max(0, client.Height - 1)));
            first = Math.Max(0, Math.Min(first, text.Length - 1));
            last = Math.Max(first, Math.Min(last + 1, text.Length));
            if (invisFont == null || Math.Abs(invisFont.Size - Font.Size * 0.85f) > 0.5f)
            {
                if (invisFont != null) invisFont.Dispose();
                invisFont = new Font(Font.FontFamily, Math.Max(8f, Font.Size * 0.85f), FontStyle.Bold);
            }
            try
            {
                using (Graphics g = Graphics.FromHwnd(Handle))
                using (Pen dotPen = new Pen(Color.FromArgb(200, invisibleColor), 1.4f))
                {
                    dotPen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dot;
                    g.SetClip(client);
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                    float dpiScale = g.DpiY / 96f;

                    int cachedLine = -1;
                    float lineH = Font.Height * dpiScale;

                    // 指定フォントの「キャップハイト(大文字の高さ)〜ベースライン」に相当する
                    // 行内の比率。GDI+ には正確なキャップハイトの取得手段が無いため、
                    // フォントの cell ascent(行頭からベースラインまで)を実測した行の高さに
                    // あてはめて近似する。
                    float ascentRatio = 0.8f;
                    try
                    {
                        FontFamily fam = Font.FontFamily;
                        FontStyle fstyle = Font.Style;
                        float lineSpacingUnits = fam.GetLineSpacing(fstyle);
                        float ascentUnits = fam.GetCellAscent(fstyle);
                        if (lineSpacingUnits > 0) ascentRatio = ascentUnits / lineSpacingUnits;
                    }
                    catch { }

                    // 次(または前)の文字位置から実測できない場合に使う、実フォント計測によるフォールバック幅
                    // (行の高さからの推定は等幅フォントでない場合などに誤差が大きく、
                    //  入力直後(次の文字がまだ無い)に幅が過大になる原因だったため、実測値に変更)
                    float fallbackHalf = TextRenderer.MeasureText(g, "M M", Font, new Size(short.MaxValue, short.MaxValue), TextFormatFlags.NoPadding).Width
                                       - TextRenderer.MeasureText(g, "MM", Font, new Size(short.MaxValue, short.MaxValue), TextFormatFlags.NoPadding).Width;
                    if (fallbackHalf < 2f) fallbackHalf = lineH * 0.5f;
                    float fallbackFull = TextRenderer.MeasureText(g, "M　M", Font, new Size(short.MaxValue, short.MaxValue), TextFormatFlags.NoPadding).Width
                                       - TextRenderer.MeasureText(g, "MM", Font, new Size(short.MaxValue, short.MaxValue), TextFormatFlags.NoPadding).Width;
                    if (fallbackFull < fallbackHalf) fallbackFull = fallbackHalf * 2;

                    for (int i = first; i < last; i++)
                    {
                        char c = text[i];
                        bool isSpace = (c == ' ' || c == '　');
                        string glyph = null;
                        if (!isSpace)
                        {
                            if (c == '\t') glyph = "→";
                            else if (c == '\n') glyph = "↵";
                            else continue;
                        }

                        Point p0 = GetPositionFromCharIndex(i);
                        if (p0.X < -20 || p0.Y < -20 || p0.X > client.Width + 20 || p0.Y > client.Height + 20) continue;

                        int curLine = GetLineFromCharIndex(i);
                        if (curLine != cachedLine)
                        {
                            cachedLine = curLine;
                            lineH = MeasureLineHeight(i, text, dpiScale);
                        }
                        float cy = p0.Y + lineH / 2f;

                        if (isSpace)
                        {
                            float cellW = c == '　' ? fallbackFull : fallbackHalf;
                            if (i + 1 < text.Length)
                            {
                                Point p1 = GetPositionFromCharIndex(i + 1);
                                if (p1.Y == p0.Y && p1.X > p0.X) cellW = p1.X - p0.X;
                            }
                            else if (i > 0)
                            {
                                // 末尾のスペース(入力直後など次の文字がまだ無い場合):
                                // 直前の文字が同種の空白文字ならその実測幅を使う(より正確)
                                Point pPrev = GetPositionFromCharIndex(i - 1);
                                if (pPrev.Y == p0.Y && p0.X > pPrev.X && text[i - 1] == c) cellW = p0.X - pPrev.X;
                            }

                            float markH = Math.Max(6f, lineH * ascentRatio);
                            float markTop = p0.Y;
                            if (c == ' ')
                            {
                                // 半角スペース: 縦はキャップハイト〜ベースライン、横はセル幅に応じた点線四角
                                float halfW = Math.Max(7f, Math.Min(lineH, cellW) * 0.62f);
                                float cx = p0.X + cellW / 2f;
                                g.DrawRectangle(dotPen, cx - halfW / 2f, markTop, halfW, markH);
                            }
                            else
                            {
                                // 全角スペース: セル幅いっぱい・縦はキャップハイト〜ベースラインの点線四角
                                float margin = Math.Max(1f, cellW * 0.10f);
                                float w = Math.Max(9f, cellW - margin * 2);
                                g.DrawRectangle(dotPen, p0.X + margin, markTop, w, markH);
                            }
                            continue;
                        }

                        SizeF sz = g.MeasureString(glyph, invisFont);
                        g.DrawString(glyph, invisFont, invisBrush, p0.X, cy - sz.Height / 2f);
                    }
                }
            }
            catch { }
        }

        public bool IsComposing { get { return composing; } }

        void RecordChange()
        {
            string now = ReadText();
            string old = shadow;
            int min = Math.Min(old.Length, now.Length);
            int p = 0;
            while (p < min && old[p] == now[p]) p++;
            int s = 0;
            while (s < min - p && old[old.Length - 1 - s] == now[now.Length - 1 - s]) s++;
            if (p == old.Length && p == now.Length) return;
            string removed = old.Substring(p, old.Length - p - s);
            string inserted = now.Substring(p, now.Length - p - s);
            shadow = now;
            redo.Clear();

            DateTime t = DateTime.Now;
            Edit top = undo.Count > 0 ? undo[undo.Count - 1] : null;
            if (top != null && !noMerge && top.Id != savedId && (t - top.Time).TotalMilliseconds < 1500)
            {
                // 連続入力
                if (removed.Length == 0 && inserted.Length == 1 && inserted != "\n" &&
                    top.Removed.Length == 0 && top.Pos + top.Inserted.Length == p && !top.Inserted.EndsWith("\n"))
                {
                    top.Inserted += inserted; top.Time = t; return;
                }
                // BackSpace 連打
                if (inserted.Length == 0 && removed.Length == 1 && top.Inserted.Length == 0 && p + 1 == top.Pos)
                {
                    top.Removed = removed + top.Removed; top.Pos = p; top.Time = t; return;
                }
                // Delete 連打
                if (inserted.Length == 0 && removed.Length == 1 && top.Inserted.Length == 0 && p == top.Pos)
                {
                    top.Removed += removed; top.Time = t; return;
                }
            }
            noMerge = false;
            Edit ed = new Edit();
            ed.Id = nextId++; ed.Pos = p; ed.Removed = removed; ed.Inserted = inserted; ed.Time = t;
            undo.Add(ed);
            if (undo.Count > 5000) undo.RemoveAt(0);
        }

        void ApplyReplace(int pos, int len, string text)
        {
            suppress++;
            try
            {
                Select(pos, len);
                SelectedText = text;
                shadow = ReadText();
            }
            finally { suppress--; }
        }

        public void DoUndo()
        {
            if (composing) return;
            RecordChange();
            if (undo.Count == 0) return;
            Edit e = undo[undo.Count - 1];
            undo.RemoveAt(undo.Count - 1);
            ApplyReplace(e.Pos, e.Inserted.Length, e.Removed);
            Select(e.Pos + e.Removed.Length, 0);
            redo.Add(e);
            noMerge = true;
        }

        public void DoRedo()
        {
            if (composing) return;
            RecordChange();
            if (redo.Count == 0) return;
            Edit e = redo[redo.Count - 1];
            redo.RemoveAt(redo.Count - 1);
            ApplyReplace(e.Pos, e.Removed.Length, e.Inserted);
            Select(e.Pos + e.Inserted.Length, 0);
            undo.Add(e);
            noMerge = true;
        }

        /// <summary>1回の操作として範囲を置換する(Undo 1回で戻せる)</summary>
        public void ReplaceRange(int pos, int len, string text)
        {
            RecordChange();
            noMerge = true;
            Select(pos, len);
            SelectedText = text;   // OnTextChanged で記録される
            noMerge = true;
        }

        /// <summary>全文を置き換える(スクロール位置は維持、Undo 1回で戻せる)</summary>
        public void ReplaceAll(string text, int caret)
        {
            RecordChange();
            noMerge = true;
            POINT sp = ScrollPos;
            SendMessage(Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            try
            {
                base.SelectAll();
                SelectedText = text;
                Select(Math.Min(caret, TextLength), 0);
                SendMessage(Handle, EM_SETSCROLLPOS, IntPtr.Zero, ref sp);
            }
            finally
            {
                SendMessage(Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                Invalidate();
            }
            noMerge = true;
        }

        // ---------------- クリップボード / キー ----------------

        public string SelectedPlainText
        {
            get
            {
                string s = SelectedText;
                return s.Replace("\r\n", "\n").Replace('\r', '\n');
            }
        }

        public void CopyPlain()
        {
            string s = SelectedPlainText;
            if (s.Length == 0) return;
            try { Clipboard.SetText(s.Replace("\n", "\r\n")); } catch { }
        }

        public void CutPlain()
        {
            if (SelectionLength == 0 || ReadOnly) return;
            CopyPlain();
            RecordChange();
            noMerge = true;
            SelectedText = "";
            noMerge = true;
        }

        public void PastePlain()
        {
            if (ReadOnly) return;
            string s = null;
            try { if (Clipboard.ContainsText()) s = Clipboard.GetText(); } catch { }
            if (s == null) return;
            s = s.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\0', ' ');
            RecordChange();
            noMerge = true;
            SelectedText = s;
            noMerge = true;
            ScrollToCaret();
        }

        protected override bool ProcessCmdKey(ref Message m, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.Z:
                    DoUndo(); return true;
                case Keys.Control | Keys.Y:
                case Keys.Control | Keys.Shift | Keys.Z:
                    DoRedo(); return true;
                case Keys.Control | Keys.V:
                case Keys.Shift | Keys.Insert:
                    PastePlain(); return true;
                case Keys.Control | Keys.C:
                case Keys.Control | Keys.Insert:
                    CopyPlain(); return true;
                case Keys.Control | Keys.X:
                case Keys.Shift | Keys.Delete:
                    CutPlain(); return true;
                // RichEdit の書式系ショートカットを無効化
                case Keys.Control | Keys.L:
                case Keys.Control | Keys.E:
                case Keys.Control | Keys.R:
                case Keys.Control | Keys.J:
                case Keys.Control | Keys.B:
                case Keys.Control | Keys.I:
                case Keys.Control | Keys.U:
                case Keys.Control | Keys.D1:
                case Keys.Control | Keys.D2:
                case Keys.Control | Keys.D5:
                case Keys.Control | Keys.Oemplus:
                case Keys.Control | Keys.Shift | Keys.Oemplus:
                case Keys.Control | Keys.Shift | Keys.A:
                case Keys.Control | Keys.Shift | Keys.L:
                case Keys.Control | Keys.Shift | Keys.Oemcomma:
                case Keys.Control | Keys.Shift | Keys.OemPeriod:
                    return true;
            }
            return base.ProcessCmdKey(ref m, keyData);
        }

        // ---------------- 表示位置 / ハイライト ----------------

        public POINT ScrollPos
        {
            get
            {
                POINT p = new POINT();
                if (IsHandleCreated) SendMessage(Handle, EM_GETSCROLLPOS, IntPtr.Zero, ref p);
                return p;
            }
        }

        /// <summary>縦スクロール位置の割合 0..1</summary>
        public double VerticalRatio
        {
            get
            {
                if (!IsHandleCreated) return 0;
                SCROLLINFO si = new SCROLLINFO();
                si.cbSize = Marshal.SizeOf(typeof(SCROLLINFO));
                si.fMask = 0x17;
                if (!GetScrollInfo(Handle, 1, ref si)) return 0;
                int range = si.nMax - si.nMin - (int)si.nPage + 1;
                if (range <= 0) return 0;
                double r = (double)(si.nPos - si.nMin) / range;
                return Math.Max(0, Math.Min(1, r));
            }
        }

        /// <summary>選択操作中や IME 変換中はハイライト処理を行わない</summary>
        public bool CanPaintNow
        {
            get
            {
                if (!IsHandleCreated || composing) return false;
                if (Control.MouseButtons != MouseButtons.None && Focused) return false;
                if ((Control.ModifierKeys & Keys.Shift) != 0 && Focused) return false;
                return true;
            }
        }

        /// <summary>
        /// 見えている範囲だけを塗り直す。starts/lens(検索ハイライト、昇順)に加えて、
        /// 数値/英字の自動着色(文書全体を対象、表示範囲のみ描画)も行う。
        /// </summary>
        public void PaintHighlights(List<int> starts, List<int> lens, Color hl,
            bool colorNumbers, Color numberColor, bool colorLetters, Color letterColor)
        {
            if (!IsHandleCreated) return;
            string text = shadow;
            bool active = starts.Count > 0 || colorNumbers || colorLetters;

            if (!active)
            {
                if (formatted)
                {
                    FormatBlock(delegate
                    {
                        base.SelectAll();
                        SelectionBackColor = BackColor;
                        SelectionColor = ForeColor;
                    });
                }
                formatted = false;
                return;
            }

            int first = GetCharIndexFromPosition(new Point(0, 0));
            int last = GetCharIndexFromPosition(new Point(Math.Max(0, ClientSize.Width - 1), Math.Max(0, ClientSize.Height - 1)));
            first = Math.Max(0, Math.Min(first, text.Length));
            last = Math.Max(first, Math.Min(last, text.Length));
            int from = first > 0 ? text.LastIndexOf('\n', first - 1) + 1 : 0;
            int to = last < text.Length ? text.IndexOf('\n', last) : text.Length;
            if (to < 0) to = text.Length;
            if (to - from > 4000000) to = from + 4000000;

            // 範囲に重なる最初の検索ハイライトを二分探索
            int lo = 0, hi = starts.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (starts[mid] + lens[mid] <= from) lo = mid + 1; else hi = mid;
            }
            int startIdx = lo;

            FormatBlock(delegate
            {
                Select(from, to - from);
                SelectionBackColor = BackColor;
                SelectionColor = ForeColor;

                if (colorNumbers || colorLetters)
                {
                    int i = from;
                    while (i < to)
                    {
                        char c = text[i];
                        bool isDigit = colorNumbers && char.IsDigit(c);
                        bool isLetter = !isDigit && colorLetters && ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'));
                        if (isDigit || isLetter)
                        {
                            int j = i + 1;
                            if (isDigit) while (j < to && char.IsDigit(text[j])) j++;
                            else while (j < to && ((text[j] >= 'A' && text[j] <= 'Z') || (text[j] >= 'a' && text[j] <= 'z'))) j++;
                            Select(i, j - i);
                            SelectionColor = isDigit ? numberColor : letterColor;
                            i = j;
                        }
                        else i++;
                    }
                }

                for (int k = startIdx; k < starts.Count && starts[k] < to; k++)
                {
                    Select(starts[k], lens[k]);
                    SelectionBackColor = hl;
                }
            });
            formatted = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (invisFont != null) invisFont.Dispose();
                if (invisBrush != null) invisBrush.Dispose();
            }
            base.Dispose(disposing);
        }

        void FormatBlock(MethodInvoker body)
        {
            int ss = SelectionStart, sl = SelectionLength;
            POINT sp = ScrollPos;
            suppress++;
            IntPtr mask = SendMessage(Handle, EM_GETEVENTMASK, IntPtr.Zero, IntPtr.Zero);
            SendMessage(Handle, EM_SETEVENTMASK, IntPtr.Zero, IntPtr.Zero);
            SendMessage(Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            try
            {
                body();
                Select(ss, sl);
                if (sl == 0) { SelectionBackColor = BackColor; SelectionColor = ForeColor; }   // 入力位置の書式を戻す
                SendMessage(Handle, EM_SETSCROLLPOS, IntPtr.Zero, ref sp);
            }
            finally
            {
                SendMessage(Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                SendMessage(Handle, EM_SETEVENTMASK, IntPtr.Zero, mask);
                suppress--;
                Invalidate();
            }
        }
    }
}
