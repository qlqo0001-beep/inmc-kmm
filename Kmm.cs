// inmc-kmm — 키보드·마우스 입력을 기록해 블록 타임라인에서 다듬고 실행 키로 재생하는 프로그램 (G HUB 편집기 느낌).
// 단일 파일. Windows 에 기본으로 들어 있는 .NET Framework 4 의 csc.exe 로 빌드한다(build.bat). C# 5 문법만 쓴다.
using System;
using System.Reflection;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("inmc-kmm")]
[assembly: AssemblyProduct("inmc-kmm")]
[assembly: AssemblyDescription("inmc keyboard & mouse")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace InmcKmm
{
    // ===================== 자료 =====================

    public enum PresetType { Once = 0, WhileHeld = 1, Toggle = 2, Sequence = 3 }
    public enum BlockKind { KeyDown = 0, KeyUp = 1, MouseDown = 2, MouseUp = 3, MouseMove = 4, Wheel = 5, Text = 6, Delay = 7 }

    public class PathPoint { public int X, Y, Dt; }

    public class Block
    {
        public int Kind;
        public int Key;                 // 키: 가상키 / 마우스 버튼: 0 왼쪽 1 오른쪽 2 가운데 3 X1 4 X2 / 휠: 델타(+위 -아래)
        public int Scan;
        public bool Ext;                // 확장키(오른쪽 Ctrl·Alt, 방향키 등)
        public bool HasPos;             // 녹화한 클릭·휠은 좌표를 가짐. '동작'으로 넣은 클릭은 재생 때 커서 자리에서
        public int X, Y;
        public int Ms;                  // 지연
        public string Text;             // 텍스트
        public List<PathPoint> Points;  // 마우스 이동 경로(점마다 앞 점과의 간격 ms)

        public static Block Delay(int ms) { return new Block { Kind = (int)BlockKind.Delay, Ms = ms }; }
        [ScriptIgnore] public BlockKind K { get { return (BlockKind)Kind; } }
    }

    public class Preset
    {
        public string Name = "";
        public int Type;
        public int TriggerVk;           // 0 = 없음
        public int TriggerMouse;        // 0 = 없음, 아니면 버튼 번호 + 1 (3 가운데, 4 X1, 5 X2)
        public bool UseStdDelay;
        public int StdDelayMs = 50;
        public int JitterPct;           // 지연 랜덤 폭(%). 0 = 끔. 20 이면 각 지연을 ±20% 안에서 무작위로
        public int Color;               // 0 = 기본 빨강, 아니면 ARGB
        public string Scope = "";       // 소속 프로필 이름. "" = 평소
        public List<Block> Blocks = new List<Block>();
        [ScriptIgnore] public int SeqPos;   // 시퀀스 유형: 다음에 실행할 단위 (저장 안 함)

        [ScriptIgnore] public bool HasTrigger { get { return TriggerVk != 0 || TriggerMouse != 0; } }
        [ScriptIgnore] public string ScopeKey { get { return Scope ?? ""; } }
    }

    // 프로필: 게임(프로세스)마다 따로 두는 프리셋 묶음. 앞 창의 프로세스가 Process 와 같으면 자동으로 이 프로필이 작동한다
    public class Profile
    {
        public string Name = "";
        public string Process = "";     // 프로세스 이름(소문자, .exe 없이). "" 이면 직접 고를 때만 작동
    }

    public class Settings
    {
        public bool RecordMouseMove = true;   // 기록할 때 마우스 움직임도 담을지
        public int RecordKey = 0x78;          // 기록 시작/중지 단축키 (기본 F9). 0 = 없음
        public List<Profile> Profiles = new List<Profile>();
        public bool AutoProfile = true;       // 앞 창을 보고 프로필을 고를지
        public string ForcedProfile = "";     // AutoProfile 이 꺼져 있을 때 작동할 프로필 이름("" = 평소)
        public bool MinimizeToTray = true;    // 최소화하면 트레이 아이콘으로
        public int MinGapMs = 1;              // 주입 입력 사이 최소 간격(ms). 0 = 제한 없음. 빠른 반복 때 렉을 막는 상한
    }

    public class Store
    {
        public List<Preset> Presets = new List<Preset>();
        public Settings Settings = new Settings();
    }

    static class Json
    {
        static JavaScriptSerializer S() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }; }
        public static string Write(object o) { return S().Serialize(o); }
        public static T Read<T>(string s) { return S().Deserialize<T>(s); }
        public static Preset Clone(Preset m) { return Read<Preset>(Write(m)); }
    }

    // ===================== Win32 =====================

    static class Native
    {
        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandle(string lpModuleName);
        [DllImport("user32.dll")] public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        public const uint GA_ROOT = 2;
        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);
        // 고해상도 대기 타이머 (Windows 10 1803+): 1ms 폴링 없이 정확히 잔다
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr CreateWaitableTimerEx(IntPtr attr, string name, uint flags, uint access);
        [DllImport("kernel32.dll")] public static extern bool SetWaitableTimer(IntPtr h, ref long due, int period, IntPtr routine, IntPtr arg, bool resume);
        [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        public const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2, TIMER_ALL_ACCESS = 0x1F0003;

        public static readonly IntPtr MAGIC = (IntPtr)0x4D43;   // 우리가 쏜 입력의 표식 — 훅에서 녹화·트리거 대상에서 뺀다

        public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
        public const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
        public const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202,
            WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208,
            WM_MOUSEWHEEL = 0x20A, WM_XBUTTONDOWN = 0x20B, WM_XBUTTONUP = 0x20C;
        public const int LLKHF_EXTENDED = 0x01;
        public const int VK_ESCAPE = 0x1B, VK_PACKET = 0xE7;

        public const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_EXTENDEDKEY = 0x01, KEYEVENTF_KEYUP = 0x02, KEYEVENTF_UNICODE = 0x04;
        public const uint MOUSEEVENTF_LEFTDOWN = 0x02, MOUSEEVENTF_LEFTUP = 0x04, MOUSEEVENTF_RIGHTDOWN = 0x08, MOUSEEVENTF_RIGHTUP = 0x10,
            MOUSEEVENTF_MIDDLEDOWN = 0x20, MOUSEEVENTF_MIDDLEUP = 0x40, MOUSEEVENTF_XDOWN = 0x80, MOUSEEVENTF_XUP = 0x100, MOUSEEVENTF_WHEEL = 0x800;

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] public struct KBDLLHOOKSTRUCT { public int vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] public struct MSLLHOOKSTRUCT { public POINT pt; public int mouseData, flags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] public struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }
        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }
        [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public InputUnion u; }
    }

    // ===================== 입력 쏘기 =====================

    static class Player
    {
        static int Size { get { return Marshal.SizeOf(typeof(Native.INPUT)); } }

        public static void Key(int vk, int scan, bool ext, bool up)
        {
            var inp = new Native.INPUT[1];
            inp[0].type = Native.INPUT_KEYBOARD;
            inp[0].u.ki.dwExtraInfo = Native.MAGIC;
            if (vk == Native.VK_PACKET)
            {
                // 유니코드 문자 입력(텍스트 블록, 가상 키보드 등) — 스캔코드 자리에 문자가 들어 있다
                inp[0].u.ki.wScan = (ushort)scan;
                inp[0].u.ki.dwFlags = Native.KEYEVENTF_UNICODE | (up ? Native.KEYEVENTF_KEYUP : 0);
            }
            else
            {
                inp[0].u.ki.wVk = (ushort)vk;
                inp[0].u.ki.wScan = (ushort)scan;
                inp[0].u.ki.dwFlags = (up ? Native.KEYEVENTF_KEYUP : 0) | (ext ? Native.KEYEVENTF_EXTENDEDKEY : 0);
            }
            Native.SendInput(1, inp, Size);
        }

        public static void Unicode(char c, bool up) { Key(Native.VK_PACKET, c, false, up); }

        public static void Mouse(uint flags, uint data)
        {
            var inp = new Native.INPUT[1];
            inp[0].type = Native.INPUT_MOUSE;
            inp[0].u.mi.dwFlags = flags;
            inp[0].u.mi.mouseData = data;
            inp[0].u.mi.dwExtraInfo = Native.MAGIC;
            Native.SendInput(1, inp, Size);
        }

        public static void Button(int b, bool down) { Mouse(ButtonFlag(b, down), b == 3 ? 1u : b == 4 ? 2u : 0u); }
        public static void Wheel(int delta) { Mouse(Native.MOUSEEVENTF_WHEEL, unchecked((uint)delta)); }

        static uint ButtonFlag(int button, bool down)
        {
            switch (button)
            {
                case 0: return down ? Native.MOUSEEVENTF_LEFTDOWN : Native.MOUSEEVENTF_LEFTUP;
                case 1: return down ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_RIGHTUP;
                case 2: return down ? Native.MOUSEEVENTF_MIDDLEDOWN : Native.MOUSEEVENTF_MIDDLEUP;
                default: return down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP;
            }
        }
    }

    // ===================== 재생 =====================

    class Runner
    {
        Thread _th;
        volatile bool _stop;
        Preset _preset;
        readonly Random _rnd = new Random();
        IntPtr _timer;          // 고해상도 대기 타이머 (없으면 IntPtr.Zero → Sleep 폴링)
        long _nextInject;       // 다음 입력을 넣어도 되는 시각(Stopwatch 틱)
        public int MinGapMs = 1;   // 주입 입력 사이 최소 간격

        public bool IsRunning { get { var t = _th; return t != null && t.IsAlive; } }
        public Preset Current { get { return IsRunning ? _preset : null; } }

        // [from, to) 블록을 재생. loop 면 멈출 때까지 반복
        public void Start(Preset m, int from, int to, bool loop)
        {
            if (IsRunning) return;
            _preset = m;
            _stop = false;
            _th = new Thread(() => Run(m, from, to, loop)) { IsBackground = true };
            _th.Start();
        }

        public void Stop() { _stop = true; }
        public void StopAndWait() { _stop = true; var t = _th; if (t != null) t.Join(1500); }

        void Run(Preset m, int from, int to, bool loop)
        {
            // 고해상도 타이머가 있으면 시스템 타이머(timeBeginPeriod)를 건드리지 않는다 — 전체 부하를 줄임
            _timer = Native.CreateWaitableTimerEx(IntPtr.Zero, null, Native.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Native.TIMER_ALL_ACCESS);
            bool legacy = _timer == IntPtr.Zero;
            if (legacy) Native.timeBeginPeriod(1);
            _nextInject = 0;
            var held = new List<Block>();   // 멈출 때 눌린 채 남는 키·버튼이 없도록 추적
            try
            {
                do
                {
                    for (int i = from; i < to && i < m.Blocks.Count && !_stop; i++) Play(m, m.Blocks[i], held);
                } while (loop && !_stop);
            }
            finally
            {
                for (int i = held.Count - 1; i >= 0; i--) Release(held[i]);
                if (legacy) Native.timeEndPeriod(1);
                else { Native.CloseHandle(_timer); _timer = IntPtr.Zero; }
            }
        }

        // 주입 입력 사이를 최소 간격만큼 띄운다 (지연 0 블록으로 초당 수천 번 쏘아 렉이 나는 것을 막음)
        void Pace()
        {
            if (MinGapMs > 0) WaitUntil(_nextInject);
            _nextInject = Stopwatch.GetTimestamp() + MinGapMs * Stopwatch.Frequency / 1000;
        }

        void Play(Preset m, Block b, List<Block> held)
        {
            switch (b.K)
            {
                case BlockKind.Delay:
                    {
                        int ms = m.UseStdDelay ? m.StdDelayMs : b.Ms;
                        if (m.JitterPct > 0)   // ±n% 안에서 무작위로
                            ms = Math.Max(0, ms + (int)Math.Round(ms * (m.JitterPct / 100.0) * (_rnd.NextDouble() * 2 - 1)));
                        Wait(ms);
                    }
                    break;
                case BlockKind.KeyDown:
                    Pace();
                    Player.Key(b.Key, b.Scan, b.Ext, false);
                    held.Add(b);
                    break;
                case BlockKind.KeyUp:
                    held.RemoveAll(h => h.K == BlockKind.KeyDown && h.Key == b.Key);
                    Pace();
                    Player.Key(b.Key, b.Scan, b.Ext, true);
                    break;
                case BlockKind.MouseDown:
                    if (b.HasPos) Native.SetCursorPos(b.X, b.Y);
                    Pace();
                    Player.Button(b.Key, true);
                    held.Add(b);
                    break;
                case BlockKind.MouseUp:
                    held.RemoveAll(h => h.K == BlockKind.MouseDown && h.Key == b.Key);
                    if (b.HasPos) Native.SetCursorPos(b.X, b.Y);
                    Pace();
                    Player.Button(b.Key, false);
                    break;
                case BlockKind.MouseMove:
                    if (b.Points != null)
                        foreach (var p in b.Points)
                        {
                            Wait(p.Dt);
                            if (_stop) break;
                            Native.SetCursorPos(p.X, p.Y);
                        }
                    break;
                case BlockKind.Wheel:
                    if (b.HasPos) Native.SetCursorPos(b.X, b.Y);
                    Pace();
                    Player.Wheel(b.Key);
                    break;
                case BlockKind.Text:
                    if (b.Text != null)
                        foreach (char c in b.Text)
                        {
                            if (_stop) break;
                            Pace();
                            Player.Unicode(c, false);
                            Pace();
                            Player.Unicode(c, true);
                            Wait(3);
                        }
                    break;
            }
        }

        static void Release(Block b)
        {
            if (b.K == BlockKind.KeyDown) Player.Key(b.Key, b.Scan, b.Ext, true);
            else if (b.K == BlockKind.MouseDown) Player.Button(b.Key, false);
        }

        void Wait(int ms)
        {
            if (ms <= 0) return;
            WaitUntil(Stopwatch.GetTimestamp() + ms * Stopwatch.Frequency / 1000);
        }

        // due(Stopwatch 틱)까지 잔다. 멈춤 요청에는 50ms 안에 반응
        void WaitUntil(long due)
        {
            while (!_stop)
            {
                long remain = due - Stopwatch.GetTimestamp();
                if (remain <= 0) return;
                double remainMs = remain * 1000.0 / Stopwatch.Frequency;
                if (_timer != IntPtr.Zero)
                {
                    long due100ns = -(long)(Math.Min(remainMs, 50) * 10000);   // 음수 = 지금부터, 100ns 단위
                    if (Native.SetWaitableTimer(_timer, ref due100ns, 0, IntPtr.Zero, IntPtr.Zero, false))
                    {
                        Native.WaitForSingleObject(_timer, 100);
                        continue;
                    }
                }
                if (remainMs > 2) Thread.Sleep(1); else Thread.Yield();
            }
        }

        // 시퀀스 유형의 '한 단위': 키(누름~뗌) 하나, 클릭(누름~뗌) 하나, 그 밖의 블록 하나. 단위 앞의 지연은 건너뛴다.
        public static List<int[]> Units(List<Block> bs)
        {
            var units = new List<int[]>();
            int i = 0;
            while (i < bs.Count)
            {
                var b = bs[i];
                if (b.K == BlockKind.Delay) { i++; continue; }
                int end = i;
                if (b.K == BlockKind.KeyDown || b.K == BlockKind.MouseDown)
                {
                    var upKind = b.K == BlockKind.KeyDown ? BlockKind.KeyUp : BlockKind.MouseUp;
                    for (int j = i + 1; j < bs.Count; j++)
                        if (bs[j].K == upKind && bs[j].Key == b.Key) { end = j; break; }
                }
                units.Add(new[] { i, end + 1 });
                i = end + 1;
            }
            return units;
        }
    }

    // ===================== 그리기 도우미 =====================

    static class UI
    {
        public static readonly Color Back = Color.Black;
        public static readonly Color Panel = Color.FromArgb(26, 26, 26);
        public static readonly Color PanelHover = Color.FromArgb(44, 44, 44);
        public static readonly Color Fg = Color.White;
        public static readonly Color Dim = Color.FromArgb(150, 150, 150);
        public static readonly Color Line = Color.FromArgb(64, 64, 64);
        public static readonly Color Red = Color.FromArgb(139, 26, 44);
        public static readonly Color Yellow = Color.FromArgb(138, 106, 18);
        public static readonly Color Orange = Color.FromArgb(138, 74, 15);
        public static readonly Color Slate = Color.FromArgb(67, 74, 107);
        public static readonly Color Blue = Color.FromArgb(43, 79, 122);
        public static readonly Color Violet = Color.FromArgb(86, 44, 126);   // 마우스 버튼 블록 (키 블록과 구별)
        public static readonly Color Gray = Color.FromArgb(58, 58, 58);
        public static readonly Color Teal = Color.FromArgb(28, 79, 110);
        public static readonly Color TealHover = Color.FromArgb(36, 100, 140);
        public static readonly Color Accent = Color.FromArgb(58, 123, 213);

        public static readonly Font Title = new Font("맑은 고딕", 20F, FontStyle.Bold);
        public static readonly Font Head = new Font("맑은 고딕", 12F, FontStyle.Bold);
        public static readonly Font Body = new Font("맑은 고딕", 9.5F);
        public static readonly Font Small = new Font("맑은 고딕", 8F);
        public static readonly Font BlockFont = new Font("맑은 고딕", 9F, FontStyle.Bold);
        public static readonly Font Big = new Font("맑은 고딕", 14F);
        public static readonly Font Plus = new Font("맑은 고딕", 16F);
        public static readonly Font Arrow = new Font("맑은 고딕", 20F);

        public const TextFormatFlags Center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
        public const TextFormatFlags Left = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
        public const TextFormatFlags Right = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;

        public static void Draw(Graphics g, string s, Font f, Color c, Rectangle r, TextFormatFlags flags)
        {
            TextRenderer.DrawText(g, s, f, r, c, flags | TextFormatFlags.NoPadding);
        }

        public static Size Measure(string s, Font f)
        {
            return TextRenderer.MeasureText(s, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }

        public static GraphicsPath Round(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Rectangle r, Color c, int rad)
        {
            using (var p = Round(r, rad)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
        }

        public static void DrawRound(Graphics g, Rectangle r, Color c, int rad, float width)
        {
            using (var p = Round(r, rad)) using (var pen = new Pen(c, width)) g.DrawPath(pen, p);
        }

        // 유형 아이콘: → / ↻ / 토글 / 겹친 네모
        public static void TypeIcon(Graphics g, Rectangle r, PresetType t, Color c)
        {
            float w = r.Width, h = r.Height, cx = r.X + w / 2, cy = r.Y + h / 2;
            float pw = Math.Max(1.5f, w / 11f);
            using (var pen = new Pen(c, pw) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var br = new SolidBrush(c))
            {
                switch (t)
                {
                    case PresetType.Once:
                        g.DrawLine(pen, r.X + w * 0.15f, cy, r.X + w * 0.85f, cy);
                        g.DrawLine(pen, r.X + w * 0.6f, cy - h * 0.25f, r.X + w * 0.85f, cy);
                        g.DrawLine(pen, r.X + w * 0.6f, cy + h * 0.25f, r.X + w * 0.85f, cy);
                        break;
                    case PresetType.WhileHeld:
                        {
                            var a = RectangleF.Inflate(new RectangleF(r.X, r.Y, w, h), -w * 0.2f, -h * 0.2f);
                            g.DrawArc(pen, a, -40, 300);
                            // 호 끝(-40도 자리)에 화살촉
                            float rad = (float)(-40 * Math.PI / 180), rx = a.Width / 2, ry = a.Height / 2;
                            float ex = cx + rx * (float)Math.Cos(rad), ey = cy + ry * (float)Math.Sin(rad);
                            g.DrawLine(pen, ex, ey, ex - w * 0.22f, ey - h * 0.02f);
                            g.DrawLine(pen, ex, ey, ex - w * 0.05f, ey + h * 0.22f);
                        }
                        break;
                    case PresetType.Toggle:
                        {
                            var pill = new Rectangle((int)(r.X + w * 0.12f), (int)(r.Y + h * 0.3f), (int)(w * 0.76f), (int)(h * 0.4f));
                            using (var path = Round(pill, pill.Height / 2)) g.DrawPath(pen, path);
                            int d = (int)(pill.Height * 0.62f);
                            g.FillEllipse(br, pill.Right - d - pill.Height * 0.19f, pill.Y + (pill.Height - d) / 2f, d, d);
                        }
                        break;
                    case PresetType.Sequence:
                        {
                            float s = w * 0.42f;
                            g.FillRectangle(br, r.X + w * 0.36f, r.Y + h * 0.2f, s, s);
                            using (var back = new SolidBrush(Back)) g.FillRectangle(back, r.X + w * 0.2f, r.Y + h * 0.36f, s, s);
                            g.DrawRectangle(pen, r.X + w * 0.2f, r.Y + h * 0.36f, s, s);
                        }
                        break;
                }
            }
        }

        public static string TypeName(PresetType t)
        {
            switch (t)
            {
                case PresetType.Once: return "반복 없음";
                case PresetType.WhileHeld: return "누르고 있는 동안 반복";
                case PresetType.Toggle: return "설정/해제";
                default: return "시퀀스";
            }
        }

        public static string KeyName(int vk)
        {
            switch (vk)
            {
                case 0x15: return "한/영";
                case 0x19: return "한자";
                case 0x0D: return "Enter";
                case 0x08: return "Backspace";
                case 0x1B: return "Esc";
                case 0x20: return "Space";
                case 0xA0: return "LShift";
                case 0xA1: return "RShift";
                case 0xA2: return "LCtrl";
                case 0xA3: return "RCtrl";
                case 0xA4: return "LAlt";
                case 0xA5: return "RAlt";
                case 0xBD: return "-";
                case 0xBB: return "=";
                case 0xDB: return "[";
                case 0xDD: return "]";
                case 0xDC: return "\\";
                case 0xBA: return ";";
                case 0xDE: return "'";
                case 0xBC: return ",";
                case 0xBE: return ".";
                case 0xBF: return "/";
                case 0xC0: return "`";
            }
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString(); // D0~D9 대신 0~9
            return ((Keys)vk).ToString();
        }

        public static string MouseName(int b)
        {
            switch (b)
            {
                case 0: return "왼쪽";
                case 1: return "오른쪽";
                case 2: return "가운데";
                case 3: return "X1";
                default: return "X2";
            }
        }

        public static string TriggerName(Preset m)
        {
            if (m.TriggerVk != 0) return KeyName(m.TriggerVk);
            if (m.TriggerMouse != 0) return "마우스 " + MouseName(m.TriggerMouse - 1);
            return "없음";
        }

        public static ContextMenuStrip DarkMenu()
        {
            return new ContextMenuStrip { Renderer = new ToolStripProfessionalRenderer(new DarkColors()), ForeColor = Fg, Font = Body, ShowImageMargin = false };
        }

        class DarkColors : ProfessionalColorTable
        {
            public override Color MenuItemSelected { get { return PanelHover; } }
            public override Color MenuItemBorder { get { return PanelHover; } }
            public override Color MenuBorder { get { return Line; } }
            public override Color ToolStripDropDownBackground { get { return Panel; } }
            public override Color ImageMarginGradientBegin { get { return Panel; } }
            public override Color ImageMarginGradientMiddle { get { return Panel; } }
            public override Color ImageMarginGradientEnd { get { return Panel; } }
        }
    }

    // 검은 바탕에 직접 그리는 화면의 공통 바탕
    class Screen : Control
    {
        public Screen()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = UI.Back;
            Dock = DockStyle.Fill;
        }
    }

    // 검은 작은 창: 글 입력 / 고르기 / 확인
    class DarkDialog : Form
    {
        DarkDialog(string title, int w, int h)
        {
            FormBorderStyle = FormBorderStyle.None;
            BackColor = UI.Back;
            ForeColor = UI.Fg;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = UI.Body;
            KeyPreview = true;
            int tw = UI.Measure(title, UI.Head).Width + 40;
            ClientSize = new Size(Math.Max(w, tw), h);
            Controls.Add(new Label { Text = title, Font = UI.Head, ForeColor = UI.Fg, AutoSize = true, Location = new Point(20, 16) });
            Paint += delegate(object s, PaintEventArgs e) { using (var p = new Pen(UI.Line)) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1); };
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel; };
        }

        Button MakeButton(string text, Color back, int x, int y, int w)
        {
            var b = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = UI.Fg, Bounds = new Rectangle(x, y, w, 30), Font = UI.Body };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        public static string Input(IWin32Window owner, string title, string initial, bool numeric)
        {
            var d = new DarkDialog(title, 380, 144);
            int w = d.ClientSize.Width;
            var box = new TextBox { Text = initial, Font = UI.Big, BackColor = UI.Panel, ForeColor = UI.Fg, BorderStyle = BorderStyle.FixedSingle, Bounds = new Rectangle(20, 50, w - 40, 30) };
            if (numeric) box.KeyPress += delegate(object s, KeyPressEventArgs e) { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };
            var ok = d.MakeButton("확인", UI.Teal, w - 180, 98, 76);
            ok.DialogResult = DialogResult.OK;
            var cancel = d.MakeButton("취소", UI.Gray, w - 96, 98, 76);
            cancel.DialogResult = DialogResult.Cancel;
            d.Controls.Add(box); d.Controls.Add(ok); d.Controls.Add(cancel);
            d.AcceptButton = ok; d.CancelButton = cancel;
            d.Shown += delegate { box.Focus(); box.SelectAll(); };
            string result = d.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
            d.Dispose();
            return result;
        }

        public static int Choose(IWin32Window owner, string title, string[] options, Color[] colors)
        {
            var d = new DarkDialog(title, 320, 50 + options.Length * 38 + 14);
            int w = d.ClientSize.Width;
            int picked = -1;
            for (int i = 0; i < options.Length; i++)
            {
                int idx = i;
                var b = d.MakeButton(options[i], colors == null ? UI.Panel : colors[i], 20, 50 + i * 38, w - 40);
                b.Click += delegate { picked = idx; d.DialogResult = DialogResult.OK; };
                d.Controls.Add(b);
            }
            d.ShowDialog(owner);
            d.Dispose();
            return picked;
        }

        public static bool Confirm(IWin32Window owner, string text)
        {
            return Choose(owner, text, new[] { "예", "아니요" }, new[] { UI.Teal, UI.Gray }) == 0;
        }

        public static void Info(IWin32Window owner, string text)
        {
            Choose(owner, text, new[] { "확인" }, new[] { UI.Teal });
        }
    }

    // ===================== 화면 1: 목록 =====================

    class ListScreen : Screen
    {
        readonly MainForm _app;
        readonly List<Rectangle> _rows = new List<Rectangle>();
        readonly List<Preset> _shown = new List<Preset>();      // 고른 탭의 프리셋만
        readonly List<Rectangle> _tabs = new List<Rectangle>(); // 0 평소 · 1.. 프로필 · 마지막 = 프로필 추가
        Rectangle _newBtn, _modeBtn, _chkStart, _chkTray, _gapBtn;
        string _selScope = "";   // 고른 탭 ("" = 평소)
        int _hover = -1;         // 행 번호 · -2 새 프리셋 · -3 작동 프로필 · -4 부팅 자동 실행 · -5 트레이 · -6 입력 최소 간격 · 100+n 탭
        string _lastStatus = "";

        public ListScreen(MainForm app)
        {
            _app = app;
            var tick = new System.Windows.Forms.Timer { Interval = 250 };
            tick.Tick += delegate { string s = StatusText(); if (s != _lastStatus) { _lastStatus = s; Invalidate(); } };
            tick.Start();
        }

        public string SelectedScope { get { return _selScope; } }

        static string ScopeLabel(string scope) { return scope == "" ? "평소" : scope; }

        string StatusText()
        {
            var m = _app.Runner.Current;
            if (m != null) return "▶  실행 중: " + m.Name;
            string fg = _app.ForegroundName, scope = _app.ActiveScope;
            string how = _app.Settings.AutoProfile ? "앞 창 " + (fg == "" ? "-" : fg + ".exe") + "  →  " : "직접 고름  →  ";
            return how + "'" + ScopeLabel(scope) + "' 프리셋이 실행 키에 반응합니다.";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int x = 40, w = Width - 80;
            var st = _app.Settings;
            if (_selScope != "" && _app.FindProfile(_selScope) == null) _selScope = "";   // 지워진 프로필 탭

            UI.Draw(g, "inmc-kmm", UI.Title, UI.Fg, new Rectangle(x, 20, 300, 40), UI.Left);
            // 작동 프로필 고르기
            _modeBtn = new Rectangle(Width - 40 - 300, 24, 300, 30);
            UI.FillRound(g, _modeBtn, _hover == -3 ? UI.PanelHover : UI.Panel, 3);
            UI.DrawRound(g, _modeBtn, _hover == -3 ? UI.Dim : UI.Line, 3, 1f);
            UI.Draw(g, "작동 프로필:  " + _app.ProfileModeText + "  ▾", UI.Body, UI.Fg, Rectangle.Inflate(_modeBtn, -10, 0), UI.Left);

            string status = StatusText();
            UI.Draw(g, status, UI.Body, _app.Runner.Current != null ? UI.Accent : UI.Dim, new Rectangle(x, 64, w, 20), UI.Left);

            // 탭: 평소 · 프로필들 · + 프로필
            _tabs.Clear();
            int tx = x, ty = 96;
            var names = new List<string> { "" };
            foreach (var p in st.Profiles) names.Add(p.Name);
            for (int i = 0; i <= names.Count; i++)
            {
                bool add = i == names.Count;
                string label = add ? "+  프로필" : (names[i] == _app.ActiveScope ? "●  " : "") + ScopeLabel(names[i]);
                int tw = UI.Measure(label, UI.Body).Width + 26;
                var r = new Rectangle(tx, ty, tw, 30);
                _tabs.Add(r);
                bool sel = !add && names[i] == _selScope;
                UI.FillRound(g, r, sel ? UI.PanelHover : (_hover == 100 + i ? UI.PanelHover : UI.Panel), 3);
                if (sel) UI.DrawRound(g, r, UI.Accent, 3, 1.5f);
                else if (add) UI.DrawRound(g, r, _hover == 100 + i ? UI.Fg : UI.Line, 3, 1f);
                UI.Draw(g, label, UI.Body, add ? (_hover == 100 + i ? UI.Fg : UI.Dim) : UI.Fg, r, UI.Center);
                tx += tw + 8;
            }
            if (_selScope != "")
            {
                var pf = _app.FindProfile(_selScope);
                string proc = pf == null || string.IsNullOrEmpty(pf.Process) ? "감지할 프로세스 없음 — 직접 고를 때만 작동 (탭 오른쪽 클릭으로 정하기)" : "앞 창이 " + pf.Process + ".exe 일 때 자동 작동";
                UI.Draw(g, proc, UI.Small, UI.Dim, new Rectangle(x, ty + 34, w, 16), UI.Left);
            }

            // 프리셋 줄
            _rows.Clear();
            _shown.Clear();
            foreach (var m in _app.Presets) if (m.ScopeKey == _selScope) _shown.Add(m);
            int y = 156;
            for (int i = 0; i < _shown.Count; i++)
            {
                var r = new Rectangle(x, y, w, 56);
                _rows.Add(r);
                var m = _shown[i];
                UI.FillRound(g, r, _hover == i ? UI.PanelHover : UI.Panel, 4);
                UI.TypeIcon(g, new Rectangle(r.X + 16, r.Y + 14, 28, 28), (PresetType)m.Type, UI.Fg);
                UI.Draw(g, m.Name, UI.Head, UI.Fg, new Rectangle(r.X + 60, r.Y + 8, w - 340, 24), UI.Left);
                UI.Draw(g, UI.TypeName((PresetType)m.Type) + "  ·  블록 " + m.Blocks.Count + "개", UI.Small, UI.Dim, new Rectangle(r.X + 60, r.Y + 33, w - 340, 16), UI.Left);
                UI.Draw(g, m.HasTrigger ? "실행 키   " + UI.TriggerName(m) : "실행 키 없음", UI.Body, m.HasTrigger ? UI.Fg : UI.Dim, new Rectangle(r.Right - 270, r.Y, 250, 56), UI.Right);
                y += 64;
            }
            _newBtn = new Rectangle(x, y, w, 56);
            UI.DrawRound(g, _newBtn, _hover == -2 ? UI.Fg : UI.Line, 4, 1f);
            UI.Draw(g, "+    새 프리셋  (" + ScopeLabel(_selScope) + ")", UI.Head, _hover == -2 ? UI.Fg : UI.Dim, _newBtn, UI.Center);
            if (_shown.Count == 0)
                UI.Draw(g, "이 프로필에는 아직 프리셋이 없습니다. 새 프리셋을 만들거나, 다른 탭의 프리셋을 오른쪽 클릭해 옮겨 오세요.", UI.Body, UI.Dim, new Rectangle(x, y + 72, w, 20), UI.Center);

            // 아래 설정 줄
            int sy = Height - 44;
            _chkStart = new Rectangle(x, sy, 260, 24);
            _chkTray = new Rectangle(x + 280, sy, 220, 24);
            DrawCheck(g, _chkStart, _app.StartWithWindows, "부팅 때 자동 실행 (트레이로 시작)", _hover == -4);
            DrawCheck(g, _chkTray, st.MinimizeToTray, "최소화하면 트레이 아이콘으로", _hover == -5);
            _gapBtn = new Rectangle(x + 520, sy, 300, 24);
            UI.Draw(g, "입력 최소 간격  " + (st.MinGapMs > 0 ? st.MinGapMs + " ms" : "제한 없음") + "   (빠른 반복 때 렉 완화)", UI.Body, _hover == -6 ? UI.Fg : UI.Dim, _gapBtn, UI.Left);
        }

        static void DrawCheck(Graphics g, Rectangle r, bool on, string text, bool hover)
        {
            var box = new Rectangle(r.X, r.Y + 5, 14, 14);
            using (var p = new Pen(hover ? UI.Fg : UI.Dim)) g.DrawRectangle(p, box);
            if (on) using (var b = new SolidBrush(UI.Fg)) g.FillRectangle(b, Rectangle.Inflate(box, -3, -3));
            UI.Draw(g, text, UI.Body, hover ? UI.Fg : UI.Dim, new Rectangle(box.Right + 8, r.Y, r.Width - 22, r.Height), UI.Left);
        }

        int Hit(Point p)
        {
            for (int i = 0; i < _rows.Count; i++) if (_rows[i].Contains(p)) return i;
            for (int i = 0; i < _tabs.Count; i++) if (_tabs[i].Contains(p)) return 100 + i;
            if (_newBtn.Contains(p)) return -2;
            if (_modeBtn.Contains(p)) return -3;
            if (_chkStart.Contains(p)) return -4;
            if (_chkTray.Contains(p)) return -5;
            if (_gapBtn.Contains(p)) return -6;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = Hit(e.Location);
            if (h != _hover) { _hover = h; Cursor = h == -1 ? Cursors.Default : Cursors.Hand; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int h = Hit(e.Location);
            bool left = e.Button == MouseButtons.Left, right = e.Button == MouseButtons.Right;
            if (h >= 100)
            {
                int t = h - 100;
                var profiles = _app.Settings.Profiles;
                if (t == profiles.Count + 1) { if (left) _app.AddProfile(); Invalidate(); return; }   // + 프로필
                string scope = t == 0 ? "" : profiles[t - 1].Name;
                if (left) { _selScope = scope; Invalidate(); }
                else if (right && t > 0)
                {
                    var pf = profiles[t - 1];
                    var menu = UI.DarkMenu();
                    menu.Items.Add("감지할 프로세스 바꾸기", null, delegate { _app.SetProfileProcess(pf); Invalidate(); });
                    menu.Items.Add("이름 바꾸기", null, delegate { _app.RenameProfile(pf); Invalidate(); });
                    menu.Items.Add("프로필 지우기 (프리셋은 평소로)", null, delegate { _app.RemoveProfile(pf); Invalidate(); });
                    menu.Show(this, e.Location);
                }
                return;
            }
            switch (h)
            {
                case -2: if (left) _app.NewPreset(_selScope); return;
                case -3: if (left) { _app.ChooseActiveProfile(); Invalidate(); } return;
                case -4: if (left) { _app.SetStartWithWindows(!_app.StartWithWindows); Invalidate(); } return;
                case -5: if (left) { _app.Settings.MinimizeToTray = !_app.Settings.MinimizeToTray; _app.SaveSettings(); Invalidate(); } return;
                case -6:
                    if (left)
                    {
                        string s = DarkDialog.Input(FindForm(), "입력 사이 최소 간격 (ms)  —  0 = 제한 없음, 1 = 초당 최대 1000번", _app.Settings.MinGapMs.ToString(), true);
                        int v;
                        if (s != null && int.TryParse(s, out v) && v >= 0) { _app.Settings.MinGapMs = Math.Min(1000, v); _app.SaveSettings(); Invalidate(); }
                    }
                    return;
            }
            if (h < 0) return;
            var m = _shown[h];
            if (left) _app.OpenEditor(m);
            else if (right)
            {
                var menu = UI.DarkMenu();
                var move = new ToolStripMenuItem("다른 프로필로 옮기기");
                if (_selScope != "") move.DropDownItems.Add("평소", null, delegate { _app.MovePreset(m, ""); Invalidate(); });
                foreach (var pf in _app.Settings.Profiles)
                {
                    if (pf.Name == _selScope) continue;
                    var target = pf.Name;
                    move.DropDownItems.Add(target, null, delegate { _app.MovePreset(m, target); Invalidate(); });
                }
                move.Enabled = move.DropDownItems.Count > 0;
                menu.Items.Add(move);
                menu.Items.Add("삭제", null, delegate { _app.DeletePreset(m); });
                menu.Show(this, e.Location);
            }
        }
    }

    // ===================== 화면 2: 이름 =====================

    class NameScreen : Screen
    {
        readonly MainForm _app;
        readonly TextBox _box;

        public NameScreen(MainForm app)
        {
            _app = app;
            _box = new TextBox { Font = new Font("맑은 고딕", 16F), BackColor = UI.Back, ForeColor = UI.Fg, BorderStyle = BorderStyle.None, TextAlign = HorizontalAlignment.Center, Width = 420 };
            _box.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    string n = _box.Text.Trim();
                    if (n != "") _app.NameEntered(n);
                }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; _app.ShowList(); }
            };
            Controls.Add(_box);
        }

        public void Reset() { _box.Text = ""; }
        public void FocusBox() { _box.Focus(); }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            _box.Location = new Point((Width - _box.Width) / 2, Height / 2 - 4);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            UI.Draw(g, "프리셋 이름을 정해주세요 :)", UI.Title, UI.Fg, new Rectangle(0, Height / 2 - 110, Width, 50), UI.Center);
            using (var p = new Pen(UI.Line)) g.DrawLine(p, _box.Left, _box.Bottom + 6, _box.Right, _box.Bottom + 6);
            UI.Draw(g, "이 프리셋을 만들려면 ENTER 키를 누르십시오.", UI.Body, UI.Dim, new Rectangle(0, _box.Bottom + 34, Width, 20), UI.Center);
        }
    }

    // ===================== 화면 3: 유형 =====================

    class TypeScreen : Screen
    {
        readonly MainForm _app;
        readonly Rectangle[] _tiles = new Rectangle[4];
        string _name = "";
        int _hover = -1;

        public TypeScreen(MainForm app) { _app = app; }

        public void Set(string name) { _name = name; _hover = -1; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            UI.Draw(g, _name, UI.Title, UI.Fg, new Rectangle(0, 36, Width, 50), UI.Center);
            UI.Draw(g, "작성할 프리셋 유형 선택", UI.Body, UI.Fg, new Rectangle(0, Height / 2 - 112, Width, 24), UI.Center);
            int size = 120, gap = 48, total = size * 4 + gap * 3, x0 = (Width - total) / 2, y0 = Height / 2 - 60;
            for (int i = 0; i < 4; i++)
            {
                var r = new Rectangle(x0 + i * (size + gap), y0, size, size);
                _tiles[i] = r;
                UI.FillRound(g, r, _hover == i ? UI.PanelHover : UI.Panel, 4);
                UI.TypeIcon(g, new Rectangle(r.X + 35, r.Y + 35, 50, 50), (PresetType)i, UI.Fg);
                TextRenderer.DrawText(g, UI.TypeName((PresetType)i), UI.Body, new Rectangle(r.X - 10, r.Bottom + 16, size + 20, 44), UI.Fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            }
        }

        int Hit(Point p)
        {
            for (int i = 0; i < 4; i++) if (_tiles[i].Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = Hit(e.Location);
            if (h != _hover) { _hover = h; Cursor = h == -1 ? Cursors.Default : Cursors.Hand; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int h = Hit(e.Location);
            if (h >= 0 && e.Button == MouseButtons.Left) _app.TypeChosen((PresetType)h);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) _app.ShowList();
        }
    }

    // ===================== 블록 타임라인 =====================

    class Timeline : Control
    {
        const int RowH = 84, Gap = 14, Sq = 36;

        Preset _m;
        readonly List<Block> _items = new List<Block>();   // 그릴 목록 = 블록 + (기록 중이면) 미리보기
        List<Block> _preview;                              // 기록 중 실시간 미리보기. null 이면 기록 중 아님
        int _previewAt;
        readonly List<Rectangle> _rects = new List<Rectangle>();
        Rectangle _plus;
        int _sel = -1, _scroll, _contentH;
        TextBox _edit;
        int _editIdx = -1;

        public event Action<Rectangle> PlusClicked;   // '+' 블록의 자리(이 컨트롤 기준, 스크롤 반영)
        public event Action Changed;
        public event Action StdDelayClicked;          // 표준 지연이 켜진 채 지연 블록을 누름 → 표준값을 고치게

        public Timeline()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = UI.Back;
        }

        public int InsertIndex
        {
            get { return _m == null ? 0 : (_sel >= 0 && _sel < _m.Blocks.Count ? _sel + 1 : _m.Blocks.Count); }
        }

        public void SetPreset(Preset m)
        {
            EndEdit(false);
            _m = m; _sel = -1; _scroll = 0; _preview = null;
            Relayout();
            Invalidate();
        }

        public void Insert(List<Block> blocks)
        {
            if (_m == null || blocks.Count == 0) return;
            int at = InsertIndex;
            _m.Blocks.InsertRange(at, blocks);
            _sel = at + blocks.Count - 1;
            Relayout();
            ScrollTo(_sel);
            Invalidate();
            if (Changed != null) Changed();
        }

        // ---- 기록 중 실시간 미리보기 ----

        public void BeginPreview()
        {
            EndEdit(false);
            _previewAt = InsertIndex;
            _preview = new List<Block>();
            Relayout();
            Invalidate();
        }

        public void SetPreview(List<Block> blocks)
        {
            if (_preview == null) return;
            _preview = blocks ?? new List<Block>();
            Relayout();
            if (_preview.Count > 0) ScrollTo(_previewAt + _preview.Count - 1);   // 방금 들어온 블록이 보이게
            Invalidate();
        }

        public void EndPreview()
        {
            _preview = null;
            Relayout();
            Invalidate();
        }

        void Rebuild()
        {
            _items.Clear();
            if (_m == null) return;
            if (_preview == null) { _items.AddRange(_m.Blocks); return; }
            int at = Math.Min(_previewAt, _m.Blocks.Count);
            _items.AddRange(_m.Blocks.GetRange(0, at));
            _items.AddRange(_preview);
            _items.AddRange(_m.Blocks.GetRange(at, _m.Blocks.Count - at));
        }

        Color KeyColor { get { return _m != null && _m.Color != 0 ? Color.FromArgb(_m.Color) : UI.Red; } }
        bool StdOn { get { return _m != null && _m.UseStdDelay; } }
        int ShownMs(Block b) { return StdOn ? _m.StdDelayMs : b.Ms; }   // 표준 지연이 켜져 있으면 모든 지연을 표준값으로 보여 준다

        public void RefreshBlocks() { Relayout(); Invalidate(); }

        static string BlockLabel(Block b)
        {
            if (b.K == BlockKind.MouseDown || b.K == BlockKind.MouseUp)
                return "마우스 " + new[] { "L", "R", "M", "X1", "X2" }[Math.Max(0, Math.Min(4, b.Key))];
            if (b.Key == Native.VK_PACKET) return ((char)b.Scan).ToString();
            return UI.KeyName(b.Key);
        }

        Size Measure(Block b)
        {
            switch (b.K)
            {
                case BlockKind.Delay: return new Size(Math.Max(44, UI.Measure(ShownMs(b).ToString(), UI.Head).Width + 10), Sq);
                case BlockKind.Text: return new Size(Math.Min(180, UI.Measure(b.Text ?? "", UI.BlockFont).Width + 20), Sq);
                case BlockKind.MouseMove: return new Size(100, Sq);
                case BlockKind.Wheel: return new Size(52, Sq);
                case BlockKind.MouseDown:
                case BlockKind.MouseUp: return new Size(Sq, Sq);
                default: return new Size(Math.Max(Sq, UI.Measure(BlockLabel(b), UI.BlockFont).Width + 14), Sq);
            }
        }

        void Relayout()
        {
            Rebuild();
            _rects.Clear();
            int x = 0, row = 0, maxW = Math.Max(100, Width - 8);
            foreach (var b in _items)
            {
                var s = Measure(b);
                if (x > 0 && x + s.Width > maxW) { x = 0; row++; }
                _rects.Add(new Rectangle(x, row * RowH + 24, s.Width, s.Height));
                x += s.Width + Gap;
            }
            if (x > 0 && x + 44 > maxW) { x = 0; row++; }
            _plus = new Rectangle(x, row * RowH + 20, 44, 44);
            _contentH = (row + 1) * RowH;
            _scroll = Math.Max(0, Math.Min(_scroll, _contentH - Height));
        }

        void ScrollTo(int idx)
        {
            if (idx < 0 || idx >= _rects.Count) return;
            var r = _rects[idx];
            if (r.Bottom + 20 - _scroll > Height) _scroll = Math.Max(0, r.Bottom + 20 - Height);
            if (r.Y - 20 - _scroll < 0) _scroll = Math.Max(0, r.Y - 20);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); EndEdit(true); Relayout(); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // 글자는 GDI(TextRenderer)라 Graphics 변환을 무시하므로, 변환 대신 사각형을 스크롤만큼 직접 옮긴다
            for (int i = 0; i < _items.Count && i < _rects.Count; i++)
            {
                var r = _rects[i];
                r.Offset(0, -_scroll);
                if (r.Bottom < -20 || r.Y > Height + 20) continue;
                DrawBlock(g, _items[i], r, _preview == null && i == _sel);
            }
            var plus = _plus;
            plus.Offset(0, -_scroll);
            UI.FillRound(g, plus, UI.Gray, 3);
            UI.Draw(g, "+", UI.Plus, UI.Fg, plus, UI.Center);
            if (_m == null || _m.Blocks.Count == 0)
                UI.Draw(g, "지금 시작", UI.Head, UI.Fg, new Rectangle(plus.Right + 14, plus.Y, 200, plus.Height), UI.Left);
        }

        void DrawBlock(Graphics g, Block b, Rectangle r, bool sel)
        {
            switch (b.K)
            {
                case BlockKind.Delay:
                    {
                        var c = StdOn ? UI.Accent : UI.Fg;   // 표준 지연으로 바뀐 값은 파란색
                        UI.Draw(g, ShownMs(b).ToString(), UI.Head, c, new Rectangle(r.X, r.Y - 2, r.Width, 20), UI.Center);
                        using (var p = new Pen(c)) g.DrawLine(p, r.X + 4, r.Y + 20, r.Right - 4, r.Y + 20);
                        UI.Draw(g, "ms", UI.Small, c, new Rectangle(r.X, r.Y + 21, r.Width, 15), UI.Center);
                    }
                    break;
                case BlockKind.Text:
                    UI.FillRound(g, r, UI.Yellow, 4);
                    UI.Draw(g, b.Text ?? "", UI.BlockFont, UI.Fg, Rectangle.Inflate(r, -6, 0), UI.Center | TextFormatFlags.EndEllipsis);
                    break;
                case BlockKind.MouseMove:
                    {
                        UI.FillRound(g, r, UI.Blue, 4);
                        int n = 0; long ms = 0;
                        if (b.Points != null) { n = b.Points.Count; foreach (var p in b.Points) ms += p.Dt; }
                        UI.Draw(g, "이동 " + n + "점", UI.BlockFont, UI.Fg, new Rectangle(r.X, r.Y + 2, r.Width, 18), UI.Center);
                        UI.Draw(g, (ms / 1000.0).ToString("0.0") + "s", UI.Small, UI.Fg, new Rectangle(r.X, r.Y + 19, r.Width, 15), UI.Center);
                    }
                    break;
                case BlockKind.Wheel:
                    UI.FillRound(g, r, UI.Orange, 4);
                    UI.Draw(g, "휠 " + (b.Key > 0 ? "▲" : "▼") + Math.Max(1, Math.Abs(b.Key) / 120), UI.BlockFont, UI.Fg, r, UI.Center);
                    break;
                default: // 키·마우스 버튼 누름(▼ 아래) / 뗌(▲ 위) — 마우스 버튼은 마우스 모양에 눌린 자리만 채움
                    {
                        bool mouse = b.K == BlockKind.MouseDown || b.K == BlockKind.MouseUp;
                        if (mouse) DrawMouse(g, r, b.Key, UI.Violet);
                        else
                        {
                            UI.FillRound(g, r, KeyColor, 4);
                            UI.Draw(g, BlockLabel(b), UI.BlockFont, UI.Fg, r, UI.Center);
                        }
                        bool down = b.K == BlockKind.KeyDown || b.K == BlockKind.MouseDown;
                        int cx = r.X + r.Width / 2;
                        using (var br = new SolidBrush(UI.Fg))
                        {
                            if (down) g.FillPolygon(br, new[] { new Point(cx - 5, r.Bottom + 6), new Point(cx + 5, r.Bottom + 6), new Point(cx, r.Bottom + 12) });
                            else g.FillPolygon(br, new[] { new Point(cx - 5, r.Y - 6), new Point(cx + 5, r.Y - 6), new Point(cx, r.Y - 12) });
                        }
                    }
                    break;
            }
            if (sel) UI.DrawRound(g, Rectangle.Inflate(r, 3, 3), UI.Fg, 5, 1.5f);
        }

        // 마우스 몸통(흰 테두리)에 눌린 버튼 자리만 색으로 채운다: 0 왼쪽 1 오른쪽 2 휠 3/4 옆 버튼(아래쪽 + X1/X2)
        static void DrawMouse(Graphics g, Rectangle r, int button, Color fill)
        {
            int w = 24, h = 34;
            var body = new Rectangle(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
            int split = body.Y + (int)(h * 0.42);   // 버튼과 몸통의 경계
            int mid = body.X + w / 2;
            var wheel = new Rectangle(mid - 3, body.Y + 4, 6, 9);
            using (var path = UI.Round(body, 10))
            using (var pen = new Pen(UI.Fg, 1.5f))
            using (var br = new SolidBrush(fill))
            {
                var state = g.Save();
                g.SetClip(path);
                switch (button)
                {
                    case 0: g.FillRectangle(br, body.X, body.Y, w / 2, split - body.Y); break;
                    case 1: g.FillRectangle(br, mid, body.Y, w - w / 2, split - body.Y); break;
                    case 2: g.FillRectangle(br, wheel); break;
                    default: g.FillRectangle(br, body.X, split, w, body.Bottom - split); break;
                }
                g.Restore(state);
                g.DrawPath(pen, path);
                g.DrawLine(pen, body.X, split, body.Right, split);
                g.DrawLine(pen, mid, body.Y, mid, split);
                if (button == 2) using (var thin = new Pen(UI.Fg, 1f)) g.DrawRectangle(thin, wheel);
                if (button >= 3) UI.Draw(g, button == 3 ? "X1" : "X2", UI.Small, UI.Fg, new Rectangle(body.X, split, w, body.Bottom - split), UI.Center);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            EndEdit(true);
            if (_preview != null) return;   // 기록 중에는 표를 건드리지 않는다
            var p = new Point(e.X, e.Y + _scroll);
            int hit = -1;
            for (int i = 0; i < _rects.Count; i++) if (Rectangle.Inflate(_rects[i], 4, 12).Contains(p)) { hit = i; break; }
            if (hit >= 0)
            {
                _sel = hit;
                Invalidate();
                if (e.Button == MouseButtons.Left && _m.Blocks[hit].K == BlockKind.Delay) EditDelay(hit);
                else if (e.Button == MouseButtons.Right) ShowMenu(hit, e.Location);
                return;
            }
            if (_plus.Contains(p))
            {
                if (e.Button == MouseButtons.Left && PlusClicked != null) PlusClicked(new Rectangle(_plus.X, _plus.Y - _scroll, _plus.Width, _plus.Height));
                return;
            }
            _sel = -1;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            EndEdit(true);
            _scroll = Math.Max(0, Math.Min(_scroll - e.Delta / 120 * 40, Math.Max(0, _contentH - Height)));
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Delete || keyData == Keys.Escape || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_preview != null) return;
            if (e.KeyCode == Keys.Delete && _sel >= 0) { Remove(_sel); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape) { _sel = -1; Invalidate(); }
        }

        void Remove(int i)
        {
            if (_m == null || _preview != null || i < 0 || i >= _m.Blocks.Count) return;
            _m.Blocks.RemoveAt(i);
            _sel = _m.Blocks.Count == 0 ? -1 : Math.Min(i, _m.Blocks.Count - 1);
            Relayout();
            Invalidate();
            if (Changed != null) Changed();
        }

        void ShowMenu(int i, Point at)
        {
            var menu = UI.DarkMenu();
            if (_m.Blocks[i].K == BlockKind.Delay) menu.Items.Add(StdOn ? "표준 지연 바꾸기" : "시간 바꾸기", null, delegate { EditDelay(i); });
            menu.Items.Add("삭제", null, delegate { Remove(i); });
            menu.Show(this, at);
        }

        // 표준 지연이 켜져 있으면 블록 하나가 아니라 표준값을 고친다
        void EditDelay(int i)
        {
            if (StdOn) { if (StdDelayClicked != null) StdDelayClicked(); }
            else BeginEdit(i);
        }

        // 지연 블록 자리에 입력칸을 띄워 숫자를 고친다
        void BeginEdit(int i)
        {
            EndEdit(true);
            if (_preview != null || i < 0 || i >= _rects.Count) return;
            var r = _rects[i];
            _editIdx = i;
            _edit = new TextBox
            {
                Bounds = new Rectangle(r.X - 2, r.Y - _scroll + 2, r.Width + 4, 24),
                Text = _m.Blocks[i].Ms.ToString(),
                Font = UI.Head,
                BackColor = UI.PanelHover,
                ForeColor = UI.Fg,
                BorderStyle = BorderStyle.None,
                TextAlign = HorizontalAlignment.Center
            };
            _edit.KeyPress += delegate(object s, KeyPressEventArgs e) { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };
            _edit.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; EndEdit(true); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; EndEdit(false); }
            };
            _edit.LostFocus += delegate { EndEdit(true); };
            Controls.Add(_edit);
            _edit.Focus();
            _edit.SelectAll();
        }

        void EndEdit(bool commit)
        {
            if (_edit == null) return;
            var box = _edit; _edit = null;
            int idx = _editIdx; _editIdx = -1;
            int v;
            if (commit && _m != null && idx >= 0 && idx < _m.Blocks.Count && int.TryParse(box.Text, out v) && v >= 0 && _m.Blocks[idx].Ms != v)
            {
                _m.Blocks[idx].Ms = v;
                Relayout();
                if (Changed != null) Changed();
            }
            Controls.Remove(box);
            box.Dispose();
            Invalidate();
        }
    }

    // '+' 를 눌렀을 때 뜨는 색 칩 메뉴 (사진 4)
    class PlusMenu : Control
    {
        public event Action<int> Picked;   // 0 키 입력 기록 · 1 텍스트 · 2 동작 · 3 지연 · -1 닫기

        static readonly string[] Labels = { "키 입력 기록", "텍스트 및 이모티콘", "동작", "지연" };
        static readonly string[] Glyphs = { "●", "T", "▶", "→" };
        static readonly Color[] Colors = { UI.Red, UI.Yellow, UI.Orange, UI.Slate };
        readonly Rectangle[] _chips = new Rectangle[4];
        readonly Rectangle _close;
        int _hover = -1;

        // downward: × 를 맨 위에 두고 칩을 아래로 펼침 ('+' 가 타임라인 위쪽에 있어 가운데 정렬이 위 줄을 가릴 때)
        public PlusMenu(bool downward)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = UI.Back;
            int y = 0, maxW = 0;
            for (int i = 0; i < 4; i++)
            {
                int w = UI.Measure(Labels[i], UI.BlockFont).Width + 50;
                _chips[i] = new Rectangle(60, y, w, 34);
                y += 40;
                maxW = Math.Max(maxW, w);
            }
            int h = y - 6;
            _close = new Rectangle(0, downward ? 0 : (h - 44) / 2, 44, 44);   // '+' 블록과 같은 크기 — 그 자리를 그대로 덮는다
            Size = new Size(60 + maxW, h);
        }

        public Rectangle CloseBox { get { return _close; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            UI.FillRound(g, _close, _hover == -2 ? UI.PanelHover : UI.Gray, 3);
            UI.Draw(g, "×", UI.Head, UI.Fg, _close, UI.Center);
            for (int i = 0; i < 4; i++)
            {
                var r = _chips[i];
                UI.FillRound(g, r, _hover == i ? ControlPaint.Light(Colors[i], 0.25f) : Colors[i], 4);
                var c = new Rectangle(r.X + 10, r.Y + 8, 18, 18);
                using (var b = new SolidBrush(UI.Fg)) g.FillEllipse(b, c);
                UI.Draw(g, Glyphs[i], UI.Small, Colors[i], c, UI.Center);
                UI.Draw(g, Labels[i], UI.BlockFont, UI.Fg, new Rectangle(r.X + 36, r.Y, r.Width - 40, r.Height), UI.Left);
            }
        }

        int Hit(Point p)
        {
            if (_close.Contains(p)) return -2;
            for (int i = 0; i < 4; i++) if (_chips[i].Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = Hit(e.Location);
            if (h != _hover) { _hover = h; Cursor = h == -1 ? Cursors.Default : Cursors.Hand; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int h = Hit(e.Location);
            if (Picked != null) Picked(h >= 0 ? h : -1);   // 칩 밖(✕ 포함)을 누르면 닫힘
        }
    }

    // ===================== 화면 4: 편집기 =====================

    class EditorScreen : Screen
    {
        readonly MainForm _app;
        readonly Timeline _tl;
        PlusMenu _menu;
        Preset _orig, _m;
        bool _dirty, _recording, _capturing, _capturingRec;
        int _hover = -1;   // 0 뒤로 · 1~4 유형 · 5 표준지연 체크 · 6 표준지연 값 · 7 색 · 8 실행 키 · 9 실행 키 지움 · 10 재설정 · 11 저장 · 12 띠의 기록 중지
                           // 13 기록 단축키 · 14 마우스 움직임 기록 토글 · 15 기록 시작/중지 단추
        Rectangle _back, _chk, _stdVal, _color, _jitter, _trig, _trigClear, _reset, _save, _recStop, _recKey, _recMouse, _recMouseChk, _recStart;   // 16 지연 랜덤
        readonly Rectangle[] _types = new Rectangle[4];
        readonly System.Windows.Forms.Timer _recTimer;   // 기록 중 0.1초마다 타임라인 미리보기 갱신

        public EditorScreen(MainForm app)
        {
            _app = app;
            _tl = new Timeline();
            _tl.PlusClicked += OnPlus;
            _tl.Changed += delegate { _dirty = true; };
            _tl.StdDelayClicked += delegate { EditStdDelay(); };
            Controls.Add(_tl);
            _recTimer = new System.Windows.Forms.Timer { Interval = 100 };
            _recTimer.Tick += delegate
            {
                if (!_recording) return;
                _tl.SetPreview(_app.PreviewRecording());
                Invalidate(new Rectangle(0, 170, Width, 40));   // 띠의 '마지막 입력' 글자
            };
        }

        public bool IsRecording { get { return _recording; } }
        public bool IsCapturing { get { return _capturing || _capturingRec; } }

        public void Open(Preset orig, Preset working)
        {
            _orig = orig; _m = working;
            _dirty = orig == null;
            _recording = false; _capturing = false; _capturingRec = false;
            _recTimer.Stop();
            CloseMenu();
            _tl.SetPreset(_m);
            Arrange();
            Invalidate();
        }

        void Arrange()
        {
            int W = Width, H = Height;
            _back = new Rectangle(14, 18, 26, 30);
            for (int i = 0; i < 4; i++) _types[i] = new Rectangle(132 + i * 38, 62, 30, 30);
            int x0 = W - 340;
            _chk = new Rectangle(x0, 22, 14, 14);
            _stdVal = new Rectangle(W - 130, 16, 90, 24);
            _color = new Rectangle(x0, 70, 140, 24);
            _jitter = new Rectangle(x0, 102, 300, 24);
            _trig = new Rectangle(104, 100, 230, 30);
            _trigClear = new Rectangle(_trig.Right + 8, 100, 30, 30);
            // 기록 줄: 단축키 칸 · 마우스 움직임 토글 · 시작/중지 단추
            _recKey = new Rectangle(104, 136, 230, 30);
            _recMouse = new Rectangle(_recKey.Right + 14, 136, 170, 30);
            _recMouseChk = new Rectangle(_recMouse.X + 4, 144, 14, 14);
            _recStart = new Rectangle(_recMouse.Right + 10, 136, 120, 30);
            _recStop = new Rectangle(W - 40 - 70, 179, 64, 22);
            int by = H - 54;
            _reset = new Rectangle(W / 2 - 190, by, 180, 32);
            _save = new Rectangle(W / 2 + 10, by, 180, 32);
            int top = _recording ? 212 : 176;   // 기록 중에는 띠가 한 줄 들어간다
            _tl.Bounds = new Rectangle(40, top, W - 80, Math.Max(50, by - top - 16));
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Arrange(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (_m == null) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int W = Width;

            UI.Draw(g, "‹", UI.Arrow, _hover == 0 ? UI.Fg : UI.Dim, _back, UI.Center);
            UI.Draw(g, _m.Name, UI.Title, UI.Fg, new Rectangle(44, 12, W - 420, 44), UI.Left | TextFormatFlags.EndEllipsis);

            UI.Draw(g, "프리셋 유형", UI.Body, UI.Fg, new Rectangle(44, 62, 84, 30), UI.Left);
            for (int i = 0; i < 4; i++)
            {
                var r = _types[i];
                UI.FillRound(g, r, UI.Panel, 3);
                if (_m.Type == i) UI.DrawRound(g, r, UI.Accent, 3, 1.5f);
                else if (_hover == 1 + i) UI.DrawRound(g, r, UI.Dim, 3, 1f);
                UI.TypeIcon(g, new Rectangle(r.X + 6, r.Y + 6, 18, 18), (PresetType)i, UI.Fg);
            }
            UI.Draw(g, UI.TypeName((PresetType)_m.Type), UI.Small, UI.Dim, new Rectangle(_types[3].Right + 12, 62, 200, 30), UI.Left);

            // 표준 지연
            using (var p = new Pen(UI.Fg)) g.DrawRectangle(p, _chk);
            if (_m.UseStdDelay) using (var b = new SolidBrush(UI.Fg)) g.FillRectangle(b, Rectangle.Inflate(_chk, -3, -3));
            UI.Draw(g, "표준 지연 사용", UI.Body, UI.Fg, new Rectangle(_chk.Right + 8, 16, 150, 26), UI.Left);
            UI.Draw(g, _m.StdDelayMs + " ms", UI.Head, _hover == 6 ? UI.Accent : UI.Fg, _stdVal, UI.Right);
            UI.Draw(g, _m.UseStdDelay ? "모든 지연을 표준 지연으로 재생" : "현재 기록된 입력 지연 사용 중", UI.Small, UI.Dim, new Rectangle(_chk.Right + 8, 42, 260, 18), UI.Left);

            // 색
            var cr = new Rectangle(_color.X, _color.Y + 3, 18, 18);
            if (_m.Color == 0)
                using (var p = new Pen(UI.Red, 2f)) { g.DrawEllipse(p, cr); g.DrawLine(p, cr.X + 4, cr.Y + 4, cr.Right - 4, cr.Bottom - 4); }
            else
                using (var b = new SolidBrush(Color.FromArgb(_m.Color))) g.FillEllipse(b, cr);
            UI.Draw(g, "프리셋 색상", UI.Body, _hover == 7 ? UI.Fg : UI.Dim, new Rectangle(_color.X + 26, _color.Y, 110, 24), UI.Left);

            // 지연 랜덤
            UI.Draw(g, "지연 랜덤", UI.Body, _hover == 16 ? UI.Fg : UI.Dim, new Rectangle(_jitter.X, _jitter.Y, 70, 24), UI.Left);
            UI.Draw(g, "± " + _m.JitterPct + " %", UI.Head, _hover == 16 ? UI.Accent : (_m.JitterPct > 0 ? UI.Fg : UI.Dim), new Rectangle(_jitter.X + 74, _jitter.Y, 80, 24), UI.Left);
            UI.Draw(g, _m.JitterPct > 0 ? "각 지연을 이 폭 안에서 무작위로" : "0 = 기록된 그대로", UI.Small, UI.Dim, new Rectangle(_jitter.X + 158, _jitter.Y, 150, 24), UI.Left);

            // 실행 키
            UI.Draw(g, "실행 키", UI.Body, UI.Fg, new Rectangle(44, 100, 60, 30), UI.Left);
            UI.FillRound(g, _trig, UI.Panel, 3);
            UI.DrawRound(g, _trig, _capturing ? UI.Accent : (_hover == 8 ? UI.Dim : UI.Line), 3, 1f);
            string trigText = _capturing ? "키 또는 마우스 버튼을 누르세요…" : (_m.HasTrigger ? UI.TriggerName(_m) : "없음 — 눌러서 정하기");
            UI.Draw(g, trigText, _capturing ? UI.Body : UI.Head, _capturing ? UI.Accent : (_m.HasTrigger ? UI.Fg : UI.Dim), Rectangle.Inflate(_trig, -10, 0), UI.Left);
            if (_m.HasTrigger && !_capturing) UI.Draw(g, "×", UI.Head, _hover == 9 ? UI.Fg : UI.Dim, _trigClear, UI.Center);
            UI.Draw(g, "이 키를 누르면 프리셋이 실행됩니다. 실행 키는 다른 프로그램에 전달되지 않습니다.", UI.Small, UI.Dim, new Rectangle(_trigClear.Right + 10, 100, Math.Max(0, _jitter.X - _trigClear.Right - 20), 30), UI.Left | TextFormatFlags.EndEllipsis);

            // 기록 줄
            var st = _app.Settings;
            UI.Draw(g, "기록", UI.Body, UI.Fg, new Rectangle(44, 136, 60, 30), UI.Left);
            UI.FillRound(g, _recKey, UI.Panel, 3);
            UI.DrawRound(g, _recKey, _capturingRec ? UI.Accent : (_hover == 13 ? UI.Dim : UI.Line), 3, 1f);
            string recText = _capturingRec ? "키를 누르세요… (Esc 취소)" : (st.RecordKey != 0 ? UI.KeyName(st.RecordKey) + "  —  기록 시작/중지" : "단축키 없음 — 눌러서 정하기");
            UI.Draw(g, recText, _capturingRec ? UI.Body : UI.Head, _capturingRec ? UI.Accent : (st.RecordKey != 0 ? UI.Fg : UI.Dim), Rectangle.Inflate(_recKey, -10, 0), UI.Left);
            using (var p = new Pen(UI.Fg)) g.DrawRectangle(p, _recMouseChk);
            if (st.RecordMouseMove) using (var b = new SolidBrush(UI.Fg)) g.FillRectangle(b, Rectangle.Inflate(_recMouseChk, -3, -3));
            UI.Draw(g, "마우스 움직임 기록", UI.Body, _hover == 14 ? UI.Fg : UI.Dim, new Rectangle(_recMouseChk.Right + 8, 136, 150, 30), UI.Left);
            UI.FillRound(g, _recStart, _recording ? UI.Red : (_hover == 15 ? UI.TealHover : UI.Teal), 3);
            UI.Draw(g, _recording ? "■  기록 중지" : "●  기록 시작", UI.Body, UI.Fg, _recStart, UI.Center);

            // 기록 중 띠
            if (_recording)
            {
                var strip = new Rectangle(40, 176, W - 80, 28);
                UI.FillRound(g, strip, UI.Red, 3);
                string last = _app.LastRecorded;
                UI.Draw(g, "●  기록 중" + (last != "" ? "  —  마지막 입력: " + last : "  —  키보드·마우스 입력이 기록됩니다"), UI.Body, UI.Fg, new Rectangle(strip.X + 12, strip.Y, W - 240, 28), UI.Left | TextFormatFlags.EndEllipsis);
                UI.DrawRound(g, _recStop, UI.Fg, 3, 1f);
                UI.Draw(g, "중지", UI.Body, UI.Fg, _recStop, UI.Center);
            }

            // 아래 단추
            UI.FillRound(g, _reset, _hover == 10 ? UI.PanelHover : UI.Gray, 3);
            UI.Draw(g, "재설정", UI.Body, UI.Fg, _reset, UI.Center);
            UI.FillRound(g, _save, _hover == 11 ? UI.TealHover : UI.Teal, 3);
            UI.Draw(g, "저장", UI.Body, UI.Fg, _save, UI.Center);
        }

        int Hit(Point p)
        {
            if (_recording) return _recStop.Contains(p) ? 12 : (_recStart.Contains(p) ? 15 : -1);
            if (_recKey.Contains(p)) return 13;
            if (_recMouse.Contains(p)) return 14;
            if (_recStart.Contains(p)) return 15;
            if (_back.Contains(p)) return 0;
            for (int i = 0; i < 4; i++) if (_types[i].Contains(p)) return 1 + i;
            if (new Rectangle(_chk.X, 14, 170, 30).Contains(p)) return 5;
            if (_stdVal.Contains(p)) return 6;
            if (_color.Contains(p)) return 7;
            if (_jitter.Contains(p)) return 16;
            if (_trig.Contains(p)) return 8;
            if (_m != null && _m.HasTrigger && _trigClear.Contains(p)) return 9;
            if (_reset.Contains(p)) return 10;
            if (_save.Contains(p)) return 11;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = Hit(e.Location);
            if (h != _hover) { _hover = h; Cursor = h == -1 ? Cursors.Default : Cursors.Hand; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left) return;
            int h = Hit(e.Location);
            if (_capturing && h != 8) { _capturing = false; _app.CancelCapture(); Invalidate(); }
            if (_capturingRec && h != 13) { _capturingRec = false; _app.CancelCapture(); Invalidate(); }
            switch (h)
            {
                case 13: ToggleRecCapture(); break;
                case 14: _app.Settings.RecordMouseMove = !_app.Settings.RecordMouseMove; _app.SaveSettings(); Invalidate(); break;
                case 15: ToggleRecording(true); break;
                case 0: Back(); break;
                case 1: case 2: case 3: case 4: _m.Type = h - 1; _dirty = true; Invalidate(); break;
                case 5: _m.UseStdDelay = !_m.UseStdDelay; _dirty = true; _tl.RefreshBlocks(); Invalidate(); break;
                case 6: EditStdDelay(); break;
                case 7: PickColor(); break;
                case 16: EditJitter(); break;
                case 8: ToggleCapture(); break;
                case 9: _m.TriggerVk = 0; _m.TriggerMouse = 0; _dirty = true; Invalidate(); break;
                case 10: Reset(); break;
                case 11: _app.SavePreset(_orig, _m); break;
                case 12: StopRecording(true); break;
            }
        }

        void Back()
        {
            if (_dirty && !DarkDialog.Confirm(FindForm(), "바뀐 내용을 저장하지 않고 나갈까요?")) return;
            _app.ShowList();
        }

        void Reset()
        {
            _m = _orig != null ? Json.Clone(_orig) : new Preset { Name = _m.Name, Type = _m.Type };
            _dirty = _orig == null;
            _tl.SetPreset(_m);
            Invalidate();
        }

        void EditStdDelay()
        {
            string s = DarkDialog.Input(FindForm(), "표준 지연 (ms)", _m.StdDelayMs.ToString(), true);
            int v;
            if (s != null && int.TryParse(s, out v) && v >= 0) { _m.StdDelayMs = v; _dirty = true; _tl.RefreshBlocks(); Invalidate(); }
        }

        void EditJitter()
        {
            string s = DarkDialog.Input(FindForm(), "지연 랜덤 폭 (%)  —  0 = 끔, 20 = 각 지연을 ±20% 안에서", _m.JitterPct.ToString(), true);
            int v;
            if (s != null && int.TryParse(s, out v) && v >= 0) { _m.JitterPct = Math.Min(100, v); _dirty = true; Invalidate(); }
        }

        void PickColor()
        {
            using (var cd = new ColorDialog { Color = _m.Color == 0 ? UI.Red : Color.FromArgb(_m.Color), FullOpen = true })
                if (cd.ShowDialog(FindForm()) == DialogResult.OK) { _m.Color = cd.Color.ToArgb(); _dirty = true; _tl.Invalidate(); Invalidate(); }
        }

        void ToggleCapture()
        {
            if (_capturing) { _capturing = false; _app.CancelCapture(); }
            else { _capturing = true; _app.CaptureTrigger(OnTriggerCaptured); }
            Invalidate();
        }

        void OnTriggerCaptured(int vk, int mouse)
        {
            _capturing = false;
            if (vk != 0) { _m.TriggerVk = vk; _m.TriggerMouse = 0; _dirty = true; }
            else if (mouse != 0) { _m.TriggerMouse = mouse; _m.TriggerVk = 0; _dirty = true; }
            Invalidate();
        }

        // ---- 기록 단축키 정하기 (키보드만) ----

        void ToggleRecCapture()
        {
            if (_capturingRec) { _capturingRec = false; _app.CancelCapture(); }
            else { _capturingRec = true; _app.CaptureTrigger(OnRecKeyCaptured); }
            Invalidate();
        }

        void OnRecKeyCaptured(int vk, int mouse)
        {
            _capturingRec = false;
            if (vk != 0) { _app.Settings.RecordKey = vk; _app.SaveSettings(); }
            Invalidate();
        }

        // 기록 단축키·기록 단추·'+' 메뉴 모두 여기로 모인다. byMouse = 마우스 클릭으로 눌렀음
        public void ToggleRecording(bool byMouse)
        {
            if (_m == null || !Visible) return;
            if (_capturing || _capturingRec) return;   // 키를 받는 중이면 그쪽이 먼저
            CloseMenu();
            if (_recording) StopRecording(byMouse);
            else StartRecording();
        }

        // ---- '+' 메뉴 ----

        void OnPlus(Rectangle plus)
        {
            CloseMenu();
            // × 상자가 '+' 블록 자리에 정확히 겹치도록 놓는다. 가운데 정렬이 타임라인 위 줄(실행 키·기록)을 가리면 아래로 펼친다
            _menu = new PlusMenu(false);
            int x = _tl.Left + plus.X - _menu.CloseBox.X, y = _tl.Top + plus.Y - _menu.CloseBox.Y;
            if (y < _tl.Top)
            {
                _menu.Dispose();
                _menu = new PlusMenu(true);
                y = _tl.Top + plus.Y - _menu.CloseBox.Y;
            }
            _menu.Location = new Point(Math.Max(0, Math.Min(Width - _menu.Width, x)), Math.Max(_tl.Top, Math.Min(Height - _menu.Height, y)));
            // 메뉴의 마우스 처리 안에서 메뉴를 없애지 않도록 한 박자 뒤에 처리한다
            _menu.Picked += delegate(int i) { BeginInvoke(new Action<int>(OnMenuPicked), i); };
            Controls.Add(_menu);
            _menu.BringToFront();
            _menu.Capture = true;   // 메뉴 밖을 누르면 메뉴가 받아서 닫힌다
        }

        void CloseMenu()
        {
            if (_menu == null) return;
            var m = _menu; _menu = null;
            m.Capture = false;
            Controls.Remove(m);
            m.Dispose();
        }

        void OnMenuPicked(int i)
        {
            CloseMenu();
            switch (i)
            {
                case 0: StartRecording(); break;
                case 1: AddText(); break;
                case 2: AddAction(); break;
                case 3: AddDelay(); break;
            }
            _tl.Focus();
        }

        void StartRecording()
        {
            if (_recording) return;
            _recording = true;
            _tl.BeginPreview();
            _app.StartRecording();
            _recTimer.Start();
            Arrange();
            Invalidate();
        }

        void StopRecording(bool byMouse)
        {
            if (!_recording) return;
            _recTimer.Stop();
            var blocks = _app.StopRecording(byMouse);
            _recording = false;
            _tl.EndPreview();
            if (blocks.Count > 0) _tl.Insert(blocks);
            Arrange();
            Invalidate();
        }

        void AddText()
        {
            string t = DarkDialog.Input(FindForm(), "텍스트 및 이모티콘", "", false);
            if (!string.IsNullOrEmpty(t)) _tl.Insert(new List<Block> { new Block { Kind = (int)BlockKind.Text, Text = t } });
        }

        void AddAction()
        {
            int c = DarkDialog.Choose(FindForm(), "동작", new[] { "왼쪽 클릭", "오른쪽 클릭", "가운데 클릭", "X1 클릭", "X2 클릭", "휠 위로", "휠 아래로" }, null);
            if (c < 0) return;
            var list = new List<Block>();
            if (c <= 4)
            {
                list.Add(new Block { Kind = (int)BlockKind.MouseDown, Key = c });
                list.Add(Block.Delay(30));
                list.Add(new Block { Kind = (int)BlockKind.MouseUp, Key = c });
            }
            else list.Add(new Block { Kind = (int)BlockKind.Wheel, Key = c == 5 ? 120 : -120 });
            _tl.Insert(list);
        }

        void AddDelay()
        {
            string s = DarkDialog.Input(FindForm(), "지연 (ms)", "50", true);
            int v;
            if (s != null && int.TryParse(s, out v) && v >= 0) _tl.Insert(new List<Block> { Block.Delay(v) });
        }
    }

    // ===================== 본 창: 화면 바꾸기 · 훅 · 녹화 · 트리거 =====================

    class MainForm : Form
    {
        readonly string _path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "inmc-kmm.json");
        Store _store = new Store();

        readonly ListScreen _list;
        readonly NameScreen _name;
        readonly TypeScreen _type;
        readonly EditorScreen _editor;
        string _pendingName = "";

        Native.HookProc _kbProc, _mouseProc;
        IntPtr _kbHook, _mouseHook;

        volatile bool _armed;                       // 목록 화면일 때만 실행 키가 먹는다
        bool _recHotkey;                            // 편집기 화면일 때만 기록 단축키가 먹는다
        Action<int, int> _captureCb;                // 실행 키 받기 (vk, 마우스 버튼+1)
        bool _recording;
        bool _recWithPos;     // 이번 기록에서 클릭·휠에 좌표를 담는지 (= 기록 시작 때 '마우스 움직임 기록' 설정)
        string _lastRecorded = "";
        List<RawEvent> _rec = new List<RawEvent>();
        readonly Stopwatch _clock = new Stopwatch();
        long _lastMove;
        readonly HashSet<int> _keysDown = new HashSet<int>();   // 누르고 있을 때 반복되는 '누름'을 거르기 위함
        readonly HashSet<int> _mouseDown = new HashSet<int>();  // 누름이 기록된 마우스 버튼 — '기록 시작'을 누른 클릭의 '뗌'이 들어오지 않게
        readonly Runner _runner = new Runner();
        readonly HashSet<int> _trigHeld = new HashSet<int>();   // 실행 키 자동 반복 거르기

        // 프로필 감지 · 트레이
        volatile string _activeScope = "";          // 지금 실행 키에 반응하는 프로필 ("" = 평소)
        string _fgName = "";                         // 앞 창의 프로세스 이름(소문자)
        uint _fgPid;
        readonly NotifyIcon _tray;
        readonly bool _startHidden;
        string _pendingScope = "";

        const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run", RUN_NAME = "inmc-kmm";

        class RawEvent { public BlockKind Kind; public int Key, Scan; public bool Ext; public int X, Y; public long T; public bool OnSelf; }

        public List<Preset> Presets { get { return _store.Presets; } }
        public Settings Settings { get { return _store.Settings; } }
        public Runner Runner { get { return _runner; } }
        public string LastRecorded { get { return _lastRecorded; } }   // 기록 중 마지막으로 들어온 입력 이름
        public string ActiveScope { get { return _activeScope; } }
        public string ForegroundName { get { return _fgName; } }

        public void SaveSettings() { SaveStore(); }

        public MainForm(bool startHidden)
        {
            _startHidden = startHidden;
            Text = "inmc-kmm";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }   // exe 에 박힌 아이콘을 창에도
            BackColor = UI.Back;
            ClientSize = new Size(980, 640);
            MinimumSize = new Size(900, 600);
            StartPosition = FormStartPosition.CenterScreen;
            Font = UI.Body;
            LoadStore();

            _list = new ListScreen(this);
            _name = new NameScreen(this);
            _type = new TypeScreen(this);
            _editor = new EditorScreen(this);
            Controls.Add(_list); Controls.Add(_name); Controls.Add(_type); Controls.Add(_editor);
            ShowList();

            // 앞 창 감지
            var fg = new System.Windows.Forms.Timer { Interval = 400 };
            fg.Tick += delegate { UpdateForeground(); };
            fg.Start();
            UpdateForeground();

            // 트레이 아이콘
            _tray = new NotifyIcon { Icon = Icon ?? SystemIcons.Application, Text = "inmc-kmm", Visible = true, ContextMenuStrip = UI.DarkMenu() };
            _tray.ContextMenuStrip.Opening += delegate { BuildTrayMenu(); };
            _tray.DoubleClick += delegate { RestoreFromTray(); };
        }

        // ---------- 프로필 ----------

        public Profile FindProfile(string name)
        {
            foreach (var p in _store.Settings.Profiles) if (p.Name == name) return p;
            return null;
        }

        public string ProfileModeText
        {
            get
            {
                var st = _store.Settings;
                if (st.AutoProfile) return "자동 (앞 창 감지)";
                return st.ForcedProfile == "" ? "평소" : st.ForcedProfile;
            }
        }

        void UpdateForeground()
        {
            uint pid;
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid);
            if (pid != _fgPid)
            {
                _fgPid = pid;
                try { _fgName = pid == 0 ? "" : Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); }
                catch { _fgName = ""; }
            }
            var st = _store.Settings;
            string scope = "";
            if (!st.AutoProfile) { if (FindProfile(st.ForcedProfile) != null) scope = st.ForcedProfile; }
            else foreach (var p in st.Profiles) if (!string.IsNullOrEmpty(p.Process) && p.Process == _fgName) { scope = p.Name; break; }
            _activeScope = scope;
        }

        static string NormalizeProcess(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            if (s.EndsWith(".exe")) s = s.Substring(0, s.Length - 4);
            return s;
        }

        // 지금 떠 있는 창들 중에서 고르거나 직접 친다. null = 취소, "" = 감지 안 함
        string PickProcess(string current)
        {
            var names = new List<string>();
            var options = new List<string> { "앞 창 감지 안 함 (직접 고를 때만 작동)" };
            try
            {
                var procs = new List<Process>(Process.GetProcesses());
                procs.Sort((a, b) => string.Compare(a.ProcessName, b.ProcessName, StringComparison.OrdinalIgnoreCase));
                foreach (var p in procs)
                {
                    try
                    {
                        if (p.Id == Process.GetCurrentProcess().Id || p.MainWindowHandle == IntPtr.Zero || p.MainWindowTitle == "") continue;
                        string n = p.ProcessName.ToLowerInvariant();
                        if (names.Contains(n) || names.Count >= 14) continue;
                        names.Add(n);
                        string title = p.MainWindowTitle;
                        if (title.Length > 30) title = title.Substring(0, 30) + "…";
                        options.Add(n + ".exe  —  " + title);
                    }
                    catch { }
                }
            }
            catch { }
            options.Add("직접 입력…");
            int c = DarkDialog.Choose(this, "감지할 프로세스 (앞 창이 이것일 때 프로필이 작동)", options.ToArray(), null);
            if (c < 0) return null;
            if (c == 0) return "";
            if (c == options.Count - 1)
            {
                string s = DarkDialog.Input(this, "프로세스 이름 (예: javaw 또는 LeagueClient.exe)", current ?? "", false);
                return s == null ? null : NormalizeProcess(s);
            }
            return names[c - 1];
        }

        public void AddProfile()
        {
            string name = DarkDialog.Input(this, "프로필 이름 (예: 롤, 마크)", "", false);
            if (name == null) return;
            name = name.Trim();
            if (name == "" || name == "평소") return;
            if (FindProfile(name) != null) { DarkDialog.Info(this, "같은 이름의 프로필이 이미 있습니다."); return; }
            string proc = PickProcess("");
            if (proc == null) return;
            _store.Settings.Profiles.Add(new Profile { Name = name, Process = proc });
            SaveStore();
            UpdateForeground();
        }

        public void SetProfileProcess(Profile p)
        {
            string proc = PickProcess(p.Process);
            if (proc == null) return;
            p.Process = proc;
            SaveStore();
            UpdateForeground();
        }

        public void RenameProfile(Profile p)
        {
            string name = DarkDialog.Input(this, "프로필 이름", p.Name, false);
            if (name == null) return;
            name = name.Trim();
            if (name == "" || name == p.Name || name == "평소") return;
            if (FindProfile(name) != null) { DarkDialog.Info(this, "같은 이름의 프로필이 이미 있습니다."); return; }
            foreach (var m in _store.Presets) if (m.ScopeKey == p.Name) m.Scope = name;
            if (_store.Settings.ForcedProfile == p.Name) _store.Settings.ForcedProfile = name;
            p.Name = name;
            SaveStore();
            UpdateForeground();
        }

        public void RemoveProfile(Profile p)
        {
            if (!DarkDialog.Confirm(this, "'" + p.Name + "' 프로필을 지울까요? 그 안의 프리셋은 평소로 옮겨집니다.")) return;
            foreach (var m in _store.Presets) if (m.ScopeKey == p.Name) m.Scope = "";
            if (_store.Settings.ForcedProfile == p.Name) { _store.Settings.ForcedProfile = ""; _store.Settings.AutoProfile = true; }
            _store.Settings.Profiles.Remove(p);
            SaveStore();
            UpdateForeground();
        }

        public void MovePreset(Preset m, string scope)
        {
            m.Scope = scope;
            var lost = ClearSameTrigger(m);
            SaveStore();
            if (lost.Count > 0) DarkDialog.Info(this, "같은 실행 키를 쓰던 '" + string.Join(", ", lost.ToArray()) + "' 의 실행 키를 비웠습니다.");
        }

        public void ChooseActiveProfile()
        {
            var st = _store.Settings;
            var options = new List<string> { "자동 (앞 창 감지)", "평소" };
            foreach (var p in st.Profiles) options.Add(p.Name);
            int c = DarkDialog.Choose(this, "작동 프로필", options.ToArray(), null);
            if (c < 0) return;
            if (c == 0) st.AutoProfile = true;
            else { st.AutoProfile = false; st.ForcedProfile = c == 1 ? "" : st.Profiles[c - 2].Name; }
            SaveStore();
            UpdateForeground();
        }

        // ---------- 트레이 · 부팅 자동 실행 ----------

        void BuildTrayMenu()
        {
            var menu = _tray.ContextMenuStrip;
            menu.Items.Clear();
            menu.Items.Add("열기", null, delegate { RestoreFromTray(); });
            menu.Items.Add(new ToolStripSeparator());
            var st = _store.Settings;
            menu.Items.Add(new ToolStripMenuItem("작동 프로필: " + ProfileModeText) { Enabled = false });
            menu.Items.Add((st.AutoProfile ? "●  " : "    ") + "자동 (앞 창 감지)", null, delegate { st.AutoProfile = true; SaveStore(); UpdateForeground(); _list.Invalidate(); });
            menu.Items.Add((!st.AutoProfile && st.ForcedProfile == "" ? "●  " : "    ") + "평소", null, delegate { st.AutoProfile = false; st.ForcedProfile = ""; SaveStore(); UpdateForeground(); _list.Invalidate(); });
            foreach (var p in st.Profiles)
            {
                var name = p.Name;
                menu.Items.Add((!st.AutoProfile && st.ForcedProfile == name ? "●  " : "    ") + name, null, delegate { st.AutoProfile = false; st.ForcedProfile = name; SaveStore(); UpdateForeground(); _list.Invalidate(); });
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("종료", null, delegate { Close(); });
        }

        void HideToTray()
        {
            Hide();
            ShowInTaskbar = false;
        }

        void RestoreFromTray()
        {
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_startHidden) HideToTray();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized && _store.Settings.MinimizeToTray) HideToTray();
        }

        public bool StartWithWindows
        {
            get
            {
                try { using (var k = Registry.CurrentUser.OpenSubKey(RUN_KEY)) return k != null && k.GetValue(RUN_NAME) != null; }
                catch { return false; }
            }
        }

        public void SetStartWithWindows(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RUN_KEY))
                {
                    if (on) k.SetValue(RUN_NAME, "\"" + Application.ExecutablePath + "\" --tray");
                    else k.DeleteValue(RUN_NAME, false);
                }
            }
            catch (Exception ex) { DarkDialog.Info(this, "시작 프로그램 등록을 바꾸지 못했습니다: " + ex.Message); }
        }

        // ---------- 저장 ----------

        void LoadStore()
        {
            try
            {
                if (!File.Exists(_path)) return;
                var s = Json.Read<Store>(File.ReadAllText(_path));
                if (s != null && s.Presets != null) _store = s;
                if (_store.Settings == null) _store.Settings = new Settings();
            }
            catch (Exception ex)
            {
                MessageBox.Show("inmc-kmm.json 을 읽지 못했습니다:\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void SaveStore()
        {
            try { File.WriteAllText(_path, Json.Write(_store)); }
            catch (Exception ex) { DarkDialog.Info(this, "저장하지 못했습니다: " + ex.Message); }
        }

        // ---------- 화면 바꾸기 ----------

        void Switch(Control c)
        {
            _runner.Stop();
            _captureCb = null;
            _recording = false;
            foreach (Control x in Controls) x.Visible = x == c;
            _armed = c == _list;
            _recHotkey = c == _editor;
            c.Focus();
            c.Invalidate();
        }

        public void ShowList() { Switch(_list); }
        public void NewPreset(string scope) { _pendingScope = scope; _name.Reset(); Switch(_name); _name.FocusBox(); }
        public void NameEntered(string n) { _pendingName = n; _type.Set(n); Switch(_type); }
        public void TypeChosen(PresetType t) { _editor.Open(null, new Preset { Name = _pendingName, Type = (int)t, Scope = _pendingScope }); Switch(_editor); }
        public void OpenEditor(Preset m) { _editor.Open(m, Json.Clone(m)); Switch(_editor); }

        public void DeletePreset(Preset m)
        {
            if (!DarkDialog.Confirm(this, "'" + m.Name + "' 프리셋을 지울까요?")) return;
            _store.Presets.Remove(m);
            SaveStore();
            _list.Invalidate();
        }

        // 같은 프로필 안에서 같은 실행 키를 쓰는 다른 프리셋의 실행 키를 비운다 (프로필이 다르면 겹쳐도 됨)
        List<string> ClearSameTrigger(Preset keep)
        {
            var lost = new List<string>();
            foreach (var o in _store.Presets)
            {
                if (o == keep || o.ScopeKey != keep.ScopeKey) continue;
                bool same = (keep.TriggerVk != 0 && o.TriggerVk == keep.TriggerVk) || (keep.TriggerMouse != 0 && o.TriggerMouse == keep.TriggerMouse);
                if (same) { o.TriggerVk = 0; o.TriggerMouse = 0; lost.Add(o.Name); }
            }
            return lost;
        }

        public void SavePreset(Preset orig, Preset working)
        {
            if (orig == null) _store.Presets.Add(working);
            else _store.Presets[_store.Presets.IndexOf(orig)] = working;
            var lost = ClearSameTrigger(working);
            SaveStore();
            if (lost.Count > 0) DarkDialog.Info(this, "같은 실행 키를 쓰던 '" + string.Join(", ", lost.ToArray()) + "' 의 실행 키를 비웠습니다.");
            ShowList();
        }

        // ---------- 훅 ----------

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _kbProc = KeyboardHook;
            _mouseProc = MouseHook;
            IntPtr mod = Native.GetModuleHandle(null);
            _kbHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _kbProc, mod, 0);
            _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, mod, 0);
            if (_kbHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
                MessageBox.Show(this, "입력 훅을 설치하지 못했습니다. 관리자 권한으로 다시 실행해 보세요.", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            _runner.StopAndWait();
            if (_kbHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_kbHook);
            if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
            _kbHook = _mouseHook = IntPtr.Zero;
            base.OnFormClosing(e);
        }

        // 우리가 쏜 입력은 구조체를 복사하기 전에 표식 한 칸만 읽고 바로 넘긴다 (빠른 반복 때 훅 부담을 줄임)
        static readonly int KB_EXTRA = (int)Marshal.OffsetOf(typeof(Native.KBDLLHOOKSTRUCT), "dwExtraInfo");
        static readonly int MS_EXTRA = (int)Marshal.OffsetOf(typeof(Native.MSLLHOOKSTRUCT), "dwExtraInfo");

        IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && Marshal.ReadIntPtr(lParam, KB_EXTRA) != Native.MAGIC)
            {
                var info = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                if (info.dwExtraInfo != Native.MAGIC)   // 우리가 쏜 입력은 건드리지 않는다
                {
                    int msg = wParam.ToInt32();
                    bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
                    bool up = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP;
                    bool ext = (info.flags & Native.LLKHF_EXTENDED) != 0;

                    var cb = _captureCb;
                    if (cb != null)
                    {
                        // 실행 키 받기: 첫 키를 기억하고(Esc 는 취소) 누름·뗌 모두 삼킨다
                        if (down) { _captureCb = null; BeginInvoke(cb, info.vkCode == Native.VK_ESCAPE ? 0 : info.vkCode, 0); }
                        return (IntPtr)1;
                    }

                    if (_recHotkey && info.vkCode != 0 && info.vkCode == _store.Settings.RecordKey)
                    {
                        // 기록 시작/중지 단축키: 기록에 넣지 않고 다른 프로그램에도 안 넘긴다
                        if (down) BeginInvoke(new Action(() => _editor.ToggleRecording(false)));
                        return (IntPtr)1;
                    }

                    if (_recording)
                    {
                        if (down) { if (_keysDown.Add(info.vkCode)) Record(BlockKind.KeyDown, info.vkCode, info.scanCode, ext, 0, 0, false); }
                        else if (up) { _keysDown.Remove(info.vkCode); Record(BlockKind.KeyUp, info.vkCode, info.scanCode, ext, 0, 0, false); }
                    }
                    else if (_armed && (down || up))
                    {
                        var m = FindByVk(info.vkCode);
                        if (m != null)
                        {
                            if (down) { if (_trigHeld.Add(info.vkCode)) BeginInvoke(new Action<Preset>(TriggerDown), m); }
                            else { _trigHeld.Remove(info.vkCode); BeginInvoke(new Action<Preset>(TriggerUp), m); }
                            return (IntPtr)1;   // 실행 키는 다른 프로그램에 넘기지 않는다
                        }
                    }
                }
            }
            return Native.CallNextHookEx(_kbHook, nCode, wParam, lParam);
        }

        IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && Marshal.ReadIntPtr(lParam, MS_EXTRA) != Native.MAGIC)
            {
                var info = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                if (info.dwExtraInfo != Native.MAGIC)
                {
                    int msg = wParam.ToInt32();
                    int x = info.pt.x, y = info.pt.y;
                    int hi = (short)(info.mouseData >> 16);   // 휠: 델타, X 버튼: 1 또는 2
                    int button = -1; bool down = false, up = false;
                    switch (msg)
                    {
                        case Native.WM_LBUTTONDOWN: button = 0; down = true; break;
                        case Native.WM_LBUTTONUP: button = 0; up = true; break;
                        case Native.WM_RBUTTONDOWN: button = 1; down = true; break;
                        case Native.WM_RBUTTONUP: button = 1; up = true; break;
                        case Native.WM_MBUTTONDOWN: button = 2; down = true; break;
                        case Native.WM_MBUTTONUP: button = 2; up = true; break;
                        case Native.WM_XBUTTONDOWN: button = 2 + hi; down = true; break;
                        case Native.WM_XBUTTONUP: button = 2 + hi; up = true; break;
                    }

                    var cb = _captureCb;
                    if (cb != null && button >= 2)
                    {
                        if (down) { _captureCb = null; BeginInvoke(cb, 0, button + 1); }
                        return (IntPtr)1;
                    }

                    if (_recording)
                    {
                        // 그 자리에 실제로 보이는 창이 우리 창인지 (화면 영역이 아니라 — 앞에 다른 창이 있으면 그 창이 나온다)
                        IntPtr hw = Native.WindowFromPoint(info.pt);
                        bool onSelf = hw != IntPtr.Zero && Native.GetAncestor(hw, Native.GA_ROOT) == Handle;
                        if (msg == Native.WM_MOUSEMOVE)
                        {
                            long now = _clock.ElapsedMilliseconds;
                            if (_store.Settings.RecordMouseMove && now - _lastMove >= 10) { _lastMove = now; Record(BlockKind.MouseMove, 0, 0, false, x, y, onSelf); }
                        }
                        else if (down) { _mouseDown.Add(button); Record(BlockKind.MouseDown, button, 0, false, x, y, onSelf); }   // 우리 창 안의 클릭도 기록한다. '중지' 클릭만 끝낼 때 뗀다
                        else if (up) { if (_mouseDown.Remove(button)) Record(BlockKind.MouseUp, button, 0, false, x, y, onSelf); }   // 누름이 없던 뗌(기록 시작 단추를 누른 클릭)은 버린다
                        else if (msg == Native.WM_MOUSEWHEEL) Record(BlockKind.Wheel, hi, 0, false, x, y, onSelf);
                    }
                    else if (_armed && button >= 2 && (down || up))
                    {
                        var m = FindByMouse(button + 1);
                        if (m != null)
                        {
                            int id = 1000 + button;
                            if (down) { if (_trigHeld.Add(id)) BeginInvoke(new Action<Preset>(TriggerDown), m); }
                            else { _trigHeld.Remove(id); BeginInvoke(new Action<Preset>(TriggerUp), m); }
                            return (IntPtr)1;
                        }
                    }
                }
            }
            return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        // 지금 작동 중인 프로필의 프리셋만 실행 키에 반응한다
        Preset FindByVk(int vk)
        {
            string scope = _activeScope;
            foreach (var m in _store.Presets) if (m.TriggerVk == vk && m.ScopeKey == scope) return m;
            return null;
        }

        Preset FindByMouse(int tm)
        {
            string scope = _activeScope;
            foreach (var m in _store.Presets) if (m.TriggerMouse == tm && m.ScopeKey == scope) return m;
            return null;
        }

        // ---------- 실행 키 받기 ----------

        public void CaptureTrigger(Action<int, int> cb) { _captureCb = cb; }
        public void CancelCapture() { _captureCb = null; }

        // ---------- 녹화 ----------

        public void StartRecording()
        {
            _rec = new List<RawEvent>();
            _keysDown.Clear();
            _mouseDown.Clear();
            _clock.Restart();
            _lastMove = -100;
            _lastRecorded = "";
            _recWithPos = _store.Settings.RecordMouseMove;
            _recording = true;
        }

        // 기록 중 화면에 바로 보여 주기 위한 미리보기 (아직 다듬지 않은 상태)
        public List<Block> PreviewRecording() { return ToBlocks(_rec, _recWithPos); }

        // byMouse: 마우스로 '중지' 단추를 눌러 끝냄 → 그 단추를 누른 클릭(마지막 누름)은 프리셋에서 뺀다
        public List<Block> StopRecording(bool byMouse)
        {
            _recording = false;
            var ev = _rec;
            _rec = new List<RawEvent>();
            TrimSelf(ev, byMouse);
            return ToBlocks(ev, _recWithPos);
        }

        void Record(BlockKind kind, int key, int scan, bool ext, int x, int y, bool onSelf)
        {
            _rec.Add(new RawEvent { Kind = kind, Key = key, Scan = scan, Ext = ext, X = x, Y = y, T = _clock.ElapsedMilliseconds, OnSelf = onSelf });
            if (kind == BlockKind.KeyDown) _lastRecorded = key == Native.VK_PACKET ? ((char)scan).ToString() : UI.KeyName(key);
            else if (kind == BlockKind.MouseDown) _lastRecorded = "마우스 " + UI.MouseName(key);
            else if (kind == BlockKind.Wheel) _lastRecorded = key > 0 ? "휠 ▲" : "휠 ▼";
        }

        // 우리 창 위를 지나는 처음(기록 시작 단추에서 나가는 움직임)은 뺀다. 클릭은 어디서 했든 다 남긴다.
        // 마우스로 끝냈으면 '중지' 단추를 누른 그 클릭(끝의 왼쪽 누름 — 뗌은 기록이 끝난 뒤라 없다)과
        // 거기까지 오는 우리 창 위 움직임만 뺀다. 안 빼면 재생 때 같은 자리의 단추를 눌러 버린다.
        static void TrimSelf(List<RawEvent> ev, bool byMouse)
        {
            if (byMouse)
            {
                TrimTrailingSelfMoves(ev);
                if (ev.Count > 0 && ev[ev.Count - 1].Kind == BlockKind.MouseDown && ev[ev.Count - 1].OnSelf && ev[ev.Count - 1].Key == 0)
                {
                    ev.RemoveAt(ev.Count - 1);
                    TrimTrailingSelfMoves(ev);
                }
            }
            while (ev.Count > 0 && ev[0].Kind == BlockKind.MouseMove && ev[0].OnSelf) ev.RemoveAt(0);
        }

        static void TrimTrailingSelfMoves(List<RawEvent> ev)
        {
            while (ev.Count > 0 && ev[ev.Count - 1].Kind == BlockKind.MouseMove && ev[ev.Count - 1].OnSelf) ev.RemoveAt(ev.Count - 1);
        }

        // withPos: 클릭·휠에 좌표를 담는다. 마우스 움직임 기록을 꺼 두었으면 좌표 없이 — 재생 때 커서가 있는 자리에서 누른다
        static List<Block> ToBlocks(List<RawEvent> ev, bool withPos)
        {
            var blocks = new List<Block>();
            long last = ev.Count > 0 ? ev[0].T : 0;   // 첫 동작 앞의 기다림은 버린다
            for (int i = 0; i < ev.Count; i++)
            {
                var e = ev[i];
                int gap = (int)(e.T - last);
                if (gap > 0) blocks.Add(Block.Delay(gap));
                if (e.Kind == BlockKind.MouseMove)
                {
                    // 이어지는 이동은 블록 하나로 묶는다 (점마다 앞 점과의 간격 보존)
                    var pts = new List<PathPoint>();
                    long prev = e.T;
                    while (i < ev.Count && ev[i].Kind == BlockKind.MouseMove)
                    {
                        pts.Add(new PathPoint { X = ev[i].X, Y = ev[i].Y, Dt = (int)(ev[i].T - prev) });
                        prev = ev[i].T;
                        i++;
                    }
                    i--;
                    blocks.Add(new Block { Kind = (int)BlockKind.MouseMove, Points = pts });
                    last = prev;
                }
                else
                {
                    bool mouse = e.Kind == BlockKind.MouseDown || e.Kind == BlockKind.MouseUp || e.Kind == BlockKind.Wheel;
                    blocks.Add(new Block { Kind = (int)e.Kind, Key = e.Key, Scan = e.Scan, Ext = e.Ext, HasPos = mouse && withPos, X = e.X, Y = e.Y });
                    last = e.T;
                }
            }
            return blocks;
        }

        // ---------- 실행 키 눌림 ----------

        void TriggerDown(Preset m)
        {
            if (!_armed || m.Blocks.Count == 0) return;
            _runner.MinGapMs = Math.Max(0, _store.Settings.MinGapMs);
            switch ((PresetType)m.Type)
            {
                case PresetType.Once:
                    if (!_runner.IsRunning) _runner.Start(m, 0, m.Blocks.Count, false);
                    break;
                case PresetType.WhileHeld:
                    if (!_runner.IsRunning) _runner.Start(m, 0, m.Blocks.Count, true);
                    break;
                case PresetType.Toggle:
                    if (_runner.IsRunning) { if (_runner.Current == m) _runner.Stop(); }
                    else _runner.Start(m, 0, m.Blocks.Count, true);
                    break;
                case PresetType.Sequence:
                    if (_runner.IsRunning) break;
                    var units = Runner.Units(m.Blocks);
                    if (units.Count == 0) break;
                    if (m.SeqPos >= units.Count) m.SeqPos = 0;
                    var u = units[m.SeqPos];
                    m.SeqPos = (m.SeqPos + 1) % units.Count;
                    _runner.Start(m, u[0], u[1], false);
                    break;
            }
        }

        void TriggerUp(Preset m)
        {
            if ((PresetType)m.Type == PresetType.WhileHeld && _runner.Current == m) _runner.Stop();
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            try { Native.SetProcessDPIAware(); } catch { }   // 훅 좌표와 SetCursorPos 좌표를 똑같이(실제 픽셀) 맞춘다
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool tray = Array.IndexOf(args, "--tray") >= 0;   // 부팅 때 자동 실행이면 트레이로 조용히 시작
            Application.Run(new MainForm(tray));
        }
    }
}
