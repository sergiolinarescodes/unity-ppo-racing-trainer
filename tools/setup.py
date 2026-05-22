"""
One-time bootstrap for the PPO racing trainer Python venv.

Usage:
    python tools/setup.py            # CPU build
    python tools/setup.py --cuda     # NVIDIA cu121 build
    python tools/setup.py --ml-agents-repo ../ml-agents    # editable install from local clone

Creates `tools/python/.venv/` and installs torch + mlagents + psutil
per `tools/requirements.txt`. Cross-platform: Windows, macOS, Linux.

After this completes, `python tools/train_unattended.py` auto-resolves
the venv interpreter and launches training.
"""

from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
import venv
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[1]
VENV_DIR = REPO_ROOT / "tools" / "python" / ".venv"
REQUIREMENTS = REPO_ROOT / "tools" / "requirements.txt"


def find_python_310() -> str | None:
    """Return a launcher command for Python 3.10, or None if unavailable.

    Order: explicit `py -3.10` on Windows; `python3.10` everywhere; current
    interpreter as last resort (validated to be >= 3.10).
    """
    candidates: list[list[str]] = []
    if sys.platform == "win32":
        candidates.append(["py", "-3.10"])
    candidates.append(["python3.10"])
    if sys.platform != "win32":
        candidates.append(["python3"])
    for cmd in candidates:
        exe = cmd[0]
        if shutil.which(exe) is None:
            continue
        try:
            out = subprocess.run(
                cmd + ["-c", "import sys; print(sys.version_info[:2])"],
                capture_output=True,
                text=True,
                timeout=10,
                check=False,
            )
            if out.returncode == 0 and "(3, 10)" in out.stdout:
                return " ".join(cmd)
        except (subprocess.SubprocessError, OSError):
            continue
    # Fall back to the current interpreter if it is 3.10.
    if sys.version_info[:2] == (3, 10):
        return sys.executable
    return None


def install_hint() -> str:
    if sys.platform == "win32":
        return "winget install Python.Python.3.10"
    if sys.platform == "darwin":
        return "brew install python@3.10"
    return "sudo apt install python3.10 python3.10-venv  (or distro equivalent)"


def venv_python(venv_dir: Path) -> Path:
    if sys.platform == "win32":
        return venv_dir / "Scripts" / "python.exe"
    return venv_dir / "bin" / "python"


def run(cmd: list[str], **kwargs) -> None:
    print(f"  $ {' '.join(str(c) for c in cmd)}", flush=True)
    subprocess.run(cmd, check=True, **kwargs)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--cuda", action="store_true", help="Install NVIDIA cu121 torch build instead of CPU.")
    parser.add_argument(
        "--ml-agents-repo",
        default=None,
        help="Optional path to a local ml-agents clone for editable install. Default: install mlagents from PyPI.",
    )
    args = parser.parse_args()

    print(f"PPO racing trainer venv bootstrap")
    print(f"  repo root: {REPO_ROOT}")
    print(f"  venv:      {VENV_DIR}")
    print(f"  host:      {sys.platform}")

    py310 = find_python_310()
    if py310 is None:
        print(f"\nERROR: Python 3.10 not found on PATH.")
        print(f"Install it first:  {install_hint()}")
        return 2
    print(f"  python:    {py310}")

    if VENV_DIR.exists():
        print(f"\nVenv already exists at {VENV_DIR} — reusing it.")
    else:
        print(f"\nCreating venv at {VENV_DIR} ...")
        VENV_DIR.parent.mkdir(parents=True, exist_ok=True)
        # Use the 3.10 launcher to create the venv (sys.executable might be different).
        run([*py310.split(), "-m", "venv", str(VENV_DIR)])

    py = venv_python(VENV_DIR)
    if not py.exists():
        print(f"ERROR: venv python not at expected path: {py}")
        return 1

    print("\nUpgrading pip + wheel ...")
    run([str(py), "-m", "pip", "install", "--upgrade", "pip", "wheel"])

    print("\nInstalling torch ...")
    if args.cuda:
        run([
            str(py), "-m", "pip", "install",
            "torch==2.2.2", "--index-url", "https://download.pytorch.org/whl/cu121",
        ])
    else:
        run([str(py), "-m", "pip", "install", "torch==2.2.2"])

    print("\nInstalling remaining requirements ...")
    # Skip torch since we just installed it explicitly above.
    run([str(py), "-m", "pip", "install", "-r", str(REQUIREMENTS), "--no-deps"])
    # Resolve transitive deps for mlagents in a separate pass so version constraints
    # in requirements.txt take precedence over mlagents' relaxed pins.
    run([str(py), "-m", "pip", "install", "-r", str(REQUIREMENTS)])

    if args.ml_agents_repo:
        repo = Path(args.ml_agents_repo).resolve()
        if not repo.exists():
            print(f"ERROR: --ml-agents-repo path does not exist: {repo}")
            return 1
        ml_agents_pkg = repo / "ml-agents"
        if not ml_agents_pkg.exists():
            print(f"ERROR: expected {ml_agents_pkg} (the inner ml-agents package) — pass the repo root.")
            return 1
        print(f"\nInstalling mlagents (editable) from {ml_agents_pkg} ...")
        run([str(py), "-m", "pip", "install", "-e", str(ml_agents_pkg)])

    print("\nVerifying mlagents-learn is callable ...")
    run([str(py), "-m", "mlagents.trainers.learn", "--help"], stdout=subprocess.DEVNULL)

    print("\nSetup complete.")
    print(f"  - Venv at {VENV_DIR}")
    print(f"  - Run training:  python tools/train_unattended.py  (auto-uses venv)")
    print(f"  - Run dashboard: python tools/dashboard/server.py  (uses your system python)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
