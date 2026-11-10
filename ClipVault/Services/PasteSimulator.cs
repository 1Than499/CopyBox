using System;
using System.Collections.Specialized;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using ClipVault.Interop;
using ClipVault.Models;

namespace ClipVault.Services
{
    public class PasteSimulator
    {
        private readonly ClipboardMonitorService _monitorService;
        public IntPtr LastTargetHwnd { get; set; } = IntPtr.Zero;

        public PasteSimulator(ClipboardMonitorService monitorService)
        {
            _monitorService = monitorService;
        }

        public void RecordTargetWindow()
        {
            LastTargetHwnd = NativeMethods.GetForegroundWindow();
        }

        public async Task PasteItemAsync(ClipboardItem item, Action onHideWindow)
        {
            try
            {
                // 1. 设置内部操作标记，避免监听到自己写回的数据
                _monitorService.IsInternalOperation = true;

                // 2. 将选中的内容写入剪贴板
                if (item.IsImage && File.Exists(item.FullImagePath))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(item.FullImagePath, UriKind.Absolute);
                    bitmap.EndInit();
                    bitmap.Freeze();
                    _monitorService.NotifyInternalImageWrite(bitmap.PixelWidth, bitmap.PixelHeight, 2500);
                    Clipboard.SetImage(bitmap);
                }
                else if (item.ItemType == "File")
                {
                    var sc = new StringCollection();
                    var lines = item.Content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var l in lines) sc.Add(l);
                    Clipboard.SetFileDropList(sc);
                }
                else
                {
                    // v3.0 智能模板变量：在覆盖系统剪贴板前先读取当前剪贴板文本供 {{clipboard}} 宏使用，再单遍安全展开所有变量
                    string? priorClip = null;
                    if (ClipVault.Helpers.TemplateMacroHelper.ContainsMacro(item.Content))
                    {
                        try
                        {
                            priorClip = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
                        }
                        catch
                        {
                            priorClip = string.Empty;
                        }
                    }
                    string finalContent = ClipVault.Helpers.TemplateMacroHelper.ExpandMacros(item.Content, priorClip);
                    Clipboard.SetText(finalContent);
                }

                // 3. 隐藏悬浮窗
                onHideWindow?.Invoke();

                // 4. 将焦点平滑交接还原回刚才工作的原窗口
                if (LastTargetHwnd != IntPtr.Zero)
                {
                    NativeMethods.SetForegroundWindow(LastTargetHwnd);
                }

                // 5. 毫秒级等待确保原窗口接管键盘事件，然后模拟按下 Ctrl + V
                await Task.Delay(65);
                ClipVault.Helpers.SoundEffectHelper.PlayPasteCue(_monitorService.CurrentSettings);
                NativeMethods.SimulateCtrlV();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Paste failed: {ex.Message}");
            }
        }
    }
}
