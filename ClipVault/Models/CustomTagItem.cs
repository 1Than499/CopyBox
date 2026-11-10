using System;

namespace ClipVault.Models
{
    /// <summary>
    /// v3.0 用户自定义分类标签实体
    /// </summary>
    public class CustomTagItem
    {
        public int Id { get; set; }
        public string TagName { get; set; } = string.Empty;
        public string TagIcon { get; set; } = "🏷️";
        public int SortOrder { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public string DisplayLabel => string.IsNullOrWhiteSpace(TagIcon)
            ? TagName
            : $"{TagIcon} {TagName}";

        public string FilterKey => $"tag:{TagName}";
    }
}
