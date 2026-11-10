#!/usr/bin/env bash
# Repacks Android arm64 JRE tarballs (from build-android-jre*.yml) into the asset layout PojavLauncher expects:
#   <out>/jre/      (Java 8)   <out>/jre-new/ (Java 17)   <out>/jre-21/ (Java 21)
# each holding universal.tar.xz (architecture-independent files), bin-arm64.tar.xz (arch-specific) and a version file.
# Logic follows repackjre.sh from PojavLauncherTeam/android-openjdk-build-multiarch, reduced to arm64.
# Usage: repack-jre-arm64.sh <dir with jreN-arm64-*release.tar.xz> <assets/components dir>
set -euo pipefail
in="$(realpath "$1")"; out="$(realpath -m "$2")"
version_tag="${RUNTIME_VERSION:-$(date +%Y%m%d)}"
declare -A target=( [8]=jre [17]=jre-new [21]=jre-21 )
found=0
for v in 8 17 21; do
  tarball="$(find "$in" -name "jre${v}-arm64-*release.tar.xz" | head -1 || true)"
  [[ -n "$tarball" ]] || { echo "JRE $v: нет архива в $in, пропускаю"; continue; }
  found=1
  dest="$out/${target[$v]}"; rm -rf "$dest"; mkdir -p "$dest"
  work="$(mktemp -d)"; work1="$(mktemp -d)"

  # universal part: everything that is not architecture specific
  tar xf "$tarball" -C "$work"
  ( cd "$work"
    rm -rf bin lib/server lib/client lib/jexec lib/jvm.cfg release
    find . -name '*.so' -delete
    XZ_OPT="-6 --threads=0" tar cJf "$dest/universal.tar.xz" * )
  rm -rf "$work"/*

  # arch part: launcher binaries, VM, and every shared library flattened into lib/
  tar xf "$tarball" -C "$work"
  ( cd "$work"
    mkdir -p "$work1/lib"
    mv bin "$work1/"
    mv lib/jexec lib/jvm.cfg "$work1/lib/"
    for vm in server client; do [[ -d lib/$vm ]] && mv "lib/$vm" "$work1/lib/"; done
    find . -name '*.so' -execdir mv {} "$work1/lib/{}" \;
    mv release "$work1/release" )
  XZ_OPT="-6 --threads=0" tar cJf "$dest/bin-arm64.tar.xz" -C "$work1" .
  echo "$version_tag-$v" > "$dest/version"
  rm -rf "$work" "$work1"
  echo "JRE $v -> $dest ($(du -sh "$dest" | cut -f1))"
done
[[ $found -eq 1 ]] || echo "ВНИМАНИЕ: ни одной JRE не найдено; приложение соберётся без Java."
