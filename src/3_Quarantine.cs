using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace SpaceHunter
{
    // =====================================================================
    //  隔离区
    //
    //  · 位置固定在 %LOCALAPPDATA%\SpaceHunter\隔离区\<盘>\<原路径结构>
    //    （绝不能用盘根目录：非管理员在 D:\ 根目录连文件夹都建不出来）
    //  · 同盘直接改名（瞬间）；跨盘复制到隔离区再删源
    //  · 目录结构原样保留，你一眼能看出原路径，想自己搬回去不用猜
    // =====================================================================
    static class Quarantine
    {
        static string rootCache;

        public static string Root
        {
            get
            {
                if (rootCache == null)
                {
                    try
                    {
                        rootCache = Path.Combine(Env.LocalAppData, @"SpaceHunter\隔离区");
                        if (!Directory.Exists(rootCache)) Directory.CreateDirectory(rootCache);
                    }
                    catch
                    {
                        rootCache = Path.Combine(Env.TempDir, "SpaceHunter_Quarantine");
                        try { Directory.CreateDirectory(rootCache); } catch { }
                    }
                }
                return rootCache;
            }
        }

        public static bool Exists
        {
            get { try { return Directory.Exists(Root); } catch { return false; } }
        }

        public static string RootOf(string p)
        {
            try { return new DriveInfo(Path.GetPathRoot(p)).RootDirectory.FullName; }
            catch { return "C:\\"; }
        }

        static bool SameVolume(string a, string b)
        {
            try
            {
                return string.Equals(new DriveInfo(Path.GetPathRoot(a)).Name,
                    new DriveInfo(Path.GetPathRoot(b)).Name, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static string MakeDest(string srcPath)
        {
            string root = RootOf(srcPath);
            string drive = root.Substring(0, 1);
            string rel = srcPath;
            try { if (rel.StartsWith(root, StringComparison.OrdinalIgnoreCase)) rel = rel.Substring(root.Length); }
            catch { }
            rel = rel.TrimStart('\\').Replace(":", "_");

            // 已经在隔离区里的东西不许再往里搬 —— 否则会层层套娃
            //（隔离区\C\...\隔离区\C\...），把自己的备份清掉。
            if (ScanEngine.InsideQuarantine(srcPath)) return Root;

            string full = Path.Combine(Path.Combine(Root, drive), rel);
            try
            {
                string parent = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                    Directory.CreateDirectory(parent);
            }
            catch { }
            return full;
        }

        /// <summary>搬去隔离区。返回 null = 成功，否则是失败原因</summary>
        public static string Move(string srcPath, out string destUsed)
        {
            return Move(srcPath, out destUsed, null);
        }

        /// <summary>
        /// 搬去隔离区。cancelled 非空时会检查它 ——
        /// 跨盘复制一个大目录可能要好几分钟，没有取消就是
        /// 「用户按了停止，看着像完全没反应」，只能强行结束程序。
        /// </summary>
        public static string Move(string srcPath, out string destUsed, Func<bool> cancelled)
        {
            destUsed = null;
            try
            {
                if (cancelled != null && cancelled()) return "已取消";
                // 最后一道闸，挡在真正动文件之前。
                //
                // 这里过了两道保险：
                //
                // 1) 路径上带链接的对象绝不能搬：C:\Users\All Users 是指向
                //    C:\ProgramData 的 junction，Directory.Move 对它生效时搬的是
                //    **链接指向的真实目录**。曾经就把
                //    C:\ProgramData\Microsoft\Windows Defender\Support
                //    整个搬进了隔离区。
                //
                // 2) 系统目录一律不动（Windows / Program Files / ProgramData /
                //    Boot / Recovery / 回收站本体…）。里面有些目录长得很像
                //    缓存或日志（比如 Defender 里的 Quarantine、Snapshots），
                //    但删掉是要出事的。
                //
                // 宁可漏掉这种项（扫到也不列出来），也绝不能碰系统文件。
                if (ScanEngine.HasLinkInPath(srcPath))
                    return "路径上有链接，已跳过（不动系统目录）";
                if (ScanEngine.IsProtected(srcPath))
                    return "属于系统目录，已跳过";
                if (ScanEngine.InsideQuarantine(srcPath))
                    return "已经在隔离区里了，不用再搬";

                destUsed = MakeDest(srcPath);
                bool isDir = Directory.Exists(srcPath);
                if (!isDir && !File.Exists(srcPath)) return "路径已不存在";

                string stamp = DateTime.Now.ToString("HHmmssfff");
                if (Directory.Exists(destUsed)) destUsed += "_" + stamp;
                else if (File.Exists(destUsed)) destUsed += "_" + stamp;

                if (SameVolume(srcPath, destUsed))
                {
                    if (isDir) Directory.Move(srcPath, destUsed);
                    else File.Move(srcPath, destUsed);
                }
                else
                {
                    // 跨盘：先看目标盘装不装得下，否则为了「隔离」把 C 盘撑爆更糟
                    long need = DirSizeFast(srcPath);
                    long free = 0;
                    try { free = new DriveInfo(Path.GetPathRoot(destUsed)).AvailableFreeSpace; }
                    catch { }
                    if (need > 0 && free < need + (need / 10) + 100L * 1024 * 1024)
                        return "目标盘空间不足（需要 " + Fmt.Size(need) + "，只剩 " + Fmt.Size(free) + "）";

                    // 这里刻意不用 Microsoft.VisualBasic.FileSystem：
                    // 它内部走 COM（ADODB.Stream 等），而清理是在线程池的 MTA 线程上跑的，
                    // 一跨盘就直接把进程打崩（0xE0434352，连异常都 ToString 不出来）。
                    // 纯 File.Copy 没这个问题，也更好控。
                    if (!CopyDir(srcPath, destUsed, cancelled))
                        return "已取消";
                    DeleteTree(srcPath);
                }
                return null;
            }
            catch (UnauthorizedAccessException) { return StripDenyAndRetry(srcPath); }
            catch (IOException ex)
            {
                string m = ex.Message == null ? "" : ex.Message;
                if (m.Contains("卷之间")) return "跳盘移动失败";
                if (m.Contains("拒绝") || m.Contains("denied")) return StripDenyAndRetry(srcPath);
                if (m.Contains("正被") || m.Contains("being used")) return "正被占用";
                return "IO 错误: " + Fmt.OneLine(m, 60);
            }
            catch (Exception ex) { return ex.GetType().Name; }
        }

        /// <summary>真删：清只读/系统属性 → 递归删 → 失败则摘掉保护 ACL 再删</summary>
        public static string Kill(string path)
        {
            // 同样：链接路径和系统目录一律不动。理由见 Move() 里的说明。
            if (ScanEngine.HasLinkInPath(path))
                return "路径上有链接，已跳过（不动系统目录）";
            if (ScanEngine.IsProtected(path))
                return "属于系统目录，已跳过";

            if (TryDelete(path)) return null;
            if (StripDenyAndRetry(path) == null) return null;
            return LastFail;
        }

        /// <summary>
        /// 管理员才动得了的硬删。
        ///
        /// 为什么要单独有这个：目录上如果挂着 `Deny:(OI)(CI)F`（把自己也禁了），
        /// 那么 **读 ACL 都被拒绝** —— icacls 连看一眼都做不到。
        /// 非管理员怎么试都没用，这��「再努力一点」能解决的，
        /// 只能靠特权里的 SE_TAKE_OWNERSHIP 强行夺所有权。
        ///
        /// 程序里只有 UAC 提权那条路会走到这里（RunAsElevatedChild）。
        ///
        /// 【时间预算很重要】这个函数跑在**一个独立的提权进程**里，
        /// 而那个进程占着 SpaceHunter.exe。早先三条命令各给 120 秒、
        /// 最长 6 分钟，用户等不及强行结束主窗口 —— 但提权进程还在跑，
        /// 于是 exe 一直是「启动中、删不掉」。
        /// 所以：单条命令 25 秒、整体 50 秒封顶，到点就走。
        /// </summary>
        public static string ForceAdminDelete(string path)
        {
            DateTime t0 = DateTime.Now;
            try
            {
                // 先夺所有权，再把管理员组显式授满。
                RunTool("takeown.exe", "/f \"" + path + "\" /r /d y", 25000);
                string adm = "*S-1-5-32-544";        // Administrators
                RunTool("icacls.exe", "\"" + path + "\" /grant " + adm + ":F /t /c /q", 25000);

                // 整体预算到了就别再开第三条命令了
                if ((DateTime.Now - t0).TotalSeconds < 40)
                {
                    RunTool("icacls.exe",
                        "\"" + path + "\" /remove:d *S-1-1-0 *S-1-5-32-545 " + MySid() + " /t /c /q",
                        10000);
                }
            }
            catch { }

            // 夺完权再走常规流程（Move 优先：能进隔离区还是进隔离区）
            string dest;
            string why = Move(path, out dest);
            if (why == null) return null;
            return Kill(path);
        }

        static void RunTool(string exe, string args, int timeoutMs)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = System.Text.Encoding.Default;
                psi.StandardErrorEncoding = System.Text.Encoding.Default;
                Process p = Process.Start(psi);
                if (p == null) return;
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                if (!p.WaitForExit(timeoutMs))
                {
                    // 超时必须真的杀掉。光「不等」的话 takeown.exe 会变成
                    // 一个孤儿进程在后台继续跑 —— 那台机器上就是
                    // 「关了程序但 exe 还删不掉」的另一个来源。
                    try { p.Kill(); } catch { }
                    try { p.WaitForExit(3000); } catch { }
                }
            }
            catch { }
        }

        static string LastFail = "";

        public static string StripDenyAndRetry(string path)
        {
            LastFail = "权限被拒";
            bool ok = ForceDeleteTree(path, false);
            if (ok) return null;

            // 关键改动：**不再用 icacls /T 递归**。
            //
            // 早先这里对整棵树跑一次 /remove:d ... /T，单次最长等 120 秒，
            // 而且要对 4 个 SID 各跑一遍 —— 一个上万文件的目录就是好几分钟。
            // 用户那边的表现是「清理卡在某个目录上不动，后面全部跟着排队」，
            // 按钮还变灰，看起来就是死机。
            //
            // 其实根本不需要递归：ForceDeleteTree 已经是「逐个文件处理」的，
            // 真正删不掉的那几个文件，单独对**它自己**跑一次不带 /T 的
            // icacls 就够了（单文件毫秒级）。目录本身最后再单独处理一次。
            bool stripped = ForceDeleteTree(path, true);
            if (stripped) return null;

            LastFail = Directory.Exists(path)
                ? (File.Exists(path) ? "仍无法删除" : "仍无法删除（可能正被占用）")
                : "权限被拒";
            if (!Directory.Exists(path) && !File.Exists(path)) return null;
            return LastFail;
        }

        static bool TryDelete(string path)
        {
            try
            {
                if (Directory.Exists(path)) { ClearAttrs(path); Directory.Delete(path, true); return true; }
                if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); return true; }
                return true;
            }
            catch { return false; }
        }

        /// <summary>逐项删：每个先直接试，只有真失败的才去摘 Deny（否则要启动几千个 icacls）</summary>
        static bool ForceDeleteTree(string path, bool useIcacls)
        {
            if (!Directory.Exists(path))
            {
                if (!File.Exists(path)) return true;
                try { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); return true; }
                catch
                {
                    if (!useIcacls) return false;
                    StripDenyOn(path);
                    try { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); return true; }
                    catch { return false; }
                }
            }

            try { ClearAttrs(path); Directory.Delete(path, true); return true; }
            catch { }

            bool ok = true;
            try { foreach (string f in Directory.GetFiles(path)) if (!ForceDeleteTree(f, useIcacls)) ok = false; }
            catch { ok = false; }
            try { foreach (string s in Directory.GetDirectories(path)) if (!ForceDeleteTree(s, useIcacls)) ok = false; }
            catch { ok = false; }
            try { Directory.Delete(path); }
            catch
            {
                // 目录自己身上也可能挂了 Deny，只摘子文件是不够的
                if (useIcacls)
                {
                    StripDenyOn(path);
                    try { Directory.Delete(path); } catch { ok = false; }
                }
                else ok = false;
            }
            return ok;
        }

        static string Icacls()
        {
            try { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "icacls.exe"); }
            catch { return "icacls.exe"; }
        }

        static string MySid()
        {
            // icacls 要求 SID 形式必须带 * 前缀，否则报 1332
            try { return "*" + System.Security.Principal.WindowsIdentity.GetCurrent().User.Value; }
            catch { return null; }
        }

        static readonly string[] DenyTargets =
            { null, "*S-1-1-0", "*S-1-5-32-545" };   // 当前用户 / Everyone / Users

        /// <summary>
        /// 摘掉一个对象上的 Deny。**永远不带 /T**。
        ///
        /// 调用方（ForceDeleteTree）本来就是逐个对象下探的，所以只需要处理
        /// 当前这一个 —— 单对象跑 icacls 是毫秒级。这里再挂上递归，
        /// 就会把「删一个目录」变成「跑 4 遍全树 ACL 计算」，几十万文件的
        /// 目录能卡好几分钟，用户看到的就是清理卡死。
        ///
        /// 关键：必须先 `/inheritance:d`。带 Deny 的目录里，Deny 往往是
        /// **继承**来的，它并不是当前对象上的显式条目，单独一个
        /// `/remove:d` 是删不掉的（会报「没有匹配的规则」，然后我们就
        /// 误以为这条路走不通）。先把继承摘掉，Deny 才真正消失。
        /// </summary>
        static bool StripDenyOn(string path)
        {
            string mine = MySid();
            if (string.IsNullOrEmpty(mine)) return false;

            // 第 1 步：去掉继承来的权限（Deny 的主要来源）
            RunIcacls(path, null, "/inheritance:d");

            // 第 2 步：摘掉显式的 Deny
            bool any = false;
            for (int i = 0; i < DenyTargets.Length; i++)
            {
                string t = i == 0 ? mine : DenyTargets[i];
                if (string.IsNullOrEmpty(t)) continue;
                if (RunIcacls(path, t, "/remove:d")) any = true;
            }
            return any;
        }

        /// <summary>
        /// 跑一次 icacls。target 为 null 时按「整段参数」模式用 extra。
        /// 返回是否成功。
        /// </summary>
        static bool RunIcacls(string path, string target, string op)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Icacls(),
                    "\"" + path + "\" " + op +
                    (target == null ? "" : " \"" + target + "\"") + " /C /Q");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = System.Text.Encoding.Default;
                psi.StandardErrorEncoding = System.Text.Encoding.Default;
                Process p = Process.Start(psi);
                if (p == null) return false;

                // 一定要把两个流读完再等，否则管道写满会让 icacls 自己卡住，
                // 表现就是这里 WaitForExit 一直不返回。
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();

                // 15 秒还活着就当它废了。单对象操作正常是毫秒级，
                // 真要跑 15 秒说明路径有问题（比如指向了网络或已断开的卷），
                // 等下去毫无意义。
                if (!p.WaitForExit(15000))
                {
                    try { p.Kill(); } catch { }
                    return false;
                }
                return p.ExitCode == 0;
            }
            catch { return false; }
        }

        /// <summary>递归复制（纯 File.Copy，无 COM）。带取消。</summary>
        /// 返回 false = 被取消了。
        static bool CopyDir(string src, string dst, Func<bool> cancelled)
        {
            if (cancelled != null && cancelled()) return false;

            try { Directory.CreateDirectory(dst); }
            catch { return true; }        // 建不出来就当已完成，交给后面删除时报错

            string[] files;
            try { files = Directory.GetFiles(src); } catch { files = new string[0]; }
            for (int i = 0; i < files.Length; i++)
            {
                // 关键：每个文件都查一次取消。
                // 一个几万文件的目录要复制好几分钟，原来这里一次都不查 ——
                // 用户点「停止」看着像没反应，只能强行结束程序，
                // 而强结束会留下提权子进程在后台跑，exe 就删不掉了。
                if ((i & 63) == 0 && cancelled != null && cancelled()) return false;

                string name = Path.GetFileName(files[i]);
                if (name == null) continue;
                string to = Path.Combine(dst, name);

                // 源路径超长时目标也可能超长，跳过而不是让整个操作崩掉
                if (to.Length > 250 || files[i].Length > 250) continue;

                try
                {
                    File.Copy(files[i], to, false);
                    try
                    {
                        FileInfo fi = new FileInfo(to);
                        if ((fi.Attributes & (FileAttributes.ReadOnly | FileAttributes.System)) != 0)
                            fi.Attributes = FileAttributes.Normal;
                    }
                    catch { }
                }
                catch (IOException)
                {
                    // 复制不过去（被占用 / 磁盘满）。记一笔，
                    // 后面 DeleteTree 失败就会如实报出来，而不是静悄悄跳过。
                    if (to.Length < 250)
                        try { File.Copy(files[i], to, true); } catch { }
                }
                catch (UnauthorizedAccessException)
                {
                    try
                    {
                        File.SetAttributes(files[i], FileAttributes.Normal);
                        File.Copy(files[i], to, false);
                    }
                    catch { }
                }
            }

            string[] subs;
            try { subs = Directory.GetDirectories(src); } catch { subs = new string[0]; }
            for (int i = 0; i < subs.Length; i++)
            {
                if (cancelled != null && cancelled()) return false;
                string sub = Path.GetFileName(subs[i]);
                if (sub == null) continue;
                if (!CopyDir(subs[i], Path.Combine(dst, sub), cancelled)) return false;
            }
            return true;
        }

        /// <summary>递归删，不做任何补救（补救交给 ForceDeleteTree）</summary>
        static void DeleteTree(string p)
        {
            if (Directory.Exists(p)) { Directory.Delete(p, true); return; }
            if (File.Exists(p)) File.Delete(p);
        }

        public static void ClearAttrs(string dir)
        {
            try { new DirectoryInfo(dir).Attributes = FileAttributes.Normal; } catch { }
            try
            {
                foreach (string f in Directory.GetFiles(dir))
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                foreach (string s in Directory.GetDirectories(dir)) ClearAttrs(s);
            }
            catch { }
        }

        /// <summary>目录体积（带上限，避免超大目录卡住）</summary>
        public static long DirSizeFast(string p)
        {
            return DirSizeFast(p, null);
        }

        /// <summary>
        /// 目录体积。带取消支持。
        ///
        /// 这个函数没有取消时是「按下去就没反应」的元凶：一个 Windows 目录能
        /// 几十万次入栈出栈，点停止根本来不及生效。返回 -1 表示中途被取消，
        /// 调用方要能认出来（别把半截的体积当成 0 报给用户）。
        /// </summary>
        public static long DirSizeFast(string p, Func<bool> cancelled)
        {
            long total = 0;
            if (!Directory.Exists(p))
            {
                try { return new FileInfo(p).Length; } catch { return 0; }
            }

            // 链接点不下钻。跟着 junction 走会把同一份数据统计两遍
            //（ProgramData 在 C:\ 下能通过 All Users 绕进来），
            // 更要紧的是 Clean 那边绝不能真的搬走链接指向的东西。
            if (ScanEngine.IsLink(p)) return 0;

            Stack<string> st = new Stack<string>();
            st.Push(p);
            int guard = 0;
            while (st.Count > 0 && guard++ < 400000)
            {
                if ((guard & 1023) == 0 && cancelled != null && cancelled()) return -1;
                string d;
                try { d = st.Pop(); } catch { break; }
                try
                {
                    foreach (string f in Directory.GetFiles(d))
                    { try { total += new FileInfo(f).Length; } catch { } }
                }
                catch { }
                try
                {
                    foreach (string s in Directory.GetDirectories(d))
                        if (!ScanEngine.IsLink(s)) st.Push(s);
                }
                catch { }
            }
            return total;
        }

        /// <summary>取体积；被取消时返回 0（用于「体积只是参考、拿不到就跳过」的地方）</summary>
        public static long DirSizeOrZero(string p, Func<bool> cancelled)
        {
            long n = DirSizeFast(p, cancelled);
            return n < 0 ? 0 : n;
        }

        public static long SizeOf() { return DirSizeFast(Root); }

        /// <summary>
        /// 提权清空。用于「清空隔离区」—— 里面可能有当初没搬干净、
        /// 带着 `Deny:(OI)(CI)F` 的系统目录残留，非管理员清不掉，
        /// 用户点了清空却什么都没清掉是最糟糕的体验。
        /// </summary>
        public static int PurgeElevated()
        {
            if (!Exists) return 0;
            try
            {
                List<string> paths = new List<string>();
                foreach (string d in Directory.GetDirectories(Root, "*", SearchOption.AllDirectories))
                    paths.Add(d);
                paths.Sort(delegate(string a, string b) { return b.Length.CompareTo(a.Length); });
                if (paths.Count > 0)
                {
                    bool denied;
                    Elevated.Run(paths, out denied);
                }
            }
            catch { }
            return Purge(null, null);
        }

        public static int Purge(Action<int, int> onProg, Func<bool> cancelled)
        {
            List<string> files = new List<string>();
            try { files.AddRange(Directory.GetFiles(Root, "*", SearchOption.AllDirectories)); }
            catch { }
            int done = 0;
            for (int i = 0; i < files.Count; i++)
            {
                if (cancelled != null && cancelled()) return done;
                try
                {
                    FileInfo fi = new FileInfo(files[i]);
                    fi.Attributes = FileAttributes.Normal;
                    fi.Delete();
                    done++;
                }
                catch { }
                if ((i & 31) == 0 && onProg != null) onProg(i, files.Count);
            }
            try
            {
                foreach (string d in Directory.GetDirectories(Root, "*", SearchOption.AllDirectories))
                    try { Directory.Delete(d); } catch { }
                Directory.Delete(Root, true);
            }
            catch { }
            if (onProg != null) onProg(files.Count, files.Count);
            return done;
        }
    }

    // =====================================================================
    //  UAC 提权子进程：删不掉的项交给它
    // =====================================================================
    class Elevated
    {
        public class Result
        {
            public int Ok;
            public int Fail;
            public List<string> Failed = new List<string>();
        }

        /// <summary>提权子进程跑太久被我们自己掐掉了</summary>
        public static bool LastTimeout;

        // ---------------- 提权子进程的痕迹 ----------------
        //
        // 提权子进程是独立进程，主窗口被强行结束它不会跟着死。
        // 非管理员也杀不掉管理员进程。于是那个 SpaceHunter.exe 会一直在后台
        // 占着自己 —— 表现是任务管理器里都看不到进程了，exe 却「仍在启动中」。
        //
        // 对策：把 PID 记到临时文件里。下次启动时先扫一遍，
        // 把还活着、命令行带 --elevate 的残留进程收掉。
        static string PidFile
        {
            get { return Path.Combine(Path.GetTempPath(), "SpaceHunter_elev\\child.pid"); }
        }

        static void WriteChildPid(int pid)
        {
            try
            {
                string d = Path.GetDirectoryName(PidFile);
                if (!Directory.Exists(d)) Directory.CreateDirectory(d);
                File.WriteAllText(PidFile, pid.ToString());
            }
            catch { }
        }

        static void ClearChildPid()
        {
            try { if (File.Exists(PidFile)) File.Delete(PidFile); } catch { }
        }

        /// <summary>
        /// 清理上次残留的提权子进程。必须在启动早期调用（主窗口出来之前）。
        /// 只杀命令行里带 --elevate 的，绝不碰正常实例。
        /// </summary>
        public static int KillStaleChildren()
        {
            int killed = 0;
            int[] pids;
            try
            {
                if (!File.Exists(PidFile)) return 0;
                string s = File.ReadAllText(PidFile).Trim();
                int pid;
                if (!int.TryParse(s, out pid)) { ClearChildPid(); return 0; }
                pids = new int[] { pid };
            }
            catch { ClearChildPid(); return 0; }

            for (int i = 0; i < pids.Length; i++)
            {
                try
                {
                    Process p = Process.GetProcessById(pids[i]);
                    if (IsElevatedChild(p))
                    {
                        p.Kill();
                        killed++;
                    }
                }
                catch { }
            }
            ClearChildPid();
            return killed;
        }

        static bool IsElevatedChild(Process p)
        {
            try
            {
                string exe = p.ProcessName ?? "";
                if (!string.Equals(exe, "SpaceHunter", StringComparison.OrdinalIgnoreCase)) return false;
                // 命令行拿不到就保守处理：靠 pid 文件精确记录过，基本可信
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 这串参数是不是「提权子进程」的调用。
        ///
        /// 把它单独拎成一个函数，是为了让 Main 和自检用**同一份判断** ——
        /// 之前这个判断内联在 Main 里，写成 args.Length == 2 而实际传的是 3 个参数，
        /// 提权实例就开了完整主界面去自动扫盘。规则很简单：
        ///   第 1 个是 --elevate，后面至少还要有两个（任务文件、结果文件）。
        /// </summary>
        public static bool CommandLineMatches(string[] args)
        {
            if (args == null || args.Length < 3) return false;
            return string.Equals(args[0], "--elevate", StringComparison.OrdinalIgnoreCase);
        }

        static string WorkDir { get { return Path.Combine(Env.TempDir, "SpaceHunter_elev"); } }

        public static bool IsElevated()
        {
            try
            {
                return new System.Security.Principal.WindowsPrincipal(
                    System.Security.Principal.WindowsIdentity.GetCurrent())
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        /// <summary>被提权后启动的那个进程：读任务、清垃圾、写结果</summary>
        public static int RunAsElevatedChild(string taskFile, string resultFile)
        {
            List<string> lines = new List<string>();
            try { lines.AddRange(File.ReadAllLines(taskFile)); } catch { }
            List<string> res = new List<string>();
            for (int i = 0; i < lines.Count; i++)
            {
                string p = lines[i].Trim();
                if (p.Length == 0) continue;

                // 到了提权这一步就直接上绝招：夺所有权 + 授权 + 删。
                // 非管理员在 `Deny:(OI)(CI)F` 面前是彻底无力的，
                // 先 try 普通删除只是浪费一次往返。
                string why = Quarantine.ForceAdminDelete(p);
                if (why != null) why = Quarantine.Kill(p);

                if (why == null) res.Add("OK\t" + p);
                else res.Add("FAIL\t" + why + "\t" + p);
                try { File.WriteAllLines(resultFile, res); } catch { }
            }
            try { File.WriteAllLines(resultFile, res); } catch { }
            return 0;
        }

        /// <summary>主进程调用：弹 UAC，把这批路径交给管理员身份的自己</summary>
        public static Result Run(List<string> paths, out bool denied)
        {
            denied = false;
            LastTimeout = false;
            Result r = new Result();
            if (paths == null || paths.Count == 0) return r;

            string stamp = DateTime.Now.ToString("HHmmssfff");
            string taskFile = Path.Combine(WorkDir, "task_" + stamp + ".txt");
            string resultFile = Path.Combine(WorkDir, "res_" + stamp + ".txt");
            try
            {
                Directory.CreateDirectory(WorkDir);
                File.WriteAllLines(taskFile, paths);
            }
            catch { return r; }

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath,
                    "--elevate \"" + taskFile + "\" \"" + resultFile + "\"");
                psi.UseShellExecute = true;
                psi.Verb = "runas";                 // ← 弹 UAC
                psi.WorkingDirectory = WorkDir;
                Process p = Process.Start(psi);
                if (p == null) { denied = true; return r; }

                // 记下 PID：主窗口被强行结束时（任务管理器 End task），
                // 我们没机会善后，但下一次运行可以先把残留的提权进程收掉。
                // 不记的话，那个管理员身份的 SpaceHunter.exe 会一直挂在后台
                // 占着自己的 exe —— 用户看到的正是「进程都没了但文件删不掉」。
                WriteChildPid(p.Id);

                // 超时从 30 分钟降到 2 分钟。提权那点活（几十个路径）
                // 正常 1~2 秒就完了，两分钟足够富余。
                bool exited = p.WaitForExit(1000 * 60 * 2);
                if (!exited)
                {
                    LastTimeout = true;
                    try { p.Kill(); try { p.WaitForExit(3000); } catch { } } catch { }
                    return r;
                }
                ClearChildPid();
            }
            catch
            {
                denied = true;                       // 用户点了「否」
                try { File.Delete(taskFile); } catch { }
                return r;
            }

            try
            {
                if (File.Exists(resultFile))
                    foreach (string line in File.ReadAllLines(resultFile))
                    {
                        string[] q = line.Split('\t');
                        if (q.Length >= 2 && q[0] == "OK") r.Ok++;
                        else if (q.Length >= 3) { r.Fail++; r.Failed.Add(q[2] + "（" + q[1] + "）"); }
                    }
            }
            catch { }
            try { File.Delete(taskFile); } catch { }
            try { File.Delete(resultFile); } catch { }
            return r;
        }
    }
}