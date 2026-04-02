#!/usr/bin/env python3
"""
sync.py — Push local Network Storage data (collections, endpoints, workflows)
to the sbox.cool management API, and generate collection JSON from C# data files.

This script lives inside the network-storage library and is invoked by the
editor Sync Tool. It requires --project-root to locate the game project.

Usage (from the editor, project root is passed automatically):
    python <lib>/Editor/sync.py --project-root <dir>                  # push everything
    python <lib>/Editor/sync.py --project-root <dir> --generate       # generate from C#
    python <lib>/Editor/sync.py --project-root <dir> --validate       # check credentials
"""

import sys as _sys
if _sys.stdout.encoding != 'utf-8':
    import io
    _sys.stdout = io.TextIOWrapper(_sys.stdout.buffer, encoding='utf-8', errors='replace')
    _sys.stderr = io.TextIOWrapper(_sys.stderr.buffer, encoding='utf-8', errors='replace')
del _sys

import argparse
import json
import os
import re
import sys
from pathlib import Path
from urllib.request import Request, urlopen
from urllib.error import HTTPError

# ── Paths (set in main() from --project-root) ────────────────────

PROJECT_ROOT = None
NS_DIR = None
CONFIG_DIR = None
COLLECTIONS_DIR = None
ENDPOINTS_DIR = None
WORKFLOWS_DIR = None


def init_paths(project_root, data_folder="Network Storage"):
    """Initialize all paths from the project root."""
    global PROJECT_ROOT, NS_DIR, CONFIG_DIR, COLLECTIONS_DIR, ENDPOINTS_DIR, WORKFLOWS_DIR
    PROJECT_ROOT = Path(project_root).resolve()
    NS_DIR = PROJECT_ROOT / "Editor" / data_folder
    CONFIG_DIR = NS_DIR / "config"
    COLLECTIONS_DIR = NS_DIR / "collections"
    ENDPOINTS_DIR = NS_DIR / "endpoints"
    WORKFLOWS_DIR = NS_DIR / "workflows"


def detect_data_folder(project_root):
    """Read dataFolder from projectConfig.json, or fall back to 'Network Storage'."""
    root = Path(project_root).resolve()
    # Check default location first
    cfg = root / "Editor" / "Network Storage" / "config" / "public" / "projectConfig.json"
    if cfg.exists():
        try:
            with open(cfg, encoding='utf-8') as f:
                return json.load(f).get("dataFolder", "Network Storage")
        except Exception:
            pass
    return "Network Storage"

# ── Config ──────────────────────────────────────────────────────────

def load_config():
    """Load project config and secret key from Editor/Network Storage/config/."""
    public_cfg_path = CONFIG_DIR / "public" / "projectConfig.json"
    secret_cfg_path = CONFIG_DIR / "secret" / "secret_key.json"

    if not public_cfg_path.exists():
        print(f"ERROR: Project config not found at {public_cfg_path}")
        sys.exit(1)

    if not secret_cfg_path.exists():
        print(f"ERROR: Secret key not found at {secret_cfg_path}")
        sys.exit(1)

    with open(public_cfg_path, encoding='utf-8') as f:
        public_cfg = json.load(f)

    with open(secret_cfg_path, encoding='utf-8') as f:
        secret_cfg = json.load(f)

    return {
        "projectId": public_cfg["projectId"],
        "publicKey": public_cfg["publicKey"],
        "secretKey": secret_cfg["secretKey"],
        "baseUrl": public_cfg.get("baseUrl", "https://api.sboxcool.com"),
        "apiVersion": public_cfg.get("apiVersion", "v3"),
    }


def api_url(config, path):
    return f"{config['baseUrl']}/{config['apiVersion']}/manage/{config['projectId']}/{path}"


def api_headers(config):
    return {
        "x-api-key": config["secretKey"],
        "x-public-key": config["publicKey"],
        "Content-Type": "application/json",
        "User-Agent": "sbox-network-storage-sync/1.0",
    }


