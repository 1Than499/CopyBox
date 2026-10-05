using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClipVault.Models;
using ClipVault.Services;

namespace ClipVault.Views
{
    public partial class QuickPasteWindow : Window
    {
        private readonly DatabaseService _dbService;
        private readonly PasteSimulator _pasteSimulator;
        private string _currentCategory = "all";
        public ObservableCollection<ClipboardItem> DisplayItems { get; } = new();

        public QuickPasteWindow(DatabaseService dbService, PasteSimulator pasteSimulator)
        {
            InitializeComponent();
            _dbService = dbService;
            _pasteSimulator = pasteSimulator;

            ItemsListBox.ItemsSource = DisplayItems;

            // 失去焦点时自动隐藏
            this.Deactivated += (s, e) => this.Hide();
        }

        public void ShowAndRefresh()
        {
            // 记录当前活跃窗口（用于选定后自动贴入该窗口）
            _pasteSimulator.RecordTargetWindow();

            SearchBox.Text = string.Empty;
            RefreshItems();

            this.Show();
            this.Activate();
            SearchBox.Focus();
        }

        public void RefreshItems()
        {
            DisplayItems.Clear();
            string keyword = SearchBox.Text.Trim();
            var items = _dbService.GetRecentItems(keyword, _currentCategory, 60);

            for (int i = 0; i < items.Count; i++)
            {
                if (i < 9)
                {
                    items[i].DisplayBadge = (i + 1).ToString();
                }
                DisplayItems.Add(items[i]);
            }

            if (DisplayItems.Count > 0)
            {
                ItemsListBox.SelectedIndex = 0;
            }
        }

        private async void TriggerPaste(ClipboardItem item)
        {
            if (item == null) return;
            await _pasteSimulator.PasteItemAsync(item, () => this.Hide());
        }

        private void Category_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string cat)
            {
                _currentCategory = cat;

                // 更新按钮高亮样式
                var activeBrush = new SolidColorBrush(Color.FromRgb(0, 120, 212));
                var inactiveBrush = new SolidColorBrush(Color.FromArgb(34, 255, 255, 255));

                BtnCatAll.Background = cat == "all" ? activeBrush : inactiveBrush;
                BtnCatText.Background = cat == "Text" ? activeBrush : inactiveBrush;
                BtnCatCode.Background = cat == "Code" ? activeBrush : inactiveBrush;
                BtnCatImage.Background = cat == "Image" ? activeBrush : inactiveBrush;
                BtnCatPinned.Background = cat == "pinned" ? activeBrush : inactiveBrush;

                RefreshItems();
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            RefreshItems();
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            // Esc 快速隐藏
            if (e.Key == Key.Escape)
            {
                this.Hide();
                e.Handled = true;
                return;
            }

            // Enter 贴入当前选中的第一项
            if (e.Key == Key.Enter)
            {
                if (ItemsListBox.SelectedItem is ClipboardItem selected)
                {
                    TriggerPaste(selected);
                }
                e.Handled = true;
                return;
            }

            // 方向键向下移动焦点到列表
            if (e.Key == Key.Down)
            {
                if (ItemsListBox.SelectedIndex < DisplayItems.Count - 1)
                {
                    ItemsListBox.SelectedIndex++;
                    ItemsListBox.ScrollIntoView(ItemsListBox.SelectedItem);
                }
                e.Handled = true;
                return;
            }

            // 方向键向上移动焦点
            if (e.Key == Key.Up)
            {
                if (ItemsListBox.SelectedIndex > 0)
                {
                    ItemsListBox.SelectedIndex--;
                    ItemsListBox.ScrollIntoView(ItemsListBox.SelectedItem);
                }
                e.Handled = true;
                return;
            }

            // 如果输入的是纯数字 1~9 且当前搜索框为空，直接按数字秒贴对应项
            if (string.IsNullOrEmpty(SearchBox.Text))
            {
                int index = -1;
                if (e.Key >= Key.D1 && e.Key <= Key.D9) index = e.Key - Key.D1;
                else if (e.Key >= Key.NumPad1 && e.Key <= Key.NumPad9) index = e.Key - Key.NumPad1;

                if (index >= 0 && index < DisplayItems.Count)
                {
                    TriggerPaste(DisplayItems[index]);
                    e.Handled = true;
                }
            }
        }

        private void ItemsListBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                this.Hide();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && ItemsListBox.SelectedItem is ClipboardItem selected)
            {
                TriggerPaste(selected);
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && ItemsListBox.SelectedItem is ClipboardItem toDelete)
            {
                _dbService.DeleteItem(toDelete.Id);
                RefreshItems();
                e.Handled = true;
            }
            else if (e.Key == Key.P && ItemsListBox.SelectedItem is ClipboardItem toPin)
            {
                _dbService.TogglePin(toPin.Id);
                RefreshItems();
                e.Handled = true;
            }
        }

        private void ItemsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ItemsListBox.SelectedItem is ClipboardItem selected)
            {
                TriggerPaste(selected);
            }
        }

        private void PinButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int id)
            {
                _dbService.TogglePin(id);
                RefreshItems();
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int id)
            {
                _dbService.DeleteItem(id);
                RefreshItems();
            }
        }
    }
}
