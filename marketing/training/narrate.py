"""Generate the training narration with the offline Kokoro voice (CPU only, no network).

    /opt/apps/masw-studio/.tools/marketing-video/.venv/bin/python marketing/training/narrate.py
"""
import json, sys
from pathlib import Path
import numpy as np, onnxruntime as ort, soundfile as sf
from kokoro_onnx import Kokoro
sys.path.insert(0, str(Path(__file__).parent))
from common import STORY, OUT, TOOLS, written

audio = OUT / 'audio'; audio.mkdir(parents=True, exist_ok=True)
settings = ort.SessionOptions(); settings.intra_op_num_threads = 4; settings.inter_op_num_threads = 1
session = ort.InferenceSession(str(TOOLS / 'models/kokoro-v1.0.onnx'), settings, providers=['CPUExecutionProvider'])
voice = Kokoro.from_session(session, str(TOOLS / 'models/voices-v1.0.bin'))
manifest = []
for seg in STORY['segments']:
    target = audio / f"{seg['id']}.wav"
    samples, rate = voice.create(seg['speech'], voice=STORY['voice'], speed=STORY['voice_speed'], lang=STORY['voice_language'])
    assert np.isfinite(samples).all() and len(samples) > rate
    peak = float(np.max(np.abs(samples)))
    if peak > 0.95:
        samples = samples * (0.95 / peak)
    sf.write(target, samples, rate, subtype='PCM_16')
    manifest.append({'id': seg['id'], 'file': target.name, 'rate': rate, 'seconds': round(len(samples) / rate, 3), 'caption': written(seg['speech'])})
    print(seg['id'], manifest[-1]['seconds'], flush=True)
(audio / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
print('total', round(sum(m['seconds'] for m in manifest), 1))
