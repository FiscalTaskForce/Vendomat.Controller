using System.Text.Json;
using SQLite;
using Vendomat.Controller.Domain.Models;

namespace Vendomat.Controller.Tablet.Persistence.Entities;

[Table("advertisements")]
public sealed class AdvertisementAssetEntity
{
    [PrimaryKey]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string Badge { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public int SortOrder { get; set; }

    public AdvertisementAsset ToDomain() => new()
    {
        Id = Guid.Parse(Id),
        Title = Title,
        Subtitle = Subtitle,
        Badge = Badge,
        LocalPath = LocalPath,
        IsEnabled = IsEnabled,
        SortOrder = SortOrder,
    };

    public static AdvertisementAssetEntity FromDomain(AdvertisementAsset asset) => new()
    {
        Id = asset.Id.ToString("N"),
        Title = asset.Title,
        Subtitle = asset.Subtitle,
        Badge = asset.Badge,
        LocalPath = asset.LocalPath,
        IsEnabled = asset.IsEnabled,
        SortOrder = asset.SortOrder,
    };
}
