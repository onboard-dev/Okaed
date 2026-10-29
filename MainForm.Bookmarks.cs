using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

namespace Okaed
{
    /// <summary>
    /// インスタントブックマーク(Ctrl+1～Ctrl+9)
    /// ・未設定の番号 → 現在の行に設定
    /// ・同じ行で同じ番号 → 解除
    /// ・別の行に設定済みの番号 → その行へ移動
    /// ファイルに対するブックマークは okaed.ini に「Bookmark.<パスのハッシュ>=1:12,3:40|<フルパス>」の形で保存する。
    /// </summary>
    public partial class MainForm
    {
        const string BookmarkKeyPrefix = "Bookmark.";
        const int BookmarkCount = 9;

        // 各文書のブックマーク行(論理行・0始まり、-1 は未設定)は Doc.BmLines にある(bmLines は表示中の文書の分)
        readonly int[] bmStarts = new int[BookmarkCount + 1];   // 各ブックマーク行の先頭文字位置(キャッシュ)
        bool bmStartsValid;
        BookmarkGutter bmGutter;

        void InitBookmarks()
        {
            bmGutter = new BookmarkGutter(this);
            bmGutter.Dock = DockStyle.Left;
            bmGutter.Width = S(26);
            bmGutter.Visible = false;
        }

        /// <summary>エディタ(タブごとに 1 つ)のイベントをブックマーク処理に結びつける</summary>
        void WireBookmarkEvents(EditorBox ed)
        {
            ed.BookmarkKey += OnBookmarkKey;
            ed.Edited += OnEditedForBookmarks;
            ed.VScroll += delegate { if (bmGutter.Visible) bmGutter.Invalidate(); };
            ed.Resize += delegate { bmStartsValid = false; if (bmGutter.Visible) bmGutter.Invalidate(); };
        }

        bool HasBookmarks
        {
            get { for (int n = 1; n <= BookmarkCount; n++) if (bmLines[n] >= 0) return true; return false; }
        }

        /// <summary>開いているすべてのタブのブックマークの数</summary>
        int CountBookmarks()
        {
            int c = 0;
            foreach (Doc d in docs)
                for (int n = 1; n <= BookmarkCount; n++) if (d.BmLines[n] >= 0) c++;
            return c;
        }

        void BookmarksChanged()
        {
            bmStartsValid = false;
            bool show = HasBookmarks;
            if (bmGutter.Visible != show) bmGutter.Visible = show;
            bmGutter.Invalidate();
        }

        // ---------------- キー操作 ----------------

        void OnBookmarkKey(int n)
        {
            string text = editor.PlainText;
            int caret = Math.Min(editor.SelectionStart, text.Length);
            int cur = CountNewlines(text, 0, caret);

            if (bmLines[n] == cur)
            {
                bmLines[n] = -1;
                BookmarksChanged();
                if (filePath != null) PersistBookmarks(filePath);
                Msg("ブックマーク " + n + " を解除しました");
            }
            else if (bmLines[n] >= 0)
            {
                JumpToBookmark(n);
            }
            else
            {
                bmLines[n] = cur;
                BookmarksChanged();
                if (filePath != null) PersistBookmarks(filePath);
                Msg("ブックマーク " + n + " を設定しました(" + (cur + 1) + " 行目)"
                    + (filePath == null ? " ※ファイルを保存すると記録されます" : ""));
            }
        }

        void JumpToBookmark(int n)
        {
            if (bmLines[n] < 0) return;
            EnsureBookmarkStarts();
            editor.Select(bmStarts[n], 0);
            editor.ScrollToCaret();
            editor.Focus();
            Msg("ブックマーク " + n + " へ移動しました(" + (bmLines[n] + 1) + " 行目)");
        }

        // ---------------- 編集に合わせて行番号を追従 ----------------

        void OnEditedForBookmarks(int pos, string removed, string inserted)
        {
            bmStartsValid = false;
            if (!HasBookmarks) return;
            int rNL = CountNewlines(removed, 0, removed.Length);
            int iNL = CountNewlines(inserted, 0, inserted.Length);
            if (rNL == 0 && iNL == 0) return;

            string text = editor.PlainText;
            pos = Math.Max(0, Math.Min(pos, text.Length));
            int L = CountNewlines(text, 0, pos);
            bool atLineStart = pos == 0 || text[pos - 1] == '\n';

            for (int n = 1; n <= BookmarkCount; n++)
            {
                int b = bmLines[n];
                if (b < 0 || b < L) continue;
                if (b == L)
                {
                    // 行頭で改行を挿入した場合は、行の内容と一緒に下へ移動する
                    if (atLineStart && rNL == 0) b += iNL;
                }
                else if (b <= L + rNL) b = L + iNL;     // 削除・結合された行 → 編集後に続く行へ
                else b += iNL - rNL;
                bmLines[n] = Math.Max(0, b);
            }
            bmGutter.Invalidate();
        }

