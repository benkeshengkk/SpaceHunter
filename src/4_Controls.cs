using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace SpaceHunter
{
    // =====================================================================
    //  所有可滚动列表的基类
    //
    //  目标：滚轮坏掉 / 没有滚轮的人，也能把列表用完。
    //  做法：右侧永远画一条真滚动条 —— 上下箭头可点、轨道可点页、
    //        滑块可拖；同时支持 ↑ ↓ PgUp PgDn Home End。
    //  子类只需要实现 RowCount / RowHeight / PaintBody。
    // =====================================================================
    abstract class ScrollList : Control
    {
        protected abstract int RowCount { get; }
        protected abstract int RowHeight { get; }
        protected virtual int HeadHeight { get { return 0; } }

        protected int ViewH { get { return Math.Max(1, Height - HeadHeight); } }

        protected int VisibleRows
        { get { return Math.Max(1, (ViewH + RowHeight - 1) / RowHeight); } }

        protected int ContentW { get { return Math.Max(20, Width - (NeedBar() ? BarW : 0)); } }

        protected bool NeedBar()
        {
            return RowCount > 0 && RowCount * RowHeight > ViewH;
        }

        protected int MaxScroll
        {
            get
            {
                int m = RowCount * RowHeight - ViewH;
                return m > 0 ? m : 0;
            }
        }

        int scroll = 0;
        protected int Scroll { get { return scroll; } }

        protected void SetScroll(int v)
        {
            int m = MaxScroll;
            if (v > m) v = m;
            if (v < 0) v = 0;
            if (v == scroll) return;
            scroll = v;
            Invalidate();
        }

        /// <summary>
        /// 行号（0 基）。传入控件坐标，返回 -1 表示不在有效行上。
        /// 关键：这里用「行的实际像素范围」判断，而不是简单除法 ——
        /// 否则点在半行上会选错行，且底部半行永远选不到。
        /// </summary>
        protected int RowAt(int x, int y)
        {
            if (x < 0 || x >= ContentW) return -1;
            if (y < HeadHeight || y >= Height) return -1;
            int r = (y - HeadHeight + scroll) / RowHeight;
            if (r < 0 || r >= RowCount) return -1;
            int top = HeadHeight + r * RowHeight - scroll;
            if (y > top + RowHeight) return -1;
            return r;
        }

        // ---------------- 滚动条几何 ----------------
        // 宽度和箭头高度都算得比常规滚动条大一号：这个程序有一批人是
        // 鼠标滚轮坏了、手指也不太灵活，命中区域宁可大也别夹缝里。
        const int BarW = 16;
        const int ArrowH = 22;

        protected int BarLeft { get { return Width - BarW; } }

        /// <summary>滑块真正能活动的区间（已经扣掉上下两个箭头按钮）</summary>
        int TrackTop { get { return HeadHeight + ArrowH; } }
        int TrackH { get { return ViewH - ArrowH * 2; } }

        int ThumbH()
        {
            int ch = RowCount * RowHeight;
            if (ch <= TrackH || TrackH <= 0) return TrackH > 0 ? TrackH : 1;
            int th = (int)((double)TrackH * TrackH / ch);
            if (th < 34) th = 34;
            if (th > TrackH) th = TrackH;
            return th;
        }

        int ThumbY()
        {
            int m = MaxScroll;
            int th = ThumbH();
            if (m <= 0 || TrackH <= th) return TrackTop;
            return TrackTop + (int)((double)(TrackH - th) * scroll / m);
        }

        // ---------------- 绘制 ----------------
        protected abstract void PaintBody(PaintEventArgs e);

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            try
            {
                g.Clear(BackColor);
                PaintBody(e);
                DrawBar(g);
            }
catch (Exception ex)
            {
                try { Program.Log(this.GetType().Name + ".PaintBody", ex); } catch { }
                try
                {
                    g.Clear(C.Bg);
                    g.DrawString("列表渲染出错：" + ex.Message, Fonts.Body, Br.Fg3, 12, 12);
                }
                catch { }
            }
        }

        void DrawBar(Graphics g)
        {
            if (!NeedBar()) return;
            int x = BarLeft;
            int top = HeadHeight, h = ViewH;

            g.FillRectangle(Br.TrackBar, x, top, BarW, h);

            // 上下两端的按钮区（点一下翻一页）
            g.FillRectangle(Br.TrackBar, x, top, BarW, ArrowH);
            g.FillRectangle(Br.TrackBar, x, top + h - ArrowH, BarW, ArrowH);
            g.DrawLine(Br.PenLine, x + 2, top + ArrowH - 1, x + BarW - 3, top + ArrowH - 1);
            g.DrawLine(Br.PenLine, x + 2, top + h - ArrowH, x + BarW - 3, top + h - ArrowH);

            // 箭头
            int cx = x + BarW / 2;
            using (SolidBrush a = new SolidBrush(C.Fg2))
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddPolygon(new Point[] {
                    new Point(cx - 5, top + ArrowH - 8),
                    new Point(cx + 5, top + ArrowH - 8),
                    new Point(cx, top + 7) });
                g.FillPath(a, p);

                p.Reset();
                p.AddPolygon(new Point[] {
                    new Point(cx - 5, top + h - ArrowH + 7),
                    new Point(cx + 5, top + h - ArrowH + 7),
                    new Point(cx, top + h - 6) });
                g.FillPath(a, p);
            }

            // 滑块
            int ty = ThumbY(), th = ThumbH();
            Color thumbCol = dragging
                ? C.FromHex("#A8B0C4")
                : (Focused && keyboardUsed ? C.FromHex("#8A93AB") : C.FromHex("#4C5468"));
            using (GraphicsPath rp = Draw.RR(x + 2, ty, BarW - 4, th, (BarW - 4) / 2f))
            using (SolidBrush sb = new SolidBrush(thumbCol))
                g.FillPath(sb, rp);

            if (Focused && keyboardUsed)
                using (GraphicsPath rp = Draw.RR(x + 2, ty, BarW - 4, th, (BarW - 4) / 2f))
                using (Pen pen = new Pen(Br.Soft(C.Accent, 190), 1f))
                    g.DrawPath(pen, rp);

            using (SolidBrush hl = new SolidBrush(Color.FromArgb(70, 255, 255, 255)))
                g.FillRectangle(hl, x + 4, ty + 2, BarW - 8, 2);
        }
        bool dragging = false;
        bool keyboardUsed = false;

        // ---------------- 鼠标 ----------------
        enum Mode { None, Thumb, PageUp, PageDown }
        Mode mode = Mode.None;
        int dragOffset;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (!NeedBar()) return;

            int x = BarLeft, top = HeadHeight, h = ViewH;
            if (e.X < x) return;

            // 顺序很重要：先箭头，再滑块，最后轨道。
            if (e.Y < top + ArrowH) { SetScroll(scroll - ViewH); ClearKeySel(); return; }
            if (e.Y >= top + h - ArrowH) { SetScroll(scroll + ViewH); ClearKeySel(); return; }

            int ty = ThumbY(), th = ThumbH();
            if (e.Y >= ty && e.Y <= ty + th)
            {
                mode = Mode.Thumb;
                dragging = true;
                dragOffset = e.Y - ty;
                keyboardUsed = false;
                Invalidate();
                return;
            }
            // 轨道空白处：点在滑块上方就往上翻一整页，下方就往下翻
            mode = Mode.None;
            if (e.Y < ty) SetScroll(scroll - ViewH);
            else SetScroll(scroll + ViewH);
            ClearKeySel();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (mode != Mode.Thumb) return;
            int th = ThumbH();
            int room = TrackH - th;
            if (room <= 0) return;
            double rel = (double)(e.Y - TrackTop - dragOffset) / room;
            if (rel < 0) rel = 0;
            if (rel > 1) rel = 1;
            SetScroll((int)(rel * MaxScroll));
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (mode != Mode.None || dragging)
            {
                mode = Mode.None;
                dragging = false;
                Invalidate();
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (RowCount <= VisibleRows) return;
            int d = e.Delta > 0 ? -RowHeight * 2 : RowHeight * 2;
            SetScroll(scroll + d);
        }

        protected override bool IsInputKey(Keys k)
        {
            if (k == Keys.Up || k == Keys.Down || k == Keys.PageUp || k == Keys.PageDown ||
                k == Keys.Home || k == Keys.End) return true;
            return base.IsInputKey(k);
        }

        // ------------------------------------------------------------------
        //  键盘 = 移动选中行，然后自动把它滚进可视区。
        //  这样即使一行有 40px 高、列表有 500 行，也能确定自己停在哪。
        // ------------------------------------------------------------------
        int sel = 0;

        protected int SelRow
        {
            get { return RowCount <= 0 ? -1 : Math.Max(0, Math.Min(RowCount - 1, sel)); }
            set { sel = value; }
        }

        /// <summary>键盘操作过才画高亮，避免鼠标用户看到莫名的选中框</summary>
        protected bool SelVisible
        {
            get { return keyboardUsed && Focused && RowCount > 0; }
        }

        /// <summary>把某一行滚到可视区内（不改变 scroll 之外的东西）</summary>
        protected bool EnsureVisible(int r)
        {
            if (r < 0 || r >= RowCount) return false;
            int top = r * RowHeight;
            int bot = top + RowHeight;
            int v = scroll, nv = v;
            if (top < v) nv = top;
            else if (bot > v + ViewH) nv = bot - ViewH;
            if (nv != v) { SetScroll(nv); return true; }
            return false;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int page = Math.Max(1, VisibleRows - 1);
            bool used = true;
            int r = SelRow;
            switch (e.KeyCode)
            {
                case Keys.Up: r = (r < 0 ? 0 : r - 1); break;
                case Keys.Down: r = (r < 0 ? 0 : r + 1); break;
                case Keys.PageUp: r = (r < 0 ? 0 : r - page); break;
                case Keys.PageDown: r = (r < 0 ? 0 : r + page); break;
                case Keys.Home: r = 0; break;
                case Keys.End: r = RowCount - 1; break;
                default: used = false; break;
            }
            if (used)
            {
                keyboardUsed = true;
                SelRow = r;
                if (!EnsureVisible(SelRow) && !NeedBar()) Invalidate();
                else Invalidate();
                OnRowActivatedKey();
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        /// <summary>键盘移动选中行之后，子类可以在这里做额外的事（比如重新画）</summary>
        protected virtual void OnRowActivatedKey() { }

        /// <summary>鼠标点击时调用：关掉键盘高亮</summary>
        protected void ClearKeySel() { if (keyboardUsed) { keyboardUsed = false; Invalidate(); } }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            SetScroll(scroll);
        }

        public ScrollList()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            TabStop = true;
            BackColor = C.Card;
        }
    }

    // =====================================================================
    //  页签小标签
    // =====================================================================
    class TabChip : Control
    {
        // 必须是属性，不能是 public bool On 字段。
        // 字段改了不会触发重绘 —— 表现就是「点了新的标签，上一个还是橙色，
        // 鼠标移过去蹭一下才正常」。Caption 当初就写了 Invalidate，
        // 唯独这个漏了。
        bool on;
        public bool On
        {
            get { return on; }
            set { if (on != value) { on = value; Invalidate(); } }
        }

        string text;
        int hover;

        public string Caption
        {
            get { return text; }
            set { text = value; Invalidate(); }
        }

        public TabChip(string t) { text = t; Size = new Size(96, 32); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(C.Bg);
            bool hov = hover == 1;
            using (GraphicsPath p = Draw.RR(0, 1, Width, Height - 2, 8))
            {
                if (On)
                {
                    g.FillPath(Br.Soft(C.Accent, 235), p);
                }
                else if (hov)
                {
                    using (SolidBrush b = new SolidBrush(C.Card2)) g.FillPath(b, p);
                }
                using (Pen pen = new Pen(On ? C.Accent : hov ? C.Line : C.Fg3, 1f))
                    g.DrawPath(pen, p);
            }
            TextRenderer.DrawText(g, text, Fonts.BodyBold,
                new Rectangle(0, 0, Width, Height), On ? Color.White : hov ? C.Fg : C.Fg2,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = 1; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = 0; Invalidate(); base.OnMouseLeave(e); }
    }

    // =====================================================================
    //  按钮
    // =====================================================================
    class ActionButton : Control
    {
        public string Caption;
        public bool Primary;
        /// <summary>红色：给「清空隔离区」这种撤不回来的操作用</summary>
        public bool Danger = false;
        public bool EnabledLook = true;
        int hover;

        public ActionButton(string cap, bool primary)
        {
            Caption = cap; Primary = primary;
            Height = 36; Width = 132;
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent == null ? C.Bg : Parent.BackColor);

            Color baseCol = Primary ? C.Accent : Danger ? C.Danger : C.Card2;
            Color edge = Primary ? C.Accent : Danger ? C.Danger : C.Line;

            using (GraphicsPath p = Draw.RR(0, 0, Width - 1, Height - 1, 8))
            {
                if (!EnabledLook)
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(60, 130, 140, 160)))
                        g.FillPath(b, p);
                    using (Pen pen = new Pen(Color.FromArgb(80, 130, 140, 160), 1f)) g.DrawPath(pen, p);
                }
                else if (Primary || Danger)
                {
                    int a = hover == 1 ? 255 : 235;
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(a, baseCol.R, baseCol.G, baseCol.B)))
                        g.FillPath(b, p);
                }
                else
                {
                    using (SolidBrush b = new SolidBrush(hover == 1 ? C.Hover : C.Card2))
                        g.FillPath(b, p);
                    using (Pen pen = new Pen(hover == 1 ? C.Fg3 : edge, 1f)) g.DrawPath(pen, p);
                }
            }

            Color fg = !EnabledLook ? C.Fg3
                : (Primary || Danger) ? Color.White
                : hover == 1 ? C.Fg : C.Fg2;

            TextRenderer.DrawText(g, Caption, Fonts.BtnText, new Rectangle(0, 0, Width, Height),
                fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = 1; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = 0; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e)
        {
            EnabledLook = Enabled; Invalidate(); base.OnEnabledChanged(e);
        }
    }

    // =====================================================================
    //  磁盘环形图
    // =====================================================================