def api_request(config, method, path, body=None):
    """Make an authenticated request to the management API."""
    url = api_url(config, path)
    headers = api_headers(config)
    data = json.dumps(body).encode() if body else None
    req = Request(url, data=data, headers=headers, method=method)

    try:
        with urlopen(req) as resp:
            return json.loads(resp.read().decode())
    except HTTPError as e:
        error_body = e.read().decode() if e.fp else ""
        print(f"  HTTP {e.code}: {error_body}")
        raise

# ── Load local files ─────────────────────────────────────────────

def load_json_dir(directory):
    """Load all .json files from a directory, returning a list of parsed objects."""
    if not directory.exists():
        return []
    items = []
    for f in sorted(directory.glob("*.json")):
        with open(f, encoding='utf-8') as fh:
            items.append(json.load(fh))
    return items


def load_collections():
    return load_json_dir(COLLECTIONS_DIR)


def load_endpoints():
    return load_json_dir(ENDPOINTS_DIR)


def load_workflows():
    return load_json_dir(WORKFLOWS_DIR)

# ── Push ─────────────────────────────────────────────────────────

def push_collections(config, dry_run=False):
    collections = load_collections()
    if not collections:
        print("  No collections found, skipping")
        return

    print(f"  Found {len(collections)} collection(s):")
    for c in collections:
        print(f"    - {c.get('name', '?')}")

    if dry_run:
        return

    # Fetch existing remote collections to preserve IDs
    try:
        remote = api_request(config, "GET", "collections")
        remote_by_name = {}
        if isinstance(remote, list):
            remote_by_name = {c["name"]: c for c in remote if "name" in c}
        elif isinstance(remote, dict) and "collections" in remote:
            remote_by_name = {c["name"]: c for c in remote["collections"] if "name" in c}
    except Exception:
        remote_by_name = {}

    # Preserve server IDs
    for c in collections:
        name = c.get("name", "")
        if name in remote_by_name and "id" in remote_by_name[name]:
            c["id"] = remote_by_name[name]["id"]

    result = api_request(config, "PUT", "collections", collections)
    print(f"  Pushed collections: {json.dumps(result, indent=2)[:200]}")


def push_endpoints(config, dry_run=False):
    endpoints = load_endpoints()
    if not endpoints:
        print("  No endpoints found, skipping")
        return

    print(f"  Found {len(endpoints)} endpoint(s):")
    for e in endpoints:
        print(f"    - {e.get('slug', '?')}")

    if dry_run:
        return

    # Fetch existing remote endpoints to preserve IDs
    try:
        remote = api_request(config, "GET", "endpoints")
        remote_by_slug = {}
        if isinstance(remote, list):
            remote_by_slug = {e["slug"]: e for e in remote if "slug" in e}
        elif isinstance(remote, dict) and "endpoints" in remote:
            remote_by_slug = {e["slug"]: e for e in remote["endpoints"] if "slug" in e}
    except Exception:
        remote_by_slug = {}

    for e in endpoints:
        slug = e.get("slug", "")
        if slug in remote_by_slug and "id" in remote_by_slug[slug]:
            e["id"] = remote_by_slug[slug]["id"]

    result = api_request(config, "PUT", "endpoints", endpoints)
    print(f"  Pushed endpoints: {json.dumps(result, indent=2)[:200]}")


def push_workflows(config, dry_run=False):
    workflows = load_workflows()
    if not workflows:
        print("  No workflows found, skipping")
        return

    print(f"  Found {len(workflows)} workflow(s):")
    for w in workflows:
        print(f"    - {w.get('id', '?')}")

    if dry_run:
        return

    try:
        remote = api_request(config, "GET", "workflows")
        remote_by_id = {}
        if isinstance(remote, list):
            remote_by_id = {w["id"]: w for w in remote if "id" in w}
        elif isinstance(remote, dict) and "workflows" in remote:
            remote_by_id = {w["id"]: w for w in remote["workflows"] if "id" in w}
    except Exception:
        remote_by_id = {}

    result = api_request(config, "PUT", "workflows", workflows)
    print(f"  Pushed workflows: {json.dumps(result, indent=2)[:200]}")


def validate(config):
    """Validate credentials against the API."""
    try:
        result = api_request(config, "GET", "validate")
        print(f"  Credentials valid: {json.dumps(result)}")
        return True
    except HTTPError:
        print("  Credentials INVALID")
        return False


