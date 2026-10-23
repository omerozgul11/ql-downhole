"""Render the narrated Downhole Studio training video (1920 x 1080, H.264/AAC).

Inputs: audio from narrate.py and frames from the TrainingCapture UI test (capture.json). The application
frames fill the top 960 px; the caption band below shows the chapter and the narration as captions. A pointer
moves to and clicks the control that leads to each shot, and detail shots zoom in smoothly.
Outputs in artifacts/training-video-<version>/: the MP4, SRT captions, chapter list and poster.

    /opt/apps/masw-studio/.tools/marketing-video/.venv/bin/python marketing/training/render.py
"""
from pathlib import Path
import json, subprocess, sys
import numpy as np
import soundfile as sf
import imageio_ffmpeg
from PIL import Image, ImageDraw, ImageFont

sys.path.insert(0, str(Path(__file__).parent))
from common import ROOT, OUT, STORY, TOOLS, written, sentences

FFMPEG = imageio_ffmpeg.get_ffmpeg_exe()
W, H, UI_H, FPS = 1920, 1080, 960, 30
BAND = H - UI_H
ORANGE, WHITE, MUTED, INK = (241, 96, 34), (245, 246, 248), (168, 177, 191), (18, 21, 27)
CHAPTER_CARD, LEAD_IN, MOVE, FADE, ZOOM = 2.4, 0.3, 0.85, 0.3, 1.1
VERSION = STORY['version']
NAME = f'DownholeStudio-Training-{VERSION}'

capture = json.loads((OUT / 'capture.json').read_text())
assert capture['status'] == 'passed', 'Run the TrainingCapture test to completion first.'
assert (capture['width'], capture['height']) == (W, UI_H)
shots = {s['id']: s for s in capture['shots']}
audio = {a['id']: a for a in json.loads((OUT / 'audio/manifest.json').read_text())}


def font(size, weight=500):
    return ImageFont.truetype(str(TOOLS / 'fonts' / f'Inter-{weight}.ttf'), size)


def wrap(draw, text, f, width):
    rows, row = [], ''
    for word in text.split():
        candidate = (row + ' ' + word).strip()
        if row and draw.textlength(candidate, font=f) > width:
            rows.append(row); row = word
        else:
            row = candidate
    return rows + ([row] if row else [])


PROBE = ImageDraw.Draw(Image.new('RGB', (10, 10)))


def caption_chunks(text):
    """Sentences, split further at commas so every caption fits two lines."""
    f, out = font(31), []
    for s in sentences(text):
        if len(wrap(PROBE, s, f, 1560)) <= 2:
            out.append(s); continue
        cur = ''
        for part in [p.strip() for p in s.split(', ')]:
            candidate = (cur + ', ' + part) if cur else part
            if cur and len(wrap(PROBE, candidate, f, 1560)) > 2:
                out.append(cur + ','); cur = part
            else:
                cur = candidate
        out.append(cur)
    return out


def band(chapter, text):
    img = Image.new('RGB', (W, BAND), INK); d = ImageDraw.Draw(img)
    d.rectangle([0, 0, W, 3], fill=ORANGE)
    if chapter:
        d.text((40, 12), written(chapter).upper(), font=font(16, 700), fill=ORANGE)
    rows = wrap(d, text, font(31), 1560) if text else []
    y = 38 + (2 - len(rows)) * 20
    for row in rows:
        d.text(((W - d.textlength(row, font=font(31))) / 2, y), row, font=font(31), fill=WHITE); y += 40
    return img


def background(height):
    top, bottom = np.array([17, 20, 27.]), np.array([33, 38, 49.])
    mix = np.linspace(0, 1, height)[:, None, None]
    return Image.fromarray(np.broadcast_to(top * (1 - mix) + bottom * mix, (height, W, 3)).astype(np.uint8).copy())


MARK = Image.open(ROOT / 'src/DownholeStudio.App/Assets/quakelogic-mark.png').convert('RGBA')