class RingChart : Control
    {
        public DriveX Drive;
        public bool Hover;
        public Action<DriveX> RingClicked;

        /// <summary>是不是当前正在看的那块盘</summary>
        public bool Selected;

        // 紧凑横向卡片：环在左、盘符容量在右。
        // 之前是竖排的大圆，三个盘就把左栏的分类列表挤没了。
        public RingChart(DriveX d) { Drive = d; Size = new Size(236, 100); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent == null ? C.Bg : Parent.BackColor);
            if (Drive == null) return;

            float cx = 52, cy = 50;
            float r = 38;
            bool warn = Drive.Ratio > 0.90;

            using (GraphicsPath card = Draw.RR(0, 0, Width - 1, Height - 1, 8))
            {
                if (Selected)
                {
                    // 当前正在看的盘：橙色描边，一眼能认出来
                    g.FillPath(Br.Soft(C.Accent, 26), card);
                    using (Pen pen = new Pen(C.Accent, 1.6f)) g.DrawPath(pen, card);
                }
                else
                {
                    if (Hover) { using (SolidBrush b = new SolidBrush(C.Card2)) g.FillPath(b, card); }
                    using (Pen pen = new Pen(Hover ? C.Line : C.FromHex("#20242F"), 1f)) g.DrawPath(pen, card);
                }
            }

            using (Pen track = new Pen(C.FromHex("#252B3C"), 10f))
                g.DrawEllipse(track, cx - r, cy - r, r * 2, r * 2);

            float ratio = (float)Drive.Ratio;
            if (ratio > 0.999f) ratio = 0.999f;
            if (ratio > 0.001f)
            {
                using (Pen arc = new Pen(Hover ? C.Accent : (warn ? C.Danger : C.Cyan), 10f))
                {
                    arc.StartCap = LineCap.Round;
                    arc.EndCap = LineCap.Round;
                    g.DrawArc(arc, cx - r, cy - r, r * 2, r * 2, -90, (float)(360 * ratio));
                }
            }

            TextRenderer.DrawText(g, ((int)(ratio * 100)) + "%", Fonts.SizeSm,
                new Rectangle(0, (int)cy - 11, (int)cx * 2, 22), warn ? C.Warn : C.Fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            int tx = (int)(cx + r) + 12;
            int tw = Width - tx - 10;
            string head = string.IsNullOrEmpty(Drive.Label) ? Drive.Root : Drive.Root + " " + Drive.Label;
            Draw.Ellipsize(g, head, Fonts.BodyBold, Hover ? Br.Fg : Br.Fg2, tx, 20, tw);
            TextRenderer.DrawText(g, Fmt.SizeShort(Drive.Total) + " 总量", Fonts.Small,
                new Rectangle(tx, 42, tw, 18), C.Fg3, TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, Fmt.SizeShort(Drive.Free) + " 可用", Fonts.SmallBold,
                new Rectangle(tx, 60, tw, 18), warn ? C.Warn : C.Cyan, TextFormatFlags.VerticalCenter);
        }

        protected override void OnMouseEnter(EventArgs e) { Hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (RingClicked != null && Drive != null) RingClicked(Drive);
            base.OnMouseClick(e);
        }
    }

    // =====================================================================
    //  扫描进度窗（自绘，避免白框）
    // =====================================================================
