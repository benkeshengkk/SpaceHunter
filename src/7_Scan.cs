using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace SpaceHunter
{
    // =====================================================================
    //  垃圾扫描
    //
    //  一次遍历把能认的东西全认出来，不重复扫盘 —— 大盘上重复 IO 是最卡的原因。
    //  每个盘一个线程，主线程只管收进度。
    // =====================================================================
    static class ScanEngine
    {
public class Result
        {
            public List<Finding> Items = new List<Finding>();
            public Dictionary<Cat, long> Sizes = new Dictionary<Cat, long>();
            public Dictionary<Cat, int> Counts = new Dictionary<Cat, int>();
            public long Total { get { long s = 0; foreach (long v in Sizes.Values) s += v; return s; } }
            public double Seconds;
            public int Drives;

            /// <summary>用户中途按了停止。结果是部分的，不能当成扫全了。</summary>
            public bool Cancelled;

            /// <summary>本次扫过哪些盘的根</summary>
            public List<string> ScannedRoots = new List<string>();
        }

        static bool IsStop(Func<bool> cancelled)
        {
            return cancelled != null && cancelled();
        }

        static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "$recycle.bin", "system volume information", "windows", "winsxs",
            "perflogs", "hiberfil.sys", "swapfile.sys", "pagefile.sys", "bootmgr",
            "recovery", "program files", "program files (x86)", "programdata",
            "system volume", "reset", "config.msi", "msocache", "$windows.~bt",
            "$windows.~ws", "windows.old", "oneboot", "minidump"
        };

        static readonly string[] InstallExt =
            { ".exe", ".msi", ".msix", ".appx", ".iso", ".zip", ".rar", ".7z", ".cab", ".img", ".wim" };

        static readonly string[] LogExt = { ".log", ".dmp", ".etl", ".old", ".tmp", ".bak" };

        static readonly string[] InstallerRoots =
        {
            @"Downloads", @"Desktop", @"Documents", @"Videos"
        };

        // 体积低于这个数的目录不值得当缓存列出来
        const long MinCacheDir = 8L * 1024 * 1024;

        // 重复文件检测的硬预算：候选数量上限 + 实际读取字节上限
        const int MaxDupCandidates = 60000;
        const long MaxHashBytes = 3L * 1024 * 1024 * 1024;

        // ---------------- 排除清单 ----------------
        static HashSet<string> excluded;

        public static void LoadExclusions()
        {
            excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string f = Path.Combine(Env.LocalAppData, @"SpaceHunter\exclude.txt");
                if (!File.Exists(f)) return;
                foreach (string line in File.ReadAllLines(f))
                {
                    string s = line.Trim();
                    if (s.Length > 1) excluded.Add(s.TrimEnd('\\'));
                }
            }
            catch { }
        }

        public static void AddExclusion(string p)
        {
            if (excluded == null) LoadExclusions();
            try
            {
                excluded.Add(p.TrimEnd('\\'));
                string f = Path.Combine(Env.LocalAppData, @"SpaceHunter\exclude.txt");
                string dir = Path.GetDirectoryName(f);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(f, p.TrimEnd('\\') + Environment.NewLine, EncodingUTF8);
            }
            catch { }
        }

        static readonly System.Text.UTF8Encoding EncodingUTF8 = new System.Text.UTF8Encoding(false);

        /// <summary>--only=… 沙箱过滤；null 表示正常全盘扫</summary>
        public static string ScanOnly;

