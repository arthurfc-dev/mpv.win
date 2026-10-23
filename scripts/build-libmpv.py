#!/usr/bin/env python3
"""Compile libmpv and preserve build inputs inside the pinned Linux image."""
import datetime
import gzip
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
PINS = json.loads((ROOT / "dependencies/libmpv-build.json").read_text())
OUT = Path("/output")
SOURCES = OUT / "sources"


def run(*args, cwd=None, **kwargs):
    return subprocess.run(args, cwd=cwd, check=True, **kwargs)


def archive(path, label, paths=()):
    with (SOURCES / (label + ".tar.gz")).open("wb") as raw:
        with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as zipped:
            # Keep archive creation bounded in memory even for FFmpeg.
            with subprocess.Popen(["git", "archive", "--format=tar",
                                   "--prefix=" + label + "/", "HEAD", *paths],
                                  cwd=path, stdout=subprocess.PIPE) as process:
                shutil.copyfileobj(process.stdout, zipped)
                if process.wait():
                    raise RuntimeError("Source archive failed: " + label)


def checkout(name, destination):
    repo, revision = PINS["sources"][name]
    path = Path(destination).resolve()
    path.mkdir(parents=True, exist_ok=False)
    run("git", "init", str(path))
    run("git", "remote", "add", "origin", repo, cwd=path)
    sparse = name == "windows-rs"
    run("git", "fetch", "--depth=1", "--no-tags",
        *(["--filter=blob:none"] if sparse else []), "origin", revision, cwd=path)
    if sparse:
        run("git", "sparse-checkout", "set", "crates/libs/bindgen/default", cwd=path)
    run("git", "checkout", "--detach", "FETCH_HEAD", cwd=path)
    actual = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=path, text=True).strip()
    if actual != revision:
        raise RuntimeError("Revision mismatch: " + name)
    archive(path, name + "-" + revision,
            ("crates/libs/bindgen/default", "license-apache-2.0", "license-mit") if sparse else ())
    if not sparse:
        run("git", "-c", "protocol.file.allow=never", "submodule", "update",
            "--init", "--recursive", "--depth=1", cwd=path)
        listing = subprocess.check_output(
            ["git", "submodule", "foreach", "--quiet", "--recursive", "pwd"], cwd=path, text=True)
        for item in listing.splitlines():
            child = Path(item)
            sha = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=child, text=True).strip()
            label = name + "-submodule-" + str(child.relative_to(path)).replace("/", "_") + "-" + sha
            archive(child, label)
    if name == "subrandr":
        lock = (path / "Cargo.lock").read_bytes()
        run("cargo", "fetch", "--locked", cwd=path)
        with (OUT / "cargo-vendor-config.toml").open("w") as config:
            run("cargo", "vendor", "--locked", "--offline", "--versioned-dirs",
                str(OUT / "rust-vendor"), cwd=path, stdout=config)
        if (path / "Cargo.lock").read_bytes() != lock:
            raise RuntimeError("Cargo.lock changed")
        (OUT / "subrandr-Cargo.lock").write_bytes(lock)


def replace(path, before, after):
    text = path.read_text()
    if text.count(before) != 1:
        raise RuntimeError("Pinned build script changed: " + before)
    path.write_text(text.replace(before, after))


