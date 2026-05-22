"""
Single-env, Editor-attached PPO training launcher.

Use this when you want to watch training happen visibly in the Unity Editor
instead of running headless. Press Play in Unity after this script reports
"[Editor] Listening on default port 5004".

Usage:
    python tools/train_editor.py
    python tools/train_editor.py --run-id editor-experiment-1
    python tools/train_editor.py --resume
"""

from __future__ import annotations

import argparse
import datetime as dt
import os
import subprocess
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[1]
VENV_DIR = REPO_ROOT / "tools" / "python" / ".venv"
DEFAULT_CONFIG = "Assets/_Bootstrap/Configs/MlAgents/racing_driver_visible.yaml"


def venv_python(venv_dir: Path) -> Path:
    if sys.platform == "win32":
        return venv_dir / "Scripts" / "python.exe"
    return venv_dir / "bin" / "python"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--run-id", default=None)
    parser.add_argument("--config-path", default=DEFAULT_CONFIG)
    parser.add_argument("--resume", action="store_true")
    args = parser.parse_args()

    run_id = args.run_id or f"editor-{dt.datetime.now():%Y%m%d-%H%M}"
    py = venv_python(VENV_DIR)
    if not py.exists():
        print(f"ERROR: venv missing at {VENV_DIR}. Run `python tools/setup.py` first.", flush=True)
        return 2
    if not Path(args.config_path).exists():
        print(f"ERROR: config not found: {args.config_path}", flush=True)
        return 1

    cmd = [
        str(py), "-m", "mlagents.trainers.learn",
        args.config_path,
        f"--run-id={run_id}",
        "--timeout-wait=300",
    ]
    if args.resume:
        cmd.append("--resume")

    env = os.environ.copy()
    env["PPO_RACING_RUN_ID"] = run_id
    print(f"[editor] run-id={run_id}", flush=True)
    print(f"[editor] launching: {' '.join(cmd)}", flush=True)
    return subprocess.run(cmd, env=env).returncode


if __name__ == "__main__":
    sys.exit(main())