public static bool IsExcluded(string p)
        {
            // 隔离区自己绝对不能出现在结果里。
            //
            // 漏掉这条会出事：隔离区里存的是**原路径的完整结构**，
            // 扫到它们以后再往隔离区里搬，就会变成
            //     隔离区\C\...\SpaceHunter\隔离区\C\...\xxx
            // 层层套娃，自己把自己的东西当垃圾清掉了。
            // （实测真的发生过：@opencode-aidesktop\d3dcompiler_47.dll
            //   被搬到了嵌套的隔离区里。）
            //
            // 注意这一句必须在 exclude 为空时的提前返回**之前** ——
            // 放后面的话，排除清单为空时（最常见的出厂状态）
            // 整个隔离区检查会被跳过，等于形同虚设。
            if (InsideQuarantine(p)) return true;

            if (excluded == null || excluded.Count == 0) return false;
            if (excluded.Contains(p)) return true;
            foreach (string x in excluded)
                if (p.StartsWith(x + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static string quarantineRoot;

        /// <summary>这个路径是不是在隔离区里面（含隔离区自身）</summary>
        public static bool InsideQuarantine(string p)
        {
            if (string.IsNullOrEmpty(p)) return false;
            if (quarantineRoot == null)
            {
                try { quarantineRoot = Quarantine.Root.TrimEnd('\\'); } catch { return false; }
            }
            string n = p.TrimEnd('\\');
            return string.Equals(n, quarantineRoot, StringComparison.OrdinalIgnoreCase) ||
                   n.StartsWith(quarantineRoot + "\\", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 这是不是链接点（junction / 符号链接 / 挂载点 / 云盘占位符）。
        ///
        /// 必须一律跳过。原因很实在：
        ///   C:\Users\All Users     ->  C:\ProgramData
        ///   C:\Users\Default User  ->  C:\Users\Default
        ///
        /// 跟着链接走有两个后果，第二个是致命的：
        ///   1) 同一份数据被重复扫一遍（Profile 变慢）
        ///   2) **清理时会把链接指向的真实目录整个搬走** ——
        ///      C:\ProgramData\Microsoft\Windows Defender\Support
        ///      就是这么被搬进隔离区的。系统目录被隔离区程序搬走是要出事的。
        /// </summary>
        public static bool IsLink(string p)
        {
            try { return (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0; }
            catch { return false; }
        }

        /// <summary>路径里任何一段是链接就返回 true</summary>
        public static bool HasLinkInPath(string p)
        {
            try
            {
                string root = Path.GetPathRoot(p);
                if (string.IsNullOrEmpty(root)) return false;
                string cur = root;
                foreach (string seg in p.Substring(root.Length).Split('\\'))
                {
                    if (seg.Length == 0) continue;
                    cur = Path.Combine(cur, seg);
                    if (IsLink(cur)) return true;
                }
            }
            catch { }
            return false;
        }

        // ============================================================
        //  系统目录保护
        //
        //  按「路径前缀」判断，不是按目录名。
        //  之前只按目录名跳过（SkipDirs 里写 "program files"），
        //  结果漏掉了 C:\ProgramData\Microsoft\Windows Defender ——
        //  那个 Support 目录就是这么被当成垃圾搬走的。
        //
        //  下面这些根目录下面的东西，**一律不进垃圾清单**。
        //  判断用「最长前缀匹配」，并且比较时忽略大小写和结尾斜杠差异。
        // ============================================================
        static List<string> protectedRoots;

        public static void InitProtected()
        {
            protectedRoots = new List<string>();

            // Windows 本身
            AddProt(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            AddProt(Environment.GetFolderPath(Environment.SpecialFolder.System));
            AddProt(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86));

            // 每块盘上的程序目录 / 系统启动相关目录。
            // 必须遍历所有盘 —— 很多人把软件装在 D:\Program Files，
            // 只保护 C:\ 的话 D 盘的系统目录就漏了。
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    if (!d.IsReady) continue;
                    if (d.DriveType != DriveType.Fixed) continue;
                    string r = d.RootDirectory.FullName;
                    AddProt(Path.Combine(r, "Program Files"));
                    AddProt(Path.Combine(r, "Program Files (x86)"));
                    AddProt(Path.Combine(r, "ProgramData"));
                    AddProt(Path.Combine(r, "Boot"));
                    AddProt(Path.Combine(r, "Recovery"));
                    AddProt(Path.Combine(r, "$Recycle.Bin"));
                    AddProt(Path.Combine(r, "System Volume Information"));
                    AddProt(Path.Combine(r, "PerfLogs"));
                }
            }
            catch { }

            // 兜底：万一盘枚举失败，至少把 C:\ProgramData 这类常见位置挡住
            AddProt(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        }

        static void AddProt(string p)
        {
            if (string.IsNullOrEmpty(p)) return;
            try
            {
                string s = p.TrimEnd('\\');
                if (s.Length < 2) return;                 // 排除 "C:\" 这种根本身
                if (protectedRoots.Contains(s)) return;
                protectedRoots.Add(s);
            }
            catch { }
        }

        /// <summary>
        /// 这个路径是不是在受保护的系统目录里（包含「恰好就是根」本身）。
        ///
        /// 比较必须同时看长度和内容。踩过的坑：
        ///   C:\System Volume Information      28 字符
        ///   某个用户的 \AppData\Roaming       28 字符
        /// 只按长度判「就是根目录」，用户的 Roaming 就被当成系统目录拦掉了。
        /// </summary>
        public static bool IsProtected(string p)
        {
            if (protectedRoots == null) InitProtected();
            if (string.IsNullOrEmpty(p)) return false;
            try
            {
                string norm = p.TrimEnd('\\');
                for (int i = 0; i < protectedRoots.Count; i++)
                {
                    string r = protectedRoots[i];
                    if (norm.Length < r.Length) continue;

                    // 长度相等时**必须比内容**，而且不相等要**继续看下一个**。
                    // 这里踩过两次，都写下来免得再犯：
                    //   只比长度    -> 某个 28 字符的系统路径 和 某个 28 字符的
                    //                  用户目录撞上了，用户目录被误判成系统目录
                    //   不等就 return -> D:\Program Files 撞上 C:\Program Files
                    //                  （都是 16 字符）直接被判成「不受保护」，
                    //                  后面的 D:\Program Files 条目根本没机会比
                    // 长度相同时必须比内容，而且不相等要**继续看下一个**。
                    if (norm.Length == r.Length)
                    {
                        if (string.Equals(norm, r, StringComparison.OrdinalIgnoreCase))
                            return true;
                        continue;
                    }

                    if (string.Equals(norm.Substring(0, r.Length), r,
                        StringComparison.OrdinalIgnoreCase) &&
                        norm[r.Length] == '\\')
                        return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>一句话说明为什么不能碰，给界面用</summary>
        public static string WhyProtected(string p)
        {
            if (IsLink(p) || HasLinkInPath(p)) return "路径上有链接";
            if (IsProtected(p)) return "属于系统目录";
            return "";
        }

        // ---------------- 主入口 ----------------
        public static Result Run(List<DriveX> drives, Func<bool> cancelled,
            Action<int, int, string> prog)
        {
LoadExclusions();
            InitProtected();
            Result res = new Result();
            res.Drives = drives == null ? 0 : drives.Count;
            DateTime t0 = DateTime.Now;

            List<Finding> all = new List<Finding>();
            List<FileInfo> dupCandidates = new List<FileInfo>();
            List<Finding> orphans = new List<Finding>();

            int done = 0;
            int totalUnits = (drives == null || drives.Count == 0) ? 1 : drives.Count;

            // ---- 第一轮：每个盘走一遍 ----
            for (int i = 0; i < totalUnits; i++)
            {
if (cancelled != null && cancelled()) break;
                if (drives == null || drives.Count == 0) break;
                string root = drives[i].Root;

                WalkDrive(root, cancelled,
                    delegate(int n, int m, string s)
                    {
                        if (prog != null)
                            prog(done * 100 + Math.Min(99, n * 99 / Math.Max(1, m)), totalUnits * 100, s);
                    },
                    all, dupCandidates);

                if (cancelled != null && cancelled()) break;
                done++;
                if (prog != null) prog(done * 100, totalUnits * 100, drives[i].Title);
            }

            bool stop = false;
            if (IsStop(cancelled)) stop = true;

            if (!stop)
            {
                if (prog != null) prog(totalUnits * 100, totalUnits * 100, "正在比对重复文件…");
                FindDuplicates(dupCandidates, all, cancelled);
                stop = IsStop(cancelled);
            }

            if (!stop)
            {
                if (prog != null) prog(totalUnits * 100, totalUnits * 100, "正在核对卸载残留…");
                FindOrphans(orphans, cancelled);
                all.AddRange(orphans);
                stop = IsStop(cancelled);
            }

            res.Cancelled = stop;

// 同一路径只保留一条（重复文件会和别的分类撞上）
            Dictionary<string, Finding> uniq = new Dictionary<string, Finding>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Size <= 0) continue;
                if (uniq.ContainsKey(all[i].Path)) continue;

                // 兜底：链接路径 / 系统目录一律不进清单。
                // 上面遍历时已经跳过一次，这里再挡一次 —— 因为孤注一掷的
                // 代价比误报高得多：漏扫一个目录没人会发现，
                // 而搬走一个系统目录是要出事的。
                if (IsLink(all[i].Path) || HasLinkInPath(all[i].Path)) continue;
                if (IsProtected(all[i].Path)) continue;

                // 总闸：隔离区里的东西一个都不能进清单。
                // 遍历时已经跳过一次，这里再挡一次 —— 隔离区里存的是原路径的
                // 完整结构，它自己不会被遍历到，但它**内部的路径**可能被
                // FindDuplicates / FindOrphans 之类的旁路收进来。
                if (InsideQuarantine(all[i].Path)) continue;

                uniq[all[i].Path] = all[i];
                res.Items.Add(all[i]);
            }

// 清理区自己绝不能出现在结果里
            for (int i = res.Items.Count - 1; i >= 0; i--)
                if (IsExcluded(res.Items[i].Path)) res.Items.RemoveAt(i);

            // --only=<路径片段>：只保留路径里含这段的结果。
            // 用来在一台装着真实数据的机器上，安全地跑通「勾选 -> 清理」整条链路，
            // 而不会碰到用户自己的文件。正式使用不传这个参数。
            string only = ScanOnly;
            if (!string.IsNullOrEmpty(only))
            {
                for (int i = res.Items.Count - 1; i >= 0; i--)
                    if (res.Items[i].Path.IndexOf(only, StringComparison.OrdinalIgnoreCase) < 0)
                        res.Items.RemoveAt(i);
            }

            foreach (Finding f in res.Items)
            {
                res.Sizes[f.Cat] = Get(res.Sizes, f.Cat) + f.Size;
                res.Counts[f.Cat] = Get(res.Counts, f.Cat) + 1;
            }

            res.Seconds = (DateTime.Now - t0).TotalSeconds;
            return res;
        }

        static long Get(Dictionary<Cat, long> d, Cat k)
        {
            long v; return d.TryGetValue(k, out v) ? v : 0;
        }
        static int Get(Dictionary<Cat, int> d, Cat k)
        {
            int v; return d.TryGetValue(k, out v) ? v : 0;
        }

        // ---------------- 单盘遍历 ----------------
        static void WalkDrive(string root, Func<bool> cancelled,
            Action<int, int, string> prog,
            List<Finding> outItems, List<FileInfo> dupCandidates)
        {
            Stack<string> stack = new Stack<string>();
            HashSet<string> seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            stack.Push(root);
            seenDirs.Add(root);

            int tick = 0;
            DateTime old = DateTime.Now.AddDays(-90);
            bool inInstallerRoot = false;
            string installRoot = "";

while (stack.Count > 0)
            {
                if ((++tick & 31) == 0 && IsStop(cancelled)) return;

                string dir;
                try { dir = stack.Pop(); } catch { break; }

                // 目录判定跟具体文件无关 —— 每个文件都重算一次 ToLowerInvariant
                // 会在几百万个文件上白烧掉大量 CPU，这里提出来只算一次。
                string dirLow = dir.ToLowerInvariant();
                bool chatDir = IsChatDir(dirLow);
                bool cacheDir = IsCacheDir(dirLow);

                string[] files;
                try { files = Directory.GetFiles(dir); }
                catch { continue; }

                long chatBytes = 0, logBytes = 0, cacheBytes = 0, instBytes = 0;
                string chatSample = null, logSample = null, cacheSample = null, instSample = null;

for (int i = 0; i < files.Length; i++)
                {
                    // 单个目录里可能有几万个文件（node_modules、.git 之类）。
                    // 只在最外层目录循环检查取消的话，点停止要等一整个目录走完
                    // 才生效 —— 那体感就跟「按钮没用」一样。
                    if ((i & 2047) == 0 && i > 0 && IsStop(cancelled)) return;

                    string f = files[i];
                    string nm;
                    try { nm = Path.GetFileName(f); } catch { continue; }
                    string low = nm.ToLowerInvariant();
                    if (low.EndsWith(".shw~")) continue;

                    FileInfo fi;
                    try { fi = new FileInfo(f); } catch { continue; }
                    long len = fi.Length;
                    if (len <= 0) continue;
                    DateTime acc = fi.LastAccessTime;

                    // 重复文件候选：1MB ~ 256MB，太大或太小都不划算
                    // （数量必须有上限，否则后面 MD5 会把整块盘读一遍）
                    if (len >= 1024 * 1024 && len <= 256L * 1024 * 1024 &&
                        dupCandidates.Count < MaxDupCandidates)
                    {
                        try { dupCandidates.Add(fi); } catch { }
                    }

                    // 分类
                    if (IsLog(low, nm)) { logBytes += len; if (logSample == null) logSample = f; }
                    else if (IsInstallerName(low)) { instBytes += len; if (instSample == null) instSample = f; }
                    else if (inInstallerRoot && len > 4L * 1024 * 1024 && HasInstallExt(low))
                    { instBytes += len; if (instSample == null) instSample = f; }
                    else if (chatDir)
                    { chatBytes += len; if (chatSample == null) chatSample = f; }
                    else if (cacheDir)
                    { cacheBytes += len; if (cacheSample == null) cacheSample = f; }

                    if (len > 800L * 1024 * 1024 && !chatDir && !cacheDir)
                    {
                        if (!IsExcluded(f))
                            outItems.Add(new Finding(f, len, Cat.Big,
                                Fmt.Size(len) + " 的大文件"));
                    }
                    if (acc < old && !cacheDir && !chatDir)
                    {
                        if (len > 32L * 1024 * 1024 && !IsExcluded(f))
                            outItems.Add(new Finding(f, len, Cat.OldFile,
                                Fmt.Age(acc) + "没打开过"));
                    }
                }

                if (logBytes > MinCacheDir && logSample != null)
                    outItems.Add(new Finding(dir, logBytes, Cat.Log,
                        "日志 / 崩溃记录 " + Fmt.Size(logBytes)));
                if (chatBytes > MinCacheDir && chatSample != null)
                    outItems.Add(new Finding(dir, chatBytes, Cat.Chat,
                        "聊天缓存 " + Fmt.Size(chatBytes)));
                if (cacheBytes > MinCacheDir && cacheSample != null)
                    outItems.Add(new Finding(dir, cacheBytes, Cat.Cache,
                        "删掉会自动重建 " + Fmt.Size(cacheBytes)));
                if (instBytes > 64L * 1024 * 1024 && instSample != null && inInstallerRoot)
                    outItems.Add(new Finding(dir, instBytes, Cat.Installer,
                        "安装包 / 压缩包 " + Fmt.Size(instBytes)));

                // 回收站
                if (nmEq(dir, "$recycle.bin")) continue;

                string[] subs;
                try { subs = Directory.GetDirectories(dir); }
                catch { continue; }

for (int i = 0; i < subs.Length; i++)
                {
                    if ((i & 2047) == 0 && i > 0 && IsStop(cancelled)) return;

                    string s = subs[i];
                    string sn;
                    try { sn = Path.GetFileName(s); } catch { continue; }
                    if (SkipDirs.Contains(sn)) continue;
                    if (IsExcluded(s)) continue;

                    // 链接点一律不进：跟着 junction 走会重复扫一遍同一份数据，
                    // 而且清理时会把链接指向的真实目录整个搬走
                    // （Windows Defender 的 Support 目录就是这么丢的）。
                    if (IsLink(s)) continue;

                    // 系统目录一律不进。
                    if (IsProtected(s)) continue;

                    // 隔离区一律不进（防止自己把自己的东西当垃圾）
                    if (InsideQuarantine(s)) continue;

                    if (!seenDirs.Add(s)) continue;
                    stack.Push(s);

                    // 下载 / 桌面 这种地方单独标一下，用来多收安装包
                    bool was = inInstallerRoot;
                    if (IsInstallerRoot(sn)) { inInstallerRoot = true; installRoot = s; }
                    else inInstallerRoot = false;
                    // 恢复（栈是后进先出，这里只影响紧邻的一层，够用）
                    if (!was) inInstallerRoot = IsUnderInstallerRoot(s, installRoot);
                }

                if (prog != null && (tick & 7) == 0)
                    prog(Math.Min(tick, 4000), 4000, "正在扫 " + Draw.Shorten(dir, 40));
            }
        }

        static bool IsUnderInstallerRoot(string p, string root)
        {
            if (string.IsNullOrEmpty(root)) return false;
            return p.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
        }

        static bool nmEq(string dir, string name)
        {
            try { return string.Equals(Path.GetFileName(dir.TrimEnd('\\')), name, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        static bool IsInstallerRoot(string dirName)
        {
            for (int i = 0; i < InstallerRoots.Length; i++)
                if (dirName.Equals(InstallerRoots[i], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static bool HasInstallExt(string low)
        {
            for (int i = 0; i < InstallExt.Length; i++)
                if (low.EndsWith(InstallExt[i], StringComparison.Ordinal)) return true;
            return false;
        }

        static bool IsInstallerName(string low)
        {
            if (!HasInstallExt(low)) return false;
            // setup / installer / install / v?.?.? / 全是数字版本
            if (low.Contains("setup") || low.Contains("install")) return true;
            string stem = low;
            int dot = stem.LastIndexOf('.');
            if (dot > 0) stem = stem.Substring(0, dot);
            bool digit = stem.Length > 0;
            for (int i = 0; i < stem.Length; i++)
                if (stem[i] < '0' || stem[i] > '9') { digit = false; break; }
            if (digit && stem.Length > 2) return true;
            return false;
        }

        static bool IsLog(string low, string nm)
        {
            for (int i = 0; i < LogExt.Length; i++)
                if (low.EndsWith(LogExt[i], StringComparison.Ordinal)) return true;
            return nm.StartsWith("WER", StringComparison.OrdinalIgnoreCase) && low.EndsWith(".tmp.wer", StringComparison.Ordinal);
        }

        static bool IsChatDir(string dir)
        {
            string low = dir.ToLowerInvariant();
            if (low.Contains(@"\tencent\wechat") || low.Contains(@"\tencent\qq"))
                return low.Contains("image") || low.Contains("video") || low.Contains("file")
                    || low.Contains("cache") || low.Contains("emoji") || low.Contains("avatar");
            if (low.Contains("wechat") || low.Contains(@"weixin\"))
                return low.Contains("image") || low.Contains("video") || low.Contains("cache");
            if (low.Contains(@"qq\") || low.Contains(@"\qq\"))
                return low.Contains("image") || low.Contains("video") || low.Contains("recv") || low.Contains("file");
            return false;
        }

        static bool IsCacheDir(string dir)
        {
            string low = dir.ToLowerInvariant();
            if (low.EndsWith(@"\cache") || low.EndsWith(@"\caches") || low.EndsWith(@"\cache2")
                || low.EndsWith(@"\gpuCache") || low.EndsWith(@"\code cache") || low.EndsWith(@"\shadercache"))
                return true;
            if (low.Contains(@"\appdata\local\google\chrome\user data") && low.Contains("cache"))
                return true;
            if (low.Contains(@"\appdata\local\microsoft\edge\user data") && low.Contains("cache"))
                return true;
            if (low.Contains(@"\appdata\local\crashdumps")) return true;
            if (low.Contains(@"\appdata\local\installer")) return true;
            if (low.EndsWith(@"\temp") && low.Contains(@"\appdata\")) return true;
            if (low.Contains(@"\appdata\local\pip\cache")) return true;
            if (low.Contains(@"\appdata\local\npm-cache")) return true;
            if (low.Contains(@"\.gradle\caches")) return true;
            if (low.Contains(@"\appdata\roaming\npm-cache")) return true;
            if (low.Contains(@"\appdata\local\yarn\cache")) return true;
            if (low.EndsWith(@"\windows\prefetch")) return true;
            if (low.Contains(@"\appdata\local\vkcache")) return true;
            if (low.Contains(@"\appdata\roaming\discord\cache")) return true;
            if (low.Contains(@"\appdata\roaming\slack\cache")) return true;
            return false;
        }

        // ---------------- 重复文件 ----------------
        static void FindDuplicates(List<FileInfo> candidates, List<Finding> outItems, Func<bool> cancelled)
{
            Dictionary<long, List<FileInfo>> bySize = new Dictionary<long, List<FileInfo>>();
            for (int i = 0; i < candidates.Count; i++)
            {
                if ((i & 8191) == 0 && i > 0 && IsStop(cancelled)) return;
                FileInfo fi = candidates[i];
                if (!fi.Exists) continue;
                if (IsExcluded(fi.FullName)) continue;
                List<FileInfo> l;
                if (!bySize.TryGetValue(fi.Length, out l)) { l = new List<FileInfo>(); bySize[fi.Length] = l; }
                l.Add(fi);
            }

            // 算 MD5 是要真的读文件的。给它一个总预算（默认 3 GB），
            // 免得在满盘的系统上卡上半小时 —— 超了就到此为止，报告里说明。
            long budget = MaxHashBytes;
            bool ranOut = false;

            foreach (KeyValuePair<long, List<FileInfo>> kv in bySize)
            {
                if (kv.Value.Count < 2) continue;
                if (IsStop(cancelled)) return;
                // 同尺寸文件一多（>12）多半是巧合或填充文件，比了也白比
                if (kv.Value.Count > 12) continue;

                // 字节数一样不等于内容一样，必须真的比一遍
                Dictionary<string, FileInfo> seen = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < kv.Value.Count; i++)
                {
                    FileInfo fi = kv.Value[i];
                    if (IsStop(cancelled)) return;
                    if (fi.Length > budget) { ranOut = true; break; }
                    string h;
                    try { h = Hash(fi); }
                    catch { continue; }
                    budget -= fi.Length;

                    FileInfo first;
                    if (seen.TryGetValue(h, out first))
                    {
                        if (IsUnder(first.FullName, fi.FullName)) continue;  // 留最早的
                        outItems.Add(new Finding(fi.FullName, fi.Length, Cat.Dup,
                            "和 " + Path.GetFileName(first.FullName) + " 一模一样"));
                    }
                    else seen[h] = fi;
                }
                if (ranOut) break;
            }
        }

        static bool IsUnder(string maybeChild, string other)
        {
            return maybeChild.StartsWith(other.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }

        static string Hash(FileInfo fi)
        {
            using (MD5 md5 = MD5.Create())
            using (FileStream fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                return Convert.ToBase64String(md5.ComputeHash(fs));
        }

// ---------------- 卸载残留 ----------------
        static void FindOrphans(List<Finding> outItems, Func<bool> cancelled)
        {
            // 判据：AppData 下这个目录的名字，在 Program Files / 注册表里都找不到对应程序，
            // 而且它自己不小 —— 三条都满足才敢列出来让人勾。

            // 这里刻意只做「轻量盘点」：读注册表名字 + 列 Program Files 的目录名。
            // 早先这里直接调了完整的软件扫描（含快捷方式 COM），结果垃圾扫描
            // 顺带把软件扫描重跑一遍，慢得离谱 —— 而且它自己就是被判残留的对象。

            if (IsStop(cancelled)) return;
            HashSet<string> known = KnownInstallNames();

            string[] roots = { Env.LocalAppData, Env.AppData };
            int budget = 120;                    // 最多核这么多目录，别把时间全烧在 AppData 上
            for (int r = 0; r < roots.Length; r++)
            {
                if (IsStop(cancelled)) return;
                string[] subs;
                try { if (!Directory.Exists(roots[r])) continue; subs = Directory.GetDirectories(roots[r]); }
                catch { continue; }

                for (int i = 0; i < subs.Length; i++)
                {
                    // 这一段以前完全没有取消检查，而每个候选都要 DirSizeFast 走一遍整个目录
                    // （几十万次入栈出栈）。结果就是：扫到最后这阶段点停止，
                    // 还得把几十个 AppData 目录全走完才停 —— 看起来跟按钮坏了一样。
                    if (IsStop(cancelled)) return;
                    if (budget <= 0) return;

                    string s = subs[i];
                    if (IsExcluded(s)) continue;
                    string name;
                    try { name = Path.GetFileName(s); } catch { continue; }
                    string norm = Fmt.Norm(name);
                    if (norm.Length < 3) continue;
                    if (known.Contains(norm)) continue;
                    bool underKnown = false;
                    foreach (string k in known)
                        if (k.Length > 4 && norm.StartsWith(k, StringComparison.Ordinal)) { underKnown = true; break; }
                    if (underKnown) continue;

                    budget--;
                    long sz = Quarantine.DirSizeFast(s, cancelled);
                    if (sz < 0) return;                       // 被取消了
                    if (sz < 64L * 1024 * 1024) continue;

                    DateTime when;
                    try { when = Directory.GetLastWriteTime(s); } catch { continue; }
                    if (DateTime.Now - when > TimeSpan.FromDays(60)) continue;   // 半年内还在动的，多半在用

                    outItems.Add(new Finding(s, sz, Cat.Orphan,
                        "找不到对应程序 · " + Fmt.Age(when) + "没变化"));
                }
            }
        }

        /// <summary>只拿「装过的软件的标识」，不做完整扫描</summary>
        static HashSet<string> KnownInstallNames()
        {
            HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            List<string> subs = new List<string>();
            subs.Add(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            subs.Add(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");

            for (int i = 0; i < subs.Count; i++)
            {
                RegistryKey root = null;
                try { root = Registry.LocalMachine.OpenSubKey(subs[i]); } catch { }
                if (root == null) continue;
                string[] names;
                try { names = root.GetSubKeyNames(); } catch { continue; }
                for (int j = 0; j < names.Length; j++)
                {
                    RegistryKey k = null;
                    try
                    {
                        k = root.OpenSubKey(names[j]);
                        if (k == null) continue;
                        string[] vals = { "DisplayName", "InstallLocation", "Publisher" };
                        for (int v = 0; v < vals.Length; v++)
                        {
                            string s = k.GetValue(vals[v]) as string;
                            if (string.IsNullOrEmpty(s)) continue;
                            string n = Fmt.Norm(s);
                            if (n.Length >= 3) known.Add(n);
                            // "Adobe Acrobat Reader" -> "adobe"
                            int sp = n.IndexOf("reader");
                            if (n.Length > 4) known.Add(n);
                        }
                        string loc = k.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrEmpty(loc))
                        {
                            string n = Fmt.Norm(loc);
                            if (n.Length >= 3) known.Add(n);
                        }
                    }
                    catch { }
                    finally { if (k != null) try { k.Close(); } catch { } }
                }
                try { root.Close(); } catch { }
            }

            // Program Files 下的目录名也算「还在」的反证
            List<string> dirs = Installer.FixedDrives();
            for (int i = 0; i < dirs.Count; i++)
            {
                string[] pf = { "Program Files", "Program Files (x86)" };
                for (int j = 0; j < pf.Length; j++)
                {
                    try
                    {
                        string p = Path.Combine(dirs[i], pf[j]);
                        if (!Directory.Exists(p)) continue;
                        foreach (string d in Directory.GetDirectories(p))
                        {
                            string n = Fmt.Norm(Path.GetFileName(d));
                            if (n.Length >= 3) known.Add(n);
                        }
                    }
                    catch { }
                }
            }
            return known;
        }

        // ---------------- 回收站 ----------------
        public static long RecycleBinSize()
        {
            long total = 0;
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    string rb = Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin");
                    if (!Directory.Exists(rb)) continue;
                    string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
                    string mine = Path.Combine(rb, sid);
                    if (Directory.Exists(mine)) total += Quarantine.DirSizeFast(mine);
                }
            }
            catch { }
            return total;
        }
    }
}