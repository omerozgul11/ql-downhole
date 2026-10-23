#!/usr/bin/env python3
"""Builds the Downhole Studio client package: one zip with the installer, field guide, training video,
brochure and user guide, datasheet, example field records and licence notices (like the MASW Studio package).

    python3 scripts/package-client.py [--version 1.0.0]

Inputs (build them first):
  artifacts/installer/DownholeStudio-Setup-<version>.exe                scripts/build.sh
  docs/marketing/*.pdf                                                   marketing/*/build.py
  artifacts/training-video-<version>/DownholeStudio-Training-<version>.mp4/.srt   marketing/training
Output: artifacts/client-package/DownholeStudio-<version>-Client-Package.zip and SHA256SUMS.txt
"""
import argparse, hashlib, re, shutil, subprocess, sys, zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open('rb') as f:
        for block in iter(lambda: f.read(1 << 20), b''):
            h.update(block)
    return h.hexdigest()


def video_minutes(path: Path) -> str:
    try:
        sys.path.insert(0, str(ROOT / 'marketing/training'))
        import imageio_ffmpeg  # noqa: F401  (available in the video tool environment)
        ffmpeg = imageio_ffmpeg.get_ffmpeg_exe()
    except ImportError:
        ffmpeg = shutil.which('ffmpeg')
    if ffmpeg:
        out = subprocess.run([ffmpeg, '-hide_banner', '-i', str(path)], capture_output=True, text=True).stderr
        m = re.search(r'Duration: (\d+):(\d+):(\d+)', out)
        if m:
            seconds = int(m[1]) * 3600 + int(m[2]) * 60 + int(m[3])
            return f'{seconds / 60:.1f}'.rstrip('0').rstrip('.')
    raise SystemExit(f'Cannot read the duration of {path} (ffmpeg not found).')


def main() -> None:
    props = (ROOT / 'Directory.Build.props').read_text()
    parser = argparse.ArgumentParser()
    parser.add_argument('--version', default=re.search(r'<VersionPrefix>([^<]+)<', props)[1])
    version = parser.parse_args().version
    name = f'DownholeStudio-{version}-Client-Package'
    video_dir = ROOT / f'artifacts/training-video-{version}'
    video, captions = video_dir / f'DownholeStudio-Training-{version}.mp4', video_dir / f'DownholeStudio-Training-{version}.srt'
    installer = ROOT / f'artifacts/installer/DownholeStudio-Setup-{version}.exe'
    marketing = ROOT / 'docs/marketing'
    examples = ROOT / 'artifacts/testenv/test-data/Clean survey'

    files = {
        f'1 - Install Downhole Studio/DownholeStudio-Setup-{version}.exe': installer,
        '2 - Field Guide (set up your equipment)/QuakeLogic-Downhole-Studio-Field-Guide.pdf': marketing / 'QuakeLogic-Downhole-Studio-Field-Guide.pdf',
        '3 - Training Video/Downhole-Studio-Training-Video.mp4': video,
        '3 - Training Video/Downhole-Studio-Training-Video.srt': captions,
        '4 - Brochure and User Guide/Downhole-Studio-Brochure-and-User-Guide.pdf': marketing / 'Downhole-Studio-Brochure-and-User-Guide.pdf',
        '5 - Product Datasheet/QuakeLogic-Downhole-Studio-Datasheet.pdf': marketing / 'QuakeLogic-Downhole-Studio-Datasheet.pdf',
        'Licence and notices/Licence agreement (EULA).txt': ROOT / 'packaging/windows/EULA.txt',
        'Licence and notices/Third-party notices.txt': ROOT / 'THIRD_PARTY_NOTICES.txt',
    }
    if not examples.is_dir():
        raise SystemExit(f'Missing {examples}: run "dotnet run --project tools/DownholeStudio.Cli -- synth --dataset Clean --out \'{examples}\'".')
    for f in sorted(examples.iterdir()):
        files[f'6 - Example Field Records/{f.name}'] = f
    missing = [str(src) for src in files.values() if not src.is_file()]
    if missing:
        raise SystemExit('Missing inputs:\n  ' + '\n  '.join(missing))
    newest_source = max(p.stat().st_mtime for p in (ROOT / 'src').rglob('*.cs'))
    if installer.stat().st_mtime < newest_source:
        raise SystemExit(f'{installer.name} is older than the source code; rebuild it with scripts/build.sh.')

    template = (ROOT / 'packaging/client/START HERE.txt').read_text()
    lines = template.replace('{version}', version).replace('{minutes}', video_minutes(video)).splitlines()
    lines[1] = '=' * len(lines[0])  # underline the title
    start_here = '\r\n'.join(lines) + '\r\n'  # Windows line endings for Notepad

    sums = ''.join(f'{sha256(src)}  {arc}\n' for arc, src in files.items())
    out = ROOT / 'artifacts/client-package'; out.mkdir(parents=True, exist_ok=True)
    target = out / f'{name}.zip'
    with zipfile.ZipFile(target, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        z.writestr(f'{name}/START HERE.txt', start_here)
        for arc, src in files.items():
            # Installer and video are already compressed; store them to keep extraction fast.
            kind = zipfile.ZIP_STORED if src.suffix in ('.exe', '.mp4') else zipfile.ZIP_DEFLATED
            z.write(src, f'{name}/{arc}', compress_type=kind)
        z.writestr(f'{name}/SHA256SUMS.txt', sums.replace('\n', '\r\n'))
    with zipfile.ZipFile(target) as z:
        assert z.testzip() is None
    (out / 'SHA256SUMS.txt').write_text(f'{sha256(target)}  {target.name}\n')
    print(f'{target} ({target.stat().st_size / 1e6:.1f} MB, {len(files) + 2} files)')


if __name__ == '__main__':
    main()