# ── C# Parsing ───────────────────────────────────────────────────

def camel_to_snake(name):
    """Convert CamelCase/PascalCase to snake_case."""
    s1 = re.sub(r'(.)([A-Z][a-z]+)', r'\1_\2', name)
    return re.sub(r'([a-z0-9])([A-Z])', r'\1_\2', s1).lower()

def pascal_to_camel(name):
    """Convert PascalCase to camelCase."""
    return name[0].lower() + name[1:] if name else name

def parse_cs_args(args_str):
    """Parse comma-separated C# constructor arguments, respecting quoted strings."""
    args = []
    current = ''
    in_string = False
    escape = False
    depth = 0
    for ch in args_str:
        if escape:
            current += ch
            escape = False
            continue
        if ch == '\\' and in_string:
            current += ch
            escape = True
            continue
        if ch == '"':
            in_string = not in_string
            current += ch
            continue
        if not in_string:
            if ch == '(':
                depth += 1
                current += ch
            elif ch == ')':
                depth -= 1
                current += ch
            elif ch == ',' and depth == 0:
                args.append(current.strip())
                current = ''
            else:
                current += ch
        else:
            current += ch
    if current.strip():
        args.append(current.strip())
    return args

def convert_cs_value(raw, field_type=None):
    """Convert a raw C# literal to a Python value."""
    raw = raw.strip()
    if raw.startswith('"') and raw.endswith('"'):
        return raw[1:-1]
    if '.' in raw and not raw.startswith('-'):
        parts = raw.split('.')
        if len(parts) == 2 and parts[0][0].isupper():
            return parts[1]
    if raw.lower() == 'true': return True
    if raw.lower() == 'false': return False
    try:
        if '.' in raw or raw.lower().endswith('f') or raw.lower().endswith('d'):
            return float(raw.rstrip('fFdD'))
        return int(raw)
    except ValueError:
        return raw

def parse_cs_records(content):
    """Extract record definitions: record Name(Type Field, ...) -> {Name: [{name, type}]}"""
    records = {}
    for m in re.finditer(r'(?:public\s+)?record\s+(\w+)\s*\(\s*(.*?)\s*\)\s*;', content, re.DOTALL):
        name = m.group(1)
        fields = []
        for param in re.split(r',(?![^<]*>)', m.group(2)):
            param = param.strip()
            if not param: continue
            parts = param.rsplit(None, 1)
            if len(parts) == 2:
                fields.append({'name': parts[1], 'type': parts[0]})
        records[name] = fields
    return records

def parse_named_instances(content, record_name, fields):
    """Find static readonly RecordType Name = new(...); declarations and return {VarName: row_dict}."""
    instances = {}
    pattern = (
        r'(?:public|private|internal|protected)\s+static\s+(?:readonly\s+)?'
        + re.escape(record_name) + r'\s+(\w+)\s*=\s*new\s*(?:'
        + re.escape(record_name) + r')?\s*\((.*?)\)\s*;'
    )
    for m in re.finditer(pattern, content, re.DOTALL):
        var_name = m.group(1)
        args = parse_cs_args(m.group(2))
        if len(args) == len(fields):
            row = {}
            for i, field in enumerate(fields):
                key = pascal_to_camel(field['name'])
                row[key] = convert_cs_value(args[i], field['type'])
            instances[var_name] = row
    return instances

