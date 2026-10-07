using DevX.Domain;
using Xunit;

namespace DevX.Tests.Domain;

public sealed class FeatureProviderTests
{
    [Fact]
    public void DefaultProvider_EnablesCoreFeatures()
    {
        var provider = new DefaultFeatureProvider();
        Assert.True(provider.IsEnabled("core.index"));
        Assert.True(provider.IsEnabled("core.search"));
        Assert.True(provider.IsEnabled("core.anything"));
    }

    [Fact]
    public void DefaultProvider_DeniesOtherOrDifferentlyCasedNames()
    {
        var provider = new DefaultFeatureProvider();
        Assert.False(provider.IsEnabled("other.feature"));
        Assert.False(provider.IsEnabled("unknown.feature"));
        Assert.False(provider.IsEnabled(""));
        Assert.False(provider.IsEnabled("CORE.index"));
        Assert.False(provider.IsEnabled("Core.index"));
    }

    [Fact]
    public void Registry_AllowsCustomProviderRegistration()
    {
        var original = FeatureProviderRegistry.Current;
        var custom = new TestFeatureProvider();
        try
        {
            FeatureProviderRegistry.Current = custom;
            Assert.Same(custom, FeatureProviderRegistry.Current);
            Assert.True(FeatureProviderRegistry.Current.IsEnabled("other.feature"));
        }
        finally
        {
            FeatureProviderRegistry.Current = original;
        }
    }

    private sealed class TestFeatureProvider : IFeatureProvider
    {
        public bool IsEnabled(string feature) => feature == "other.feature";
    }
}
