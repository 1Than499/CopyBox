using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipVault.Models;
using ClipVault.Services;
using Microsoft.Win32;
using System.Linq;

namespace ClipVault
{
    public partial class MainWindow : Window
    {
        private readonly DatabaseService _dbService;
        private readonly SettingsService _settingsService;
        private readonly ClipboardMonitorService _monitorService;
        private readonly PasteSimulator _pasteSimulator;

        private string _currentCategory = "all";
        public ObservableCollection<ClipboardItem> DisplayCards { get; } = new();

        // 浮层设置卡片在窗口内自由拖拽状态
        private bool _isDraggingSettings = false;
        private Point _dragStartMousePos;
        private Point _dragStartOffset;
        private bool _isInternalSettingUpdate = false;
        private bool _isSavingOwnSettings = false;
        private System.Windows.Threading.DispatcherTimer? _saveSettingsDebounceTimer;
        private string? _cachedMainWallpaperPath;
        private BitmapImage? _cachedMainWallpaperBitmap;

        public MainWindow(DatabaseService dbService, SettingsService settingsService, ClipboardMonitorService monitorService, PasteSimulator pasteSimulator)
        {
            _dbService = dbService;
            _settingsService = settingsService;
            _monitorService = monitorService;
            _pasteSimulator = pasteSimulator;
            _isInternalSettingUpdate = true;

            InitializeComponent();

            // 加载应用图标
            try
            {
                string icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "app.ico");
                if (File.Exists(icoPath))
                {
                    this.Icon = new BitmapImage(new Uri(icoPath, UriKind.Absolute));
                }
            }
            catch { }

            CardsListBox.ItemsSource = DisplayCards;

            // 加载配置、主题与卡片
            LoadSettingsToUI();
            UpdateAllLocalizedTexts();
            RefreshCards();

            // 监听语言变化，毫秒级热刷新全界面文案
            LocalizationService.Instance.LanguageChanged += () =>
            {
                Dispatcher.Invoke(() =>
                {
                    UpdateAllLocalizedTexts();
                    RefreshCards();
                    UpdateStorageInfo();
                });
            };

            // 监听后台新捕获的剪贴板条目
            _monitorService.ItemCaptured += (item) =>
            {
                Dispatcher.Invoke(() =>
                {
                    RefreshCards();
                    UpdateStorageInfo();
                });
            };

            // 失去焦点时交互逻辑（默认关闭，点击外部绝不自动收起/绝不最小化到托盘！）
            this.Deactivated += (s, e) =>
            {
                if (_settingsService != null && 
                    _settingsService.CurrentSettings.HideOnDeactivate &&
                    (SettingsOverlayHost == null || SettingsOverlayHost.Visibility != Visibility.Visible) && 
                    this.WindowState != WindowState.Minimized)
                {
                    this.Hide();
                }
            };

            _isInternalSettingUpdate = false;
        }

        public void SyncSettingsFromService(bool refreshCards)
        {
            if (_isSavingOwnSettings || _isInternalSettingUpdate) return;
            Dispatcher.Invoke(() =>
            {
                LoadSettingsToUI();
                UpdateAllLocalizedTexts();
                if (refreshCards)
                {
                    RefreshCards();
                }
                UpdateStorageInfo();
            });
        }

        private void LoadSettingsToUI()
        {
            _isInternalSettingUpdate = true;
            try
            {
                var s = _settingsService.CurrentSettings;
                TxtSettingsStoragePath.Text = s.StorageDirectory;

                // 0. 语言选择加载
                if (s.Language == "en-US")
                {
                    RadioLangEn.IsChecked = true;
                }
                else
                {
                    RadioLangZh.IsChecked = true;
                }

                // 0.5 界面展示形态偏好加载
                if (s.WindowMode == "Centered")
                {
                    RadioModeCentered.IsChecked = true;
                }
                else
                {
                    RadioModeSidebar.IsChecked = true;
                }

                // 1. 关闭行为加载
                if (s.CloseAction == "ExitApp")
                {
                    RadioCloseExit.IsChecked = true;
                }
                else
                {
                    RadioCloseMinimize.IsChecked = true;
                }

                // 2. 双击卡片行为加载 (默认仅复制且保持窗口开启)
                if (s.DoubleClickAction == "PasteAndHide")
                {
                    RadioDoubleClickPaste.IsChecked = true;
                }
                else
                {
                    RadioDoubleClickCopy.IsChecked = true;
                }

                // 3. 点击外部是否收起加载 (默认 false，点击外部不隐藏)
                ChkHideOnDeactivate.IsChecked = s.HideOnDeactivate;

                // 4. 主题与背景透明度加载 (0% 代表完全不透明，向右滑动增大背景透明度)
                string theme = s.ThemeMode ?? "Dark";
                if (RadioThemeDark != null)
                {
                    if (theme.Equals("Light", StringComparison.OrdinalIgnoreCase)) RadioThemeLight.IsChecked = true;
                    else if (theme.Equals("Aurora", StringComparison.OrdinalIgnoreCase)) RadioThemeAurora.IsChecked = true;
                    else if (theme.Equals("Sunset", StringComparison.OrdinalIgnoreCase)) RadioThemeSunset.IsChecked = true;
                    else if (theme.Equals("Ocean", StringComparison.OrdinalIgnoreCase)) RadioThemeOcean.IsChecked = true;
                    else if (theme.Equals("CustomImage", StringComparison.OrdinalIgnoreCase)) RadioThemeCustom.IsChecked = true;
                    else RadioThemeDark.IsChecked = true;
                }

                if (SliderMainBgOverlay != null)
                {
                    SliderMainBgOverlay.Value = Math.Clamp(s.BackgroundOverlayOpacity, 0.10, 0.90);
                }
                if (TxtMainBgOverlayValue != null)
                {
                    TxtMainBgOverlayValue.Text = $"{(int)(Math.Clamp(s.BackgroundOverlayOpacity, 0.10, 0.90) * 100)}%";
                }

                double clampedOpacity = Math.Clamp(s.WindowOpacity, 0.15, 1.00);
                double transparencyPct = Math.Round((1.0 - clampedOpacity) * 100.0);
                SliderOpacity.Value = Math.Clamp(transparencyPct, 0, 85);
                TxtOpacityValue.Text = $"{(int)SliderOpacity.Value}%";

                this.Opacity = 1.0;
                ApplyTheme(theme);

                UpdateStorageInfo();
            }
            finally
            {
                _isInternalSettingUpdate = false;
            }
        }

