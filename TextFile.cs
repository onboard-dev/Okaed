using System;
using System.IO;
using System.Text;

namespace Okaed
{
    public enum FileEnc { Sjis, Utf8Bom }

    /// <summary>SJIS / UTF-8(BOM) の読み書き</summary>
    public static class TextFile
    {
        public static string EncName(FileEnc e) { return e == FileEnc.Sjis ? "Shift_JIS" : "UTF-8 (BOM)"; }

        /// <summary>
        /// 読み込み。改行は "\n" に正規化して返す。
        /// 判定: BOM付きUTF-8 → UTF-8 / BOMなしでも正しいUTF-8なら UTF-8(保存時BOM付与) / それ以外 SJIS
        /// </summary>
        public static string Load(string path, FileEnc defaultEnc, out FileEnc enc, out string eol, out string note)
        {
            byte[] b = File.ReadAllBytes(path);
            note = null;
            string text;
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
            {
                enc = FileEnc.Utf8Bom;
                text = new UTF8Encoding(false).GetString(b, 3, b.Length - 3);
            }
            else if (b.Length >= 2 && ((b[0] == 0xFF && b[1] == 0xFE) || (b[0] == 0xFE && b[1] == 0xFF)))
            {
                enc = FileEnc.Utf8Bom;
                Encoding u16 = b[0] == 0xFF ? (Encoding)new UnicodeEncoding(false, true) : new UnicodeEncoding(true, true);
                text = u16.GetString(b, 2, b.Length - 2);
                note = "UTF-16 は非対応のため UTF-8 (BOM) として保存されます";
            }
            else
            {
                bool hasHigh = false;
                for (int i = 0; i < b.Length; i++) { if (b[i] >= 0x80) { hasHigh = true; break; } }
                if (!hasHigh)
                {
                    enc = defaultEnc;
                    text = Encoding.ASCII.GetString(b);
                }
                else
                {
                    string u = null;
                    try { u = new UTF8Encoding(false, true).GetString(b); } catch (DecoderFallbackException) { u = null; }
                    if (u != null)
                    {
                        enc = FileEnc.Utf8Bom;
                        text = u;
                        note = "BOMなしUTF-8 として読み込みました(保存時は BOM 付き)";
                    }
                    else
                    {
                        enc = FileEnc.Sjis;
                        text = Encoding.GetEncoding(932).GetString(b);
                    }
                }
            }

            int crlf = 0, lf = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n') { if (i > 0 && text[i - 1] == '\r') crlf++; else lf++; }
            }
            eol = (lf > crlf) ? "\n" : "\r\n";
            if (text.IndexOf('\r') >= 0) text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            return text;
        }

        /// <summary>SJIS で表現できない最初の文字位置。すべて表現できれば -1</summary>
        public static int FindUnencodable(string text)
        {
            // 往復変換して最初に食い違う位置を返す(どの実装でも確実に判定できる)
            Encoding e = Encoding.GetEncoding(932, new EncoderReplacementFallback("?"), new DecoderReplacementFallback("?"));
            string back = e.GetString(e.GetBytes(text));
            if (back == text) return -1;
            int n = Math.Min(back.Length, text.Length);
            for (int i = 0; i < n; i++) if (back[i] != text[i]) return i;
            return n < text.Length ? n : 0;
        }

        public static void Save(string path, string text, FileEnc enc, string eol)
        {
            if (eol != "\n") text = text.Replace("\n", eol);
            byte[] body;
            if (enc == FileEnc.Sjis)
            {
                body = Encoding.GetEncoding(932, new EncoderReplacementFallback("?"), DecoderFallback.ReplacementFallback).GetBytes(text);
            }
            else
            {
                byte[] t = new UTF8Encoding(false).GetBytes(text);
                body = new byte[t.Length + 3];
                body[0] = 0xEF; body[1] = 0xBB; body[2] = 0xBF;
                Buffer.BlockCopy(t, 0, body, 3, t.Length);
            }
            // 一時ファイルに書いてから置き換え(書き込み途中の破損防止)
            string tmp = path + ".okaed~";
            File.WriteAllBytes(tmp, body);
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, null); return; }
                catch (Exception) { }
                File.WriteAllBytes(path, body);
                try { File.Delete(tmp); } catch { }
            }
            else
            {
                File.Move(tmp, path);
            }
        }
    }
}