def build():
    if not os.environ.get("FFBUILD_PREFIX") or os.environ.get("VARIANT") != "lgpl":
        raise RuntimeError("Use the pinned win64-lgpl image")
    SOURCES.mkdir(parents=True, exist_ok=True)
    shutil.copy2(ROOT / "dependencies/libmpv-build.json", OUT)
    shutil.copy2(ROOT / "dependencies/libmpv-container-sources.json", OUT)
    shutil.copy2(__file__, OUT)
    os.environ["SOURCE_DATE_EPOCH"] = str(int(datetime.datetime.fromisoformat(PINS["sourceDate"]).timestamp()))
    os.environ.update(TZ="UTC", LC_ALL="C.UTF-8", GIT_TERMINAL_PROMPT="0")
    checkout("mpv", "/build/mpv")
    mpv = Path("/build/mpv")
    script = mpv / "ci/build-mingw64-full.sh"
    common = mpv / "ci/build-common.sh"
    for old, new in [
        ('$gitclone https://github.com/FFmpeg/FFmpeg.git ffmpeg', 'mpv_win_checkout ffmpeg ffmpeg'),
        ('$gitclone -b v2.1 https://github.com/LuaJIT/LuaJIT.git', 'mpv_win_checkout LuaJIT LuaJIT'),
        ('$gitclone https://github.com/saitoha/libsixel.git', 'mpv_win_checkout libsixel libsixel'),
        ('wget -q "https://github.com/microsoft/cppwinrt/archive/${cppwinrt_ver}.tar.gz" -O - | tar -xz',
         'mpv_win_checkout cppwinrt "cppwinrt-$cppwinrt_ver"'),
        ('git clone --depth=1 --filter=blob:none --sparse -b "$windows_rs_ver" \\\n    https://github.com/microsoft/windows-rs.git', 'mpv_win_checkout windows-rs windows-rs'),
        ('meson wrap install mujs', ': # Verified MuJS wrap is supplied by mpv.win'),
    ]:
        replace(script, old, new)
    replace(common, 'git clone --depth=1 https://github.com/afishhh/subrandr.git',
            'mpv_win_checkout subrandr subrandr')
    replace(common, '-Dtests=true', '-Dtests=false')
    replace(common, 'cargo xtask install', 'CARGO_NET_OFFLINE=true cargo xtask install')
    mujs = PINS["mujs"]
    cache = mpv / "subprojects/packagecache"
    cache.mkdir(parents=True, exist_ok=True)
    for key, filename in [("source", "mujs-1.3.8.tar.gz"), ("patch", "mujs_1.3.8-1_patch.zip")]:
        target = SOURCES / filename
        with urllib.request.urlopen(mujs[key], timeout=180) as response, target.open("wb") as output:
            shutil.copyfileobj(response, output)
        with target.open("rb") as file:
            if hashlib.file_digest(file, "sha256").hexdigest() != mujs[key + "Sha256"]:
                raise RuntimeError("MuJS checksum mismatch")
        shutil.copy2(target, cache / filename)
    (mpv / "subprojects/mujs.wrap").write_text(
        "[wrap-file]\ndirectory = mujs-1.3.8\nsource_filename = mujs-1.3.8.tar.gz\n"
        + "source_url = " + mujs["source"] + "\nsource_hash = " + mujs["sourceSha256"]
        + "\npatch_filename = mujs_1.3.8-1_patch.zip\npatch_url = " + mujs["patch"]
        + "\npatch_hash = " + mujs["patchSha256"]
        + "\n[provide]\ndependency_names = mujs\nprogram_names = mujs, mujs-pp\n")
    with (OUT / "build-script.patch").open("wb") as output:
        run("git", "diff", "--binary", cwd=mpv, stdout=output)
    shutil.copy2(mpv / "subprojects/mujs.wrap", OUT)
    helper = str(Path(__file__).resolve())
    run("bash", "-c", 'mpv_win_checkout() { python3 "' + helper
        + '" checkout "$@"; }; export -f mpv_win_checkout; bash ci/build-mingw64-full.sh', cwd=mpv)
    run("git", "diff", "--exit-code", "--", "Cargo.lock", cwd=mpv / "subrandr")
    shutil.copytree(mpv / "artifact-libmpv", OUT / "libmpv")
    for name in PINS["sources"]:
        path = mpv if name == "mpv" else mpv / ("cppwinrt-2.0.250303.1" if name == "cppwinrt" else name)
        with (OUT / (name + "-changes.patch")).open("wb") as output:
            run("git", "diff", "--binary", cwd=path, stdout=output)
    shutil.copy2(mpv / "ffmpeg/config.h", OUT / "ffmpeg-config.h")
    shutil.copy2(mpv / "ffmpeg/ffbuild/config.mak", OUT / "ffmpeg-config.mak")
    for name in ["meson-info", "meson-logs"]:
        shutil.copytree(mpv / "build" / name, OUT / name)
    checksums = {}
    for path in sorted(OUT.rglob("*")):
        # tee is still writing this log outside the container until we exit.
        if path.is_file() and path != OUT / "build.log":
            with path.open("rb") as file:
                checksums[str(path.relative_to(OUT))] = hashlib.file_digest(file, "sha256").hexdigest()
    (OUT / "sha256.json").write_text(json.dumps(checksums, indent=2) + "\n")


if __name__ == "__main__":
    if len(sys.argv) == 4 and sys.argv[1] == "checkout":
        checkout(sys.argv[2], sys.argv[3])
    else:
        build()
