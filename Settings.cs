using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Okaed
{
    /// <summary>%APPDATA%\Okaed\okaed.ini に key=value で保存する簡易設定</summary>
    public class Settings
    {
        readonly Dictionary<string, string> d = new Dictionary<string, string>();
        readonly string path;

        public Settings()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Okaed");
            path = Path.Combine(dir, "okaed.ini");
            try
            {
                if (File.Exists(path))
                {
                    foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        int i = line.IndexOf('=');
                        if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1);
                    }
                }
            }
            catch { }
        }

        public string Get(string k, string def) { string v; return d.TryGetValue(k, out v) ? v : def; }
        public bool GetBool(string k, bool def) { string v = Get(k, null); return v == null ? def : v == "1"; }
        public int GetInt(string k, int def) { int r; return int.TryParse(Get(k, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out r) ? r : def; }
        public float GetFloat(string k, float def) { float r; return float.TryParse(Get(k, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out r) ? r : def; }

        public void Set(string k, string v) { d[k] = v; }
        public void Set(string k, bool v) { d[k] = v ? "1" : "0"; }
        public void Set(string k, int v) { d[k] = v.ToString(CultureInfo.InvariantCulture); }
        public void Set(string k, float v) { d[k] = v.ToString(CultureInfo.InvariantCulture); }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, string> kv in d) sb.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            }
            catch { }
        }
    }
}
