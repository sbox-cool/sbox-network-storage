import io
import sys
import threading
import unittest
from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path
from unittest.mock import MagicMock, patch
from urllib.error import HTTPError, URLError

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


def _opener_returning(body):
    opener = MagicMock()
    opener.open.return_value.__enter__.return_value.read.return_value = body
    return opener


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
    def test_request_has_a_timeout(self):
        opener = _opener_returning(b'{"ok": true}')
        with patch.object(sync, "_secret_opener", opener):
            self.assertEqual({"ok": True}, sync.api_request(_config(8080), "GET", "validate"))
        _, kwargs = opener.open.call_args
        self.assertEqual(sync.API_TIMEOUT_SECONDS, kwargs.get("timeout"))

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

    def test_http_error_still_raises_sync_error_and_prints_body(self):
        error = HTTPError(
            "http://127.0.0.1:8080/v3/validate", 403, "Forbidden", {}, io.BytesIO(b'{"error":"KEY_DISABLED"}')
        )
        opener = MagicMock()
        opener.open.side_effect = error
        with patch.object(sync, "_secret_opener", opener), patch.object(sync, "print_api_error") as printer:
            with self.assertRaises(SyncApiError):
                sync.api_request(_config(8080), "GET", "validate")
        printer.assert_called_once_with(403, '{"error":"KEY_DISABLED"}')

    def test_empty_success_body_returns_empty_dict(self):
        with _serve(204, b"") as port:
            self.assertEqual({}, sync.api_request(_config(port), "PUT", "collections", {"a": 1}))

    def test_non_json_success_body_becomes_sync_error(self):
        with _serve(200, b"<html>proxy page</html>") as port:
            with self.assertRaises(SyncApiError):
                sync.api_request(_config(port), "GET", "validate")


if __name__ == "__main__":
    unittest.main()
