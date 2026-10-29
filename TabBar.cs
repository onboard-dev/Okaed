using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Okaed
{
    /// <summary>
    /// 文書タブの帯。ライト/ダークのテーマ色に合わせて自前で描画する(フォーカスは取らない)。
    /// ・クリックで切り替え、×(または中ボタン)で閉じる、ドラッグで並べ替え、右端の + で新規タブ
    /// ・未保存のタブは × の位置に ● を表示
    /// ・タブが多くて入りきらないときはマウスホイールで横にスクロール
    /// </summary>
    public class TabBar : Control
    {
        public class TabItem
        {
            public string Title = "";
            public string Tip = "";
            public bool Dirty;
            public object Tag;
        }

        public readonly List<TabItem> Tabs = new List<TabItem>();

        public event Action<int> TabSelected;          // ユーザーがタブをクリックした(選択の反映は呼び出し側が SelectedIndex で行う)
        public event Action<int> TabCloseRequested;    // × または中クリック
        public event Action NewTabRequested;           // + または空き領域のダブルクリック
        public event Action<int, int> TabMoved;        // ドラッグで並べ替えた(移動元, 移動先)

        readonly float scale;
        readonly ToolTip tip = new ToolTip();
        string tipShown = "";

        int selected = -1;
        int scrollX;
        int hot = -1;
        bool hotClose, hotPlus;
        int pressIndex = -1;
        bool pressClose, pressPlus, dragging;
        Point pressPt;

        Color barBack = Color.FromArgb(225, 225, 225);
        Color tabBack = Color.FromArgb(238, 238, 238);
        Color activeBack = Color.White;
        Color fore = Color.Black;
        Color border = Color.FromArgb(200, 200, 200);
        static readonly Color Accent = Color.FromArgb(59, 136, 195);

        public TabBar(float scale)
        {
            this.scale = scale;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            Font = SystemFonts.MessageBoxFont;
            Height = S(30);
        }

        int S(int px) { return (int)Math.Round(px * scale); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tip.Dispose();
            base.Dispose(disposing);
        }

        // ---------------- 公開 API ----------------

        public int SelectedIndex
        {
            get { return selected; }
            set
            {
                if (value >= Tabs.Count) value = Tabs.Count - 1;
                selected = value;
                EnsureVisible(selected);
                Invalidate();
            }
        }

        public void AddTab(TabItem t)
        {
            Tabs.Add(t);
            ClampScroll();
            Invalidate();
        }

        public void RemoveTab(int index)
        {
            if (index < 0 || index >= Tabs.Count) return;
            Tabs.RemoveAt(index);
            if (index < selected) selected--;
            else if (index == selected) selected = Math.Min(selected, Tabs.Count - 1);
            hot = -1;
            ClampScroll();
            EnsureVisible(selected);
            Invalidate();
        }

        /// <summary>テーマ色(エディタの背景色・文字色)に合わせて帯の配色を決める</summary>
        public void SetColors(Color editorBack, Color editorFore)
        {
            activeBack = editorBack;
            fore = editorFore;
            barBack = Blend(editorBack, editorFore, 0.10f);
            tabBack = Blend(editorBack, editorFore, 0.05f);
            border = Blend(editorBack, editorFore, 0.20f);
            Invalidate();
        }

        /// <summary>クライアント座標 p の下にあるタブの番号(なければ -1)</summary>
        public int HitTest(Point p)
        {
            bool c, pl;
            return HitTab(p, out c, out pl);
        }

        // ---------------- 配置の計算 ----------------

        int PlusW { get { return S(32); } }
        int Avail { get { return Math.Max(1, Width - PlusW); } }

        int TabW
        {
            get
            {
                if (Tabs.Count == 0) return S(210);
                int w = Avail / Tabs.Count;
                return Math.Max(S(96), Math.Min(S(210), w));
            }
        }

        int TotalW { get { return TabW * Tabs.Count; } }

        void ClampScroll()
        {
            int max = Math.Max(0, TotalW - Avail);
            scrollX = Math.Max(0, Math.Min(max, scrollX));
        }

        void EnsureVisible(int i)
        {
            if (i < 0 || i >= Tabs.Count) return;
            int l = i * TabW, r = l + TabW;
            if (l - scrollX < 0) scrollX = l;
            else if (r - scrollX > Avail) scrollX = r - Avail;
            ClampScroll();
        }

        Rectangle TabRect(int i)
        {
            return new Rectangle(i * TabW - scrollX, S(3), TabW, Height - S(3));
        }

        Rectangle CloseRect(Rectangle t)
        {
            int sz = S(18);
            return new Rectangle(t.Right - sz - S(6), t.Y + (t.Height - sz) / 2, sz, sz);
        }

        Rectangle PlusRect()
        {
            int x = Math.Min(TotalW - scrollX, Avail);
            int sz = S(24);
            return new Rectangle(x + S(4), (Height - sz) / 2, sz, sz);
        }

        int HitTab(Point p, out bool onClose, out bool onPlus)
        {
            onClose = false; onPlus = false;
            if (PlusRect().Contains(p)) { onPlus = true; return -1; }
            if (p.X >= Avail) return -1;
            for (int i = 0; i < Tabs.Count; i++)
            {
                Rectangle r = TabRect(i);
                if (r.Contains(p))
                {
                    onClose = CloseRect(r).Contains(p);
                    return i;
                }
            }
            return -1;
        }

        // ---------------- 描画 ----------------

        static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(barBack);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen bp = new Pen(border)) g.DrawLine(bp, 0, Height - 1, Width, Height - 1);

            g.SetClip(new Rectangle(0, 0, Avail, Height));
            for (int i = 0; i < Tabs.Count; i++)
            {
                Rectangle r = TabRect(i);
                if (r.Right < 0 || r.Left > Avail) continue;
                DrawTab(g, i, r);
            }
            g.ResetClip();
            DrawPlus(g);
        }

        void DrawTab(Graphics g, int i, Rectangle r)
        {
            TabItem t = Tabs[i];
            bool act = i == selected;
            bool isHot = i == hot;
            Color bg = act ? activeBack : (isHot ? Blend(tabBack, activeBack, 0.5f) : tabBack);

            Rectangle body = new Rectangle(r.X, r.Y, r.Width - 1, act ? r.Height : r.Height - 1);
            using (SolidBrush b = new SolidBrush(bg)) g.FillRectangle(b, body);
            if (act)
            {
                using (SolidBrush ab = new SolidBrush(Accent)) g.FillRectangle(ab, body.X, body.Y, body.Width, S(2));
            }
            else if (i + 1 != selected)
            {
                using (Pen sp = new Pen(border)) g.DrawLine(sp, r.Right - 1, r.Y + S(6), r.Right - 1, r.Bottom - S(6));
            }

            Rectangle cr = CloseRect(r);
            Color tc = act ? fore : Blend(fore, bg, 0.35f);
            Rectangle textR = new Rectangle(r.X + S(10), r.Y, Math.Max(1, cr.X - r.X - S(12)), r.Height);
            TextRenderer.DrawText(g, t.Title, Font, textR, tc,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

            bool overClose = isHot && hotClose;
            if (t.Dirty && !overClose)
            {
                int d = S(8);
                using (SolidBrush db = new SolidBrush(tc))
                    g.FillEllipse(db, cr.X + (cr.Width - d) / 2f, cr.Y + (cr.Height - d) / 2f, d, d);
            }
            else if (act || isHot)
            {
                if (overClose)
                    using (SolidBrush hb = new SolidBrush(Blend(bg, fore, 0.18f))) g.FillEllipse(hb, cr);
                int m = S(5);
                using (Pen xp = new Pen(tc, Math.Max(1.2f, scale * 1.2f)))
                {
                    g.DrawLine(xp, cr.X + m, cr.Y + m, cr.Right - m, cr.Bottom - m);
                    g.DrawLine(xp, cr.Right - m, cr.Y + m, cr.X + m, cr.Bottom - m);
                }
            }
        }

        void DrawPlus(Graphics g)
        {
            Rectangle pr = PlusRect();
            Color c = Blend(fore, barBack, 0.25f);
            if (hotPlus)
                using (SolidBrush hb = new SolidBrush(Blend(barBack, fore, 0.15f))) g.FillEllipse(hb, pr);
            int m = S(7);
            int cx = pr.X + pr.Width / 2, cy = pr.Y + pr.Height / 2;
            using (Pen p = new Pen(c, Math.Max(1.4f, scale * 1.4f)))
            {
                g.DrawLine(p, cx - m + S(1), cy, cx + m - S(1), cy);
                g.DrawLine(p, cx, cy - m + S(1), cx, cy + m - S(1));
            }
        }

        // ---------------- マウス ----------------

        void UpdateHot(Point p)
        {
            bool c, pl;
            int i = HitTab(p, out c, out pl);
            if (i != hot || c != hotClose || pl != hotPlus)
            {
                hot = i; hotClose = c; hotPlus = pl;
                Invalidate();
            }
            string text = "";
            if (pl) text = "新しいタブ (Ctrl+N)";
            else if (i >= 0) text = c ? "閉じる (Ctrl+W)" : Tabs[i].Tip;
            if (text != tipShown) { tipShown = text; tip.SetToolTip(this, text); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (pressIndex >= 0 && !pressClose && (e.Button & MouseButtons.Left) != 0)
            {
                if (!dragging && Math.Abs(e.X - pressPt.X) > S(6)) dragging = true;
                if (dragging)
                {
                    int target = Math.Max(0, Math.Min(Tabs.Count - 1, (e.X + scrollX) / Math.Max(1, TabW)));
                    if (target != pressIndex)
                    {
                        MoveTab(pressIndex, target);
                        pressIndex = target;
                        hot = target;
                    }
                    return;
                }
            }
            UpdateHot(e.Location);
        }

        void MoveTab(int src, int dst)
        {
            TabItem t = Tabs[src];
            Tabs.RemoveAt(src);
            Tabs.Insert(dst, t);
            if (selected == src) selected = dst;
            else if (src < selected && dst >= selected) selected--;
            else if (src > selected && dst <= selected) selected++;
            Action<int, int> h = TabMoved;
            if (h != null) h(src, dst);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            bool onClose, onPlus;
            int i = HitTab(e.Location, out onClose, out onPlus);
            pressIndex = -1; pressClose = false; pressPlus = false; dragging = false;
            if (onPlus) { pressPlus = e.Button == MouseButtons.Left; return; }
            if (i < 0) return;
            pressIndex = i;
            pressPt = e.Location;
            if (e.Button == MouseButtons.Left && onClose) { pressClose = true; return; }
            if ((e.Button == MouseButtons.Left || e.Button == MouseButtons.Right) && i != selected)
            {
                Action<int> h = TabSelected;
                if (h != null) h(i);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            bool onClose, onPlus;
            int i = HitTab(e.Location, out onClose, out onPlus);
            int pi = pressIndex; bool pc = pressClose, pp = pressPlus;
            pressIndex = -1; pressClose = false; pressPlus = false; dragging = false;
            if (e.Button == MouseButtons.Left)
            {
                if (pp && onPlus) { Action h = NewTabRequested; if (h != null) h(); }
                else if (pc && onClose && pi == i && i >= 0) { Action<int> h = TabCloseRequested; if (h != null) h(i); }
            }
            else if (e.Button == MouseButtons.Middle)
            {
                if (i >= 0 && pi == i) { Action<int> h = TabCloseRequested; if (h != null) h(i); }
            }
            Invalidate();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            bool onClose, onPlus;
            if (e.Button == MouseButtons.Left && HitTab(e.Location, out onClose, out onPlus) < 0 && !onPlus)
            {
                Action h = NewTabRequested;
                if (h != null) h();
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (TotalW <= Avail) return;
            scrollX -= Math.Sign(e.Delta) * TabW;
            ClampScroll();
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hot != -1 || hotClose || hotPlus) { hot = -1; hotClose = false; hotPlus = false; Invalidate(); }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ClampScroll();
            EnsureVisible(selected);
        }
    }
}
