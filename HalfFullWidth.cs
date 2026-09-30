using System;
using System.Collections.Generic;
using System.Text;

namespace Okaed
{
    /// <summary>
    /// 半角/全角の相互変換(数字・英字・記号・スペース・カタカナ)。
    /// OS の機能に頼らない固定テーブル方式。
    /// </summary>
    public static class HalfFullWidth
    {
        // JIS X 0201 半角カナ(U+FF61-FF9F)⇔全角カナ の対応表
        static readonly string HalfKana =
            "｡｢｣､･ｦｧｨｩｪｫｬｭｮｯｰｱｲｳｴｵｶｷｸｹｺｻｼｽｾｿﾀﾁﾂﾃﾄﾅﾆﾇﾈﾉﾊﾋﾌﾍﾎﾏﾐﾑﾒﾓﾔﾕﾖﾗﾘﾙﾚﾛﾜﾝﾞﾟ";
        static readonly string FullKana =
            "。「」、・ヲァィゥェォャュョッーアイウエオカキクケコサシスセソタチツテトナニヌネノハヒフヘホマミムメモヤユヨラリルレロワン゛゜";

        // 半角2文字(濁点/半濁点)→全角1文字
        static readonly string[] DakutenHalf = {
            "ｶﾞ","ｷﾞ","ｸﾞ","ｹﾞ","ｺﾞ","ｻﾞ","ｼﾞ","ｽﾞ","ｾﾞ","ｿﾞ",
            "ﾀﾞ","ﾁﾞ","ﾂﾞ","ﾃﾞ","ﾄﾞ","ﾊﾞ","ﾋﾞ","ﾌﾞ","ﾍﾞ","ﾎﾞ",
            "ﾊﾟ","ﾋﾟ","ﾌﾟ","ﾍﾟ","ﾎﾟ","ｳﾞ"
        };
        static readonly string[] DakutenFull = {
            "ガ","ギ","グ","ゲ","ゴ","ザ","ジ","ズ","ゼ","ゾ",
            "ダ","ヂ","ヅ","デ","ド","バ","ビ","ブ","ベ","ボ",
            "パ","ピ","プ","ペ","ポ","ヴ"
        };

        static readonly Dictionary<char, char> H2F = new Dictionary<char, char>();
        static readonly Dictionary<char, char> F2H = new Dictionary<char, char>();
        static readonly Dictionary<string, char> DakutenToFull = new Dictionary<string, char>();
        static readonly Dictionary<char, string> FullToDakuten = new Dictionary<char, string>();

        static HalfFullWidth()
        {
            for (int i = 0; i < HalfKana.Length; i++)
            {
                H2F[HalfKana[i]] = FullKana[i];
                if (!F2H.ContainsKey(FullKana[i])) F2H[FullKana[i]] = HalfKana[i];
            }
            for (int i = 0; i < DakutenHalf.Length; i++)
            {
                DakutenToFull[DakutenHalf[i]] = DakutenFull[i][0];
                FullToDakuten[DakutenFull[i][0]] = DakutenHalf[i];
            }
        }

        /// <summary>全角(数字・英字・記号・スペース・カタカナ)を半角に変換する</summary>
        public static string ToHalf(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                // 全角英数記号(U+FF01-FF5E) → 半角(ASCII 0x21-0x7E)
                if (c >= '！' && c <= '～') { sb.Append((char)(c - 0xFEE0)); continue; }
                if (c == '　') { sb.Append(' '); continue; }               // 全角スペース
                string dak;
                if (FullToDakuten.TryGetValue(c, out dak)) { sb.Append(dak); continue; }  // ガ→ｶﾞ 等
                char h;
                if (F2H.TryGetValue(c, out h)) { sb.Append(h); continue; }     // 全角カナ→半角カナ
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>半角(数字・英字・記号・スペース・カナ)を全角に変換する</summary>
        public static string ToFull(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= '｡' && c <= 'ﾟ' && i + 1 < s.Length)
                {
                    string pair = s.Substring(i, 2);
                    char full;
                    if (DakutenToFull.TryGetValue(pair, out full)) { sb.Append(full); i++; continue; }
                }
                if (c == ' ') { sb.Append('　'); continue; }               // 半角スペース
                char f;
                if (H2F.TryGetValue(c, out f)) { sb.Append(f); continue; }     // 半角カナ→全角カナ
                if (c >= '!' && c <= '~') { sb.Append((char)(c + 0xFEE0)); continue; } // ASCII → 全角
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
