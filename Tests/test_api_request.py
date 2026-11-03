import io
import sys
import threading
import unittest
from contextlib import contextmanager, redirect_stdout
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path
from unittest.mock import MagicMock, patch
from urllib.error import URLError

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Editor"))
import sync
from sync_api_errors import SyncApiError


def _config(port):
    return {
        "baseUrl": f"http://127.0.0.1:{port}",
        "apiVersion": "v3",
        "projectId": "project",
        "publicKey": "sbox_ns_public",
        "secretKey": "sbox_sk_secret",
    }


@contextmanager
def _serve(status, body):
    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            self._reply()

        def do_PUT(self):
            self._reply()

        def _reply(self):
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def log_message(self, *args):
            pass

    server = HTTPServer(("127.0.0.1", 0), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    try:
        yield server.server_address[1]
    finally:
        server.shutdown()
        server.server_close()


class ApiRequestTests(unittest.TestCase):
    def test_network_error_becomes_sync_error(self):
        opener = MagicMock()
        opener.open.side_effect = URLError("connection refused")
        with patch.object(sync, "_secret_opener", opener):
            with self.assertRaises(SyncApiError) as ctx:
                sync.api_request(_config(8080), "GET", "validate")
        self.assertIn("connection refused", str(ctx.exception))

    def test_timeout_while_reading_body_becomes_sync_error(self):
        opener = MagicMock()
        opener.open.return_value.__enter__.return_value.read.side_effect = TimeoutError("timed out")
        with patch.object(sync, "_secret_opener", opener):
            with self.assertRaises(SyncApiError):
                sync.api_request(_config(8080), "GET", "validate")

    def test_http_error_reports_server_code(self):
        output = io.StringIO()
        with _serve(403, b'{"error":"KEY_DISABLED"}') as port, redirect_stdout(output):
            with self.assertRaises(SyncApiError):
                sync.api_request(_config(port), "GET", "validate")
        self.assertIn("KEY_DISABLED", output.getvalue())

    def test_empty_success_body_returns_empty_dict(self):
        with _serve(204, b"") as port:
            self.assertEqual({}, sync.api_request(_config(port), "PUT", "collections", {"a": 1}))

    def test_non_json_success_body_becomes_sync_error(self):
        with _serve(200, b"<html>proxy page</html>") as port:
            with self.assertRaises(SyncApiError):
                sync.api_request(_config(port), "GET", "validate")


if __name__ == "__main__":
    unittest.main()
