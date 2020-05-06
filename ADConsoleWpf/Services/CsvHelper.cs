using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ADConsole.Services
{
    /// <summary>CSV 解析与导入校验。表头支持中英文、不区分大小写。</summary>
    public static class CsvHelper
    {
        // 表头别名 → 字段名
        private static readonly Dictionary<string, string> Aliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static CsvHelper()
        {
            Map("Sam", "Sam");
            Map("SamAccountName", "Sam");
            Map("sAMAccountName", "Sam");
            Map("Account", "Sam");
            Map("LoginName", "Sam");
            Map("登录名", "Sam");
            Map("帐号", "Sam");
            Map("账号", "Sam");

            Map("Name", "Name");
            Map("DisplayName", "Name");
            Map("姓名", "Name");
            Map("用户名", "Name");

            Map("Password", "Password");
            Map("Pwd", "Password");
            Map("密码", "Password");

            Map("OU", "Ou");
            Map("Path", "Ou");
            Map("DistinguishedName", "Ou");
            Map("所在OU", "Ou");
            Map("所在组织单位", "Ou");

            Map("Department", "Department");
            Map("部门", "Department");

            Map("Title", "Title");
            Map("JobTitle", "Title");
            Map("职位", "Title");
            Map("岗位", "Title");

            Map("Email", "Email");
            Map("EmailAddress", "Email");
            Map("Mail", "Email");
            Map("邮箱", "Email");
            Map("电子邮件", "Email");

            Map("TelephoneNumber", "Phone");
            Map("Phone", "Phone");
            Map("Mobile", "Phone");
            Map("电话", "Phone");
            Map("手机号", "Phone");

            Map("Description", "Description");
            Map("备注", "Description");
            Map("说明", "Description");
        }

        private static void Map(string header, string field)
        {
            Aliases[header] = field;
        }

        /// <summary>
        /// 按候选编码依次尝试解析，返回成功解析出的表头。
        /// 用于同时兼容 UTF-8 和 GBK 导出的 CSV。
        /// </summary>
        public static List<string> ParseWithEncodingAuto(string path, out List<string[]> rows)
        {
            Encoding[] candidates = new Encoding[]
            {
                new UTF8Encoding(false),
                Encoding.GetEncoding(936)
            };

            Exception last = null;
            foreach (Encoding enc in candidates)
            {
                try
                {
                    List<List<string>> table = ReadTable(path, enc);
                    if (table.Count == 0)
                    {
                        rows = new List<string[]>();
                        return new List<string>();
                    }

                    List<string> header = table[0];
                    // 编码猜错时表头会变成乱码，匹配不到任何已知字段 → 换下一个编码
                    if (CountKnownHeaders(header) == 0) continue;

                    rows = new List<string[]>();
                    for (int i = 1; i < table.Count; i++)
                    {
                        if (table[i].Count == 1 && string.IsNullOrEmpty(table[i][0])) continue;
                        rows.Add(table[i].ToArray());
                    }
                    return header;
                }
                catch (Exception ex)
                {
                    last = ex;
                }
            }

            rows = new List<string[]>();
            if (last != null) throw new IOException("无法解析 CSV 文件：" + last.Message);
            return new List<string>();
        }

        private static int CountKnownHeaders(List<string> header)
        {
            int n = 0;
            foreach (string h in header)
            {
                string key = h == null ? string.Empty : h.Trim().Trim('\uFEFF');
                if (Aliases.ContainsKey(key)) n++;
            }
            return n;
        }

        /// <summary>读整个 CSV 为二维表（支持引号包裹、转义引号 ""）</summary>
        public static List<List<string>> ReadTable(string path, Encoding encoding)
        {
            List<List<string>> table = new List<List<string>>();
            using (StreamReader sr = new StreamReader(path, encoding))
            {
                string line;
                bool inQuote = false;
                StringBuilder buffer = new StringBuilder();

                while ((line = sr.ReadLine()) != null)
                {
                    if (buffer.Length > 0)
                    {
                        // 上一行带未闭合引号 → 拼接
                        buffer.Append("\n").Append(line);
                    }
                    else
                    {
                        buffer.Append(line);
                    }

                    string content = buffer.ToString();
                    inQuote = HasUnbalancedQuote(content);
                    if (inQuote) continue;

                    table.Add(ParseLine(content));
                    buffer.Length = 0;
                }

                if (buffer.Length > 0) table.Add(ParseLine(buffer.ToString()));
            }

            // 去掉 BOM 残留
            if (table.Count > 0 && table[0].Count > 0 && table[0][0] != null)
            {
                table[0][0] = table[0][0].Trim('\uFEFF');
            }
            return table;
        }

        private static bool HasUnbalancedQuote(string s)
        {
            bool inQ = false;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '"')
                {
                    if (inQ && i + 1 < s.Length && s[i + 1] == '"') { i++; continue; }
                    inQ = !inQ;
                }
            }
            return inQ;
        }

        /// <summary>解析单行 CSV</summary>
        public static List<string> ParseLine(string line)
        {
            List<string> cells = new List<string>();
            if (line == null) { return cells; }

            StringBuilder sb = new StringBuilder();
            bool inQuote = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuote)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                        else inQuote = false;
                    }
                    else sb.Append(c);
                }
                else
                {
                    if (c == '"') inQuote = true;
                    else if (c == ',') { cells.Add(sb.ToString().Trim()); sb.Length = 0; }
                    else sb.Append(c);
                }
            }
            cells.Add(sb.ToString().Trim());
            return cells;
        }

        /// <summary>
        /// 把表头 + 数据行映射成 CsvUserRow 列表，并完成本地校验。
        /// duplicateChecker 用于预检查登录名是否已在域中存在（可为 null）。
        /// </summary>
        public static List<CsvUserRow> MapRows(List<string> header, List<string[]> data,
            string defaultOuDn, Func<string, bool> duplicateChecker)
        {
            List<CsvUserRow> result = new List<CsvUserRow>();
            Dictionary<string, int> colMap = BuildColumnMap(header);

            for (int i = 0; i < data.Count; i++)
            {
                string[] cells = data[i];
                CsvUserRow row = new CsvUserRow();
                row.LineNo = i + 2;   // CSV 第 1 行是表头

                row.SamAccountName = Cell(cells, colMap, "Sam");
                row.Name = Cell(cells, colMap, "Name");
                row.Password = Cell(cells, colMap, "Password");
                row.OuDn = Cell(cells, colMap, "Ou");
                row.Department = Cell(cells, colMap, "Department");
                row.Title = Cell(cells, colMap, "Title");
                row.Email = Cell(cells, colMap, "Email");
                row.Phone = Cell(cells, colMap, "Phone");
                row.Description = Cell(cells, colMap, "Description");

                if (string.IsNullOrEmpty(row.Name)) row.Name = row.SamAccountName;
                if (string.IsNullOrEmpty(row.OuDn)) row.OuDn = defaultOuDn;

                row.Error = Validate(row, duplicateChecker);
                result.Add(row);
            }
            return result;
        }

        private static Dictionary<string, int> BuildColumnMap(List<string> header)
        {
            Dictionary<string, int> map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < header.Count; i++)
            {
                string key = header[i] == null ? string.Empty : header[i].Trim().Trim('\uFEFF');
                if (string.IsNullOrEmpty(key)) continue;

                string field;
                if (Aliases.TryGetValue(key, out field))
                {
                    if (!map.ContainsKey(field)) map[field] = i;
                }
            }
            return map;
        }

        private static string Cell(string[] cells, Dictionary<string, int> colMap, string field)
        {
            int idx;
            if (!colMap.TryGetValue(field, out idx)) return null;
            if (idx >= cells.Length) return null;
            string v = cells[idx];
            return string.IsNullOrEmpty(v) ? null : v;
        }

        /// <summary>单行校验。返回 null 表示通过</summary>
        public static string Validate(CsvUserRow row, Func<string, bool> duplicateChecker)
        {
            string samError = AdService.ValidateSam(row.SamAccountName);
            if (samError != null) return samError;

            if (string.IsNullOrEmpty(row.Name)) return "姓名不能为空";
            if (AdService.ValidatePassword(row.Password) != null) return AdService.ValidatePassword(row.Password);

            if (duplicateChecker != null && duplicateChecker(row.SamAccountName))
                return "登录名在域中已存在";

            return null;
        }

        /// <summary>写 CSV 文件（带 UTF-8 BOM，便于 Excel 直接打开）</summary>
        public static void WriteCsv(string path, List<string> header, List<List<string>> rows)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(string.Join(",", header.ToArray())).Append("\r\n");
            foreach (List<string> r in rows)
            {
                List<string> escaped = new List<string>();
                foreach (string c in r) escaped.Add(EscapeCell(c));
                sb.Append(string.Join(",", escaped.ToArray())).Append("\r\n");
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        private static string EscapeCell(string s)
        {
            if (s == null) return string.Empty;
            if (s.IndexOfAny(new char[] { ',', '"', '\r', '\n' }) >= 0)
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }
    }
}
