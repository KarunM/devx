using DevX.Domain;
using Xunit;

namespace DevX.Tests.Domain;

public sealed class EntitlementProviderTests
{
    [Fact]
    public void DefaultProvider_EnablesCoreEntitlements()
    {
        var provider = new DefaultEntitlementProvider();
        Assert.True(provider.IsEnabled("core.index"));
        Assert.True(provider.IsEnabled("core.search"));
        Assert.True(provider.IsEnabled("core.anything"));
    }

    [Fact]
    public void DefaultProvider_DeniesOtherOrDifferentlyCasedNames()
    {
        var provider = new DefaultEntitlementProvider();
        Assert.False(provider.IsEnabled("enterprise.identity"));
        Assert.False(provider.IsEnabled("unknown.feature"));
        Assert.False(provider.IsEnabled(""));
        Assert.False(provider.IsEnabled("CORE.index"));
        Assert.False(provider.IsEnabled("Core.index"));
    }

    [Fact]
    public void Registry_AllowsCustomProviderRegistration()
    {
        var original = EntitlementProviderRegistry.Current;
        var custom = new TestEntitlementProvider();
        try
        {
            EntitlementProviderRegistry.Current = custom;
            Assert.Same(custom, EntitlementProviderRegistry.Current);
            Assert.True(EntitlementProviderRegistry.Current.IsEnabled("enterprise.identity"));
        }
        finally
        {
            EntitlementProviderRegistry.Current = original;
        }
    }

    private sealed class TestEntitlementProvider : IEntitlementProvider
    {
        public bool IsEnabled(string entitlement) => entitlement == "enterprise.identity";
    }
}
