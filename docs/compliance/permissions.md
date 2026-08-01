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

Every tenant-scoped endpoint carries an explicit role policy, and the route group
carries Viewer as a floor so one added without a policy of its own still requires
membership at a role rather than merely an authenticated caller.
