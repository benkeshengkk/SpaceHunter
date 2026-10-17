using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SpaceHunter
{
    // 复选框
    static class Ck
    {
        public static void Box(Graphics g, int x, int y, bool on)
        {
            using (GraphicsPath p = Draw.RR(x, y, 16, 16, 4))
            {
                if (on)
                {
                    using (SolidBrush b = new SolidBrush(C.Accent)) g.FillPath(b, p);
                    using (Pen pen = new Pen(C.Accent)) g.DrawPath(pen, p);
                }
                else
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(190, C.Bg.R, C.Bg.G, C.Bg.B))) g.FillPath(b, p);
                    using (Pen pen = new Pen(C.Fg3)) g.DrawPath(pen, p);
                }
            }
            if (!on) return;
            using (Pen pen = new Pen(Color.White, 2f))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                g.DrawLines(pen, new Point[] {
                    new Point(x + 4, y + 8),
                    new Point(x + 7, y + 11),
                    new Point(x + 12, y + 4) });
            }
        }
    }

    // =====================================================================
    //  垃圾清单
    // =====================================================================
    class FileGrid : ScrollList
    {
        const int RowH = 46;

        public List<Finding> Items = new List<Finding>();
        public HashSet<string> Hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public Action<string> OnExclude;
        public Action Changed;
        public string EmptyText = "这一类里没有找到东西";

        protected override int RowCount { get { return Items.Count; } }
        protected override int RowHeight { get { return RowH; } }
        protected override int HeadHeight { get { return 30; } }

        public void Set(List<Finding> items)
        {
            Items = items ?? new List<Finding>();
            if (SelRow >= Items.Count) SelRow = Items.Count - 1;
            SetScroll(0);
            Invalidate();
        }

        public long SelectedSize
        {
            get
            {
                long s = 0;
                for (int i = 0; i < Items.Count; i++) if (Items[i].On) s += Items[i].Size;
                return s;
            }
        }

        public int SelectedCount
        {
            get { int n = 0; for (int i = 0; i < Items.Count; i++) if (Items[i].On) n++; return n; }
        }

        void Header(Graphics g)
        {
            g.FillRectangle(Br.Card2, 0, 0, ContentW, HeadHeight);
            g.DrawLine(Br.PenLine, 0, HeadHeight - 1, ContentW, HeadHeight - 1);

            int n = SelectedCount;
            bool all = Items.Count > 0 && n == Items.Count;
            Ck.Box(g, 12, 7, all);
            TextRenderer.DrawText(g, n > 0 ? n + " / " + Items.Count : "全部", Fonts.SmallBold,
                new Rectangle(34, 0, 120, HeadHeight), all ? C.Fg : C.Fg2,
                TextFormatFlags.VerticalCenter);

            TextRenderer.DrawText(g, "文件", Fonts.SmallBold,
                new Rectangle(ContentW - 210, 0, 130, HeadHeight), C.Fg3,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            TextRenderer.DrawText(g, "大小", Fonts.SmallBold,
                new Rectangle(ContentW - 76, 0, 62, HeadHeight), C.Fg3,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        }

        protected override void PaintBody(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Header(g);

            if (Items.Count == 0)
            {
                TextRenderer.DrawText(g, EmptyText, Fonts.Body, new Rectangle(0, 0, ContentW, Height),
                    C.Fg3, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            int first = Scroll / RowH;
            int last = Math.Min(Items.Count - 1, (Scroll + ViewH) / RowH);

            for (int r = first; r <= last; r++)
            {
                Finding f = Items[r];
                int top = HeadHeight + r * RowH - Scroll;
                int w = ContentW;

                if (r % 2 == 1) g.FillRectangle(Br.RowSep, 0, top, w, RowH);
                if (SelVisible && r == SelRow)
                {
                    g.FillRectangle(Br.SelBg, 0, top, w, RowH);
                    g.FillRectangle(Br.Accent, 0, top, 3, RowH);
                }
                g.DrawLine(Br.PenRowSep, 12, top + RowH - 1, w - 12, top + RowH - 1);

                // 搬不动的项标一条红线（它们走直接删除）
                if (f.Blocked) g.FillRectangle(Br.Soft(C.Danger, 150), 0, top, 2, RowH);

                Ck.Box(g, 12, top + 15, f.On);

                // 分类色点
                using (SolidBrush cb = new SolidBrush(CatMeta.Col(f.Cat)))
                    g.FillEllipse(cb, 36, top + 15, 6, 6);

                int tx = 50;
                int nameW = w - tx - 130;
                if (nameW < 60) nameW = 60;
                Draw.Ellipsize(g, f.DisplayName, Fonts.RowName, Br.Fg, tx, top + 7, nameW);
                Draw.Ellipsize(g, f.DisplayReason, Fonts.RowReason,
                    f.Blocked ? Br.Soft(C.Danger, 220) : Br.Fg2, tx, top + 24, nameW - 90);

                TextRenderer.DrawText(g, Fmt.Size(f.Size), Fonts.SizeSm,
                    new Rectangle(w - 76, top, 62, RowH), f.Blocked ? C.Fg2 : C.Fg,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            ClearKeySel();
            int r = RowAt(e.X, e.Y);
            if (r >= 0 && Items.Count > 0)
            {
                SelRow = r;
                if (e.Button == MouseButtons.Left) { Items[r].On = !Items[r].On; if (Changed != null) Changed(); }
            }
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Right) return;
            int r = RowAt(e.X, e.Y);
            if (r >= 0 && OnExclude != null && !NeedBar()) OnExclude(Items[r].Path);
            else if (r >= 0 && OnExclude != null) OnExclude(Items[r].Path);
        }

        public void ToggleAll()
        {
            bool all = SelectedCount == Items.Count && Items.Count > 0;
            for (int i = 0; i < Items.Count; i++) Items[i].On = !all;
            if (Changed != null) Changed();
            Invalidate();
        }
    }

    // =====================================================================
    //  空间地图：每个大目录一行 + 占比条
    // =====================================================================
    class BarList : ScrollList
    {
        const int RowH = 40;
        List<DirSize> items = new List<DirSize>();
        public bool Loading;
        public string Note = "";
        public Action<string> OpenFolder;
        long biggest = 1;

        protected override int RowCount { get { return items.Count; } }
        protected override int RowHeight { get { return RowH; } }
        protected override int HeadHeight { get { return 30; } }

        /// <summary>
        /// 先把目录名列出来（瞬间），大小先留空。
        ///
        /// 早先是一口气把所有目录的体积算完才显示，C 盘能卡住一分钟以上 ——
        /// 用户看到的就是「点一下就又扫盘」。分两步之后，点开地图立刻能看到目录名，
        /// 大小在后台一个一个补上。
        ///
        /// 注意：这里**不要**设 Loading。Loading 的含义是「此刻正在算体积」，
        /// 而这个方法只是清空列表、摆一句提示语。之前它顺手把 Loading 设成 true，
        /// 于是「还没统计过，点下面的按钮统计」这句提示在表头上显示成了
        /// 「统计中」，而且永远不会再变回去 —— 用户看到的「一直显示统计中」就是它。
        /// </summary>
        public void StartList(string note)
        {
            items = new List<DirSize>();
            Loading = false;
            Note = note ?? "";
            SetScroll(0);
            Invalidate();
        }

        /// <summary>进入「正在算体积」状态。要跟 StartList 配对使用。</summary>
        public void BeginStat()
        {
            Loading = true;
            Invalidate();
        }

        public void SetDir(int index, string path)
        {
            while (items.Count <= index) items.Add(new DirSize(path, 0));
            items[index] = new DirSize(path, 0);
            Invalidate();
        }

        /// <summary>一次性把全部目录名列出来（一次重绘，不闪）</summary>
        public void SetDirs(string[] paths)
        {
            items = new List<DirSize>();
            if (paths != null)
                for (int i = 0; i < paths.Length; i++) items.Add(new DirSize(paths[i], 0));
            Loading = true;      // 接下来要逐个算体积了
            Invalidate();
        }

        /// <summary>强制回到「不在统计」状态。</summary>
        /// 除了正常收尾的 FinishStat，中断/异常时也要调它 ——
        /// 否则 Loading 永远为 true，界面就一直写着「统计中」。</summary>
        public void EndStat()
        {
            Loading = false;
            Invalidate();
        }

        /// <summary>填第 index 行的体积。填完后由调用方决定要不要重新排序</summary>
        public void SetSize(int index, long size)
        {
            if (index < 0 || index >= items.Count) return;
            string p = items[index].Path;
            items[index] = new DirSize(p, size);
            if (size > biggest) biggest = size;
            Invalidate();
        }

        /// <summary>统计结束：按体积从大到小排一遍</summary>
        public void FinishStat()
        {
            Loading = false;
            biggest = 1;
            for (int i = 0; i < items.Count; i++) if (items[i].Size > biggest) biggest = items[i].Size;
            items.Sort(delegate(DirSize a, DirSize b) { return b.Size.CompareTo(a.Size); });
            if (items.Count > 400) items.RemoveRange(400, items.Count - 400);
            SetScroll(0);
            Invalidate();
        }

        public void Clear(string note)
        {
            items = new List<DirSize>();
            Note = note; Loading = true; SetScroll(0); Invalidate();
        }

        /// <summary>给缓存用的一份拷贝</summary>
        public List<DirSize> ItemsSnapshot
        {
            get { return new List<DirSize>(items); }
        }

        public void BeginLoad() { Loading = true; Note = "正在统计…"; Invalidate(); }

        public void Finish(List<DirSize> found)
        {
            items = found ?? new List<DirSize>();
            biggest = 1;
            for (int i = 0; i < items.Count; i++) if (items[i].Size > biggest) biggest = items[i].Size;
            items.Sort(delegate(DirSize a, DirSize b) { return b.Size.CompareTo(a.Size); });
            if (items.Count > 400) items.RemoveRange(400, items.Count - 400);
            Loading = false; Note = ""; SetScroll(0); Invalidate();
        }

        protected override void PaintBody(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            g.FillRectangle(Br.Card2, 0, 0, ContentW, HeadHeight);
            g.DrawLine(Br.PenLine, 0, HeadHeight - 1, ContentW, HeadHeight - 1);
            TextRenderer.DrawText(g, Loading ? "统计中" : Note.Length > 0 ? Note : "目录", Fonts.SmallBold,
                new Rectangle(12, 0, 320, HeadHeight), C.Fg3, TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, "占用", Fonts.SmallBold,
                new Rectangle(ContentW - 76, 0, 62, HeadHeight), C.Fg3,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right);

            if (items.Count == 0)
            {
                // 空态要说人话：正在统计 / 有提示语就用提示语 / 都没有才叫「没有目录」
                string emptyText = Loading ? "正在逐层统计…"
                                : Note.Length > 0 ? Note
                                : "这个盘没有可统计的目录";
                TextRenderer.DrawText(g, emptyText, Fonts.Body,
                    new Rectangle(0, HeadHeight, ContentW, Height - HeadHeight),
                    C.Fg3, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            int nameW = 150;
            int sizeW = 74;
            int barX = nameW + 8;
            int barW = ContentW - barX - sizeW - 16;
            if (barW < 40) barW = 40;

            int first = Scroll / RowH;
            int last = Math.Min(items.Count - 1, (Scroll + ViewH) / RowH);

            for (int r = first; r <= last; r++)
            {
                DirSize d = items[r];
                int top = HeadHeight + r * RowH - Scroll;
                int w = ContentW;

                if (r % 2 == 1) g.FillRectangle(Br.RowSep, 0, top, w, RowH);
                if (SelVisible && r == SelRow)
                {
                    g.FillRectangle(Br.SelBg, 0, top, w, RowH);
                    g.FillRectangle(Br.Accent, 0, top, 3, RowH);
                }
                g.DrawLine(Br.PenRowSep, 12, top + RowH - 1, w - 12, top + RowH - 1);

                TextRenderer.DrawText(g, d.Name, Fonts.RowName, new Rectangle(12, top, nameW - 16, RowH),
                    C.Fg, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

                double rel = (double)d.Size / biggest;
                int bh = 8;
                int by = top + (RowH - bh) / 2;
                using (GraphicsPath tr = Draw.RR(barX, by, barW, bh, bh / 2f))
                using (SolidBrush tb = new SolidBrush(C.FromHex("#232838")))
                    g.FillPath(tb, tr);

                int fw = (int)(barW * Math.Max(0.004, Math.Min(1, rel)));
                Color col = rel > 0.7 ? C.Danger : rel > 0.4 ? C.Accent : C.Cyan;
                using (GraphicsPath fr = Draw.RR(barX, by, Math.Max(3, fw), bh, bh / 2f))
                using (LinearGradientBrush lg = new LinearGradientBrush(
                    new Rectangle(barX, by, Math.Max(3, fw), bh),
                    Color.FromArgb(col.R, col.G, col.B), Color.FromArgb(255, col.R, col.G, col.B), 0f))
                    g.FillPath(lg, fr);

                TextRenderer.DrawText(g, Fmt.Size(d.Size), Fonts.SizeSm,
                    new Rectangle(w - sizeW - 14, top, sizeW, RowH), C.Fg2,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            ClearKeySel();
            int r = RowAt(e.X, e.Y);
            if (r >= 0) { SelRow = r; Invalidate(); }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            int r = RowAt(e.X, e.Y);
            if (r >= 0 && OpenFolder != null) OpenFolder(items[r].Path);
        }
    }

    // =====================================================================
    //  分类清单（左边那列，第 0 行是「全部」）
    // =====================================================================
    class CatList : ScrollList
    {
        const int RowH = 46;
        public HashSet<Cat> On = new HashSet<Cat>();
        Dictionary<Cat, long> sizes = new Dictionary<Cat, long>();
        Dictionary<Cat, int> counts = new Dictionary<Cat, int>();
        public Action<Cat, bool> Changed;
        public bool HasScan { get { return sizes.Count > 0; } }

        protected override int RowCount { get { return HasScan ? CatMeta.All.Length + 1 : 1; } }
        protected override int RowHeight { get { return RowH; } }
        protected override int HeadHeight { get { return 0; } }

        public void Load(Dictionary<Cat, long> sz, Dictionary<Cat, int> ct, HashSet<Cat> on)
        {
            sizes = sz ?? new Dictionary<Cat, long>();
            counts = ct ?? new Dictionary<Cat, int>();
            On = on ?? new HashSet<Cat>();
            if (SelRow >= RowCount) SelRow = RowCount - 1;
            Invalidate();
        }

        static bool IsAll(int r) { return r == 0; }

        protected override void PaintBody(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int w = ContentW;

            long allSize = 0;
            int allCnt = 0;
            foreach (KeyValuePair<Cat, long> kv in sizes) { allSize += kv.Value; allCnt += counts.ContainsKey(kv.Key) ? counts[kv.Key] : 0; }

            int n = RowCount;
            int first = Math.Max(0, Scroll / RowH);
            int last = Math.Min(n - 1, (Scroll + ViewH) / RowH);

            for (int r = first; r <= last; r++)
            {
                int top = r * RowH - Scroll;
                bool all = IsAll(r);
                Cat c = all ? Cat.Cache : CatMeta.All[r - 1];

                bool on = all ? (On.Count > 0) : On.Contains(c);
                bool has = all ? HasScan : sizes.ContainsKey(c) && counts.ContainsKey(c) && counts[c] > 0;

                using (GraphicsPath p = Draw.RR(6, top + 4, w - 12, RowH - 8, 8))
                {
                    if (on && has)
                    {
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(34, CatMeta.Col(c).R, CatMeta.Col(c).G, CatMeta.Col(c).B)))
                            g.FillPath(b, p);
                        Color cc = CatMeta.Col(c);
                        using (Pen pen = new Pen(Color.FromArgb(150, cc.R, cc.G, cc.B), 1f))
                            g.DrawPath(pen, p);
                    }
                    else if (SelVisible && r == SelRow)
                    {
                        using (SolidBrush b = new SolidBrush(C.Hover)) g.FillPath(b, p);
                    }
                }

                Color col = has ? CatMeta.Col(c) : C.Fg3;
                using (SolidBrush cb = new SolidBrush(col))
                    g.FillEllipse(cb, 16, top + 14, 8, 8);

                string name = all ? "全部" : CatMeta.Name(c);
                TextRenderer.DrawText(g, name, Fonts.BodyBold,
                    new Rectangle(32, top + 3, w - 140, 20), has ? C.Fg : C.Fg2,
                    TextFormatFlags.VerticalCenter);

                long sz = all ? allSize : (sizes.ContainsKey(c) ? sizes[c] : 0);
                int ct = all ? allCnt : (counts.ContainsKey(c) ? counts[c] : 0);
                TextRenderer.DrawText(g, has ? ct + " 项　" + Fmt.Size(sz) : "—", Fonts.Small,
                    new Rectangle(32, top + 22, w - 90, 18), has ? C.Fg2 : C.Fg3,
                    TextFormatFlags.VerticalCenter);

                if (!all)
                    Ck.Box(g, w - 32, top + (RowH - 16) / 2, on && has);
            }

            if (!HasScan)
                TextRenderer.DrawText(g, "先扫描一下", Fonts.Small, new Rectangle(0, 0, w, Height),
                    C.Fg3, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            ClearKeySel();
            int r = RowAt(e.X, e.Y);
            if (r < 0) return;
            SelRow = r; Invalidate();
            if (!HasScan) return;
            if (e.Button != MouseButtons.Left) return;

            if (IsAll(r))
            {
                On.Clear();
                foreach (Cat x in CatMeta.All) if (CatMeta.DefaultOn(x)) On.Add(x);
            }
            else
            {
                Cat c = CatMeta.All[r - 1];
                if (On.Contains(c)) On.Remove(c); else On.Add(c);
                if (On.Count == 0) { foreach (Cat x in CatMeta.All) if (CatMeta.DefaultOn(x)) On.Add(x); }
            }
            if (Changed != null) Changed(cFor(r), true);
            Invalidate();
        }

        static Cat cFor(int r) { return r == 0 ? Cat.Cache : CatMeta.All[r - 1]; }

        public void SelectAllRows()
        {
            On.Clear();
            foreach (Cat x in CatMeta.All) if (CatMeta.DefaultOn(x)) On.Add(x);
            Invalidate();
        }
    }

    // =====================================================================
    //  软件列表
    // =====================================================================
    class AppList : ScrollList
    {
        const int RowH = 38;
        public List<AppEntry> Items = new List<AppEntry>();
        public HashSet<string> Picked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public Action Changed;
        public string EmptyText = "还没有扫描。点上面的「扫描已安装软件」";

        protected override int RowCount { get { return Items.Count; } }
        protected override int RowHeight { get { return RowH; } }
        protected override int HeadHeight { get { return 30; } }

        public void Set(List<AppEntry> list)
        {
            Items = list ?? new List<AppEntry>();
            Picked.Clear();
            if (SelRow >= Items.Count) SelRow = Items.Count - 1;
            SetScroll(0); Invalidate();
        }

        public int PickedCount { get { return Picked.Count; } }

        protected override void PaintBody(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int w = ContentW;

            g.FillRectangle(Br.Card2, 0, 0, w, HeadHeight);
            g.DrawLine(Br.PenLine, 0, HeadHeight - 1, w, HeadHeight - 1);

            bool all = Items.Count > 0 && Picked.Count == Items.Count;
            Ck.Box(g, 12, 7, all);
            TextRenderer.DrawText(g, Items.Count > 0 ? Picked.Count + " / " + Items.Count : "未扫描", Fonts.SmallBold,
                new Rectangle(34, 0, 160, HeadHeight), all ? C.Fg : C.Fg2, TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, "大小", Fonts.SmallBold,
                new Rectangle(w - 168, 0, 66, HeadHeight), C.Fg3,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            TextRenderer.DrawText(g, "来源", Fonts.SmallBold,
                new Rectangle(w - 92, 0, 78, HeadHeight), C.Fg3,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right);

            if (Items.Count == 0)
            {
                TextRenderer.DrawText(g, EmptyText, Fonts.Body, new Rectangle(0, HeadHeight, w, Height - HeadHeight),
                    C.Fg3, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            int first = Scroll / RowH;
            int last = Math.Min(Items.Count - 1, (Scroll + ViewH) / RowH);

            for (int r = first; r <= last; r++)
            {
                AppEntry a = Items[r];
                int top = HeadHeight + r * RowH - Scroll;
                bool on = Picked.Contains(a.Key);

                if (r % 2 == 1) g.FillRectangle(Br.RowSep, 0, top, w, RowH);
                if (SelVisible && r == SelRow)
                {
                    g.FillRectangle(Br.SelBg, 0, top, w, RowH);
                    g.FillRectangle(Br.Accent, 0, top, 3, RowH);
                }
                g.DrawLine(Br.PenRowSep, 12, top + RowH - 1, w - 12, top + RowH - 1);

                Ck.Box(g, 12, top + 11, on);

                int nameW = w - 50 - 180;
                if (nameW < 60) nameW = 60;
                Draw.Ellipsize(g, a.Name, Fonts.RowName, Br.Fg, 36, top + 5, nameW);
                string sub = string.IsNullOrEmpty(a.Publisher) ? a.InstallDir : a.Publisher + "　·　" + a.InstallDir;
                Draw.Ellipsize(g, Fmt.OneLine(sub, 200), Fonts.RowReason, Br.Fg2, 36, top + 21, nameW - 20);

                TextRenderer.DrawText(g, a.Size > 0 ? Fmt.Size(a.Size) : "—", Fonts.SizeSm,
                    new Rectangle(w - 168, top, 66, RowH), C.Fg2,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right);

                // 来源标签：有卸载命令 / 只在磁盘上找到 / 来自快捷方式
                Color sc = a.CanUninstall ? C.Ok : a.FromShortcut ? C.Warn : C.Cyan;
                string st = a.CanUninstall ? "可卸载" : a.FromShortcut ? "快捷方式" : "仅目录";
                TextRenderer.DrawText(g, st, Fonts.Small,
                    new Rectangle(w - 92, top, 78, RowH), sc,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            ClearKeySel();
            int r = RowAt(e.X, e.Y);
            if (r < 0 || r >= Items.Count) return;
            SelRow = r;
            if (e.Button != MouseButtons.Left) return;
            if (Picked.Contains(Items[r].Key)) Picked.Remove(Items[r].Key);
            else Picked.Add(Items[r].Key);
            if (Changed != null) Changed();
            Invalidate();
        }

        public void ToggleAll()
        {
            if (Items.Count == 0) return;
            if (Picked.Count == Items.Count) Picked.Clear();
            else { Picked.Clear(); for (int i = 0; i < Items.Count; i++) Picked.Add(Items[i].Key); }
            if (Changed != null) Changed();
            Invalidate();
        }

        public List<AppEntry> PickedItems
        {
            get
            {
                List<AppEntry> l = new List<AppEntry>();
                for (int i = 0; i < Items.Count; i++) if (Picked.Contains(Items[i].Key)) l.Add(Items[i]);
                return l;
            }
        }
    }

    // =====================================================================
    //  卸载残留勾选
    // =====================================================================
    class PickList : ScrollList
    {
        const int RowH = 32;
        public Action Changed;

        public class Item
        {
            public string Path;
            public long Size;
            public bool On;
            public bool Default;
            public bool IsDir;
            public string Name;
        }

        public List<Item> Items = new List<Item>();

        protected override int RowCount { get { return Items.Count; } }
        protected override int RowHeight { get { return RowH; } }
        protected override int HeadHeight { get { return 28; } }

        public long PickedSize
        {
            get { long s = 0; for (int i = 0; i < Items.Count; i++) if (Items[i].On) s += Items[i].Size; return s; }
        }

        public List<Item> PickedItems
        {
            get { List<Item> l = new List<Item>(); for (int i = 0; i < Items.Count; i++) if (Items[i].On) l.Add(Items[i]); return l; }
        }

        protected override void PaintBody(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int w = ContentW;

            g.FillRectangle(Br.Card2, 0, 0, w, HeadHeight);
            g.DrawLine(Br.PenLine, 0, HeadHeight - 1, w, HeadHeight - 1);
            int n = 0;
            for (int i = 0; i < Items.Count; i++) if (Items[i].On) n++;
            Ck.Box(g, 12, 6, Items.Count > 0 && n == Items.Count);
            TextRenderer.DrawText(g, n > 0 ? n + " / " + Items.Count + " 项　" + Fmt.Size(PickedSize) : "共 " + Items.Count + " 项",
                Fonts.SmallBold, new Rectangle(34, 0, 260, HeadHeight), n > 0 ? C.Fg : C.Fg2,
                TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, "大小", Fonts.SmallBold,
                new Rectangle(w - 76, 0, 62, HeadHeight), C.Fg3,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right);

            if (Items.Count == 0)
            {
                TextRenderer.DrawText(g, "没有发现残留", Fonts.Body, new Rectangle(0, HeadHeight, w, Height - HeadHeight),
                    C.Fg3, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            int first = Scroll / RowH;
            int last = Math.Min(Items.Count - 1, (Scroll + ViewH) / RowH);

            for (int r = first; r <= last; r++)
            {
                Item it = Items[r];
                int top = HeadHeight + r * RowH - Scroll;

                if (r % 2 == 1) g.FillRectangle(Br.RowSep, 0, top, w, RowH);
                if (SelVisible && r == SelRow)
                {
                    g.FillRectangle(Br.SelBg, 0, top, w, RowH);
                    g.FillRectangle(Br.Accent, 0, top, 3, RowH);
                }
                g.DrawLine(Br.PenRowSep, 12, top + RowH - 1, w - 12, top + RowH - 1);

                Ck.Box(g, 12, top + 8, it.On);
                int tx = 36;
                int nw = w - tx - 90;
                if (nw < 60) nw = 60;
                Draw.Ellipsize(g, it.Name + (it.IsDir ? "  \\" : ""), Fonts.RowName,
                    it.Default ? Br.Fg : Br.Fg2, tx, top + 5, nw);
                TextRenderer.DrawText(g, Fmt.Size(it.Size), Fonts.SizeSm,
                    new Rectangle(w - 76, top, 62, RowH), C.Fg2,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            ClearKeySel();
            int r = RowAt(e.X, e.Y);
            if (r < 0 || r >= Items.Count) return;
            SelRow = r;
            if (e.Button != MouseButtons.Left) return;
            Items[r].On = !Items[r].On;
            if (Changed != null) Changed();
            Invalidate();
        }

        public void ToggleAll()
        {
            if (Items.Count == 0) return;
            bool all = true;
            for (int i = 0; i < Items.Count; i++) if (Items[i].On) { all = false; break; }
            for (int i = 0; i < Items.Count; i++) Items[i].On = !all;
            if (Changed != null) Changed();
            Invalidate();
        }
    }
}