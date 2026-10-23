"""Build the Downhole Studio product datasheet (8-page QuakeLogic house format).

Fills marketing/datasheet/template.html (the QuakeLogic datasheet template, unchanged layout) with the
Downhole Studio content and screenshots from docs/images, renders it and runs the house layout QA
(marketing/datasheet/qa.py); the build fails unless QA passes.
Output: docs/marketing/QuakeLogic-Downhole-Studio-Datasheet.pdf

Needs a Python with Playwright + Chromium and Pillow (see marketing/common/house.py), e.g.:
    /opt/apps/masw-studio/engine/.venv/bin/python marketing/datasheet/build.py
"""
from pathlib import Path
import importlib.util
import json
import re
import sys

from PIL import Image

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / 'common'))
import house  # noqa: E402

# Shared product copy (features, specifications, gallery crops) comes from the brochure builder.
_spec = importlib.util.spec_from_file_location('dhs_brochure', HERE.parent / 'brochure/build.py')
brochure = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(brochure)

ROOT = house.ROOT
SHOTS = ROOT / 'docs/images'
BUILD = ROOT / 'artifacts/marketing/datasheet'
OUTPUT = ROOT / 'docs/marketing/QuakeLogic-Downhole-Studio-Datasheet.pdf'
NAME = 'DOWNHOLE STUDIO'

# Close-ups for the 3 x 3 interface page (1600 x 1000 screenshot space) with captions of at most four words.
CLOSEUPS = [
    ('c-diagram', 'geometry', (1210, 228, 1580, 568), 'Live borehole diagram'),
    ('c-confidence', 'p-picks', (1283, 290, 1590, 560), 'Explained pick confidence'),
    ('c-spectrum', 'waveforms', (240, 745, 540, 960), 'Raw vs processed spectrum'),
    ('c-overlay', 's-picks', (236, 705, 610, 960), 'S+ / S− comparison'),
    ('c-residuals', 'velocity-model', (700, 300, 925, 690), 'Travel-time residuals'),
    ('c-profile', 'velocity-model', (935, 300, 1250, 690), 'Layer velocity profile'),
    ('c-vs30', 'engineering', (235, 210, 615, 470), 'Vs30 from measured data'),
    ('c-verdict', 'quality-control', (235, 255, 600, 570), 'Quality verdict'),
    ('c-report', 'report', (770, 205, 1580, 960), 'Live report preview'),
]
GALLERY = ['g-waveforms', 'g-processing', 'g-ppicks', 'g-spicks', 'g-engineering', 'g-qc']

CONFIG = [
    ('Application', 'Native Windows desktop application (64-bit)'),
    ('Analysis engine', 'Built in; runs locally, no cloud service'),
    ('Project file', '.dhsx: original records, interpretation, history'),
    ('Raw data', 'Kept byte-for-byte with SHA-256 checksums'),
    ('Editing model', 'Undo / redo, snapshots and scenarios'),
    ('Saving', 'Atomic save with backup copy; recovery copies'),
    ('Instrument profiles', 'QuakeLogic 3C Borehole and custom profiles'),
    ('Interface modes', 'Guided and Expert; light and dark themes'),
    ('Help', 'Built-in user guide (F1) and command palette'),
    ('Sample project', 'Complete synthetic survey with a known answer'),
    ('Updates', 'Signed, verified in-app updates'),
    ('Licensing', 'Per computer; 14-day trial; activation key'),
]
CAPACITY = [
    ('Operating system', 'Windows 10 (21H2 or later) or Windows 11, 64-bit'),
    ('Memory', '8 GB RAM recommended'),
    ('Disk space', 'About 200 MB plus projects'),
    ('Administrator rights', 'Not required (per-user installation)'),
    ('Internet', 'To start the trial or activate; then 30 days offline'),
    ('Seismic input', 'SEG-2 rev. 1; 16/32-bit integer, 32/64-bit float'),
    ('Report and tables', 'PDF; Excel (.xlsx) and CSV at full precision'),
    ('Figures', 'PNG (any pixel size), SVG, PDF; clipboard'),
    ('Audit record', 'JSON: files, processing, picks, methods, version'),
]
OPTIONS = [
    ('FIELD GUIDE', 'Step-by-step borehole, source and recording procedure, with checklists and a printable field log sheet.'),
    ('TRAINING VIDEO', 'A narrated, captioned walkthrough of the complete workflow, from import to the finished report.'),
    ('LICENCE TRANSFER', 'Deactivate on one computer and activate on another; projects are never locked to a licence.'),
    ('QUAKELOGIC SUPPORT', 'Email support@quakelogic.net with the diagnostic package. Email response time: 3 business days.'),
]
OVERVIEW = ('Downhole Studio is QuakeLogic\'s Windows software for downhole seismic testing. It takes SEG-2 records from a '
            'borehole survey through validated import, non-destructive processing and trigger correction, explained P and S '
            'picking, layer interpretation with four velocity methods, elastic moduli and Vs30 to a branded report. Raw data '
            'are never modified and every step is recorded.')


def prepare_images() -> None:
    assets = BUILD / 'assets'
    brochure.BUILD = BUILD
    brochure.prepare_images()  # cover, hero, insets and gallery crops
    for name, source, box, _ in CLOSEUPS:
        image = Image.open(SHOTS / f'{source}.png').convert('RGB')
        scale = image.width / 1600
        image = image.crop(tuple(round(v * scale) for v in box))
        image.save(assets / f'{name}.jpg', quality=90, optimize=True)


