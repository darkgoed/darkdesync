# Issue #35: laboratory network experiments

The main panel still has exactly four production pulse effects. `DiagnosticMode`,
legacy settings migration/rejection, subjective ratings, 250/800 ms presets,
START/SPACE/STOP and global outbound policy are unchanged. No rejected effect is
reintroduced. The new button opens a separate modal laboratory only while STOPPED.
No experiment feeds the pulse engine, requires a game PID, injects into a process,
or claims a game exploit. Never run global pulse tests on a shared workstation.

## Matrix (capability, not an invented result)

| Mechanism | Type when successful | Dependencies / actual test | Limitation and risk |
|---|---|---|---|
| TCP Receive Window Clamping | Observation only | Own IPv4/IPv6 TCP listener + client, sniff-only WinDivert capture before connect. Decode SYN/SYN-ACK, advertised windows, negotiated WS per direction. A/B sends and exact reads in both directions; raw packet hex retained. | No clamp: cannot guarantee safe right-edge/window consistency in a generic outbound interceptor. No zero-window, ACK edits or unsafe shrink. SYN windows unscaled; subsequent windows use WS only if both sides advertised it. TCP is irrelevant to UDP. |
| IPv6 Flow Label Variation | Operational (own test) | Two bound ::1 UDP sockets, WinDivert injection and sniff. Same 5-tuple, labels 0x12345 and 0xabcde in two explicit phases, two packets/phase. 20-bit codec preserves all other fields. Mandatory UDP pseudoheader checksum, byte-exact payload delivery and captured labels. | IPv6/driver failure is unavailable, not simulated. Fixed hop limit 64, fresh packets only, no impostor reinjection. Flow label does not force a different route. |
| ICMP Control-Plane Feedback | Observation only | Legitimate Ping API echo to 127.0.0.1 and ::1 with random owned payload, status, RTT and exact reply verification. | Type numbers in the report describe Echo protocol/API semantics, not raw-capture evidence. Does not forge errors or intercept ICMPv6, PTB or NDP. Router-error/PMTU/recovery trials require a separate owned router lab. |
| NIC Offload Variation | Observation only | Read-only PowerShell Get-NetAdapter, Get-NetAdapterUso/Lso/ChecksumOffload/Rss, per-command availability, selected capability/configuration fields per adapter. Paired own IPv4 UDP loopback capture, exact payload and local interface names (optional WinDivert dependency). | Configured state plus loopback capture is not evidence of actual physical hardware use: loopback does not exercise that NIC. No physical capture/offload correlation or toggle claimed. Never Set/Disable/Enable-NetAdapter. A missing cmdlet/capture is explicit, not silently a disabled feature. |
| UDP NAT Mapping Lifetime | Operational only with observed translation and delayed inbound; otherwise requires lab | Explicit owned reflector consent, numeric unicast IP, port >=1024, alphanumeric token. One client send per fresh socket at 1/3/6 s idle; initial mapped IP:port + later inbound without keepalive. | Loopback proves protocol only, never external NAT. Delayed success establishes survival lower bound; failure can be loss/filter/timeout, not proof of expiry. No STUN server or router is contacted by default; no NAT configuration change. |
| TCP SACK / Timestamp Behavior | Observation only | Same own TCP handshake/capture as window test. Strict bounded TCP options decoder: SACK Permitted, real SACK blocks, TSval/TSecr, WS. | Windows may not negotiate timestamps and loss-free loopback may contain no SACK blocks. Absent fields are null/empty, not manufactured. No handshake edits, false ACKs or UDP claim. |

## Execution / evidence

Open `Experimentos de rede (6)` while stopped, choose a technique and run. Initial
state is unverified/requires lab. Returned status is one of Operational (own test),
Observation only, Requires lab, Unavailable. STOP cancels and drains local work;
closing while running requests cancellation before closing. All trials have a
25-second total deadline, bounded capture (128 frames), scoped owned socket filters
and released handles. Captures are sniff-only/fail-open; IPv6 injection creates only
four new datagrams to the process-owned bound ::1 receiver. No persistent host
configuration is modified. JSON can be explicitly saved; reflector token is not
saved in evidence/settings. TCP raw hex contains only generated local test traffic.

### Controlled NAT reflector

Python 3 stdlib only. On a server/router YOU control:

    DD_LAB_TOKEN=<random alphanumeric 16..128 chars> python3 tools/nat_reflector.py --bind <owned-server-IP> --allow <exact-owned-client-egress-IP> --port 39435

Windows PowerShell can set `$env:DD_LAB_TOKEN` instead. The default bind/allow is
127.0.0.1. Unspecified/multicast bind/allow addresses are rejected. Nonces, tokens,
idle bounds and exact source IP are checked, responses bounded, concurrent workers
capped at 16. Do not expose an unrestricted reflector. Enter the same token in the
lab UI and explicitly confirm ownership. A local reflector is sufficient to test
the protocol but will correctly report `Requires lab` without translated mapping.
The tool is a small authenticated lab protocol, NOT a claim of RFC STUN compliance.

### Gates / honest evidence

- Unit tests cover independent checksums, odd payloads, 20-bit bounds, preservation,
  TCP option lengths/protocols/truncation/fragments, absent options, six stable IDs,
  unchanged four effects, topology/consent rejection and real delayed loopback UDP.
- `tools/test_nat_reflector.py` exercises actual UDP baseline/delayed replies and
  verifies invalid token/nonce/interval get no response.
- Windows WPF tests use an external UIA process, six-item selection and actual Run
  invocation proving NAT prerequisite status with zero sent packets. Existing four
  favorite, rating, compact layout and legacy setting tests remain mandatory.
- Explicit Windows driver dispatch executes real new TCP, UDP IPv6, ICMP, offload
  diagnostics, asserts required status and records JSON/raw frames in TRX stdout.
  Cancellation tests require STOP status and a clean next run. Driver gates fail
  on missing capture/handshake or unavailable local prerequisites; no replacement
  fabricated evidence and no retries used as proof.
- External NAT timeout, hardware offload use and router ICMP error/recovery are NOT
  verified by GitHub's loopback runner. They remain explicit deployment/lab limits.

Release evidence must name exact PR and final main SHAs, workflow IDs/conclusions,
ZIP SHA256 and SOURCE_COMMIT. Keep .12 as rollback. Completion is not claimed until
both exact-SHA CI+driver gates pass and the downloaded release is checked.
