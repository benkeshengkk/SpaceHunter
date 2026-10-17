using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SpaceHunter
{
    // =====================================================================
    //  一个软件
    // =====================================================================
    class AppEntry
    {
        public string Name = "";
        public string Version = "";
        public string Publisher = "";
        public string InstallDir = "";
        public string UninstallCmd = "";
        public long Size;                 // 来自注册表 EstimatedSize（KB），没有就是 0
        public bool FromRegistry;
        public bool FromShortcut;
        public bool FoundOnDisk;
        public string ShortcutPath = "";

        public bool CanUninstall { get { return !string.IsNullOrEmpty(UninstallCmd); } }

        public string Key
        {
            get
            {
                if (!string.IsNullOrEmpty(InstallDir)) return Fmt.Norm(InstallDir);
                if (!string.IsNullOrEmpty(UninstallCmd)) return Fmt.Norm(UninstallCmd);
                return Fmt.Norm(Name);
            }
        }

        /// <summary>卸载命令里的那个 exe（用来定位残留目录）</summary>
        public string ExeOf(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return "";
            if (cmd[0] == '"')
            {
                int e = cmd.IndexOf('"', 1);
                if (e > 0) return cmd.Substring(1, e - 1);
            }
            int i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (i > 0)
            {
                for (int k = i - 1; k >= 0; k--)
                {
                    char ch = cmd[k];
                    if (ch == ' ' || ch == '"') return cmd.Substring(k + 1, i - k + 4);
                }
                return cmd.Substring(0, i + 4);
            }
            return "";
        }

        public string MainExe { get { return ExeOf(UninstallCmd); } }
    }

    // =====================================================================
    //  软件扫描
    //
    //  三个来源，缺一不可：
    //    1) 注册表 Uninstall 键 —— 只有这里才有真正的卸载命令
    //    2) 磁盘程序目录 —— 能发现绿色软件 / 注册表坏掉的
    //    3) 快捷方式 —— 能发现装在奇怪位置的
    //
    //  顺序有讲究：先注册表（拿到卸载命令），再用磁盘和快捷方式去「补」，
    //  补不上的才是新增项。这样列表不会同一个软件出现三次。
    // =====================================================================
    static class Installer
    {
        static readonly string[] DiskSkip =
        {
            "common files", "windowsapps", "windows defender", "microsoft.net",
            "microsoft sdks", "package cache", "mobilesync", "windowsportabledevices",
            "temp", "microsoft office", "microsoft shared", "microsoft help",
            "windows nt", "amd", "x86", "x64", "spp", "htc", "gadgets"
        };

        public static List<string> FixedDrives()
        {
            List<string> l = new List<string>();
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                    if (d.DriveType == DriveType.Fixed && d.IsReady) l.Add(d.RootDirectory.FullName);
            }
            catch { }
            if (l.Count == 0) l.Add("C:\\");
            return l;
        }

        // ---------------------------------------------------------------
        public static List<AppEntry> Scan(Func<bool> cancelled, Action<int, int, string> prog)
        {
            Dictionary<string, AppEntry> map = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
            List<string> drives = FixedDrives();

            // 1) 注册表
            prog(0, 3, "正在读注册表…");
            FromRegistry(map, cancelled);

            // 2) 磁盘
            prog(1, 3, "正在扫程序目录…");
            FromDisk(map, drives, cancelled);

            // 3) 快捷方式
            prog(2, 3, "正在看快捷方式…");
            FromShortcuts(map, cancelled);

prog(3, 3, "整理结果…");
            List<AppEntry> list = new List<AppEntry>(map.Values);
            list.Sort(delegate(AppEntry a, AppEntry b)
            {
                int c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
                c = string.Compare(a.Publisher, b.Publisher, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
                return string.Compare(a.Key, b.Key, StringComparison.Ordinal);
            });
            return list;
        }

        // ---------------- 1) 注册表 ----------------
static void FromRegistry(Dictionary<string, AppEntry> map, Func<bool> cancelled)
        {
            // WOW6432Node 一定要查：64 位系统上 32 位程序全在那个节点下面，
            // 漏了它等于漏掉一大半软件。
            List<KeyValuePair<RegistryKey, string>> src =
                new List<KeyValuePair<RegistryKey, string>>();
            src.Add(new KeyValuePair<RegistryKey, string>(Registry.LocalMachine,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"));
            src.Add(new KeyValuePair<RegistryKey, string>(Registry.LocalMachine,
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"));
            src.Add(new KeyValuePair<RegistryKey, string>(Registry.CurrentUser,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"));

            for (int i = 0; i < src.Count; i++)
            {
                if (cancelled != null && cancelled()) return;
                RegistryKey root = null;
                try { root = src[i].Key.OpenSubKey(src[i].Value); } catch { }
                if (root == null) continue;

                string[] names;
                try { names = root.GetSubKeyNames(); } catch { continue; }
                for (int j = 0; j < names.Length; j++)
                {
                    if (cancelled != null && cancelled()) return;
                    RegistryKey k = null;
                    try
                    {
                        k = root.OpenSubKey(names[j]);
                        if (k == null) continue;
                        AppEntry a = ReadKey(k);
                        if (a != null) Merge(map, a);
                    }
                    catch { }
                    finally { if (k != null) try { k.Close(); } catch { } }
                }
                try { root.Close(); } catch { }
            }
        }

        static AppEntry ReadKey(RegistryKey k)
        {
            AppEntry a = new AppEntry();
            a.Name = Str(k, "DisplayName");
            if (string.IsNullOrEmpty(a.Name)) return null;
            a.Version = Str(k, "DisplayVersion");
            a.Publisher = Str(k, "Publisher");
            a.InstallDir = Str(k, "InstallLocation");
            a.UninstallCmd = Str(k, "QuietUninstallString");
            if (string.IsNullOrEmpty(a.UninstallCmd)) a.UninstallCmd = Str(k, "UninstallString");
            a.FromRegistry = true;

            long kb = 0;
            try { kb = Convert.ToInt64(k.GetValue("EstimatedSize") ?? 0); } catch { }
            a.Size = kb * 1024L;

            if (a.Size == 0)
            {
                string exe = a.MainExe;
                if (exe != null && exe.Length > 0)
                    try { a.Size = new FileInfo(exe).Length; } catch { }
            }

            // 去掉 (x86) / 中文括号里的架构后缀，列表好看点
            a.Name = a.Name.Replace(" (x86)", "").Replace("（x86）", "").Trim();
            return a;
        }

static string Str(RegistryKey k, string v)
        {
            string s;
            try { s = k.GetValue(v) as string; } catch { return ""; }
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Trim();
            // 有些软件写成 "C:\Program Files\Foo\" 这样带引号，直接拿来拼路径会错
            if (s.Length > 1 && s[0] == '"' && s[s.Length - 1] == '"')
                s = s.Substring(1, s.Length - 2);
            if (s.Length > 1 && s[s.Length - 1] == '\\' && s.IndexOf(':') > 1)
                s = s.Substring(0, s.Length - 1);
            return s;
        }

        // ---------------- 2) 磁盘 ----------------
        static void FromDisk(Dictionary<string, AppEntry> map, List<string> drives, Func<bool> cancelled)
        {
            List<string> roots = new List<string>();
            for (int i = 0; i < drives.Count; i++)
            {
                string r = drives[i];
                roots.Add(Path.Combine(r, "Program Files"));
                roots.Add(Path.Combine(r, "Program Files (x86)"));
                roots.Add(Path.Combine(r, "ProgramData"));
                roots.Add(Path.Combine(Env.LocalAppData, "Programs"));
            }

            for (int i = 0; i < roots.Count; i++)
            {
                if (cancelled != null && cancelled()) return;
                string dir;
                try { if (!Directory.Exists(roots[i])) continue; dir = roots[i]; }
                catch { continue; }

                string[] subs;
                try { subs = Directory.GetDirectories(dir); } catch { continue; }
                for (int j = 0; j < subs.Length; j++)
                {
                    if (cancelled != null && cancelled()) return;
                    string sub = subs[j];
                    string nm = Path.GetFileName(sub);
                    string low = nm.ToLowerInvariant();
                    bool skip = false;
                    for (int k = 0; k < DiskSkip.Length; k++)
                        if (low.StartsWith(DiskSkip[k], StringComparison.Ordinal)) { skip = true; break; }
                    if (skip) continue;

                    // 目录里（有浅层）exe 才算软件，避免把一堆数据目录也算进来
                    if (!HasExe(sub)) continue;

                    string key = Fmt.Norm(sub);
                    AppEntry ex;
                    if (map.TryGetValue(key, out ex) && ex.FromRegistry)
                    {
                        ex.FoundOnDisk = true;
                        if (string.IsNullOrEmpty(ex.InstallDir)) ex.InstallDir = sub;
                        continue;
                    }
                    // 换个键再找一次：注册表里 InstallLocation 可能带了尾斜杠或大小写差异
                    if (ByDir(map, sub, out ex))
                    {
                        ex.FoundOnDisk = true;
                        continue;
                    }

                    AppEntry a = new AppEntry
                    {
                        Name = nm,
                        InstallDir = sub,
                        FoundOnDisk = true
                    };
                    a.Size = 0;
                    map[key] = a;
                }
            }
        }

        static bool ByDir(Dictionary<string, AppEntry> map, string dir, out AppEntry hit)
        {
            string n2 = Fmt.Norm(dir).TrimEnd('/');
            foreach (KeyValuePair<string, AppEntry> kv in map)
            {
                string d = Fmt.Norm(kv.Value.InstallDir ?? "");
                if (d.Length == 0) continue;
                if (d.TrimEnd('/') == n2) { hit = kv.Value; return true; }
            }
            hit = null;
            return false;
        }

        static bool HasExe(string dir)
        {
            try
            {
                string[] exes = Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly);
                if (exes.Length > 0) return true;
                string[] subs = Directory.GetDirectories(dir);
                for (int i = 0; i < subs.Length && i < 6; i++)
                {
                    try
                    {
                        if (Directory.GetFiles(subs[i], "*.exe", SearchOption.TopDirectoryOnly).Length > 0)
                            return true;
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        // ---------------- 3) 快捷方式 ----------------
        static void FromShortcuts(Dictionary<string, AppEntry> map, Func<bool> cancelled)
        {
            List<string> dirs = new List<string>();
            dirs.Add(Env.Desktop);
            dirs.Add(Env.CommonDesktop);
            dirs.Add(Env.StartMenu);
            dirs.Add(Env.CommonStartMenu);
            dirs.Add(Path.Combine(Env.UserProfile, @"AppData\Roaming\Microsoft\Windows\Start Menu"));

            int budget = 600;
            for (int i = 0; i < dirs.Count && budget > 0; i++)
            {
                if (cancelled != null && cancelled()) return;
                List<string> lnks = new List<string>();
                try
                {
                    if (!Directory.Exists(dirs[i])) continue;
                    lnks.AddRange(Directory.GetFiles(dirs[i], "*.lnk", SearchOption.AllDirectories));
                }
                catch { continue; }

                for (int j = 0; j < lnks.Count && budget > 0; j++)
                {
                    if (cancelled != null && cancelled()) return;
                    budget--;
                    string target = ShortcutTarget(lnks[j]);
                    if (string.IsNullOrEmpty(target)) continue;
                    if (target.IndexOf(":\\Windows\\", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (target.IndexOf(":\\Program Files\\WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    string dir = null;
                    try { dir = Path.GetDirectoryName(target); } catch { }
                    if (string.IsNullOrEmpty(dir)) continue;

                    // 已经登记过的目录不用再报
                    AppEntry hit;
                    if (ByDir(map, dir, out hit)) continue;

                    string key = Fmt.Norm(dir);
                    if (map.ContainsKey(key)) continue;

                    string nm;
                    try
                    {
                        nm = Path.GetFileNameWithoutExtension(target);
                        if (nm.Length <= 2) nm = Path.GetFileName(dir);
                    }
                    catch { nm = Path.GetFileName(dir); }
                    if (string.IsNullOrEmpty(nm)) nm = dir;

                    map[key] = new AppEntry
                    {
                        Name = nm,
                        InstallDir = dir,
                        FromShortcut = true,
                        ShortcutPath = lnks[j]
                    };
                }
            }
        }

        /// <summary>用 WScript.Shell 读 .lnk 的目标（COM，走反射免得再引 Microsoft.CSharp）</summary>
        public static string ShortcutTarget(string lnk)
        {
            object sh = null;
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return "";
                sh = Activator.CreateInstance(t);
                if (sh == null) return "";
                object sc = sh.GetType().InvokeMember("CreateShortcut",
                    BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
                if (sc == null) return "";
                string target = sc.GetType().InvokeMember("TargetPath",
                    BindingFlags.GetProperty, null, sc, null) as string;
                return target ?? "";
            }
            catch { return ""; }
            finally
            {
                if (sh != null) { try { Marshal_Release(sh); } catch { } }
            }
        }

        static void Marshal_Release(object o)
        {
            try
            {
                if (o != null && System.Runtime.InteropServices.Marshal.IsComObject(o))
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(o);
            }
            catch { }
        }

        // ---------------- 合并 ----------------
        static void Merge(Dictionary<string, AppEntry> map, AppEntry a)
        {
            string k = a.Key;
            if (k.Length == 0) return;

            AppEntry ex;
            if (map.TryGetValue(k, out ex))
            {
                if (string.IsNullOrEmpty(ex.Name)) ex.Name = a.Name;
                if (string.IsNullOrEmpty(ex.Version)) ex.Version = a.Version;
                if (string.IsNullOrEmpty(ex.Publisher)) ex.Publisher = a.Publisher;
                if (string.IsNullOrEmpty(ex.InstallDir)) ex.InstallDir = a.InstallDir;
                if (string.IsNullOrEmpty(ex.UninstallCmd)) ex.UninstallCmd = a.UninstallCmd;
                if (ex.Size == 0) ex.Size = a.Size;
                ex.FromRegistry = true;
                return;
            }
            // InstallLocation 空的软件，换卸载命令当键再试一次
            if (string.IsNullOrEmpty(a.InstallDir))
            {
                string k2 = Fmt.Norm(a.UninstallCmd);
                if (k2.Length > 0 && map.TryGetValue(k2, out ex))
                {
                    if (string.IsNullOrEmpty(ex.UninstallCmd)) ex.UninstallCmd = a.UninstallCmd;
                    if (ex.Size == 0) ex.Size = a.Size;
                    ex.FromRegistry = true;
                    return;
                }
            }
            map[k] = a;
        }

        // ---------------- 卸载 ----------------
        public class UnResult
        {
            public bool Launched;
            public bool Denied = false;
            public int ExitCode = -1;
            public string Fail = "";
        }

        /// <summary>静默卸载：优先 /S、/silent、/verysilent 这类开关，其次 GUI</summary>
        public static UnResult Uninstall(AppEntry app)
        {
            UnResult r = new UnResult();
            string cmd = app.UninstallCmd;
            if (string.IsNullOrEmpty(cmd)) { r.Fail = "没有卸载命令"; return r; }

            string args = SilentArgs(cmd);
            if (args != null) cmd = cmd + " " + args;

            // 跑之前先把该退的窗口关掉（Office / Adobe 之类会拦）
            KillOccupier(app.MainExe);

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c \"" + cmd + "\"");
                psi.UseShellExecute = true;
                psi.WorkingDirectory = Path.GetDirectoryName(app.MainExe) ?? Env.TempDir;
                Process p = Process.Start(psi);
                if (p == null) { r.Fail = "无法启动卸载程序"; return r; }
                r.Launched = true;
                if (psi.UseShellExecute) { try { p.WaitForExit(1000 * 60 * 60); } catch { } }
                try { r.ExitCode = p.ExitCode; } catch { }
            }
            catch (Exception ex)
            {
                r.Fail = ex.GetType().Name;
            }
            return r;
        }

        static string SilentArgs(string cmd)
        {
            string low = cmd.ToLowerInvariant();
            // 每个安装器认自己的开关，猜错反而会弹出界面挡路
            if (low.Contains("unins") || low.Contains("uninst"))
                return low.Contains("/silent") ? "" : "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";
            if (low.Contains("msiexec")) return "/qn";
            if (low.Contains("wixburn")) return "/quiet";
            return null;   // 交给它自己的界面
        }

        static void KillOccupier(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return;
            string nm;
            try { nm = Path.GetFileNameWithoutExtension(exe); } catch { return; }
            if (string.IsNullOrEmpty(nm)) return;
            try
            {
                foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcessesByName(nm))
                {
                    try
                    {
                        string fp = p.MainModule.FileName;
                        if (fp != null && string.Equals(Path.GetDirectoryName(fp),
                            Path.GetDirectoryName(exe), StringComparison.OrdinalIgnoreCase))
                            p.Kill();
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>找卸载后残留：安装目录 + AppData 下同名目录 + 程序本体旁边的文件</summary>
        public static List<PickList.Item> Leftovers(AppEntry app, Func<bool> cancelled)
        {
            List<PickList.Item> list = new List<PickList.Item>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string nm = app == null ? "" : app.Name;
            string baseName = nm;
            int p1 = baseName.IndexOf(' ');
            if (p1 > 2 && p1 < baseName.Length - 1) baseName = baseName.Substring(0, p1);
            baseName = Fmt.Norm(baseName);
            if (baseName.Length < 2) return list;

            AddDir(list, seen, app.InstallDir, true);

            // 主程序所在目录 —— 卸载完常常还在那儿
            string exeDir = null;
            try { exeDir = Path.GetDirectoryName(app.MainExe); } catch { }
            if (exeDir != null) AddDir(list, seen, exeDir, false);

            string[] roots =
            {
                Env.LocalAppData, Env.AppData, Env.CommonAppData,
                Path.Combine(Env.UserProfile, "Documents")
            };
            for (int i = 0; i < roots.Length; i++)
            {
                if (cancelled != null && cancelled()) return list;
                string[] subs;
                try { if (!Directory.Exists(roots[i])) continue; subs = Directory.GetDirectories(roots[i]); }
                catch { continue; }
                for (int j = 0; j < subs.Length; j++)
                {
                    if ((j & 15) == 0 && cancelled != null && cancelled()) return list;
                    string n = Fmt.Norm(Path.GetFileName(subs[j]));
                    if (n.Length < 2) continue;
                    if (n.StartsWith(baseName, StringComparison.Ordinal)) AddDir(list, seen, subs[j], true);
                }
            }

            list.Sort(delegate(PickList.Item a, PickList.Item b) { return b.Size.CompareTo(a.Size); });
            return list;
        }

        static void AddDir(List<PickList.Item> list, HashSet<string> seen, string dir, bool def)
        {
            if (string.IsNullOrEmpty(dir)) return;
            // 别把自己当成残留
            if (Fmt.Norm(dir).StartsWith(Fmt.Norm(Env.LocalAppData) + "spacehunter", StringComparison.Ordinal)) return;
            if (!seen.Add(dir)) return;
            try { if (!Directory.Exists(dir)) return; } catch { return; }

            PickList.Item it = new PickList.Item
            {
                Path = dir,
                IsDir = true,
                Default = def,
                On = false
            };
            try { it.Name = Path.GetFileName(dir.TrimEnd('\\')); } catch { it.Name = dir; }
            if (string.IsNullOrEmpty(it.Name)) it.Name = dir;
            it.Size = Quarantine.DirSizeFast(dir);
            list.Add(it);
        }
    }

    // =====================================================================
    //  卸载后的残留确认窗
    // =====================================================================
    class LeftoverForm : Form
    {
        public List<PickList.Item> Picked;

        PickList list;
        Label head, sub;
        ActionButton okBtn, allBtn;

public LeftoverForm(string appName, List<PickList.Item> items)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(760, 540);
            BackColor = C.Bg;
            Text = "卸载残留";

            Panel all = new Panel { Dock = DockStyle.Fill, BackColor = C.Bg };

            head = new Label
            {
                Text = appName + "　卸载完了，这些要不要一起删掉？",
                Font = Fonts.BodyBold, ForeColor = C.Fg, BackColor = C.Bg,
                Location = new Point(24, 20), AutoSize = true
            };
            sub = new Label
            {
                Text = "已删除的部分不会出现在这里。勾中的会移到隔离区，随时能找回来。",
                Font = Fonts.Small, ForeColor = C.Fg2, BackColor = C.Bg,
                Location = new Point(24, 44), AutoSize = true
            };

            list = new PickList
            {
                Location = new Point(24, 74),
                Size = new Size(712, 386)
            };
            list.Items = items ?? new List<PickList.Item>();
            list.Changed += delegate { UpdateFoot(); };
            UpdateFoot();

            // 底栏（先建，靠后加 Dock=Bottom 才不会被 Fill 盖住）
            Panel foot = new Panel { Height = 64, Dock = DockStyle.Bottom, BackColor = C.Bg };
            foot.Paint += delegate(object snd, PaintEventArgs pe)
            {
                Graphics g = pe.Graphics;
                using (Pen p = new Pen(C.Line)) g.DrawLine(p, 0, 0, foot.Width, 0);
            };

            allBtn = new ActionButton("全选", false) { Width = 100, Height = 32 };
            allBtn.Location = new Point(24, 16);
            allBtn.Click += delegate { list.ToggleAll(); UpdateFoot(); };

            // 不设 Anchor：Parent 还没挂上时设 Anchor，WinForms 会拿错基准把位置算歪
            // （在 Confirm 上就表现为按钮被挤到窗口边裁掉一半）。
            // 这个窗允许缩放，所以另外挂 Resize 手动重排。
            okBtn = new ActionButton("完成", true) { Width = 130, Height = 32 };
            okBtn.Location = new Point(ClientSize.Width - 154, 16);
            okBtn.Click += delegate
            {
                Picked = list.PickedItems;
                DialogResult = DialogResult.OK;
                Close();
            };

            foot.Controls.Add(allBtn);
            foot.Controls.Add(okBtn);

            all.Controls.Add(head);
            all.Controls.Add(sub);
            all.Controls.Add(list);
            Controls.Add(all);
            Controls.Add(foot);

            Resize += delegate { okBtn.Left = Math.Max(0, ClientSize.Width - 154); };

            Shown += delegate { list.Focus(); };
            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { Close(); }
                if (e.KeyCode == Keys.Enter &&
                    (e.Modifiers & Keys.Control) == Keys.Control)
                {
                    Picked = list.PickedItems;
                    DialogResult = DialogResult.OK;
                    Close();
                }
            };
            Region = new Region(Draw.RR(0, 0, ClientSize.Width, ClientSize.Height, 12));
        }

        void UpdateFoot()
        {
            long s = list.PickedSize;
            int n = 0;
            for (int i = 0; i < list.Items.Count; i++) if (list.Items[i].On) n++;
            sub.Text = n > 0
                ? "勾中 " + n + " 项　共 " + Fmt.Size(s) + "　（Ctrl+Enter 确认）"
                : "没有勾选任何东西 —— 直接点「完成」就好";
        }
    }
}