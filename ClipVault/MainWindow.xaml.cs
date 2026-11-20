using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipVault.Helpers;
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
        private readonly LanSyncService _lanSyncService;

        private string _currentCategory = "all";
        private int? _pendingTagAssignItemId = null;
        private Point _mainCardDragStartPoint;
        private bool _isMainCardMouseDownForDrag = false;
        private ClipboardItem? _dragCandidateMainCardItem = null;
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
        private System.Threading.CancellationTokenSource? _mainBackfillCts;

        public MainWindow(DatabaseService dbService, SettingsService settingsService, ClipboardMonitorService monitorService, PasteSimulator pasteSimulator, LanSyncService? lanSyncService = null)
        {
            _dbService = dbService;
            _settingsService = settingsService;
            _monitorService = monitorService;
            _pasteSimulator = pasteSimulator;
            _lanSyncService = lanSyncService ?? new LanSyncService(_dbService, _settingsService, _monitorService);
            _isInternalSettingUpdate = true;

            InitializeComponent();

            if (TxtMainAppVersion != null)
            {
                var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                TxtMainAppVersion.Text = ver != null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "v3.9.0";
            }

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
            RefreshMainCustomTagPills();
            RefreshCards();
            MemoryHelper.BindLiveMemoryMonitor(this, TxtMainMemoryUsage);

            _dbService.CustomTagsChanged += () =>
            {
                Dispatcher.Invoke(() =>
                {
                    RefreshMainCustomTagPills();
                    if (this.IsVisible) RefreshCards();
                });
            };

            _lanSyncService.ItemReceivedFromLan += (item) =>
            {
                Dispatcher.Invoke(() =>
                {
                    RefreshCards();
                    UpdateStorageInfo();
                    ShowToast(LocalizationService.Instance.CurrentLanguage == "en-US"
                        ? "Received from LAN Drop"
                        : "已收到局域网快传内容");
                });
            };

            _lanSyncService.StatusMessageChanged += (msg) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (TxtMainLanSyncLiveStatus != null)
                    {
                        TxtMainLanSyncLiveStatus.Text = msg;
                    }
                    UpdateMainLanSyncBtnHighlight();
                });
            };

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

            // 监听后台新捕获的剪贴板条目（仅在前台可见时刷新，后台静默不产生视觉对象）
            _monitorService.ItemCaptured += (item) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (this.IsVisible)
                    {
                        RefreshCards();
                        UpdateStorageInfo();
                    }
                });
            };

            _monitorService.ItemOcrCompleted += (itemId, ocrText) =>
            {
                Dispatcher.Invoke(() =>
                {
                    var card = DisplayCards.FirstOrDefault(x => x.Id == itemId);
                    if (card != null)
                    {
                        card.OcrText = ocrText;
                    }
                });
            };

            // 窗口隐藏时主动释放内存工作集与临时缓存
            this.IsVisibleChanged += (s, e) =>
            {
                if (!this.IsVisible)
                {
                    foreach (var it in DisplayCards)
                    {
                        it.ClearImageCache();
                    }
                    ClipboardItem.ClearThumbnailCache();
                    ClipVault.Helpers.MemoryHelper.ScheduleIdleTrim();
                }
            };

            // 失去焦点时交互逻辑（默认关闭，点击外部绝不自动收起/绝不最小化到托盘！）
            this.Deactivated += (s, e) =>
            {
                if (_settingsService != null && 
                    _settingsService.CurrentSettings.HideOnDeactivate &&
                    (SettingsOverlayHost == null || SettingsOverlayHost.Visibility != Visibility.Visible) && 
                    (MainAddTagOverlay == null || MainAddTagOverlay.Visibility != Visibility.Visible) &&
                    (MainLanSyncOverlay == null || MainLanSyncOverlay.Visibility != Visibility.Visible) &&
                    !ClipVault.Views.TemplateInputDialog.IsAnyDialogOpen &&
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
            bool prev = _isInternalSettingUpdate;
            _isInternalSettingUpdate = true;
            try
            {
                var s = _settingsService.CurrentSettings;
                TxtSettingsStoragePath.Text = s.StorageDirectory;
                if (ChkMainFollowAppDir != null) ChkMainFollowAppDir.IsChecked = s.FollowAppDirectory;
                UpdateMainStorageLockUiState(s.FollowAppDirectory);

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

                // 0.6 贴边方向与多显示器策略加载
                if (RadioMainDockLeft != null && RadioMainDockRight != null)
                {
                    if (string.Equals(s.DockEdge, "Left", StringComparison.OrdinalIgnoreCase)) RadioMainDockLeft.IsChecked = true;
                    else RadioMainDockRight.IsChecked = true;
                }
                if (RadioMainMonitorPrimary != null && RadioMainMonitorCursor != null)
                {
                    if (string.Equals(s.MonitorStrategy, "Primary", StringComparison.OrdinalIgnoreCase)) RadioMainMonitorPrimary.IsChecked = true;
                    else RadioMainMonitorCursor.IsChecked = true;
                }

                // 0.7 全屏防误触与敏感信息自动遮罩加载
                if (ChkMainFullscreenDnd != null) ChkMainFullscreenDnd.IsChecked = s.EnableFullscreenDnd;
                if (ChkMainManualDnd != null) ChkMainManualDnd.IsChecked = s.IsManualDndMode;
                if (ChkMainAutoMaskSensitive != null) ChkMainAutoMaskSensitive.IsChecked = s.AutoMaskSensitiveData;
                if (ChkMainIgnorePasswordManagers != null) ChkMainIgnorePasswordManagers.IsChecked = s.IgnorePasswordManagers;
                if (TxtMainPrivacyBlacklist != null) TxtMainPrivacyBlacklist.Text = s.CustomPrivacyBlacklist ?? string.Empty;
                if (ChkMainDeduplicateOnCopy != null) ChkMainDeduplicateOnCopy.IsChecked = s.DeduplicateOnCopy;

                switch (s.MaxHistoryCount)
                {
                    case 200: if (RadioMainMaxHist200 != null) RadioMainMaxHist200.IsChecked = true; break;
                    case 500: if (RadioMainMaxHist500 != null) RadioMainMaxHist500.IsChecked = true; break;
                    case 2000: if (RadioMainMaxHist2000 != null) RadioMainMaxHist2000.IsChecked = true; break;
                    case 0: if (RadioMainMaxHist0 != null) RadioMainMaxHist0.IsChecked = true; break;
                    default: if (RadioMainMaxHist1000 != null) RadioMainMaxHist1000.IsChecked = true; break;
                }

                ClipboardItem.GlobalAutoMaskEnabled = s.AutoMaskSensitiveData;
                foreach (var c in DisplayCards) c.NotifyMaskStateChanged();

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

                double clampedOpacity = ThemePalette.NormalizeWindowOpacity(s.WindowOpacity);
                s.WindowOpacity = clampedOpacity;
                int transparencyPct = Math.Clamp((int)Math.Round((1.0 - clampedOpacity) * 100.0), 0, 85);
                SliderOpacity.Value = transparencyPct;
                TxtOpacityValue.Text = $"{transparencyPct}%";

                string backdrop = s.BackdropMaterial ?? "Default";
                if (string.Equals(backdrop, "Mica", StringComparison.OrdinalIgnoreCase))
                {
                    if (RadioMainBackdropMica != null) RadioMainBackdropMica.IsChecked = true;
                }
                else if (string.Equals(backdrop, "Acrylic", StringComparison.OrdinalIgnoreCase))
                {
                    if (RadioMainBackdropAcrylic != null) RadioMainBackdropAcrylic.IsChecked = true;
                }
                else
                {
                    if (RadioMainBackdropDefault != null) RadioMainBackdropDefault.IsChecked = true;
                }
                if (ChkMainSoundEffects != null) ChkMainSoundEffects.IsChecked = s.EnableSoundEffects;

                this.Opacity = 1.0;
                ApplyTheme(theme);

                UpdateStorageInfo();
            }
            finally
            {
                _isInternalSettingUpdate = prev;
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
            if (BtnMainLanSync != null) BtnMainLanSync.ToolTip = loc.Get("LanSyncBtnTip");
            if (BtnMainAddTag != null) BtnMainAddTag.ToolTip = loc.Get("TagAddBtnTip");
            if (BtnToggleSettings != null) BtnToggleSettings.ToolTip = loc.Get("ToolTipSettings");
            if (BtnMinimizeWindow != null) BtnMinimizeWindow.ToolTip = loc.Get("ToolTipMinimize");
            if (BtnQuickTheme != null)
            {
                bool isDark = _settingsService?.CurrentSettings.ThemeMode != "Light";
                BtnQuickTheme.ToolTip = isDark ? loc.Get("ToolTipThemeLight") : loc.Get("ToolTipThemeDark");
            }
            UpdateCloseButtonToolTip();

            // 自定义标签与局域网快传浮层国际化
            if (TxtMainAddTagDialogTitle != null) TxtMainAddTagDialogTitle.Text = loc.Get("AddTagDialogTitle");
            if (TxtMainAddTagDialogSub != null) TxtMainAddTagDialogSub.Text = string.Empty;
            if (TxtMainAddTagIconLabel != null) TxtMainAddTagIconLabel.Text = loc.Get("AddTagIconLabel");
            if (TxtMainAddTagNameLabel != null) TxtMainAddTagNameLabel.Text = loc.Get("AddTagNameLabel");
            if (TxtMainAddTagTemplateLabel != null) TxtMainAddTagTemplateLabel.Text = loc.Get("AddTagTemplateLabel");
            if (BtnMainCancelAddTag != null) BtnMainCancelAddTag.Content = loc.Get("BtnCancel");
            if (BtnMainConfirmAddTag != null) BtnMainConfirmAddTag.Content = loc.Get("BtnSaveTag");

            if (TxtMainLanSyncTitle != null) TxtMainLanSyncTitle.Text = loc.Get("LanSyncTitle");
            if (TxtMainLanSyncSubTitle != null) TxtMainLanSyncSubTitle.Text = loc.Get("LanSyncSubTitle");
            if (BtnMainCopyLanUrl != null) BtnMainCopyLanUrl.Content = loc.Get("LanSyncCopyUrl");
            if (BtnMainKeepLanSyncRunning != null) BtnMainKeepLanSyncRunning.Content = loc.Get("LanSyncKeepRunning");
            if (BtnMainStopLanSync != null) BtnMainStopLanSync.Content = loc.Get("LanSyncStopBtn");

            // 设置浮层面板
            if (TxtSettingsTitle != null) TxtSettingsTitle.Text = loc.Get("SettingsTitle");
            if (TxtSettingsDragTip != null) TxtSettingsDragTip.Text = loc.Get("SettingsDragTip");
            if (TxtLanguageTitle != null) TxtLanguageTitle.Text = loc.Get("LangSectionTitle");
            if (RadioLangZh != null) RadioLangZh.Content = loc.Get("LangZh");
            if (RadioLangEn != null) RadioLangEn.Content = loc.Get("LangEn");

            if (TxtWindowModeTitle != null) TxtWindowModeTitle.Text = loc.Get("WindowModeTitle");
            if (RadioModeSidebar != null) RadioModeSidebar.Content = loc.Get("WindowModeSidebar");
            if (RadioModeCentered != null) RadioModeCentered.Content = loc.Get("WindowModeCentered");

            if (TxtMainDockAndMonitorTitle != null) TxtMainDockAndMonitorTitle.Text = loc.Get("DockAndMonitorTitle");
            if (TxtMainDockEdgeLabel != null) TxtMainDockEdgeLabel.Text = loc.Get("DockEdgeLabel");
            if (RadioMainDockRight != null) RadioMainDockRight.Content = loc.Get("DockEdgeRight");
            if (RadioMainDockLeft != null) RadioMainDockLeft.Content = loc.Get("DockEdgeLeft");
            if (TxtMainMonitorStrategyLabel != null) TxtMainMonitorStrategyLabel.Text = loc.Get("MonitorStrategyLabel");
            if (RadioMainMonitorCursor != null) RadioMainMonitorCursor.Content = loc.Get("MonitorFollowCursor");
            if (RadioMainMonitorPrimary != null) RadioMainMonitorPrimary.Content = loc.Get("MonitorPrimaryOnly");
            if (TxtMainSmartGuardTitle != null) TxtMainSmartGuardTitle.Text = loc.Get("SmartGuardTitle");
            if (ChkMainFullscreenDnd != null)
            {
                ChkMainFullscreenDnd.Content = loc.Get("ChkFullscreenDnd");
                ChkMainFullscreenDnd.ToolTip = loc.Get("ChkFullscreenDndTip");
            }
            if (ChkMainManualDnd != null) ChkMainManualDnd.Content = loc.Get("ChkManualDnd");
            if (ChkMainAutoMaskSensitive != null)
            {
                ChkMainAutoMaskSensitive.Content = loc.Get("ChkAutoMaskSensitive");
                ChkMainAutoMaskSensitive.ToolTip = loc.Get("ChkAutoMaskSensitiveTip");
            }
            foreach (var c in DisplayCards) c.NotifyMaskStateChanged();

            if (TxtStoragePathTitle != null) TxtStoragePathTitle.Text = loc.Get("StoragePathTitle");
            if (ChkMainFollowAppDir != null) ChkMainFollowAppDir.Content = loc.Get("FollowAppDirCheck");
            if (BtnBrowseStorage != null) BtnBrowseStorage.Content = loc.Get("BrowseButton");
            UpdateMainStorageLockUiState(_settingsService?.CurrentSettings.FollowAppDirectory ?? true);

            if (TxtAppearanceTitle != null) TxtAppearanceTitle.Text = loc.Get("AppearanceTitle");
            if (RadioThemeDark != null) RadioThemeDark.Content = loc.Get("ThemeDarkShort");
            if (RadioThemeLight != null) RadioThemeLight.Content = loc.Get("ThemeLightShort");
            if (RadioThemeAurora != null) RadioThemeAurora.Content = loc.Get("ThemeAuroraShort");
            if (RadioThemeSunset != null) RadioThemeSunset.Content = loc.Get("ThemeSunsetShort");
            if (RadioThemeOcean != null) RadioThemeOcean.Content = loc.Get("ThemeOceanShort");
            if (RadioThemeCustom != null) RadioThemeCustom.Content = loc.Get("ThemeCustomShort");

            if (BtnMainUploadBg != null) BtnMainUploadBg.Content = isEn ? "Upload" : "选择图片";
            if (BtnMainResetBg != null) BtnMainResetBg.Content = isEn ? "Reset" : "重置";
            if (TxtMainBgOverlayLabel != null) TxtMainBgOverlayLabel.Text = isEn ? "Overlay" : "遮罩";
            if (TxtOpacityLabel != null) TxtOpacityLabel.Text = loc.Get("OpacityLabel");
            if (TxtMainBackdropAndAudioTitle != null) TxtMainBackdropAndAudioTitle.Text = loc.Get("BackdropAndAudioTitle");
            if (TxtMainBackdropLabel != null) TxtMainBackdropLabel.Text = loc.Get("BackdropLabel");
            if (RadioMainBackdropDefault != null) RadioMainBackdropDefault.Content = loc.Get("BackdropDefault");
            if (RadioMainBackdropMica != null) RadioMainBackdropMica.Content = loc.Get("BackdropMica");
            if (RadioMainBackdropAcrylic != null) RadioMainBackdropAcrylic.Content = loc.Get("BackdropAcrylic");
            if (ChkMainSoundEffects != null) ChkMainSoundEffects.Content = loc.Get("ChkSoundEffects");

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

            if (BtnMainClearHistory != null) BtnMainClearHistory.Content = loc.Get("TrayClear");
            if (BtnDirectExit != null) BtnDirectExit.Content = loc.Get("BtnExitApp");
            if (BtnCloseSettingsOverlay != null) BtnCloseSettingsOverlay.Content = loc.Get("BtnDone");
            if (ChkMainIgnorePasswordManagers != null) ChkMainIgnorePasswordManagers.Content = loc.Get("ChkIgnorePasswordManagers");
            if (TxtMainPrivacyBlacklistLabel != null) TxtMainPrivacyBlacklistLabel.Text = loc.Get("PrivacyBlacklistLabel");
            if (TxtMainPrivacyBlacklist != null) TxtMainPrivacyBlacklist.ToolTip = loc.Get("PrivacyBlacklistTip");
            if (ChkMainDeduplicateOnCopy != null) ChkMainDeduplicateOnCopy.Content = loc.Get("ChkDeduplicateOnCopy");
            if (TxtMainMaxHistoryLabel != null) TxtMainMaxHistoryLabel.Text = loc.Get("MaxHistoryLabel");
            if (RadioMainMaxHist0 != null) RadioMainMaxHist0.Content = loc.Get("MaxHistoryUnlimited");
            if (BtnMainBackfillOcr != null && _mainBackfillCts == null) BtnMainBackfillOcr.Content = loc.Get("BtnBackfillOcr");
            if (BtnMainExportBackup != null) BtnMainExportBackup.Content = loc.Get("BtnExportBackup");
            if (BtnMainImportBackup != null) BtnMainImportBackup.Content = loc.Get("BtnImportBackup");

            // 底部快捷键指南
            if (TxtBottomTips != null) TxtBottomTips.Text = loc.Get("BottomTips");
            if (TxtMainMemoryUsage != null) TxtMainMemoryUsage.ToolTip = loc.Get("MemoryUsageTip");
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

        private void MainDockEdgeRadio_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInternalSettingUpdate || _settingsService == null) return;
            string newEdge = RadioMainDockLeft?.IsChecked == true ? "Left" : "Right";
            if (!string.Equals(_settingsService.CurrentSettings.DockEdge, newEdge, StringComparison.OrdinalIgnoreCase))
            {
                _settingsService.CurrentSettings.DockEdge = newEdge;
                _settingsService.SaveSettings();
            }
        }

        private void MainMonitorStrategyRadio_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInternalSettingUpdate || _settingsService == null) return;
            string newStrategy = RadioMainMonitorPrimary?.IsChecked == true ? "Primary" : "FollowCursor";
            if (!string.Equals(_settingsService.CurrentSettings.MonitorStrategy, newStrategy, StringComparison.OrdinalIgnoreCase))
            {
                _settingsService.CurrentSettings.MonitorStrategy = newStrategy;
                _settingsService.SaveSettings();
            }
        }

        private void ChkMainSmartGuard_Click(object sender, RoutedEventArgs e)
        {
            if (_isInternalSettingUpdate || _settingsService == null) return;
            var s = _settingsService.CurrentSettings;
            bool changed = false;

            if (ChkMainFullscreenDnd != null && s.EnableFullscreenDnd != (ChkMainFullscreenDnd.IsChecked == true))
            {
                s.EnableFullscreenDnd = ChkMainFullscreenDnd.IsChecked == true;
                changed = true;
            }
            if (ChkMainManualDnd != null && s.IsManualDndMode != (ChkMainManualDnd.IsChecked == true))
            {
                s.IsManualDndMode = ChkMainManualDnd.IsChecked == true;
                changed = true;
            }
            if (ChkMainAutoMaskSensitive != null && s.AutoMaskSensitiveData != (ChkMainAutoMaskSensitive.IsChecked == true))
            {
                s.AutoMaskSensitiveData = ChkMainAutoMaskSensitive.IsChecked == true;
                ClipboardItem.GlobalAutoMaskEnabled = s.AutoMaskSensitiveData;
                foreach (var c in DisplayCards) c.NotifyMaskStateChanged();
                changed = true;
            }
            if (ChkMainIgnorePasswordManagers != null && s.IgnorePasswordManagers != (ChkMainIgnorePasswordManagers.IsChecked == true))
            {
                s.IgnorePasswordManagers = ChkMainIgnorePasswordManagers.IsChecked == true;
                changed = true;
            }

            if (changed)
            {
                _settingsService.SaveSettings();
            }
        }

        private void TxtMainPrivacyBlacklist_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_isInternalSettingUpdate || _settingsService == null || TxtMainPrivacyBlacklist == null) return;
            string next = TxtMainPrivacyBlacklist.Text?.Trim() ?? string.Empty;
            if (!string.Equals(_settingsService.CurrentSettings.CustomPrivacyBlacklist, next, StringComparison.Ordinal))
            {
                _settingsService.CurrentSettings.CustomPrivacyBlacklist = next;
                _settingsService.SaveSettings();
            }
        }

        private void ChkMainDeduplicateOnCopy_Click(object sender, RoutedEventArgs e)
        {
            if (_isInternalSettingUpdate || _settingsService == null || ChkMainDeduplicateOnCopy == null) return;
            _settingsService.CurrentSettings.DeduplicateOnCopy = ChkMainDeduplicateOnCopy.IsChecked == true;
            _settingsService.SaveSettings();
        }

        private void MainMaxHistoryRadio_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInternalSettingUpdate || _settingsService == null) return;
            if (sender is RadioButton rb && rb.IsChecked == true && rb.Tag is string raw && int.TryParse(raw, out int limit))
            {
                if (_settingsService.CurrentSettings.MaxHistoryCount != limit)
                {
                    _settingsService.CurrentSettings.MaxHistoryCount = limit;
                    _settingsService.SaveSettings();
                    if (limit > 0)
                    {
                        _dbService.PruneOldItemsAndOrphanImages(limit);
                        RefreshCards();
                        UpdateStorageInfo();
                    }
                }
            }
        }

        private async void BtnMainBackfillOcr_Click(object sender, RoutedEventArgs e)
        {
            var loc = LocalizationService.Instance;
            if (_mainBackfillCts != null)
            {
                _mainBackfillCts.Cancel();
                return;
            }

            if (_dbService.CountImagesMissingOcr() <= 0)
            {
                ShowToast(loc.Get("BackfillOcrNone"));
                return;
            }

            _mainBackfillCts = new System.Threading.CancellationTokenSource();
            var token = _mainBackfillCts.Token;
            try
            {
                int indexed = await NativeOcrService.BackfillMissingOcrAsync(_dbService, (done, total, _) =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (BtnMainBackfillOcr != null) BtnMainBackfillOcr.Content = loc.Get("BtnBackfillOcrWorking", done, total);
                    });
                }, token);

                RefreshCards();
                ShowToast(loc.Get("BackfillOcrDone", indexed));
            }
            catch { }
            finally
            {
                _mainBackfillCts?.Dispose();
                _mainBackfillCts = null;
                if (BtnMainBackfillOcr != null) BtnMainBackfillOcr.Content = loc.Get("BtnBackfillOcr");
            }
        }

        private void BtnMainExportBackup_Click(object sender, RoutedEventArgs e)
        {
            if (_settingsService == null) return;
            try
            {
                var dlg = new SaveFileDialog
                {
                    Title = LocalizationService.Instance.Get("BtnExportBackup"),
                    Filter = "CopyBox Backup (*.copybox.json)|*.copybox.json|JSON (*.json)|*.json",
                    FileName = $"CopyBox-Backup-{DateTime.Now:yyyyMMdd}.copybox.json"
                };
                if (dlg.ShowDialog() == true)
                {
                    var (tags, snippets, actions) = ProfileBackupService.ExportPackage(dlg.FileName, _settingsService, _dbService);
                    ShowToast(LocalizationService.Instance.Get("BackupExportDone", tags, snippets, actions));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Export Backup", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnMainImportBackup_Click(object sender, RoutedEventArgs e)
        {
            if (_settingsService == null) return;
            try
            {
                var dlg = new OpenFileDialog
                {
                    Title = LocalizationService.Instance.Get("BtnImportBackup"),
                    Filter = "CopyBox Backup (*.copybox.json;*.json)|*.copybox.json;*.json|All Files (*.*)|*.*"
                };
                if (dlg.ShowDialog() == true)
                {
                    var (tags, addedSnippets, mergedActions) = ProfileBackupService.ImportPackage(dlg.FileName, _settingsService, _dbService);
                    RefreshMainCustomTagPills();
                    RefreshCards();
                    UpdateStorageInfo();
                    ShowToast(LocalizationService.Instance.Get("BackupImportDone", tags, addedSnippets, mergedActions));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Import Backup", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void UpdateStorageInfo()
        {
            var (dbSize, totalCount, pinnedCount, imgCount, imgSize) = _dbService.GetStats();
            TxtStorageStatusInfo.Text = LocalizationService.Instance.Get("StatusInfoFormat", totalCount, pinnedCount, imgCount);
        }

        public void ShowAndActivate()
        {
            ClipVault.Helpers.MemoryHelper.CancelIdleTrim();
            // 记录当前活跃的工作窗口句柄，供选定后自动模拟粘贴
            _pasteSimulator.RecordTargetWindow();

            SearchInputBox.Text = string.Empty;
            if (SettingsOverlayHost != null) SettingsOverlayHost.Visibility = Visibility.Collapsed;
            if (MainAddTagOverlay != null) MainAddTagOverlay.Visibility = Visibility.Collapsed;
            if (MainLanSyncOverlay != null) MainLanSyncOverlay.Visibility = Visibility.Collapsed;
            UpdateMainLanSyncBtnHighlight();
            RefreshMainCustomTagPills();
            RefreshCards();

            // 根据多显示器策略在目标显示器工作区居中展示
            try
            {
                string monitorStrategy = _settingsService?.CurrentSettings.MonitorStrategy ?? "FollowCursor";
                var (_, workRect, scaleX, scaleY) = ClipVault.Interop.NativeMethods.GetTargetMonitorBounds(monitorStrategy);
                if (scaleX <= 0) scaleX = 1.0;
                if (scaleY <= 0) scaleY = 1.0;

                double workLeftDip = workRect.Left / scaleX;
                double workTopDip = workRect.Top / scaleY;
                double workWidthDip = workRect.Width / scaleX;
                double workHeightDip = workRect.Height / scaleY;

                double winW = this.Width > 0 ? this.Width : 640;
                double winH = this.Height > 0 ? this.Height : 540;

                if (workWidthDip > 100 && workHeightDip > 100)
                {
                    this.WindowStartupLocation = WindowStartupLocation.Manual;
                    this.Left = workLeftDip + Math.Max(0, (workWidthDip - winW) / 2.0);
                    this.Top = workTopDip + Math.Max(0, (workHeightDip - winH) / 2.0);
                }
            }
            catch { }

            if (this.WindowState == WindowState.Minimized)
            {
                this.WindowState = WindowState.Normal;
            }
            this.Show();
            this.Activate();

            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                string theme = _settingsService?.CurrentSettings.ThemeMode ?? "Dark";
                string backdropMode = _settingsService?.CurrentSettings.BackdropMaterial ?? "Default";
                bool isDark = !string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase);
                ClipVault.Interop.NativeMethods.ApplyWindowBackdrop(hwnd, backdropMode, isDark);
                ClipVault.Interop.NativeMethods.SetForegroundWindow(hwnd);
            }
            ClipVault.Helpers.SoundEffectHelper.PlayPopCue(_settingsService?.CurrentSettings);

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
            string keyword = SearchInputBox.Text.Trim();
            var items = _dbService.GetRecentItems(keyword, _currentCategory, 60);

            for (int i = 0; i < items.Count; i++)
            {
                items[i].DisplayBadge = i < 9 ? $"[{i + 1}]" : string.Empty;
            }

            int common = Math.Min(DisplayCards.Count, items.Count);
            for (int i = 0; i < common; i++)
            {
                var existing = DisplayCards[i];
                var next = items[i];
                if (existing.Id == next.Id &&
                    existing.IsPinned == next.IsPinned &&
                    existing.CreatedAt == next.CreatedAt &&
                    string.Equals(existing.CustomTag, next.CustomTag, StringComparison.Ordinal))
                {
                    if (!string.Equals(existing.DisplayBadge, next.DisplayBadge, StringComparison.Ordinal))
                    {
                        existing.DisplayBadge = next.DisplayBadge;
                    }
                }
                else
                {
                    DisplayCards[i] = next;
                }
            }

            for (int i = common; i < items.Count; i++)
            {
                DisplayCards.Add(items[i]);
            }

            while (DisplayCards.Count > items.Count)
            {
                DisplayCards.RemoveAt(DisplayCards.Count - 1);
            }

            if (DisplayCards.Count > 0 && CardsListBox.SelectedIndex < 0)
            {
                CardsListBox.SelectedIndex = 0;
            }
        }

        private async void TriggerPaste(ClipboardItem item, bool plainTextOnly = false)
        {
            if (item == null) return;
            ShowToast(LocalizationService.Instance.Get(plainTextOnly ? "ToastPlainPasted" : "ToastPasted"));
            await _pasteSimulator.PasteItemAsync(item, () => this.Hide(), plainTextOnly);
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
            bool isLight = ThemePalette.IsLightTheme(mode);
            bool isCustom = mode.Equals("CustomImage", StringComparison.OrdinalIgnoreCase);
            bool isDark = !isLight;

            var loc = LocalizationService.Instance;
            bool isEn = loc.CurrentLanguage == "en-US";

            if (BtnQuickTheme != null)
            {
                BtnQuickTheme.Content = isLight ? "\uE708" : "\uE706";
                BtnQuickTheme.ToolTip = isLight ? loc.Get("ToolTipThemeDark") : loc.Get("ToolTipThemeLight");
            }

            if (BdMainCustomImageSettings != null)
            {
                BdMainCustomImageSettings.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
            }

            double opacity = ThemePalette.NormalizeWindowOpacity(_settingsService?.CurrentSettings.WindowOpacity ?? 0.92);
            string backdropMode = _settingsService?.CurrentSettings.BackdropMaterial ?? "Default";

            MainBorder.Background = ThemePalette.Apply(this, mode, opacity, backdropMode);

            if (isCustom)
            {
                string customPath = _settingsService?.CurrentSettings.CustomBackgroundImagePath ?? string.Empty;
                if (string.IsNullOrWhiteSpace(customPath) || !File.Exists(customPath))
                {
                    customPath = EnsureDefaultMainWallpaperExists();
                    if (TxtMainCustomBgPath != null) TxtMainCustomBgPath.Text = isEn ? "Built-in wallpaper" : "内置壁纸";
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
                            bmp.DecodePixelWidth = 960;
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
                    BdMainCustomBgOverlay.Background = new SolidColorBrush(Color.FromArgb(ovAlpha, 0x0E, 0x0E, 0x12));
                }
            }
            else
            {
                if (MainBgImageContainer != null) MainBgImageContainer.Visibility = Visibility.Collapsed;
                if (ImgMainCustomBg != null) ImgMainCustomBg.Source = null;
                _cachedMainWallpaperBitmap = null;
                _cachedMainWallpaperPath = null;
            }

            UpdateCloseButtonToolTip();
            UpdateCategoryTabsAppearance();

            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                ClipVault.Interop.NativeMethods.ApplyWindowBackdrop(hwnd, backdropMode, isDark);
            }
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
            double clamped = ThemePalette.NormalizeWindowOpacity(opacity);
            string mode = _settingsService?.CurrentSettings.ThemeMode ?? "Dark";
            string backdrop = _settingsService?.CurrentSettings.BackdropMaterial ?? "Default";
            bool isCustom = mode.Equals("CustomImage", StringComparison.OrdinalIgnoreCase);

            if (isCustom)
            {
                if (MainBgImageContainer != null) MainBgImageContainer.Opacity = clamped;
                return;
            }

            var (bgAlpha, _, cardAlpha) = ThemePalette.ComputeAlphas(clamped, backdrop, isCustom: false);

            if (MainBorder.Background is SolidColorBrush sb && !sb.IsFrozen)
            {
                var c = sb.Color;
                sb.Color = Color.FromArgb(bgAlpha, c.R, c.G, c.B);
            }

            SetMainBrushAlphaInPlace("CardBgBrush", cardAlpha);
            SetMainBrushAlphaInPlace("SearchBgBrush", cardAlpha);
        }

        private void SaveOwnSettingsSafely()
        {
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
        }

        private void FlushPendingSettingsSave()
        {
            if (_saveSettingsDebounceTimer != null && _saveSettingsDebounceTimer.IsEnabled)
            {
                _saveSettingsDebounceTimer.Stop();
                SaveOwnSettingsSafely();
            }
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
                    SaveOwnSettingsSafely();
                };
            }
            _saveSettingsDebounceTimer.Stop();
            _saveSettingsDebounceTimer.Start();
        }

        private void ApplyOpacity(double transparencyPct, bool save = true)
        {
            int pct = Math.Clamp((int)Math.Round(transparencyPct), 0, 85);
            double opacity = Math.Clamp(1.0 - (pct / 100.0), 0.15, 1.00);

            if (TxtOpacityValue != null)
            {
                TxtOpacityValue.Text = $"{pct}%";
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
            SaveOwnSettingsSafely();
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
                SaveOwnSettingsSafely();
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

        private void MainBackdropRadio_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInternalSettingUpdate || _settingsService == null) return;
            if (sender is not RadioButton rb || rb.Tag is not string mode || rb.IsChecked != true) return;
            if (!string.Equals(_settingsService.CurrentSettings.BackdropMaterial, mode, StringComparison.OrdinalIgnoreCase))
            {
                _settingsService.CurrentSettings.BackdropMaterial = mode;
                ApplyTheme(_settingsService.CurrentSettings.ThemeMode);
                SaveOwnSettingsSafely();
            }
        }

        private void ChkMainSoundEffects_Click(object sender, RoutedEventArgs e)
        {
            if (_isInternalSettingUpdate || _settingsService == null || ChkMainSoundEffects == null) return;
            bool enabled = ChkMainSoundEffects.IsChecked == true;
            if (_settingsService.CurrentSettings.EnableSoundEffects != enabled)
            {
                _settingsService.CurrentSettings.EnableSoundEffects = enabled;
                _settingsService.SaveSettings();
                if (enabled)
                {
                    ClipVault.Helpers.SoundEffectHelper.PlayCopyCue(_settingsService.CurrentSettings);
                }
            }
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
                ShowToast(isEn ? "History cleared" : "已清空未置顶历史");
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
            FlushPendingSettingsSave();
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
            // 如果点击在设置浮层或弹窗内部，绝对不触发主窗口整体拖拽
            if (SettingsOverlayHost != null && SettingsOverlayHost.Visibility == Visibility.Visible)
            {
                var pos = e.GetPosition(SettingsOverlayHost);
                if (pos.X >= 0 && pos.X <= SettingsOverlayHost.ActualWidth &&
                    pos.Y >= 0 && pos.Y <= SettingsOverlayHost.ActualHeight)
                {
                    return;
                }
            }
            if ((MainAddTagOverlay != null && MainAddTagOverlay.Visibility == Visibility.Visible) ||
                (MainLanSyncOverlay != null && MainLanSyncOverlay.Visibility == Visibility.Visible))
            {
                return;
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
            FlushPendingSettingsSave();
            LoadSettingsToUI();
            if (SettingsOverlayHost != null) SettingsOverlayHost.Visibility = Visibility.Visible;
            EnsureSettingsInBounds();
        }

        private void BtnToggleSettings_Click(object sender, RoutedEventArgs e)
        {
            if (SettingsOverlayHost == null) return;
            if (SettingsOverlayHost.Visibility == Visibility.Visible)
            {
                FlushPendingSettingsSave();
                SettingsOverlayHost.Visibility = Visibility.Collapsed;
            }
            else
            {
                FlushPendingSettingsSave();
                LoadSettingsToUI();
                SettingsOverlayHost.Visibility = Visibility.Visible;
                EnsureSettingsInBounds();
            }
        }

        private void BtnCloseSettingsOverlay_Click(object sender, RoutedEventArgs e)
        {
            FlushPendingSettingsSave();
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

        private void MainCategoryScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (MainCategoryScrollViewer != null)
            {
                double offset = MainCategoryScrollViewer.HorizontalOffset - (e.Delta * 0.45);
                MainCategoryScrollViewer.ScrollToHorizontalOffset(Math.Max(0, offset));
                e.Handled = true;
            }
        }

        public void RefreshMainCustomTagPills()
        {
            if (MainCustomTagsPanel == null || _dbService == null) return;
            try
            {
                MainCustomTagsPanel.Children.Clear();
                var tags = _dbService.GetCustomTags();
                var loc = LocalizationService.Instance;

                foreach (var tagItem in tags)
                {
                    var btn = new Button
                    {
                        Content = tagItem.DisplayLabel,
                        Tag = tagItem.FilterKey,
                        Margin = new Thickness(0, 0, 4, 0),
                        Padding = new Thickness(9, 4, 9, 4),
                        FontSize = 11.5,
                        Cursor = Cursors.Hand,
                        ToolTip = loc.Get("TagRightClickTip")
                    };
                    var borderStyle = new Style(typeof(Border));
                    borderStyle.Setters.Add(new Setter(Border.CornerRadiusProperty, new CornerRadius(6)));
                    btn.Resources.Add(typeof(Border), borderStyle);

                    btn.Click += TabCategory_Click;
                    btn.MouseRightButtonUp += (s, args) =>
                    {
                        args.Handled = true;
                        var menu = new ContextMenu();
                        if (TryFindResource("ModernContextMenuStyle") is Style ctxStyle) menu.Style = ctxStyle;
                        Style? itemStyle = TryFindResource("ModernMenuItemStyle") as Style;
                        var delMi = new MenuItem
                        {
                            Icon = ThemePalette.CreateMenuGlyph("\uE74D", new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44))),
                            Header = loc.Get("TagDeleteMenu", tagItem.TagName)
                        };
                        if (itemStyle != null) delMi.Style = itemStyle;
                        delMi.Click += (_, _) =>
                        {
                            bool wasSelected = string.Equals(_currentCategory, tagItem.FilterKey, StringComparison.OrdinalIgnoreCase);
                            _dbService.DeleteCustomTag(tagItem.TagName);
                            if (wasSelected) _currentCategory = "all";
                            RefreshMainCustomTagPills();
                            RefreshCards();
                        };
                        menu.Items.Add(delMi);
                        menu.PlacementTarget = btn;
                        menu.Resources = this.Resources;
                        menu.IsOpen = true;
                    };

                    MainCustomTagsPanel.Children.Add(btn);
                }
                UpdateCategoryTabsAppearance();
            }
            catch { }
        }

        private void UpdateCategoryTabsAppearance()
        {
            if (TabAll == null) return;
            var activeBg = TryFindResource("CardHoverBgBrush") as Brush ?? Brushes.Gray;
            var inactiveBg = Brushes.Transparent;
            var activeFg = TryFindResource("TitleTextBrush") as Brush ?? Brushes.White;
            var inactiveFg = TryFindResource("SubTextBrush") as Brush ?? Brushes.Gray;

            UpdateButtonTab(TabAll, _currentCategory == "all", activeBg, inactiveBg, activeFg, inactiveFg);
            UpdateButtonTab(TabText, _currentCategory == "Text", activeBg, inactiveBg, activeFg, inactiveFg);
            UpdateButtonTab(TabImages, _currentCategory == "Image", activeBg, inactiveBg, activeFg, inactiveFg);
            UpdateButtonTab(TabCode, _currentCategory == "Code", activeBg, inactiveBg, activeFg, inactiveFg);
            UpdateButtonTab(TabStarred, _currentCategory == "pinned", activeBg, inactiveBg, activeFg, inactiveFg);

            if (MainCustomTagsPanel != null)
            {
                foreach (var child in MainCustomTagsPanel.Children)
                {
                    if (child is Button customBtn && customBtn.Tag is string filterKey)
                    {
                        bool isActive = string.Equals(_currentCategory, filterKey, StringComparison.OrdinalIgnoreCase);
                        UpdateButtonTab(customBtn, isActive, activeBg, inactiveBg, activeFg, inactiveFg);
                    }
                }
            }
        }

        private void UpdateButtonTab(Button btn, bool isActive, Brush activeBg, Brush inactiveBg, Brush activeFg, Brush inactiveFg)
        {
            if (btn == null) return;
            btn.Background = isActive ? activeBg : inactiveBg;
            btn.Foreground = isActive ? activeFg : inactiveFg;
            btn.BorderBrush = isActive ? (TryFindResource("CardBorderBrush") as Brush ?? Brushes.Transparent) : Brushes.Transparent;
            btn.BorderThickness = new Thickness(1);
            btn.FontWeight = isActive ? FontWeights.Medium : FontWeights.Normal;
        }

        private System.Windows.Threading.DispatcherTimer? _mainSearchDebounceTimer;

        private void SearchInputBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (SearchPlaceholder != null)
            {
                SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchInputBox.Text) 
                    ? Visibility.Visible 
                    : Visibility.Collapsed;
            }

            if (string.IsNullOrEmpty(SearchInputBox.Text))
            {
                _mainSearchDebounceTimer?.Stop();
                RefreshCards();
                return;
            }

            if (_mainSearchDebounceTimer == null)
            {
                _mainSearchDebounceTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(95)
                };
                _mainSearchDebounceTimer.Tick += (s, args) =>
                {
                    _mainSearchDebounceTimer.Stop();
                    RefreshCards();
                };
            }

            _mainSearchDebounceTimer.Stop();
            _mainSearchDebounceTimer.Start();
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
                    TriggerPaste(selected, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
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
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(item.FullImagePath, UriKind.Absolute);
                    bmp.EndInit();
                    bmp.Freeze();
                    _monitorService.NotifyInternalImageWrite(bmp.PixelWidth, bmp.PixelHeight, 2500);
                    Clipboard.SetImage(bmp);
                }
                else
                {
                    var prompts = ClipVault.Helpers.TemplateMacroHelper.ExtractInputPrompts(item.Content);
                    System.Collections.Generic.Dictionary<string, string>? inputValues = null;
                    if (prompts.Count > 0)
                    {
                        inputValues = ClipVault.Views.TemplateInputDialog.PromptForInputs(prompts);
                        if (inputValues == null) return;
                    }

                    string priorClip = string.Empty;
                    try { if (Clipboard.ContainsText()) priorClip = Clipboard.GetText(); } catch { }
                    string expanded = ClipVault.Helpers.TemplateMacroHelper.ExpandMacros(item.Content ?? string.Empty, priorClip, inputValues);
                    Clipboard.SetText(expanded);
                }

                ShowToast(LocalizationService.Instance.Get("ToastCopied"));
                ClipVault.Helpers.SoundEffectHelper.PlayCopyCue(_settingsService?.CurrentSettings);
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

        private void BtnMainToggleMask_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement elem && elem.DataContext is ClipboardItem item)
            {
                item.IsMaskRevealed = !item.IsMaskRevealed;
            }
        }

        private void BtnMainPinScreen_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement elem && elem.DataContext is ClipboardItem item)
            {
                OpenMainPinImage(item);
            }
        }

        private void BtnMainOpenUrl_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement elem && elem.DataContext is ClipboardItem item && item.IsUrl)
            {
                SystemControlService.OpenUri(item.ExtractedUrl);
                ShowToast(LocalizationService.Instance.Get("ToastUrlOpened"));
            }
        }

        private void OpenMainPinImage(ClipboardItem item)
        {
            if (item == null || !item.IsImage || string.IsNullOrWhiteSpace(item.FullImagePath) || !File.Exists(item.FullImagePath)) return;
            var pinWin = new ClipVault.Views.PinImageWindow(item.FullImagePath, _monitorService);
            pinWin.Show();
            ShowToast(LocalizationService.Instance.Get("ToastPinOpened"));
        }

        private void FormatAndCopyMainJson(ClipboardItem item)
        {
            string? formatted = item.TryFormatJson();
            if (string.IsNullOrEmpty(formatted)) return;
            _monitorService.Pause();
            try
            {
                Clipboard.SetText(formatted);
                ShowToast(LocalizationService.Instance.Get("ToastJsonFormatted"));
                ClipVault.Helpers.SoundEffectHelper.PlayCopyCue(_settingsService?.CurrentSettings);
            }
            catch { }
            finally
            {
                _monitorService.Resume();
            }
        }

        private void BtnMainExtractOcr_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement elem && elem.DataContext is ClipboardItem item)
            {
                _ = ExtractAndCopyMainOcrTextAsync(item);
            }
        }

        private async System.Threading.Tasks.Task ExtractAndCopyMainOcrTextAsync(ClipboardItem item)
        {
            if (item == null || !item.IsImage) return;
            var loc = LocalizationService.Instance;
            bool isEn = loc.CurrentLanguage == "en-US";

            try
            {
                string text = item.OcrText ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text) && !string.IsNullOrEmpty(item.FullImagePath) && File.Exists(item.FullImagePath))
                {
                    ShowToast(isEn ? "Recognizing text..." : "正在提取图片文字...");
                    text = await NativeOcrService.RecognizeImageFileAsync(item.FullImagePath);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        item.OcrText = text;
                        _dbService.UpdateItemOcrText(item.Id, text);
                    }
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    _monitorService.Pause();
                    try
                    {
                        Clipboard.SetText(text);
                    }
                    finally
                    {
                        _monitorService.Resume();
                    }
                    ShowToast(loc.Get("OcrCopiedToast", text.Length));
                    ClipVault.Helpers.SoundEffectHelper.PlayCopyCue(_settingsService?.CurrentSettings);
                }
                else
                {
                    ShowToast(isEn ? "No text detected" : "未识别到文字");
                }
            }
            catch (Exception ex)
            {
                ShowToast($"OCR Error: {ex.Message}");
            }
        }

        private void BtnMainLanSync_Click(object sender, RoutedEventArgs e)
        {
            OpenMainLanSyncOverlay();
        }

        private void OpenMainLanSyncOverlay()
        {
            if (MainLanSyncOverlay == null) return;
            var loc = LocalizationService.Instance;
            bool isEn = loc.CurrentLanguage == "en-US";

            if (!_lanSyncService.IsRunning)
            {
                bool ok = _lanSyncService.Start();
                if (!ok)
                {
                    MessageBox.Show(isEn ? "Failed to bind local LAN port." : "局域网快传端口绑定失败，请检查防火墙或更换端口。", isEn ? "LAN Quick Drop Error" : "局域网快传启动失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            string url = _lanSyncService.PairingUrl;
            if (TxtMainLanSyncUrl != null) TxtMainLanSyncUrl.Text = url;
            if (ImgMainLanQrCode != null && !string.IsNullOrWhiteSpace(url))
            {
                ImgMainLanQrCode.Source = ClipVault.Helpers.QrCodeHelper.GenerateQrBitmap(url, 220);
            }
            if (TxtMainLanSyncLiveStatus != null)
            {
                TxtMainLanSyncLiveStatus.Text = isEn
                    ? $"Ready · Port {_lanSyncService.ActivePort}"
                    : $"已就绪 · 端口 {_lanSyncService.ActivePort}";
            }

            UpdateMainLanSyncBtnHighlight();
            MainLanSyncOverlay.Visibility = Visibility.Visible;
        }

        private void BtnMainCopyLanUrl_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_lanSyncService.PairingUrl)) return;
            try
            {
                _monitorService.Pause();
                Clipboard.SetText(_lanSyncService.PairingUrl);
                ShowToast(LocalizationService.Instance.CurrentLanguage == "en-US"
                    ? "URL copied"
                    : "已复制链接");
            }
            catch { }
            finally
            {
                _monitorService.Resume();
            }
        }

        private void BtnMainHideLanSyncOverlay_Click(object sender, RoutedEventArgs e)
        {
            if (MainLanSyncOverlay != null) MainLanSyncOverlay.Visibility = Visibility.Collapsed;
            UpdateMainLanSyncBtnHighlight();
        }

        private void BtnMainStopLanSync_Click(object sender, RoutedEventArgs e)
        {
            _lanSyncService.Stop();
            if (MainLanSyncOverlay != null) MainLanSyncOverlay.Visibility = Visibility.Collapsed;
            UpdateMainLanSyncBtnHighlight();
            ShowToast(LocalizationService.Instance.CurrentLanguage == "en-US"
                ? "LAN Drop stopped"
                : "已停止局域网快传");
        }

        private void UpdateMainLanSyncBtnHighlight()
        {
            if (BtnMainLanSync == null) return;
            if (_lanSyncService != null && _lanSyncService.IsRunning)
            {
                BtnMainLanSync.Foreground = new SolidColorBrush(Color.FromRgb(52, 211, 153));
            }
            else
            {
                BtnMainLanSync.ClearValue(Button.ForegroundProperty);
            }
        }

        private void BtnMainAddTag_Click(object sender, RoutedEventArgs e)
        {
            OpenMainAddTagDialog(null);
        }

        private void OpenMainAddTagDialog(int? assignToItemId = null)
        {
            if (MainAddTagOverlay == null || TxtMainNewTagName == null || TxtMainNewTagIcon == null) return;
            _pendingTagAssignItemId = assignToItemId;
            TxtMainNewTagIcon.Text = "#";
            TxtMainNewTagName.Text = string.Empty;
            if (TxtMainNewTagTemplateContent != null) TxtMainNewTagTemplateContent.Text = string.Empty;
            MainAddTagOverlay.Visibility = Visibility.Visible;
            TxtMainNewTagName.Focus();
        }

        private void BtnMainInsertMacroToken_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string token && TxtMainNewTagTemplateContent != null)
            {
                int caret = TxtMainNewTagTemplateContent.CaretIndex;
                string current = TxtMainNewTagTemplateContent.Text ?? string.Empty;
                caret = Math.Clamp(caret, 0, current.Length);
                TxtMainNewTagTemplateContent.Text = current.Insert(caret, token);
                TxtMainNewTagTemplateContent.CaretIndex = caret + token.Length;
                TxtMainNewTagTemplateContent.Focus();
            }
        }

        private void BtnMainCancelAddTag_Click(object sender, RoutedEventArgs e)
        {
            _pendingTagAssignItemId = null;
            if (MainAddTagOverlay != null) MainAddTagOverlay.Visibility = Visibility.Collapsed;
        }

        private void BtnMainConfirmAddTag_Click(object sender, RoutedEventArgs e)
        {
            ConfirmMainAddCustomTag();
        }

        private void TxtMainNewTagName_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ConfirmMainAddCustomTag();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                _pendingTagAssignItemId = null;
                if (MainAddTagOverlay != null) MainAddTagOverlay.Visibility = Visibility.Collapsed;
                e.Handled = true;
            }
        }

        private void ConfirmMainAddCustomTag()
        {
            if (TxtMainNewTagName == null || TxtMainNewTagIcon == null) return;
            string name = TxtMainNewTagName.Text.Trim();
            string icon = string.IsNullOrWhiteSpace(TxtMainNewTagIcon.Text) ? "#" : TxtMainNewTagIcon.Text.Trim();
            string templateText = TxtMainNewTagTemplateContent?.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                TxtMainNewTagName.Focus();
                return;
            }

            if (_dbService.AddCustomTag(name, icon))
            {
                if (_pendingTagAssignItemId.HasValue)
                {
                    _dbService.SetItemCustomTag(_pendingTagAssignItemId.Value, name);
                }

                if (!string.IsNullOrWhiteSpace(templateText))
                {
                    string summary = templateText.Replace("\r", " ").Replace("\n", " ");
                    if (summary.Length > 50) summary = summary.Substring(0, 50) + "...";

                    using var sha = System.Security.Cryptography.SHA256.Create();
                    byte[] hashBytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes("TPL:" + name + ":" + templateText));
                    string hash = Convert.ToHexString(hashBytes);

                    var tplItem = new ClipboardItem
                    {
                        CreatedAt = DateTime.Now,
                        ItemType = "Text",
                        Content = templateText,
                        Summary = $"{summary} ({templateText.Length} 字符)",
                        SourceApp = "CopyBox Template",
                        Sha256 = hash,
                        CharCount = templateText.Length,
                        IsPinned = true,
                        CustomTag = name
                    };
                    _dbService.InsertItem(tplItem);
                }

                _pendingTagAssignItemId = null;
                if (MainAddTagOverlay != null) MainAddTagOverlay.Visibility = Visibility.Collapsed;
                _currentCategory = "tag:" + name;
                RefreshMainCustomTagPills();
                RefreshCards();
                UpdateStorageInfo();
            }
        }

        private void BtnMainCardAssignTag_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not FrameworkElement elem || elem.DataContext is not ClipboardItem item) return;

            var loc = LocalizationService.Instance;
            var menu = new ContextMenu();
            if (TryFindResource("ModernContextMenuStyle") is Style ctxStyle) menu.Style = ctxStyle;
            Style? itemStyle = TryFindResource("ModernMenuItemStyle") as Style;
            Style? sepStyle = TryFindResource("ModernMenuSeparatorStyle") as Style;
            var secBrush = TryFindResource("TextSecondaryBrush") as Brush;

            var tags = _dbService.GetCustomTags();
            foreach (var tag in tags)
            {
                bool isCurrent = string.Equals(item.CustomTag, tag.TagName, StringComparison.OrdinalIgnoreCase);
                var mi = new MenuItem
                {
                    Icon = ThemePalette.CreateMenuGlyph(isCurrent ? "\uE73E" : "\uE8EC", secBrush),
                    Header = tag.TagName
                };
                if (itemStyle != null) mi.Style = itemStyle;
                mi.Click += (_, _) =>
                {
                    _dbService.SetItemCustomTag(item.Id, tag.TagName);
                    item.CustomTag = tag.TagName;
                    RefreshCards();
                };
                menu.Items.Add(mi);
            }

            if (item.HasCustomTag)
            {
                if (menu.Items.Count > 0)
                {
                    var s1 = new Separator();
                    if (sepStyle != null) s1.Style = sepStyle;
                    menu.Items.Add(s1);
                }
                var rmMi = new MenuItem
                {
                    Icon = ThemePalette.CreateMenuGlyph("\uE711", secBrush),
                    Header = loc.Get("TagRemoveItem", item.CustomTag)
                };
                if (itemStyle != null) rmMi.Style = itemStyle;
                rmMi.Click += (_, _) =>
                {
                    _dbService.SetItemCustomTag(item.Id, string.Empty);
                    item.CustomTag = string.Empty;
                    RefreshCards();
                };
                menu.Items.Add(rmMi);
            }

            if (menu.Items.Count > 0)
            {
                var s2 = new Separator();
                if (sepStyle != null) s2.Style = sepStyle;
                menu.Items.Add(s2);
            }
            var createMi = new MenuItem
            {
                Icon = ThemePalette.CreateMenuGlyph("\uE710", secBrush),
                Header = loc.Get("TagCreateNewMenu")
            };
            if (itemStyle != null) createMi.Style = itemStyle;
            createMi.Click += (_, _) =>
            {
                OpenMainAddTagDialog(item.Id);
            };
            menu.Items.Add(createMi);

            menu.Resources = this.Resources;
            menu.PlacementTarget = elem;
            menu.IsOpen = true;
        }

        private void MainCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject dep)
            {
                var cur = dep;
                while (cur != null && cur is not ListBoxItem)
                {
                    if (cur is Button)
                    {
                        _isMainCardMouseDownForDrag = false;
                        return;
                    }
                    cur = VisualTreeHelper.GetParent(cur);
                }
            }

            if (sender is ListBoxItem lbi && lbi.DataContext is ClipboardItem item)
            {
                _mainCardDragStartPoint = e.GetPosition(this);
                _isMainCardMouseDownForDrag = true;
                _dragCandidateMainCardItem = item;
            }
        }

        private void MainCard_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isMainCardMouseDownForDrag || e.LeftButton != MouseButtonState.Pressed || _dragCandidateMainCardItem == null)
            {
                return;
            }

            Point currentPos = e.GetPosition(this);
            Vector diff = _mainCardDragStartPoint - currentPos;
            if (Math.Abs(diff.X) > 10 || Math.Abs(diff.Y) > 10)
            {
                var item = _dragCandidateMainCardItem;
                _isMainCardMouseDownForDrag = false;
                _dragCandidateMainCardItem = null;

                try
                {
                    var dataObj = new DataObject();
                    if (item.IsImage && !string.IsNullOrEmpty(item.FullImagePath) && File.Exists(item.FullImagePath))
                    {
                        var files = new System.Collections.Specialized.StringCollection { item.FullImagePath };
                        dataObj.SetFileDropList(files);
                        try
                        {
                            var bmp = new BitmapImage(new Uri(item.FullImagePath, UriKind.Absolute));
                            dataObj.SetImage(bmp);
                        }
                        catch { }
                    }
                    else
                    {
                        string priorClip = string.Empty;
                        try { if (Clipboard.ContainsText()) priorClip = Clipboard.GetText(); } catch { }
                        string expanded = ClipVault.Helpers.TemplateMacroHelper.ExpandMacros(item.Content ?? string.Empty, priorClip);
                        dataObj.SetText(expanded, TextDataFormat.UnicodeText);
                        dataObj.SetText(expanded, TextDataFormat.Text);
                    }

                    if ( sender is DependencyObject dragSource)
                    {
                        DragDrop.DoDragDrop(dragSource, dataObj, DragDropEffects.Copy);
                    }
                }
                catch { }
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

            var secBrush = TryFindResource("TextSecondaryBrush") as Brush;
            var dangerBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
            Style? sepStyle = TryFindResource("ModernMenuSeparatorStyle") as Style;

            var pinItem = new MenuItem
            {
                Icon = ThemePalette.CreateMenuGlyph(item.IsPinned ? "\uE77A" : "\uE718", secBrush),
                Header = item.IsPinned ? loc.Get("MenuUnpin") : loc.Get("MenuPin")
            };
            if (itemStyle != null) pinItem.Style = itemStyle;
            pinItem.Click += (s, args) =>
            {
                _dbService.TogglePin(item.Id);
                RefreshCards();
                UpdateStorageInfo();
            };

            var copyItem = new MenuItem
            {
                Icon = ThemePalette.CreateMenuGlyph("\uE8C8", secBrush),
                Header = loc.Get("MenuCopy")
            };
            if (itemStyle != null) copyItem.Style = itemStyle;
            copyItem.Click += (s, args) =>
            {
                CopyItemToClipboard(item);
            };

            var delItem = new MenuItem
            {
                Icon = ThemePalette.CreateMenuGlyph("\uE74D", dangerBrush),
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

            if (!item.IsImage && item.ItemType != "File")
            {
                var plainMi = new MenuItem
                {
                    Icon = ThemePalette.CreateMenuGlyph("\uE8D2", secBrush),
                    Header = loc.Get("MenuPastePlainText")
                };
                if (itemStyle != null) plainMi.Style = itemStyle;
                plainMi.Click += (s, args) => TriggerPaste(item, plainTextOnly: true);
                menu.Items.Add(plainMi);

                if (item.IsUrl)
                {
                    var urlMi = new MenuItem
                    {
                        Icon = ThemePalette.CreateMenuGlyph("\uE8A7", secBrush),
                        Header = loc.Get("MenuOpenUrl")
                    };
                    if (itemStyle != null) urlMi.Style = itemStyle;
                    urlMi.Click += (s, args) =>
                    {
                        SystemControlService.OpenUri(item.ExtractedUrl);
                        ShowToast(loc.Get("ToastUrlOpened"));
                    };
                    menu.Items.Add(urlMi);
                }

                if (item.IsJsonContent)
                {
                    var jsonMi = new MenuItem
                    {
                        Icon = ThemePalette.CreateMenuGlyph("\uE943", secBrush),
                        Header = loc.Get("MenuFormatJson")
                    };
                    if (itemStyle != null) jsonMi.Style = itemStyle;
                    jsonMi.Click += (s, args) => FormatAndCopyMainJson(item);
                    menu.Items.Add(jsonMi);
                }
            }

            if (item.IsImage)
            {
                var pinScreenMi = new MenuItem
                {
                    Icon = ThemePalette.CreateMenuGlyph("\uE840", secBrush),
                    Header = loc.Get("MenuPinScreen")
                };
                if (itemStyle != null) pinScreenMi.Style = itemStyle;
                pinScreenMi.Click += (s, args) => OpenMainPinImage(item);
                menu.Items.Add(pinScreenMi);

                var ocrMi = new MenuItem
                {
                    Icon = ThemePalette.CreateMenuGlyph("\uE8FE", secBrush),
                    Header = loc.Get("OcrExtractBtn")
                };
                if (itemStyle != null) ocrMi.Style = itemStyle;
                ocrMi.Click += (s, args) =>
                {
                    _ = ExtractAndCopyMainOcrTextAsync(item);
                };
                menu.Items.Add(ocrMi);
            }

            foreach (var tag in _dbService.GetCustomTags())
            {
                var tagMi = new MenuItem
                {
                    Icon = ThemePalette.CreateMenuGlyph("\uE8EC", secBrush),
                    Header = loc.Get("TagAssignTo", tag.TagName)
                };
                if (itemStyle != null) tagMi.Style = itemStyle;
                tagMi.Click += (s, args) =>
                {
                    _dbService.SetItemCustomTag(item.Id, tag.TagName);
                    item.CustomTag = tag.TagName;
                    RefreshCards();
                };
                menu.Items.Add(tagMi);
            }

            if (item.HasCustomTag)
            {
                var rmTagMi = new MenuItem
                {
                    Icon = ThemePalette.CreateMenuGlyph("\uE711", secBrush),
                    Header = loc.Get("TagRemoveItem", item.CustomTag)
                };
                if (itemStyle != null) rmTagMi.Style = itemStyle;
                rmTagMi.Click += (s, args) =>
                {
                    _dbService.SetItemCustomTag(item.Id, string.Empty);
                    item.CustomTag = string.Empty;
                    RefreshCards();
                };
                menu.Items.Add(rmTagMi);
            }

            var newTagMi = new MenuItem
            {
                Icon = ThemePalette.CreateMenuGlyph("\uE710", secBrush),
                Header = loc.Get("TagCreateNewMenu")
            };
            if (itemStyle != null) newTagMi.Style = itemStyle;
            newTagMi.Click += (s, args) =>
            {
                OpenMainAddTagDialog(item.Id);
            };
            menu.Items.Add(newTagMi);

            if (item.IsSensitive && ClipboardItem.GlobalAutoMaskEnabled)
            {
                var maskItem = new MenuItem
                {
                    Icon = ThemePalette.CreateMenuGlyph(item.IsMaskRevealed ? "\uED1A" : "\uE7B3", secBrush),
                    Header = item.IsMaskRevealed ? loc.Get("MenuToggleMaskHide") : loc.Get("MenuToggleMaskShow")
                };
                if (itemStyle != null) maskItem.Style = itemStyle;
                maskItem.Click += (s, args) =>
                {
                    item.IsMaskRevealed = !item.IsMaskRevealed;
                };
                menu.Items.Add(maskItem);
            }

            var sep = new Separator();
            if (sepStyle != null) sep.Style = sepStyle;
            menu.Items.Add(sep);
            menu.Items.Add(delItem);
            menu.Resources = this.Resources;

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

        private void UpdateMainStorageLockUiState(bool followAppDir)
        {
            if (BtnBrowseStorage != null)
            {
                BtnBrowseStorage.IsEnabled = !followAppDir;
                BtnBrowseStorage.Opacity = followAppDir ? 0.45 : 1.0;
                BtnBrowseStorage.Cursor = followAppDir ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.Hand;
                BtnBrowseStorage.ToolTip = followAppDir ? LocalizationService.Instance.Get("FollowAppDirLockedTip") : null;
            }
            if (BdMainStoragePath != null)
            {
                BdMainStoragePath.Opacity = followAppDir ? 0.72 : 1.0;
            }
        }

        private void ChkMainFollowAppDir_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInternalSettingUpdate || _settingsService == null || ChkMainFollowAppDir == null) return;
            bool follow = ChkMainFollowAppDir.IsChecked == true;
            bool migrated = _settingsService.SetFollowAppDirectory(follow, true);
            UpdateMainStorageLockUiState(follow);
            TxtSettingsStoragePath.Text = _settingsService.CurrentSettings.StorageDirectory;
            if (migrated)
            {
                RefreshCards();
                UpdateStorageInfo();
                var loc = LocalizationService.Instance;
                MessageBox.Show(loc.Get("StorageMigratedMsg", _settingsService.CurrentSettings.StorageDirectory), loc.Get("StorageMigratedTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnBrowseStorage_Click(object sender, RoutedEventArgs e)
        {
            if (_settingsService == null || _settingsService.CurrentSettings.FollowAppDirectory) return;
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