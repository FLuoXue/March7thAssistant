"""Build the self-contained Windows RDP host (SDK needed only when building)."""
import argparse
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / 'native' / 'March7th.Desktop'
OUTPUT = ROOT / 'build' / 'desktop-session'


def ensure_helper(force=False):
    executable = OUTPUT / 'March7th.Desktop.exe'
    sources = [*PROJECT.glob('*.cs'), *PROJECT.glob('*.csproj'), PROJECT / 'app.manifest']
    if not force and executable.exists() and all(path.stat().st_mtime <= executable.stat().st_mtime for path in sources):
        return executable
    dotnet = shutil.which('dotnet')
    if not dotnet:
        raise RuntimeError('源码运行桌面分身需要 .NET 8 或更高版本 SDK。发行版已自带分身程序。')
    OUTPUT.mkdir(parents=True, exist_ok=True)
    subprocess.run([dotnet, 'publish', str(PROJECT / 'March7th.Desktop.csproj'), '-c', 'Release',
                    '-r', 'win-x64', '--self-contained', 'true', '-o', str(OUTPUT), '--nologo'],
                   cwd=ROOT, check=True)
    shutil.copyfile(ROOT / 'LICENSE', OUTPUT / 'LICENSE')
    shutil.copyfile(PROJECT / 'THIRD_PARTY_NOTICES.md', OUTPUT / 'THIRD_PARTY_NOTICES.md')
    return executable


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--force', action='store_true')
    args = parser.parse_args()
    if sys.platform != 'win32':
        parser.error('桌面分身仅支持 Windows')
    print(ensure_helper(args.force))
