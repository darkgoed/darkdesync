# Issue #30 — gap/deadline and explicit unavailability

Base/rollback release .10: ee64c3e407ae21d081256e4bbc0f996d6494c704. PR https://github.com/darkgoed/darkdesync/pull/32. Single existing parent, no agents/accounts/projects switched.

## Scope and independent feasibility checks

Only two newly actionable modes, append-only IDs 36/37: Inter-Packet Gap Equalization and Deadline-Aware Scheduling. Eleven choices total = four favorites + five #28 techniques + two schedulers. Eight rejected modes and Packet Size-Aware remain unavailable. Defaults, presets, explicit settings saves, GLOBAL warning/arming/manual pulse and STOP preserved. Three unsupported techniques shown in separate noninteractive text with requirements, not selectable effects or fake enums.

UDMUX: searches for official Roblox UDMUX channel wire-schema/proof returned forum reports, log parsers and third-party packet excerpts, not a verified externally distinguishable channel format. Examined result categories include Roblox DevForum and https://github.com/Froststrap/Froststrap/commit/38517aa502ae03a06ae207da85dbad113275f333 (address log parser, not channel proof). Neither UDP tuple, packet size, direction nor an endpoint log proves channel semantics. No owner-controlled Roblox capture set, labelled channel ground truth or authoritative schema is available here. Status INCONCLUSIVE/NOT VERIFIED, not a claim that external channels can never exist. No protected-protocol reverse engineering, keys, game scripts, injection or anticheat interaction undertaken; no heuristic channel classifier shipped.

Multipath: bundled official WinDivert 2.2.2 documentation `doc/WinDivert.html` lines 760–762 and 1620–1621 explicitly state outbound IfIdx/SubIfIdx ignored. Online fetch https://reqrypt.org/windivert-doc.html was blocked by retrieval's private-address safety check; local official bundled documentation used instead. A read-only Windows test records adapter types/status/gateway address families, without addresses or credentials. Adapter count is not proof of distinct paths. No two controlled Windows routes/gateways plus validation receiver supplied; no steering implemented, no interface-bit counterfeit, no route mutations.

NAT: no controlled gateway/NAT + external receiving validator/topology supplied. Loopback source-port change cannot prove WAN mapping rebinding. No rebinding implementation exposed; no NAT/firewall/interface persistence changed. Requires independently measured before/after external IP:port plus safe session/rollback in own topology.

These unavailability outcomes satisfy the explicit #30 fallback; they are not three implemented effects. Cancelled Roblox ownership/prediction/input/path experiments are not backlog or implementation.

## Scheduler implementation / inspection

TemporalPulseQueue owns bounded per-flow FIFO lanes under existing packet lock, no worker/timer per packet. TCP/UDP exact unfragmented IPv4 (options allowed)/IPv6 without extensions <=4096 IP bytes. ICMP, fragments, extension headers/impostors/inbound pass. Direction/address/payload/checksums unchanged. Protocol/filter not rewritten. At most 128 lanes/512 packets/768 KiB; overload cancels and drains fail-open, including current original packet once. Lanes retained only for <=800 ms pulse, preserving prior release/arrival metadata; drained/discarded at expiry/STOP. Monotonic input clamp; no future-packet dependency.

Gap: arrival-gap EMA (1/8 sample, 7/8 prior), bounded 6–24 ms, initial 8. Next release uses last ACTUAL dequeue time, not a precomputed constant byte bucket. Minimum 2 ms residence, maximum age 96 ms/pulse deadline. Same-flow FIFO; round-robin among eligible flows. TTL/STOP may compress intervals deliberately for safe delivery. Equal-sized and variable-sized traces compared; deterministic constant burst has 6 ms intervals independent of bytes, unlike fixed delay/byte shaping.

Deadline: earliest absolute deadline among FIFO heads, ties round-robin. Age budget 16 ms for IP<=256 bytes /48 ms otherwise; same-flow later short packet cannot overtake earlier large packet and may inherit its deadline (age still <=48 ms). Backlog reserves min(24 ms,(count−1)*2 ms) of service time and starts earlier under load; singleton behavior differs from backlog. Minimum residence 2 ms unless pulse deadline sooner. Exhaustive queue release checks at deadline/overload and no-future tests included. Windows scheduling is not hard real-time; bounds describe scheduler eligibility, not OS wakeup guarantee.

Inspected PacketEngine pulse/expiry/release/flush/STOP lifecycle, checksum-preserving shared parser path, settings/catalog, neutral rating fallback, compact XAML and WPF selection/keyboard/state tests. Standard/spec review kept in single parent per owner request. Existing lifecycle locks, worker and native send path reused; no dependency or CI semantics changes.

## Verification contract

Deterministic tests: exact gap trace, size independence vs byte shaper, gap/deadline distinction, earliest deadline/age/backlog policy, FIFO, round-robin fairness, 128-lane/512-packet/byte bounds, backward clock clamp, no-future timeout, pulse deadline shorter than min hold, 0/800 ms activation, mixed IPv4/IPv6 TCP/UDP/ICMP, exact packet bytes/address timestamp, STOP/restart/no stale, overflow fail-open, save/load unchanged bytes and no fake modes.

Native isolated Windows: own UDP IPv4/IPv6 sockets, before(+100)/after(−1000) WinDivert sniff with production priority0 literal outbound. Baseline/effect inter-gap histograms, actual pre/post kernel-QPC delay, exact FIFO payload/header checks, STOP/restart and sentinel. Additional real TCP IPv4/IPv6 streams during pulse and after STOP. Read-only topology inventory is only inventory, not multipath/NAT proof. WPF UIA iterates all eleven real choices, native ARMADO/PULSO/STOP; negative driver failures/regressions unchanged. Final main source and parsed TRX counts belong in release evidence after CI, not assumed here.

## Uninspected / risks

No owner's PC, physical key, real game/anticheat, WAN paths, ECN response/QoS priority, external NAT mapping, authenticated UDMUX channel identity or native routing. Timing effects can worsen application latency/retransmission. No benefit/exploit promise. Windows/driver/kernel can miss scheduling targets and fail sends; safety drain does not promise zero loss. No Semgrep/Jev approval claimed where tools unavailable. All public claims limited to actual own-loopback Windows evidence and deterministic scheduling; final release requires exact main SHA green, ZIP inspection, rollback and published-download hashes.