        static int CountNewlines(string s, int from, int to)
        {
            int c = 0;
            for (int i = from; i < to; i++) if (s[i] == '\n') c++;
            return c;
        }

        /// <summary>各ブックマーク行の先頭文字位置を 1 回の走査で求める(行数を超えるものは最終行に丸める)</summary>
        void EnsureBookmarkStarts()
        {
            if (bmStartsValid) return;
            string text = editor.PlainText;
            int maxLine = -1;
            for (int n = 1; n <= BookmarkCount; n++) maxLine = Math.Max(maxLine, bmLines[n]);
            if (maxLine >= 0)
            {
                // lineStart[k] を順に求めながら、該当する番号に割り当てる
                int line = 0, start = 0, i = 0;
                while (true)
                {
                    for (int n = 1; n <= BookmarkCount; n++) if (bmLines[n] == line) bmStarts[n] = start;
                    if (line >= maxLine) break;
                    int nl = text.IndexOf('\n', i);
                    if (nl < 0)
                    {
                        // 行数を超えたブックマークは最終行へ
                        for (int n = 1; n <= BookmarkCount; n++) if (bmLines[n] > line) { bmLines[n] = line; bmStarts[n] = start; }
                        break;
                    }
                    line++;
                    start = nl + 1;
                    i = start;
                }
            }
            bmStartsValid = true;
        }

        // ---------------- okaed.ini への保存 / 読み込み ----------------

        static string BookmarkKeyFor(string path)
        {
            // FNV-1a 64bit(大文字小文字を区別しないパス)。キーに '=' などを含めないためハッシュにする
            ulong h = 14695981039346656037UL;
            foreach (char c in path.ToUpperInvariant()) { h ^= c; h *= 1099511628211UL; }
            return BookmarkKeyPrefix + h.ToString("X16", CultureInfo.InvariantCulture);
        }

        void PersistBookmarks(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            string key = BookmarkKeyFor(path);
            if (!HasBookmarks)
            {
                settings.Remove(key);
            }
            else
            {
                EnsureBookmarkStarts();   // 行数を超えたものを丸める
                StringBuilder sb = new StringBuilder();
                for (int n = 1; n <= BookmarkCount; n++)
                {
                    if (bmLines[n] < 0) continue;
                    if (sb.Length > 0) sb.Append(',');
                    sb.Append(n).Append(':').Append((bmLines[n] + 1).ToString(CultureInfo.InvariantCulture));
                }
                sb.Append('|').Append(path);
                settings.Set(key, sb.ToString());
            }
            settings.Save();
        }

