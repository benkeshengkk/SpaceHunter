using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace SpaceHunter
{
    static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
    // =====================================================================
    //  主界面
    //
    //  左：磁盘环 + 分类勾选    右：清单 / 空间地图 / 软件卸载
    //  所有清单共用 ScrollList —— 滚轮坏掉也能用（拖滑块、点箭头、键盘都行）
    // =====================================================================
    class MainForm : Form
    {
        // 左栏
        Panel left;
        FlowLayoutPanel ringPanel;
        CatList catList;
        Label subtitle;
        ActionButton btnScan, btnClean, btnQuar, btnExclude, btnAbout;

        // 右栏
        Panel right;
        Label title, bigNum, statusLine;
        FlowLayoutPanel chips;
        Panel pageClean, pageSpace, pageApps;
        FileGrid fileGrid;
        BarList barList;
        AppList appList;
        Label appsHint;
        ActionButton btnScanApps, btnUninstall, btnStatMap;

        // 页签
        TabChip[] chipArray;
        TabChip chipAll, chipSpace, chipApps;

        // 磁盘（惰性读，读到就缓存；一个盘都没有时兜底给 C:）
        List<DriveX> drives;
        List<DriveX> Drives
        {
            get
            {
                if (drives != null) return drives;
                drives = new List<DriveX>();
                try
                {
                    foreach (DriveInfo d in DriveInfo.GetDrives())
                        if (d.DriveType == DriveType.Fixed && d.IsReady)
                            drives.Add(new DriveX(d.RootDirectory.FullName));
                }
                catch { }

                // --drives=N 用来在多盘机器上模拟单盘 / 零盘机器的布局。
                // 目标机器就是这么一台（只有一个盘），必须能真跑一遍才敢交。
                int lim = DriveLimit;
                if (lim >= 0 && drives.Count > lim) drives.RemoveRange(lim, drives.Count - lim);

                // 一个固定盘都读不到时也不能崩，退回 C:\ 让界面还是能打开
                if (drives.Count == 0) drives.Add(new DriveX("C:\\"));
                return drives;
            }
        }

        public static int DriveLimit = -1;

        /// <summary>当前正在看的盘。点环形图只改它，不重新扫。</summary>
        DriveX curDrive;

        /// <summary>这个盘有没有被扫过（决定状态栏怎么措辞）</summary>
        bool DriveScanned(DriveX d)
        {
            if (d == null) return false;
            if (scan == null || scan.Items.Count == 0) return false;
            for (int i = 0; i < scan.Items.Count; i++)
                if (string.Equals(scan.Items[i].Drive, d.Root, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        int page = 0;                       // 0 垃圾 1 空间 2 软件
        HashSet<Cat> catOn = new HashSet<Cat>();
        ScanEngine.Result scan = new ScanEngine.Result();
        bool scanning, cleaning, scanningMap;
        bool appScanned;

        public bool AutoScan
        {
            get { return autoScan; }
            set { autoScan = value; }
        }
        bool autoScan;

        public MainForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1180, 720);
            BackColor = C.Bg;
            Text = "空间猎手 SpaceHunter";
            MinimumSize = new Size(1020, 640);

            subtitle = new Label
            {
                Font = Fonts.Small, ForeColor = C.Fg3, BackColor = C.Bg,
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "先扫描，再决定删什么"
            };
            title = new Label
            {
                Font = Fonts.SectionTitle, ForeColor = C.Fg, BackColor = C.Bg,
                TextAlign = ContentAlignment.MiddleLeft, Text = "垃圾清单"
            };
            statusLine = new Label
            {
                Font = Fonts.Body, ForeColor = C.Fg2, BackColor = C.Bg,
                TextAlign = ContentAlignment.MiddleLeft, Text = "准备就绪"
            };
            bigNum = new Label
            {
                Font = Fonts.HugeNum, ForeColor = C.Fg, BackColor = C.Bg,
                TextAlign = ContentAlignment.MiddleLeft, Text = "—"
            };

            foreach (Cat c in CatMeta.All) if (CatMeta.DefaultOn(c)) catOn.Add(c);

            // 当前看哪个盘：默认第一个。点环形图只改它，不会重新扫这块盘。
            List<DriveX> ds0 = Drives;
            curDrive = ds0.Count > 0 ? ds0[0] : null;
            mapDrive = curDrive;

            BuildLeft();
            BuildRight();

            Resize += delegate { DoLayout(); };
            DoLayout();
            Shown += delegate { if (autoScan) StartScan(); };
            KeyPreview = true;
            KeyDown += OnGlobalKey;
            catList.On = catOn;
        }

        void TryClose()
        {
            if (scanning || cleaning)
            {
                if (!Ask("还有任务在跑，确定要走？", "退出")) return;
            }
            Close();
        }

        // ---------------- 左栏 ----------------
// ================================================================
        //  布局
        //
        //  全部手工算坐标。原因很实际：Dock 的布局顺序是「后添加的先靠边」，
        //  一旦添加顺序变了就会出现「右栏被左栏盖掉」这种问题，
        //  而且报错时完全看不出来。写死坐标虽然土，但一眼能验证。
        // ================================================================
        const int LeftW = 272;
        const int LogoH = 56;
        const int SubH = 24;
        const int ChipH = 92;
        const int BigH = 56;
        const int StatH = 30;
        const int FootH = 62;

        Panel logo, leftFoot, cleanFoot, appsFoot, mapFoot;
        WinBtn winClose, winMin;

        void BuildLeft()
        {
            left = new Panel { BackColor = C.Bg };

            logo = new Panel { BackColor = C.Bg };
            logo.Paint += delegate(object s, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                using (GraphicsPath p = Draw.RR(20, 22, 26, 26, 8))
                using (LinearGradientBrush lg = new LinearGradientBrush(
                    new Rectangle(20, 22, 26, 26), C.Accent, C.Warn, 60f))
                    g.FillPath(lg, p);
                TextRenderer.DrawText(g, "空间猎手", Fonts.BigTitle, new Rectangle(56, 20, 200, 30),
                    C.Fg, TextFormatFlags.VerticalCenter);
            };

            Panel sep = new Panel { BackColor = C.Bg };
            sep.Paint += delegate(object s, PaintEventArgs e)
            { e.Graphics.DrawLine(Br.PenLine, 20, sep.Height - 1, LeftW - 20, sep.Height - 1); };

            ringPanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = false,          // 高度本来就按盘数算好了，别再冒系统滚动条
                BackColor = C.Bg,
                Padding = new Padding(14, 2, 4, 2)
            };

            catList = new CatList { BackColor = C.Bg };
            catList.Changed += delegate { FilterList(); };

            leftFoot = new Panel { BackColor = C.Bg };
            leftFoot.Paint += delegate(object s, PaintEventArgs e)
            { e.Graphics.DrawLine(Br.PenLine, 20, 0, LeftW - 20, 0); };
            btnQuar = MkBtn("隔离区", 108);
            btnExclude = MkBtn("排除项", 108);
            btnAbout = MkBtn("关于", 108);
            btnQuar.Click += delegate { ShowQuarantine(); };
            btnExclude.Click += delegate { ShowExcludeList(); };
            btnAbout.Click += delegate { ShowAbout(); };
            leftFoot.Controls.Add(btnQuar);
            leftFoot.Controls.Add(btnExclude);
            leftFoot.Controls.Add(btnAbout);

            left.Controls.Add(catList);
            left.Controls.Add(ringPanel);
            left.Controls.Add(sep);
            left.Controls.Add(subtitle);
            left.Controls.Add(logo);
            left.Controls.Add(leftFoot);
            Controls.Add(left);
        }

        ActionButton MkBtn(string cap, int w)
        {
            ActionButton b = new ActionButton(cap, false) { Width = w, Height = 30 };
            return b;
        }

        void BuildRight()
        {
            right = new Panel { BackColor = C.Bg };

            chips = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                // 12 个页签一行放不下，折成两行 —— 宁可多占 44px，
                // 也不要一个系统原生滚动条把整行切掉
                WrapContents = true,
                AutoScroll = false,
                BackColor = C.Bg,
                Padding = new Padding(20, 6, 20, 0)
            };
            BuildChips();

            // --- 垃圾 ---
            pageClean = new Panel { BackColor = C.Bg };
            fileGrid = new FileGrid();
            fileGrid.Changed += delegate { UpdateTotals(); };
            fileGrid.OnExclude += ExcludeOne;
            cleanFoot = new Panel { BackColor = C.Bg };
            cleanFoot.Paint += delegate(object s, PaintEventArgs e)
            { e.Graphics.DrawLine(Br.PenLine, 24, 0, cleanFoot.Width, 0); };
            btnClean = new ActionButton("清理所选", true) { Width = 148, Height = 34 };
            btnClean.Click += delegate { Clean(); };
            btnScan = new ActionButton("重新扫描", false) { Width = 120, Height = 34 };
            btnScan.Click += delegate { StartScan(); };
            cleanFoot.Controls.Add(btnClean);
            cleanFoot.Controls.Add(btnScan);
            pageClean.Controls.Add(fileGrid);
            pageClean.Controls.Add(cleanFoot);

            // --- 空间地图 ---
            pageSpace = new Panel { BackColor = C.Bg };
            barList = new BarList();
            barList.OpenFolder += delegate(string p) { OpenIn(p); };
            mapFoot = new Panel { BackColor = C.Bg };
            mapFoot.Paint += delegate(object s, PaintEventArgs e)
            { e.Graphics.DrawLine(Br.PenLine, 24, 0, mapFoot.Width, 0); };
            btnStatMap = new ActionButton("统计这个盘", true) { Width = 148, Height = 34 };
            btnStatMap.Click += delegate
            {
                if (curDrive == null || scanningMap) return;
                StatDrive(curDrive);
            };
            mapFoot.Controls.Add(btnStatMap);
            pageSpace.Controls.Add(barList);
            pageSpace.Controls.Add(mapFoot);

            // --- 软件 ---
            pageApps = new Panel { BackColor = C.Bg };
            appList = new AppList();
            appList.Changed += delegate { UpdateAppsFoot(); };
            appsHint = new Label
            {
                Font = Fonts.Small, ForeColor = C.Fg3, BackColor = C.Bg,
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "这里先不显示任何东西 —— 你得先扫一次，扫完才有清单。"
            };
            appsFoot = new Panel { BackColor = C.Bg };
            appsFoot.Paint += delegate(object s, PaintEventArgs e)
            { e.Graphics.DrawLine(Br.PenLine, 24, 0, appsFoot.Width, 0); };
            btnScanApps = new ActionButton("扫描已安装软件", true) { Width = 168, Height = 34 };
            btnScanApps.Click += delegate { ScanApps(); };
            btnUninstall = new ActionButton("卸载所选", false) { Width = 128, Height = 34 };
            btnUninstall.Enabled = false;
            btnUninstall.Click += delegate { UninstallPicked(); };
            appsFoot.Controls.Add(btnScanApps);
            appsFoot.Controls.Add(btnUninstall);
            pageApps.Controls.Add(appsHint);
            pageApps.Controls.Add(appList);
            pageApps.Controls.Add(appsFoot);

            foreach (Panel p in new Panel[] { pageClean, pageSpace, pageApps })
            {
                p.Visible = false;
                right.Controls.Add(p);
            }
            right.Controls.Add(statusLine);
            right.Controls.Add(bigNum);
            right.Controls.Add(chips);
            right.Controls.Add(title);
            Controls.Add(right);

            // 窗口按钮挂在 right 上：挂在 Form 上会因为客户区/窗口尺寸差被裁掉
            winClose = new WinBtn(true);
            winMin = new WinBtn(false);
            winClose.Click += delegate { TryClose(); };
            winMin.Click += delegate { WindowState = FormWindowState.Minimized; };
            right.Controls.Add(winClose);
            right.Controls.Add(winMin);

            SetTab(-1);
        }

        void BuildChips()
        {
            chips.Controls.Clear();
            chipAll = new TabChip("全部") { Width = 88 };
            chipAll.Click += delegate { SetTab(-1); };
            chips.Controls.Add(chipAll);

            chipArray = new TabChip[CatMeta.All.Length];
            for (int i = 0; i < CatMeta.All.Length; i++)
            {
                int k = i;
                TabChip t = new TabChip(CatMeta.Name(CatMeta.All[i])) { Width = 112 };
                t.Click += delegate { SetTab(k); };
                chipArray[i] = t;
                chips.Controls.Add(t);
            }
            chips.Controls.Add(new Panel { Width = 14, Height = 1 });
            chipSpace = new TabChip("空间地图") { Width = 108 };
            chipSpace.Click += delegate { ShowPage(1); };
            chips.Controls.Add(chipSpace);
            chipApps = new TabChip("软件卸载") { Width = 108 };
            chipApps.Click += delegate { ShowPage(2); };
            chips.Controls.Add(chipApps);
            // 注意：这里不能调 SetTab —— 它要用到 pageClean/pageSpace/pageApps，
            // 那三个是在 BuildChips 之后才建的。放到 BuildRight 末尾统一做。
        }

        int curCat = -1;      // -1 = 全部

        void SetTab(int cat)
        {
            curCat = cat;
            ShowPage(0);
            FilterList();
        }

        void ShowPage(int p)
        {
            page = p;
            pageClean.Visible = p == 0;
            pageSpace.Visible = p == 1;
            pageApps.Visible = p == 2;
            chipAll.On = p == 0 && curCat == -1;
            chipSpace.On = p == 1;
            chipApps.On = p == 2;
            for (int i = 0; i < chipArray.Length; i++)
                chipArray[i].On = p == 0 && curCat == i;
            title.Text = p == 0 ? "垃圾清单" : p == 1 ? "空间地图" : "软件卸载";
            DoLayout();
            RefreshHead();
        }

        /// <summary>大数字和状态栏要跟着当前页面走 —— 不然切过去还写着上一页的数</summary>
        void RefreshHead()
        {
            if (page == 0)
            {
                UpdateTotals();
                return;
            }
            if (page == 1)
            {
                bigNum.Text = mapDrive == null ? "—" : Fmt.Size(mapDrive.Used);
                statusLine.Text = mapDrive == null
                    ? "点左边的环形图，挑一个盘"
                    : mapDrive.Title + "　已用 " + Fmt.Size(mapDrive.Used) +
                      " / " + Fmt.Size(mapDrive.Total) + "　·　双击一行能直接打开那个文件夹";
                return;
            }
            // 软件页
            if (!appScanned)
            {
                bigNum.Text = "—";
                statusLine.Text = "还没扫。点下面的「扫描已安装软件」。";
                return;
            }
            bigNum.Text = appList.Items.Count + " 个";
            UpdateAppsFoot();
            statusLine.Text = string.Format("扫到 {0} 个　·　{1} 个带卸载命令　·　{2} 个是磁盘/快捷方式找到的",
                appList.Items.Count,
                CountWhere(appList.Items, delegate(AppEntry a) { return a.CanUninstall; }),
                CountWhere(appList.Items, delegate(AppEntry a) { return a.FoundOnDisk || a.FromShortcut; }));
        }

        DriveX mapDrive;

        void DoLayout()
        {
            if (chips == null || catList == null) return;
            int W = ClientSize.Width, H = ClientSize.Height;
            int RW = Math.Max(200, W - LeftW);

            // ---- 左栏 ----
            left.SetBounds(0, 0, LeftW, H);
            logo.SetBounds(0, 0, LeftW, LogoH);
            int ringTop = LogoH + SubH;
            int ringH = DriveCount * 104 + 4;
            if (ringH < 0) ringH = 0;
            ringPanel.SetBounds(0, ringTop, LeftW, ringH);
            int catTop = ringTop + ringH + 6;
            leftFoot.SetBounds(0, H - 96, LeftW, 96);
            catList.SetBounds(0, catTop, LeftW, Math.Max(40, H - catTop - 96));
            btnQuar.SetBounds(20, 16, 108, 30);
            btnExclude.SetBounds(140, 16, 108, 30);
            btnAbout.SetBounds(20, 54, 108, 30);

            // ---- 右栏 ----
            right.SetBounds(LeftW, 0, RW, H);
            title.SetBounds(0, 0, RW, 40);
            chips.SetBounds(0, 40, RW, ChipH);
            bigNum.SetBounds(0, 40 + ChipH, RW, BigH);
            statusLine.SetBounds(0, 40 + ChipH + BigH, RW, StatH);

            int pageTop = 40 + ChipH + BigH + StatH;
            int pageH = Math.Max(80, H - pageTop);

            pageClean.SetBounds(0, pageTop, RW, pageH);
            cleanFoot.SetBounds(0, pageH - FootH, RW, FootH);
            fileGrid.SetBounds(0, 0, RW, pageH - FootH);
            btnClean.SetBounds(24, 14, 148, 34);
            btnScan.SetBounds(184, 14, 120, 34);

            pageSpace.SetBounds(0, pageTop, RW, pageH);
            // 空间地图的底栏和按钮也必须摆位置。早先漏了这一段：
            // 控件建出来了却从来没 SetBounds 过（尺寸停在 0×0），
            // 于是「统计这个盘」按钮压根不显示 —— 界面上连个入口都没有，
            // 地图就永远停在一句提示语上。
            mapFoot.SetBounds(0, pageH - FootH, RW, FootH);
            barList.SetBounds(0, 0, RW, pageH - FootH);
            btnStatMap.SetBounds(24, 14, 148, 34);

            pageApps.SetBounds(0, pageTop, RW, pageH);
            appsHint.SetBounds(24, 4, RW - 48, 30);
            appsFoot.SetBounds(0, pageH - FootH, RW, FootH);
            appList.SetBounds(0, 34, RW, pageH - FootH - 34);
            btnScanApps.SetBounds(24, 14, 168, 34);
            btnUninstall.SetBounds(204, 14, 128, 34);

            // ---- 窗口按钮（无边框窗口必须自己给）----
            // 注意坐标是相对于 right 面板的，不能用 Form 的宽度
            if (winClose != null)
            {
                winClose.SetBounds(RW - 44, 0, 44, 32);
                winMin.SetBounds(RW - 88, 0, 44, 32);
            }
            if (title != null) title.Width = Math.Max(60, RW - 100);

            RebuildRings();
        }

        int DriveCount
        {
            get { return Drives.Count; }
        }

        void RebuildRings()
        {
            List<DriveX> ds = Drives;
            ringPanel.SuspendLayout();
            ringPanel.Controls.Clear();
            for (int i = 0; i < ds.Count; i++)
            {
                DriveX d = ds[i];
                RingChart rc = new RingChart(d)
                {
                    Size = new Size(Math.Max(80, LeftW - 32), 100),
                    Selected = curDrive != null &&
                        string.Equals(curDrive.Root, d.Root, StringComparison.OrdinalIgnoreCase)
                };
                rc.RingClicked += delegate(DriveX x) { SelectDrive(x); };
                ringPanel.Controls.Add(rc);
            }
            ringPanel.ResumeLayout();
        }

        /// <summary>
        /// 点环形图 = 只切换「现在看哪个盘」。
        ///
        /// 这里绝对不能再去扫盘。早先这里是直接 ShowMapOf，它会把整块盘
        /// 逐目录走一遍算体积 —— 用户点一下哪个盘，就等于又扫了一遍哪个盘，
        /// 而且是在垃圾已经扫完的情况下再来一遍。所以：
        ///   · 垃圾清单：只是过滤已有结果，瞬间
        ///   · 空间地图：只有缓存里没有才统计，且统计是可取消的渐进式
        /// </summary>
        void SelectDrive(DriveX d)
        {
            if (d == null) return;
            bool same = curDrive != null &&
                string.Equals(curDrive.Root, d.Root, StringComparison.OrdinalIgnoreCase);
            curDrive = d;
            if (same && page != 1) return;

            if (page == 1) ShowMapOf(d, false);
            else { DoLayout(); FilterList(); }
        }
        // ---------------- 扫描 ----------------
        void StartScan()
        {
            if (scanning || cleaning) return;
            scanning = true;
            btnScan.Enabled = false;
            btnStatMap.Enabled = false;

            List<DriveX> ds = Drives;
            ProgressForm pf = new ProgressForm("扫描中", "正在扫描 " + ds.Count + " 个盘");

            ScanEngine.Result[] box = new ScanEngine.Result[1];
            pf.Run(delegate(ProgressForm f)
            {
                // 直接问进度窗的 Cancelled，不要自己再维护一份 bool ——
                // 两份状态容易对不上（曾经就因为对不上，点了停止还一直扫）
                box[0] = ScanEngine.Run(ds, delegate { return f.Cancelled; },
                    delegate(int d, int t, string s) { f.Progress(d, t, s); });
            });

            while (pf.Visible) { Application.DoEvents(); Thread.Sleep(20); }

            scanning = false;
            btnScan.Enabled = true;
            btnStatMap.Enabled = true;

            bool stopped = pf.Cancelled;
            ScanEngine.Result r = box[0];

            if (r != null && r.Items.Count > 0)
            {
                scan = r;
                DoLayout();
                catList.Load(scan.Sizes, scan.Counts, catOn);
                FilterList();

                // 取消之后必须说清楚发生了什么。已扫到的部分照样留下来可用 ——
                // 比全丢掉对用户有用得多（哪怕只扫了一半，几十项也能先清）。
                if (stopped || r.Cancelled)
                {
                    subtitle.Text = string.Format("已按你的要求停下　扫到 {0} 项",
                        scan.Items.Count);
                    Msg(string.Format(
                        "扫描已停止。\n\n已经扫到的 {0} 项（共 {1}）保留在下面，可以直接用；\n" +
                        "但这个结果不完整 —— 没扫到的部分不会出现在清单里。\n\n" +
                        "想看全的话，再点一次「重新扫描」。",
                        scan.Items.Count, Fmt.Size(scan.Total)),
                        "已停止", false);
                }
                else
                {
                    subtitle.Text = string.Format("扫完了，用了 {0}　共 {1} 项",
                        Fmt.Elapsed(TimeSpan.FromSeconds(scan.Seconds)), scan.Items.Count);
                }
            }
            else if (stopped)
            {
                subtitle.Text = "已按你的要求停下（这一轮什么也没扫到）";
            }
            DoLayout();
            UpdateTotals();
        }

        void FilterList()
        {
            List<Finding> show = new List<Finding>();
            long all = 0;
            for (int i = 0; i < scan.Items.Count; i++)
            {
                Finding f = scan.Items[i];

                // 先按盘过滤。用户点哪个盘就只看哪个盘 —— 这只是过滤已经扫过的结果，
                // 不重新扫盘。没选盘（比如 --drives=0 的极端情况）就不过滤。
                if (curDrive != null && !string.IsNullOrEmpty(f.Drive)
                    && !string.Equals(f.Drive, curDrive.Root, StringComparison.OrdinalIgnoreCase))
                    continue;

                // 选了具体分类：只看这一类，跟左边的勾选无关。
                //   （早先这里也套了 catOn 过滤，结果点「聊天缓存」这种没勾选的
                //     分类，右边一片空白 —— 看着像没扫到东西。）
                if (curCat >= 0)
                {
                    if ((int)f.Cat != curCat) continue;
                }
                else
                {
                    // 「全部」页：按左边的勾选过滤
                    if (catOn.Count > 0 && !catOn.Contains(f.Cat)) continue;
                }

                show.Add(f);
                all += f.Size;
            }
            fileGrid.Set(show);
            bigNum.Text = Fmt.Size(all);
            UpdateTotals();

            if (scan.Items.Count > 0 && show.Count == 0)
            {
                string cats = "";
                foreach (Cat c in catOn) cats += c + ",";
                Program.Log("FilterList 空结果", new Exception(
                    "scan=" + scan.Items.Count + " 显示=" + show.Count +
                    " curCat=" + curCat + " curDrive=" + (curDrive == null ? "null" : curDrive.Root) +
                    " catOn={" + cats + "}"));
            }
        }

        void UpdateTotals()
        {
            if (fileGrid == null) return;
            string who = curDrive == null ? "本机" : curDrive.Title;
            statusLine.Text = string.Format("{0}　共 {1} 项　已选 {2} 项 / {3}",
                who, fileGrid.Items.Count, fileGrid.SelectedCount, Fmt.Size(fileGrid.SelectedSize));
            if (scan != null && scan.Items.Count > 0)
                statusLine.Text += string.Format("　·　三个盘一共扫到 {0} 项 / {1}{2}",
                    scan.Items.Count, Fmt.Size(scan.Total),
                    scan.Cancelled ? "　·　中途停止过，结果不完整" : "");
        }

        void UpdateAppsFoot()
        {
            btnUninstall.Enabled = appList.PickedCount > 0;
            // 没扫描时才用这行提示，扫描完的统计交给 statusLine，别两处重复
            appsHint.Visible = !appScanned;
            if (!appScanned) return;
            appList.EmptyText = appList.Items.Count == 0
                ? "扫完了，一个软件都没找到（这台机器上可能真的很干净）"
                : "";
        }

        // ---------------- 清理 ----------------
        void Clean()
        {
            if (cleaning) return;
            List<Finding> sel = new List<Finding>();
            for (int i = 0; i < fileGrid.Items.Count; i++)
                if (fileGrid.Items[i].On) sel.Add(fileGrid.Items[i]);
            if (sel.Count == 0)
            {
                Msg("先在上面勾几项。", "没勾东西", false);
                return;
            }

            long total = 0;
            for (int i = 0; i < sel.Count; i++) total += sel[i].Size;
            if (!Ask("要清理 " + sel.Count + " 项，共 " + Fmt.Size(total) + "。\n\n" +
                "能搬动的移到隔离区（后悔了可以自己找回来），搬不动的直接删掉。\n\n" +
                "确定继续？", "确认清理"))
                return;

            cleaning = true;
            btnClean.Enabled = false;
            btnScan.Enabled = false;

            List<string> leftOver = new List<string>();      // "显示名（原因）"，给用户看
            List<string> leftPaths = new List<string>();     // 真正的路径，给 UAC 用
            int done = 0, failed = 0;

            ProgressForm pf = new ProgressForm("清理中", "正在处理 " + sel.Count + " 项");
            pf.Run(delegate(ProgressForm f)
            {
                for (int i = 0; i < sel.Count; i++)
                {
                    if (f.Cancelled) break;

                    Finding x = sel[i];

                    // 每项都先报一次进度。原来是 (i & 1) == 0 才报 ——
                    // 一旦卡在某个奇数项上，进度条就完全不动，看着跟死机没区别。
                    f.Progress(i, sel.Count,
                        "第 " + (i + 1) + " / " + sel.Count + " 项　" + x.DisplayName);

                    string dest;
                    string why;
                    if (!x.Blocked)
                    {
                        // 把取消信号传进去。跨盘复制大目录可能要走好几分钟，
                        // 不传的话用户在地图上点「停止」会看着像完全没反应，
                        // 只能强行结束程序 —— 而强结束会留下提权子进程占着 exe。
                        why = Quarantine.Move(x.Path, out dest,
                            delegate { return f.Cancelled; });
                        if (why != null && why != "已取消") why = Quarantine.Kill(x.Path);
                    }
                    else
                    {
                        why = Quarantine.Kill(x.Path);
                    }

                    if (why == "已取消") break;
                    if (why == null) done++;
                    else
                    {
                        failed++;
                        // 记下卡住的原因，最后一并告诉用户是哪几项、为什么
                        leftOver.Add(x.DisplayName + "　（" + why + "）");
                        leftPaths.Add(x.Path);
                    }
                }
                f.Progress(sel.Count, sel.Count, "");
            });
            while (pf.Visible) { Application.DoEvents(); Thread.Sleep(20); }

            bool stoppedClean = pf.Cancelled;

            // 还剩的交给 UAC 提权再来一次。
            // 用户主动点了取消的话就别去弹 UAC 了 —— 他已经说了不想继续。
            bool timedOut = false;
            if (leftPaths.Count > 0 && !stoppedClean && !Elevated.IsElevated())
            {
                bool denied;
                Elevated.Result r = Elevated.Run(leftPaths, out denied);
                timedOut = Elevated.LastTimeout;
                done += r.Ok;
                failed -= r.Ok;
                for (int i = 0; i < r.Ok && i < leftOver.Count; i++)
                {
                    // 提权删掉的那几项，从待清理名单里摘出去
                    if (i < leftPaths.Count)
                    {
                        leftOver.RemoveAt(i);
                        leftPaths.RemoveAt(i);
                        i--;
                    }
                }
                if (denied)
                    Msg("有几项需要管理员权限。你点了「否」，所以那几项还留在原地。\n\n" +
                        "想清掉它们，就右键用管理员身份重新运行本程序。", "还有没清掉的", false);
            }

            cleaning = false;
            btnClean.Enabled = true;
            btnScan.Enabled = true;

            // 把没清掉的原样列出来 —— 原来只给一个数字，用户完全不知道是哪几项、
            // 为什么没删掉，下次还会踩同样的坑。
            string detail = "";
            if (leftOver.Count > 0)
            {
                int show = leftOver.Count < 8 ? leftOver.Count : 8;
                detail = "\n\n没成功的是这 " + leftOver.Count + " 项（只列前 " + show + " 项）：\n";
                for (int i = 0; i < show; i++) detail += "　· " + leftOver[i] + "\n";
                if (leftOver.Count > show) detail += "　… 还有 " + (leftOver.Count - show) + " 项\n";
                detail += "\n绝大多数是「正被占用」—— 关掉对应程序或重启一次就能清掉。\n" +
                          "隔离区在：\n" + Quarantine.Root;

                if (timedOut)
                    detail = "\n\n提权的那一步跑了超过 10 分钟还没结束，已经把它停掉了。\n" +
                             "剩下的几项需要你用管理员身份重新运行本程序再清一次。" +
                             detail;
            }

            Msg(string.Format("{0}　清掉 {1} 项，{2} 项没成功。{3}",
                    stoppedClean ? "清理已按你的要求停止。" : "清理完成。",
                    done, failed, detail),
                stoppedClean ? "已停止" : "清理完成",
                failed == 0 && !stoppedClean);

            // 从结果里去掉已处理的
            HashSet<string> gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < sel.Count; i++)
                if (!leftPaths.Contains(sel[i].Path)) gone.Add(sel[i].Path);
            for (int i = scan.Items.Count - 1; i >= 0; i--)
                if (gone.Contains(scan.Items[i].Path)) scan.Items.RemoveAt(i);
            catList.Load(scan.Sizes, scan.Counts, catOn);
            DoLayout();
            FilterList();
        }

        // ---------------- 排除 ----------------
        void ExcludeOne(string path)
        {
            if (!Ask("以后都不再碰这个路径：\n\n" + path + "\n\n确定？", "排除")) return;
            ScanEngine.AddExclusion(path);
            StartScan();
        }

        void ShowExcludeList()
        {
            string f = Path.Combine(Env.LocalAppData, @"SpaceHunter\exclude.txt");
            List<string> lines = new List<string>();
            try { if (File.Exists(f)) lines.AddRange(File.ReadAllLines(f)); } catch { }
            Msg(lines.Count == 0
                ? "还没有排除任何东西。\n\n在垃圾清单里右键某一项就能排除。"
                : "当前排除的路径：\n\n" + string.Join("\n", lines.ToArray()),
                "排除项", true);
        }

        // ---------------- 空间地图 ----------------

        // 每块盘统计过没有。统计一次就存下来，来回切盘不再重扫。
        Dictionary<string, List<DirSize>> mapCache =
            new Dictionary<string, List<DirSize>>(StringComparer.OrdinalIgnoreCase);

        void ShowMapOf(DriveX d)
        {
            ShowMapOf(d, true);
        }

        void ShowMapOf(DriveX d, bool allowStat)
        {
            mapDrive = d;
            ShowPage(1);
            RefreshHead();

            string key = d.Root;

            List<DirSize> cached;
            if (mapCache.TryGetValue(key, out cached))
            {
                barList.Finish(cached);
                return;
            }

            if (!allowStat)
            {
                barList.StartList("（还没统计过，点下面的「统计这个盘」）");
                return;
            }
            StatDrive(d);
        }

        /// <summary>
        /// 统计当前盘的顶层目录。
        ///
        /// 关键：不阻塞在「一次性算完」上。先把目录名全列出来（瞬间），
        /// 大小在后台一个一个补，界面上边出边填。所以即使 C 盘有几十万个目录项，
        /// 用户也是立刻看到东西，而不是盯着一块空白等一分钟。
        /// </summary>
        void StatDrive(DriveX d)
        {
            if (scanningMap) return;
            scanningMap = true;
            try { StatDriveCore(d); }
            finally { scanningMap = false; btnStatMap.Enabled = true; }
        }

        void StatDriveCore(DriveX d)
        {
            string root = d.Root;
            barList.StartList("");
            barList.BeginStat();          // 此刻才真的开始算体积

            ProgressForm pf = new ProgressForm("统计中", d.Title);
            pf.Run(delegate(ProgressForm f)
            {
                try
                {
                    string[] subs = Directory.GetDirectories(root);
                    // 第一步：先把名字全铺上去，用户马上能看到清单
                    barList.SetDirs(subs);

                    // 第二步：一个一个算，边算边显示
                    for (int i = 0; i < subs.Length; i++)
                    {
                        if (f.Cancelled) break;
                        long sz = Quarantine.DirSizeFast(subs[i], delegate { return f.Cancelled; });
                        if (sz < 0) break;                      // 取消了
                        barList.SetSize(i, sz);
                        f.Progress(i + 1, subs.Length, Path.GetFileName(subs[i]));
                    }
                }
                catch (Exception ex) { Program.Log("StatDrive", ex); }

                // 无论正常结束、取消还是抛异常，都必须把「统计中」这个状态收掉。
                // 漏掉任何一条路径，界面上就会永远挂着「统计中」——而实际上
                // 什么都没在跑，用户只能干瞪眼。
                barList.EndStat();
                barList.FinishStat();
                mapCache[root] = barList.ItemsSnapshot;
            });
            while (pf.Visible) { Application.DoEvents(); Thread.Sleep(20); }
            RefreshHead();
        }

        void OpenIn(string p)
        {
            try
            {
                if (Directory.Exists(p)) Process.Start("explorer.exe", "\"" + p + "\"");
            }
            catch { }
        }

        // ---------------- 隔离区 ----------------
        void ShowQuarantine()
        {
            long sz = Quarantine.Exists ? Quarantine.SizeOf() : 0;
            if (!Quarantine.Exists || sz == 0)
            {
                Msg("隔离区现在是空的。\n\n" + Quarantine.Root, "隔离区", true);
                return;
            }
            if (!AskDanger("隔离区里有 " + Fmt.Size(sz) + "。\n\n清空之后就找不回来了，程序也救不回来。\n\n真的要清空？", "清空隔离区"))
                return;

            ProgressForm pf = new ProgressForm("清空中", "正在清空隔离区");
            pf.Run(delegate(ProgressForm f)
            {
                // 先走提权那一条：里面可能有带 Deny 保护的系统目录残留，
                // 非管理员清不掉。用户点了「清空」却什么都没清掉是最糟的。
                if (!Elevated.IsElevated()) Quarantine.PurgeElevated();
                else Quarantine.Purge(delegate(int d, int t) { f.Progress(d, t, ""); }, null);
            });
            while (pf.Visible) { Application.DoEvents(); Thread.Sleep(20); }

            long left = Quarantine.Exists ? Quarantine.SizeOf() : 0;
            Msg(left <= 0
                ? "隔离区已经清空了。\n\n" + Quarantine.Root
                : "已经清掉大部分，但还剩 " + Fmt.Size(left) + "。\n\n" +
                  "这些多半是当初带着系统保护权限搬进来的，非管理员动不了。\n\n" +
                  "用管理员身份重新运行一次本程序，再清一次就干净了。\n\n" +
                  Quarantine.Root,
                left <= 0 ? "完成" : "还剩一点",
                left <= 0);
        }

        // ---------------- 软件 ----------------
        void ScanApps()
        {
            btnScanApps.Enabled = false;
            List<AppEntry>[] box = new List<AppEntry>[1];
            ProgressForm pf = new ProgressForm("扫描软件", "正在找这台电脑上的软件");
            pf.Run(delegate(ProgressForm f)
            {
                box[0] = Installer.Scan(null, delegate(int d, int t, string s)
                {
                    f.Progress(d, t, s);
                });
            });
            while (pf.Visible) { Application.DoEvents(); Thread.Sleep(20); }
            btnScanApps.Enabled = true;

            if (box[0] == null) return;
            appList.Set(box[0]);
            appScanned = true;
            UpdateAppsFoot();
            if (page == 2) RefreshHead();
        }

        static int CountWhere(List<AppEntry> l, Func<AppEntry, bool> f)
        {
            int n = 0;
            for (int i = 0; i < l.Count; i++) if (f(l[i])) n++;
            return n;
        }

        void UninstallPicked()
        {
            List<AppEntry> sel = appList.PickedItems;
            if (sel.Count == 0) return;
            int canUn = 0;
            for (int i = 0; i < sel.Count; i++) if (sel[i].CanUninstall) canUn++;
            if (canUn == 0)
            {
                Msg("选中的这些都没有卸载命令。\n\n它们是扫磁盘时找到的（绿色软件或没登记的），" +
                    "只能告诉你装在哪，卸载得去软件自己的界面里点。", "没法卸载", false);
                return;
            }
            if (!Ask(string.Format(
                "准备卸载 {0} 个软件。\n\n带卸载命令的会走官方卸载程序，" +
                "不带的我只会给你看位置。\n\n有的卸载程序会弹自己的界面，需要你在那个窗口里点确认。\n\n开始？",
                canUn), "确认卸载"))
                return;

            List<AppEntry> done = new List<AppEntry>();
            for (int i = 0; i < sel.Count; i++)
            {
                if (!sel[i].CanUninstall) continue;
                Installer.UnResult r = Installer.Uninstall(sel[i]);
                if (r.Launched) done.Add(sel[i]);
            }

            if (done.Count == 0)
            {
                Msg("一个都没能启动。可能是卸载程序被拦了，或者需要管理员权限 —— " +
                    "那就用管理员身份再跑一次本程序。", "没启动成功", false);
                return;
            }
            Msg(string.Format("{0} 个卸载程序已经跑完（有几个可能还开着它自己的窗口，去看看）。\n\n" +
                "接下来逐个核对残留。", done.Count), "卸载程序已启动", true);

            for (int i = 0; i < done.Count; i++) ScanLeftover(done[i]);
        }

        void ScanLeftover(AppEntry a)
        {
            List<PickList.Item>[] box = new List<PickList.Item>[1];
            ProgressForm pf = new ProgressForm("查找残留", a.Name);
            pf.Run(delegate(ProgressForm f)
            {
                box[0] = Installer.Leftovers(a, null);
                f.Progress(1, 1, "");
            });
            while (pf.Visible) { Application.DoEvents(); Thread.Sleep(20); }
            if (box[0] == null || box[0].Count == 0) return;

            LeftoverForm lf = new LeftoverForm(a.Name, box[0]);
            lf.ShowDialog();
            if (lf.Picked == null || lf.Picked.Count == 0) return;

            List<string> paths = new List<string>();
            for (int i = 0; i < lf.Picked.Count; i++) paths.Add(lf.Picked[i].Path);

            int ok = 0;
            List<string> bad = new List<string>();
            for (int i = 0; i < paths.Count; i++)
            {
                string dest;
                string why = Quarantine.Move(paths[i], out dest);
                if (why != null) why = Quarantine.Kill(paths[i]);
                if (why == null) ok++; else bad.Add(paths[i]);
            }
            if (bad.Count > 0 && !Elevated.IsElevated())
            {
                bool denied;
                Elevated.Result r = Elevated.Run(bad, out denied);
                ok += r.Ok;
                bad.Clear();
                bad.AddRange(r.Failed);
            }
            Msg(string.Format("{0.Name}：清掉 {1} 项残留，{2} 项没成功。",
                    a, ok, bad.Count), "残留处理", bad.Count == 0);
        }

        // ---------------- 杂项 ----------------
        void ShowAbout()
        {
            Msg(
                "空间猎手 SpaceHunter\n" +
                "────────────────────\n" +
                "找得到垃圾就列出来，列出来才让你删。\n" +
                "删的一律先进隔离区，你反悔了还能自己找回来。\n\n" +
                "操作提示（给滚轮不好使的人）：\n" +
                "· 列表右侧那条竖条可以拖，也可以点两端翻页\n" +
                "· 列表点一下再按 ↑ ↓ PgUp PgDn Home End\n\n" +
                "隔离区：\n" + Quarantine.Root + "\n\n" +
                "排除清单：\n" + Path.Combine(Env.LocalAppData, @"SpaceHunter\exclude.txt"),
                "关于", true);
        }

        void OnGlobalKey(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F5) { StartScan(); e.Handled = true; }
            if (e.KeyCode == Keys.F1) { ShowAbout(); e.Handled = true; }
        }

        bool AskDanger(string text, string cap)
        {
            return MsgBox(text, cap, true, true, true);
        }

        bool Ask(string text, string cap)
        {
            return MsgBox(text, cap, true, true, false);
        }

        void Msg(string text, string cap, bool info)
        {
            MsgBox(text, cap, info, false, false);
        }

        bool MsgBox(string text, string cap, bool info, bool okNo, bool danger)
        {
            using (Confirm f = new Confirm(text, cap, info, okNo, danger))
                return f.ShowDialog() == DialogResult.OK;
        }

        void OnMouseDownGlobal(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool ReleaseCapture();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, IntPtr lp);

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(C.Bg);
            base.OnPaint(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            // 标题条区域拖动
            if (e.Button == MouseButtons.Left && e.Y < 40 && e.X > LeftW && !InClientButton(e))
            {
                ReleaseCapture();
                SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
            }
        }

        bool InClientButton(MouseEventArgs e)
        {
            if (winClose != null && winClose.Bounds.Contains(e.Location)) return true;
            if (btnClean != null && btnClean.Bounds.Contains(e.Location)) return true;
            return false;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left && e.Y < 40 && e.X > LeftW && !InClientButton(e))
            {
                WindowState = WindowState == FormWindowState.Maximized
                    ? FormWindowState.Normal : FormWindowState.Maximized;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (scanning || cleaning)
            {
                if (!Ask("还有任务在跑，确定要走？", "退出")) { e.Cancel = true; return; }
            }
            base.OnFormClosing(e);
        }
    }

    // =====================================================================
    //  小确认框（自绘，跟整体风格一致）
    // =====================================================================
    class Confirm : Form
    {
        public Confirm(string text, string cap, bool info, bool okNo, bool danger)
        {
            // 固定尺寸 + 绝对坐标。
            // 原来用 Dock + Anchor 排按钮，结果 Anchor 在 Parent 还没挂上时
            // 就把位置算错了 —— 按钮被挤到窗口边上裁掉一半，「取消」整个不见了。
            const int PW = 460, PH = 216;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(PW, PH);
            BackColor = C.Bg;
            Text = cap;
            TopMost = true;
            ShowInTaskbar = false;

            Label t = new Label
            {
                Text = text,
                Font = Fonts.Body,
                ForeColor = C.Fg,
                BackColor = C.Bg,
                Bounds = new Rectangle(24, 20, PW - 48, 124),
                AutoSize = false
            };

            Panel rule = new Panel
            { Bounds = new Rectangle(24, 152, PW - 48, 1), BackColor = C.Line };

            ActionButton no = new ActionButton(okNo ? "取消" : "知道了", false)
            { Bounds = new Rectangle(PW - 24 - 110, 164, 110, 32) };
            no.Click += delegate
            {
                DialogResult = okNo ? DialogResult.Cancel : DialogResult.OK;
                Close();
            };

            ActionButton yes = new ActionButton(okNo ? "确定" : "好", true)
            { Bounds = new Rectangle(PW - 24 - 110 - 12 - 110, 164, 110, 32) };
            yes.Visible = okNo;
            yes.Click += delegate { DialogResult = DialogResult.OK; Close(); };

            Controls.Add(t);
            Controls.Add(rule);
            Controls.Add(no);
            Controls.Add(yes);

            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
                if (e.KeyCode == Keys.Enter) { DialogResult = DialogResult.OK; Close(); }
            };
            Region = new Region(Draw.RR(0, 0, PW, PH, 12));
        }
    }

    // 窗口右上角的小按钮（无边框窗口只能自己画）
    class WinBtn : Control
    {
        public bool IsClose;
        int hover;

        public WinBtn(bool close)
        {
            IsClose = close;
            Size = new Size(44, 32);
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent == null ? C.Bg : Parent.BackColor);
            Color fg = hover == 1 ? (IsClose ? Color.White : C.Fg) : C.Fg3;
            int cx = Width / 2, cy = Height / 2;
            using (Pen p = new Pen(fg, 1.4f))
            {
                if (IsClose)
                {
                    p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                    g.DrawLine(p, cx - 5, cy - 5, cx + 5, cy + 5);
                    g.DrawLine(p, cx + 5, cy - 5, cx - 5, cy + 5);
                }
                else g.DrawLine(p, cx - 5, cy, cx + 5, cy);
            }
        }

        protected override void OnMouseEnter(EventArgs e) { hover = 1; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = 0; Invalidate(); base.OnMouseLeave(e); }
    }

    // =====================================================================
    //  入口
    // =====================================================================
    static class Program
    {
        static string LogPath
        {
            get
            {
                try { return Path.Combine(Env.LocalAppData, @"SpaceHunter\崩溃日志.txt"); }
                catch { return Path.Combine(Env.TempDir, "SpaceHunter_crash.txt"); }
            }
        }

        public static void Log(string where, Exception ex)
        {
            try
            {
                File.AppendAllText(LogPath,
                    "==== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + where + " ====\r\n" +
                    (ex == null ? "(no exception)" : ex.ToString()) + "\r\n\r\n",
                    new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>
        /// 单实例保护。
        ///
        /// 为什么要这个：用户（尤其是双击快捷方式手快两下、或者从不同位置
        /// 各启动一次）很容易跑出两个实例。两个实例会互相踩：
        ///   · 一个在编译/替换文件，另一个正占着它 -> exe 删不掉
        ///   · 两个都在扫描、都在等隔离区的锁
        ///   · 强制关掉其中一个，另一个还活着，exe 依然删不掉
        ///
        /// 症状是「关掉了进程但 exe 还在启动中、删不掉」，
        /// 而真正的原因往往就是有个你没注意到的第二实例。
        ///
        /// Mutex 是全局的（带程序名），所以从任何位置启动都认得出来。
        /// 注意：**提权子进程不占这个 Mutex** —— 它是给人调用的工具，
        /// 不该因为主窗口开着就被拒。
        /// </summary>
        static Mutex instanceMutex;

        static bool ClaimSingleInstance()
        {
            try
            {
                // Local\ 前缀：只在本机会话内互斥，不受其他用户/远程会话影响
                bool created;
                instanceMutex = new Mutex(true, @"Local\SpaceHunter_SingleInstance", out created);
                if (created) return true;

                // 已经有人在跑了。把他的窗口叫到前面来，然后自己退出。
                try
                {
                    foreach (Process p in Process.GetProcessesByName("SpaceHunter"))
                    {
                        try
                        {
                            if (p.Id == Process.GetCurrentProcess().Id) continue;
                            NativeMethods.ShowWindow(p.MainWindowHandle, 9);
                            NativeMethods.SetForegroundWindow(p.MainWindowHandle);
                            break;
                        }
                        catch { }
                    }
                }
                catch { }
                return false;
            }
            catch
            {
                // 拿不到 Mutex 也不该因此不启动 —— 宁可多开也别打不开
                return true;
            }
        }

        [STAThread]
        static void Main(string[] args)
        {
            // ============ UAC 提权子进程 ============
            //
            // 这里是整个程序最要命的一段：判断写错过一次，代价是
            // 提权实例开出**完整的主界面**并自动开始扫盘，
            // 而主窗口那边还在 WaitForExit(30 分钟)，
            // 用户必须手动把那个多余窗口关掉才能看到清理结果。
            //
            // Elevated.Run 传的是 **3 个**参数：
            //     --elevate  <任务文件>  <结果文件>
            // 早先这里写的是 args.Length == 2 —— 永远不等于 3，于是
            // 提权实例走了正常流程。这个判断必须：
            //   · 用 >= 3 而不是 == 3（以后再加参数也不会失效）
            //   · 不看顺序之外的东西
            //   · 匹配失败时**绝不能**回落到开主界面
            if (args.Length >= 3 &&
                string.Equals(args[0], "--elevate", StringComparison.OrdinalIgnoreCase))
            {
                // 注意：在 EnableVisualStyles 之前就直接干完活退出，
                // 不碰任何 UI，不创建窗口。
                int rc;
                try { rc = Elevated.RunAsElevatedChild(args[1], args[2]); }
                catch (Exception ex) { Program.Log("提权子进程", ex); rc = -1; }
                Environment.Exit(rc);
                return;
            }

            // 提权子进程干完就退，**不要**参与单实例互斥 ——
            // 它是被人调用的工具，主窗口开着是正常情况。
            if (!ClaimSingleInstance())
            {
                // 静默退出，不弹框。
                // 弹框的话会卡在「请点确定」上，用户不开第二个实例的话
                // 那个进程就一直挂着 —— 而它又占着 exe，
                // 「进程都关掉了但文件删不掉」就是这么来的。
                // ClaimSingleInstance 里已经把老窗口叫到前面了，这里直接走。
                Environment.Exit(0);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 调试开关：--noscan 跳过自动扫描，--drives=N 只显示前 N 个盘
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                int n;
                if (a == "--drives" && i + 1 < args.Length && int.TryParse(args[i + 1], out n))
                {
                    MainForm.DriveLimit = n;
                }
                else if (a.StartsWith("--drives=", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(a.Substring(9), out n))
                {
                    MainForm.DriveLimit = n;
                }
                else if (a.StartsWith("--only=", StringComparison.OrdinalIgnoreCase))
                {
                    // 只列出路径里含这段的东西。用来在真实数据上安全地验证清理链路
                    ScanEngine.ScanOnly = a.Substring(7);
                }
            }

            // 上次如果是被强行结束的，可能还留着一个管理员权限的提权子进程，
            // 它会一直占着 SpaceHunter.exe（用户看到的就是「进程都没了
            // 但 exe 还删不掉」）。先收掉它。
            try { Elevated.KillStaleChildren(); }
            catch (Exception ex) { Log("清残留提权进程", ex); }

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                Log("UI", e.Exception);
                try
                {
                    MessageBox.Show("出了个错：\n\n" + e.Exception.Message +
                        "\n\n详细信息记在：\n" + LogPath, "空间猎手");
                }
                catch { }
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Log("未捕获", e.ExceptionObject as Exception);
            };

            // 超过 30 天的隔离内容启动时顺手清掉，免得越攒越多
            try { PurgeStale(); } catch (Exception ex) { Log("清旧隔离", ex); }

            try
            {
                MainForm mf = new MainForm();

                // 第二道保险：只要命令行里出现过任何内部用途的开关
                // （提权子进程、沙箱调试），就绝不自动扫盘。
                // 宁可漏扫，也不能在用户没要求的情况下把几个盘都读一遍 ——
                // 上面的参数判断万一哪天又写错了，这里能兜住。
                bool internalUse = false;
                for (int i = 0; i < args.Length; i++)
                    if (args[i].StartsWith("--elevate", StringComparison.OrdinalIgnoreCase))
                        internalUse = true;

                if (!internalUse && Array.IndexOf(args, "--noscan") < 0)
                    mf.AutoScan = true;

                Application.Run(mf);
            }
            catch (Exception ex)
            {
                Log("启动", ex);
                try
                {
                    MessageBox.Show("启动失败：\n\n" + ex.Message + "\n\n日志：" + LogPath, "空间猎手");
                }
                catch { }
            }
        }

        static void PurgeStale()
        {
            if (!Quarantine.Exists) return;
            DateTime cut = DateTime.Now.AddDays(-30);
            string[] files;
            try { files = Directory.GetFiles(Quarantine.Root, "*", SearchOption.AllDirectories); }
            catch { return; }
            int n = 0;
            for (int i = 0; i < files.Length; i++)
                try { if (File.GetLastWriteTime(files[i]) < cut) { File.Delete(files[i]); n++; } }
                catch { }
            if (n > 0)
            {
                try
                {
                    foreach (string d in Directory.GetDirectories(Quarantine.Root, "*", SearchOption.AllDirectories))
                        try { Directory.Delete(d); } catch { }
                }
                catch { }
            }
        }
    }
}
