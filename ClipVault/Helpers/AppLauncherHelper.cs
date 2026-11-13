using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ClipVault.Interop;
using ClipVault.Models;

namespace ClipVault.Helpers
{
    /// <summary>
    /// 自定义快捷动作启动器：支持优先检测并前置已运行的程序窗口（避免重复多开），若未运行则正常启动新实例
    /// </summary>
    public static class AppLauncherHelper
    {
        private static readonly HashSet<string> ExcludedShellClasses = new(StringComparer.OrdinalIgnoreCase)
        {
            "Progman",
            "WorkerW",
            "Shell_TrayWnd",
            "Shell_SecondaryTrayWnd",
            "Windows.UI.Core.CoreWindow"
        };

        private static readonly Dictionary<string, string[]> KnownProcessAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["calc"] = new[] { "CalculatorApp", "win32calc", "calc" },
            ["wt"] = new[] { "WindowsTerminal", "wt" },
            ["taskmgr"] = new[] { "Taskmgr" },
            ["edge"] = new[] { "msedge" },
            ["vscode"] = new[] { "Code" }
        };

        /// <summary>
        /// 执行快捷动作，返回 true 表示已成功唤醒正在运行的已有实例，false 表示新启动了程序或指令
        /// </summary>
        public static bool ExecuteCustomAction(CustomActionItem action)
        {
            string raw = Environment.ExpandEnvironmentVariables((action.CommandOrPath ?? string.Empty).Trim());
            if (string.IsNullOrEmpty(raw))
            {
                throw new InvalidOperationException("执行目标不能为空");
            }

            if (action.ActivateExistingInstance && !IsUriOrProtocol(raw))
            {
                if (TryActivateExistingProcess(raw))
                {
                    return true;
                }
            }

            var (launchTarget, launchArgs) = SplitCommandAndArguments(raw);
            Process.Start(new ProcessStartInfo
            {
                FileName = launchTarget,
                Arguments = launchArgs,
                UseShellExecute = true
            });
            return false;
        }