        public void UpdateAllLocalizedTexts()
        {
            var loc = LocalizationService.Instance;
            bool isEn = loc.CurrentLanguage == "en-US";

            // 窗口标题
            this.Title = loc.Get("AppTitle");

            // 搜索框占位符
            if (SearchPlaceholder != null)
            {
                SearchPlaceholder.Text = loc.Get("SearchPlaceholder");
            }

            // 分类药丸
            if (TabAll != null) TabAll.Content = loc.Get("TabAll");
            if (TabText != null) TabText.Content = loc.Get("TabText");
            if (TabImages != null) TabImages.Content = loc.Get("TabImages");
            if (TabCode != null) TabCode.Content = loc.Get("TabCode");
            if (TabStarred != null) TabStarred.Content = loc.Get("TabStarred");

            // 顶栏按钮 ToolTip
            if (BtnToggleSettings != null) BtnToggleSettings.ToolTip = loc.Get("ToolTipSettings");
            if (BtnMinimizeWindow != null) BtnMinimizeWindow.ToolTip = loc.Get("ToolTipMinimize");
            if (BtnQuickTheme != null)
            {
                bool isDark = _settingsService?.CurrentSettings.ThemeMode != "Light";
                BtnQuickTheme.ToolTip = isDark ? loc.Get("ToolTipThemeLight") : loc.Get("ToolTipThemeDark");
            }
            UpdateCloseButtonToolTip();

            // 设置浮层面板
            if (TxtSettingsTitle != null) TxtSettingsTitle.Text = loc.Get("SettingsTitle");
            if (TxtSettingsDragTip != null) TxtSettingsDragTip.Text = loc.Get("SettingsDragTip");
            if (TxtLanguageTitle != null) TxtLanguageTitle.Text = loc.Get("LangSectionTitle");
            if (RadioLangZh != null) RadioLangZh.Content = loc.Get("LangZh");
            if (RadioLangEn != null) RadioLangEn.Content = loc.Get("LangEn");

            if (TxtWindowModeTitle != null) TxtWindowModeTitle.Text = loc.Get("WindowModeTitle");
            if (RadioModeSidebar != null) RadioModeSidebar.Content = loc.Get("WindowModeSidebar");
            if (RadioModeCentered != null) RadioModeCentered.Content = loc.Get("WindowModeCentered");

            if (TxtStoragePathTitle != null) TxtStoragePathTitle.Text = loc.Get("StoragePathTitle");
            if (BtnBrowseStorage != null) BtnBrowseStorage.Content = loc.Get("BrowseButton");

            if (TxtAppearanceTitle != null) TxtAppearanceTitle.Text = loc.Get("AppearanceTitle");
            if (RadioThemeDark != null) RadioThemeDark.Content = isEn ? "🌙 Dark Geek" : "🌙 暗夜极客";
            if (RadioThemeLight != null) RadioThemeLight.Content = isEn ? "☀️ Pure Light" : "☀️ 纯净白天";
            if (RadioThemeAurora != null) RadioThemeAurora.Content = isEn ? "🌌 Aurora" : "🌌 极光紫霓";
            if (RadioThemeSunset != null) RadioThemeSunset.Content = isEn ? "🌅 Sunset" : "🌅 落日熔金";
            if (RadioThemeOcean != null) RadioThemeOcean.Content = isEn ? "🌊 Cyber Ocean" : "🌊 深海赛博";
            if (RadioThemeCustom != null) RadioThemeCustom.Content = isEn ? "🖼️ Custom Pic" : "🖼️ 自定义图";

            if (BtnMainUploadBg != null) BtnMainUploadBg.Content = isEn ? "Upload" : "上传壁纸";
            if (BtnMainResetBg != null) BtnMainResetBg.Content = isEn ? "Reset" : "重置";
            if (TxtMainBgOverlayLabel != null) TxtMainBgOverlayLabel.Text = isEn ? "Dim Overlay:" : "壁纸暗化遮罩:";
            if (TxtOpacityLabel != null) TxtOpacityLabel.Text = loc.Get("OpacityLabel");

            if (TxtCloseActionTitle != null) TxtCloseActionTitle.Text = loc.Get("CloseActionTitle");
            if (RadioCloseMinimize != null) RadioCloseMinimize.Content = loc.Get("CloseActionTray");
            if (RadioCloseExit != null) RadioCloseExit.Content = loc.Get("CloseActionExit");

            if (TxtDoubleClickTitle != null) TxtDoubleClickTitle.Text = loc.Get("DoubleClickTitle");
            if (RadioDoubleClickCopy != null) RadioDoubleClickCopy.Content = loc.Get("DoubleClickCopy");
            if (RadioDoubleClickPaste != null) RadioDoubleClickPaste.Content = loc.Get("DoubleClickPaste");

            if (ChkHideOnDeactivate != null)
            {
                ChkHideOnDeactivate.Content = loc.Get("DeactivateTitle");
                ChkHideOnDeactivate.ToolTip = loc.Get("DeactivateTooltip");
            }

            if (BtnMainClearHistory != null) BtnMainClearHistory.Content = isEn ? "🗑️ Clear History" : "🗑️ 清空历史";
            if (BtnDirectExit != null) BtnDirectExit.Content = loc.Get("BtnExitApp");
            if (BtnCloseSettingsOverlay != null) BtnCloseSettingsOverlay.Content = loc.Get("BtnDone");

            // 底部快捷键指南
            if (TxtBottomTips != null) TxtBottomTips.Text = loc.Get("BottomTips");
        }

