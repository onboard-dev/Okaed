using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Okaed
{
    /// <summary>
    /// 軽量 Markdown → HTML 変換(外部ライブラリ不要)。
    /// 見出し/段落/改行/強調/打消し/コード/コードブロック/引用/リスト(入れ子・チェックボックス)/表/リンク/画像/水平線
    /// 生HTMLはエスケープ(&lt;br&gt; のみ許可)。
    /// </summary>
    public static class Markdown
    {
        static readonly Regex FenceRe = new Regex(@"^ {0,3}(`{3,}|~{3,})[ \t]*([^`\s]*)", RegexOptions.Compiled);
        static readonly Regex HeadRe = new Regex(@"^ {0,3}(#{1,6})(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$", RegexOptions.Compiled);
        static readonly Regex HrRe = new Regex(@"^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$", RegexOptions.Compiled);
        static readonly Regex QuoteRe = new Regex(@"^ {0,3}> ?", RegexOptions.Compiled);
        static readonly Regex ListRe = new Regex(@"^( *)([-*+]|\d{1,9}[.)])([ \t]+|$)(.*)$", RegexOptions.Compiled);
        static readonly Regex TableSepRe = new Regex(@"^ {0,3}\|?[ \t]*:?-+:?[ \t]*(\|[ \t]*:?-+:?[ \t]*)*\|?[ \t]*$", RegexOptions.Compiled);
        static readonly Regex SetextH1 = new Regex(@"^ {0,3}=+[ \t]*$", RegexOptions.Compiled);
        static readonly Regex SetextH2 = new Regex(@"^ {0,3}-+[ \t]*$", RegexOptions.Compiled);

        static readonly Regex CodeSpanRe = new Regex(@"(`+)(.+?)(?<!`)\1(?!`)", RegexOptions.Compiled | RegexOptions.Singleline);
        static readonly Regex EscRe = new Regex(@"\\([\\`*_{}\[\]()#+\-.!|~<>])", RegexOptions.Compiled);
        static readonly Regex AutoLinkRe = new Regex(@"<((?:https?|ftp|mailto):[^\s<>]+)>", RegexOptions.Compiled);
        static readonly Regex ImageRe = new Regex(@"!\[([^\]]*)\]\(\s*<?([^\s)>]*)>?(?:\s+[""']([^""']*)[""'])?\s*\)", RegexOptions.Compiled);
        static readonly Regex LinkRe = new Regex(@"\[([^\]]+)\]\(\s*<?([^\s)>]*)>?(?:\s+[""']([^""']*)[""'])?\s*\)", RegexOptions.Compiled);
        static readonly Regex BareUrlRe = new Regex(@"(?<![\w/""'=])(https?://[^\s<>""'「」『』（）、。]+[^\s<>""'「」『』（）、。.,;:!?)\]])", RegexOptions.Compiled);
        static readonly Regex StrongRe1 = new Regex(@"\*\*(?=\S)(.+?)(?<=\S)\*\*", RegexOptions.Compiled | RegexOptions.Singleline);
        static readonly Regex StrongRe2 = new Regex(@"(?<![\w])__(?=\S)(.+?)(?<=\S)__(?![\w])", RegexOptions.Compiled | RegexOptions.Singleline);
        static readonly Regex EmRe1 = new Regex(@"(?<!\*)\*(?=[^\s*])(.+?)(?<=[^\s*])\*(?!\*)", RegexOptions.Compiled | RegexOptions.Singleline);
        static readonly Regex EmRe2 = new Regex(@"(?<![\w])_(?=\S)(.+?)(?<=\S)_(?![\w])", RegexOptions.Compiled | RegexOptions.Singleline);
        static readonly Regex DelRe = new Regex(@"~~(?=\S)(.+?)(?<=\S)~~", RegexOptions.Compiled | RegexOptions.Singleline);
        static readonly Regex BrTagRe = new Regex(@"&lt;br\s*/?&gt;", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex SlotRe = new Regex("\u0001(\\d+)\u0002", RegexOptions.Compiled);

        public static string ToHtml(string text)
        {
            string[] raw = text.Replace("\r\n", "\n").Replace('\r', '\n').Split(new char[] { '\n' });
            List<string> lines = new List<string>(raw.Length);
            foreach (string l in raw) lines.Add(ExpandTabs(l));
            StringBuilder sb = new StringBuilder(text.Length + 256);
            Blocks(lines, sb, false);
            return sb.ToString();
        }

        static string ExpandTabs(string l)
        {
            if (l.IndexOf('\t') < 0) return l;
            StringBuilder sb = new StringBuilder();
            int col = 0;
            bool lead = true;
            foreach (char c in l)
            {
                if (c == '\t' && lead) { int n = 4 - (col % 4); sb.Append(' ', n); col += n; }
                else { if (c != ' ') lead = false; sb.Append(c); col++; }
            }
            return sb.ToString();
        }

        static bool IsBlank(string l) { return l.Trim().Length == 0; }
        static int Indent(string l) { int i = 0; while (i < l.Length && l[i] == ' ') i++; return i; }

        static bool IsBlockStart(List<string> L, int i)
        {
            string l = L[i];
            if (FenceRe.IsMatch(l) || HeadRe.IsMatch(l) || HrRe.IsMatch(l) || QuoteRe.IsMatch(l)) return true;
            Match m = ListRe.Match(l);
            if (m.Success && m.Groups[4].Value.Trim().Length > 0 && Indent(l) < 4)
            {
                string mk = m.Groups[2].Value;
                if (!char.IsDigit(mk[0]) || mk.StartsWith("1")) return true;
            }
            if (i + 1 < L.Count && l.IndexOf('|') >= 0 && TableSepRe.IsMatch(L[i + 1]) && L[i + 1].IndexOf('-') >= 0) return true;
            return false;
        }

        static void Blocks(List<string> L, StringBuilder sb, bool tight)
        {
            int i = 0;
            while (i < L.Count)
            {
                string line = L[i];
                if (IsBlank(line)) { i++; continue; }
                Match m;

                // コードブロック(フェンス)
                m = FenceRe.Match(line);
                if (m.Success)
                {
                    string fence = m.Groups[1].Value;
                    string lang = m.Groups[2].Value;
                    int ind = Indent(line);
                    i++;
                    StringBuilder code = new StringBuilder();
                    while (i < L.Count)
                    {
                        string t = L[i].Trim();
                        if (t.Length >= fence.Length && t.TrimEnd(fence[0]).Length == 0) { i++; break; }
                        string cl = L[i];
                        int strip = Math.Min(ind, Indent(cl));
                        code.Append(cl.Substring(strip)).Append('\n');
                        i++;
                    }
                    sb.Append("<pre><code");
                    if (lang.Length > 0) sb.Append(" class=\"language-").Append(Esc(lang)).Append('"');
                    sb.Append('>').Append(Esc(code.ToString())).Append("</code></pre>\n");
                    continue;
                }

                // 見出し
                m = HeadRe.Match(line);
                if (m.Success)
                {
                    int lv = m.Groups[1].Length;
                    sb.Append("<h").Append(lv).Append('>').Append(Inline(m.Groups[2].Value)).Append("</h").Append(lv).Append(">\n");
                    i++; continue;
                }

                // 水平線
                if (HrRe.IsMatch(line)) { sb.Append("<hr />\n"); i++; continue; }

                // 引用
                if (QuoteRe.IsMatch(line))
                {
                    List<string> q = new List<string>();
                    while (i < L.Count && !IsBlank(L[i]))
                    {
                        Match qm = QuoteRe.Match(L[i]);
                        if (qm.Success) q.Add(L[i].Substring(qm.Length));
                        else if (q.Count > 0 && !IsBlockStart(L, i)) q.Add(L[i]);
                        else break;
                        i++;
                    }
                    sb.Append("<blockquote>\n");
                    Blocks(q, sb, false);
                    sb.Append("</blockquote>\n");
                    continue;
                }

                // リスト
                m = ListRe.Match(line);
                if (m.Success && Indent(line) < 4)
                {
                    ParseList(L, ref i, sb);
                    continue;
                }

                // 表
                if (i + 1 < L.Count && line.IndexOf('|') >= 0 && TableSepRe.IsMatch(L[i + 1]) && L[i + 1].IndexOf('-') >= 0)
                {
                    ParseTable(L, ref i, sb);
                    continue;
                }

                // インデントコード
                if (Indent(line) >= 4)
                {
                    StringBuilder code = new StringBuilder();
                    while (i < L.Count && (Indent(L[i]) >= 4 || IsBlank(L[i])))
                    {
                        if (IsBlank(L[i]))
                        {
                            int j = i; while (j < L.Count && IsBlank(L[j])) j++;
                            if (j >= L.Count || Indent(L[j]) < 4) break;
                        }
                        code.Append(L[i].Length >= 4 ? L[i].Substring(4) : "").Append('\n');
                        i++;
                    }
                    sb.Append("<pre><code>").Append(Esc(code.ToString())).Append("</code></pre>\n");
                    continue;
                }

                // 段落(Setext 見出しを含む)
                List<string> para = new List<string>();
                para.Add(line);
                i++;
                int heading = 0;
                while (i < L.Count && !IsBlank(L[i]))
                {
                    if (SetextH1.IsMatch(L[i])) { heading = 1; i++; break; }
                    if (SetextH2.IsMatch(L[i])) { heading = 2; i++; break; }
                    if (IsBlockStart(L, i)) break;
                    para.Add(L[i]);
                    i++;
                }
                if (heading > 0)
                {
                    sb.Append("<h").Append(heading).Append('>').Append(Inline(string.Join(" ", para.ToArray()).Trim())).Append("</h").Append(heading).Append(">\n");
                    continue;
                }
                StringBuilder p = new StringBuilder();
                for (int k = 0; k < para.Count; k++)
                {
                    string pl = para[k];
                    bool last = k == para.Count - 1;
                    if (!last && pl.EndsWith("  ")) p.Append(pl.Trim()).Append('\u0003');
                    else if (!last && pl.EndsWith("\\")) p.Append(pl.Trim().TrimEnd('\\')).Append('\u0003');
                    else { p.Append(pl.Trim()); if (!last) p.Append('\n'); }
                }
                string html = Inline(p.ToString()).Replace("\u0003", "<br />\n");
                if (tight) sb.Append(html).Append('\n');
                else sb.Append("<p>").Append(html).Append("</p>\n");
            }
        }

        static void ParseList(List<string> L, ref int i, StringBuilder sb)
        {
            Match first = ListRe.Match(L[i]);
            string mk0 = first.Groups[2].Value;
            bool ordered = char.IsDigit(mk0[0]);
            int baseIndent = first.Groups[1].Length;
            if (ordered)
            {
                int start;
                int.TryParse(mk0.Substring(0, mk0.Length - 1), out start);
                sb.Append(start != 1 ? "<ol start=\"" + start + "\">\n" : "<ol>\n");
            }
            else sb.Append("<ul>\n");

            // 項目を収集
            List<List<string>> items = new List<List<string>>();
            bool loose = false;
            while (i < L.Count)
            {
                if (IsBlank(L[i]))
                {
                    int j = i; while (j < L.Count && IsBlank(L[j])) j++;
                    if (j < L.Count && items.Count > 0)
                    {
                        Match nm = ListRe.Match(L[j]);
                        if (nm.Success && Indent(L[j]) == baseIndent && char.IsDigit(nm.Groups[2].Value[0]) == ordered)
                        {
                            loose = true; i = j; continue;
                        }
                    }
                    break;
                }
                Match m = ListRe.Match(L[i]);
                if (!m.Success || Indent(L[i]) > baseIndent + 3 || Indent(L[i]) < baseIndent || char.IsDigit(m.Groups[2].Value[0]) != ordered) break;

                int pad = m.Groups[3].Value.Length;
                if (pad == 0 || pad > 4) pad = 1;
                int contentIndent = m.Groups[1].Length + m.Groups[2].Length + pad;
                List<string> item = new List<string>();
                item.Add(m.Groups[4].Value);
                i++;
                while (i < L.Count)
                {
                    string l = L[i];
                    if (IsBlank(l))
                    {
                        int j = i; while (j < L.Count && IsBlank(L[j])) j++;
                        if (j < L.Count && Indent(L[j]) >= contentIndent)
                        {
                            for (int k = i; k < j; k++) item.Add("");
                            i = j;
                            continue;
                        }
                        break;
                    }
                    if (Indent(l) >= contentIndent) { item.Add(l.Substring(contentIndent)); i++; continue; }
                    if (ListRe.IsMatch(l))
                    {
                        // より深いがcontentIndent未満のサブリスト → 入れ子として扱う
                        if (Indent(l) > baseIndent) { item.Add(l.Substring(Math.Min(Indent(l), contentIndent))); i++; continue; }
                        break;
                    }
                    if (IsBlockStart(L, i)) break;
                    item.Add(l.Trim());   // 遅延継続行
                    i++;
                }
                items.Add(item);
            }

            foreach (List<string> item in items)
            {
                string head = item[0];
                string check = null;
                if (head.StartsWith("[ ] ") || head == "[ ]") { check = ""; head = head.Substring(Math.Min(4, head.Length)); }
                else if (head.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase) || head.Equals("[x]", StringComparison.OrdinalIgnoreCase))
                { check = " checked"; head = head.Substring(Math.Min(4, head.Length)); }
                item[0] = head;

                bool itemLoose = loose;
                for (int k = 1; k < item.Count - 1; k++) if (item[k].Length == 0) { itemLoose = true; break; }

                sb.Append(check != null ? "<li class=\"task\">" : "<li>");
                if (check != null) sb.Append("<input type=\"checkbox\" disabled").Append(check).Append(" /> ");
                StringBuilder inner = new StringBuilder();
                Blocks(item, inner, !itemLoose);
                string s = inner.ToString();
                if (s.EndsWith("\n")) s = s.Substring(0, s.Length - 1);
                sb.Append(s).Append("</li>\n");
            }
            sb.Append(ordered ? "</ol>\n" : "</ul>\n");
        }

        static List<string> SplitRow(string row)
        {
            string r = row.Trim();
            if (r.StartsWith("|")) r = r.Substring(1);
            if (r.EndsWith("|") && !r.EndsWith("\\|")) r = r.Substring(0, r.Length - 1);
            List<string> cells = new List<string>();
            StringBuilder cur = new StringBuilder();
            bool inCode = false;
            for (int k = 0; k < r.Length; k++)
            {
                char c = r[k];
                if (c == '\\' && k + 1 < r.Length && r[k + 1] == '|') { cur.Append('|'); k++; continue; }
                if (c == '`') inCode = !inCode;
                if (c == '|' && !inCode) { cells.Add(cur.ToString().Trim()); cur.Length = 0; continue; }
                cur.Append(c);
            }
            cells.Add(cur.ToString().Trim());
            return cells;
        }

        static void ParseTable(List<string> L, ref int i, StringBuilder sb)
        {
            List<string> head = SplitRow(L[i]);
            List<string> seps = SplitRow(L[i + 1]);
            string[] align = new string[head.Count];
            for (int k = 0; k < head.Count; k++)
            {
                string s = k < seps.Count ? seps[k] : "";
                bool left = s.StartsWith(":"), right = s.EndsWith(":");
                align[k] = left && right ? " style=\"text-align:center\"" : right ? " style=\"text-align:right\"" : left ? " style=\"text-align:left\"" : "";
            }
            i += 2;
            sb.Append("<table>\n<thead><tr>");
            for (int k = 0; k < head.Count; k++) sb.Append("<th").Append(align[k]).Append('>').Append(Inline(head[k])).Append("</th>");
            sb.Append("</tr></thead>\n<tbody>\n");
            while (i < L.Count && !IsBlank(L[i]) && L[i].IndexOf('|') >= 0)
            {
                List<string> cells = SplitRow(L[i]);
                sb.Append("<tr>");
                for (int k = 0; k < head.Count; k++)
                    sb.Append("<td").Append(align[k]).Append('>').Append(k < cells.Count ? Inline(cells[k]) : "").Append("</td>");
                sb.Append("</tr>\n");
                i++;
            }
            sb.Append("</tbody>\n</table>\n");
        }

        // ---------------- インライン ----------------

        public static string Esc(string s)
        {
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        static string SafeUrl(string u)
        {
            string t = u.Trim();
            string lower = t.ToLowerInvariant();
            if (lower.StartsWith("javascript:") || lower.StartsWith("vbscript:") || lower.StartsWith("data:text")) return "#";
            return Esc(t);
        }

        static string Inline(string s)
        {
            return InlineCore(s, new List<string>());
        }

        static string InlineCore(string s, List<string> slots)
        {
            MatchEvaluator add = null;

            s = CodeSpanRe.Replace(s, delegate (Match m)
            {
                string c = m.Groups[2].Value;
                if (c.Length >= 2 && c.StartsWith(" ") && c.EndsWith(" ") && c.Trim().Length > 0) c = c.Substring(1, c.Length - 2);
                return Slot(slots, "<code>" + Esc(c.Replace('\n', ' ')) + "</code>");
            });
            s = EscRe.Replace(s, delegate (Match m) { return Slot(slots, Esc(m.Groups[1].Value)); });
            s = AutoLinkRe.Replace(s, delegate (Match m)
            {
                string u = m.Groups[1].Value;
                return Slot(slots, "<a href=\"" + SafeUrl(u) + "\">" + Esc(u) + "</a>");
            });
            s = ImageRe.Replace(s, delegate (Match m)
            {
                string t = m.Groups[3].Success && m.Groups[3].Value.Length > 0 ? " title=\"" + Esc(m.Groups[3].Value) + "\"" : "";
                return Slot(slots, "<img src=\"" + SafeUrl(m.Groups[2].Value) + "\" alt=\"" + Esc(m.Groups[1].Value) + "\"" + t + " />");
            });
            s = LinkRe.Replace(s, delegate (Match m)
            {
                string t = m.Groups[3].Success && m.Groups[3].Value.Length > 0 ? " title=\"" + Esc(m.Groups[3].Value) + "\"" : "";
                return Slot(slots, "<a href=\"" + SafeUrl(m.Groups[2].Value) + "\"" + t + ">" + InlineCore(m.Groups[1].Value, slots) + "</a>");
            });
            s = BareUrlRe.Replace(s, delegate (Match m)
            {
                string u = m.Groups[1].Value;
                return Slot(slots, "<a href=\"" + SafeUrl(u) + "\">" + Esc(u) + "</a>");
            });

            s = Esc(s);
            s = BrTagRe.Replace(s, "<br />");
            s = StrongRe1.Replace(s, "<strong>$1</strong>");
            s = StrongRe2.Replace(s, "<strong>$1</strong>");
            s = EmRe1.Replace(s, "<em>$1</em>");
            s = EmRe2.Replace(s, "<em>$1</em>");
            s = DelRe.Replace(s, "<del>$1</del>");

            add = delegate (Match m)
            {
                int n = int.Parse(m.Groups[1].Value);
                return n < slots.Count ? slots[n] : "";
            };
            for (int guard = 0; guard < 5 && s.IndexOf('\u0001') >= 0; guard++) s = SlotRe.Replace(s, add);
            return s;
        }

        static string Slot(List<string> slots, string html)
        {
            slots.Add(html);
            return "\u0001" + (slots.Count - 1) + "\u0002";
        }
    }
}