        public static bool TryActivateExistingProcess(string rawCommandOrPath)
        {
            var (exeToken, _) = SplitCommandAndArguments(rawCommandOrPath);
            if (string.IsNullOrWhiteSpace(exeToken) || IsUriOrProtocol(exeToken))
            {
                return false;
            }

            string? fullExePath = ResolveFullExePath(exeToken);
            string effectiveToken = fullExePath ?? exeToken;
            string ext = Path.GetExtension(effectiveToken);

            // 若目标明确是文档/图片/文件夹等非可执行文件，交由 Shell 默认处理而不误匹配宿主进程
            if (!string.IsNullOrEmpty(ext) && !ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (Directory.Exists(effectiveToken))
            {
                return false;
            }

            string baseName = Path.GetFileNameWithoutExtension(effectiveToken).Trim();
            if (string.IsNullOrWhiteSpace(baseName))
            {
                return false;
            }

            var procNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { baseName };
            if (KnownProcessAliases.TryGetValue(baseName, out var aliases))
            {
                foreach (var a in aliases) procNames.Add(a);
            }

            int selfPid = Environment.ProcessId;
            var exactPathPids = new HashSet<int>();
            var inaccessiblePathPids = new HashSet<int>();
            var allNamePids = new HashSet<int>();

            foreach (string procName in procNames)
            {
                Process[] procs;
                try
                {
                    procs = Process.GetProcessesByName(procName);
                }
                catch
                {
                    continue;
                }

                foreach (var p in procs)
                {
                    try
                    {
                        if (p.Id == selfPid) continue;
                        allNamePids.Add(p.Id);

                        if (!string.IsNullOrEmpty(fullExePath))
                        {
                            try
                            {
                                string? modulePath = p.MainModule?.FileName;
                                if (!string.IsNullOrEmpty(modulePath) &&
                                    string.Equals(Path.GetFullPath(modulePath), fullExePath, StringComparison.OrdinalIgnoreCase))
                                {
                                    exactPathPids.Add(p.Id);
                                }
                            }
                            catch
                            {
                                // 高权限进程（如任务管理器）无法读取 MainModule，保留 PID 作为兜底匹配
                                inaccessiblePathPids.Add(p.Id);
                            }
                        }
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }

            if (allNamePids.Count == 0)
            {
                return false;
            }

            // 若已知完整 .exe 路径，优先匹配完全同路径实例（若无则仅补充无权读取路径的高权限实例）
            HashSet<int> candidatePids;
            if (!string.IsNullOrEmpty(fullExePath))
            {
                candidatePids = new HashSet<int>(exactPathPids);
                if (candidatePids.Count == 0)
                {
                    candidatePids.UnionWith(inaccessiblePathPids);
                }
                // 部分 UWP / 别名命令（如 calc 启动 CalculatorApp.exe）与 Command 字符串路径不同，允许使用别名 PID
                if (candidatePids.Count == 0 && KnownProcessAliases.ContainsKey(baseName))
                {
                    candidatePids = allNamePids;
                }
            }
            else
            {
                candidatePids = allNamePids;
            }

            if (candidatePids.Count == 0)
            {
                return false;
            }

            IntPtr targetHwnd = FindBestTopLevelWindow(candidatePids);
            if (targetHwnd == IntPtr.Zero)
            {
                return false;
            }

            BringWindowToFront(targetHwnd);
            return true;
        }

        private static bool IsUriOrProtocol(string raw)
        {
            return raw.Contains("://") || (raw.EndsWith(":") && !raw.Contains("\\") && !raw.Contains("/"));
        }

        private static (string target, string args) SplitCommandAndArguments(string raw)
        {
            string trimmed = raw.Trim();
            if (trimmed.StartsWith("\""))
            {
                int endQuote = trimmed.IndexOf('"', 1);
                if (endQuote > 1)
                {
                    string target = trimmed.Substring(1, endQuote - 1).Trim();
                    string args = trimmed.Substring(endQuote + 1).Trim();
                    return (target, args);
                }
            }

            if (File.Exists(trimmed) || Directory.Exists(trimmed))
            {
                return (trimmed, string.Empty);
            }

            string[] exts = { ".exe", ".lnk", ".bat", ".cmd" };
            foreach (string ext in exts)
            {
                int idx = trimmed.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
                if (idx > 0)
                {
                    int after = idx + ext.Length;
                    if (after == trimmed.Length || char.IsWhiteSpace(trimmed[after]))
                    {
                        return (trimmed.Substring(0, after).Trim(), trimmed.Substring(after).Trim());
                    }
                }
            }

            int spaceIdx = trimmed.IndexOf(' ');
            if (spaceIdx > 0)
            {
                return (trimmed.Substring(0, spaceIdx).Trim(), trimmed.Substring(spaceIdx + 1).Trim());
            }

            return (trimmed, string.Empty);
        }

        private static string? ResolveFullExePath(string token)
        {
            try
            {
                string candidate = token.Trim();
                if (candidate.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
                {
                    string? lnkTarget = ResolveShortcutTarget(candidate);
                    if (!string.IsNullOrEmpty(lnkTarget))
                    {
                        candidate = lnkTarget;
                    }
                }

                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }

                if (!candidate.Contains("\\") && !candidate.Contains("/"))
                {
                    string withExe = candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? candidate : candidate + ".exe";
                    string p1 = Path.Combine(Environment.SystemDirectory, withExe);
                    if (File.Exists(p1)) return Path.GetFullPath(p1);

                    string p2 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), withExe);
                    if (File.Exists(p2)) return Path.GetFullPath(p2);

                    string? pathEnv = Environment.GetEnvironmentVariable("PATH");
                    if (!string.IsNullOrEmpty(pathEnv))
                    {
                        foreach (string dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
                        {
                            try
                            {
                                string p = Path.Combine(dir.Trim(), withExe);
                                if (File.Exists(p)) return Path.GetFullPath(p);
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static string? ResolveShortcutTarget(string lnkPath)
        {
            object? shell = null;
            object? shortcut = null;
            try
            {
                Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return null;
                shell = Activator.CreateInstance(shellType);
                if (shell == null) return null;

                shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                if (shortcut == null) return null;

                return shortcut.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null) as string;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.ReleaseComObject(shortcut);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
            }
        }

        private static IntPtr FindBestTopLevelWindow(HashSet<int> candidatePids)
        {
            IntPtr firstValid = IntPtr.Zero;
            IntPtr firstTitledVisible = IntPtr.Zero;
            var classSb = new StringBuilder(128);

            NativeMethods.EnumWindows((hwnd, _) =>
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                if (!candidatePids.Contains((int)pid)) return true;

                if (!NativeMethods.IsWindowVisible(hwnd)) return true;
                if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero) return true;

                long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
                if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;

                // 排除被 DWM 隐藏（如 UWP 挂起态 / 跨虚拟桌面后台）的幽灵窗口
                if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                {
                    return true;
                }

                classSb.Clear();
                if (NativeMethods.GetClassName(hwnd, classSb, classSb.Capacity) > 0 &&
                    ExcludedShellClasses.Contains(classSb.ToString()))
                {
                    return true;
                }

                bool iconic = NativeMethods.IsIconic(hwnd);
                if (!iconic && NativeMethods.GetWindowRect(hwnd, out var rect))
                {
                    if (rect.Width < 40 || rect.Height < 40) return true;
                }

                if (firstValid == IntPtr.Zero)
                {
                    firstValid = hwnd;
                }

                if (NativeMethods.GetWindowTextLength(hwnd) > 0)
                {
                    firstTitledVisible = hwnd;
                    return false; // EnumWindows 按 Z 序排列，首个带标题主窗口即最近使用窗口
                }

                return true;
            }, IntPtr.Zero);

            return firstTitledVisible != IntPtr.Zero ? firstTitledVisible : firstValid;
        }

        private static void BringWindowToFront(IntPtr targetHwnd)
        {
            NativeMethods.AllowSetForegroundWindow(-1);

            if (NativeMethods.IsIconic(targetHwnd))
            {
                NativeMethods.ShowWindow(targetHwnd, NativeMethods.SW_RESTORE);
            }
            else
            {
                NativeMethods.ShowWindow(targetHwnd, NativeMethods.SW_SHOW);
            }

            IntPtr fgHwnd = NativeMethods.GetForegroundWindow();
            uint fgTid = fgHwnd != IntPtr.Zero ? NativeMethods.GetWindowThreadProcessId(fgHwnd, out _) : 0;
            uint curTid = NativeMethods.GetCurrentThreadId();
            uint targetTid = NativeMethods.GetWindowThreadProcessId(targetHwnd, out _);

            if (fgTid != 0 && fgTid != curTid) NativeMethods.AttachThreadInput(curTid, fgTid, true);
            if (targetTid != 0 && targetTid != curTid && targetTid != fgTid) NativeMethods.AttachThreadInput(curTid, targetTid, true);

            try
            {
                NativeMethods.BringWindowToTop(targetHwnd);
                NativeMethods.SetForegroundWindow(targetHwnd);
                NativeMethods.SwitchToThisWindow(targetHwnd, true);
            }
            finally
            {
                if (targetTid != 0 && targetTid != curTid && targetTid != fgTid) NativeMethods.AttachThreadInput(curTid, targetTid, false);
                if (fgTid != 0 && fgTid != curTid) NativeMethods.AttachThreadInput(curTid, fgTid, false);
            }
        }
    }
}
