# 0036. Bind Genie conversation ownership to the tenant

Status: accepted

Date: 2026-11-28

## Context

ADR 0032 records which application principal owns a Genie conversation. The record held the
conversation id and the owner key, and nothing else. Every operation on an existing conversation
checked the owner, then resolved the Genie space from the `TenantContext` the caller supplied at
that moment. Nothing tied the conversation to the tenant that started it.

A host can use the same owner key in two tenants, for example the user's subject for a person who
belongs to both. That person could start a conversation in tenant A, then continue, read or delete
it while tenant B was resolved. The library would then address tenant B's space with tenant A's
conversation id. Whether Databricks refuses that request was not verified, and
`ValidateResponseIdentity` rejects only identity fields that are present and different. The only
control was a remark asking hosts to make owner keys unique across tenants.

## Decision

The built-in stores record the tenant with the owner.

- `IConversationOwnership` gains `RecordAsync(TenantId, ...)` and `IsOwnerAsync(TenantId, ...)`.
  `GenieConversations` calls only these. A conversation started in one tenant is refused, before
  any credential or HTTP call, when another tenant is resolved.
- The in-memory store keeps the tenant beside the owner. The Redis store appends a hash of the
  tenant to its claim value, and compares each part in fixed time.
- The new members are default interface members that forward to the tenant-unaware ones. A custom
  store that does not override them compiles and behaves as before, without the binding. This keeps
  3.x source- and binary-compatible, which package validation against 3.0.0 enforces.
- A claim recorded before this change has no tenant. It is still accepted for its owner in any
  tenant, so existing durable conversations keep working after an upgrade. These claims are only
  as safe as the host's owner keys, as they were before.
- Conversations in the acknowledged shared space (`SharedSpaceId` with
  `AcknowledgeNoTenantIsolation`) are bound to one reserved tenant, `TenantId(Guid.Empty)`, not to
  the caller's. Every tenant there addresses the same space and the host has accepted that there is
  no tenant isolation, so a staff tool can still continue a conversation from another tenant's
  context. Conversations do not cross between the shared space and a tenant's own space in either
  direction. A custom store must treat the reserved tenant as an ordinary tenant, and options
  validation rejects a `Spaces` entry for `Guid.Empty` when shared mode is on.
- `ListAsync` still lists an owner's conversation ids across tenants. It returns identifiers only,
  and every operation on one of them now checks the tenant.

## Consequences

- The binding is a library control, not a replacement for tenant-specific owner keys. `ListAsync`
  has no tenant, and records written before this change and custom stores that do not override the
  new members are unbound, so the guide still says to build an owner key from the tenant and the
  principal.
- A custom `IConversationOwnership` must override the two tenant-aware members to get the binding.
- The next major version makes the tenant-aware members the contract and removes the
  tenant-unaware ones. Hosts that need every conversation bound before then can delete
  conversations started before the upgrade.
