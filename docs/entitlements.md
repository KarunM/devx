# Entitlements

Entitlements mark the boundary between DevX's always available local capabilities and capabilities that a separate build may govern.

`IEntitlementProvider.IsEnabled(string)` answers whether a named entitlement is available. Names are case-sensitive. The default provider enables every `core.*` name and denies `enterprise.*`, unknown names, and differently cased names. It performs no network or licence checks.

A composition root can register a provider by assigning `EntitlementProviderRegistry.Current`. The default remains active unless a host explicitly replaces it. Entitlement checks do not establish identity, membership, or authorization.

Core capabilities remain available offline without authentication or a subscription. This repository contains no enterprise implementation.
