# Permission matrix

Generated from the routing table by `PermissionMatrixTests`. Do not edit by hand:
the test rewrites this file and fails when it drifts from the code.

Roles are a floor, not an exact match. An Admin satisfies a Member policy, and a
Member satisfies a Viewer policy. Every endpoint below also requires a resolved tenant,
which means membership of that organization; a caller who is not a member gets 404
before authorization is consulted.

| Method | Route | Minimum role |
|---|---|---|
| POST | `/organizations/{organizationId}/operations/` | Member |
| GET | `/organizations/{organizationId}/operations/{operationId:guid}` | Viewer |

Endpoints with no explicit policy fall back to requiring an authenticated user, so a
new endpoint is protected by omission rather than exposed by it.
