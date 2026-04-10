#!/usr/bin/env python3
import json
import sys
from datetime import datetime, timezone

if len(sys.argv) != 6:
    raise SystemExit("usage: update_manifest.py <manifest> <version> <targetAbi> <sourceUrl> <checksum>")

manifest_path, version, target_abi, source_url, checksum = sys.argv[1:]

with open(manifest_path, 'r', encoding='utf-8') as f:
    data = json.load(f)

entry = data[0]
entry['versions'] = [{
    'version': version,
    'changelog': f'Release {version}.',
    'targetAbi': target_abi,
    'sourceUrl': source_url,
    'checksum': checksum,
    'timestamp': datetime.now(timezone.utc).strftime('%Y-%m-%d %H:%M:%S')
}]

with open(manifest_path, 'w', encoding='utf-8') as f:
    json.dump(data, f, indent=2)
    f.write('\n')
