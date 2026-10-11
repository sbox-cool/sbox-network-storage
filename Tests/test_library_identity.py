import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Editor"))
import sync


class LibraryRevisionTests(unittest.TestCase):
    def test_missing_package_revision_is_optional(self):
        with tempfile.TemporaryDirectory() as directory:
            with patch.object(sync, "_library_root", Path(directory)):
                self.assertIsNone(sync.read_library_revision())

    def test_package_revision_preserves_positive_int64_values(self):
        cases = {
            "1.0.309133\n": "309133",
            "1.0.000309133": "309133",
            "1.0.2147483648": "2147483648",
            "1.0.9223372036854775807": "9223372036854775807",
        }
        self.check_revisions(cases)

    def test_invalid_package_revisions_are_omitted_not_game_or_semver_identity(self):
        self.check_revisions(dict.fromkeys([
            "", "1.1.0", "309133", "1.0.0", "1.0.-1", "1.0.+1",
            "1.0.9223372036854775808", "1.0.309133-extra", "1.0." + "9" * 5000,
        ]))

    def check_revisions(self, cases):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with patch.object(sync, "_library_root", root):
                for value, expected in cases.items():
                    with self.subTest(value=value[:64]):
                        (root / ".version").write_text(value, encoding="utf-8")
                        self.assertEqual(expected, sync.read_library_revision())


if __name__ == "__main__":
    unittest.main()
