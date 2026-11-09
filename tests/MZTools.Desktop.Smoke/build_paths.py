"""Locate SDK assets in the shared layout (or an explicit --artifacts-path)."""
import os
from pathlib import Path
import platform

ROOT = Path(__file__).resolve().parents[2]


def assets_path(project):
    base = Path(os.environ['MZTOOLS_ASSETS_ROOT']) if os.environ.get('MZTOOLS_ASSETS_ROOT') else ROOT / 'obj/artifacts' / platform.system().lower()
    return base / 'obj' / Path(project).name / 'project.assets.json'
