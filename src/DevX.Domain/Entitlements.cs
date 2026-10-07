namespace DevX.Domain;

/// <summary>Answers whether a named capability is available in this process.</summary>
public interface IEntitlementProvider
{
    bool IsEnabled(string entitlement);
}

/// <summary>Enables core capabilities and denies all other entitlement names.</summary>
public sealed class DefaultEntitlementProvider : IEntitlementProvider
{
    public bool IsEnabled(string entitlement) =>
        entitlement is not null && entitlement.StartsWith("core.", StringComparison.Ordinal);
}

/// <summary>Composition root hook for a build that supplies its own entitlement policy.</summary>
public static class EntitlementProviderRegistry
{
    private static IEntitlementProvider _current = new DefaultEntitlementProvider();

    public static IEntitlementProvider Current
    {
        get => _current;
        set => _current = value ?? throw new ArgumentNullException(nameof(value));
    }
}
