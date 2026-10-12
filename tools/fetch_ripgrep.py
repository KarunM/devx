#!/usr/bin/env python3
"""Download the pinned ripgrep binary for one RID, verify it, and unpack it.

DevX ships ripgrep so that `devx search text` uses a real engine on a machine where `rg` is
not installed. The binary is never committed: the release workflow calls this tool, and the
download is pinned to an exact release and checked against a sha256 recorded here.

    python3 tools/fetch_ripgrep.py <rid> <output-dir>     # writes <output-dir>/rg[.exe]
    python3 tools/fetch_ripgrep.py --list                 # the pinned version and RIDs

Exit 0 with one summary line on success. Any failure -- unknown RID, download error, hash
mismatch, no rg in the archive -- exits 1 with the reason and writes nothing.
"""

import argparse
import hashlib
import io
import sys
import tarfile
import urllib.request
import zipfile
from pathlib import Path

VERSION = "15.2.0"
BASE_URL = f"https://github.com/BurntSushi/ripgrep/releases/download/{VERSION}"

# RID -> (release asset, sha256 of that asset, archive kind). The hashes are the .sha256
# files published beside each asset, recorded here so a download cannot be substituted.
TARGETS = {
    "osx-arm64": ("aarch64-apple-darwin.tar.gz", "3750b2e93f37e0c692657da574d7019a101c0084da05a790c83fd335bad973e4", "tar"),
    "osx-x64": ("x86_64-apple-darwin.tar.gz", "af7825fcc69a2afc7a7aea55fc9af90e26421d8f20fe59df32e233c0b8a231c1", "tar"),
    # musl builds: statically linked, so they run on any glibc/musl distribution.
    "linux-x64": ("x86_64-unknown-linux-musl.tar.gz", "33e15bcf1624b25cdd2a55813a47a2f95dbe126268203e76aa6a585d1e7b149c", "tar"),
    "linux-arm64": ("aarch64-unknown-linux-musl.tar.gz", "800b1e7206afe799dfb5a6901f23147cfaabe0e52210538100f61e86e1740915", "tar"),
    "win-x64": ("x86_64-pc-windows-msvc.zip", "71b2fef860abe467217a538ff31de02f5258807c0129f771846f87bd029aafc5", "zip"),
}

def binary_name(rid: str) -> str:
    return "rg.exe" if rid.startswith("win-") else "rg"


def find_binary(names):
    """The single rg entry in the archive, which sits inside a versioned top-level folder."""
    for name in names:
        if Path(name).name in {"rg", "rg.exe"}:
            return name
    return None


def extract(archive: bytes, kind: str, output_dir: Path, rid: str) -> None:
    if kind == "tar":
        with tarfile.open(fileobj=io.BytesIO(archive), mode="r:gz") as tar:
            member = find_binary(tar.getnames())
            if member is None:
                raise RuntimeError("no rg binary in the archive")
            source = tar.extractfile(member)
            if source is None:
                raise RuntimeError(f"cannot read {member} from the archive")
            data = source.read()
    else:
        with zipfile.ZipFile(io.BytesIO(archive)) as zipped:
            member = find_binary(zipped.namelist())
            if member is None:
                raise RuntimeError("no rg binary in the archive")
            data = zipped.read(member)

    output_dir.mkdir(parents=True, exist_ok=True)
    target = output_dir / binary_name(rid)
    target.write_bytes(data)
    target.chmod(0o755)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("rid", nargs="?", help="one of: " + ", ".join(sorted(TARGETS)))
    parser.add_argument("output", nargs="?", help="directory to write rg into")
    parser.add_argument("--list", action="store_true", help="print the pinned version and RIDs")
    args = parser.parse_args()

    if args.list or not args.rid or not args.output:
        print(f"ripgrep {VERSION}: {', '.join(sorted(TARGETS))}")
        return 0 if args.list else 1

    if args.rid not in TARGETS:
        print(f"FAIL unknown rid '{args.rid}'; known: {', '.join(sorted(TARGETS))}")
        return 1

    asset, expected, kind = TARGETS[args.rid]
    url = f"{BASE_URL}/ripgrep-{VERSION}-{asset}"
    try:
        with urllib.request.urlopen(url, timeout=120) as response:
            archive = response.read()
    except Exception as error:  # noqa: BLE001 - any transport failure is the same outcome
        print(f"FAIL could not download {url}: {error}")
        return 1

    actual = hashlib.sha256(archive).hexdigest()
    if actual != expected:
        print(f"FAIL sha256 mismatch for {asset}: expected {expected}, got {actual}")
        return 1

    try:
        extract(archive, kind, Path(args.output), args.rid)
    except Exception as error:  # noqa: BLE001 - a bad archive must not half-write
        print(f"FAIL could not unpack {asset}: {error}")
        return 1

    target = Path(args.output) / binary_name(args.rid)
    print(f"ripgrep {VERSION} {args.rid} -> {target} ({target.stat().st_size} bytes, sha256 verified)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