def rows(pairs):
    return ''.join(f'<tr><td class="k">{k}</td><td>{v}</td></tr>' for k, v in pairs)


def document() -> str:
    t = (HERE / 'template.html').read_text('utf-8')
    frame = house.frame('cover.jpg')
    values = {
        'PRODUCT_CATEGORY': 'Downhole<br>Seismic<br>Software',
        'PRODUCT_CATEGORY_ONELINE': 'DOWNHOLE SEISMIC ANALYSIS SOFTWARE',
        'PRODUCT_NAME': NAME,
        'HERO_PHOTO': 'hero.jpg', 'INSET_A': 'inset-wave.jpg', 'INSET_B': 'inset-profile.jpg',
        'OVERVIEW_HEADLINE': 'FROM FIELD RECORDS TO A DEFENSIBLE VS PROFILE',
        'OVERVIEW_PARAGRAPH': OVERVIEW,
        'BENEFIT_1': 'Validated import: every file is inspected before it is accepted, and corrections are applied only when the analyst approves them.',
        'BENEFIT_2': 'Picks you can explain: each automatic pick carries a confidence score built from signal quality, sharpness and agreement.',
        'BENEFIT_3': 'Honest results: density is never assumed, Vs30 is never silently extrapolated and invalid moduli are withheld and flagged.',
        'BENEFIT_4': 'Client-ready output: branded PDF reports, full-precision Excel tables, figures and an audit record from one project file.',
        'STANDARD_FEATURES_LI': brochure.li(brochure.STANDARD),
        'KEY_FEATURES_LI': brochure.li(brochure.KEY),
        'PRODUCT_FEATURE_GROUPS': ''.join(f'<div class="pfgroup"><div class="t">{a}</div><div class="v">{b}</div></div>' for a, b in brochure.GROUPS),
        'CONFIG_TABLE_TITLE': 'SOFTWARE CONFIGURATION',
        'CONFIG_ROWS': rows(CONFIG),
        'CAPACITY_TABLE_TITLE': 'SYSTEM REQUIREMENTS &amp; DATA',
        'CAPACITY_ROWS': rows(CAPACITY),
        'OPTION_CARDS': ''.join(f'<div class="optbox"><div class="cap">{a}</div><p>{b}</p></div>' for a, b in OPTIONS),
        'SPEC_ROWS_LEFT': rows(brochure.SPECS_L),
        'SPEC_ROWS_RIGHT': rows(brochure.SPECS_R),
        'GALLERY_CELLS': ''.join(f'<div class="cell"><img src="assets/{g}.jpg"></div>' for g in GALLERY),
        'COMPONENT_CELLS': ''.join(f'<div><div class="ph"><img src="assets/{n}.jpg"></div><div class="cap">{c}</div></div>' for n, _, _, c in CLOSEUPS),
    }
    # The cover and back-cover artwork is a framed screenshot (software, not a machine cut-out).
    t = t.replace('<div class="machine"><img src="assets/{{HERO_CUTOUT}}"></div>', f'<div class="machine">{frame}</div>')
    t = t.replace('OPTIONAL FEATURES &amp; ACCESSORIES', 'INCLUDED WITH DOWNHOLE STUDIO')
    t = t.replace('PRECISION COMPONENTS &amp; CONFIGURATION', 'INTERFACE CLOSE-UPS')
    t = t.replace('Scan to View Product:', 'Scan to View Products:')
    t = t.replace('available configurations of the {{PRODUCT_NAME}}', 'licensing of Downhole Studio')
    for key, value in values.items():
        t = t.replace('{{' + key + '}}', value)
    assert '{{' not in t, 'unfilled placeholder: ' + re.search(r'\{\{[A-Z_]+\}\}', t).group(0)
    t = t.replace('<head>', '<head>\n<title>Downhole Studio — Datasheet</title>', 1)
    css = ('<link rel="stylesheet" href="brochure-extra.css"><style>'
           '.cover .machine{bottom:46px;left:70px;width:690px;}'
           '.comp .ph img{object-fit:cover;object-position:left top;background:#fff;}'
           '.gal .cell img{object-position:left top;}</style>')
    t = t.replace('<link rel="stylesheet" href="datasheet.css">', '<link rel="stylesheet" href="datasheet.css">' + css)
    ready = ('<script>(async () => { await document.fonts.ready; await Promise.all([...document.images].map(i => i.complete ? 0 : '
             'new Promise(r => { i.onload = i.onerror = r; }))); window.__ready = true; })();</script>')
    return t.replace('</body>', ready + '\n</body>')


def build() -> None:
    house.stage(BUILD)
    prepare_images()
    text = document()
    assert '$' not in re.sub(r'<script>.*?</script>', '', text, flags=re.S), 'Datasheets never carry prices'
    html_path = BUILD / 'datasheet.html'
    html_path.write_text(text, 'utf-8')
    issues = house.layout_issues(html_path)
    if issues:
        for issue in issues:
            print(issue)
        raise SystemExit(f'Layout QA failed with {len(issues)} issue(s).')
    pages = house.render(html_path, OUTPUT)
    print(json.dumps({'pdf': str(OUTPUT.relative_to(ROOT)), 'pages': pages, 'bytes': OUTPUT.stat().st_size, 'qa': 'PASS'}))


if __name__ == '__main__':
    build()
