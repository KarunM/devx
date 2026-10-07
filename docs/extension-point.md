# Feature extension point

The feature provider is a small composition hook for deciding whether named capabilities are enabled in the current process. The default implementation enables every `core.*` key and denies all other keys. Names are case-sensitive.

## Contract

`IFeatureProvider.IsEnabled(string)` returns whether a named feature is enabled. The default provider performs no network, licence, identity, membership, or authorization checks.

## Registration

A composition root can register a provider by assigning `FeatureProviderRegistry.Current`. The default provider remains active unless a host explicitly replaces it. The registry rejects a null provider.

## Guarantees

All `core.*` keys are enabled by the default provider. Unknown keys and differently cased keys return false. The extension point does not change public CLI commands or JSON keys.
