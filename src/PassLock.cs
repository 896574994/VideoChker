/* -*- coding: utf-8 -*-
 * PassLock.cs — 开屏密码锁（**开源版**）
 *
 * ★★ 与原内部版相比，这里**刻意去掉**两样东西：
 *
 *   ① **主控密码** ✗
 *      原版有一个硬编码的主控密码，忘了密码时靠它进入并重设。
 *      开源版**没有**这个东西 —— 因为它同时也是一把"万能钥匙"：
 *      谁拿到源码、或者反编译一下 exe，就都知道了，等于锁没锁。
 *
 *   ② **注册表防绕过镜像** ✗
 *      原版把密码状态**同时**写进 HKCU\Software\VideoChecker，
 *      用来防"删掉 lock.dat 就等于解锁"。代价是：
 *        · 程序会去改注册表（有些环境不允许、也不好审计）
 *        · 密码状态跟着**这台机器**走 → 换个目录装、或者装到别人机器上，
 *          就会出现"我没设过密码，它却问我要密码"这类怪事 ✗
 *      开源版**只碰自己目录下的 data\lock.dat** ✓ 完全不写注册表 ✓
 *
 * ★ 保留的能力：
 *   · PBKDF2(SHA1, 1000 次迭代) + 16 字节随机盐 —— 文件里看不到明文密码 ✓
 *   · 连续输错 5 次 → 逐级锁定 1 / 5 / 15 / 60 / 240 分钟
 *     （锁定状态**写盘** ✓ 关掉程序重开也躲不过 ✓）
 *   · 设置 / 修改 / 取消密码 ✓
 *
 * ★ 忘了密码怎么办（开源版的"应急通道"）：
 *   **删掉程序目录下的 data\lock.dat** ✓ 下次打开就是全新状态（未设密码）✓
 *   —— 这不是漏洞：能碰到那个文件的人，本来也能直接改程序 ✗
 *
 * C# 5 兼容语法（没有字符串插值、没有 ?.、没有 async/await）。
 */
