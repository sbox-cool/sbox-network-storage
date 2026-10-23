import sys
import unittest
from pathlib import Path
from unittest.mock import patch
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Editor"))
import sync
from sync_api_errors import SyncApiError


class SecretTransportTests(unittest.TestCase):
    def test_secret_is_sent_to_https_and_loopback_only(self):
        for base_url in [
            "https://api.sboxcool.com",
            "HTTPS://api.sboxcool.com",
            "http://localhost:8080",
            "http://127.0.0.1:5000",
            "http://[::1]:5000",
        ]:
            with self.subTest(base_url=base_url):
                self.assertTrue(sync.secret_transport_allowed(base_url))

    def test_secret_is_refused_for_plain_http_non_loopback_and_malformed_urls(self):
        for base_url in [
            "http://api.sboxcool.com",
            "http://192.168.1.10:8080",
            "ftp://api.sboxcool.com",
            "api.sboxcool.com",
            "http://[::1",
            "",
            None,
        ]:
            with self.subTest(base_url=base_url):
                self.assertFalse(sync.secret_transport_allowed(base_url))

    def test_non_http_schemes_are_refused_even_for_loopback_hosts(self):
        # A loopback host alone is not enough: only plain http is allowed to loopback.
        for base_url in [
            "file:///tmp",
            "file://localhost/tmp",
            "ftp://localhost",
            "ftp://localhost:21",
            "ws://127.0.0.1:8080",
        ]:
            with self.subTest(base_url=base_url):
                self.assertFalse(sync.secret_transport_allowed(base_url))

    def test_api_request_refuses_insecure_base_url_before_any_network_io(self):
        config = {
            "baseUrl": "http://api.example.com",
            "apiVersion": "v3",
            "projectId": "project",
            "publicKey": "sbox_ns_public",
            "secretKey": "sbox_sk_secret",
        }
        with patch.object(sync, "_secret_opener") as opener:
            with self.assertRaises(SyncApiError):
                sync.api_request(config, "GET", "validate")
        opener.open.assert_not_called()

    def test_api_request_does_not_follow_redirects_that_would_replay_the_key(self):
        received = []

        class Target(BaseHTTPRequestHandler):
            def do_GET(self):
                received.append(self.headers.get("x-api-key"))
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.end_headers()
                self.wfile.write(b"{}")

            def log_message(self, *args):
                pass

        target_url = None

        class Redirector(BaseHTTPRequestHandler):
            def do_GET(self):
                self.send_response(302)
                self.send_header("Location", target_url)
                self.end_headers()

            def log_message(self, *args):
                pass

        target = HTTPServer(("127.0.0.1", 0), Target)
        redirector = HTTPServer(("127.0.0.1", 0), Redirector)
        target_url = f"http://127.0.0.1:{target.server_address[1]}/"
        servers = [target, redirector]
        for server in servers:
            threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            config = {
                "baseUrl": f"http://127.0.0.1:{redirector.server_address[1]}",
                "apiVersion": "v3",
                "projectId": "project",
                "publicKey": "sbox_ns_public",
                "secretKey": "sbox_sk_secret",
            }
            with patch.object(sync, "print_api_error"):
                with self.assertRaises(SyncApiError):
                    sync.api_request(config, "GET", "validate")
        finally:
            for server in servers:
                server.shutdown()
                server.server_close()
        self.assertEqual([], received)


if __name__ == "__main__":
    unittest.main()
