"""Prepare child configuration before importing module.config or creating Qt."""
import os
from pathlib import Path


def prepare_child_environment(root):
    root = Path(root)
    destination = root / 'config' / 'desktop-session' / 'config.yaml'
    destination.parent.mkdir(parents=True, exist_ok=True)
    source = root / 'config.yaml'
    if not source.exists():
        source = root / 'assets' / 'config' / 'config.example.yaml'
    initial_config = source.read_bytes()
    try:
        with destination.open('xb') as target:
            target.write(initial_config)
    except FileExistsError:
        pass
    os.environ['MARCH7TH_CONFIG_PATH'] = str(destination.resolve())
    os.environ['MARCH7TH_DESKTOP_SESSION'] = '1'
    return destination
