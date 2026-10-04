// Bearly core engine - compiled at runtime by Windows PowerShell 5.1 (C# 5 syntax only).
// Nothing here makes permanent system changes: processes are killed, services are
// stopped (start type untouched), caches are deleted. A reboot restores everything.
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Bearly
{
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength; public uint dwMemoryLoad;
            public ulong ullTotalPhys; public ulong ullAvailPhys;
            public ulong ullTotalPageFile; public ulong ullAvailPageFile;
            public ulong ullTotalVirtual; public ulong ullAvailVirtual; public ulong ullAvailExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PERFORMANCE_INFORMATION
        {
            public uint cb;
            public UIntPtr CommitTotal; public UIntPtr CommitLimit; public UIntPtr CommitPeak;
            public UIntPtr PhysicalTotal; public UIntPtr PhysicalAvailable; public UIntPtr SystemCache;
            public UIntPtr KernelTotal; public UIntPtr KernelPaged; public UIntPtr KernelNonpaged;
            public UIntPtr PageSize;
            public uint HandleCount; public uint ProcessCount; public uint ThreadCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID Luid; public uint Attributes; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
        [DllImport("kernel32.dll")] public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        [DllImport("psapi.dll", SetLastError = true)] public static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION pi, uint cb);
        [DllImport("psapi.dll")] public static extern bool EmptyWorkingSet(IntPtr hProcess);
        [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("advapi32.dll", SetLastError = true)] public static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool LookupPrivilegeValue(string sys, string name, out LUID luid);
        [DllImport("advapi32.dll", SetLastError = true)] public static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, uint len, IntPtr prev, IntPtr retLen);
        [DllImport("ntdll.dll")] public static extern int NtSetSystemInformation(int infoClass, ref int info, int length);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetSystemFileCacheSize(IntPtr min, IntPtr max, uint flags);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHQueryRecycleBin(string root, ref SHQUERYRBINFO info);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, uint flags);
        [DllImport("dnsapi.dll")] public static extern int DnsFlushResolverCache();
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    }

    public class Stats
    {
        public double Cpu;
        public double RamUsedGB;
        public double RamTotalGB;
        public int Processes;
        public double DiskFreeGB;
        public double DiskTotalGB;
    }

    public static class SysMonitor
    {
        const double GB = 1073741824.0;
        static long _idle, _kernel, _user;
        static bool _primed;

        // Called from the UI thread only (CPU delta state is not shared with the engine).
        public static Stats Read()
        {
            var s = new Stats();
            long idle, kernel, user;
            if (Native.GetSystemTimes(out idle, out kernel, out user))
            {
                if (_primed)
                {
                    long di = idle - _idle;
                    long dt = (kernel - _kernel) + (user - _user);
                    s.Cpu = dt > 0 ? Math.Max(0.0, Math.Min(100.0, (1.0 - (double)di / dt) * 100.0)) : 0.0;
                }
                _idle = idle; _kernel = kernel; _user = user; _primed = true;
            }
            ulong total, avail;
            if (Mem(out total, out avail))
            {
                s.RamTotalGB = total / GB;
                s.RamUsedGB = (total - avail) / GB;
            }
            s.Processes = ProcCount();
            try
            {
                var d = new DriveInfo("C");
                s.DiskFreeGB = d.AvailableFreeSpace / GB;
                s.DiskTotalGB = d.TotalSize / GB;
            }
            catch { }
            return s;
        }

        public static bool Mem(out ulong total, out ulong avail)
        {
            var m = new Native.MEMORYSTATUSEX();
            m.dwLength = (uint)Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX));
            if (Native.GlobalMemoryStatusEx(ref m)) { total = m.ullTotalPhys; avail = m.ullAvailPhys; return true; }
            total = 0; avail = 0; return false;
        }

        public static double RamUsedGB()
        {
            ulong t, a;
            return Mem(out t, out a) ? (t - a) / GB : 0.0;
        }

        public static int ProcCount()
        {
            Native.PERFORMANCE_INFORMATION pi;
            if (Native.GetPerformanceInfo(out pi, (uint)Marshal.SizeOf(typeof(Native.PERFORMANCE_INFORMATION))))
                return (int)pi.ProcessCount;
            return 0;
        }
    }

    public class Engine
    {
        public readonly ConcurrentQueue<string> Log = new ConcurrentQueue<string>();
        public string[] KillList = new string[0];
        public string[] ProtectList = new string[0];
        public string[] ServiceList = new string[0];
        public string[] ExtraCleanPaths = new string[0];
        public bool IsAdmin;
        public string ResultBig = "";
        public string ResultDetail = "";

        private volatile bool _busy;
        public bool Busy { get { return _busy; } }

        // Hard safety net - never touched even if a wildcard in config matches them.
        static readonly string[] NeverStop = {
            "Audiosrv", "AudioEndpointBuilder", "Dhcp", "Dnscache", "nsi", "Winmgmt", "RpcSs", "RpcEptMapper",
            "DcomLaunch", "EventLog", "BFE", "mpssvc", "WinDefend", "WdNisSvc", "SecurityHealthService", "LSM",
            "Power", "PlugPlay", "ProfSvc", "Schedule", "SystemEventsBroker", "Themes", "UserManager",
            "CoreMessagingRegistrar", "StateRepository", "TimeBrokerSvc", "WlanSvc", "netprofm", "NlaSvc",
            "LanmanWorkstation", "CryptSvc", "KeyIso", "SamSs", "gpsvc", "EventSystem", "FontCache", "Wcmsvc",
            "AppXSvc", "ClipSVC", "BrokerInfrastructure", "TokenBroker", "XboxGipSvc", "GamingServices",
            "GamingServicesNet", "hidserv", "TabletInputService", "TextInputManagementService", "WpnService",
            "WpnUserService*", "cbdhsvc*", "DispBrokerDesktopSvc", "DisplayEnhancementService", "AMD External Events Utility"
        };

        static readonly string[] AlwaysCacheNames = {
            "GPUCache", "Code Cache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache",
            "GrShaderCache", "GraphiteDawnCache", "ShaderCache"
        };

        static readonly string[] SweepSkip = {
            "Packages", "node_modules", "Temp", "WindowsApps", ".git", "Programs", "Windows",
            "site-packages", "Lib", "venv", ".venv", "Sdk", "Android"
        };

        const double GB = 1073741824.0;

        void L(string kind, string text) { Log.Enqueue(kind + "|" + text); }

        public void StartZero()
        {
            if (_busy) return;
            _busy = true;
            Task.Run(() => Guard(RunZero));
        }

        public void StartClean()
        {
            if (_busy) return;
            _busy = true;
            Task.Run(() => Guard(RunClean));
        }

        void Guard(Action a)
        {
            try { a(); }
            catch (Exception ex) { L("warn", "Error: " + ex.Message); }
            finally { _busy = false; }
        }

        public static void TrimSelf()
        {
            try { Native.EmptyWorkingSet(Native.GetCurrentProcess()); } catch { }
        }

        public static void SetAppId(string id)
        {
            try { Native.SetCurrentProcessExplicitAppUserModelID(id); } catch { }
        }

        // ------------------------------------------------------------------ ZERO MODE

        void RunZero()
        {
            ResultBig = ""; ResultDetail = "";
            double ramBefore = SysMonitor.RamUsedGB();
            int procBefore = SysMonitor.ProcCount();

            var kill = Compile(KillList);
            var prot = Compile(ProtectList);
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var mem = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            L("head", "Killing dev stack & background apps");
            int killed = KillPass(kill, prot, counts, mem);
            Thread.Sleep(700);
            killed += KillPass(kill, prot, counts, mem); // catch respawned children
            LogKills(counts, mem);
            if (killed == 0) L("info", "Nothing from the kill list was running");

            int stopped = 0;
            L("head", "Pausing background services");
            if (IsAdmin) stopped = StopServices();
            else L("warn", "Not running as admin - services skipped");

            ShutdownWsl();

            var late = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var lateMem = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            int extra = KillPass(kill, prot, late, lateMem);
            if (extra > 0) { killed += extra; LogKills(late, lateMem); }

            L("head", "Flushing memory");
            PurgeMemory();
            Thread.Sleep(1300);

            double ramAfter = SysMonitor.RamUsedGB();
            int procAfter = SysMonitor.ProcCount();
            double freed = Math.Max(0.0, ramBefore - ramAfter);

            ResultBig = "\u2212" + freed.ToString("0.0") + " GB RAM";
            ResultDetail = string.Format("RAM {0:0.0} \u2192 {1:0.0} GB  \u00B7  {2} processes killed  \u00B7  {3} services paused  \u00B7  {4} \u2192 {5} running",
                ramBefore, ramAfter, killed, stopped, procBefore, procAfter);
            L("head", "Done. Restart the PC and everything comes back.");
        }

        int KillPass(List<Regex> kill, List<Regex> prot, Dictionary<string, int> counts, Dictionary<string, long> mem)
        {
            int self = Process.GetCurrentProcess().Id;
            int total = 0;
            Process[] all;
            try { all = Process.GetProcesses(); } catch { return 0; }
            foreach (var p in all)
            {
                try
                {
                    if (p.Id == self || p.Id <= 4) continue;
                    string n = p.ProcessName;
                    if (!Any(kill, n) || Any(prot, n)) continue;
                    long ws = 0;
                    try { ws = p.WorkingSet64; } catch { }
                    p.Kill();
                    total++;
                    int c; counts.TryGetValue(n, out c); counts[n] = c + 1;
                    long b; mem.TryGetValue(n, out b); mem[n] = b + ws;
                }
                catch { }
                finally { p.Dispose(); }
            }
            return total;
        }

        void LogKills(Dictionary<string, int> counts, Dictionary<string, long> mem)
        {
            var names = new List<string>(counts.Keys);
            names.Sort((a, b) => mem[b].CompareTo(mem[a]));
            foreach (var n in names)
            {
                string x = counts[n] > 1 ? "  \u00D7" + counts[n] : "";
                L("ok", n + x + "  \u00B7  " + Fmt(mem[n]));
            }
        }

        int StopServices()
        {
            var want = Compile(ServiceList);
            var never = Compile(NeverStop);
            var targets = new List<ServiceController>();
            ServiceController[] all;
            try { all = ServiceController.GetServices(); } catch { return 0; }

            foreach (var s in all)
            {
                bool take = false;
                try
                {
                    take = Any(want, s.ServiceName) && !Any(never, s.ServiceName)
                        && s.Status == ServiceControllerStatus.Running && s.CanStop;
                }
                catch { }
                if (take) targets.Add(s); else s.Dispose();
            }

            if (targets.Count == 0) { L("info", "No target services were running"); return 0; }

            var tasks = new Task[targets.Count];
            for (int i = 0; i < targets.Count; i++)
            {
                var sc = targets[i];
                tasks[i] = Task.Run(() =>
                {
                    try { sc.Stop(); sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10)); }
                    catch { }
                });
            }
            try { Task.WaitAll(tasks, 14000); } catch { }

            int stopped = 0;
            foreach (var sc in targets)
            {
                try
                {
                    sc.Refresh();
                    if (sc.Status == ServiceControllerStatus.Stopped || sc.Status == ServiceControllerStatus.StopPending)
                    {
                        stopped++;
                        L("ok", sc.DisplayName);
                    }
                    else L("warn", sc.DisplayName + " refused to stop");
                }
                catch { }
            }
            return stopped;
        }

        void ShutdownWsl()
        {
            bool running = false;
            foreach (var n in new[] { "vmmem", "vmmemWSL", "wslservice", "wslhost" })
            {
                Process[] ps;
                try { ps = Process.GetProcessesByName(n); } catch { continue; }
                if (ps.Length > 0) running = true;
                foreach (var p in ps) p.Dispose();
            }
            if (!running) return;
            string wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
            if (!File.Exists(wsl)) return;
            try
            {
                var psi = new ProcessStartInfo(wsl, "--shutdown")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (var p = Process.Start(psi)) { p.WaitForExit(15000); }
                L("ok", "WSL / Docker virtual machine shut down");
            }
            catch { }
        }

        void PurgeMemory()
        {
            if (!IsAdmin)
            {
                foreach (var p in Process.GetProcesses())
                {
                    try { Native.EmptyWorkingSet(p.Handle); } catch { } finally { p.Dispose(); }
                }
                L("ok", "Working sets trimmed (limited mode)");
                return;
            }
            EnablePriv("SeProfileSingleProcessPrivilege");
            EnablePriv("SeIncreaseQuotaPrivilege");

            int r1 = MemCmd(2); // MemoryEmptyWorkingSets
            bool fc = false;
            try { fc = Native.SetSystemFileCacheSize(new IntPtr(-1), new IntPtr(-1), 0); } catch { }
            MemCmd(3);          // MemoryFlushModifiedList
            int r4 = MemCmd(4); // MemoryPurgeStandbyList
            MemCmd(5);          // MemoryPurgeLowPriorityStandbyList

            if (r1 == 0) L("ok", "Every working set flushed");
            if (fc) L("ok", "System file cache dropped");
            if (r4 == 0) L("ok", "Standby list purged");
            else L("warn", "Standby purge failed (0x" + r4.ToString("X8") + ")");
        }

        static int MemCmd(int c)
        {
            int v = c;
            try { return Native.NtSetSystemInformation(80, ref v, 4); } catch { return -1; }
        }

        static bool EnablePriv(string name)
        {
            IntPtr tok;
            if (!Native.OpenProcessToken(Native.GetCurrentProcess(), 0x0020 | 0x0008, out tok)) return false;
            try
            {
                var tp = new Native.TOKEN_PRIVILEGES();
                tp.PrivilegeCount = 1;
                tp.Attributes = 0x00000002; // SE_PRIVILEGE_ENABLED
                if (!Native.LookupPrivilegeValue(null, name, out tp.Luid)) return false;
                return Native.AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)
                    && Marshal.GetLastWin32Error() == 0;
            }
            finally { Native.CloseHandle(tok); }
        }

        // ----------------------------------------------------------------- DEEP CLEAN

        class Target
        {
            public string Label, Path, Pattern;
            public bool RemoveRoot;
            public DateTime Cutoff;
        }

        List<Target> _targets;
        Dictionary<string, bool> _seen;
        string[] _forbidden;

        void RunClean()
        {
            ResultBig = ""; ResultDetail = "";
            long diskBefore = FreeC();
            long total = 0;

            L("head", "Wiping caches");
            if (IsAdmin)
            {
                foreach (var s in new[] { "wuauserv", "UsoSvc", "bits", "DoSvc" }) StopQuiet(s);
            }

            BuildTargets();

            var order = new List<string>();
            var groups = new Dictionary<string, List<Target>>();
            foreach (var t in _targets)
            {
                List<Target> g;
                if (!groups.TryGetValue(t.Label, out g)) { g = new List<Target>(); groups[t.Label] = g; order.Add(t.Label); }
                g.Add(t);
            }

            foreach (var label in order)
            {
                long sum = 0;
                foreach (var t in groups[label])
                {
                    try { sum += t.Pattern == null ? Wipe(t.Path, t.RemoveRoot, t.Cutoff) : WipeFiles(t.Path, t.Pattern); }
                    catch { }
                }
                total += sum;
                if (sum > 0) L("ok", label + "  \u00B7  " + Fmt(sum));
                else L("info", label + "  \u00B7  already clean");
            }

            long rb = EmptyRecycleBin();
            total += rb;
            if (rb > 0) L("ok", "Recycle Bin  \u00B7  " + Fmt(rb)); else L("info", "Recycle Bin  \u00B7  already empty");

            try { Native.DnsFlushResolverCache(); L("ok", "DNS cache flushed"); } catch { }

            long diskAfter = FreeC();
            ResultBig = Fmt(total) + " vanished";
            ResultDetail = string.Format("C: free space {0:0.00} GB \u2192 {1:0.00} GB  \u00B7  locked files in use were skipped",
                diskBefore / GB, diskAfter / GB);
            L("head", "Done.");
        }

        void BuildTargets()
        {
            _targets = new List<Target>();
            _seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            string la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string ra = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string temp = Path.GetTempPath();

            _forbidden = new[] { la, ra, win, pd, up, Path.GetPathRoot(win), temp.TrimEnd('\\') + "\\.." };
            DateTime any = DateTime.MaxValue;
            DateTime twoHours = DateTime.UtcNow.AddHours(-2);

            Add("Temp files", temp, twoHours);
            Add("Temp files", Path.Combine(win, "Temp"), twoHours);

            Add("Windows Update cache", Path.Combine(win, @"SoftwareDistribution\Download"), any);
            Add("Delivery Optimization", Path.Combine(win, @"SoftwareDistribution\DeliveryOptimization"), any);
            Add("Delivery Optimization", Path.Combine(win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"), any);

            const string dumps = "Crash dumps & error reports";
            Add(dumps, Path.Combine(la, "CrashDumps"), any);
            Add(dumps, Path.Combine(win, "Minidump"), any);
            Add(dumps, Path.Combine(win, "LiveKernelReports"), any);
            AddFiles(dumps, win, "MEMORY.DMP");
            Add(dumps, Path.Combine(pd, @"Microsoft\Windows\WER\ReportArchive"), any);
            Add(dumps, Path.Combine(pd, @"Microsoft\Windows\WER\ReportQueue"), any);
            Add(dumps, Path.Combine(pd, @"Microsoft\Windows\WER\Temp"), any);
            Add(dumps, Path.Combine(la, @"Microsoft\Windows\WER"), any);

            const string logs = "Windows logs";
            Add(logs, Path.Combine(win, @"Logs\CBS"), any);
            Add(logs, Path.Combine(win, @"Logs\DISM"), any);
            Add(logs, Path.Combine(win, @"Logs\WindowsUpdate"), any);

            const string shader = "GPU shader caches";
            Add(shader, Path.Combine(la, "D3DSCache"), any);
            foreach (var n in new[] { "DxCache", "DxcCache", "VkCache", "GLCache", "OglCache" })
                Add(shader, Path.Combine(la, @"AMD\" + n), any);
            Add(shader, Path.Combine(la, @"NVIDIA\DXCache"), any);
            Add(shader, Path.Combine(la, @"NVIDIA\GLCache"), any);
            Add(shader, Path.Combine(up, @"AppData\LocalLow\NVIDIA\PerDriverVersion\DXCache"), any);
            Add(shader, Path.Combine(pd, @"NVIDIA Corporation\NV_Cache"), any);

            const string thumbs = "Thumbnail & web cache";
            AddFiles(thumbs, Path.Combine(la, @"Microsoft\Windows\Explorer"), "thumbcache_*.db");
            AddFiles(thumbs, Path.Combine(la, @"Microsoft\Windows\Explorer"), "iconcache_*.db");
            Add(thumbs, Path.Combine(la, @"Microsoft\Windows\INetCache"), any);

            const string dev = "Dev caches (npm, pip, uv, go, yarn...)";
            Add(dev, Path.Combine(la, @"npm-cache\_cacache"), any);
            Add(dev, Path.Combine(la, @"npm-cache\_logs"), any);
            Add(dev, Path.Combine(la, @"pip\cache"), any);
            Add(dev, Path.Combine(la, @"uv\cache"), any);
            Add(dev, Path.Combine(la, @"Yarn\Cache"), any);
            Add(dev, Path.Combine(la, "go-build"), any);
            Add(dev, Path.Combine(la, @"NuGet\v3-cache"), any);
            Add(dev, Path.Combine(la, @"NuGet\plugins-cache"), any);
            Add(dev, Path.Combine(la, @"electron\Cache"), any);
            Add(dev, Path.Combine(la, @"electron-builder\Cache"), any);
            Add(dev, Path.Combine(la, @"node-gyp\Cache"), any);
            Add(dev, Path.Combine(la, "pnpm-cache"), any);
            Add(dev, Path.Combine(la, @"Microsoft\TypeScript"), any);
            Add(dev, Path.Combine(up, @".bun\install\cache"), any);

            const string editors = "Editor caches & logs";
            foreach (var app in new[] { "Code", "Code - Insiders", "Cursor", "Windsurf", "Antigravity", "Kiro", "Trae" })
            {
                string b = Path.Combine(ra, app);
                Add(editors, Path.Combine(b, "logs"), any);
                Add(editors, Path.Combine(b, "CachedData"), any);
                Add(editors, Path.Combine(b, "CachedExtensionVSIXs"), any);
                Add(editors, Path.Combine(b, @"Crashpad\reports"), any);
            }

            const string adobe = "Adobe media cache";
            foreach (var n in new[] { "Media Cache Files", "Media Cache", "Peak Files", "Analyzer Cache Files" })
                Add(adobe, Path.Combine(ra, @"Adobe\Common\" + n), any);

            const string apps = "App caches (Spotify, Steam, Epic, Roblox)";
            Add(apps, Path.Combine(la, @"Spotify\Storage"), any);
            Add(apps, Path.Combine(la, @"Steam\htmlcache"), any);
            Add(apps, Path.Combine(la, @"Roblox\logs"), any);
            AddDirs(apps, Path.Combine(la, @"EpicGamesLauncher\Saved"), "webcache*");

            if (ExtraCleanPaths != null)
                foreach (var p in ExtraCleanPaths)
                    if (!string.IsNullOrWhiteSpace(p)) Add("Custom paths", Environment.ExpandEnvironmentVariables(p), any);

            const string web = "Browser & Electron caches";
            foreach (var root in new[] { la, ra })
            {
                try { Sweep(web, new DirectoryInfo(root), 0); } catch { }
            }
        }

        bool Safe(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length < 8) return false;
            string full;
            try { full = Path.GetFullPath(path).TrimEnd('\\'); } catch { return false; }
            foreach (var f in _forbidden)
            {
                try { if (string.Equals(Path.GetFullPath(f).TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase)) return false; } catch { }
            }
            if (_seen.ContainsKey(full)) return false;
            _seen[full] = true;
            return true;
        }

        void Add(string label, string path, DateTime cutoff)
        {
            if (!Safe(path)) return;
            _targets.Add(new Target { Label = label, Path = path, Pattern = null, RemoveRoot = false, Cutoff = cutoff });
        }

        void AddFiles(string label, string dir, string pattern)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            _targets.Add(new Target { Label = label, Path = dir, Pattern = pattern, RemoveRoot = false, Cutoff = DateTime.MaxValue });
        }

        void AddDirs(string label, string parent, string pattern)
        {
            try
            {
                if (!Directory.Exists(parent)) return;
                foreach (var d in new DirectoryInfo(parent).GetDirectories(pattern)) Add(label, d.FullName, DateTime.MaxValue);
            }
            catch { }
        }

        void Sweep(string label, DirectoryInfo d, int depth)
        {
            if (depth > 6) return;
            DirectoryInfo[] subs;
            try { subs = d.GetDirectories(); } catch { return; }
            int profile = -1; // -1 unknown, 0 no, 1 yes
            foreach (var s in subs)
            {
                try { if ((s.Attributes & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
                string n = s.Name;
                if (Contains(AlwaysCacheNames, n)) { Add(label, s.FullName, DateTime.MaxValue); continue; }
                if (string.Equals(n, "Cache", StringComparison.OrdinalIgnoreCase))
                {
                    if (profile < 0) profile = IsChromiumProfile(d) ? 1 : 0;
                    if (profile == 1) { Add(label, s.FullName, DateTime.MaxValue); continue; }
                }
                if (string.Equals(n, "Service Worker", StringComparison.OrdinalIgnoreCase))
                {
                    Add(label, Path.Combine(s.FullName, "CacheStorage"), DateTime.MaxValue);
                    Add(label, Path.Combine(s.FullName, "ScriptCache"), DateTime.MaxValue);
                    continue;
                }
                if (Contains(SweepSkip, n)) continue;
                Sweep(label, s, depth + 1);
            }
        }

        static bool IsChromiumProfile(DirectoryInfo d)
        {
            string p = d.FullName;
            return Directory.Exists(Path.Combine(p, "GPUCache")) || Directory.Exists(Path.Combine(p, "Code Cache"))
                || File.Exists(Path.Combine(p, "Preferences")) || Directory.Exists(Path.Combine(p, "Local Storage"));
        }

        static long Wipe(string dir, bool removeRoot, DateTime cutoff)
        {
            DirectoryInfo di;
            try
            {
                di = new DirectoryInfo(dir);
                if (!di.Exists) return 0;
                if ((di.Attributes & FileAttributes.ReparsePoint) != 0) return 0;
            }
            catch { return 0; }

            long freed = 0;
            FileInfo[] files = new FileInfo[0];
            try { files = di.GetFiles(); } catch { }
            foreach (var f in files) freed += Del(f, cutoff);

            DirectoryInfo[] subs = new DirectoryInfo[0];
            try { subs = di.GetDirectories(); } catch { }
            foreach (var s in subs)
            {
                try { if ((s.Attributes & FileAttributes.ReparsePoint) != 0) continue; } catch { continue; }
                freed += Wipe(s.FullName, true, cutoff);
            }

            if (removeRoot) { try { di.Delete(false); } catch { } }
            return freed;
        }

        static long WipeFiles(string dir, string pattern)
        {
            long freed = 0;
            try
            {
                if (!Directory.Exists(dir)) return 0;
                foreach (var f in new DirectoryInfo(dir).GetFiles(pattern)) freed += Del(f, DateTime.MaxValue);
            }
            catch { }
            return freed;
        }

        static long Del(FileInfo f, DateTime cutoff)
        {
            try
            {
                if (cutoff != DateTime.MaxValue && f.LastWriteTimeUtc > cutoff) return 0;
                long len = f.Length;
                if ((f.Attributes & (FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden)) != 0)
                    f.Attributes = FileAttributes.Normal;
                f.Delete();
                return len;
            }
            catch { return 0; }
        }

        static long EmptyRecycleBin()
        {
            var info = new Native.SHQUERYRBINFO();
            info.cbSize = Marshal.SizeOf(typeof(Native.SHQUERYRBINFO));
            long size = 0;
            try { if (Native.SHQueryRecycleBin(null, ref info) == 0) size = info.i64Size; } catch { }
            if (size <= 0 && info.i64NumItems <= 0) return 0;
            try { Native.SHEmptyRecycleBin(IntPtr.Zero, null, 0x7); } catch { } // no confirm / progress / sound
            return size;
        }

        static void StopQuiet(string name)
        {
            try
            {
                using (var sc = new ServiceController(name))
                {
                    if (sc.Status == ServiceControllerStatus.Running && sc.CanStop)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(8));
                    }
                }
            }
            catch { }
        }

        // ------------------------------------------------------------------- helpers

        static long FreeC()
        {
            try { return new DriveInfo("C").AvailableFreeSpace; } catch { return 0; }
        }

        static List<Regex> Compile(string[] pats)
        {
            var list = new List<Regex>();
            if (pats == null) return list;
            foreach (var p in pats)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                string rx = "^" + Regex.Escape(p.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                list.Add(new Regex(rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            }
            return list;
        }

        static bool Any(List<Regex> rx, string s)
        {
            if (s == null) return false;
            foreach (var r in rx) if (r.IsMatch(s)) return true;
            return false;
        }

        static bool Contains(string[] arr, string s)
        {
            foreach (var a in arr) if (string.Equals(a, s, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static string Fmt(long bytes)
        {
            if (bytes >= 1073741824L) return (bytes / GB).ToString("0.00") + " GB";
            if (bytes >= 1048576L) return (bytes / 1048576.0).ToString("0") + " MB";
            if (bytes > 0) return Math.Max(1.0, bytes / 1024.0).ToString("0") + " KB";
            return "0 MB";
        }
    }
}
