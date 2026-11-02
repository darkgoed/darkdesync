# GLOBAL mixed traffic follow-up — patch .4

Independent audit PR #15 found that literal WinDivert `outbound` captures IPv6 and ICMP as well as TCP and IPv4 UDP. Patch .3 bypassed only valid IPv4 TCP; unrelated IPv6/ICMP cancelled active UDP pacing. This was a functional pacing interruption, not permission to shape those protocols.

The fix passes non-impostor traffic recognizably outside IPv4 UDP straight through without cancelling the UDP pulse. IPv4 UDP still undergoes complete parsing and size/queue/deadline limits. Malformed IPv4 UDP, impostors (including non-UDP impostors) and short unidentifiable packets retain conservative fail-open cancellation/drain. Retained packets are sent before the rejected current packet, preventing overtake. No filter broadening, scheduler changes, new permissions, persisted consent, intentional drops or reordering were added.

Review and verification scope:
- A deterministic mixed TCP + IPv6 + ICMP + UDP regression confirms disjoint traffic does not revoke the UDP pulse.
- Three deterministic safety regressions confirm malformed UDP / UDP impostor / TCP impostor drain FIFO before the current packet, cancel the pulse, and STOP does not replay.
- Fixed fixture clock avoids 200ms wall-clock flakes. Production timing remains unchanged.
- Full Windows regression, WPF, dependency security and opt-in native WinDivert suite are release gates on PR head and merged main SHA. Exact run links, results, native pacing measurements and payload hashes are attached as RELEASE_EVIDENCE.json and TRX, not inferred from compilation.
- The native suite generates only owned loopback traffic on an isolated Windows runner; GLOBAL can intercept ambient outbound on that ephemeral runner. Mixed IPv6/ICMP assertions are deterministic packet fixtures, not native IPv6/ICMP driver claims.

Compatibility remains unchanged from .3: GLOBAL Packet Size-Aware is consented outbound IPv4 UDP only; PID outbound requires exact tuples; reordering remains PID-owned UDP loopback + experimental opt-in; inbound/duplex do not work under pure outbound. GLOBAL affects other apps in actual use. START arms without effects, default Space pulse 200ms, STOP drains/closes. No Roblox Player, server effect or anti-cheat validation is claimed.

Rollback: preserve v2026.11.02-five-inbound-experimental.3 and every asset unchanged. Release .4 must use only the validated main artifact, with AMD64 EXE/DLL/SYS, exact SOURCE_COMMIT, ZIP CRC and SHA-256, remote asset readback, and Latest/tag verification. No automation configuration or unrelated project is changed.