        void LoadBookmarksFor(string path)
        {
            for (int n = 1; n <= BookmarkCount; n++) bmLines[n] = -1;
            string v = settings.Get(BookmarkKeyFor(path), null);
            if (!string.IsNullOrEmpty(v))
            {
                int bar = v.IndexOf('|');
                string list = bar >= 0 ? v.Substring(0, bar) : v;
                string savedPath = bar >= 0 ? v.Substring(bar + 1) : null;
                if (savedPath == null || string.Equals(savedPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    int lastLine = CountNewlines(editor.PlainText, 0, editor.PlainText.Length);
                    foreach (string item in list.Split(','))
                    {
                        int colon = item.IndexOf(':');
                        int n, line;
                        if (colon <= 0) continue;
                        if (!int.TryParse(item.Substring(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) continue;
                        if (!int.TryParse(item.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out line)) continue;
                        if (n < 1 || n > BookmarkCount || line < 1) continue;
                        bmLines[n] = Math.Min(line - 1, lastLine);
                    }
                }
            }
            BookmarksChanged();
        }

        void ClearBookmarks()
        {
            for (int n = 1; n <= BookmarkCount; n++) bmLines[n] = -1;
            BookmarksChanged();
        }

        /// <summary>編集メニュー「すべてのブックマークを削除」: okaed.ini に保存された全ファイル分と、表示中のものを削除</summary>
        void DeleteAllBookmarks()
        {
            List<string> keys = settings.KeysWithPrefix(BookmarkKeyPrefix);
            int current = CountBookmarks();
            if (keys.Count == 0 && current == 0)
            {
                MessageBox.Show(this, "ブックマークはありません。", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            string detail = keys.Count > 0 ? "\n(保存されているブックマーク: " + keys.Count + " ファイル分)" : "";
            if (MessageBox.Show(this, "すべてのブックマークを削除します。よろしいですか?" + detail, AppName,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            foreach (string k in keys) settings.Remove(k);
            settings.Save();
            foreach (Doc d in docs)
                for (int n = 1; n <= BookmarkCount; n++) d.BmLines[n] = -1;
            BookmarksChanged();
            Msg("すべてのブックマークを削除しました");
        }

        // ---------------- 左側の表示欄 ----------------

        static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        /// <summary>エディタ左側にブックマーク番号(1️⃣～9️⃣ 風のキーキャップ)を描く欄。クリックでその行へ移動</summary>
        class BookmarkGutter : Control
        {
            readonly MainForm owner;
            static readonly Color KeycapColor = Color.FromArgb(59, 136, 195);

            public BookmarkGutter(MainForm owner)
            {
                this.owner = owner;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                Cursor = Cursors.Hand;
            }

            /// <summary>各ブックマークの描画位置(行の上端 y と行の高さ)を求める</summary>
            int visFirst, visLast;
            Font keycapFont;

            void UpdateVisibleRange()
            {
                EditorBox ed = owner.editor;
                visFirst = ed.GetCharIndexFromPosition(new Point(0, 0));
                if (visFirst > 0) { int nl = ed.PlainText.LastIndexOf('\n', visFirst - 1); visFirst = nl + 1; }
                visLast = ed.GetCharIndexFromPosition(new Point(Math.Max(0, ed.ClientSize.Width - 1), Math.Max(0, ed.ClientSize.Height - 1)));
                if (visLast >= ed.PlainText.Length - 1) visLast = int.MaxValue;   // 末尾の空行も含める
            }

            bool TryGetLineTop(int n, out int y, out int lineH)
            {
                EditorBox ed = owner.editor;
                lineH = Math.Max(8, ed.Font.Height);
                y = 0;
                if (owner.bmLines[n] < 0) return false;
                owner.EnsureBookmarkStarts();
                int idx = owner.bmStarts[n];
                int len = ed.PlainText.Length;
                if (len == 0) { y = 1; return true; }
                // 画面外の位置を問い合わせると RichEdit が文書全体を組版し直して遅くなるため、見えている範囲だけ調べる
                if (idx < visFirst || idx > visLast) return false;
                if (idx < len) { y = ed.GetPositionFromCharIndex(idx).Y; return true; }
                // 末尾の空行(最後の改行の直後)
                y = ed.GetPositionFromCharIndex(len - 1).Y + lineH;
                return true;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                EditorBox ed = owner.editor;
                Graphics g = e.Graphics;
                using (SolidBrush bg = new SolidBrush(Blend(ed.BackColor, ed.ForeColor, 0.06f)))
                    g.FillRectangle(bg, ClientRectangle);
                using (Pen border = new Pen(Blend(ed.BackColor, ed.ForeColor, 0.18f)))
                    g.DrawLine(border, Width - 1, 0, Width - 1, Height);

                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                UpdateVisibleRange();
                // 同じ行に複数ある場合は少しずらして重ねる
                Dictionary<int, int> stackOnY = new Dictionary<int, int>();
                for (int n = 1; n <= BookmarkCount; n++)
                {
                    int y, lineH;
                    if (!TryGetLineTop(n, out y, out lineH)) continue;
                    if (y + lineH < 0 || y > Height) continue;
                    int stacked;
                    stackOnY.TryGetValue(y, out stacked);
                    stackOnY[y] = stacked + 1;

                    int sz = Math.Max(10, Math.Min(lineH - 2, owner.S(20)));
                    float x = (Width - 1 - sz) / 2f + stacked * owner.S(3);
                    float top = y + (lineH - sz) / 2f + stacked * owner.S(2);
                    if (keycapFont == null || Math.Abs(keycapFont.Size - sz * 0.68f) > 0.01f)
                    {
                        if (keycapFont != null) keycapFont.Dispose();
                        keycapFont = new Font("Segoe UI", sz * 0.68f, FontStyle.Bold, GraphicsUnit.Pixel);
                    }
                    DrawKeycap(g, n, new RectangleF(x, top, sz, sz), keycapFont);
                }
            }

            static void DrawKeycap(Graphics g, int n, RectangleF r, Font f)
            {
                float rad = r.Width / 4.5f;
                using (GraphicsPath path = new GraphicsPath())
                {
                    path.AddArc(r.X, r.Y, rad * 2, rad * 2, 180, 90);
                    path.AddArc(r.Right - rad * 2, r.Y, rad * 2, rad * 2, 270, 90);
                    path.AddArc(r.Right - rad * 2, r.Bottom - rad * 2, rad * 2, rad * 2, 0, 90);
                    path.AddArc(r.X, r.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);
                    path.CloseFigure();
                    using (SolidBrush b = new SolidBrush(KeycapColor)) g.FillPath(b, path);
                    using (Pen p = new Pen(Color.FromArgb(90, 255, 255, 255), 1f)) g.DrawPath(p, path);
                }
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(n.ToString(CultureInfo.InvariantCulture), f, Brushes.White, new RectangleF(r.X, r.Y + r.Height * 0.03f, r.Width, r.Height), sf);
                }
            }

            protected override void OnMouseClick(MouseEventArgs e)
            {
                base.OnMouseClick(e);
                UpdateVisibleRange();
                for (int n = 1; n <= BookmarkCount; n++)
                {
                    int y, lineH;
                    if (!TryGetLineTop(n, out y, out lineH)) continue;
                    if (e.Y >= y && e.Y < y + lineH) { owner.JumpToBookmark(n); return; }
                }
            }
        }
    }
}
