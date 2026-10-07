namespace DevX.Domain;

/// <summary>Answers whether a named capability is available in this process.</summary>
public interface IFeatureProvider
{
    bool IsEnabled(string feature);
}

/// <summary>Enables core capabilities and denies all other feature names.</summary>
public sealed class DefaultFeatureProvider : IFeatureProvider
{
    public bool IsEnabled(string feature) =>
        feature is not null && feature.StartsWith("core.", StringComparison.Ordinal);
}

/// <summary>Composition root hook for a build that supplies its own feature policy.</summary>
public static class FeatureProviderRegistry
{
    private static IFeatureProvider _current = new DefaultFeatureProvider();

    public static IFeatureProvider Current
    {
        get => _current;
        set => _current = value ?? throw new ArgumentNullException(nameof(value));
    }
}
