using Microsoft.Extensions.DependencyInjection;

namespace Vendomat.Controller.Tablet;

public static class ServiceRegistry
{
    private static IServiceProvider? _serviceProvider;

    public static void Initialize(IServiceProvider serviceProvider) => _serviceProvider = serviceProvider;

    public static T GetRequiredService<T>() where T : notnull
    {
        if (_serviceProvider is null)
        {
            throw new InvalidOperationException("Service provider is not initialized.");
        }

        return _serviceProvider.GetRequiredService<T>();
    }
}
