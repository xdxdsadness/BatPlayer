using System;

namespace BatPlayer;

/// <summary>
/// Extension methods on IServiceProvider for resolving required services.
/// Avoids taking a dependency on Microsoft.Extensions.DependencyInjection.
/// </summary>
public static class ServiceProviderExtensions
{
    public static T GetRequiredService<T>(this IServiceProvider sp) where T : class
        => (T)(sp.GetService(typeof(T)) ?? throw new InvalidOperationException(
            $"Service of type {typeof(T).Name} is not registered."));
}
