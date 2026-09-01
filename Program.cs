// 远程桌面快切 (RdpQuickSwitch)
// 屏幕左上角常驻一个最高置顶的迷你浮窗，显示当前打开的远程桌面(mstsc)窗口数量；
// 鼠标悬停自动展开窗口列表，点击任意一项即可切换到对应远程桌面。
// 使用 Windows 自带 .NET Framework 编译器编译，零依赖。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RdpQuickSwitch
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            try { Win32.SetProcessDPIAware(); } catch { }

            bool createdNew;
            using (System.Threading.Mutex mutex = new System.Threading.Mutex(true, "RdpQuickSwitch_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("远程桌面快切已经在运行了。", "远程桌面快切",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                float scale = 1f;
                try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f; } catch { }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayContext(scale));
            }
        }
    }

    // ---------------------------------------------------------------------

    internal static class Win32
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags, StringBuilder text, ref int size);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x1;
        public const uint SWP_NOMOVE = 0x2;
        public const uint SWP_NOACTIVATE = 0x10;
        public const uint SWP_NOOWNERZORDER = 0x200;

        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x80;
        public const int SW_RESTORE = 9;
        public const int SW_SHOW = 5;
        public const int SW_MINIMIZE = 6;
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const byte VK_MENU = 0x12;
        public const uint KEYEVENTF_KEYUP = 0x2;
    }

    // ---------------------------------------------------------------------

    internal class TargetWindow
    {
        public IntPtr Handle;
        public string Title;
        public bool IsActive;
    }

    internal static class WindowScanner
    {
        private static readonly HashSet<string> Defaults = new HashSet<string>(new string[] { "mstsc" });
        private static DateTime _cfgLastWrite = DateTime.MinValue;
        private static HashSet<string> _cached;

        // 目标进程名：默认 mstsc（Windows 远程桌面连接），可经 targets.txt 扩展
        public static HashSet<string> LoadTargetProcesses()
        {
            try
            {
                string cfg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "targets.txt");
                if (File.Exists(cfg))
                {
                    DateTime write = File.GetLastWriteTimeUtc(cfg);
                    if (_cached != null && write == _cfgLastWrite) return _cached;
                    _cfgLastWrite = write;

                    HashSet<string> set = new HashSet<string>(Defaults);
                    foreach (string line in File.ReadAllLines(cfg))
                    {
                        string t = line.Trim().ToLowerInvariant();
                        if (t.Length == 0 || t.StartsWith("#")) continue;
                        if (t.EndsWith(".exe")) t = t.Substring(0, t.Length - 4);
                        if (t.Length > 0) set.Add(t);
                    }
                    _cached = set;
                    return set;
                }
            }
            catch { }
            return Defaults;
        }

        public static List<TargetWindow> Scan()
        {
            HashSet<string> targets = LoadTargetProcesses();
            List<TargetWindow> result = new List<TargetWindow>();
            IntPtr active = Win32.GetForegroundWindow();

            Win32.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                try
                {
                    if (!Win32.IsWindowVisible(h)) return true;
                    if ((Win32.GetWindowLong(h, Win32.GWL_EXSTYLE) & Win32.WS_EX_TOOLWINDOW) != 0) return true;

                    StringBuilder sb = new StringBuilder(512);
                    if (Win32.GetWindowText(h, sb, 512) == 0) return true;
                    string title = sb.ToString();
                    if (title.Trim().Length == 0) return true;

                    uint pid;
                    Win32.GetWindowThreadProcessId(h, out pid);
                    string proc = ProcessImageName(pid);
                    if (proc != null && targets.Contains(proc))
                    {
                        TargetWindow tw = new TargetWindow();
                        tw.Handle = h;
                        tw.Title = title;
                        tw.IsActive = h == active;
                        result.Add(tw);
                    }
                }
                catch { }
                return true;
            }, IntPtr.Zero);

            return result; // EnumWindows 按 Z 序返回：活跃/靠前的窗口排在前
        }

        private static string ProcessImageName(uint pid)
        {
            IntPtr hProc = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProc == IntPtr.Zero) return null;
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (!Win32.QueryFullProcessImageName(hProc, 0, sb, ref size)) return null;
                string path = sb.ToString(0, size);
                string name = Path.GetFileName(path).ToLowerInvariant();
                if (name.EndsWith(".exe")) name = name.Substring(0, name.Length - 4);
                return name;
            }
            finally { Win32.CloseHandle(hProc); }
        }
    }

    // ---------------------------------------------------------------------

    internal static class Switcher
    {
        // 一键回到主机桌面：最小化所有远程桌面窗口（已最小化的跳过）
        public static void MinimizeAll(List<TargetWindow> windows)
        {
            for (int i = 0; i < windows.Count; i++)
            {
                TargetWindow w = windows[i];
                if (Win32.IsWindow(w.Handle) && !Win32.IsIconic(w.Handle))
                    Win32.ShowWindow(w.Handle, Win32.SW_MINIMIZE);
            }
        }

        public static void SwitchTo(IntPtr hwnd)
        {
            if (!Win32.IsWindow(hwnd)) return;

            if (Win32.IsIconic(hwnd)) Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
            else Win32.ShowWindow(hwnd, Win32.SW_SHOW);

            // 先按一下 ALT 再调用 SetForegroundWindow，绕过系统的前台锁定限制
            Win32.keybd_event(Win32.VK_MENU, 0, 0, UIntPtr.Zero);
            bool ok = Win32.SetForegroundWindow(hwnd);
            Win32.keybd_event(Win32.VK_MENU, 0, Win32.KEYEVENTF_KEYUP, UIntPtr.Zero);

            if (!ok)
            {
                uint thisThread = Win32.GetCurrentThreadId();
                uint dummy;
                uint foreThread = Win32.GetWindowThreadProcessId(Win32.GetForegroundWindow(), out dummy);
                if (foreThread != 0 && foreThread != thisThread)
                {
                    Win32.AttachThreadInput(thisThread, foreThread, true);
                    Win32.SetForegroundWindow(hwnd);
                    Win32.AttachThreadInput(thisThread, foreThread, false);
                }
            }
        }
    }

    // ---------------------------------------------------------------------

    internal class FloatWindow : Form
    {
        private readonly float _s; // DPI 缩放系数
        private List<TargetWindow> _windows = new List<TargetWindow>();
        private bool _expanded;
        private int _hoverRow = -1;
        private bool _hoverFooter;
        private Point _collapsedLocation;
        private Point _downPos;
        private bool _dragMoved;
        private bool _dragArmed;      // 已按下（处于可拖动状态）
        private bool _dragZoneHeader; // 按下点位于展开面板的标题栏
        private readonly System.Windows.Forms.Timer _pollTimer;
        private readonly System.Windows.Forms.Timer _collapseTimer;
        private readonly System.Windows.Forms.Timer _expandTimer; // 悬停延时展开，避免划过/按下拖动时面板立刻弹出
        private readonly ToolTip _tip = new ToolTip();

        private int PillW { get { return (int)(78 * _s); } }
        private int PillH { get { return (int)(26 * _s); } }
        private int ExpW { get { return (int)(260 * _s); } }
        private int RowH { get { return (int)(30 * _s); } }
        private int HeadH { get { return (int)(26 * _s); } }
        private int Pad { get { return (int)(8 * _s); } }

        public FloatWindow(float scale)
        {
            _s = scale;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            // 浮窗尺寸完全由 UpdateSize 按 DPI 自行计算，必须禁掉 WinForms 的
            // 自动缩放，否则句柄创建时会被按字体/DPI 基线放大一次，和 Region 错位
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            Opacity = 0.92;
            BackColor = Color.FromArgb(28, 30, 35);
            Font = new Font("Microsoft YaHei UI", 9F);

            _collapsedLocation = LoadPosition();
            Location = _collapsedLocation;
            _tip.SetToolTip(this, "远程桌面快切：悬停展开，点击切换");

            _pollTimer = new System.Windows.Forms.Timer();
            _pollTimer.Interval = 1000;
            _pollTimer.Tick += delegate { RefreshWindows(); ReassertTopMost(); };

            _collapseTimer = new System.Windows.Forms.Timer();
            _collapseTimer.Interval = 350;
            _collapseTimer.Tick += CollapseTimerTick;

            // 悬停约 300ms 才展开面板：快速划过或按下拖动时保持小药丸
            _expandTimer = new System.Windows.Forms.Timer();
            _expandTimer.Interval = 280;
            _expandTimer.Tick += delegate
            {
                _expandTimer.Stop();
                Rectangle b = new Rectangle(Location, Size);
                b.Inflate(1, 1);
                if (b.Contains(Cursor.Position) && _windows.Count > 0 && !_expanded)
                {
                    _expanded = true;
                    UpdateSize();
                    Invalidate();
                }
            };

            // 初始必须无条件按收起态定好尺寸：
            // 若当前没有任何远程桌面窗口，RefreshWindows 检测不到变化会跳过 UpdateSize，
            // 表单就会保持 WinForms 默认的 300x300，浮窗变成一个大灰块。
            UpdateSize();
            RefreshWindows();
            _pollTimer.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _pollTimer.Dispose();
                _collapseTimer.Dispose();
                _expandTimer.Dispose();
                _tip.Dispose();
            }
            base.Dispose(disposing);
        }

        // ---------------- 置顶自校正 ----------------
        // RDP 会话重连或系统重排 Z 序时，WS_EX_TOPMOST 样式位可能被清除，
        // 导致浮窗被远程桌面窗口盖住。这里定时无条件把窗口重新放回最顶层带。

        private void ReassertTopMost()
        {
            if (!IsHandleCreated || !Visible) return;
            Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Text = "远程桌面快切";
            UpdateSize(); // 句柄创建后按最终状态再定一次尺寸+Region，防止创建期的缩放干扰
            ReassertTopMost();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ApplyRegion(); // 尺寸无论被谁改动，圆角区域始终跟随
        }

        // ---------------- 尺寸与位置 ----------------

        private void UpdateSize()
        {
            if (_expanded && _windows.Count > 0)
            {
                // 标题行 + 窗口列表 + 「回到主机桌面」按钮行 + 底部留白
                int h = HeadH + _windows.Count * RowH + RowH + Pad;
                Rectangle work = Screen.FromPoint(_collapsedLocation).WorkingArea;
                int x = _collapsedLocation.X;
                int y = _collapsedLocation.Y;
                if (x + ExpW > work.Right) x = Math.Max(work.Left, work.Right - ExpW);
                if (y + h > work.Bottom) y = Math.Max(work.Top, work.Bottom - h);
                Location = new Point(x, y);
                ClientSize = new Size(ExpW, h);
            }
            else
            {
                Location = _collapsedLocation;
                ClientSize = new Size(PillW, PillH);
                _hoverRow = -1;
                _hoverFooter = false;
            }
            ApplyRegion();
        }

        private void ApplyRegion()
        {
            GraphicsPath path = RoundedPath(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
            Region old = Region;
            Region = new Region(path);
            path.Dispose();
            if (old != null) old.Dispose();
        }

        private GraphicsPath RoundedPath(Rectangle rect)
        {
            int r = (int)(8 * _s);
            GraphicsPath path = new GraphicsPath();
            path.AddArc(rect.X, rect.Y, r, r, 180, 90);
            path.AddArc(rect.Right - r, rect.Y, r, r, 270, 90);
            path.AddArc(rect.Right - r, rect.Bottom - r, r, r, 0, 90);
            path.AddArc(rect.X, rect.Bottom - r, r, r, 90, 90);
            path.CloseFigure();
            return path;
        }

        private Point LoadPosition()
        {
            int x = (int)(6 * _s), y = (int)(6 * _s);
            try
            {
                string f = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "floatpos.txt");
                if (File.Exists(f))
                {
                    string[] parts = File.ReadAllText(f).Trim().Split(',');
                    if (parts.Length == 2)
                    {
                        int px = int.Parse(parts[0].Trim());
                        int py = int.Parse(parts[1].Trim());
                        foreach (Screen sc in Screen.AllScreens)
                        {
                            if (sc.Bounds.Contains(px, py)) { x = px; y = py; break; }
                        }
                    }
                }
            }
            catch { }
            return new Point(x, y);
        }

        private void SavePosition()
        {
            try
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "floatpos.txt"),
                    Location.X + "," + Location.Y);
            }
            catch { }
        }

        // ---------------- 窗口扫描刷新 ----------------

        private void RefreshWindows()
        {
            List<TargetWindow> list = WindowScanner.Scan();
            bool changed = list.Count != _windows.Count;
            if (!changed)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].Handle != _windows[i].Handle ||
                        list[i].Title != _windows[i].Title ||
                        list[i].IsActive != _windows[i].IsActive)
                    {
                        changed = true;
                        break;
                    }
                }
            }
            if (changed)
            {
                _windows = list;
                if (_windows.Count == 0) _expanded = false;
                UpdateSize();
                Invalidate();
            }
        }

        // ---------------- 绘制 ----------------

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle full = new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            Color bg = Color.FromArgb(28, 30, 35);
            Color border = Color.FromArgb(75, 95, 125);

            if (!_expanded || _windows.Count == 0)
            {
                // 收起态：一个小药丸，双箭头图标 + 窗口数量
                using (SolidBrush b = new SolidBrush(bg)) g.FillPath(b, RoundedPath(full));
                using (Pen p = new Pen(border)) g.DrawPath(p, RoundedPath(full));

                int iconW = (int)(14 * _s);
                int iconH = (int)(10 * _s);
                Rectangle iconRect = new Rectangle((int)(12 * _s), PillH / 2 - iconH / 2, iconW, iconH);
                Color iconColor = _windows.Count > 0 ? Color.FromArgb(110, 185, 255) : Color.FromArgb(115, 120, 130);
                DrawSwitchIcon(g, iconRect, iconColor);

                Color numColor = _windows.Count > 0 ? Color.White : Color.FromArgb(125, 130, 140);
                Rectangle numRect = new Rectangle(iconRect.Right + (int)(6 * _s), 0,
                    ClientSize.Width - iconRect.Right - (int)(10 * _s), PillH);
                TextRenderer.DrawText(g, _windows.Count.ToString(), Font, numRect, numColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
            else
            {
                // 展开态：标题行 + 每个远程桌面窗口一行
                using (SolidBrush b = new SolidBrush(bg)) g.FillPath(b, RoundedPath(full));
                using (Pen p = new Pen(border)) g.DrawPath(p, RoundedPath(full));

                TextRenderer.DrawText(g, "远程桌面 (" + _windows.Count + ")", Font,
                    new Rectangle(Pad, 0, ExpW - Pad * 2, HeadH), Color.FromArgb(215, 220, 230),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                using (Pen linePen = new Pen(Color.FromArgb(55, 60, 70)))
                    g.DrawLine(linePen, Pad, HeadH, ExpW - Pad, HeadH);

                for (int i = 0; i < _windows.Count; i++)
                {
                    TargetWindow w = _windows[i];
                    Rectangle rowRect = new Rectangle(0, HeadH + i * RowH, ExpW, RowH);

                    bool hovered = i == _hoverRow;
                    if (w.IsActive || hovered)
                    {
                        Color rowBg = w.IsActive ? Color.FromArgb(45, 58, 82) : Color.FromArgb(42, 46, 54);
                        using (SolidBrush b = new SolidBrush(rowBg)) g.FillRectangle(b, rowRect);
                    }
                    if (w.IsActive)
                    {
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(85, 160, 255)))
                            g.FillRectangle(b, rowRect.X, rowRect.Y + (int)(6 * _s), (int)(3 * _s), RowH - (int)(12 * _s));
                    }

                    TextRenderer.DrawText(g, w.Title, Font,
                        new Rectangle(Pad + (int)(4 * _s), rowRect.Y, ExpW - Pad * 2 - (int)(4 * _s), RowH),
                        w.IsActive ? Color.White : Color.FromArgb(205, 210, 220),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                }

                // 底部「回到主机桌面」按钮行
                int footY = HeadH + _windows.Count * RowH;
                using (Pen linePen = new Pen(Color.FromArgb(55, 60, 70)))
                    g.DrawLine(linePen, Pad, footY, ExpW - Pad, footY);
                if (_hoverFooter)
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(42, 46, 54)))
                        g.FillRectangle(b, 0, footY, ExpW, RowH);
                }
                TextRenderer.DrawText(g, "« 回到主机桌面（最小化全部远程）", Font,
                    new Rectangle(Pad + (int)(2 * _s), footY, ExpW - Pad * 2 - (int)(4 * _s), RowH),
                    Color.FromArgb(110, 185, 255),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }
        }

        private static void DrawSwitchIcon(Graphics g, Rectangle r, Color color)
        {
            int cy = r.Top + r.Height / 2;
            int gap = Math.Max(2, r.Height / 4);
            using (Pen pen = new Pen(color, 1.8f))
            using (AdjustableArrowCap cap = new AdjustableArrowCap(2.6f, 3.2f, true))
            {
                pen.CustomEndCap = cap;
                g.DrawLine(pen, r.Left, cy - gap, r.Right, cy - gap); // 上箭头 →
                g.DrawLine(pen, r.Right, cy + gap, r.Left, cy + gap); // 下箭头 ←
            }
        }

        // ---------------- 鼠标交互 ----------------

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _collapseTimer.Stop();
            // 延时展开：停稳一会儿才弹面板，抓取拖动不会触发
            if (_windows.Count > 0 && !_expanded)
                _expandTimer.Start();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _expandTimer.Stop();
            _hoverRow = -1;
            _hoverFooter = false;
            _dragArmed = false;
            _dragZoneHeader = false;
            Invalidate();
            _collapseTimer.Start();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            // 拖动：收起态按药丸任意位置拖；展开态按住标题栏拖
            if (e.Button == MouseButtons.Left && _dragArmed && (!_expanded || _dragZoneHeader))
            {
                int dx = Cursor.Position.X - _downPos.X;
                int dy = Cursor.Position.Y - _downPos.Y;
                if (!_dragMoved && Math.Abs(dx) + Math.Abs(dy) > 5) _dragMoved = true;
                if (_dragMoved)
                {
                    if (_expanded)
                    {
                        // 拖展开面板：整体移动，收起时药丸会落在面板停留处
                        Point lp = Location;
                        lp.X += dx; lp.Y += dy;
                        Location = lp;
                        Cursor = Cursors.SizeAll;
                    }
                    else
                    {
                        Point p = _collapsedLocation;
                        p.X += dx; p.Y += dy;
                        _collapsedLocation = p;
                        ClampCollapsedIntoScreen();
                    }
                    _downPos = Cursor.Position;
                }
                return;
            }

            int row = -1;
            bool footer = false;
            if (_expanded && e.Y >= HeadH)
            {
                int rowsBottom = HeadH + _windows.Count * RowH;
                if (e.Y < rowsBottom && _windows.Count > 0)
                    row = Math.Min((e.Y - HeadH) / RowH, _windows.Count - 1);
                else if (e.Y < rowsBottom + RowH)
                    footer = true;
            }
            bool header = _expanded && e.Y < HeadH;
            Cursor = header ? Cursors.SizeAll : ((row >= 0 || footer) ? Cursors.Hand : Cursors.Default);
            if (row != _hoverRow || footer != _hoverFooter)
            {
                _hoverRow = row;
                _hoverFooter = footer;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _expandTimer.Stop(); // 按下即视为拖动意图，不再弹出面板
                _downPos = Cursor.Position;
                _dragMoved = false;
                _dragArmed = true;
                _dragZoneHeader = _expanded && e.Y < HeadH;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;

            bool headerDrag = _dragArmed && _dragZoneHeader;
            bool headerClick = _dragArmed && _dragZoneHeader && !_dragMoved;
            _dragArmed = false;
            _dragZoneHeader = false;

            if (_dragMoved)
            {
                if (headerDrag)
                {
                    // 面板拖到哪，药丸收起后就落在哪
                    _collapsedLocation = Location;
                    ClampCollapsedIntoScreen();
                }
                SavePosition();
                return;
            }

            if (_expanded)
            {
                int rowsBottom = HeadH + _windows.Count * RowH;

                if (headerClick)
                {
                    // 单击展开面板标题栏（未拖动）：收起浮窗
                    _expanded = false;
                    UpdateSize();
                    Invalidate();
                    return;
                }

                // 直接按点击坐标定位，不依赖悬停状态（快速点击/触屏时 MouseMove 可能不触发）
                if (e.Y >= rowsBottom && e.Y < rowsBottom + RowH)
                {
                    // 「回到主机桌面」：最小化全部远程桌面窗口
                    Switcher.MinimizeAll(_windows);
                    _expanded = false;
                    UpdateSize();
                    Invalidate();
                    return;
                }

                int row = -1;
                if (_windows.Count > 0 && e.Y >= HeadH && e.Y < rowsBottom)
                    row = Math.Min((e.Y - HeadH) / RowH, _windows.Count - 1);
                if (row >= 0)
                {
                    IntPtr target = _windows[row].Handle;
                    _expanded = false;
                    UpdateSize();
                    Invalidate();
                    Switcher.SwitchTo(target);
                }
            }
            else if (_windows.Count > 0)
            {
                _expanded = true; // 点击收起态兜底展开
                UpdateSize();
                Invalidate();
            }
        }

        private void ClampCollapsedIntoScreen()
        {
            Rectangle wa = Screen.FromPoint(_collapsedLocation).WorkingArea;
            Point p = _collapsedLocation;
            if (p.X < wa.Left) p.X = wa.Left;
            if (p.Y < wa.Top) p.Y = wa.Top;
            if (p.X + PillW > wa.Right) p.X = wa.Right - PillW;
            if (p.Y + PillH > wa.Bottom) p.Y = wa.Bottom - PillH;
            _collapsedLocation = p;
            Location = p;
        }

        private void CollapseTimerTick(object sender, EventArgs e)
        {
            Rectangle b = new Rectangle(Location, Size);
            b.Inflate(2, 2);
            if (!b.Contains(Cursor.Position))
            {
                _collapseTimer.Stop();
                if (_expanded) { _expanded = false; UpdateSize(); Invalidate(); }
            }
        }
    }

    // ---------------------------------------------------------------------

    internal class TrayContext : ApplicationContext
    {
        private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        private const string AppId = "RemoteDesktopQuickSwitch";

        private readonly NotifyIcon _tray;
        private readonly FloatWindow _float;
        private readonly ToolStripMenuItem _toggleItem;
        private readonly ToolStripMenuItem _autoStartItem;

        public TrayContext(float scale)
        {
            _float = new FloatWindow(scale);
            _float.Show();

            _toggleItem = new ToolStripMenuItem("隐藏浮窗");
            _toggleItem.Click += delegate { ToggleFloat(); };

            _autoStartItem = new ToolStripMenuItem("开机自动启动");
            _autoStartItem.Checked = IsAutoStart();
            _autoStartItem.Click += delegate { ToggleAutoStart(); };

            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += delegate { ExitApp(); };

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add(_toggleItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_autoStartItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            _tray = new NotifyIcon();
            _tray.Icon = MakeIcon();
            _tray.Text = "远程桌面快切（悬停左上角浮窗可切换）";
            _tray.ContextMenuStrip = menu;
            _tray.Visible = true;
            _tray.DoubleClick += delegate { ToggleFloat(); };
        }

        private void ToggleFloat()
        {
            if (_float.Visible) { _float.Hide(); _toggleItem.Text = "显示浮窗"; }
            else { _float.Show(); _float.TopMost = true; _toggleItem.Text = "隐藏浮窗"; }
        }

        private static bool IsAutoStart()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
            {
                return key != null && key.GetValue(AppId) != null;
            }
        }

        private void ToggleAutoStart()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (IsAutoStart()) key.DeleteValue(AppId, false);
                    else key.SetValue(AppId, "\"" + Application.ExecutablePath + "\"");
                }
            }
            catch { }
            _autoStartItem.Checked = IsAutoStart();
        }

        private void ExitApp()
        {
            _tray.Visible = false;
            _tray.Dispose();
            _float.Dispose();
            Application.Exit();
        }

        private static Icon MakeIcon()
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(37, 99, 235)))
                    g.FillPath(b, RoundRect(new Rectangle(1, 1, 30, 30), 7));
                using (Pen pen = new Pen(Color.White, 2.6f))
                using (AdjustableArrowCap cap = new AdjustableArrowCap(3.2f, 4f, true))
                {
                    pen.CustomEndCap = cap;
                    g.DrawLine(pen, 7, 13, 25, 13); // 上箭头 →
                    g.DrawLine(pen, 25, 20, 7, 20); // 下箭头 ←
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        private static GraphicsPath RoundRect(Rectangle rect, int r)
        {
            GraphicsPath path = new GraphicsPath();
            path.AddArc(rect.X, rect.Y, r, r, 180, 90);
            path.AddArc(rect.Right - r, rect.Y, r, r, 270, 90);
            path.AddArc(rect.Right - r, rect.Bottom - r, r, r, 0, 90);
            path.AddArc(rect.X, rect.Bottom - r, r, r, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
