"""YAML source-definition discovery for the Network Storage sync tool."""

SOURCE_LOCATIONS = (
    ("collection", "collections"),
    ("endpoint", "endpoints"),
    ("workflow", "workflows"),
    ("test", "tests"),
    ("library", "libraries"),
)


def source_id_from_filename(path, kind):
    for suffix in (f".{kind}.yaml", f".{kind}.yml", ".yaml", ".yml", ".json"):
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
        typed = list(directory.glob(f"*.{kind}.yaml")) + list(directory.glob(f"*.{kind}.yml"))
        plain = [p for p in list(directory.glob("*.yaml")) + list(directory.glob("*.yml")) if f".{kind}." not in p.name]
        files = sorted({*typed, *plain})
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
