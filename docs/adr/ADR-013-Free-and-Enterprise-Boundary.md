# ADR-013: DevX Free and Enterprise boundary

Status: Accepted
Date: 2026-10-07

## Context

DevX needs a commercial offering while preserving a complete local engineering workflow. Organization reporting currently accepts caller-supplied event exports and cannot establish authenticated membership, authorization, or trusted organization data.

## Decision

DevX Free is the complete engineering tool for one developer. DevX Enterprise makes that same engineering system safe, shared, observable, and controllable across an organization.

Core entitlements are always available offline and without authentication. Enterprise entitlements are enforced centrally at service boundaries.

Free includes unlimited local repositories, indexing, search, source reads, patches, validation/testing, local agents and shared sessions, local agent handoffs, local MCP, and local metrics/reporting. Local engineering work must not depend on login, subscription checks, a licensing server, or network access. User-selected remote services may require their own connectivity and credentials; that does not introduce a DevX licensing requirement for core operations.

Enterprise v1 covers organization accounts, OIDC/SSO, RBAC, shared remote indexing, audit trails, central policy, team analytics, GitHub/GitLab integration, and self-hosted/private deployment. Enterprise manages organization-wide sessions, permissions, locking, retention, review queues, and governance; local sessions and handoffs remain Free.

A central entitlement abstraction will define the commercial boundary in one codebase. Core capabilities are unconditional. Enterprise checks belong at service entry points, alongside organization identity and authorization, rather than scattered through local implementations. Entitlement possession does not replace RBAC or establish organization membership.

Initial entitlement names:

```text
core.index
core.search
core.read
core.validate
core.agents
core.metrics

enterprise.identity
enterprise.shared_index
enterprise.policy
enterprise.audit
enterprise.analytics
enterprise.deployment
```

## Organization report status

The organization HTML report is preview-only. Caller-supplied exports demonstrate aggregation and presentation but are not authoritative enterprise reporting. The report must disclose that identity, attribution, and input provenance are unverified and that governance guarantees are unavailable.

Authoritative organization reporting requires authenticated organization identity, RBAC-controlled collection and export, and trusted organization-level data. Local developer reports remain Free. Missing evidence must be represented as unavailable rather than as zero violations, proven savings, or inferred quality outcomes.

## Implementation sequence

1. Central entitlement framework.
2. Organization identity, OIDC, and RBAC.
3. Shared remote index.
4. Audit and central policy.
5. Integrations and analytics.
6. Deployment hardening.

The shared index supplies the first major shared technical value; audit and policy provide the governance around it.

## Deferred scope

SCIM, SIEM export, air-gapped licensing, sophisticated cost allocation, long-term support channels, complex concurrency controls, and complex billing are deferred until concrete customer requirements justify them.

## Consequences

Free remains useful independently of the Enterprise control plane. Loss of Enterprise authentication or entitlement cannot disable core operations. Enterprise failures deny managed service access without affecting local engineering capabilities.

This is the accepted architecture direction, not a statement that all listed capabilities are implemented. Subsequent changes must preserve this boundary or explicitly supersede this decision.
