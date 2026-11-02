import json
import os
import pathlib
import socket
import subprocess
import sys
import unittest
import uuid


class ReflectorTests(unittest.TestCase):
    def test_real_loopback_and_unauthorized_silence(self):
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as reservation:
            reservation.bind(('127.0.0.1', 0))
            port = reservation.getsockname()[1]
        token = uuid.uuid4().hex
        env = dict(os.environ, DD_LAB_TOKEN=token)
        process = subprocess.Popen([sys.executable, str(pathlib.Path(__file__).with_name('nat_reflector.py')), '--port', str(port)],
                                   env=env, stdout=subprocess.PIPE, text=True)
        try:
            assert process.stdout is not None
            self.assertTrue(json.loads(process.stdout.readline())['ready'])
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as client:
                client.connect(('127.0.0.1', port))
                client.settimeout(3)
                nonce = uuid.uuid4().hex
                client.send(json.dumps(dict(token=token, nonce=nonce, idle=1)).encode())
                baseline = json.loads(client.recv(1024))
                delayed = json.loads(client.recv(1024))
                self.assertEqual(baseline['phase'], 'baseline')
                self.assertEqual(delayed['phase'], 'delayed')
                self.assertEqual(delayed['nonce'], nonce)
                self.assertEqual(delayed['port'], client.getsockname()[1])
                client.settimeout(.25)
                for invalid in [dict(token='wrong', nonce=nonce, idle=1), dict(token=token, nonce=nonce, idle=99), dict(token=token, nonce='bad', idle=1)]:
                    client.send(json.dumps(invalid).encode())
                    with self.assertRaises(socket.timeout):
                        client.recv(1024)
        finally:
            process.terminate()
            process.wait(timeout=5)
            if process.stdout is not None:
                process.stdout.close()


if __name__ == '__main__':
    unittest.main()
