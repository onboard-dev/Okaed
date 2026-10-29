using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Okaed
{
    /// <summary>
    /// 複数文書のタブ編集。文書(タブ)ごとに EditorBox を 1 つ持ち、表示中のものだけを Visible にする。
    /// これまでの単一文書用のコードが使う editor / filePath / fileEnc / eol などは、
    /// 「いま表示中の文書」を指すプロパティとして残してある。
    /// </summary>
    public partial class MainForm
    {
        /// <summary>1 つのタブ(文書)が持つ状態</summary>
        class Doc
        {
            public EditorBox Editor;
            public string FilePath;
            public FileEnc Enc = FileEnc.Utf8Bom;
            public string Eol = "\r\n";
            public bool MetaDirty;                       // 文字コード/改行の変更
            public readonly int[] BmLines = new int[BookmarkCount + 1];
            public string AutosavePath;
            public string AutosaveLastText;
            public bool PreviewOn;
            public int UntitledNo;
            public TabBar.TabItem Tab;

            public Doc()
            {
                for (int i = 0; i < BmLines.Length; i++) BmLines[i] = -1;
            }

            public bool IsDirty { get { return Editor.IsDirty || MetaDirty; } }

            public string Title
            {
                get
                {
                    if (FilePath != null) return Path.GetFileName(FilePath);
                    return UntitledNo > 1 ? "無題 " + UntitledNo : "無題";
                }
            }
        }

        readonly List<Doc> docs = new List<Doc>();
        Doc cur;                                   // いま表示中の文書

        // ---- 表示中の文書の状態(従来のフィールド名のまま使えるようにするプロパティ)----
        EditorBox editor { get { return cur == null ? null : cur.Editor; } }
        string filePath { get { return cur.FilePath; } set { cur.FilePath = value; } }
        FileEnc fileEnc { get { return cur.Enc; } set { cur.Enc = value; } }
        string eol { get { return cur.Eol; } set { cur.Eol = value; } }
        bool metaDirty { get { return cur.MetaDirty; } set { cur.MetaDirty = value; } }
        string autosavePath { get { return cur.AutosavePath; } set { cur.AutosavePath = value; } }
        string autosaveLastText { get { return cur.AutosaveLastText; } set { cur.AutosaveLastText = value; } }
        int[] bmLines { get { return cur.BmLines; } }

        // ======================= 初期化 =======================

        /// <summary>タブの帯とそのメニューを作る(BuildUi から呼ぶ)</summary>
        void InitTabs()
        {
            tabBar = new TabBar(scale);
            tabBar.Dock = DockStyle.Top;
            tabBar.AllowDrop = true;
            tabBar.DragEnter += OnDragEnter;
            tabBar.DragDrop += OnDragDrop;
            tabBar.TabSelected += delegate (int i) { if (i >= 0 && i < docs.Count) SwitchTo(docs[i]); };
            tabBar.TabCloseRequested += delegate (int i) { if (i >= 0 && i < docs.Count) CloseDoc(docs[i]); };
            tabBar.NewTabRequested += delegate { NewTab(); };
            tabBar.TabMoved += delegate (int src, int dst)
            {
                Doc d = docs[src];
                docs.RemoveAt(src);
                docs.Insert(dst, d);
            };

            ContextMenuStrip cm = new ContextMenuStrip();
            ToolStripMenuItem miClose = new ToolStripMenuItem("閉じる", null, delegate { CloseDoc(cur); });
            miClose.ShortcutKeyDisplayString = "Ctrl+W";
            ToolStripMenuItem miOthers = new ToolStripMenuItem("ほかのタブをすべて閉じる", null, delegate { CloseOthers(cur); });
            ToolStripMenuItem miCopyPath = new ToolStripMenuItem("ファイルのパスをコピー", null, delegate
            {
                if (cur.FilePath != null) { try { Clipboard.SetText(cur.FilePath); } catch { } }
            });
            cm.Items.AddRange(new ToolStripItem[] { miClose, miOthers, new ToolStripSeparator(), miCopyPath });
            cm.Opening += delegate (object s, System.ComponentModel.CancelEventArgs e)
            {
                // タブの上以外(空き領域)ではメニューを出さない
                if (tabBar.HitTest(tabBar.PointToClient(Control.MousePosition)) < 0) { e.Cancel = true; return; }
                miOthers.Enabled = docs.Count > 1;
                miCopyPath.Enabled = cur.FilePath != null;
            };
            tabBar.ContextMenuStrip = cm;
        }

        /// <summary>最初の文書(起動時の 1 つ目のタブ)を作る。BuildUi の最後に呼ぶ</summary>
        void InitFirstDoc()
        {
            Doc d = CreateDoc();
            cur = d;
            d.Editor.Visible = true;
            tabBar.SelectedIndex = 0;
        }

        // ======================= 文書の追加・切り替え・閉じる =======================

        int NextUntitledNo()
        {
            for (int n = 1; ; n++)
            {
                bool used = false;
                foreach (Doc x in docs)
                    if (x.FilePath == null && x.UntitledNo == n) { used = true; break; }
                if (!used) return n;
            }
        }

        /// <summary>エディタとタブを作って一覧に加える(まだ表示はしない)</summary>
        Doc CreateDoc()
        {
            Doc d = new Doc();
            d.UntitledNo = NextUntitledNo();

            EditorBox ed = new EditorBox();
            ed.Dock = DockStyle.Fill;
            ed.AllowDrop = true;
            ed.Visible = false;
            ed.TextChanged += delegate { OnEditorTextChanged(); };
            ed.SelectionChanged += delegate { UpdatePos(); };
            ed.DragEnter += OnDragEnter;
            ed.DragDrop += OnDragDrop;
            ed.ContextMenuStrip = editorMenu;
            ed.TripleClick += delegate { ResetWindowToDefaultSize(); };
            WireBookmarkEvents(ed);
            d.Editor = ed;

            // Dock は子コントロールの並びが後ろのものほど先に配置される。
            // 左端のブックマーク欄(Left)を先に配置させるため、Fill のエディタは先頭側に置く
            split.Panel1.Controls.Add(ed);
            split.Panel1.Controls.SetChildIndex(ed, 0);

            // 表示中の文書と同じ見た目(フォント・折り返し・色・空白可視化)にそろえる
            if (cur != null)
            {
                EditorBox src = cur.Editor;
                ed.BackColor = src.BackColor;
                ed.ForeColor = src.ForeColor;
                ed.InvisibleColor = src.InvisibleColor;
                ed.WrapEnabled = src.WrapEnabled;
                ed.ShowInvisibles = src.ShowInvisibles;
                ed.SetEditorFont(src.Font);
            }

            d.Tab = new TabBar.TabItem();
            d.Tab.Tag = d;
            d.Tab.Title = d.Title;
            docs.Add(d);
            tabBar.AddTab(d.Tab);
            UpdateTabBarVisible();
            return d;
        }

        /// <summary>新しい空のタブを作って、それを表示する</summary>
        Doc AddDoc()
        {
            Doc d = CreateDoc();
            SwitchTo(d);
            return d;
        }

        /// <summary>新規(Ctrl+N): 空の無題タブを開く</summary>
        void NewTab()
        {
            AddDoc();
            NewFile();
        }

        void SwitchTo(Doc d)
        {
            if (d == null) return;
            if (d == cur) { d.Editor.Focus(); return; }

            Doc old = cur;
            if (old != null) old.PreviewOn = miPreview.Checked;
            cur = d;
            d.Editor.Visible = true;
            if (old != null) old.Editor.Visible = false;
            tabBar.SelectedIndex = docs.IndexOf(d);

            // 画面まわりを、切り替え先の文書に合わせて更新する
            bmStartsValid = false;
            BookmarksChanged();
            UpdateEncUi();
            regexKey = null;
            lastScroll.X = -1; lastScroll.Y = -1; lastClient = Size.Empty;
            needPaint = true;
            lastHtml = null;
            lastRatio = -1;
            SetPreview(d.PreviewOn);
            RecomputeMatches();
            UpdatePos();
            UpdateTitle(true);
            Msg("");
            d.Editor.Focus();
        }

        void SelectNextTab(int dir)
        {
            if (docs.Count < 2) return;
            int i = docs.IndexOf(cur) + dir;
            if (i < 0) i = docs.Count - 1;
            else if (i >= docs.Count) i = 0;
            SwitchTo(docs[i]);
        }

        Doc FindDocByPath(string fullPath)
        {
            foreach (Doc d in docs)
                if (d.FilePath != null && string.Equals(d.FilePath, fullPath, StringComparison.OrdinalIgnoreCase)) return d;
            return null;
        }

        /// <summary>何も入力されていない無題のタブか(ファイルを開くとき、このタブを使い回す)</summary>
        static bool IsPristineUntitled(Doc d)
        {
            return d.FilePath == null && !d.IsDirty && d.Editor.PlainText.Length == 0;
        }

        /// <summary>タブを閉じる。未保存なら確認する。キャンセルされたら false</summary>
        bool CloseDoc(Doc d)
        {
            if (d == null) return false;
            if (!ConfirmClose(d)) return false;
            int idx = docs.IndexOf(d);
            if (idx < 0) return true;

            if (docs.Count == 1)
            {
                // 最後のタブは、空の無題タブに置き換える(ウィンドウは閉じない)
                if (IsPristineUntitled(d)) return true;
                AddDoc();
                NewFile();
            }
            else if (d == cur)
            {
                SwitchTo(docs[idx + 1 < docs.Count ? idx + 1 : idx - 1]);
            }
            RemoveDoc(d);
            return true;
        }

        void CloseOthers(Doc keep)
        {
            foreach (Doc d in docs.ToArray())
            {
                if (d == keep) continue;
                if (!CloseDoc(d)) return;
            }
        }

        void RemoveDoc(Doc d)
        {
            DeleteAutosaveFile(d);
            int idx = docs.IndexOf(d);
            if (idx < 0) return;
            docs.RemoveAt(idx);
            tabBar.RemoveTab(idx);
            split.Panel1.Controls.Remove(d.Editor);
            d.Editor.Dispose();
            tabBar.SelectedIndex = docs.IndexOf(cur);
            UpdateTabBarVisible();
            RefreshTabs();
        }

        /// <summary>タブが 1 枚だけのときはタブの帯を隠して、編集領域を広く使う</summary>
        void UpdateTabBarVisible()
        {
            // Visible の読み取りはウィンドウ表示前は常に false を返すので、比較せずに毎回設定する
            tabBar.Visible = docs.Count > 1;
        }

        /// <summary>タブの表示(名前・未保存マーク・ヒント)を、各文書の状態に合わせる</summary>
        void RefreshTabs()
        {
            if (tabBar == null) return;
            bool changed = false;
            foreach (Doc d in docs)
            {
                TabBar.TabItem t = d.Tab;
                string title = d.Title;
                bool dirty = d.IsDirty;
                string tip = d.FilePath != null ? d.FilePath : title;
                if (t.Title != title || t.Dirty != dirty || t.Tip != tip)
                {
                    t.Title = title; t.Dirty = dirty; t.Tip = tip;
                    changed = true;
                }
            }
            if (changed) tabBar.Invalidate();
        }
    }
}
