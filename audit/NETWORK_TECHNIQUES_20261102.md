# Issue #28 — five real network techniques

Base / rollback: release .9, main 760ca4706ca67c6aa763e9f618c2cb85ac90bf55. PR https://github.com/darkgoed/darkdesync/pull/31. Publication evidence follows only after final main gates/download verification; this document is not a release-success claim.

## Inspected

PacketEngine capture epoch, packet/handle/lifecycle locks, arming, manual pulse generation, monotonic expiry, release worker, cancellation, STOP/drain/dispose; WinDivert address bit layout and documented priority/reinjection semantics; parser, checksum writer, ACK scheduler, settings and compact WPF bindings; existing favorites and retired settings contracts. No workflow gates or semantics changed. Single existing agent, project-only scope.

Production has nine selectable options, append-only IDs 31–35 plus four favorites. Eight rejected IDs remain legacy-only. Five new options have neutral subjective assessments. Default 200 ms, two primary styles 250/800 ms, manual 0–800 ms. START only captures/pass-through, pulse explicitly triggers, STOP drains with live handle, then joins workers/closes. No PID/filter/protocol suppression. Effect changes do not mutate current pulse DSCP.

## Techniques and measurable limits

- Duplication: one immediate extra copy of eligible UDP IPv4/IPv6 <=1400 IP bytes after successful original send; max 16 copies/32 KiB per pulse; no delayed duplicate queue or TCP duplication. Impostors pass, production priority prevents self-recursion. Counts successful copies. Optional-copy send failure requests safety stop without resending original.
- ECN: ECT(0)/ECT(1) to CE; Not-ECT and CE unchanged. No transport congestion-response claim.
- DSCP: configured 0–63 (default 8), ECN preserved; value frozen for pulse. No routing/QoS priority claim.
- Marking parser: exact-length unfragmented IPv4 including options or IPv6 without extension headers; TCP/UDP <=4096 IP bytes. Unsupported/ICMP/fragments/extensions pass. Payload, flow label, tuple preserved; software IPv4/TCP/UDP checksums repaired and validity flags updated, not interface/direction/timestamp.
- MTU: read-only actual Windows loopback MTU (NetworkInterface / GetIfEntry fallback), eight own IPv4 UDP probes, DF requested on/off and exact payload verification. Cancellation bounded by pulse and joined at STOP. Partial report possible for short pulse or STOP. No persistent network changes or IPv6 PMTUD change. Local maximum delivery is only a lower bound; fragmentation/reassembly and WAN PMTU are explicitly NOT proven. Never emits crafted global fragments.
- ACK pacing: only pure TCP ACK, not piggyback/SYN/FIN/RST/PSH/ECN-control ACK. Per-flow FIFO, target first 8 ms, subsequent 12 ms; TTL min(64 ms, pulse deadline). Round-robin, <=128 flow lanes /512 packets /768 KiB; queue exhaustion cancels/drains fail-open. Data/control for same tuple drain prior ACKs first. No payload/sequence modification or ACK fabrication. STOP/expiry prioritizes safe drain over intervals; Windows is not real-time.

## Verification and corrections

Linux .NET 8 Windows-target builds passed; execution is Windows GitHub isolated runners, not Linux WPF and not owner's PC.

First run 41494069022: all new deterministic tests passed, but native checks exposed IPv4 checksum normalization in Windows loopback sniffed copies, unavailable loopback adapter in GetAdaptersAddresses, and WPF test's obsolete single-TextBox assumption. Corrected exact named duration editor, read-only MIB_IFROW lookup, and verification split: strict checksums at the real native-send boundary plus actual received payload and post-WFP transport checksum/Traffic Class. Only IPv4 header checksum field is excluded in raw loopback equality; payload/transport never normalized. Test observer calls real WinDivertSend through live engine handle (not synthetic delivery).

Run 41494668892: Windows build, dependency audit, unit/regression and WPF visual checks passed; 78/79 driver tests passed, including all nine new native cases. Existing retired Gap test failed (12 packets held, no reordered pairs, arrival around 974 ms beyond 800 ms pulse); no test bypass/relaxation or legacy implementation change. Full gate must be repeated and remain green before release.

Native tests use bound owned loopback sockets; injection chain priority +1000 -> production 0 -> sniff -1000. TCP ACK test uses real connected sockets, pre/post QPC timestamp and ACK identity matching, no fabricated ACKs. UDP marks/copies have baseline/pulse/STOP, exact payload/checksum/header assertions on IPv4/IPv6. MTU probe report includes only own endpoint ports and diagnostic values. WPF UIA loops every production choice through ARMADO/PULSO/STOP with native driver and screenshots; no direct-command fallback or skipped tests.

## Uninspected / risks

No owner PC, physical keyboard, NIC, Internet/WAN, actual Roblox Player, endpoint ECN response, router DSCP prioritization, real IPv4 fragmentation/reassembly or IPv6 PMTUD experiment. Duplication/pacing/CE may degrade connections or induce retransmissions. GLOBAL affects all outbound traffic during manual pulse; tests run only isolated VM. Kernel/network send failures can lose packets; fail-open does not promise zero loss. No Roblox exploitation/advantage, cancelled Roblox experiments, UDMUX proof, multipath or NAT rebinding claim.

Jev/Semgrep external tools unavailable in prior environment; do not claim their approval. Review performed in existing parent as required (no second agent); standards/spec axes checked against #28 and current repository conventions. Main final SHA, run links, counts and package hashes belong in separately generated release evidence.
