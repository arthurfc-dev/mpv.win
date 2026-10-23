#!/usr/bin/env python3
"""Preserve notices from verified build inputs, without executing their contents."""
import argparse
import hashlib
import io
import json
from pathlib import Path, PurePosixPath
import re
import tarfile
import zipfile

LEGAL = re.compile(r"^(?:copying|copyright|licen[cs]e|notice|authors|patents)(?:[._-].*)?$", re.I)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("sources", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    pins = json.loads(Path("dependencies/libmpv-sources.json").read_text())
    records, archives = [], []

    def preserve(group, member, data):
        parts = PurePosixPath(member).parts
        if not parts or any(p in ("..", "/") for p in parts):
            raise ValueError("Unsafe archive name: " + member)
        # Keep installed paths short enough for Windows build tools. Preserve the
        # complete original name in the inventory and keep notice bytes unchanged.
        digest = hashlib.sha256(data).hexdigest()
        suffix = PurePosixPath(member).suffix
        if not re.fullmatch(r"\.[A-Za-z0-9]{1,6}", suffix):
            suffix = ".txt"
        relative = Path("files", digest + suffix)
        destination = args.output / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(data)
        records.append({"path": relative.as_posix(), "sourcePath": group + "/" + member, "sha256": digest, "bytes": len(data)})

    def inspect_tar(stream, group):
        count = 0
        with tarfile.open(fileobj=stream, mode="r|*") as archive:
            for entry in archive:
                if entry.isfile() and LEGAL.match(PurePosixPath(entry.name).name):
                    if entry.size > 32 * 1024 * 1024:
                        raise ValueError("Unexpected notice size: " + entry.name)
                    preserve(group, entry.name, archive.extractfile(entry).read())
                    count += 1
                elif entry.isfile() and "ffnvcodec" in group and entry.name.endswith(".h"):
                    # nv-codec-headers carries its license in headers rather than
                    # a standalone LICENSE file. Preserve each exact comment.
                    if entry.size > 32 * 1024 * 1024:
                        raise ValueError("Unexpected header size: " + entry.name)
                    data = archive.extractfile(entry).read()
                    for index, match in enumerate(re.finditer(rb"/\*.*?\*/", data, re.S)):
                        block = match.group()
                        if b"copyright" in block.lower() and b"permission is hereby granted" in block.lower():
                            preserve(group, entry.name + f".{index}.license.txt", block)
                            count += 1
        archives.append({"archive": group, "noticeCount": count})

    for bundle in pins["bundles"]:
        path = args.sources / bundle["name"]
        with path.open("rb") as stream:
            if hashlib.file_digest(stream, "sha256").hexdigest() != bundle["sha256"]:
                raise ValueError("Source bundle checksum mismatch: " + path.name)
        if path.suffix == ".zip":
            with zipfile.ZipFile(path) as archive:
                for entry in archive.infolist():
                    if entry.filename.startswith("sources/") and entry.filename.endswith((".tar.gz", ".tgz", ".tar.xz", ".tar.bz2")):
                        with archive.open(entry) as stream:
                            inspect_tar(stream, "container/" + PurePosixPath(entry.filename).name)
        else:
            # Source tar contains nested source archives and the exact Cargo vendor tree.
            with tarfile.open(path, "r|gz") as archive:
                for entry in archive:
                    if not entry.isfile():
                        continue
                    name = entry.name.removeprefix("./")
                    if name.startswith("sources/") and name.endswith((".tar.gz", ".tgz", ".tar.xz", ".tar.bz2")):
                        # Nested tar readers must not consume the parent tar stream's next entry.
                        inspect_tar(io.BytesIO(archive.extractfile(entry).read()), "build/" + PurePosixPath(name).name)
                    elif name.startswith("rust-vendor/") and LEGAL.match(PurePosixPath(name).name):
                        preserve("build", name, archive.extractfile(entry).read())
                    elif name.startswith("meson-info/"):
                        preserve("evidence", name, archive.extractfile(entry).read())
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "notice-inventory.json").write_text(json.dumps({
        "schemaVersion": 1,
        "scope": "Conservative build-input notice collection, including build-only components; not a claim that every input is linked into the DLL.",
        "sources": pins,
        "archives": archives,
        "files": sorted(records, key=lambda r: r["path"]),
    }, indent=2) + "\n")
    print(f"Preserved {len(records)} files from {len(archives)} source archives")


if __name__ == "__main__":
    main()
