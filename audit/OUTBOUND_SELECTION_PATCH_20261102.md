# Outbound selection patch — 2026-11-02

## Cause and scope

`SelectVerifiedScopeForScopedProfile` changed GLOBAL + PID to ProcessPorts on target/effect/START; removed. `ReconcileScope` replaced rather than intersected the selected direction; fixed. Explicit PID Outbound remains Outbound and retains exact tuple authorization; inbound/duplex combinations now fail visibly instead of silently capturing another direction.

GLOBAL Packet Size-Aware is implemented using the existing bounded scheduler, native literal outbound capture and explicit session-only GLOBAL UDP IPv4 consent. It does not require PID tuples. As of patch .4, disjoint TCP/IPv6/ICMP traffic passes through without cancelling UDP pacing; malformed IPv4 UDP, short unidentified packets and impostors still cancel and drain safely. Global reordering remains forbidden. There is no inbound-under-outbound claim and no extra effects. Follow-up: [mixed traffic audit](GLOBAL_MIXED_TRAFFIC_PATCH_20261102.md).

## Review (single executor, no duplicate agents)

- Four approved favorites and serialized enum/settings values unchanged; existing GLOBAL paths retained.
- New GLOBAL permission is separate from experimental consent and not serialized. Filter/effect/settings changes revoke it; revocation drains under the packet lock. Engine checks consent at arm, trigger and capture.
- No scheduler changes: FIFO per flow, <=512 packets / 786432 bytes / 128 historical flows / 1536 bytes per packet, deadline + watchdog, fail-open, synchronous STOP drain and epoch retirement. No replay, intentional drops, TCP reorder or anti-cheat work.
- Inbound and duplex require compatible PID direction. Custom expressions intersect, never widen, tuples/direction; a custom expression excluding the effect direction can yield no effect (documented).
- New deterministic 9 x 5 selection/settings matrix, GLOBAL scheduling/consent tests, WPF consent/status and preserved-selection checks. Real Windows driver case measures different small/large UDP interarrival pacing without PID, FIFO, 200ms expiry, STOP and no duplicates; PID cases exercise explicitly selected Outbound/AllTraffic, FLOW -> NETWORK and existing isolation/kill/epoch cases.

## Validation and release gates

Linux cross-build of application, tests and WPF smoke project passed. One existing xUnit2012 warning in MinimalEffectsTests remains unchanged. Windows execution is required: PR CI plus workflow_dispatch with driver_tests=true and legacy_global_tests=true; merge only green, then repeat on main SHA. Release evidence must identify those runs, TRX totals, SOURCE_COMMIT, payload and ZIP hashes. Do not interpret build success as Windows execution success.

Rollback: keep prior Latest v2026.11.02-five-inbound-experimental.2 and all assets intact; reselect its archive if needed. No DB, host runtime or unrelated project changes. Tests generate only owned loopback UDP/TCP on an isolated Windows runner. The GLOBAL suite can intercept ambient outbound on that ephemeral VM, which requires the separate workflow consent. Production GLOBAL affects other applications. No Roblox Player, server-side effect or anti-cheat claim.
