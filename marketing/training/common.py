"""Shared settings for the Downhole Studio training video.

Tools (not part of this repository): a Python environment with kokoro-onnx, onnxruntime, soundfile,
numpy, Pillow and imageio-ffmpeg, plus the Kokoro model files and Inter fonts. By default the MASW Studio
marketing-video toolkit on the build host is used; set DHS_VIDEO_TOOLS to another folder with the same
layout (models/kokoro-v1.0.onnx, models/voices-v1.0.bin, fonts/Inter-*.ttf).
"""
from pathlib import Path
import json, os, re

ROOT = Path(__file__).resolve().parents[2]
STORY = json.loads((Path(__file__).parent / 'storyboard.json').read_text())
OUT = ROOT / 'artifacts' / f"training-video-{STORY['version']}"
TOOLS = Path(os.environ.get('DHS_VIDEO_TOOLS', '/opt/apps/masw-studio/.tools/marketing-video'))

# Narration is written for the speech engine; captions show the written forms.
WRITTEN = [
    (r'\bQuake Logic\b', 'QuakeLogic'), (r'\bV s 30\b', 'Vs30'), (r'\bS E G two\b', 'SEG-2'), (r'\bA I C\b', 'AIC'),
    (r'\bP D F\b', 'PDF'), (r'\bC S V\b', 'CSV'), (r'\bP N G\b', 'PNG'), (r'\bS V G\b', 'SVG'), (r'\bQ C\b', 'QC'),
    (r'\bfourteen day\b', '14-day'), (r'\bband pass\b', 'band-pass'), (r'\bshot to shot\b', 'shot-to-shot'),
    (r'\bsignal to noise\b', 'signal-to-noise'), (r'\btravel time\b', 'travel-time'), (r'\brefracted ray\b', 'refracted-ray'),
    (r'\bbyte for byte\b', 'byte-for-byte'), (r'\bone over the velocity\b', '1 / velocity'), (r'\blicence\b', 'license'),
]


def written(text: str) -> str:
    for pattern, replacement in WRITTEN:
        text = re.sub(pattern, replacement, text)
    return text


def sentences(text: str):
    return [s.strip() for s in re.split(r'(?<=[.!?])\s+', text) if s.strip()]
