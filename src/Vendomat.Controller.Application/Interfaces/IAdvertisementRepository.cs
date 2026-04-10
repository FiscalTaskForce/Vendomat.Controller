using Vendomat.Controller.Domain.Models;

namespace Vendomat.Controller.Application.Interfaces;

public interface IAdvertisementRepository
{
    Task<IReadOnlyList<AdvertisementAsset>> GetAllAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(IReadOnlyCollection<AdvertisementAsset> assets, CancellationToken cancellationToken = default);
}
