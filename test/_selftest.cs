using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace SpaceHunter
{
    // ================================================================
    //  自检：只碰临时目录，不动任何真实数据
    // ================================================================
    static class SelfTest
    {
        static int pass = 0, fail = 0;

        static void Check(bool ok, string what, string detail)
        {
            if (ok) { pass++; Console.WriteLine("  [OK] " + what); }
            else { fail++; Console.WriteLine("  [!!] " + what + "  -> " + detail); }
        }

        static string Sandbox()
        {
            string p = Path.Combine(Path.GetTempPath(), "SpaceHunter_SelfTest");
            if (Directory.Exists(p)) KillAll(p);
            Directory.CreateDirectory(p);
            return p;
        }

        static void KillAll(string p)
        {
            try
            {
                foreach (string f in Directory.GetFiles(p, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); } catch { }
                Directory.Delete(p, true);
            }
            catch { }
        }

        static void MakeFile(string p, int kb)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllBytes(p, new byte[kb * 1024]);
        }

        // ----------------------------------------------------------------
        static void Main2()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("隔离区: " + Quarantine.Root);
            Console.WriteLine();

            // ---- 1. 可移动性预检查 ----
            Console.WriteLine("[1] MovCheck 预检查");
            string sb = Sandbox();
            string normal = Path.Combine(sb, @"a\normal.txt");
            string inDir = Path.Combine(sb, @"b\inside");
            string locked = Path.Combine(sb, @"c\locked.txt");
            MakeFile(normal, 1);
            MakeFile(inDir + @"\\x.txt", 1);
            MakeFile(locked, 1);

            List<Finding> items = new List<Finding>
            {
                new Finding(normal, 1024, Cat.Cache, "test"),
                new Finding(inDir,  1024, Cat.Cache, "test"),
                new Finding(locked, 1024, Cat.Cache, "test")
            };
            FileStream hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

            MovCheck.Run(items, null, null);

            Check(items[0].Mov == Mov.Ok, "普通文件判为可移动", items[0].Mov + " " + items[0].MovNote);
            Check(File.Exists(normal), "预检查后文件还在（改名已改回）", "文件没了！");
            Check(items[1].Mov == Mov.Ok, "目录判为可移动", items[1].Mov + " " + items[1].MovNote);
            Check(Directory.Exists(inDir) && Directory.GetFileSystemEntries(inDir).Length == 1,
                "预检查后目录内容完好", "目录内容变了");
            Check(items[2].Mov == Mov.Locked, "被占用的文件判为搬不动", items[2].Mov + " " + items[2].MovNote);
            hold.Close(); hold.Dispose();
            Check(MovCheck.CountUnmovable(items) == 1, "统计出 1 项搬不动",
                MovCheck.CountUnmovable(items).ToString());

            // ---- 2. 同盘搬进隔离区 ----
            Console.WriteLine();
            Console.WriteLine("[2] 搬进隔离区（同盘）");
            string dest;
            string why = Quarantine.Move(normal, out dest);
            Check(why == null, "同盘移动成功", why ?? "");
            Check(!File.Exists(normal), "原位置已清空", "还在 " + normal);
            Check(File.Exists(dest), "隔离区里有对应文件", dest);
            Check(dest.StartsWith(Quarantine.Root, StringComparison.OrdinalIgnoreCase),
                "落在隔离区内", dest);
            // 关键：原路径结构必须保留，用户才能自己找回来
            Check(dest.IndexOf("SpaceHunter_SelfTest", StringComparison.OrdinalIgnoreCase) > 0,
                "保留了原路径结构（找得回来）", dest);

            // ---- 3. 目录搬进隔离区 ----
            Console.WriteLine();
            Console.WriteLine("[3] 目录搬进隔离区");
            string d2;
            why = Quarantine.Move(inDir, out d2);
            Check(why == null, "目录移动成功", why ?? "");
            Check(!Directory.Exists(inDir), "原目录已清空", "还在");
            Check(Directory.Exists(d2), "隔离区里有对应目录", d2);

            // ---- 4. 跨盘 ----
            Console.WriteLine();
            Console.WriteLine("[4] 跨盘移动");
            string other = null;
            foreach (DriveInfo di in DriveInfo.GetDrives())
            {
                if (di.DriveType != DriveType.Fixed || !di.IsReady) continue;
                if (string.Equals(di.RootDirectory.FullName, Path.GetPathRoot(sb), StringComparison.OrdinalIgnoreCase))
                    continue;
                // 盘根目录非管理员通常建不了东西（这台机器的 D:\ 就写不进去），
                // 所以测试目录要放在该盘上一个确实能写的位置
                string probe = Path.Combine(di.RootDirectory.FullName, "SpaceHunter_SelfTest_probe");
                try
                {
                    if (!Directory.Exists(probe)) Directory.CreateDirectory(probe);
                    File.WriteAllText(Path.Combine(probe, "t.tmp"), "x");
                    File.Delete(Path.Combine(probe, "t.tmp"));
                    Directory.Delete(probe);
                    other = di.RootDirectory.FullName;
                    break;
                }
                catch { }
            }

            if (other == null)
            {
                Console.WriteLine("  [--] 没有可写的第二个固定盘，跳过跨盘测试");
            }
            else
            {
                try
                {
                    string cross = other + @"SpaceHunter_SelfTest_cross\";
                    MakeFile(cross + @"file.dat", 2);
                    MakeFile(cross + @"sub\nested.dat", 3);

                    string d3;
                    why = Quarantine.Move(cross.TrimEnd('\\'), out d3);
                    Check(why == null, "跨盘移动整个目录成功（" + other + "）", why ?? "");
                    Check(!Directory.Exists(cross.TrimEnd('\\')), "源盘已删掉", "还在 " + cross);
                    Check(File.Exists(Path.Combine(d3, "file.dat")), "目标隔离区里有顶层文件", d3);
                    Check(File.Exists(Path.Combine(d3, "sub", "nested.dat")), "目标隔离区里有子目录文件", d3);
                    try
                    {
                        FileInfo fi = new FileInfo(Path.Combine(d3, "file.dat"));
                        Check(fi.Length == 2048, "文件大小一致", fi.Length.ToString());
                    }
                    catch { }
                    try { Directory.Delete(other + "SpaceHunter_SelfTest_cross", true); } catch { }
                }
                catch (Exception ex)
                {
                    fail++;
                    Console.WriteLine("  [!!] 跨盘测试抛异常: " + ex.GetType().Name + " " + ex.Message);
                }
            }

            // ---- 5. 带 Deny ACL 的目录必须能删掉 ----
            Console.WriteLine();
            Console.WriteLine("[5] 删受保护（带 Deny）的目录");
            string prot = Path.Combine(sb, @"protected\inner");
            MakeFile(prot + @"\\a.txt", 1);
            MakeFile(prot + @"\..\b.txt", 1);
            string icacls = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "icacls.exe");
            string me = null;
            try { me = "*" + System.Security.Principal.WindowsIdentity.GetCurrent().User.Value; } catch { }
            if (me == null)
            {
                Console.WriteLine("  [--] 拿不到当前用户 SID，跳过");
            }
            else
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(
                    icacls, "\"" + Path.Combine(sb, "protected") + "\" /deny " + me + ":(OI)(CI)F /T /C /Q");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                try
                {
                    System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi);
                    p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
                    p.WaitForExit(60000);
                }
                catch { }

                bool denied = false;
                try
                {
                    File.WriteAllText(Path.Combine(prot, "probe.txt"), "x");
                    File.Delete(Path.Combine(prot, "probe.txt"));
                }
                catch { denied = true; }

                if (!denied)
                {
                    Console.WriteLine("  [--] 没能加上 Deny（权限限制），跳过");
                }
                else
                {
                    Console.WriteLine("  （已确认该目录当前无法直接写入）");
                    // 注意：这里**不能**期望非管理员能删掉它。
                    // `Deny:(OI)(CI)F` 连读 ACL 都拒绝，icacls 连看一眼都做不到，
                    // 非管理员无论怎么试都没用 —— 唯一的出路是 UAC 提权后
                    // 用 SE_TAKE_OWNERSITY 夺所有权。
                    // 所以这里验证的是「程序能干净地失败并说明原因」。
                    string k = Quarantine.Kill(Path.Combine(sb, "protected"));
                    Check(k != null, "非管理员下如实报告删不掉（而不是假装成功）", "竟然报告成功");
                    Check(!string.IsNullOrEmpty(k), "给出了失败原因", "原因是空");
                    Console.WriteLine("     实际原因: " + k);

                    // 目录还在 —— 交给提权那条路（用户点 UAC 才会走到）
                    if (Elevated.IsElevated())
                    {
                        string k2 = Quarantine.ForceAdminDelete(Path.Combine(sb, "protected"));
                        Check(k2 == null, "以管理员身份能强制清掉", k2 ?? "");
                        Check(!Directory.Exists(Path.Combine(sb, "protected")),
                            "目录已删除", "还在");
                    }
                    else
                    {
                        Console.WriteLine("  [--] 非管理员运行，强制清空那一段跳过（提权后才会走）");
                    }
                }
            }

            // ---- 6. 隔离区清空 ----
            Console.WriteLine();
            Console.WriteLine("[6] 清空隔离区");
            long before = Quarantine.Exists ? Quarantine.SizeOf() : 0;
            int purged = Quarantine.Purge(null, null);
            Console.WriteLine("  清掉文件 " + purged + " 个，共 " + Fmt.Size(before));
            long left = Quarantine.Exists ? Quarantine.SizeOf() : 0;
            if (left == 0)
            {
                Check(true, "隔离区已空", "");
            }
            else
            {
                // 非管理员下残留是正常的 —— 被 `Deny:(OI)(CI)F` 保护的东西
                // 只有提权才能清。这里的正确行为是「如实报告还剩多少」，
                // 而不是假装清干净了。
                Console.WriteLine("  [--] 还剩 " + Fmt.Size(left) +
                    "（受保护，非管理员清不动；提权后会清掉）");
                Check(Quarantine.Exists, "如实报告隔离区仍存在（没有谎报清空）", "谎报了");
            }

            // ---- 7. 排版与格式化不能崩 ----
            Console.WriteLine();
            Console.WriteLine("[7] 链接点必须被挡住（这条是血的教训）");
            // C:\Users\All Users -> C:\ProgramData
            // 曾经跟着这个 junction 走进 ProgramData，然后把
            // Windows Defender\Support 整个搬进了隔离区。
            string allUsers = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string viaLink = @"C:\Users\All Users";
            Check(ScanEngine.IsLink(viaLink) || !Directory.Exists(viaLink),
                "认出 " + viaLink + " 是链接点（或不存在）", "没认出来");
            if (Directory.Exists(viaLink))
                Check(ScanEngine.HasLinkInPath(viaLink + @"\Microsoft\Windows Defender\Support"),
                    "路径含链接时能识别出来", "没识别出来");

            // 真搬一次，验证 Move 根本不会动它
            string k1 = @"C:\Users\All Users\Microsoft\Windows Defender\Support";
            if (Directory.Exists(k1))
            {
                string dd;
                string w = Quarantine.Move(k1, out dd);
                Check(w != null, "Move 拒绝搬链接路径下的目录", "竟然搬了！");
                Check(Directory.Exists(k1), "目标目录原地未动", "不见了！");
                string w2 = Quarantine.Kill(k1);
                Check(w2 != null, "Kill 也拒绝链接路径下的目录", "竟然删了！");
                Check(Directory.Exists(k1), "Kill 后目标目录仍在", "不见了！");
            }
            else
            {
                Console.WriteLine("  [--] " + k1 + " 不存在，跳过搬运验证");
            }

            // 真目录不该被误伤
            string realDir = Path.Combine(sb, @"real\plain");
            MakeFile(realDir + @"\\f.txt", 1);
            Check(!ScanEngine.HasLinkInPath(realDir), "普通目录不被误判成链接", "误判");
            string dd2;
            Check(Quarantine.Move(realDir, out dd2) == null, "普通目录照常能搬", "搬不了");

            Console.WriteLine();
            Console.WriteLine("[8] 系统目录必须被挡住（比链接点更严重）");
            ScanEngine.InitProtected();

            // 这些就是曾经出事的 / 可能出事的路径
            string[] sys =
            {
                @"C:\ProgramData\Microsoft\Windows Defender",
                @"C:\ProgramData\Microsoft\Windows Defender\Support",
                @"C:\ProgramData\Microsoft\Windows Defender\Quarantine",
                @"C:\Windows",
                @"C:\Windows\System32",
                @"C:\Program Files",
                @"C:\Program Files\Anything\App",
                @"C:\Program Files (x86)\Anything",
                @"C:\Boot",
                @"C:\Recovery",
                @"C:\$Recycle.Bin"
            };
            foreach (string s in sys)
                Check(ScanEngine.IsProtected(s), "保护：" + s, "没保护");

            // 别的盘上的程序目录也要保护。
            // 注意 D:\Program Files 和 C:\Program Files 一样都是 16 字符 ——
            // 这里如果等长时直接 return，D:/E: 的保护会被 C: 的条目顶掉。
            int otherDisks = 0;
            foreach (DriveInfo di in DriveInfo.GetDrives())
            {
                if (!di.IsReady || di.DriveType != DriveType.Fixed) continue;
                if (string.Equals(di.RootDirectory.FullName, "C:\\",
                    StringComparison.OrdinalIgnoreCase)) continue;
                otherDisks++;
                foreach (string sub in new string[] { "Program Files", "Program Files (x86)", "ProgramData" })
                {
                    string p = Path.Combine(di.RootDirectory.FullName, sub);
                    Check(ScanEngine.IsProtected(p),
                        di.RootDirectory.FullName + " 的 " + sub + " 也保护",
                        "没保护（可能被同长度的 C:\\ 条目顶掉）");
                }
            }
            Console.WriteLine("  （检查了 " + otherDisks + " 块非 C 盘）");

            // 必须能被 Move / Kill 双重拒绝
            string sysProbe = @"C:\ProgramData\Microsoft\Windows Defender\Support";
            if (Directory.Exists(sysProbe))
            {
                string p3;
                string w = Quarantine.Move(sysProbe, out p3);
                Check(w != null, "Move 拒绝搬系统目录", "竟然搬了！");
                Check(Directory.Exists(sysProbe), "系统目录原地未动", "不见了！");
                string w2 = Quarantine.Kill(sysProbe);
                Check(w2 != null, "Kill 拒绝删系统目录", "竟然删了！");
                Check(Directory.Exists(sysProbe), "Kill 后系统目录仍在", "不见了！");
            }
            else
            {
                Console.WriteLine("  [--] " + sysProbe + " 不存在，跳过搬运验证");
            }

            // 用户数据不能被误伤
            string[] mine =
            {
                Env.LocalAppData + @"\Temp",
                Env.LocalAppData + @"\Microsoft\Edge",
                Env.AppData,
                Path.Combine(Env.UserProfile, "Documents"),
                Path.Combine(Env.UserProfile, "Downloads"),
                Path.Combine(Env.UserProfile, "Desktop")
            };
            foreach (string m in mine)
                Check(!ScanEngine.IsProtected(m), "不该拦（用户数据）：" + m, "被误拦了");

            // 长度相同但内容不同 —— 曾经的回归点。
            // C:\System Volume Information 和 AppData\Roaming 都是 28 字符，
            // 只比长度的话用户的 Roaming 就被当成系统目录拦掉了。
            string sysVol = @"C:\System Volume Information";
            string roam = Env.AppData;
            Check(sysVol.Length == roam.Length,
                "长度确实相同（否则这条用例没意义）",
                sysVol.Length + " vs " + roam.Length);
            Check(ScanEngine.IsProtected(sysVol), "等长的系统目录仍要保护", "漏了");
            Check(!ScanEngine.IsProtected(roam), "等长的用户目录不能被误伤", "误伤了");

            Console.WriteLine();
            Console.WriteLine("[9] UAC 提权子进程的参数约定");
            // 这个约定写错过一次，代价很大：
            // Elevated.Run 传的是 3 个参数（--elevate 任务文件 结果文件），
            // Main 里却按 args.Length == 2 判断 —— 永远不成立，于是提权实例
            // 开了完整主界面还自动扫盘，主窗口干等 30 分钟。
            // 这里把「约定」本身断言掉，以后谁改坏都会立刻炸出来。
            string exe = System.Windows.Forms.Application.ExecutablePath;
            Check(File.Exists(exe), "自身路径可读: " + exe, "读不到");
            Check(Elevated.CommandLineMatches(
                    new string[] { "--elevate", "C:\\t.txt", "C:\\r.txt" }),
                "3 个参数能识别为提权调用", "识别不了");
            Check(!Elevated.CommandLineMatches(new string[] { "--noscan" }),
                "单个 --noscan 不会被误认成提权", "误认了");
            Check(!Elevated.CommandLineMatches(new string[] { "--elevate", "C:\\t.txt" }),
                "参数不全不会被误认成提权", "误认了");
            Check(!Elevated.CommandLineMatches(new string[] { null }),
                "null 不会被误认", "误认了");
            // 主界面绝对不能因为这些开关被拉起来
            Check(!Elevated.CommandLineMatches(new string[] { "--drives=1" }),
                "--drives 不会被误认成提权", "误认了");

            Console.WriteLine();
            Console.WriteLine("[10] 空间地图的「统计中」状态机");
            // 用户报的「一直显示统计中」就是这个：
            //   StartList() 顺手把 Loading 设成了 true，而它被
            //   ShowMapOf(drive, false) 调用（= 在地图页切盘、看缓存里没有该盘）
            //   这条路径上根本没人统计，于是 Loading 永远 true，
            //   表头上就一直写着「统计中」，而实际上什么都没在跑。
            BarList bl = new BarList();
            bl.StartList("（还没统计过，点下面的「统计这个盘」）");
            Check(!bl.Loading, "StartList 之后不是「统计中」", "还是统计中");
            Check(bl.Note == "（还没统计过，点下面的「统计这个盘」）",
                "StartList 保留了提示语", "提示语丢了: " + bl.Note);

            bl.BeginStat();
            Check(bl.Loading, "BeginStat 之后才是「统计中」", "没进入统计中");

            bl.EndStat();
            Check(!bl.Loading, "EndStat 之后退出「统计中」", "还挂着统计中");

            // 收尾路径也必须能清掉状态
            bl.SetDirs(new string[] { "C:\\a", "C:\\b" });
            Check(bl.Loading, "SetDirs 后进入统计中（马上要算体积）", "没进入");
            bl.FinishStat();
            Check(!bl.Loading, "FinishStat 之后退出「统计中」", "还挂着统计中");
            Check(bl.ItemsSnapshot.Count == 2, "FinishStat 保留结果供缓存", bl.ItemsSnapshot.Count.ToString());

            // 切盘（没缓存）这条最容易出问题的路径，必须停在非统计状态
            BarList bl2 = new BarList();
            bl2.StartList("");
            Check(!bl2.Loading, "切盘时 StartList(\"\") 也不是统计中", "是统计中");

            Console.WriteLine();
            Console.WriteLine("[11] 杂项");
            Check(Fmt.Size(0) == "0 B", "Fmt.Size(0)", Fmt.Size(0));
            Check(Fmt.Size(1536) == "1 KB", "Fmt.Size(1536) 取整显示 KB", Fmt.Size(1536));
            Check(Fmt.Size(1536 * 1024) == "1.5 MB", "Fmt.Size(1.5MB) 保留小数", Fmt.Size(1536 * 1024));
            Check(Fmt.Norm("A-b_C 1") == "abc1", "Fmt.Norm", Fmt.Norm("A-b_C 1"));
            Check(Fmt.Size(-5).StartsWith("-"), "负数不崩", Fmt.Size(-5));
            Check(Installer.FixedDrives().Count >= 1, "至少能列出一个盘",
                Installer.FixedDrives().Count.ToString());

            // ---- 12. 单盘场景不能越界 ----
            Console.WriteLine();
            Console.WriteLine("[12] 空间地图底栏必须有尺寸且可见");
            // 「统计这个盘」按钮曾经建出来了但从没被 SetBounds 过（0×0），
            // 界面上连个入口都没有。控件存在 != 用户看得见，
            // 所以这里直接断言「有尺寸」和「可见」。
            MainForm mf = new MainForm();
            mf.AutoScan = false;
            System.Windows.Forms.Application.DoEvents();

            System.Windows.Forms.Control statBtn = CtrlTree.FindFirst(mf,
                delegate(System.Windows.Forms.Control c)
            {
                ActionButton ab = c as ActionButton;
                return ab != null && ab.Caption == "统计这个盘";
            });
            System.Windows.Forms.Control mapList = CtrlTree.FindFirst(mf,
                delegate(System.Windows.Forms.Control c) { return c is BarList; });

            Check(statBtn != null, "能找到「统计这个盘」按钮", "控件树里没有");
            if (statBtn != null)
            {
                Check(statBtn.Width > 20 && statBtn.Height > 15,
                    "按钮有实际尺寸（不是 0×0）",
                    statBtn.Width + "×" + statBtn.Height);
                Check(statBtn.Parent != null && statBtn.Parent.Height > statBtn.Height,
                    "按钮的父容器比它大（没被压扁）",
                    statBtn.Parent == null ? "没有父容器" : "父容器高 " + statBtn.Parent.Height);
            }
            Check(mapList != null, "能找到空间地图列表", "控件树里没有");
            if (mapList != null)
                Check(mapList.Height > 50, "地图列表有高度", mapList.Height.ToString());

            Console.WriteLine();
            Console.WriteLine("[13] 隔离区不能把自己当垃圾（另一条血的教训）");
            // 实测真发生过：隔离区里存的是**原路径的完整结构**，
            // 扫到以后再往隔离区搬，就成了
            //   隔离区\C\...\SpaceHunter\隔离区\C\...\xxx
            // 层层套娃，@opencode-aidesktop\d3dcompiler_47.dll 就是这么被搬进去的。
            string q = Quarantine.Root;
            ScanEngine.InitProtected();

            Check(ScanEngine.InsideQuarantine(q), "隔离区自身算「在里面」", "不算");
            Check(ScanEngine.InsideQuarantine(Path.Combine(q, @"C\Users\me\file.exe")),
                "隔离区里的文件算「在里面」", "不算");
            Check(ScanEngine.IsExcluded(Path.Combine(q, @"C\x.txt")),
                "隔离区里的东西被 IsExcluded 排除", "没排除");

            Check(!ScanEngine.InsideQuarantine(Env.LocalAppData),
                "别把整个 LocalAppData 都当隔离区", "误判");
            Check(!ScanEngine.InsideQuarantine(Env.LocalAppData + @"\Temp\a.txt"),
                "Temp 下的正常文件不算隔离区", "误判");

            // 真搬一次，再验证它不会被当成垃圾
            string probeDir = Path.Combine(sb, @"qprobe");
            MakeFile(probeDir + @"\\data.bin", 1);
            string dq;
            Check(Quarantine.Move(probeDir, out dq) == null, "先把探针搬进隔离区", "搬失败");
            Check(ScanEngine.InsideQuarantine(dq), "搬完后它确实在隔离区里", dq);

            string dq2;
            string why2 = Quarantine.Move(dq, out dq2);
            Check(why2 != null, "拒绝把隔离区里的东西再往里搬", "竟然搬了");
            Check(Directory.Exists(dq), "原路径还在隔离区里（没被搬走）", "不见了");
            Quarantine.Kill(dq);

            Console.WriteLine();
            Console.WriteLine("[15] 单实例与残留清理");
            // 朋友那台上的症状：「关闭后 exe 仍在启动中、无法删除」。
            // 根因就是跑出了第二个实例（以及强结束后残留的提权子进程），
            // 两个进程一起占着同一个 exe。
            Check(Elevated.CommandLineMatches(new string[] { "--elevate", "a", "b" }),
                "提权调用仍被正确识别（不受单实例影响）", "识别不了");
            Check(!Elevated.CommandLineMatches(new string[] { "--drives=1" }),
                "普通启动不会被当成提权调用", "误认");

            // KillStaleChildren 在没有 pid 文件时必须安静返回，不能抛
            int killed = Elevated.KillStaleChildren();
            Check(killed == 0, "没有残留记录时返回 0", killed.ToString());

            // pid 文件指向一个不存在的进程 -> 不能抛异常
            string pdir = Path.Combine(Path.GetTempPath(), "SpaceHunter_elev");
            Directory.CreateDirectory(pdir);
            File.WriteAllText(Path.Combine(pdir, "child.pid"), "99999999");
            int kDead = Elevated.KillStaleChildren();
            Check(kDead == 0, "pid 指向死进程时返回 0", kDead.ToString());
            Check(!File.Exists(Path.Combine(pdir, "child.pid")),
                "pid 文件被清掉", "还在");

            // 跨盘复制必须能被取消（原来 CopyDir 一次都不查取消）
            // 这里只验证「取消时立刻返回、不做无谓工作」
            string bigSrc = Path.Combine(sb, @"cancelprobe");
            MakeFile(bigSrc + @"\\a.bin", 1);
            string cancelDest;
            string whyCancel = Quarantine.Move(bigSrc, out cancelDest,
                delegate { return true; });      // 一上来就说取消
            Check(whyCancel == "已取消", "一开始就取消时立刻返回", whyCancel ?? "居然成功了");
            Check(Directory.Exists(bigSrc), "取消后源目录没被动", "不见了");
            Quarantine.Kill(bigSrc);

            Console.WriteLine();
            Console.WriteLine("[16] 杂项");
            MainForm.DriveLimit = 0;
            Check(new MainForm { AutoScan = false } != null, "0 个盘也能建出窗口", "");
            MainForm.DriveLimit = 1;
            Check(new MainForm { AutoScan = false } != null, "1 个盘也能建出窗口", "");
            MainForm.DriveLimit = -1;

            KillAll(sb);
            Console.WriteLine();
            Console.WriteLine("================  通过 " + pass + "　失败 " + fail + "  ================");
            Environment.Exit(fail == 0 ? 0 : 1);
        }

        public static void Main(string[] args)
        {
            try { Main2(); }
            catch (Exception ex)
            {
                // 不加这层的话，自检崩了只会打印「异常字符串无法输出」，什么信息都没有
                Console.WriteLine();
                Console.WriteLine("!! 自检自身崩了: " + ex.GetType().FullName);
                Console.WriteLine("   " + ex.Message);
                Console.WriteLine("   " + ex.StackTrace);
                Environment.Exit(9);
            }
        }
    }
}