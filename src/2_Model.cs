using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using Path2 = System.IO.Path;

namespace SpaceHunter
{
    // =====================================================================
    //  分类
    // =====================================================================
    enum Cat { Cache, Installer, Dup, Big, OldFile, Orphan, Chat, Log, Recycle }

    static class CatMeta
    {
        public static readonly Cat[] All = new Cat[]
        {
            Cat.Cache, Cat.Installer, Cat.Dup, Cat.Big,
            Cat.OldFile, Cat.Orphan, Cat.Chat, Cat.Log, Cat.Recycle
        };

        public static string Name(Cat c)
        {
            switch (c)
            {
                case Cat.Cache: return "缓存垃圾";
                case Cat.Installer: return "安装包 / 压缩包";
                case Cat.Dup: return "重复文件";
                case Cat.Big: return "超大文件";
                case Cat.OldFile: return "久未使用";
                case Cat.Orphan: return "卸载残留";
                case Cat.Chat: return "聊天缓存";
                case Cat.Log: return "日志 / 崩溃";
                case Cat.Recycle: return "回收站";
            }
            return "?";
        }

        public static string Hint(Cat c)
        {
            switch (c)
            {
                case Cat.Cache: return "删掉会自动重建";
                case Cat.Installer: return "下载目录里的安装包、压缩包";
                case Cat.Dup: return "内容完全相同的多份文件";
                case Cat.Big: return "单个文件大于 800 MB";
                case Cat.OldFile: return "超过 90 天没打开过";
                case Cat.Orphan: return "找不到对应程序的 AppData 目录";
                case Cat.Chat: return "QQ / 微信 的图片视频附件";
                case Cat.Log: return "纯文本日志与崩溃记录";
                case Cat.Recycle: return "回收站里的内容";
            }
            return "";
        }

        public static Color Col(Cat c)
        {
            switch (c)
            {
                case Cat.Cache: return C.FromHex("#4C9AFF");
                case Cat.Installer: return C.FromHex("#FF9F0A");
                case Cat.Dup: return C.FromHex("#BF5AF2");
                case Cat.Big: return C.FromHex("#FF6B35");
                case Cat.OldFile: return C.FromHex("#FFD426");
                case Cat.Orphan: return C.FromHex("#FF375F");
                case Cat.Chat: return C.FromHex("#64D2FF");
                case Cat.Log: return C.FromHex("#5AC8FA");
                case Cat.Recycle: return C.FromHex("#8E8E93");
            }
            return C.Fg2;
        }

        public static bool DefaultOn(Cat c)
        {
            return c == Cat.Cache || c == Cat.Log || c == Cat.Recycle
                || c == Cat.Dup || c == Cat.Installer;
        }
    }

    enum Mov { Unknown, Ok, Locked, NoPerm, Missing }

    // =====================================================================
    //  扫描出来的一条垃圾
    // =====================================================================
    class Finding
    {
        public string Path;
        public long Size;
        public Cat Cat;
        public string Reason;
        public DateTime When;
        public bool On;
        public bool IsDir;
        public string Name;

        /// <summary>
        /// 这一项在哪个盘上，比如 "D:\"。
        /// 从路径自己推断 —— 有了它，切盘就只是过滤已扫过的结果，
        /// 不需要为了换一个盘去重扫一遍。
        /// </summary>
        public string Drive = "";

        public Mov Mov = Mov.Unknown;
        public string MovNote = "";

        public Finding(string path, long size, Cat cat, string reason)
        {
            Path = path;
            Size = size;
            Cat = cat;
            Reason = reason;
            On = CatMeta.DefaultOn(cat);
            try { IsDir = Directory.Exists(path); } catch { IsDir = false; }
            try
            {
                FileSystemInfo fi = IsDir
                    ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
                When = fi.LastWriteTime;
            }
            catch { When = DateTime.MinValue; }
            try { Name = Path2.GetFileName(path.TrimEnd('\\')); } catch { Name = path; }
            if (string.IsNullOrEmpty(Name)) Name = path;
            try
            {
                string root = Path2.GetPathRoot(path);
                if (!string.IsNullOrEmpty(root))
                    Drive = new DriveInfo(root).RootDirectory.FullName;
            }
            catch { }
        }

        public bool Blocked
        {
            get { return Mov == Mov.Locked || Mov == Mov.NoPerm || Mov == Mov.Missing; }
        }

