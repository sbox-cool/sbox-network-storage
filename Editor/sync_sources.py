"""YAML source-definition discovery for the Network Storage sync tool."""

SOURCE_LOCATIONS = (
    ("collection", "collections"),
    ("endpoint", "endpoints"),
    ("workflow", "workflows"),
    ("test", "tests"),
    ("library", "libraries"),
)


def source_id_from_filename(path, kind):
    for suffix in (f".{kind}.yaml", f".{kind}.yml"):
        if path.name.endswith(suffix):
            return path.name[:-len(suffix)]
    return path.stem


def load_source_definitions(ns_dir):
    """Load YAML source definitions as raw source payloads for backend compilation."""
    items = []
    for kind, folder_name in SOURCE_LOCATIONS:
        directory = ns_dir / folder_name
        if not directory.exists():
            continue
        files = sorted(list(directory.glob(f"*.{kind}.yaml")) + list(directory.glob(f"*.{kind}.yml")))
        for path in files:
            rel = path.relative_to(ns_dir).as_posix()
            with open(path, encoding="utf-8") as fh:
                items.append({
                    "kind": kind,
                    "id": source_id_from_filename(path, kind),
                    "sourceFormat": "yaml",
                    "sourcePath": rel,
                    "sourceText": fh.read(),
                })
    return items