// =====================================================================
    //  扫描 / 清理进度窗（自绘，避免白框）
    // =====================================================================
    class ProgressForm : Form
    {
        const int PW = 520, PH = 186;

        Panel header, barWrap;
        Label cap, sub, pct;
        ActionButton cancelBtn;
        Action<ProgressForm> work;
        public Action<bool> OnCancelled = null;
        bool closing;
        bool cancelled;

        /// <summary>用户点过停止。干活的那边可以随时问这个，别再自己维护一份。</summary>
        public bool Cancelled { get { return cancelled; } }

        public bool Running { get; private set; }

        public ProgressForm(string title, string caption)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(PW, PH);
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            BackColor = C.Bg; ForeColor = C.Fg;
            Text = title;
            // 必须置顶：清理几十项要一阵子，用户切去干别的活是很正常的。
            // 不置顶的话进度窗会被别的窗口压住，只剩一个变灰的
            // 「清理所选」按钮 —— 看着就像程序卡死了。
            TopMost = true;

            // 固定大小窗，全用绝对坐标。
            // 原来用 Dock 排「父 Panel → 子 Panel → 按钮」三层，取消按钮被排到了
            // 容器外面，界面上什么都没有 —— 而且完全不报错，很难查。
            header = new Panel { Bounds = new Rectangle(0, 0, PW, 4), BackColor = C.Accent };

            cap = MkLabel(caption, Fonts.SectionTitle, C.Fg, 24, 16, PW - 48, 28);
            sub = MkLabel("", Fonts.Small, C.Fg2, 24, 44, PW - 48, 20);

            barWrap = new Panel { Bounds = new Rectangle(24, 74, PW - 48, 10), BackColor = C.FromHex("#1D2230") };
            barWrap.Paint += PaintBar;

            pct = MkLabel("", Fonts.SmallBold, C.Accent, 24, 90, PW - 48, 20);
            pct.TextAlign = ContentAlignment.MiddleRight;

            cancelBtn = new ActionButton("取消", false) { Bounds = new Rectangle(24, 124, 110, 32) };
            cancelBtn.Click += delegate { DoCancel(); };

            Panel foot = new Panel { Bounds = new Rectangle(0, PH - 2, PW, 2), BackColor = C.Line };

            Controls.Add(foot);
            Controls.Add(cancelBtn);
            Controls.Add(pct);
            Controls.Add(barWrap);
            Controls.Add(sub);
            Controls.Add(cap);
            Controls.Add(header);

            Region = new Region(Draw.RR(0, 0, PW, PH, 12));
        }

        Label MkLabel(string text, Font f, Color col, int x, int y, int w, int h)
        {
            Label l = new Label
            {
                Text = text,
                Font = f,
                ForeColor = col,
                BackColor = C.Bg,
                Bounds = new Rectangle(x, y, w, h),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft
            };
            return l;
        }

        double prog = 0;
        string progText = "";

        public void Progress(int done, int total, string note)
        {
            prog = total <= 0 ? 0 : (double)done / total;
            if (prog > 1) prog = 1;
            progText = total <= 0
                ? note
                : (string.IsNullOrEmpty(note) ? done + " / " + total
                                             : note + "　" + done + " / " + total);
            Safe(delegate
            {
                sub.Text = progText;
                barWrap.Invalidate();
            });
        }

        void PaintBar(object sender, PaintEventArgs e)
        {
            Panel p = sender as Panel;
            Graphics g = e.Graphics;
            g.Clear(C.FromHex("#1D2230"));
            double v = prog;
            if (v < 0) v = 0;
            if (v > 1) v = 1;
            int w = (int)(p.Width * v);
            if (w <= 2) return;
            using (GraphicsPath rp = Draw.RR(1, 1, w - 2, p.Height - 2, (p.Height - 2) / 2f))
            using (LinearGradientBrush lg = new LinearGradientBrush(
                new Rectangle(1, 1, w - 2, p.Height - 2), C.Accent, C.Warn, 90f))
                g.FillPath(lg, rp);
        }

        public void Note(string s) { Safe(delegate { sub.Text = s; }); }
        public void Pct(string s) { Safe(delegate { pct.Text = s; }); }
        public void Caption(string s) { Safe(delegate { cap.Text = s; }); }

        /// <summary>
        /// 进度是后台线程往上报的。句柄还没建、控件正在销毁、或者线程已经不是
        /// 创建者了，这时候碰 UI 都会炸 —— 统一在这里挡掉。
        /// </summary>
        void Safe(MethodVoid a)
        {
            try
            {
                if (!IsHandleCreated || IsDisposed || Disposing) return;
                if (InvokeRequired) { BeginInvoke(new Action(a)); return; }
                a();
            }
            catch { }
        }

        delegate void MethodVoid();

        void DoCancel()
        {
            if (closing) return;
            closing = true;
            cancelled = true;
            cancelBtn.Caption = "正在停止…";
            cancelBtn.Invalidate();
            cancelBtn.Enabled = false;
            if (OnCancelled != null) OnCancelled(true);
            Note("正在停下，等当前这一步走完…");
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (Running && e.CloseReason != CloseReason.WindowsShutDown)
            {
                // 关不掉是故意的：得先让后台线程停下
                e.Cancel = true;
                DoCancel();
                return;
            }
            base.OnFormClosing(e);
        }

        /// <summary>开一个后台线程干活，进度通过 Progress 回主线程</summary>
        public void Run(Action<ProgressForm> work)
        {
            this.work = work;
            Running = true;
            cancelled = false;
            Show();
            Application.DoEvents();
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { work(this); }
                catch (Exception ex) { Program.Log("ProgressForm", ex); }
                Safe(delegate
                {
                    Running = false;
                    closing = false;
                    Close();
                });
            });
        }
    }
}
