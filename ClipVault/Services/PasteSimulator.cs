using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using ClipVault.Helpers;
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

        public static string NormalizePlainText(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            string s = raw.Replace("\r\n", "\n").Replace('\r', '\n');
            s = Regex.Replace(s, @"[ \t]+\n", "\n");
            s = Regex.Replace(s, @"\n{3,}", "\n\n");
            return s.Trim();
        }

        public Task PasteItemAsync(ClipboardItem item, Action onHideWindow)
        {
            return PasteItemAsync(item, onHideWindow, plainTextOnly: false);
        }

        public async Task PasteItemAsync(ClipboardItem item, Action onHideWindow, bool plainTextOnly)
        {
            try
            {
                // 若包含 {{input:...}} 交互变量，先在贴入前弹窗采集参数
                Dictionary<string, string>? inputValues = null;
                if (!item.IsImage && item.ItemType != "File")
                {
                    var prompts = TemplateMacroHelper.ExtractInputPrompts(item.Content);
                    if (prompts.Count > 0)
                    {
                        inputValues = ClipVault.Views.TemplateInputDialog.PromptForInputs(prompts);
                        if (inputValues == null)
                        {
                            // 用户取消填空，中止粘贴
                            return;
                        }
                    }
                }

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
                    string? priorClip = null;
                    if (TemplateMacroHelper.ContainsMacro(item.Content))
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
                    string finalContent = TemplateMacroHelper.ExpandMacros(item.Content, priorClip, inputValues);
                    if (plainTextOnly)
                    {
                        finalContent = NormalizePlainText(finalContent);
                    }
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
                SoundEffectHelper.PlayPasteCue(_monitorService.CurrentSettings);
                NativeMethods.SimulateCtrlV();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Paste failed: {ex.Message}");
            }
        }
    }
}
