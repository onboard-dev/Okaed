using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Okaed
{
    /// <summary>
    /// 数値・英字・記号(空白可視化)の色を 1 つのダイアログで設定する。
    /// RGB を 0～255 で指定し、サンプル表示とエディタ本文の両方で確認できる。
    /// </summary>
    public class ColorSettingsDialog : Form
    {
        public const int Number = 0, Letter = 1, Symbol = 2;
        static readonly string[] Captions = { "数値を色分けする", "英字を色分けする", "記号(空白可視化)を色分けする" };

        readonly CheckBox[] ons = new CheckBox[3];
        readonly RgbEditor[] rgbs = new RgbEditor[3];
        readonly Color[] defaults;
        readonly SamplePanel sample;

        public bool IsOn(int k) { return ons[k].Checked; }
        public Color ColorOf(int k) { return rgbs[k].Value; }

        /// <summary>値が変わるたびに呼ばれる(エディタ本文へのライブ反映用)</summary>
        public event EventHandler ValuesChanged;

        public ColorSettingsDialog(string themeName, Font editorFont, Color back, Color fore,
            bool[] on, Color[] colors, Color[] defaultColors, float scale)
        {
            Func<int, int> S = delegate (int px) { return (int)Math.Round(px * scale); };
            defaults = defaultColors;

            Text = "数値・英字・記号の色(" + themeName + ")";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            Font = SystemFonts.MessageBoxFont;
            ClientSize = new Size(S(560), S(600));

            int y = S(10);
            for (int k = 0; k < 3; k++)
            {
                CheckBox cb = new CheckBox();
                cb.Text = Captions[k];
                cb.Checked = on[k];
                cb.AutoSize = true;
                cb.Location = new Point(S(14), y);
                Controls.Add(cb);
                ons[k] = cb;
                y += S(26);
                RgbEditor ed = new RgbEditor(colors[k], S);
                ed.Location = new Point(S(14), y);
                Controls.Add(ed);
                rgbs[k] = ed;
                y += ed.Height + S(8);
                if (k < 2)
                {
                    Label sep = new Label();
                    sep.BorderStyle = BorderStyle.Fixed3D;
                    sep.Location = new Point(S(14), y);
                    sep.Size = new Size(ClientSize.Width - S(28), 2);
                    Controls.Add(sep);
                    y += S(10);
                }
            }

            Label lp = new Label();
            lp.Text = "プレビュー(記号の色は「空白可視化」をオンにしたときに使われます)";
            lp.AutoSize = true;
            lp.Location = new Point(S(14), y);
            Controls.Add(lp);
            y += S(22);
            sample = new SamplePanel(editorFont, back, fore);
            sample.Location = new Point(S(14), y);
            sample.Size = new Size(ClientSize.Width - S(28), Math.Max(S(76), editorFont.Height * 3 + S(12)));
            Controls.Add(sample);
            y += sample.Height + S(12);

            Button reset = new Button();
            reset.Text = "既定の色に戻す";
            reset.AutoSize = true;
            reset.Location = new Point(S(14), y);
            reset.Click += delegate { for (int k = 0; k < 3; k++) { rgbs[k].Value = defaults[k]; ons[k].Checked = true; } };
            Controls.Add(reset);

            Button ok = new Button();
            ok.Text = "OK";
            ok.DialogResult = DialogResult.OK;
            ok.Size = new Size(S(90), S(28));
            ok.Location = new Point(ClientSize.Width - S(14) - ok.Width * 2 - S(8), y);
            Controls.Add(ok);
            Button cancel = new Button();
            cancel.Text = "キャンセル";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Size = new Size(S(90), S(28));
            cancel.Location = new Point(ClientSize.Width - S(14) - cancel.Width, y);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
            ClientSize = new Size(ClientSize.Width, y + S(28) + S(12));

            EventHandler changed = delegate { UpdateSample(); EventHandler h = ValuesChanged; if (h != null) h(this, EventArgs.Empty); };
            for (int k = 0; k < 3; k++) { ons[k].CheckedChanged += changed; rgbs[k].ValueChanged += changed; }
            UpdateSample();
        }

        void UpdateSample()
        {
            for (int k = 0; k < 3; k++) rgbs[k].Enabled = ons[k].Checked;
            sample.NumberColor = ons[Number].Checked ? rgbs[Number].Value : (Color?)null;
            sample.LetterColor = ons[Letter].Checked ? rgbs[Letter].Value : (Color?)null;
            sample.SymbolColor = ons[Symbol].Checked ? rgbs[Symbol].Value : sample.ForeColor;
            sample.Invalidate();
        }

        /// <summary>R/G/B のスライダーと数値入力(0～255)、色見本</summary>
        class RgbEditor : Panel
        {
            public event EventHandler ValueChanged;
            readonly TrackBar[] bars = new TrackBar[3];
            readonly NumericUpDown[] nums = new NumericUpDown[3];
            readonly Panel swatch;
            bool updating;

            public RgbEditor(Color c, Func<int, int> S)
            {
                Size = new Size(S(532), S(3 * 30));
                string[] names = { "R", "G", "B" };
                int[] vals = { c.R, c.G, c.B };
                for (int i = 0; i < 3; i++)
                {
                    int top = i * S(30);
                    Label l = new Label();
                    l.Text = names[i];
                    l.AutoSize = true;
                    l.Location = new Point(S(6), top + S(6));
                    Controls.Add(l);

                    TrackBar tb = new TrackBar();
                    tb.Minimum = 0; tb.Maximum = 255;
                    tb.TickStyle = TickStyle.None;
                    tb.SmallChange = 1; tb.LargeChange = 16;
                    tb.AutoSize = false;
                    tb.Location = new Point(S(26), top);
                    tb.Size = new Size(S(320), S(28));
                    tb.Value = vals[i];
                    Controls.Add(tb);
                    bars[i] = tb;

                    NumericUpDown nu = new NumericUpDown();
                    nu.Minimum = 0; nu.Maximum = 255;
                    nu.Location = new Point(S(352), top + S(2));
                    nu.Width = S(64);
                    nu.Value = vals[i];
                    Controls.Add(nu);
                    nums[i] = nu;

                    int idx = i;
                    tb.ValueChanged += delegate { if (updating) return; updating = true; nums[idx].Value = bars[idx].Value; updating = false; Changed(); };
                    nu.ValueChanged += delegate { if (updating) return; updating = true; bars[idx].Value = (int)nums[idx].Value; updating = false; Changed(); };
                }
                swatch = new Panel();
                swatch.BorderStyle = BorderStyle.FixedSingle;
                swatch.Location = new Point(S(430), S(4));
                swatch.Size = new Size(S(96), Height - S(10));
                swatch.BackColor = c;
                Controls.Add(swatch);
            }

            void Changed()
            {
                swatch.BackColor = Value;
                EventHandler h = ValueChanged;
                if (h != null) h(this, EventArgs.Empty);
            }

            public Color Value
            {
                get { return Color.FromArgb(bars[0].Value, bars[1].Value, bars[2].Value); }
                set
                {
                    updating = true;
                    int[] v = { value.R, value.G, value.B };
                    for (int i = 0; i < 3; i++) { bars[i].Value = v[i]; nums[i].Value = v[i]; }
                    updating = false;
                    Changed();
                }
            }
        }

        /// <summary>エディタと同じフォント・背景色で、数値/英字の色分けと空白記号を試し描きする</summary>
        class SamplePanel : Control
        {
            // \t=タブ、\u3000=全角スペース、行末に改行記号を描く
            static readonly string[] Lines = {
                "Okaed Version 1.2.0 で 2026年10月3日 に保存",
                "Ctrl+S\t/ UTF-8 (BOM)\u3000120 行, 35 桁",
            };
            readonly Font font;
            public Color? NumberColor, LetterColor;
            public Color SymbolColor;

            public SamplePanel(Font f, Color back, Color fore)
            {
                font = f;
                BackColor = back;
                ForeColor = fore;
                SymbolColor = fore;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.Clear(BackColor);
                ControlPaint.DrawBorder(g, ClientRectangle, SystemColors.ControlDark, ButtonBorderStyle.Solid);
                int lineH = font.Height + 4;
                int top = (Height - lineH * Lines.Length) / 2;
                for (int i = 0; i < Lines.Length; i++) DrawLine(g, Lines[i], 8, top + i * lineH, lineH);
            }

            void DrawLine(Graphics g, string s, int x, int y, int lineH)
            {
                TextFormatFlags fl = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
                Size big = new Size(short.MaxValue, short.MaxValue);
                using (Pen dot = new Pen(SymbolColor, 1.4f))
                using (SolidBrush sb = new SolidBrush(SymbolColor))
                {
                    dot.DashStyle = System.Drawing.Drawing2D.DashStyle.Dot;
                    int i = 0;
                    while (i < s.Length)
                    {
                        char ch = s[i];
                        if (ch == ' ' || ch == '\u3000' || ch == '\t')
                        {
                            int w = ch == '\t' ? TextRenderer.MeasureText(g, "MMMM", font, big, fl).Width
                                  : TextRenderer.MeasureText(g, "M" + ch + "M", font, big, fl).Width - TextRenderer.MeasureText(g, "MM", font, big, fl).Width;
                            int h = (int)(font.Height * 0.75f);
                            if (ch == '\t') TextRenderer.DrawText(g, "→", font, new Point(x, y), SymbolColor, fl);
                            else
                            {
                                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                                float bw = ch == ' ' ? Math.Max(5, w * 0.62f) : Math.Max(7, w - 4);
                                g.DrawRectangle(dot, x + (w - bw) / 2f, y + 1, bw, h);
                            }
                            x += w;
                            i++;
                            continue;
                        }
                        int kind = Kind(ch);
                        int j = i + 1;
                        while (j < s.Length && Kind(s[j]) == kind && s[j] != ' ' && s[j] != '\u3000' && s[j] != '\t') j++;
                        string run = s.Substring(i, j - i);
                        Color c = ForeColor;
                        if (kind == 1 && NumberColor.HasValue) c = NumberColor.Value;
                        if (kind == 2 && LetterColor.HasValue) c = LetterColor.Value;
                        TextRenderer.DrawText(g, run, font, new Point(x, y), c, fl);
                        x += TextRenderer.MeasureText(g, run, font, big, fl).Width;
                        i = j;
                    }
                    TextRenderer.DrawText(g, "↵", font, new Point(x + 2, y), SymbolColor, fl);
                }
            }

            static int Kind(char c)
            {
                if (char.IsDigit(c)) return 1;
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) return 2;
                return 0;
            }
        }
    }

    /// <summary>拡張子の変更ダイアログ</summary>
    public class ExtensionDialog : Form
    {
        readonly ComboBox cmb;

        /// <summary>入力された拡張子(先頭のドットなし。空文字なら拡張子なし)</summary>
        public string Extension { get { return cmb.Text.Trim().TrimStart('.'); } }

        public ExtensionDialog(string fileName, string currentExt, float scale)
        {
            Func<int, int> S = delegate (int px) { return (int)Math.Round(px * scale); };
            Text = "拡張子の変更";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            Font = SystemFonts.MessageBoxFont;
            ClientSize = new Size(S(380), S(130));

            Label l = new Label();
            l.Text = "「" + fileName + "」の新しい拡張子:";
            l.AutoSize = false;
            l.AutoEllipsis = true;
            l.Location = new Point(S(14), S(14));
            l.Size = new Size(ClientSize.Width - S(28), S(20));
            Controls.Add(l);

            Label dot = new Label();
            dot.Text = ".";
            dot.AutoSize = true;
            dot.Location = new Point(S(14), S(46));
            Controls.Add(dot);

            cmb = new ComboBox();
            cmb.DropDownStyle = ComboBoxStyle.DropDown;
            cmb.Items.AddRange(new object[] { "txt", "md", "csv", "log", "ini", "html", "json", "xml", "bat", "ps1" });
            cmb.Text = currentExt;
            cmb.Location = new Point(S(26), S(42));
            cmb.Width = S(160);
            Controls.Add(cmb);

            Button ok = new Button();
            ok.Text = "変更";
            ok.DialogResult = DialogResult.OK;
            ok.Size = new Size(S(90), S(28));
            ok.Location = new Point(ClientSize.Width - S(14) - ok.Width * 2 - S(8), S(88));
            Controls.Add(ok);
            Button cancel = new Button();
            cancel.Text = "キャンセル";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Size = new Size(S(90), S(28));
            cancel.Location = new Point(ClientSize.Width - S(14) - cancel.Width, S(88));
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
            Shown += delegate { cmb.Focus(); cmb.SelectAll(); };
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (DialogResult == DialogResult.OK)
            {
                string ext = Extension;
                if (ext.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || ext.EndsWith(".") || ext.IndexOf(' ') >= 0)
                {
                    MessageBox.Show(this, "拡張子に使えない文字が含まれています。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    e.Cancel = true;
                    cmb.Focus();
                    return;
                }
            }
            base.OnFormClosing(e);
        }
    }
}
