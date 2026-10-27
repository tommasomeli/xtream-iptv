#!/usr/bin/env python3
"""Adds (or replaces) one version in the Jellyfin plugin repository manifest.

Usage: update-manifest.py <manifest.json> <version> <targetAbi> <zip path> <zip download url> [changelog]
The manifest is the list Jellyfin reads from a repository URL: one plugin entry with its versions,
newest first, each with the MD5 checksum of its zip.
"""
import hashlib
import json
import sys
from datetime import datetime, timezone

GUID = "c02587d5-61e0-415a-a2d5-3ccd3107d112"


def main(argv):
    if len(argv) < 6:
        print(__doc__)
        return 2
    path, version, abi, zip_path, url = argv[1:6]
    changelog = argv[6] if len(argv) > 6 else ""
    if version.count(".") == 2:
        version += ".0"
    with open(zip_path, "rb") as f:
        checksum = hashlib.md5(f.read()).hexdigest()
    try:
        with open(path, encoding="utf-8") as f:
            manifest = json.load(f)
    except FileNotFoundError:
        manifest = []
    plugin = next((p for p in manifest if p.get("guid") == GUID), None)
    if plugin is None:
        plugin = {
            "guid": GUID,
            "name": "Xtream IPTV",
            "description": "Live TV with guide plus a movie and series catalogue from an Xtream account, with metadata from the Xtream lists.",
            "overview": "Xtream account as Live TV and a file-less catalogue",
            "owner": "tommasomeli",
            "category": "Live TV",
            "imageUrl": "",
            "versions": [],
        }
        manifest.append(plugin)
    entry = {
        "version": version,
        "changelog": changelog,
        "targetAbi": abi,
        "sourceUrl": url,
        "checksum": checksum,
        "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }
    plugin["versions"] = [entry] + [v for v in plugin.get("versions", []) if v.get("version") != version]
    with open(path, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")
    print("manifest: %s %s %s" % (version, abi, checksum))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
