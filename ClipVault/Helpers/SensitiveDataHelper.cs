using System;
using System.Text.RegularExpressions;

namespace ClipVault.Helpers
{
    public static class SensitiveDataHelper
    {
        // 1. 主流 API Key / Token / JWT 匹配
        private static readonly Regex ApiKeyRegex = new(
            @"(?<token>\b(sk-[a-zA-Z0-9_\-]{12,}|gh[pousr]_[a-zA-Z0-9_]{16,}|github_pat_[a-zA-Z0-9_]{16,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z\-_]{25,}|xox[baprs]-[0-9a-zA-Z\-]{10,}|eyJ[a-zA-Z0-9_\-]{10,}\.eyJ[a-zA-Z0-9_\-]{10,}\.[a-zA-Z0-9_\-]{8,})\b|(?<=Bearer\s+)[a-zA-Z0-9\-._~+/]{12,}=*)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // 2. 密码/密钥键值赋值语句 (JSON / YAML / .env / 代码 / 常见账号密码文本)
        private static readonly Regex PasswordKvRegex = new(
            @"(?<prefix>(""|')?\b(password|passwd|pwd|secret|api_key|apikey|access_key|secret_key|private_key|auth_token|access_token|client_secret)\b(""|')?\s*[:=]\s*(""|')?|(?:\b(?:密码|口令|密钥)\s*[：:=]\s*))(?<val>[^\s'"",;}\]\r\n]{4,})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // 3. 数据库/服务连接串内嵌密码 (如 redis://:pass@host 或 postgres://user:pass@host)
        private static readonly Regex ConnStringSecretRegex = new(
            @"(?<prefix>\b(?:mongodb(?:\+srv)?|postgres(?:ql)?|mysql|redis|mssql|amqp)://[^:\s/@]*:)(?<val>[^@\s]+)(?=@)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // 4. 私钥证书块
        private static readonly Regex PrivateKeyBlockRegex = new(
            @"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]+?-----END [A-Z ]*PRIVATE KEY-----",
            RegexOptions.Compiled);

        // 5. 18位二代身份证号
        private static readonly Regex IdCardRegex = new(
            @"(?<!\d)[1-9]\d{5}(?:19|20)\d{2}(?:0[1-9]|1[0-2])(?:0[1-9]|[12]\d|3[01])\d{3}[\dXx](?!\d)",
            RegexOptions.Compiled);

        // 6. 银行卡号 / 信用卡号 (16~19位，支持空格或短横线分隔)
        private static readonly Regex BankCardRegex = new(
            @"(?<!\d)(?:[3-6]\d{3}[\s\-]\d{4}[\s\-]\d{4}[\s\-]\d{4}(?:[\s\-]?\d{1,3})?|[3-6]\d{15,18})(?!\d)",
            RegexOptions.Compiled);

        // 7. 11位中国大陆手机号码
        private static readonly Regex PhoneRegex = new(
            @"(?<!\d)1[3-9]\d{9}(?!\d)",
            RegexOptions.Compiled);

        public static (bool isSensitive, string badgeKey, string maskedText) InspectAndMask(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return (false, string.Empty, input ?? string.Empty);
            }

            string text = input;
            bool detected = false;
            string primaryBadgeKey = string.Empty;

            // 1. 私钥块
            if (PrivateKeyBlockRegex.IsMatch(text))
            {
                detected = true;
                primaryBadgeKey = "MaskTagSecret";
                text = PrivateKeyBlockRegex.Replace(text, "-----BEGIN PRIVATE KEY-----\n••••••••••••••••••••••••••••••••\n-----END PRIVATE KEY-----");
            }

            // 2. API Key / JWT / Bearer Token
            if (ApiKeyRegex.IsMatch(text))
            {
                detected = true;
                if (string.IsNullOrEmpty(primaryBadgeKey)) primaryBadgeKey = "MaskTagApiKey";
                text = ApiKeyRegex.Replace(text, m => MaskTokenMiddle(m.Value, keepStart: 5, keepEnd: 4));
            }

            // 3. 密码与密钥键值对
            if (PasswordKvRegex.IsMatch(text))
            {
                detected = true;
                if (string.IsNullOrEmpty(primaryBadgeKey)) primaryBadgeKey = "MaskTagPassword";
                text = PasswordKvRegex.Replace(text, m =>
                {
                    string prefix = m.Groups["prefix"].Value;
                    string val = m.Groups["val"].Value;
                    return prefix + MaskPasswordValue(val);
                });
            }

            // 4. 连接串密码
            if (ConnStringSecretRegex.IsMatch(text))
            {
                detected = true;
                if (string.IsNullOrEmpty(primaryBadgeKey)) primaryBadgeKey = "MaskTagPassword";
                text = ConnStringSecretRegex.Replace(text, m => m.Groups["prefix"].Value + "••••••••");
            }

            // 5. 18位身份证号 (先于银行卡匹配，避免18位纯数字被银行卡规则截胡)
            if (IdCardRegex.IsMatch(text))
            {
                detected = true;
                if (string.IsNullOrEmpty(primaryBadgeKey)) primaryBadgeKey = "MaskTagIdCard";
                text = IdCardRegex.Replace(text, m =>
                {
                    string v = m.Value;
                    if (v.Length == 18)
                    {
                        return v.Substring(0, 6) + "••••••••" + v.Substring(14, 4);
                    }
                    return MaskTokenMiddle(v, 4, 4);
                });
            }

            // 6. 银行卡号
            if (BankCardRegex.IsMatch(text))
            {
                detected = true;
                if (string.IsNullOrEmpty(primaryBadgeKey)) primaryBadgeKey = "MaskTagBankCard";
                text = BankCardRegex.Replace(text, m =>
                {
                    string rawDigits = Regex.Replace(m.Value, @"[\s\-]", "");
                    if (rawDigits.Length >= 16)
                    {
                        return $"{rawDigits.Substring(0, 4)} •••• •••• {rawDigits.Substring(rawDigits.Length - 4)}";
                    }
                    return MaskTokenMiddle(m.Value, 4, 4);
                });
            }

            // 7. 手机号码
            if (PhoneRegex.IsMatch(text))
            {
                detected = true;
                if (string.IsNullOrEmpty(primaryBadgeKey)) primaryBadgeKey = "MaskTagPhone";
                text = PhoneRegex.Replace(text, m =>
                {
                    string v = m.Value;
                    if (v.Length == 11)
                    {
                        return v.Substring(0, 3) + "••••" + v.Substring(7, 4);
                    }
                    return MaskTokenMiddle(v, 3, 3);
                });
            }

            return (detected, primaryBadgeKey, text);
        }

        private static string MaskTokenMiddle(string token, int keepStart, int keepEnd)
        {
            if (string.IsNullOrEmpty(token)) return "••••••";
            if (token.Length <= keepStart + keepEnd + 2)
            {
                int s = Math.Min(2, token.Length / 3);
                return token.Substring(0, s) + "••••••";
            }
            string start = token.Substring(0, keepStart);
            string end = token.Substring(token.Length - keepEnd);
            return $"{start}••••••••{end}";
        }

        private static string MaskPasswordValue(string val)
        {
            if (string.IsNullOrEmpty(val)) return "••••••••";
            if (val.Length <= 4) return "••••••";
            return val.Substring(0, 1) + "••••••" + val.Substring(val.Length - 1);
        }
    }
}
