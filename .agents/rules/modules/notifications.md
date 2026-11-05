# Module: Notifications

Per-user in-app inbox (bell icon), meant to be driven by cross-module integration events with live SignalR push. Module `Order = 750`. Since the Chat module was removed (04/11/2026) no handler writes inbox rows: the only consumers left (Billing's invoice and tenant-expiry events) send e-mail, so the inbox stays empty until a new notification type is added.

**Entities / DbContext:** `Notification` (aggregate: `UserId`, `Type`, `Title`/`Body`/`Link`, `Source`, `MetadataJson`, `ReadAtUtc`). `NotificationsDbContext`. Consumes integration events from other modules (Billing's invoice and tenant-expiry events, which send e-mail).
**Areas:** List, GetUnreadCount, MarkRead, MarkAllRead. Full list: `Features/v1/` or `/scalar`.

## Gotchas

- **It's a consumer.** New notification types come from **handling another module's integration event** (`AddIntegrationEventHandlers`), not from new endpoints. A handler that adds an inbox notification writes the row **and** pushes `"NotificationCreated"` to SignalR group `user:{userId}` via `IHubContext<AppHub>`.
- **Order matters** — Notifications (750) must load before any publisher whose events it consumes. If a new module publishes events Notifications should react to, mind the `Order`.
- In-memory bus runs handlers **synchronously in the publisher's request scope** — keep the handler minimal; an exception surfaces to the originating request. See `eventing.md`.
- Inbox rows are **denormalized** (Title/Body/Link/MetadataJson copied in) so rendering never calls back into the source module. `MarkRead` is idempotent (`ReadAtUtc ??= now`).
