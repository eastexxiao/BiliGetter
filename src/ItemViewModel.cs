// 列表行 ViewModel：包住核心的 DownloadItem，只暴露界面需要的可绑定属性。
using CommunityToolkit.Mvvm.ComponentModel;

namespace BiliGetter
{
    internal partial class ItemViewModel : ObservableObject
    {
        /// <summary>核心模型；下载时直接交给 Downloader。</summary>
        public DownloadItem Item { get; private set; }

        public int Index { get; private set; }
        public string Title { get { return Item == null ? "" : Item.Title; } }
        public string Group { get { return Item == null ? "" : Item.Group; } }
        public string Duration { get { return Item == null ? "" : Item.Duration; } }

        [ObservableProperty] private bool isSelected;
        [ObservableProperty] private string quality = "";
        [ObservableProperty] private string status = "";

        public ItemViewModel(DownloadItem item, int index)
        {
            Item = item;
            Index = index;
            IsSelected = item != null && item.Selected;
        }

        /// <summary>把勾选状态同步回核心模型（Downloader 会读它）。</summary>
        public void SyncSelection()
        {
            if (Item != null) Item.Selected = IsSelected;
        }
    }
}
