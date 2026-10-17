using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace SpaceHunter
{
    // =====================================================================
    //  运行环境：全部动态获取，保证能拷到任何电脑跑
    // =====================================================================
    static class Env
    {
        public static readonly string UserProfile =
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public static readonly string LocalAppData =
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        public static readonly string AppData =
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        public static readonly string CommonAppData =
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        public static readonly string WinDir =
            Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        public static readonly string TempDir = Path.GetTempPath().TrimEnd('\\');
        public static readonly string Desktop =
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        public static readonly string StartMenu =
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        public static readonly string CommonStartMenu =
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        public static readonly string CommonDesktop =
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

        /// <summary>按优先级挑第一个系统里真实存在的字体，缺了也不会崩</summary>
        public static Font Make(float size, FontStyle style, string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                try
                {
                    FontFamily ff = new FontFamily(names[i]);
                    if (ff.IsStyleAvailable(style)) return new Font(ff, size, style);
                }
                catch { }
            }
            try { return new Font(FontFamily.GenericSansSerif, size, style); }
            catch { return new Font("Arial", size, style); }
        }
    }

    static class Fonts
    {
        static readonly string[] Cjk = { "Microsoft YaHei UI", "Microsoft YaHei", "SimSun", "Segoe UI", "Tahoma" };
        static readonly string[] Num = { "Segoe UI", "Tahoma", "Arial", "Microsoft YaHei UI" };

        public static readonly Font BigTitle = Env.Make(15f, FontStyle.Bold, Cjk);
        public static readonly Font SectionTitle = Env.Make(9.5f, FontStyle.Bold, Cjk);
        public static readonly Font Body = Env.Make(9f, FontStyle.Regular, Cjk);
        public static readonly Font BodyBold = Env.Make(9f, FontStyle.Bold, Cjk);
        public static readonly Font RowName = Env.Make(9f, FontStyle.Regular, Cjk);
        public static readonly Font RowReason = Env.Make(7.5f, FontStyle.Regular, Cjk);
        public static readonly Font Small = Env.Make(7.5f, FontStyle.Regular, Cjk);
        public static readonly Font SmallBold = Env.Make(7.5f, FontStyle.Bold, Cjk);
        public static readonly Font BtnText = Env.Make(10.5f, FontStyle.Bold, Cjk);
        public static readonly Font RingBig = Env.Make(13.5f, FontStyle.Bold, Num);
        public static readonly Font HugeNum = Env.Make(25f, FontStyle.Bold, Num);
        public static readonly Font Size = Env.Make(9.5f, FontStyle.Bold, Num);
        public static readonly Font SizeSm = Env.Make(8.5f, FontStyle.Bold, Num);
        public static readonly Font Crumb = Env.Make(9f, FontStyle.Regular, Num);
    }

    // =====================================================================
    //  主题
    // =====================================================================
    static class C
    {
        public static readonly Color Bg = FromHex("#101219");
        public static readonly Color Card = FromHex("#171A23");
        public static readonly Color Card2 = FromHex("#1E2230");
        public static readonly Color Hover = FromHex("#242A3A");
        public static readonly Color Line = FromHex("#2A3042");
        public static readonly Color Fg = FromHex("#E9ECF5");
        public static readonly Color Fg2 = FromHex("#8A93AB");
        public static readonly Color Fg3 = FromHex("#5B6480");
        public static readonly Color Accent = FromHex("#FF6B35");
        public static readonly Color Ok = FromHex("#35C759");
        public static readonly Color Warn = FromHex("#FFC53D");
        public static readonly Color Danger = FromHex("#FF453A");
        public static readonly Color Cyan = FromHex("#4CC9F0");

        public static Color FromHex(string h)
        {
            int v = Convert.ToInt32(h.Substring(1), 16);
            return Color.FromArgb((v >> 16) & 255, (v >> 8) & 255, v & 255);
        }
    }

    // =====================================================================
    //  画笔缓存：OnPaint 里绝不能 new Brush，否则高频重绘会堆爆 GDI 资源
    // =====================================================================
    static class Br
    {
        static readonly Dictionary<int, SolidBrush> soft = new Dictionary<int, SolidBrush>();

        /// <summary>
        /// 半透明画笔的缓存。
        ///
        /// 重要：返回的是**共享实例**，谁都不许 Dispose。
        /// （曾经写成 using(SolidBrush b = Br.Soft(...))，结果块一结束
        ///   就把缓存里的画笔 Dispose 了，后面再画直接抛「参数无效」——
        ///   GDI+ 的报错完全不指向真凶，这种坑只能从约定上堵。）
        /// 确实要局部新建的，用 new SolidBrush(Color.FromArgb(...))。
        /// </summary>
        public static SolidBrush Soft(Color c, int a)
        {
            int key = c.ToArgb() ^ (a * 397);
            SolidBrush b;
            if (!soft.TryGetValue(key, out b))
            {
                b = new SolidBrush(Color.FromArgb(a, c.R, c.G, c.B));
                soft[key] = b;
            }
            return b;
        }

        public static SolidBrush Track(Color c) { return Soft(c, 255); }

        /// <summary>DrawLine 只吃 Pen，缓存几支免得每次 new</summary>
        public static readonly Pen PenLine = new Pen(C.Line);
        public static readonly Pen PenLine2 = new Pen(C.Line, 2f);
        public static readonly Pen PenAccent = new Pen(C.Accent);
        public static readonly Pen PenDim = new Pen(C.Fg3);
        public static readonly Pen PenRowSep = new Pen(Color.FromArgb(22, 255, 255, 255));

        public static readonly SolidBrush Bg = new SolidBrush(C.Bg);
        public static readonly SolidBrush Card = new SolidBrush(C.Card);
        public static readonly SolidBrush Card2 = new SolidBrush(C.Card2);
        public static readonly SolidBrush Hover = new SolidBrush(C.Hover);
        public static readonly SolidBrush Line = new SolidBrush(C.Line);
        public static readonly SolidBrush Fg = new SolidBrush(C.Fg);
        public static readonly SolidBrush Fg2 = new SolidBrush(C.Fg2);
        public static readonly SolidBrush Fg3 = new SolidBrush(C.Fg3);
        public static readonly SolidBrush White = new SolidBrush(Color.White);
        public static readonly SolidBrush Accent = new SolidBrush(C.Accent);
        public static readonly SolidBrush TrackBar = new SolidBrush(C.FromHex("#232838"));
        public static readonly SolidBrush RowSep = new SolidBrush(Color.FromArgb(22, 255, 255, 255));
        public static readonly SolidBrush RowSep2 = new SolidBrush(Color.FromArgb(40, 255, 255, 255));
        public static readonly SolidBrush SelBg = new SolidBrush(Color.FromArgb(40, 255, 107, 53));
        public static readonly SolidBrush ChkOff = new SolidBrush(C.FromHex("#39415A"));
    }

    // =====================================================================
    //  格式化
    // =====================================================================
    static class Fmt
    {
        static readonly string[] U = { "B", "KB", "MB", "GB", "TB" };

        public static string Size(long b)
        {
            double v = b;
            int i = 0;
            while (v >= 1024 && i < U.Length - 1) { v /= 1024.0; i++; }
            if (i <= 1) return ((long)v).ToString() + " " + U[i];
            return v.ToString("0.##") + " " + U[i];
        }

        public static string SizeShort(long b)
        {
            double v = b;
            int i = 0;
            while (v >= 1024 && i < U.Length - 1) { v /= 1024.0; i++; }
            return (i <= 1 ? ((long)v).ToString() : v.ToString("0.#")) + U[i];
        }

        public static string Age(DateTime t)
        {
            if (t == DateTime.MinValue) return "-";
            int d = (int)(DateTime.Now - t).TotalDays;
            if (d < 0) d = 0;
            if (d < 1) return "今天";
            if (d < 30) return d + " 天前";
            if (d < 365) return (d / 30) + " 个月前";
            return (d / 365) + " 年前";
        }

        public static string Date(DateTime t)
        {
            return t == DateTime.MinValue ? "-" : t.ToString("yyyy-MM-dd");
        }

        public static string Norm(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char ch = char.ToLowerInvariant(s[i]);
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')) sb.Append(ch);
            }
            return sb.ToString();
        }

        public static string Elapsed(TimeSpan ts)
        {
            if (ts.TotalSeconds < 60) return ((int)ts.TotalSeconds) + " 秒";
            return ((int)ts.TotalMinutes) + " 分 " + ts.Seconds + " 秒";
        }

        public static string OneLine(string s, int max)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
            return s.Length > max ? s.Substring(0, max) : s;
        }
    }

    // =====================================================================
    //  绘图
    // =====================================================================
    static class Draw
    {
        public static GraphicsPath RR(float x, float y, float w, float h, float r)
        {
            GraphicsPath p = new GraphicsPath();
            if (w <= 0 || h <= 0) return p;
            if (r <= 0.5f) { p.AddRectangle(new RectangleF(x, y, w, h)); return p; }
            if (r * 2 > w) r = w / 2f;
            if (r * 2 > h) r = h / 2f;
            p.AddArc(x, y, r * 2, r * 2, 180, 92);
            p.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 88);
            p.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 92);
            p.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 88);
            p.CloseFigure();
            return p;
        }

        public static GraphicsPath Circle(float cx, float cy, float r)
        {
            GraphicsPath p = new GraphicsPath();
            p.AddEllipse(cx - r, cy - r, r * 2, r * 2);
            return p;
        }

        public static void Ellipsize(Graphics g, string text, Font f, Brush b,
            float x, float y, float w)
        {
            if (string.IsNullOrEmpty(text) || w < 4) return;
            if (g.MeasureString(text, f).Width <= w) { g.DrawString(text, f, b, x, y); return; }
            int lo = 0, hi = text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (g.MeasureString(text.Substring(0, mid) + "…", f).Width <= w) lo = mid;
                else hi = mid - 1;
            }
            g.DrawString(text.Substring(0, lo) + "…", f, b, x, y);
        }

        public static string Shorten(string p, int max)
        {
            if (p == null) return "";
            if (!string.IsNullOrEmpty(Env.UserProfile))
                p = p.Replace(Env.UserProfile, "~");
            if (p.Length <= max) return p;
            return "…" + p.Substring(p.Length - max + 1);
        }
    }

    /// <summary>
    /// 控件树里按条件找东西。
    ///
    /// 自检要用它来断言「按钮真的存在、真的有尺寸、真的在屏幕里」。
    /// 之前出过的事故：按钮 new 出来了、Click 也挂上了、但从头到尾没人
    /// 给它 SetBounds，尺寸停在 0×0 —— 界面上一丁点都看不见，
    /// 而「控件 != null」这种检查完全看不出问题。
    /// </summary>
    static class CtrlTree
    {
        public static void Each(Control root, Action<Control> act)
        {
            if (root == null || act == null) return;
            act(root);
            // 遍历时必须先复制一份：回调里有可能增删控件，
            // 直接遍历 ControlCollection 会抛「集合已被修改」。
            Control[] kids;
            try
            {
                kids = new Control[root.Controls.Count];
                root.Controls.CopyTo(kids, 0);
            }
            catch { return; }
            for (int i = 0; i < kids.Length; i++) Each(kids[i], act);
        }

        public static Control FindFirst(Control root, Func<Control, bool> pred)
        {
            Control hit = null;
            Each(root, delegate(Control c)
            {
                if (hit == null && pred(c)) hit = c;
            });
            return hit;
        }
    }
}