using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VideoChecker
{
    public static class PassLock
    {
        /// <summary>连续错几次开始锁定。</summary>
        public const int MaxAttempts = 5;

        /// <summary>锁定阶梯（分钟）：第 1 轮锁 1 分钟，第 5 轮及以后锁 240 分钟。</summary>
        private static readonly int[] LockMinutes = new int[] { 1, 5, 15, 60, 240 };

        /// <summary>密码状态文件：程序目录下 data\lock.dat ✓ 只用这一个地方 ✓</summary>
        private static string FilePath { get { return AppPaths.Data("lock.dat"); } }

        // ==================== 状态 ====================

        private class State
        {
            public bool Enabled;                     // 是否启用了开屏密码
            public bool EverChanged;                 // 用户是否自己设过（用于提示）
            public byte[] Salt;
            public byte[] Hash;
            public int FailCount;                    // 本轮连续错了几次
            public int LockLevel;                    // 已经锁过几轮
            public DateTime LockUntil = DateTime.MinValue;
        }

        private static State Load()
        {
            State s = new State();
            try
            {
                string path = FilePath;
                if (!File.Exists(path)) return s;
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string ln = lines[i].Trim();
                    if (ln.Length == 0 || ln[0] == '#') continue;
                    int eq = ln.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = ln.Substring(0, eq).Trim();
                    string v = ln.Substring(eq + 1).Trim();
                    try
                    {
                        if (k == "enabled") s.Enabled = (v == "1");
                        else if (k == "everchanged") s.EverChanged = (v == "1");
                        else if (k == "salt") s.Salt = Convert.FromBase64String(v);
                        else if (k == "hash") s.Hash = Convert.FromBase64String(v);
                        else if (k == "fail") { int n; if (int.TryParse(v, out n)) s.FailCount = n; }
                        else if (k == "locklevel") { int n; if (int.TryParse(v, out n)) s.LockLevel = n; }
                        else if (k == "lockuntil")
                        {
                            long ticks;
                            if (long.TryParse(v, out ticks) && ticks > 0) s.LockUntil = new DateTime(ticks);
                        }
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception ex) { Log.Warn("读取密码状态失败：" + ex.Message); }
            return s;
        }

        private static void Save(State s)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# 开屏密码锁状态。密码以 PBKDF2 哈希存储，看不到明文。");
                sb.AppendLine("# enabled=0 表示未启用开屏密码（直接进入程序）。");
                sb.AppendLine("# 本文件由程序自动维护，请勿手工修改。");
                sb.AppendLine("# ★ 忘记密码时：删掉本文件即可重置（下次打开就是未设密码状态）。");
                sb.AppendLine("enabled=" + (s.Enabled ? "1" : "0"));
                sb.AppendLine("everchanged=" + (s.EverChanged ? "1" : "0"));
                if (s.Salt != null) sb.AppendLine("salt=" + Convert.ToBase64String(s.Salt));
                if (s.Hash != null) sb.AppendLine("hash=" + Convert.ToBase64String(s.Hash));
                sb.AppendLine("fail=" + s.FailCount.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("locklevel=" + s.LockLevel.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("lockuntil=" + (s.LockUntil > DateTime.MinValue
                    ? s.LockUntil.Ticks.ToString(CultureInfo.InvariantCulture) : "0"));

                string path = FilePath;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            }
            catch (Exception ex) { Log.Warn("保存密码状态失败：" + ex.Message); }
        }

        // ==================== 哈希 ====================

        private static byte[] Derive(string pwd, byte[] salt)
        {
            using (Rfc2898DeriveBytes d = new Rfc2898DeriveBytes(pwd, salt, 1000))
            {
                return d.GetBytes(32);
            }
        }

        /// <summary>定长比较（不提前返回，少一点时序信息）。</summary>
        private static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= (a[i] ^ b[i]);
            return diff == 0;
        }

        // ==================== 对外：状态 ====================

        public static bool IsEnabled
        {
            get
            {
                State s = Load();
                return s.Enabled && s.Salt != null && s.Hash != null;
            }
        }

        public static bool IsLocked
        {
            get
            {
                State s = Load();
                return s.LockUntil > DateTime.Now;
            }
        }

        /// <summary>还要等多久（人话，例如 "3 分 20 秒"）。</summary>
        public static string LockRemaining()
        {
            State s = Load();
            TimeSpan left = s.LockUntil - DateTime.Now;
            if (left.TotalSeconds <= 0) return "0 秒";
            if (left.TotalMinutes >= 1)
                return ((int)left.TotalMinutes) + " 分 " + left.Seconds + " 秒";
            return ((int)left.TotalSeconds) + " 秒";
        }

        public static int RemainingTries()
        {
            State s = Load();
            int left = MaxAttempts - s.FailCount;
            return left < 0 ? 0 : left;
        }

        public static bool EverChanged
        {
            get { State s = Load(); return s.EverChanged; }
        }

        // ==================== 对外：验证 ====================

        /// <summary>只判断密码对不对 —— 不记失败、不写日志（给自检和只读场景用）。</summary>
        public static bool VerifyQuiet(string input)
        {
            State s = Load();
            if (!s.Enabled || s.Salt == null || s.Hash == null) return true;   // 没启用 → 任何输入都放行
            if (input == null) return false;
            return Same(Derive(input, s.Salt), s.Hash);
        }

        /// <summary>验证密码（启用后必须对；未启用时一律放行）。</summary>
        public static bool Verify(string input)
        {
            return VerifyQuiet(input);
        }

        /// <summary>记一次失败。返回 { 结果码, 提示语 }，结果码是 FAIL 或 LOCKED。</summary>
        public static string[] RecordFailure()
        {
            State s = Load();
            s.FailCount++;
            string msg = "密码错误，请重新输入";

            if (s.FailCount >= MaxAttempts)
            {
                int lvl = s.LockLevel;
                if (lvl >= LockMinutes.Length) lvl = LockMinutes.Length - 1;
                int minutes = LockMinutes[lvl];
                s.LockUntil = DateTime.Now.AddMinutes(minutes);
                s.LockLevel++;
                s.FailCount = 0;
                Save(s);
                Log.Warn("开屏密码连续错误达到 " + MaxAttempts + " 次，已锁定 " + minutes + " 分钟");
                msg = "密码连续错误 " + MaxAttempts + " 次，已锁定 " + minutes + " 分钟";
                return new string[] { "LOCKED", msg };
            }
            Save(s);
            return new string[] { "FAIL", msg };
        }

        // ==================== 对外：设置 / 修改 / 取消 ====================

        public static string SetPassword(string newPwd, string confirmPwd)
        {
            if (IsEnabled) return "当前已设置密码，请用「修改密码」";
            if (newPwd == null || newPwd.Length < 4) return "密码至少 4 位";
            if (newPwd != confirmPwd) return "两次输入的密码不一致";

            State s = Load();
            s.Salt = new byte[16];
            new RNGCryptoServiceProvider().GetBytes(s.Salt);
            s.Hash = Derive(newPwd, s.Salt);
            s.Enabled = true;
            s.EverChanged = true;
            s.FailCount = 0; s.LockLevel = 0; s.LockUntil = DateTime.MinValue;
            Save(s);
            Log.Info("已启用开屏密码");
            return "";
        }

        public static string ChangePassword(string oldPwd, string newPwd, string confirmPwd)
        {
            if (!IsEnabled) return "当前未设置密码，请用「设置密码」";
            if (IsLocked) return "密码锁处于锁定状态（等待 " + LockRemaining() + "）";
            if (!VerifyQuiet(oldPwd)) return "原密码不正确";
            if (newPwd == null || newPwd.Length < 4) return "新密码至少 4 位";
            if (newPwd != confirmPwd) return "两次输入的密码不一致";

            State s = Load();
            s.Salt = new byte[16];
            new RNGCryptoServiceProvider().GetBytes(s.Salt);
            s.Hash = Derive(newPwd, s.Salt);
            s.Enabled = true;
            s.EverChanged = true;
            s.FailCount = 0; s.LockLevel = 0; s.LockUntil = DateTime.MinValue;
            Save(s);
            Log.Info("已修改开屏密码");
            return "";
        }

        public static string ClearPassword(string pwd)
        {
            if (!IsEnabled) return "";
            if (IsLocked) return "密码锁处于锁定状态（等待 " + LockRemaining() + "）";
            if (!VerifyQuiet(pwd)) return "密码不正确";

            State s = Load();
            s.Enabled = false;
            s.Salt = null;
            s.Hash = null;
            s.FailCount = 0; s.LockLevel = 0; s.LockUntil = DateTime.MinValue;
            Save(s);
            Log.Info("已取消开屏密码（下次打开将直接进入）");
            return "";
        }
    }
}
