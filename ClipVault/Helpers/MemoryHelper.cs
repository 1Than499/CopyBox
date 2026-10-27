using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClipVault.Helpers
{
    /// <summary>
    /// 进程级极限轻量化内存管理助手：
    /// 当窗口关闭、收起至贴边或最小化到托盘时，主动释放未使用的托管堆与工作集物理内存，
    /// 确保常驻后台与前台交互均保持超低资源占用（~20MB-35MB）。
    /// </summary>
    public static class MemoryHelper
    {
        [DllImport("psapi.dll")]
        private static extern int EmptyWorkingSet(IntPtr hwProc);

        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

        /// <summary>
        /// 执行强制垃圾回收并收缩工作集物理内存
        /// </summary>
        public static void TrimWorkingSet()
        {
            try
            {
                // 1. 回收托管堆第 0, 1, 2 代与大对象堆 (LOH)
                GC.Collect(2, GCCollectionMode.Forced, false, false);
                GC.WaitForPendingFinalizers();

                // 2. 将进程未活跃使用的物理内存页归还给 Windows 系统
                IntPtr hProcess = Process.GetCurrentProcess().Handle;
                EmptyWorkingSet(hProcess);
            }
            catch { }
        }
    }
}
