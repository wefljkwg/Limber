"""Write repo.json (the custom plugin repository Dalamud reads) from the manifest the build produced.

Usage: python make_repo_json.py <built manifest json> <output repo.json>
Uses GITHUB_REPOSITORY (owner/name) for the download links, so nothing account-specific is stored in the code.
"""
import json
import os
import sys
import time

manifest_path, out_path = sys.argv[1], sys.argv[2]
repo = os.environ["GITHUB_REPOSITORY"]

with open(manifest_path, encoding="utf-8-sig") as f:
    manifest = json.load(f)

# The packager normally fills in AssemblyVersion; fall back to the project version.
version = manifest.get("AssemblyVersion") or os.environ["LIMBER_VERSION"]

download = f"https://github.com/{repo}/releases/latest/download/latest.zip"


def newest_changes(path="CHANGELOG.md"):
    """The newest version's entry in CHANGELOG.md (shown in Dalamud's plugin installer), or ""."""
    try:
        with open(path, encoding="utf-8-sig") as f:
            text = f.read()
    except OSError:
        return ""
    parts = text.split("\n## ")
    return parts[1].split("\n", 1)[1].strip() if len(parts) > 1 and "\n" in parts[1] else ""


entry = {
    "Author": manifest.get("Author", ""),
    "Name": manifest["Name"],
    "InternalName": manifest["InternalName"],
    "AssemblyVersion": version,
    "TestingAssemblyVersion": version,
    "Punchline": manifest.get("Punchline", ""),
    "Description": manifest.get("Description", ""),
    "ApplicableVersion": manifest.get("ApplicableVersion", "any"),
    "Tags": manifest.get("Tags", []),
    "DalamudApiLevel": manifest["DalamudApiLevel"],
    "TestingDalamudApiLevel": manifest["DalamudApiLevel"],
    "LoadRequiredState": manifest.get("LoadRequiredState", 0),
    "LoadSync": manifest.get("LoadSync", False),
    "CanUnloadAsync": manifest.get("CanUnloadAsync", False),
    "LoadPriority": manifest.get("LoadPriority", 0),
    "AcceptsFeedback": False,
    "Changelog": newest_changes(),
    "RepoUrl": f"https://github.com/{repo}",
    "IsHide": False,
    "IsTestingExclusive": False,
    "DownloadCount": 0,
    "LastUpdate": int(time.time()),
    "DownloadLinkInstall": download,
    "DownloadLinkUpdate": download,
    "DownloadLinkTesting": download,
}

with open(out_path, "w", encoding="utf-8", newline="\n") as f:
    json.dump([entry], f, indent=2, ensure_ascii=False)
    f.write("\n")

print(f"Wrote {out_path} for {entry['InternalName']} {entry['AssemblyVersion']}")
