#!/usr/bin/env python3
"""Owned UDP lab reflector. No spoofing, forwarding or host configuration changes.
Bind loopback by default. For a real NAT test, bind an owned reachable address and
--allow the client's exact gateway egress IP. Set DD_LAB_TOKEN (16..128 ASCII
alphanumeric characters). Never run this as an unrestricted public reflector.
"""
import argparse
import hmac
import ipaddress
import json
import os
import socket
import threading
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--bind', default='127.0.0.1')
    parser.add_argument('--port', type=int, default=39435)
    parser.add_argument('--allow', default='127.0.0.1', help='exact owned client egress IP, no CIDR')
    args = parser.parse_args()
    address, allowed = ipaddress.ip_address(args.bind), ipaddress.ip_address(args.allow)
    token = os.environ.get('DD_LAB_TOKEN', '')
    if not (16 <= len(token) <= 128 and token.isascii() and token.isalnum()):
        parser.error('DD_LAB_TOKEN must contain 16..128 ASCII alphanumeric characters')
    if address.is_multicast or address.is_unspecified or allowed.is_multicast or allowed.is_unspecified:
        parser.error('explicit unicast bind and allowed IP required')
    if not 1024 <= args.port <= 65535:
        parser.error('port must be 1024..65535')
    family = socket.AF_INET6 if address.version == 6 else socket.AF_INET
    slots = threading.BoundedSemaphore(16)
    sock = socket.socket(family, socket.SOCK_DGRAM)
    sock.bind((str(address), args.port))
    print(json.dumps({'ready': True, 'bind': str(address), 'port': args.port}), flush=True)

    def respond(peer, nonce, idle):
        try:
            for phase in ('baseline', 'delayed'):
                if phase == 'delayed':
                    time.sleep(idle)
                packet = json.dumps({'nonce': nonce, 'phase': phase, 'address': peer[0], 'port': peer[1]}).encode()
                sock.sendto(packet, peer)
        finally:
            slots.release()

    while True:
        data, peer = sock.recvfrom(1025)
        if len(data) > 1024 or ipaddress.ip_address(peer[0]) != allowed:
            continue
        try:
            request = json.loads(data)
            nonce, idle = request['nonce'], request['idle']
            if not isinstance(request['token'], str) or not hmac.compare_digest(request['token'], token):
                continue
            if not isinstance(nonce, str) or len(nonce) != 32 or any(c not in '0123456789abcdef' for c in nonce):
                continue
            if type(idle) is not int or idle not in (1, 3, 6):
                continue
            if slots.acquire(blocking=False):
                threading.Thread(target=respond, args=(peer, nonce, idle), daemon=True).start()
        except (ValueError, KeyError, TypeError):
            continue


if __name__ == '__main__':
    main()
