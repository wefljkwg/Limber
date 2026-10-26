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