def parse_cs_arrays(content, records):
    """Find static arrays of known record types and extract data rows."""
    tables = []
    for record_name, fields in records.items():
        # Find named instances first (e.g. static readonly RodDefinition Wooden = new(...))
        instances = parse_named_instances(content, record_name, fields)

        # Find array declarations: RecordType[] VarName = { ... };
        pattern = (
            r'(?:private|public|internal|protected)\s+(?:static\s+)?(?:readonly\s+)?'
            + re.escape(record_name) + r'\[\]\s+(\w+)\s*=\s*\{(.*?)\}\s*;'
        )
        for m in re.finditer(pattern, content, re.DOTALL):
            var_name = m.group(1)
            block = m.group(2)
            rows = []

            # Try parsing new(...) constructor calls first
            for row_m in re.finditer(r'new\s*(?:' + re.escape(record_name) + r')?\s*\((.*?)\)', block, re.DOTALL):
                args = parse_cs_args(row_m.group(1))
                if len(args) == len(fields):
                    row = {}
                    for i, field in enumerate(fields):
                        key = pascal_to_camel(field['name'])
                        row[key] = convert_cs_value(args[i], field['type'])
                    rows.append(row)

            # If no constructor calls found, try resolving variable references
            if not rows and instances:
                refs = [r.strip().rstrip(',') for r in block.split(',') if r.strip()]
                for ref in refs:
                    ref = ref.strip()
                    if ref in instances:
                        rows.append(dict(instances[ref]))

            if not rows:
                continue

            # Derive table ID
            if var_name.lower() in ('all', '_all'):
                base = record_name
                if base.endswith('Definition'):
                    base = base[:-len('Definition')]
                table_id = camel_to_snake(base) + '_types'
                table_name = base + ' Types'
            else:
                table_id = camel_to_snake(var_name)
                table_name = var_name

            # Build columns
            columns = []
            has_id = any(pascal_to_camel(f['name']) == 'id' for f in fields)
            if not has_id:
                columns.append({'key': 'id', 'type': 'string'})
            for field in fields:
                key = pascal_to_camel(field['name'])
                col_type = 'number' if field['type'] in ('int', 'long', 'float', 'double', 'decimal') else 'string'
                columns.append({'key': key, 'type': col_type})

            # Add id values if missing
            if not has_id:
                for row in rows:
                    name_val = row.get('name', '')
                    row['id'] = camel_to_snake(str(name_val)).replace(' ', '_')

            tables.append({
                'id': table_id,
                'name': table_name,
                'columns': columns,
                'rows': rows,
            })
    return tables

def parse_cs_tuple_arrays(content):
    """Find tuple arrays like (Rarity Rarity, int Weight)[] Name = { ... }"""
    tables = []
    pattern = r'(?:public|private|internal|protected)\s+(?:static\s+)?(?:readonly\s+)?\(([^)]+)\)\[\]\s+(\w+)\s*=\s*\{(.*?)\}\s*;'
    for m in re.finditer(pattern, content, re.DOTALL):
        tuple_def = m.group(1)
        var_name = m.group(2)
        block = m.group(3)
        fields = []
        for part in tuple_def.split(','):
            part = part.strip()
            parts = part.rsplit(None, 1)
            if len(parts) == 2:
                fields.append({'name': parts[1], 'type': parts[0]})
        rows = []
        for row_m in re.finditer(r'\(\s*(.*?)\s*\)', block, re.DOTALL):
            args = parse_cs_args(row_m.group(1))
            if len(args) == len(fields):
                row = {}
                for i, field in enumerate(fields):
                    key = pascal_to_camel(field['name'])
                    row[key] = convert_cs_value(args[i], field['type'])
                rows.append(row)
        if rows:
            tables.append({
                'id': camel_to_snake(var_name),
                'name': var_name,
                'columns': [
                    {'key': pascal_to_camel(f['name']),
                     'type': 'number' if f['type'] in ('int', 'long', 'float', 'double') else 'string'}
                    for f in fields
                ],
                'rows': rows,
            })
    return tables