        private void LangRadio_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInternalSettingUpdate || _settingsService == null) return;
            string newLang = RadioLangEn.IsChecked == true ? "en-US" : "zh-CN";
            if (_settingsService.CurrentSettings.Language != newLang)
            {
                _settingsService.CurrentSettings.Language = newLang;
                _settingsService.SaveSettings();
                LocalizationService.Instance.SetLanguage(newLang);
            }
        }

        private void WindowMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInternalSettingUpdate || _settingsService == null || RadioModeSidebar == null || RadioModeCentered == null) return;
            string newMode = RadioModeSidebar.IsChecked == true ? "Sidebar" : "Centered";
            if (_settingsService.CurrentSettings.WindowMode != newMode)
            {
                _settingsService.CurrentSettings.WindowMode = newMode;
                _settingsService.SaveSettings();

                if (Application.Current is App app)
                {
                    app.OnWindowModeChanged(newMode);
                }
            }
        }

        private void UpdateStorageInfo()
        {
            var (dbSize, totalCount, pinnedCount, imgCount, imgSize) = _dbService.GetStats();
            TxtStorageStatusInfo.Text = LocalizationService.Instance.Get("StatusInfoFormat", totalCount, pinnedCount, imgCount);
        }

        public void ShowAndActivate()
        {
            // 记录当前活跃的工作窗口句柄，供选定后自动模拟粘贴
            _pasteSimulator.RecordTargetWindow();

            SearchInputBox.Text = string.Empty;
            if (SettingsOverlayHost != null) SettingsOverlayHost.Visibility = Visibility.Collapsed;
            RefreshCards();

            if (this.WindowState == WindowState.Minimized)
            {
                this.WindowState = WindowState.Normal;
            }
            this.Show();
            this.Activate();

            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                ClipVault.Interop.NativeMethods.SetForegroundWindow(hwnd);
            }

            SearchInputBox.Focus();
        }

        public void ToggleMainWindow()
        {
            if (this.IsVisible && this.WindowState != WindowState.Minimized)
            {
                this.Hide();
            }
            else
            {
                ShowAndActivate();
            }
        }

        public void RefreshCards()
        {
            DisplayCards.Clear();
            string keyword = SearchInputBox.Text.Trim();
            var items = _dbService.GetRecentItems(keyword, _currentCategory, 60);

            for (int i = 0; i < items.Count; i++)
            {
                if (i < 9)
                {
                    items[i].DisplayBadge = $"[{i + 1}]";
                }
                DisplayCards.Add(items[i]);
            }

            if (DisplayCards.Count > 0)
            {
                CardsListBox.SelectedIndex = 0;
            }
        }

        private async void TriggerPaste(ClipboardItem item)
        {
            if (item == null) return;
            ShowToast(LocalizationService.Instance.Get("ToastPasted"));
            await _pasteSimulator.PasteItemAsync(item, () => this.Hide());
        }

        // ================= 外观：白天/夜间主题与透明度 =================

        private string EnsureDefaultMainWallpaperExists()
        {
            string assetsDir = Path.Combine(AppSettings.GetAppRootDirectory(), "assets");
            if (!Directory.Exists(assetsDir)) Directory.CreateDirectory(assetsDir);
            string defaultBgPath = Path.Combine(assetsDir, "default_bg.png");

            if (!File.Exists(defaultBgPath))
            {
                try
                {
                    int w = 420, h = 900;
                    var dv = new DrawingVisual();
                    using (var dc = dv.RenderOpen())
                    {
                        var bgGrad = new LinearGradientBrush(
                            Color.FromRgb(15, 12, 41),
                            Color.FromRgb(36, 36, 62),
                            new Point(0, 0), new Point(1, 1));
                        bgGrad.GradientStops.Add(new GradientStop(Color.FromRgb(48, 43, 99), 0.5));
                        dc.DrawRectangle(bgGrad, null, new Rect(0, 0, w, h));

                        var orb1 = new RadialGradientBrush(Color.FromArgb(140, 236, 72, 153), Colors.Transparent);
                        dc.DrawEllipse(orb1, null, new Point(w * 0.8, h * 0.2), 180, 180);

                        var orb2 = new RadialGradientBrush(Color.FromArgb(130, 56, 189, 248), Colors.Transparent);
                        dc.DrawEllipse(orb2, null, new Point(w * 0.2, h * 0.55), 210, 210);

                        var orb3 = new RadialGradientBrush(Color.FromArgb(120, 139, 92, 246), Colors.Transparent);
                        dc.DrawEllipse(orb3, null, new Point(w * 0.75, h * 0.85), 190, 190);
                    }

                    var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(dv);

                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(rtb));
                    using var fs = new FileStream(defaultBgPath, FileMode.Create, FileAccess.Write);
                    encoder.Save(fs);
                }
                catch { }
            }
            return defaultBgPath;
        }

        private void ApplyTheme(string themeMode)
        {
            string mode = string.IsNullOrWhiteSpace(themeMode) ? "Dark" : themeMode;
            bool isLight = mode.Equals("Light", StringComparison.OrdinalIgnoreCase);
            bool isAurora = mode.Equals("Aurora", StringComparison.OrdinalIgnoreCase);
            bool isSunset = mode.Equals("Sunset", StringComparison.OrdinalIgnoreCase);
            bool isOcean = mode.Equals("Ocean", StringComparison.OrdinalIgnoreCase);
            bool isCustom = mode.Equals("CustomImage", StringComparison.OrdinalIgnoreCase);
            bool isDark = !isLight;

            var loc = LocalizationService.Instance;
            bool isEn = loc.CurrentLanguage == "en-US";

            if (BtnQuickTheme != null)
            {
                BtnQuickTheme.Content = isLight ? "🌙" : "☀️";
                BtnQuickTheme.ToolTip = isLight ? loc.Get("ToolTipThemeDark") : loc.Get("ToolTipThemeLight");
            }

            if (BdMainCustomImageSettings != null)
            {
                BdMainCustomImageSettings.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
            }

            double opacity = Math.Clamp(_settingsService?.CurrentSettings.WindowOpacity ?? 0.92, 0.15, 1.00);
            byte bgAlpha = (byte)Math.Clamp(opacity * 255, 38, 255);
            byte cardAlpha = (byte)Math.Clamp((0.35 + opacity * 0.65) * 255, 95, 255);

            if (isCustom)
            {
                string customPath = _settingsService?.CurrentSettings.CustomBackgroundImagePath ?? string.Empty;
                if (string.IsNullOrWhiteSpace(customPath) || !File.Exists(customPath))
                {
                    customPath = EnsureDefaultMainWallpaperExists();
                    if (TxtMainCustomBgPath != null) TxtMainCustomBgPath.Text = isEn ? "Default Built-in Wallpaper" : "默认内置极光壁纸";
                }
                else
                {
                    if (TxtMainCustomBgPath != null) TxtMainCustomBgPath.Text = Path.GetFileName(customPath);
                }

                try
                {
                    if (File.Exists(customPath) && ImgMainCustomBg != null && MainBgImageContainer != null)
                    {
                        if (_cachedMainWallpaperBitmap == null || !string.Equals(_cachedMainWallpaperPath, customPath, StringComparison.OrdinalIgnoreCase))
                        {
                            var bmp = new BitmapImage();
                            bmp.BeginInit();
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.UriSource = new Uri(customPath, UriKind.Absolute);
                            bmp.EndInit();
                            bmp.Freeze();
                            _cachedMainWallpaperPath = customPath;
                            _cachedMainWallpaperBitmap = bmp;
                        }
                        ImgMainCustomBg.Source = _cachedMainWallpaperBitmap;
                        MainBgImageContainer.Visibility = Visibility.Visible;
                        MainBgImageContainer.Opacity = opacity;
                    }
                }
                catch
                {
                    if (MainBgImageContainer != null) MainBgImageContainer.Visibility = Visibility.Collapsed;
                }

                double overlayAlphaRatio = Math.Clamp(_settingsService?.CurrentSettings.BackgroundOverlayOpacity ?? 0.55, 0.10, 0.90);
                byte ovAlpha = (byte)(overlayAlphaRatio * 255);
                if (BdMainCustomBgOverlay != null)
                {
                    BdMainCustomBgOverlay.Background = new SolidColorBrush(Color.FromArgb(ovAlpha, 10, 14, 23));
                }

                MainBorder.Background = new SolidColorBrush(Color.FromArgb(40, 13, 17, 26));
                this.Resources["CardBgBrush"] = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(cardAlpha * 0.78, 80, 210), 22, 29, 42));
                this.Resources["CardBorderBrush"] = new SolidColorBrush(Color.FromArgb(95, 255, 255, 255));
                this.Resources["WindowBorderBrush"] = new SolidColorBrush(Color.FromRgb(236, 72, 153));
            }
            else
            {
                if (MainBgImageContainer != null) MainBgImageContainer.Visibility = Visibility.Collapsed;

                if (isLight)
                {
                    MainBorder.Background = new SolidColorBrush(Color.FromArgb(bgAlpha, 248, 250, 252));
                    this.Resources["CardBgBrush"] = new SolidColorBrush(Color.FromArgb(cardAlpha, 255, 255, 255));
                    this.Resources["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(226, 232, 240));
                    this.Resources["WindowBorderBrush"] = new SolidColorBrush(Color.FromRgb(203, 213, 225));
                }
                else if (isAurora)
                {
                    var auroraGrad = new LinearGradientBrush(
                        Color.FromArgb(bgAlpha, 20, 15, 45),
                        Color.FromArgb(bgAlpha, 12, 29, 54),
                        new Point(0, 0), new Point(1, 1));
                    auroraGrad.GradientStops.Add(new GradientStop(Color.FromArgb(bgAlpha, 38, 20, 71), 0.5));
                    MainBorder.Background = auroraGrad;
                    this.Resources["CardBgBrush"] = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(cardAlpha * 0.85, 90, 230), 34, 27, 68));
                    this.Resources["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(91, 68, 150));
                    this.Resources["WindowBorderBrush"] = new SolidColorBrush(Color.FromRgb(139, 92, 246));
                }
                else if (isSunset)
                {
                    var sunsetGrad = new LinearGradientBrush(
                        Color.FromArgb(bgAlpha, 43, 16, 29),
                        Color.FromArgb(bgAlpha, 33, 21, 16),
                        new Point(0, 0), new Point(1, 1));
                    sunsetGrad.GradientStops.Add(new GradientStop(Color.FromArgb(bgAlpha, 58, 24, 34), 0.5));
                    MainBorder.Background = sunsetGrad;
                    this.Resources["CardBgBrush"] = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(cardAlpha * 0.85, 90, 230), 56, 27, 36));
                    this.Resources["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(133, 61, 69));
                    this.Resources["WindowBorderBrush"] = new SolidColorBrush(Color.FromRgb(249, 115, 22));
                }
                else if (isOcean)
                {
                    var oceanGrad = new LinearGradientBrush(
                        Color.FromArgb(bgAlpha, 7, 25, 43),
                        Color.FromArgb(bgAlpha, 6, 38, 48),
                        new Point(0, 0), new Point(1, 1));
                    MainBorder.Background = oceanGrad;
                    this.Resources["CardBgBrush"] = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(cardAlpha * 0.85, 90, 230), 13, 39, 61));
                    this.Resources["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(28, 93, 125));
                    this.Resources["WindowBorderBrush"] = new SolidColorBrush(Color.FromRgb(6, 182, 212));
                }
                else
                {
                    MainBorder.Background = new SolidColorBrush(Color.FromArgb(bgAlpha, 22, 25, 34));
                    this.Resources["CardBgBrush"] = new SolidColorBrush(Color.FromArgb(cardAlpha, 24, 28, 37));
                    this.Resources["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(40, 50, 66));
                    this.Resources["WindowBorderBrush"] = new SolidColorBrush(Color.FromRgb(51, 64, 80));
                }
            }

            this.Resources["CardTextBrush"] = isDark ? new SolidColorBrush(Color.FromRgb(226, 232, 240)) : new SolidColorBrush(Color.FromRgb(15, 23, 42));
            this.Resources["CodeBgBrush"] = isDark ? new SolidColorBrush(Color.FromArgb((byte)Math.Clamp((int)cardAlpha, 120, 255), 17, 20, 27)) : new SolidColorBrush(Color.FromArgb((byte)Math.Clamp((int)cardAlpha, 160, 255), 241, 245, 249));
            this.Resources["CodeTextBrush"] = isDark ? new SolidColorBrush(Color.FromRgb(52, 211, 153)) : new SolidColorBrush(Color.FromRgb(5, 150, 105));
            this.Resources["SubTextBrush"] = isDark ? new SolidColorBrush(Color.FromRgb(148, 163, 184)) : new SolidColorBrush(Color.FromRgb(100, 116, 139));
            this.Resources["TitleTextBrush"] = isDark ? Brushes.White : new SolidColorBrush(Color.FromRgb(15, 23, 42));
            this.Resources["SearchBgBrush"] = isDark ? new SolidColorBrush(Color.FromArgb((byte)Math.Clamp((int)cardAlpha, 130, 255), 19, 22, 31)) : new SolidColorBrush(Color.FromArgb((byte)Math.Clamp((int)cardAlpha, 180, 255), 255, 255, 255));
            this.Resources["SearchBorderBrush"] = isDark ? new SolidColorBrush(Color.FromRgb(40, 50, 66)) : new SolidColorBrush(Color.FromRgb(203, 213, 225));
            this.Resources["HoverBgBrush"] = isDark 
                ? new SolidColorBrush(Color.FromArgb(32, 255, 255, 255)) 
                : new SolidColorBrush(Color.FromArgb(20, 0, 0, 0));
            this.Resources["PressedBgBrush"] = isDark 
                ? new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)) 
                : new SolidColorBrush(Color.FromArgb(35, 0, 0, 0));
            this.Resources["InfoBgBrush"] = isDark 
                ? new SolidColorBrush(Color.FromRgb(22, 34, 52)) 
                : new SolidColorBrush(Color.FromRgb(239, 246, 255));
            this.Resources["InfoBorderBrush"] = isDark 
                ? new SolidColorBrush(Color.FromRgb(30, 58, 138)) 
                : new SolidColorBrush(Color.FromRgb(191, 219, 254));
            this.Resources["InfoTextBrush"] = isDark 
                ? new SolidColorBrush(Color.FromRgb(96, 165, 250)) 
                : new SolidColorBrush(Color.FromRgb(29, 78, 216));

            // 顶栏控制岛高对比防透资源
            this.Resources["TopPillBgBrush"] = isDark 
                ? new SolidColorBrush(Color.FromArgb(235, 30, 37, 50)) 
                : new SolidColorBrush(Color.FromArgb(245, 241, 245, 249));
            this.Resources["TopPillBorderBrush"] = isDark 
                ? new SolidColorBrush(Color.FromRgb(64, 78, 99)) 
                : new SolidColorBrush(Color.FromRgb(203, 213, 225));
            this.Resources["TopButtonFgBrush"] = isDark 
                ? Brushes.White 
                : new SolidColorBrush(Color.FromRgb(15, 23, 42));

            // 分类胶囊防透实体底色与边框
            this.Resources["TabPillBgBrush"] = isDark 
                ? new SolidColorBrush(Color.FromRgb(30, 37, 50)) 
                : new SolidColorBrush(Color.FromRgb(241, 245, 249));
            this.Resources["TabPillBorderBrush"] = isDark 
                ? new SolidColorBrush(Color.FromRgb(58, 70, 89)) 
                : new SolidColorBrush(Color.FromRgb(203, 213, 225));
            this.Resources["TabPillFgBrush"] = isDark 
                ? new SolidColorBrush(Color.FromRgb(226, 232, 240)) 
                : new SolidColorBrush(Color.FromRgb(30, 41, 59));

            // 选中卡片自适应柔和光晕
            this.Resources["SelectedBorderBrush"] = isDark 
                ? new SolidColorBrush(Color.FromRgb(56, 189, 248)) 
                : new SolidColorBrush(Color.FromRgb(0, 120, 212));

            this.Resources["SelectedCardEffect"] = isDark 
                ? new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 10, ShadowDepth = 0, Color = Color.FromRgb(0, 120, 212), Opacity = 0.38 }
                : new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 1.5, Direction = 270, Color = Color.FromRgb(0, 120, 212), Opacity = 0.18 };

            // 悬浮微质感菜单资源
            this.Resources["MenuBgBrush"] = isDark 
                ? new SolidColorBrush(Color.FromRgb(30, 36, 48)) 
                : new SolidColorBrush(Color.FromRgb(255, 255, 255));
            this.Resources["MenuBorderBrush"] = isDark 
                ? new SolidColorBrush(Color.FromRgb(55, 68, 88)) 
                : new SolidColorBrush(Color.FromRgb(203, 213, 225));

            UpdateCloseButtonToolTip();
            UpdateCategoryTabsAppearance();
        }

        private void UpdateCloseButtonToolTip()
        {
            if (BtnCloseWindow != null && _settingsService != null)
            {
                var loc = LocalizationService.Instance;
                BtnCloseWindow.ToolTip = _settingsService.CurrentSettings.CloseAction == "ExitApp"
                    ? loc.Get("ToolTipCloseExit")
                    : loc.Get("ToolTipCloseTray");
            }
        }

        private void SetMainBrushAlphaInPlace(string key, byte alpha)
        {
            if (this.Resources[key] is SolidColorBrush brush && !brush.IsFrozen)
            {
                var c = brush.Color;
                if (c.A != alpha)
                {
                    brush.Color = Color.FromArgb(alpha, c.R, c.G, c.B);
                }
            }
        }

        private void UpdateMainBackgroundOpacityLive(double opacity)
        {
            double clamped = Math.Clamp(opacity, 0.15, 1.00);
            byte bgAlpha = (byte)Math.Clamp(clamped * 255, 38, 255);
            byte cardAlpha = (byte)Math.Clamp((0.35 + clamped * 0.65) * 255, 95, 255);

            string mode = _settingsService?.CurrentSettings.ThemeMode ?? "Dark";
            bool isLight = mode.Equals("Light", StringComparison.OrdinalIgnoreCase);
            bool isCustom = mode.Equals("CustomImage", StringComparison.OrdinalIgnoreCase);
            bool isSpecialGrad = mode.Equals("Aurora", StringComparison.OrdinalIgnoreCase) ||
                                 mode.Equals("Sunset", StringComparison.OrdinalIgnoreCase) ||
                                 mode.Equals("Ocean", StringComparison.OrdinalIgnoreCase);

            if (isCustom)
            {
                if (MainBgImageContainer != null) MainBgImageContainer.Opacity = clamped;
                SetMainBrushAlphaInPlace("CardBgBrush", (byte)Math.Clamp(cardAlpha * 0.78, 80, 210));
            }
            else
            {
                if (MainBorder.Background is SolidColorBrush sb && !sb.IsFrozen)
                {
                    var c = sb.Color;
                    sb.Color = Color.FromArgb(bgAlpha, c.R, c.G, c.B);
                }
                else if (MainBorder.Background is LinearGradientBrush lgb && !lgb.IsFrozen)
                {
                    foreach (var stop in lgb.GradientStops)
                    {
                        var c = stop.Color;
                        stop.Color = Color.FromArgb(bgAlpha, c.R, c.G, c.B);
                    }
                }

                byte targetCardAlpha = isSpecialGrad ? (byte)Math.Clamp(cardAlpha * 0.85, 90, 230) : cardAlpha;
                SetMainBrushAlphaInPlace("CardBgBrush", targetCardAlpha);
            }

            SetMainBrushAlphaInPlace("CodeBgBrush", (byte)Math.Clamp((int)cardAlpha, isLight ? 160 : 120, 255));
            SetMainBrushAlphaInPlace("SearchBgBrush", (byte)Math.Clamp((int)cardAlpha, isLight ? 180 : 130, 255));
        }

        private void ScheduleDebouncedMainSaveSettings()
        {
            if (_saveSettingsDebounceTimer == null)
            {
                _saveSettingsDebounceTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(250)
                };
                _saveSettingsDebounceTimer.Tick += (s, e) =>
                {
                    _saveSettingsDebounceTimer.Stop();
                    if (_settingsService == null) return;
                    _isSavingOwnSettings = true;
                    try
                    {
                        _settingsService.SaveSettings();
                    }
                    finally
                    {
                        _isSavingOwnSettings = false;
                    }
                };
            }
            _saveSettingsDebounceTimer.Stop();
            _saveSettingsDebounceTimer.Start();
        }

        private void ApplyOpacity(double transparencyPct, bool save = true)
        {
            double clampedPct = Math.Clamp(transparencyPct, 0, 85);
            double opacity = Math.Clamp(1.0 - (clampedPct / 100.0), 0.15, 1.00);

            if (TxtOpacityValue != null)
            {
                TxtOpacityValue.Text = $"{(int)Math.Round(clampedPct)}%";
            }

            if (_settingsService != null)
            {
                _settingsService.CurrentSettings.WindowOpacity = opacity;
            }

            this.Opacity = 1.0;
            UpdateMainBackgroundOpacityLive(opacity);

            if (save)
            {
                ScheduleDebouncedMainSaveSettings();
            }
        }

        private void BtnQuickTheme_Click(object sender, RoutedEventArgs e)
        {
            if (_settingsService == null) return;
            string current = _settingsService.CurrentSettings.ThemeMode ?? "Dark";
            string newTheme = current.Equals("Light", StringComparison.OrdinalIgnoreCase) ? "Dark" : "Light";

            _isInternalSettingUpdate = true;
            try
            {
                if (newTheme == "Light" && RadioThemeLight != null) RadioThemeLight.IsChecked = true;
                else if (RadioThemeDark != null) RadioThemeDark.IsChecked = true;
            }
            finally
            {
                _isInternalSettingUpdate = false;
            }

            _settingsService.CurrentSettings.ThemeMode = newTheme;
            _settingsService.SaveSettings();
            ApplyTheme(newTheme);
        }

        private void ThemeRadio_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInternalSettingUpdate || _settingsService == null) return;

            string newTheme = "Dark";
            if (RadioThemeLight?.IsChecked == true) newTheme = "Light";
            else if (RadioThemeAurora?.IsChecked == true) newTheme = "Aurora";
            else if (RadioThemeSunset?.IsChecked == true) newTheme = "Sunset";
            else if (RadioThemeOcean?.IsChecked == true) newTheme = "Ocean";
            else if (RadioThemeCustom?.IsChecked == true) newTheme = "CustomImage";

            if (_settingsService.CurrentSettings.ThemeMode != newTheme)
            {
                _settingsService.CurrentSettings.ThemeMode = newTheme;
                _settingsService.SaveSettings();
            }
            ApplyTheme(newTheme);
        }

        private void BtnMainUploadBg_Click(object sender, RoutedEventArgs e)
        {
            bool isEn = LocalizationService.Instance.CurrentLanguage == "en-US";
            var dlg = new OpenFileDialog
            {
                Title = isEn ? "Select Custom Wallpaper Image" : "选择自定义背景壁纸图片",
                Filter = "Image Files (*.png;*.jpg;*.jpeg;*.webp;*.bmp)|*.png;*.jpg;*.jpeg;*.webp;*.bmp"
            };

            if (dlg.ShowDialog() == true)
            {
                try
                {
                    string bgDir = Path.Combine(_settingsService.CurrentSettings.StorageDirectory, "wallpapers");
                    if (!Directory.Exists(bgDir)) Directory.CreateDirectory(bgDir);

                    string ext = Path.GetExtension(dlg.FileName);
                    string destPath = Path.Combine(bgDir, $"custom_bg_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
                    File.Copy(dlg.FileName, destPath, true);

                    _settingsService.CurrentSettings.CustomBackgroundImagePath = destPath;
                    _settingsService.CurrentSettings.ThemeMode = "CustomImage";
                    _settingsService.SaveSettings();

                    ApplyTheme("CustomImage");
                }
                catch (Exception ex)
                {
                    ShowToast($"Error: {ex.Message}");
                }
            }
        }

        private void BtnMainResetBg_Click(object sender, RoutedEventArgs e)
        {
            _settingsService.CurrentSettings.CustomBackgroundImagePath = string.Empty;
            _settingsService.SaveSettings();
            ApplyTheme("CustomImage");
        }

        private void SliderMainBgOverlay_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isInternalSettingUpdate || _settingsService == null) return;
            double val = Math.Clamp(e.NewValue, 0.10, 0.90);
            if (TxtMainBgOverlayValue != null)
            {
                TxtMainBgOverlayValue.Text = $"{(int)(val * 100)}%";
            }
            _settingsService.CurrentSettings.BackgroundOverlayOpacity = val;
            if (BdMainCustomBgOverlay != null)
            {
                byte ovAlpha = (byte)(val * 255);
                if (BdMainCustomBgOverlay.Background is SolidColorBrush sb && !sb.IsFrozen)
                {
                    sb.Color = Color.FromArgb(ovAlpha, 10, 14, 23);
                }
                else
                {
                    BdMainCustomBgOverlay.Background = new SolidColorBrush(Color.FromArgb(ovAlpha, 10, 14, 23));
                }
            }
            ScheduleDebouncedMainSaveSettings();
        }

        private void SliderOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isInternalSettingUpdate || _settingsService == null || MainBorder == null) return;
            ApplyOpacity(e.NewValue, save: true);
        }

        // ================= 窗口控制与退出行为 =================

        private void BtnMinimizeWindow_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void BtnCloseWindow_Click(object sender, RoutedEventArgs e)
        {
            if (_settingsService.CurrentSettings.CloseAction == "ExitApp")
            {
                ExitApplication();
            }
            else
            {
                this.Hide(); // 最小化收起到后台系统托盘
            }
        }

        private void CloseActionRadio_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInternalSettingUpdate || _settingsService == null) return;

            if (RadioCloseExit.IsChecked == true)
            {
                _settingsService.CurrentSettings.CloseAction = "ExitApp";
            }
            else
            {
                _settingsService.CurrentSettings.CloseAction = "MinimizeToTray";
            }
            _settingsService.SaveSettings();
            UpdateCloseButtonToolTip();
        }

        private void DoubleClickRadio_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInternalSettingUpdate || _settingsService == null) return;
            _settingsService.CurrentSettings.DoubleClickAction = RadioDoubleClickPaste.IsChecked == true
                ? "PasteAndHide"
                : "CopyOnly";
            _settingsService.SaveSettings();
        }

        private void ChkHideOnDeactivate_Click(object sender, RoutedEventArgs e)
        {
            if (_isInternalSettingUpdate || _settingsService == null) return;
            _settingsService.CurrentSettings.HideOnDeactivate = ChkHideOnDeactivate.IsChecked == true;
            _settingsService.SaveSettings();
        }

        private void BtnMainClearHistory_Click(object sender, RoutedEventArgs e)
        {
            var loc = LocalizationService.Instance;
            bool isEn = loc.CurrentLanguage == "en-US";
            var res = MessageBox.Show(loc.Get("ConfirmClearMsg"), loc.Get("ConfirmClearTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res == MessageBoxResult.Yes)
            {
                _dbService.ClearAll();
                RefreshCards();
                UpdateStorageInfo();
                _settingsService.SaveSettings();
                ShowToast(isEn ? "✓ Unpinned history cleared" : "✓ 已清空未置顶历史");
            }
        }

        private void BtnDirectExit_Click(object sender, RoutedEventArgs e)
        {
            var loc = LocalizationService.Instance;
            var res = MessageBox.Show(loc.Get("ConfirmExitMsg"), loc.Get("ConfirmExitTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res == MessageBoxResult.Yes)
            {
                ExitApplication();
            }
        }

        private void ExitApplication()
        {
            if (Application.Current is App app)
            {
                app.ExitApplication();
            }
            else
            {
                _monitorService?.Stop();
                _dbService?.Dispose();
                Application.Current.Shutdown();
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_settingsService?.CurrentSettings?.CloseAction == "ExitApp")
            {
                ExitApplication();
            }
            else
            {
                e.Cancel = true;
                this.Hide();
            }
        }

        // ================= 鼠标拖拽与设置浮层移动 =================

        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // 如果点击在设置浮层内部，绝对不触发主窗口整体拖拽
            if (SettingsOverlayHost != null && SettingsOverlayHost.Visibility == Visibility.Visible)
            {
                var pos = e.GetPosition(SettingsOverlayHost);
                if (pos.X >= 0 && pos.X <= SettingsOverlayHost.ActualWidth &&
                    pos.Y >= 0 && pos.Y <= SettingsOverlayHost.ActualHeight)
                {
                    return;
                }
            }

            if (e.LeftButton == MouseButtonState.Pressed && 
                e.OriginalSource is not TextBox && 
                e.OriginalSource is not Button && 
                e.OriginalSource is not Slider &&
                e.OriginalSource is not ListBox &&
                e.OriginalSource is not ListBoxItem)
            {
                try { this.DragMove(); } catch { }
            }
        }

        public void OpenSettings()
        {
            LoadSettingsToUI();
            if (SettingsOverlayHost != null) SettingsOverlayHost.Visibility = Visibility.Visible;
            EnsureSettingsInBounds();
        }

        private void BtnToggleSettings_Click(object sender, RoutedEventArgs e)
        {
            if (SettingsOverlayHost == null) return;
            if (SettingsOverlayHost.Visibility == Visibility.Visible)
            {
                SettingsOverlayHost.Visibility = Visibility.Collapsed;
            }
            else
            {
                LoadSettingsToUI();
                SettingsOverlayHost.Visibility = Visibility.Visible;
                EnsureSettingsInBounds();
            }
        }

        private void BtnCloseSettingsOverlay_Click(object sender, RoutedEventArgs e)
        {
            if (SettingsOverlayHost != null) SettingsOverlayHost.Visibility = Visibility.Collapsed;
        }

        // 浮层设置卡片在软件窗口内的独立拖拽交互
        private void SettingsDragBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && MainRootGrid != null && SettingsTransform != null)
            {
                _isDraggingSettings = true;
                _dragStartMousePos = e.GetPosition(MainRootGrid);
                _dragStartOffset = new Point(SettingsTransform.X, SettingsTransform.Y);
                SettingsDragBar.CaptureMouse();
                e.Handled = true;
            }
        }

        private void SettingsDragBar_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingSettings && e.LeftButton == MouseButtonState.Pressed && MainRootGrid != null && SettingsTransform != null)
            {
                Point currentMousePos = e.GetPosition(MainRootGrid);
                double deltaX = currentMousePos.X - _dragStartMousePos.X;
                double deltaY = currentMousePos.Y - _dragStartMousePos.Y;

                double targetX = _dragStartOffset.X + deltaX;
                double targetY = _dragStartOffset.Y + deltaY;

                // 限制在主窗口客户区边界内 (绝不超出软件窗口边缘)
                double containerW = MainRootGrid.ActualWidth;
                double containerH = MainRootGrid.ActualHeight;
                double cardW = (SettingsOverlayHost != null && SettingsOverlayHost.ActualWidth > 0) ? SettingsOverlayHost.ActualWidth : 420;
                double cardH = (SettingsOverlayHost != null && SettingsOverlayHost.ActualHeight > 0) ? SettingsOverlayHost.ActualHeight : 480;

                if (containerW > 0 && containerH > 0)
                {
                    double maxOffsetX = Math.Max(0, (containerW - cardW) / 2);
                    double maxOffsetY = Math.Max(0, (containerH - cardH) / 2);

                    targetX = Math.Clamp(targetX, -maxOffsetX, maxOffsetX);
                    targetY = Math.Clamp(targetY, -maxOffsetY, maxOffsetY);
                }

                SettingsTransform.X = targetX;
                SettingsTransform.Y = targetY;
                e.Handled = true;
            }
        }

        private void SettingsDragBar_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingSettings)
            {
                _isDraggingSettings = false;
                SettingsDragBar.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        private void EnsureSettingsInBounds()
        {
            if (MainRootGrid == null || SettingsOverlayHost == null || SettingsTransform == null) return;

            double containerW = MainRootGrid.ActualWidth;
            double containerH = MainRootGrid.ActualHeight;
            double cardW = (SettingsOverlayHost.ActualWidth > 0) ? SettingsOverlayHost.ActualWidth : 420;
            double cardH = (SettingsOverlayHost.ActualHeight > 0) ? SettingsOverlayHost.ActualHeight : 480;

            if (containerW > 0 && containerH > 0)
            {
                double maxOffsetX = Math.Max(0, (containerW - cardW) / 2);
                double maxOffsetY = Math.Max(0, (containerH - cardH) / 2);

                SettingsTransform.X = Math.Clamp(SettingsTransform.X, -maxOffsetX, maxOffsetX);
                SettingsTransform.Y = Math.Clamp(SettingsTransform.Y, -maxOffsetY, maxOffsetY);
            }
        }

        private void TabCategory_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string cat)
            {
                _currentCategory = cat;
                UpdateCategoryTabsAppearance();
                RefreshCards();
            }
        }

        private void UpdateCategoryTabsAppearance()
        {
            if (TabAll == null) return;
            var activeBg = new SolidColorBrush(Color.FromRgb(0, 120, 212));
            var inactiveBg = (Brush)this.Resources["TabPillBgBrush"];
            var activeFg = Brushes.White;
            var inactiveFg = (Brush)this.Resources["TabPillFgBrush"];

            UpdateButtonTab(TabAll, _currentCategory == "all", activeBg, inactiveBg, activeFg, inactiveFg);
            UpdateButtonTab(TabText, _currentCategory == "Text", activeBg, inactiveBg, activeFg, inactiveFg);
            UpdateButtonTab(TabImages, _currentCategory == "Image", activeBg, inactiveBg, activeFg, inactiveFg);
            UpdateButtonTab(TabCode, _currentCategory == "Code", activeBg, inactiveBg, activeFg, inactiveFg);
            UpdateButtonTab(TabStarred, _currentCategory == "pinned", activeBg, inactiveBg, activeFg, inactiveFg);
        }

        private void UpdateButtonTab(Button btn, bool isActive, Brush activeBg, Brush inactiveBg, Brush activeFg, Brush inactiveFg)
        {
            if (btn == null) return;
            btn.Background = isActive ? activeBg : inactiveBg;
            btn.Foreground = isActive ? activeFg : inactiveFg;
            btn.BorderBrush = isActive ? activeBg : (Brush)this.Resources["TabPillBorderBrush"];
            btn.BorderThickness = new Thickness(1);
            btn.FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal;
        }

        private void SearchInputBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (SearchPlaceholder != null)
            {
                SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchInputBox.Text) 
                    ? Visibility.Visible 
                    : Visibility.Collapsed;
            }
            RefreshCards();
        }

        private void SearchInputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                this.Hide();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter)
            {
                if (CardsListBox.SelectedItem is ClipboardItem selected)
                {
                    TriggerPaste(selected);
                }
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Down)
            {
                if (CardsListBox.SelectedIndex < DisplayCards.Count - 1)
                {
                    CardsListBox.SelectedIndex++;
                    CardsListBox.ScrollIntoView(CardsListBox.SelectedItem);
                }
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Up)
            {
                if (CardsListBox.SelectedIndex > 0)
                {
                    CardsListBox.SelectedIndex--;
                    CardsListBox.ScrollIntoView(CardsListBox.SelectedItem);
                }
                e.Handled = true;
                return;
            }

            // 快捷秒贴：支持 Alt + 1~9 或 Ctrl + 1~9 (防止阻断用户正常在搜索框输入数字)
            bool hasModifier = Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt) ||
                               Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);

            if (hasModifier)
            {
                int index = -1;
                if (e.Key >= Key.D1 && e.Key <= Key.D9) index = e.Key - Key.D1;
                else if (e.Key >= Key.NumPad1 && e.Key <= Key.NumPad9) index = e.Key - Key.NumPad1;

                if (index >= 0 && index < DisplayCards.Count)
                {
                    TriggerPaste(DisplayCards[index]);
                    e.Handled = true;
                    return;
                }
            }
        }

        private void CardsListBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                this.Hide();
                e.Handled = true;
                return;
            }

            // 在列表获得焦点时，直接按数字 1~9 或 Alt+1~9 均可秒贴
            int numIndex = -1;
            if (e.Key >= Key.D1 && e.Key <= Key.D9) numIndex = e.Key - Key.D1;
            else if (e.Key >= Key.NumPad1 && e.Key <= Key.NumPad9) numIndex = e.Key - Key.NumPad1;

            if (numIndex >= 0 && numIndex < DisplayCards.Count)
            {
                TriggerPaste(DisplayCards[numIndex]);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter && CardsListBox.SelectedItem is ClipboardItem selected)
            {
                TriggerPaste(selected);
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && CardsListBox.SelectedItem is ClipboardItem toDel)
            {
                _dbService.DeleteItem(toDel.Id);
                RefreshCards();
                UpdateStorageInfo();
                e.Handled = true;
            }
            else if (e.Key == Key.P && CardsListBox.SelectedItem is ClipboardItem toPin)
            {
                _dbService.TogglePin(toPin.Id);
                RefreshCards();
                UpdateStorageInfo();
                e.Handled = true;
            }
            else if ((e.Key == Key.Apps || (e.Key == Key.F10 && (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)) && 
                     CardsListBox.SelectedItem is ClipboardItem toMenu)
            {
                var container = CardsListBox.ItemContainerGenerator.ContainerFromItem(toMenu) as FrameworkElement ?? CardsListBox;
                ShowCardContextMenu(toMenu, container, isFromRightClick: false);
                e.Handled = true;
            }
        }

        private System.Windows.Threading.DispatcherTimer? _toastTimer;

        private void ShowToast(string message)
        {
            if (ToastText == null) return;
            ToastText.Text = message;
            ToastText.Visibility = Visibility.Visible;

            _toastTimer?.Stop();
            _toastTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1.8)
            };
            _toastTimer.Tick += (s, e) =>
            {
                _toastTimer.Stop();
                ToastText.Visibility = Visibility.Collapsed;
            };
            _toastTimer.Start();
        }

        private void CopyItemToClipboard(ClipboardItem item)
        {
            if (item == null) return;
            try
            {
                _monitorService.Pause();

                if (item.IsImage && !string.IsNullOrEmpty(item.FullImagePath) && File.Exists(item.FullImagePath))
                {
                    var bmp = new BitmapImage(new Uri(item.FullImagePath, UriKind.Absolute));
                    Clipboard.SetImage(bmp);
                }
                else
                {
                    Clipboard.SetText(item.Content ?? string.Empty);
                }

                ShowToast(LocalizationService.Instance.Get("ToastCopied"));
            }
            catch (Exception ex)
            {
                ShowToast($"Error: {ex.Message}");
            }
            finally
            {
                _monitorService.Resume();
            }
        }

        private void CardsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true; // 拦截双击事件，避免冒泡
            if (CardsListBox.SelectedItem is ClipboardItem selected)
            {
                if (_settingsService.CurrentSettings.DoubleClickAction == "PasteAndHide")
                {
                    TriggerPaste(selected);
                }
                else
                {
                    // 默认安全交互：仅复制到系统剪贴板，保持窗口打开，绝不收起/最小化到托盘！
                    CopyItemToClipboard(selected);
                }
            }
        }

        private void PinItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int id)
            {
                _dbService.TogglePin(id);
                RefreshCards();
                UpdateStorageInfo();
            }
        }

        private void DeleteItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int id)
            {
                _dbService.DeleteItem(id);
                RefreshCards();
                UpdateStorageInfo();
            }
        }

        // 卡片右键交互与上下文菜单
        private void ListBoxItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (SettingsOverlayHost != null && SettingsOverlayHost.Visibility == Visibility.Visible)
            {
                return;
            }

            if (sender is ListBoxItem lbi && lbi.DataContext is ClipboardItem item)
            {
                lbi.IsSelected = true;
                lbi.Focus();
                CardsListBox.SelectedItem = item;
            }
        }

        private void ListBoxItem_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (SettingsOverlayHost != null && SettingsOverlayHost.Visibility == Visibility.Visible)
            {
                return;
            }

            if (sender is ListBoxItem lbi && lbi.DataContext is ClipboardItem item)
            {
                e.Handled = true;
                ShowCardContextMenu(item, lbi, isFromRightClick: true);
            }
        }

        private void ItemMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int id)
            {
                var item = DisplayCards.FirstOrDefault(x => x.Id == id);
                if (item == null) return;
                ShowCardContextMenu(item, btn, isFromRightClick: false);
            }
        }

        private void ShowCardContextMenu(ClipboardItem item, FrameworkElement placementTarget, bool isFromRightClick)
        {
            if (item == null) return;

            // 保持当前选中项与此卡片同步
            CardsListBox.SelectedItem = item;

            var loc = LocalizationService.Instance;
            var menu = new ContextMenu();
            if (TryFindResource("ModernContextMenuStyle") is Style ctxStyle)
            {
                menu.Style = ctxStyle;
            }

            Style? itemStyle = TryFindResource("ModernMenuItemStyle") as Style;

            // 1. 置顶 / 取消置顶 (高雅暖金实心星号 ★)
            var pinIcon = new TextBlock 
            { 
                Text = "★", 
                Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11)), 
                FontSize = 13, 
                FontWeight = FontWeights.Bold, 
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var pinItem = new MenuItem 
            { 
                Icon = pinIcon,
                Header = item.IsPinned ? loc.Get("MenuUnpin") : loc.Get("MenuPin") 
            };
            if (itemStyle != null) pinItem.Style = itemStyle;
            pinItem.Click += (s, args) =>
            {
                _dbService.TogglePin(item.Id);
                RefreshCards();
                UpdateStorageInfo();
            };

            // 2. 复制到剪贴板 (现代天蓝双矩形符号 ❐)
            var copyIcon = new TextBlock 
            { 
                Text = "❐", 
                Foreground = new SolidColorBrush(Color.FromRgb(0, 120, 212)), 
                FontSize = 13, 
                FontWeight = FontWeights.Bold, 
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var copyItem = new MenuItem 
            { 
                Icon = copyIcon,
                Header = loc.Get("MenuCopy") 
            };
            if (itemStyle != null) copyItem.Style = itemStyle;
            copyItem.Click += (s, args) =>
            {
                CopyItemToClipboard(item);
            };

            // 3. 删除此条记录 (警示珊瑚红删除符号 ✕)
            var delIcon = new TextBlock 
            { 
                Text = "✕", 
                Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)), 
                FontSize = 13, 
                FontWeight = FontWeights.Bold, 
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var delItem = new MenuItem 
            { 
                Icon = delIcon,
                Header = loc.Get("MenuDelete") 
            };
            if (itemStyle != null) delItem.Style = itemStyle;
            delItem.Click += (s, args) =>
            {
                _dbService.DeleteItem(item.Id);
                RefreshCards();
                UpdateStorageInfo();
            };

            menu.Items.Add(pinItem);
            menu.Items.Add(copyItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(delItem);

            if (isFromRightClick)
            {
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            }
            else
            {
                // 三点按钮位于卡片右上角：向左内收展开，完美收纳在当前卡片内部，绝不横跨相邻卡片产生边框割裂！
                menu.PlacementTarget = placementTarget;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.HorizontalOffset = -140;
                menu.VerticalOffset = 2;
            }

            menu.IsOpen = true;
        }

        private void BtnBrowseStorage_Click(object sender, RoutedEventArgs e)
        {
            var loc = LocalizationService.Instance;
            var dialog = new OpenFolderDialog
            {
                Title = loc.Get("FolderDialogTitle"),
                InitialDirectory = _settingsService.CurrentSettings.StorageDirectory
            };

            if (dialog.ShowDialog() == true)
            {
                string chosenPath = dialog.FolderName;
                if (_settingsService.UpdateStorageDirectory(chosenPath, true))
                {
                    TxtSettingsStoragePath.Text = _settingsService.CurrentSettings.StorageDirectory;
                    RefreshCards();
                    UpdateStorageInfo();

                    MessageBox.Show(loc.Get("StorageMigratedMsg", _settingsService.CurrentSettings.StorageDirectory), loc.Get("StorageMigratedTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }
    }
}