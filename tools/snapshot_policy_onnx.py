"""
Freeze the freshest trained ONNX as a versioned snapshot policy file.

Renames/copies the newest non-baseline `RacingDriver-*.onnx` (or
`RacingDriver.onnx`) under `Assets/Resources/AiDriver/Policies/` to
`RacingDriver-v<N>.onnx`, then prunes intermediate step-numbered
checkpoints so the folder doesn't accumulate stale weights.

Pairs with `docs/snapshot-version.md` step 3: instead of hand-copying
the ONNX into the right name, run this. The version-id (e.g. `v2`) must
match the `runtime.onnxResourcePath` recorded in the snapshot manifest
at `Assets/_Bootstrap/Configs/Versions/<id>.json`.

Pruning rules — files KEPT under `Assets/Resources/AiDriver/Policies/`:
  * `RacingDriver.onnx`             (rolling latest from the supervisor)
  * `RacingDriver-baseline.onnx`    (shipped reference)
  * `RacingDriver-v<N>.onnx`        (every per-version snapshot)

Anything else matching `RacingDriver-<digits>.onnx` is removed along
with its `.meta`. Custom-named checkpoints are left alone — only the
auto-step-suffix pattern gets pruned.

Usage:
    python tools/snapshot_policy_onnx.py --version v2
    python tools/snapshot_policy_onnx.py --version v2 --source RacingDriver-8199814.onnx
    python tools/snapshot_policy_onnx.py --version v2 --keep-intermediates
    python tools/snapshot_policy_onnx.py --version v2 --dry-run
"""

from __future__ import annotations

import argparse
import re
import shutil
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_POLICY_DIR = REPO_ROOT / "Assets" / "Resources" / "AiDriver" / "Policies"

VERSION_RE = re.compile(r"^v\d+$")
INTERMEDIATE_RE = re.compile(r"^RacingDriver-(\d+)\.onnx$")
SNAPSHOT_RE = re.compile(r"^RacingDriver-v\d+\.onnx$")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument(
        "--version",
        required=True,
        help="Snapshot version id (e.g. `v2`). Lower-case `v` + digits only.",
    )
    parser.add_argument(
        "--source",
        default=None,
        help=(
            "Filename inside the policies dir to freeze. Defaults to the newest "
            "non-baseline non-snapshot `RacingDriver-*.onnx` (falling back to "
            "`RacingDriver.onnx`)."
        ),
    )
    parser.add_argument(
        "--dest-dir",
        default=str(DEFAULT_POLICY_DIR),
        help="Override the Resources/AiDriver/Policies directory.",
    )
    parser.add_argument(
        "--keep-intermediates",
        action="store_true",
        help="Skip the prune step. Leaves every `RacingDriver-<digits>.onnx` in place.",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Print planned actions without modifying any files.",
    )
    return parser.parse_args()


def pick_source(policy_dir: Path, target_name: str) -> Path | None:
    """Newest-mtime ONNX in `policy_dir` that's eligible to be frozen."""
    candidates: list[Path] = []
    for entry in policy_dir.glob("RacingDriver-*.onnx"):
        name = entry.name
        if name == "RacingDriver-baseline.onnx":
            continue
        if SNAPSHOT_RE.match(name):
            continue
        if name == target_name:
            continue
        candidates.append(entry)

    if candidates:
        return max(candidates, key=lambda p: p.stat().st_mtime)

    rolling = policy_dir / "RacingDriver.onnx"
    if rolling.exists():
        return rolling
    return None


def freeze_snapshot(src: Path, dst: Path, dry_run: bool) -> None:
    action = "would copy" if dry_run else "copying"
    print(f"[snapshot] {action} {src.name} -> {dst.name}", flush=True)
    if dry_run:
        return
    dst.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(src, dst)


def prune_intermediates(policy_dir: Path, dry_run: bool) -> int:
    removed = 0
    for entry in policy_dir.glob("RacingDriver-*.onnx"):
        if not INTERMEDIATE_RE.match(entry.name):
            continue
        action = "would remove" if dry_run else "removing"
        print(f"[snapshot] {action} intermediate {entry.name}", flush=True)
        if not dry_run:
            entry.unlink()
            meta = entry.with_suffix(entry.suffix + ".meta")
            if meta.exists():
                meta.unlink()
        removed += 1
    return removed


def main() -> int:
    args = parse_args()

    version_id = args.version.strip().lower()
    if not VERSION_RE.match(version_id):
        print(
            f"[snapshot] --version must look like `v2` (got `{args.version}`).",
            file=sys.stderr,
        )
        return 2

    policy_dir = Path(args.dest_dir).resolve()
    if not policy_dir.exists():
        print(f"[snapshot] dest dir not found: {policy_dir}", file=sys.stderr)
        return 2

    target_name = f"RacingDriver-{version_id}.onnx"
    target_path = policy_dir / target_name

    if args.source:
        src = (policy_dir / args.source).resolve()
        if not src.exists():
            print(f"[snapshot] --source not found: {src}", file=sys.stderr)
            return 2
    else:
        picked = pick_source(policy_dir, target_name)
        if picked is None:
            print(
                f"[snapshot] no eligible ONNX found in {policy_dir} - train a "
                "policy or pass --source.",
                file=sys.stderr,
            )
            return 1
        src = picked

    freeze_snapshot(src, target_path, args.dry_run)

    if args.keep_intermediates:
        print("[snapshot] prune skipped (--keep-intermediates).", flush=True)
    else:
        removed = prune_intermediates(policy_dir, args.dry_run)
        if removed == 0:
            print("[snapshot] no intermediate checkpoints to prune.", flush=True)

    if args.dry_run:
        print("[snapshot] dry-run complete - nothing written.", flush=True)
    else:
        print(
            f"[snapshot] done. {target_name} now pairs with "
            f"Assets/_Bootstrap/Configs/Versions/{version_id}.json - verify "
            "`runtime.onnxResourcePath` matches `AiDriver/Policies/RacingDriver-"
            f"{version_id}`.",
            flush=True,
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