        public string DisplayReason
        {
            get
            {
                string s = Reason + (IsDir ? "　·　文件夹" : "");
                if (Mov == Mov.Ok) s += "　·　会移到隔离区";
                else if (Blocked)
                    s += "　·　会直接删除（" +
                         (string.IsNullOrEmpty(MovNote) ? "搬不动" : MovNote) + "）";
                else if (!string.IsNullOrEmpty(MovNote)) s += "　·　" + MovNote;
                return s;
            }
        }

        public string DisplayName
        {
            get
            {
                string n = Name;
                if (n.Length > 58) n = n.Substring(0, 27) + " … " + n.Substring(n.Length - 26);
                return n;
            }
        }
    }

    class DirSize
    {
        public string Path;
        public string Name;
        public long Size;
public DirSize(string p, long s)
        {
            Path = p; Size = s;
            try { Name = System.IO.Path.GetFileName(p.TrimEnd('\\')); } catch { Name = p; }
            if (string.IsNullOrEmpty(Name)) Name = p;
        }
    }

    class DriveX
    {
        public string Root;
        public string Label;
        public long Total;
        public long Free;

        public DriveX(string root)
        {
            Root = root;
            try
            {
                DriveInfo d = new DriveInfo(root);
                Label = string.IsNullOrEmpty(d.VolumeLabel) ? "" : d.VolumeLabel;
                Total = d.TotalSize;
                Free = d.AvailableFreeSpace;
            }
            catch { Label = ""; Total = 1; Free = 0; }
        }

        public long Used { get { return Total - Free; } }
        public double Ratio { get { return Total <= 0 ? 0 : (double)Used / Total; } }
        public string Title
        {
            get { return string.IsNullOrEmpty(Label) ? Root : Root + " · " + Label; }
        }
    }

    // =====================================================================
    //  可移动性预检查：结论必须和真正的清理操作完全一致
    // =====================================================================
    static class MovCheck
    {
        public static void Run(List<Finding> items, Action<int, int> onProg, Func<bool> cancelled)
        {
            if (items == null) return;
            int i = 0;
            for (int n = 0; n < items.Count; n++)
            {
                if (cancelled != null && cancelled()) return;
                Check(items[n]);
                i++;
                if ((i & 7) == 0 && onProg != null) onProg(i, items.Count);
            }
            if (onProg != null) onProg(items.Count, items.Count);
        }

        static void Check(Finding f)
        {
            try
            {
                bool isDir = Directory.Exists(f.Path);
                if (!isDir && !File.Exists(f.Path))
                {
                    f.Mov = Mov.Missing;
                    f.MovNote = "已不存在";
                    return;
                }

                // 不管文件还是目录，都真的改名一次再改回来。
                // NTFS 下目录改名是常数时间，不慢；而这样得出的结论
                // 和真正的隔离操作是同一件事，零偏差。
                string tmp = f.Path.TrimEnd('\\') + ".shw~";
                if (isDir) Directory.Move(f.Path, tmp);
                else File.Move(f.Path, tmp);

                try
                {
                    if (isDir) Directory.Move(tmp, f.Path);
                    else File.Move(tmp, f.Path);
                    f.Mov = Mov.Ok;
                }
                catch
                {
                    try { if (isDir) Directory.Move(tmp, f.Path); else File.Move(tmp, f.Path); }
                    catch { }
                    f.Mov = Mov.Locked;
                    f.MovNote = "改名后无法还原";
                }
            }
            catch (UnauthorizedAccessException) { f.Mov = Mov.NoPerm; f.MovNote = "没有权限"; }
            catch (IOException ex)
            {
                string m = ex.Message == null ? "" : ex.Message;
                f.Mov = Mov.Locked;
                if (m.Contains("拒绝")) { f.Mov = Mov.NoPerm; f.MovNote = "访问被拒绝"; }
                else if (m.Contains("正被") || m.Contains("being used")) f.MovNote = "正被占用";
                else if (m.Contains("文件名") || m.Contains("too long"))
                { f.Mov = Mov.NoPerm; f.MovNote = "路径太长或名字非法"; }
                else f.MovNote = "暂时无法移动";
            }
            catch { f.Mov = Mov.Locked; f.MovNote = "无法移动"; }
        }

        /// <summary>统计搬不动的数量（它们会走「直接删除」这条路）</summary>
        public static int CountUnmovable(List<Finding> items)
        {
            int n = 0;
            for (int i = 0; i < items.Count; i++) if (items[i].Blocked) n++;
            return n;
        }
    }
}