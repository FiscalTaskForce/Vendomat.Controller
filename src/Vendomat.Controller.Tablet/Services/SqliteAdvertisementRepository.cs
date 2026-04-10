using Vendomat.Controller.Application.Interfaces;
using Vendomat.Controller.Domain.Models;
using Vendomat.Controller.Tablet.Persistence.Entities;

namespace Vendomat.Controller.Tablet.Services;

public sealed class SqliteAdvertisementRepository(LocalDatabaseService database) : IAdvertisementRepository
{
    public async Task<IReadOnlyList<AdvertisementAsset>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync();
        var rows = await database.Connection.Table<AdvertisementAssetEntity>()
            .OrderBy(x => x.SortOrder)
            .ToListAsync();

        if (rows.Count > 0)
        {
            return rows.Select(x => x.ToDomain()).ToList();
        }

        var defaults = new List<AdvertisementAsset>
        {
            new() { Title = "Lapte crud rece", Subtitle = "Panoul de sus poate afișa imagini sau reclame setate de operator.", Badge = "Ecran promo", SortOrder = 1 },
            new() { Title = "Curățare și trasabilitate", Subtitle = "Fiecare igienizare și fiecare vânzare sunt păstrate local pe tabletă.", Badge = "Audit local", SortOrder = 2 },
            new() { Title = "Control local robust", Subtitle = "Mașina rulează local și sincronizează compact, ca să consume puține date GSM.", Badge = "Offline first", SortOrder = 3 },
        };

        await SaveAsync(defaults, cancellationToken);
        return defaults;
    }

    public async Task SaveAsync(IReadOnlyCollection<AdvertisementAsset> assets, CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync();
        var rows = assets.Select(AdvertisementAssetEntity.FromDomain).ToList();
        await database.Connection.DeleteAllAsync<AdvertisementAssetEntity>();
        await database.Connection.InsertAllAsync(rows);
    }
}
