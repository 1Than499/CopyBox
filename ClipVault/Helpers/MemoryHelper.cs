using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ClipVault.Helpers
{
    /// <summary>
    /// 进程级智能分级内存管理助手：
    /// 1. 常规/快速切换场景：保持热页常驻，取消待执行的深度修剪，保障 120FPS 边缘秒出；
    /// 2. 后台静默/隐藏 idle (>= 2.5s) 与大图 OCR 爆发后：释放闲置 WinRT OCR 引擎、执行 Gen2 + LOH 紧凑回收，
    ///    并温和收缩进程工作集 (SetProcessWorkingSetSizeEx)，使后台常驻内存长期维持低水位 (~25-45 MB)。
    /// </summary>
    public static class MemoryHelper
    {
        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(IntPtr hProcess, out PROCESS_MEMORY_COUNTERS_EX2 counters, uint cb);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, out VM_COUNTERS_EX2 processInformation, int processInformationLength, out int returnLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_MEMORY_COUNTERS_EX2
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
            public UIntPtr PrivateUsage;
            public UIntPtr PrivateWorkingSetSize;
            public ulong SharedCommitUsage;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct VM_COUNTERS_EX2
        {
            public UIntPtr PeakVirtualSize;
            public UIntPtr VirtualSize;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
            public UIntPtr PrivateUsage;
            public UIntPtr PrivateWorkingSetSize;
            public ulong SharedCommitUsage;
        }

        /// <summary>
        /// 零分配读取与 Windows 任务管理器「内存」列完全一致的专用工作集 (Private Working Set) 字节数。
        /// </summary>
        public static long GetPrivateWorkingSetBytes()
        {
            try
            {
                IntPtr hProc = GetCurrentProcess();
                uint ex2Size = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX2>();
                if (GetProcessMemoryInfo(hProc, out var pmc, ex2Size) && pmc.PrivateWorkingSetSize != UIntPtr.Zero)
                {
                    return (long)pmc.PrivateWorkingSetSize.ToUInt64();
                }

                int vmSize = Marshal.SizeOf<VM_COUNTERS_EX2>();
                if (NtQueryInformationProcess(hProc, 3 /* ProcessVmCounters */, out var vm, vmSize, out _) == 0 && vm.PrivateWorkingSetSize != UIntPtr.Zero)
                {
                    return (long)vm.PrivateWorkingSetSize.ToUInt64();
                }
            }
            catch { }

            return Environment.WorkingSet;
        }

        public static string GetFormattedMemoryUsage()
        {
            double mb = GetPrivateWorkingSetBytes() / (1024.0 * 1024.0);
            return mb.ToString("F1", CultureInfo.InvariantCulture) + " MB";
        }

        /// <summary>
        /// 为窗口底栏绑定极轻量的实时内存读数（仅在窗口可见时每 2 秒刷新，隐藏时彻底停表零唤醒；点击读数可手动整理内存）。
        /// </summary>
        public static void BindLiveMemoryMonitor(Window window, TextBlock? target)
        {
            if (window == null || target == null) return;

            void RefreshText()
            {
                target.Text = GetFormattedMemoryUsage();
            }

            var timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            timer.Tick += (_, _) =>
            {
                if (window.IsVisible) RefreshText();
                else timer.Stop();
            };

            window.IsVisibleChanged += (_, _) =>
            {
                if (window.IsVisible)
                {
                    RefreshText();
                    timer.Start();
                }
                else
                {
                    timer.Stop();
                }
            };

            window.Closed += (_, _) => timer.Stop();

            target.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                _ = Task.Run(() =>
                {
                    ExecuteDeepTrimCore();
                    window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RefreshText));
                });
            };

            RefreshText();
            if (window.IsVisible) timer.Start();
        }

        private static readonly object _syncLock = new();
        private static CancellationTokenSource? _idleTrimCts;
        private static DateTime _lastLightTrimUtc = DateTime.MinValue;
        private static DateTime _lastDeepTrimUtc = DateTime.MinValue;
        private static int _deepTrimRunning;

        /// <summary>
        /// 兼容旧调用点：执行节流后的轻量整理；若需后台深度释放，请使用 ScheduleIdleTrim。
        /// </summary>
        public static void TrimWorkingSet(bool deep = false)
        {
            var now = DateTime.UtcNow;
            if (!deep && (now - _lastLightTrimUtc).TotalSeconds < 12) return;
            if (deep && (now - _lastDeepTrimUtc).TotalSeconds < 10) return;

            if (deep)
            {
                _lastDeepTrimUtc = now;
                _ = Task.Run(() => ExecuteDeepTrimCore());
            }
            else
            {
                _lastLightTrimUtc = now;
                _ = Task.Run(() =>
                {
                    try
                    {
                        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                        GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: true);
                    }
                    catch { }
                });
            }
        }

        /// <summary>
        /// 窗口唤醒前调用：取消尚未触发的延迟深度修剪，避免刚打开面板时触发页换入。
        /// </summary>
        public static void CancelIdleTrim()
        {
            lock (_syncLock)
            {
                if (_idleTrimCts != null)
                {
                    try { _idleTrimCts.Cancel(); } catch { }
                    try { _idleTrimCts.Dispose(); } catch { }
                    _idleTrimCts = null;
                }
            }
        }

        /// <summary>
        /// 窗口隐藏进入后台时调用：延迟指定毫秒后若仍未重新唤醒，则执行深度托管堆压缩 + 工作集回收。
        /// </summary>
        public static void ScheduleIdleTrim(int delayMs = 2500)
        {
            CancellationToken token;
            lock (_syncLock)
            {
                if (_idleTrimCts != null)
                {
                    try { _idleTrimCts.Cancel(); } catch { }
                    try { _idleTrimCts.Dispose(); } catch { }
                }
                _idleTrimCts = new CancellationTokenSource();
                token = _idleTrimCts.Token;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delayMs, token).ConfigureAwait(false);
                    if (token.IsCancellationRequested) return;

                    var now = DateTime.UtcNow;
                    if ((now - _lastDeepTrimUtc).TotalSeconds < 8) return;
                    _lastDeepTrimUtc = now;

                    ExecuteDeepTrimCore();
                }
                catch (OperationCanceledException) { }
                catch { }
            }, token);
        }

        /// <summary>
        /// 后台异步大图捕获 / WinRT 离线 OCR 完成后调用：在无窗口交互时安排一次温和的 Gen2/LOH 回收与工作集归还。
        /// </summary>
        public static void NotifyBackgroundMemoryPressure()
        {
            bool anyWindowVisible = false;
            try
            {
                var app = System.Windows.Application.Current;
                if (app != null)
                {
                    app.Dispatcher.Invoke(() =>
                    {
                        foreach (System.Windows.Window win in app.Windows)
                        {
                            if (win.IsVisible)
                            {
                                anyWindowVisible = true;
                                break;
                            }
                        }
                    });
                }
            }
            catch { }

            if (!anyWindowVisible)
            {
                ScheduleIdleTrim(3000);
            }
            else
            {
                TrimWorkingSet(deep: false);
            }
        }

        private static void ExecuteDeepTrimCore()
        {
            if (Interlocked.Exchange(ref _deepTrimRunning, 1) == 1) return;
            try
            {
                // 1. 释放可按需重建的闲置 WinRT OCR 引擎及语言模型 COM 引用
                ClipVault.Services.NativeOcrService.ReleaseIdleEngine();

                // 2. 触发 LOH 大对象堆紧缩 + Gen2 回收 + 终结器等待（释放 WinRT COM / GDI+ 句柄）
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

                // 3. 将闲置冷物理页归还给 Windows Standby List（下次唤醒直接从内存软缺页恢复，零磁盘卡顿）
                try
                {
                    using var proc = Process.GetCurrentProcess();
                    EmptyWorkingSet(proc.Handle);
                }
                catch { }
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _deepTrimRunning, 0);
            }
        }
    }
}