def generate_collection(mapping):
    """Generate a collection JSON file from C# source files."""
    cs_path = PROJECT_ROOT / mapping['csFile'].replace('\\', '/')
    collection_name = mapping['collection']

    cs_files = sorted(cs_path.glob('*.cs')) if cs_path.is_dir() else [cs_path] if cs_path.is_file() else []
    if not cs_files:
        print(f"  WARNING: No .cs files found at {cs_path}")
        return False

    all_tables = []
    for cs_file in cs_files:
        print(f"    Parsing {cs_file.name}...")
        content = cs_file.read_text(encoding='utf-8')
        records = parse_cs_records(content)
        all_tables.extend(parse_cs_arrays(content, records))
        all_tables.extend(parse_cs_tuple_arrays(content))

    if not all_tables:
        print(f"  WARNING: No data tables found in {mapping['csFile']}")
        return False

    # Load existing collection JSON to preserve metadata
    collection_path = COLLECTIONS_DIR / f"{collection_name}.json"
    existing = {}
    if collection_path.exists():
        with open(collection_path, encoding='utf-8') as f:
            existing = json.load(f)

    output = {
        "name": existing.get("name", collection_name),
        "description": existing.get("description", mapping.get("description", f"Generated from {mapping['csFile']}")),
    }
    for key in ["collectionType", "accessMode", "maxRecords", "allowRecordDelete",
                "requireSaveVersion", "webhookOnRateLimit", "rateLimitAction", "rateLimits", "schema"]:
        if key in existing:
            output[key] = existing[key]
    if "constants" in existing:
        output["constants"] = existing["constants"]
    output["tables"] = all_tables

    COLLECTIONS_DIR.mkdir(parents=True, exist_ok=True)
    with open(collection_path, 'w', encoding='utf-8') as f:
        json.dump(output, f, indent=2, ensure_ascii=False)

    print(f"  Generated {collection_name}.json — {len(all_tables)} table(s):")
    for t in all_tables:
        print(f"    - {t['id']}: {len(t['rows'])} rows, {len(t['columns'])} columns")
    return True


def generate_collections(collection_filter=None):
    """Generate collection JSON from C# sources using configured sync mappings."""
    public_cfg_path = CONFIG_DIR / "public" / "projectConfig.json"
    if not public_cfg_path.exists():
        print("ERROR: projectConfig.json not found")
        sys.exit(1)

    with open(public_cfg_path, encoding='utf-8') as f:
        public_cfg = json.load(f)

    mappings = public_cfg.get("syncMappings", [])
    if not mappings:
        print("No sync mappings configured in projectConfig.json")
        print('Add "syncMappings" to config, e.g.:')
        print('  "syncMappings": [{"csFile": "Code/Fishing", "collection": "game_values"}]')
        return

    if collection_filter:
        mappings = [m for m in mappings if m.get("collection") == collection_filter]
        if not mappings:
            print(f"No mapping found for collection '{collection_filter}'")
            return

    ok = 0
    for mapping in mappings:
        print(f"\n  {mapping['csFile']} -> {mapping['collection']}")
        if generate_collection(mapping):
            ok += 1

    print(f"\nGenerated {ok}/{len(mappings)} collection(s)")


# ── Main ─────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(description="Sync Network Storage data to sbox.cool")
    parser.add_argument("--project-root", type=str, required=True,
                        help="Absolute path to the game project root directory")
    parser.add_argument("--collections", action="store_true", help="Push collections only")
    parser.add_argument("--endpoints", action="store_true", help="Push endpoints only")
    parser.add_argument("--workflows", action="store_true", help="Push workflows only")
    parser.add_argument("--validate", action="store_true", help="Validate credentials only")
    parser.add_argument("--dry-run", action="store_true", help="Show what would be pushed without pushing")
    parser.add_argument("--generate", action="store_true", help="Generate collection JSON from C# data files")
    parser.add_argument("--collection", type=str, help="Filter to a specific collection name (with --generate)")
    args = parser.parse_args()

    # Initialize paths from project root
    data_folder = detect_data_folder(args.project_root)
    init_paths(args.project_root, data_folder)

    print("=== Network Storage Sync ===")
    print(f"Project root: {PROJECT_ROOT}")
    print(f"Data dir:     {NS_DIR}")
    print()

    if args.generate:
        print("── Generate Collection Data ──")
        generate_collections(args.collection)
        return

    config = load_config()
    print(f"Project ID:   {config['projectId']}")
    print(f"API:          {config['baseUrl']}/{config['apiVersion']}")
    print()

    if args.validate:
        validate(config)
        return

    push_all = not (args.collections or args.endpoints or args.workflows)

    if args.dry_run:
        print("[DRY RUN — no changes will be pushed]\n")

    if push_all or args.collections:
        print("── Collections ──")
        push_collections(config, dry_run=args.dry_run)
        print()

    if push_all or args.endpoints:
        print("── Endpoints ──")
        push_endpoints(config, dry_run=args.dry_run)
        print()

    if push_all or args.workflows:
        print("── Workflows ──")
        push_workflows(config, dry_run=args.dry_run)
        print()

    print("Done!")


if __name__ == "__main__":
    main()