def card(kind, chapter='', height=UI_H):
    img = background(height); d = ImageDraw.Draw(img)

    def centre(text, y, size, weight=500, fill=WHITE):
        f = font(size, weight); d.text(((W - d.textlength(text, font=f)) / 2, y), text, font=f, fill=fill)

    def logo(y, side):
        m = MARK.resize((side, side), Image.LANCZOS); img.paste(m, (W // 2 - side // 2, y), m)

    if kind == 'intro':
        logo(150, 130)
        centre('QuakeLogic', 305, 40, 700)
        centre('Downhole Studio', 390, 96, 700)
        centre('Training: from SEG-2 records to a layered velocity model, Vs30 and a report', 525, 36, 500, MUTED)
        centre(f'Version {VERSION}  ·  Windows 10 / 11', 595, 26, 500, ORANGE)
    elif kind == 'outro':
        logo(120, 110)
        centre('Downhole Studio', 265, 72, 700)
        centre('Download · 14-day free trial', 375, 34, 600, ORANGE)
        centre('github.com/omerozgul11/ql-downhole', 427, 32, 500)
        centre('Sales: sales@quakelogic.net  ·  +1 (916) 899-0391', 515, 30, 500, MUTED)
        centre('Support: support@quakelogic.net  ·  products.quakelogic.net', 563, 30, 500, MUTED)
        centre('QuakeLogic Inc.  ·  Roseville, California', 645, 26, 500, MUTED)
    else:
        number, title = written(chapter).split(' · ', 1)
        top = (height - 200) // 2
        centre(f'CHAPTER {number}', top, 30, 700, ORANGE)
        centre(title, top + 50, 76, 700)
        d.rectangle([W // 2 - 60, top + 165, W // 2 + 60, top + 169], fill=ORANGE)
    return img


# ---------------------------------------------------------------- pointer

def pointer_sprite(scale=1.0):
    s = int(36 * scale)
    img = Image.new('RGBA', (s, s), (0, 0, 0, 0)); d = ImageDraw.Draw(img)
    k = s / 36
    arrow = [(2, 2), (2, 27), (9, 21), (14, 32), (19, 30), (14, 19), (23, 19)]
    d.polygon([(x * k, y * k) for x, y in arrow], fill=(255, 255, 255, 255), outline=(20, 20, 24, 255))
    d.line([(x * k, y * k) for x, y in arrow + [arrow[0]]], fill=(20, 20, 24, 255), width=max(1, int(2 * k)))
    return img


POINTER = pointer_sprite()


def ease(t):
    t = min(max(t, 0.0), 1.0)
    return t * t * (3 - 2 * t)


ZOOMED, COMPOSED = {}, {}


def view_of(img, zoom, amount):
    """The UI image, zoomed towards region `zoom` by `amount` (0 = full view, 1 = region)."""
    if not zoom or amount <= 0:
        return img, (0, 0, W, UI_H)
    x, y, w, h = zoom
    a = ease(amount)
    box = (x * a, y * a, W + (x + w - W) * a, UI_H + (y + h - UI_H) * a)
    key = (id(img), tuple(zoom))
    if amount >= 1 and key in ZOOMED:
        return ZOOMED[key], box
    out = img.crop(tuple(int(round(v)) for v in box)).resize((W, UI_H), Image.BILINEAR if amount < 1 else Image.LANCZOS)
    if amount >= 1:
        ZOOMED[key] = out
    return out, box


def to_screen(pt, box):
    x0, y0, x1, y1 = box
    return ((pt[0] - x0) * W / (x1 - x0), (pt[1] - y0) * UI_H / (y1 - y0))


# ---------------------------------------------------------------- timeline

class Timeline:
    def __init__(self):
        self.events, self.clock, self.track, self.srt, self.chapters = [], 0.0, [], [], []

    def add(self, duration, draw, chapter, captions):
        self.events.append((self.clock, duration, draw, chapter, [(self.clock + a, self.clock + b, c) for a, b, c in captions]))
        self.srt += [(self.clock + a, self.clock + b, c) for a, b, c in captions]
        self.clock += duration


def timed_captions(sid, offset):
    spoken = audio[sid]['seconds']; chunks = caption_chunks(audio[sid]['caption'])
    weights = np.array([len(c) for c in chunks], float); weights /= weights.sum()
    edges = offset + np.concatenate([[0], np.cumsum(weights)]) * spoken
    return [(edges[i], edges[i + 1] + (0.4 if i == len(chunks) - 1 else 0), c) for i, c in enumerate(chunks)]


frames_dir = OUT / 'frames'
cache = {}


def frame(name):
    if name not in cache:
        cache[name] = Image.open(frames_dir / shots[name]['file']).convert('RGB')
    return cache[name]


timeline = Timeline()
state = {'image': None, 'pointer': None}

for seg in STORY['segments']:
    sid, chapter, kind = seg['id'], seg.get('chapter', ''), seg.get('card')
    if kind == 'chapter':
        img = card('chapter', chapter, H)
        timeline.chapters.append((timeline.clock, written(chapter)))
        timeline.add(CHAPTER_CARD, lambda t, img=img: (img, None), None, [])
    spoken = audio[sid]['seconds']
    timeline.track.append((timeline.clock + LEAD_IN, OUT / 'audio' / audio[sid]['file']))
    captions = timed_captions(sid, LEAD_IN)
    if kind in ('intro', 'outro'):
        if kind == 'intro':
            timeline.chapters.append((timeline.clock, 'Introduction'))
        img = card(kind)
        timeline.add(spoken + LEAD_IN + 1.6, lambda t, img=img: (img, None), chapter, captions)
        continue

    names = seg['shots']
    duration = max(spoken + LEAD_IN + 0.8, 2.6 * len(names))
    plan, start, share = [], 0.0, duration / len(names)
    for name in names:
        s = shots[name]
        plan.append({'name': name, 'start': start, 'end': start + share, 'pointer': s['pointer'], 'zoom': s['zoom'],
                     'before': state['image'], 'from': state['pointer']})
        state['image'] = name
        if s['pointer']:
            state['pointer'] = s['pointer']
        start += share
    def draw(t, plan=plan):
        step = next(p for p in plan if t < p['end'] or p is plan[-1])
        local = t - step['start']
        pointer, click = step['from'], None
        if step['pointer'] and step['before']:
            # Move to the control on the previous screen, click, then cross-fade to the result.
            if local < MOVE:
                a = ease(local / MOVE)
                start_pt = step['from'] or (W / 2, UI_H / 2)
                pointer = (start_pt[0] + (step['pointer'][0] - start_pt[0]) * a, start_pt[1] + (step['pointer'][1] - start_pt[1]) * a)
                if local > MOVE - 0.2:
                    click = (local - (MOVE - 0.2)) / 0.45
                return compose(frame(step['before']), None, 0, pointer, click, cacheable=False)
            local -= MOVE
            pointer = step['pointer']
            if local < 0.25:
                click = (0.2 + local) / 0.45
            if local < FADE:
                base = Image.blend(frame(step['before']), frame(step['name']), ease(local / FADE))
                return compose(base, None, 0, pointer, click, cacheable=False)
        elif step['pointer']:
            pointer = step['pointer']
        amount = (local - FADE) / ZOOM if step['zoom'] else 0
        return compose(frame(step['name']), step['zoom'], amount, pointer, click)

    timeline.add(duration, draw, chapter, captions)


def compose(ui, zoom, amount, pointer, click, cacheable=True):
    img, box = view_of(ui, zoom, amount)
    animating = zoom is not None and 0 < amount < 1
    if pointer is None and click is None and not animating:
        return img, (('static', id(img)) if cacheable else None)
    resting_key = ('pointer', id(ui), tuple(pointer), tuple(box)) if cacheable and click is None and not animating and pointer is not None else None
    if resting_key in COMPOSED:
        return COMPOSED[resting_key], resting_key
    img = img.copy()
    if pointer is not None:
        px, py = to_screen(pointer, box)
        if click is not None and 0 <= click <= 1:
            d = ImageDraw.Draw(img, 'RGBA')
            r = 10 + 34 * click
            d.ellipse([px - r, py - r, px + r, py + r], outline=ORANGE + (int(255 * (1 - click)),), width=4)
        img.paste(POINTER, (int(px) - 2, int(py) - 2), POINTER)
    if resting_key is not None:
        COMPOSED[resting_key] = img
    return img, resting_key


# ---------------------------------------------------------------- encode

REN = OUT / 'render'; REN.mkdir(exist_ok=True)
total = timeline.clock
rate = audio['intro']['rate']
mix = np.zeros(int((total + 0.5) * rate), dtype=np.float32)
for at, path in timeline.track:
    data, r = sf.read(str(path), dtype='float32'); assert r == rate
    i = int(at * rate); mix[i:i + len(data)] += data[: len(mix) - i]
sf.write(str(REN / 'narration.wav'), mix, rate, subtype='PCM_16')

video = OUT / f'{NAME}.mp4'
proc = subprocess.Popen([FFMPEG, '-hide_banner', '-loglevel', 'error', '-y', '-f', 'rawvideo', '-pix_fmt', 'rgb24', '-s', f'{W}x{H}', '-r', str(FPS),
                         '-i', '-', '-i', str(REN / 'narration.wav'), '-c:v', 'libx264', '-preset', 'medium', '-crf', '19', '-pix_fmt', 'yuv420p',
                         '-c:a', 'aac', '-b:a', '160k', '-movflags', '+faststart', '-shortest', str(video)], stdin=subprocess.PIPE)
band_cache, last_key, last_bytes = {}, None, None
for n in range(int(round(total * FPS))):
    t = n / FPS
    ev = next(e for e in timeline.events if t < e[0] + e[1] or e is timeline.events[-1])
    start, duration, draw, chapter, caps = ev
    ui, key = draw(t - start)
    caption = next((c for a, b, c in caps if a <= t < b), '')
    if chapter is None:
        frame_key = ('card', id(ui))
    else:
        frame_key = (key, chapter, caption) if key else None
    if frame_key is not None and frame_key == last_key:
        proc.stdin.write(last_bytes); continue
    if chapter is None:
        full = ui
    else:
        full = Image.new('RGB', (W, H)); full.paste(ui, (0, 0))
        bkey = (chapter, caption)
        if bkey not in band_cache:
            band_cache[bkey] = band(chapter, caption)
        full.paste(band_cache[bkey], (0, UI_H))
    last_key, last_bytes = frame_key, full.tobytes()
    proc.stdin.write(last_bytes)
    if n % (FPS * 30) == 0:
        print(f'{t:6.1f} / {total:.1f} s', flush=True)
proc.stdin.close()
assert proc.wait() == 0, 'ffmpeg failed'


def stamp(seconds, sep=','):
    ms = int(round(seconds * 1000)); h, ms = divmod(ms, 3600000); m, ms = divmod(ms, 60000); s, ms = divmod(ms, 1000)
    return f'{h:02}:{m:02}:{s:02}{sep}{ms:03}'


(OUT / f'{NAME}.srt').write_text(''.join(f'{i}\n{stamp(a)} --> {stamp(b)}\n{c}\n\n' for i, (a, b, c) in enumerate(timeline.srt, 1)))
(OUT / f'{NAME}-chapters.txt').write_text(''.join(f'{stamp(a, ".")[:8]}  {c}\n' for a, c in timeline.chapters))
poster = Image.new('RGB', (W, H)); poster.paste(card('intro'), (0, 0)); poster.paste(band('', 'Downhole seismic processing, interpretation and reporting'), (0, UI_H))
poster.save(OUT / f'{NAME}-poster.jpg', quality=92)
print(f'Wrote {video} ({total:.1f} s)')
