"""Build the Downhole Studio brochure + user guide PDF (QuakeLogic house format).

Input: the product screenshots in docs/images (rendered from the sample project by the explicit
ProductScreenshots UI test), docs/USER-GUIDE.md and the shared house style in marketing/common.
Output: docs/marketing/Downhole-Studio-Brochure-and-User-Guide.pdf

Needs a Python with Playwright + Chromium and Pillow (see marketing/common/house.py), e.g.:
    /opt/apps/masw-studio/engine/.venv/bin/python marketing/brochure/build.py
"""
from pathlib import Path
import html
import json
import re
import sys

from PIL import Image

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / 'common'))
import house  # noqa: E402

ROOT = house.ROOT
SHOTS = ROOT / 'docs/images'
BUILD = ROOT / 'artifacts/marketing/brochure'
OUTPUT = ROOT / 'docs/marketing/Downhole-Studio-Brochure-and-User-Guide.pdf'
VERSION = house.version()

# Crop boxes in the screenshots' 1600 x 1000 pixel space (None = whole window).
WORKSPACE = (214, 95, 1270, 975)       # page area beside the inspector
WIDE = (214, 95, 1600, 975)            # pages without an inspector
CROPS = {
    'cover': ('velocity-model', None, 1500),
    'hero': ('velocity-model', (236, 290, 905, 730), 1000),  # 456 x 300 aspect
    'inset-wave': ('waveforms', (430, 400, 790, 760), 420),
    'inset-profile': ('velocity-model', (935, 385, 1250, 700), 420),
    'g-waveforms': ('waveforms', WORKSPACE, 1000),
    'g-processing': ('processing', WORKSPACE, 1000),
    'g-ppicks': ('p-picks', WORKSPACE, 1000),
    'g-spicks': ('s-picks', WORKSPACE, 1000),
    'g-engineering': ('engineering', WIDE, 1000),
    'g-qc': ('quality-control', (214, 95, 1600, 700), 1000),
    'f-geometry': ('geometry', None, 900),
    'f-waveforms': ('waveforms', None, 900),
    'f-processing': ('processing', None, 900),
    'f-ppicks': ('p-picks', None, 900),
    'f-spicks': ('s-picks', None, 900),
    'f-velocity': ('velocity-model', None, 900),
    'f-engineering': ('engineering', None, 900),
    'f-qc': ('quality-control', None, 900),
    'f-report': ('report', None, 900),
}

EXTRA_CSS = """
.gdoc .tip{border-left:4px solid var(--orange);background:#fdf3ec;padding:8px 12px;margin:6px 0 10px;font-size:11.4px;line-height:1.5;color:#333;}
.gdoc li ul{margin:3px 0 2px;}
.gdoc .tbl td{text-align:left;vertical-align:top;}
.gdoc .figure{display:block;margin:6px 0 12px;}
.gdoc .figure .ph{width:100%;}
.gdoc .figure .cap{margin-top:6px;}
.cover .machine{bottom:46px;left:70px;width:690px;}
.galc .ph img{object-position:left top;}
"""


def prepare_images() -> None:
    assets = BUILD / 'assets'
    for name, (source, box, width) in CROPS.items():
        image = Image.open(SHOTS / f'{source}.png').convert('RGB')
        scale = image.width / 1600
        if box:
            image = image.crop(tuple(round(v * scale) for v in box))
        image = image.resize((width, round(image.height * width / image.width)), Image.LANCZOS)
        image.save(assets / f'{name}.jpg', quality=88, optimize=True, progressive=True)


# ---------------------------------------------------------------- user guide
GUIDE_FIGURES = {  # section heading -> (image, caption); inserted after the section's first paragraph
    '5. Geometry and channels': ('f-geometry', 'Geometry page: borehole, independent P and S sources, depth assignment and the live borehole diagram'),
    '6. Waveforms': ('f-waveforms', 'Waveform viewer with P picks, the trace inspector and the spectrum of the selected trace'),
    '7. Processing and trigger correction': ('f-processing', 'Processing chain for the vertical component with processed-over-raw preview and a filter suggestion'),
    '8. P picks': ('f-ppicks', 'P-wave picking: the inspector explains the confidence of the selected pick'),
    '9. S picks': ('f-spicks', 'S-wave picking with the S+ / inverted S− comparison at the selected depth'),
    '10. Velocity model': ('f-velocity', 'Travel-time graph with layer boundaries, residuals and the Vp / Vs profile'),
    '11. Engineering parameters and Vs30': ('f-engineering', 'Vs30 from measured data, density per layer and property profiles'),
    '12. Results and quality control': ('f-qc', 'Analysis quality verdict, summary cards and findings'),
    '13. Reports and exports': ('f-report', 'Report page with the live page-by-page preview'),
}


def guide_html() -> str:
    source = (ROOT / 'docs/USER-GUIDE.md').read_text('utf-8')
    source = re.sub(r'^# .*\n', '', source)
    source = re.sub(r'^- \[\d+\..*\n', '', source, flags=re.M)  # the linked table of contents
    blocks = house.markdown(source).split('\n')  # one block per line
    for heading, (image, caption) in GUIDE_FIGURES.items():
        tag = f'<h2>{html.escape(heading, quote=False)}</h2>'
        assert tag in blocks, 'Guide heading not found: ' + heading
        at = blocks.index(tag) + 2  # after the section's first block
        blocks.insert(at, f'<div class="figure"><div class="ph"><img src="assets/{image}.jpg"></div><div class="cap">{html.escape(caption)}</div></div>')
    body = '\n'.join(blocks)
    return body


# ---------------------------------------------------------------- content
def li(items):
    return ''.join(f'<li>{x}</li>' for x in items)


def rows(pairs):
    return ''.join(f'<tr><td class="k">{k}</td><td>{v}</td></tr>' for k, v in pairs)


STANDARD = ['SEG-2 import with validation', 'Byte-exact raw data archive', 'Channel profiles & geometry', 'Live borehole diagram',
            'Non-destructive processing', 'Butterworth & notch filters', 'Reference trigger correction', 'AIC, STA/LTA & MER pickers',
            'Explained pick confidence', 'S+ / S− difference traces', 'Polarization & rotation', 'Four velocity methods',
            'Robust Huber regression', 'Suggested layer boundaries', 'Moduli with explicit density', 'Vs30 & custom intervals',
            'Automatic quality control', 'Branded PDF report', 'Excel, CSV, PNG, SVG export', 'Undo, snapshots, scenarios']
KEY = [
    'Inspects every SEG-2 file before import — sample rates, missing channels, duplicates and depth order — and never repairs questionable data silently.',
    'Keeps the original records byte-for-byte with SHA-256 checksums; every processing step works on a copy and can be switched off, edited or undone.',
    'Corrects trigger delay and shot-to-shot jitter from reference-geophone onsets, keeping raw and corrected times side by side.',
    'Picks P and S arrivals with AIC, STA/LTA or modified energy ratio, and explains each confidence score: SNR, sharpness, agreement and consistency.',
    'Confirms shear arrivals with opposite-direction shots, (S+ − S−)/2 difference traces, polarization analysis and per-depth rotation, applied only on request.',
    'Builds the layer model with regression, pseudo-interval, true-interval or Snell\'s-law refracted-ray layer stripping, with draggable boundaries.',
    'Reports Poisson\'s ratio and moduli only with explicit density, and Vs30 from measured depth only — extrapolation is labelled everywhere.',
    'Produces a QuakeLogic-branded PDF report with methodology, QC, figures, tables and the full processing history, ready for review.',
]
GROUPS = [
    ('Data import', 'SEG-2 rev. 1, 16/32-bit integer and 32/64-bit float, either byte order; files or whole folders.'),
    ('Geometry', 'Ground or collar depth reference; independent P and S offsets; depth assignment by order or file; channel profiles.'),
    ('Processing & trigger', 'Mean/trend removal, taper, Butterworth and notch filters, AGC, balance, polarity, shifts, resampling; trigger correction.'),
    ('Picking', 'AIC, STA/LTA, MER; confidence scores; keyboard-driven review; S+ / S− comparison; cross-correlation checks.'),
    ('Velocity model', 'OLS or Huber layer regression, pseudo- and true-interval, refracted-ray stripping; BIC boundary suggestions.'),
    ('Engineering', 'Vp/Vs, ν, G, E, K; measured, constant or labelled empirical density; Vs30 and custom intervals.'),
    ('Outputs', 'Branded PDF report, Excel and CSV tables, PNG / SVG / PDF figures, JSON audit record, .dhsx project.'),
]
WORKFLOW = [
    ('Project', 'Name the survey, job number and date; snapshots, scenarios and archive'),
    ('Import', 'Drag SEG-2 files or folders; review the inspection before importing'),
    ('Geometry', 'Borehole, P and S source offsets, depths and shot types, channel map'),
    ('Waveforms', 'Inspect every trace raw or processed; gain, polarity, exclusions'),
    ('Processing', 'Processing chains per component; reference onsets and trigger correction'),
    ('P picks · S picks', 'Automatic picking, then review of every depth with explained confidence'),
    ('Velocity model', 'Layer boundaries on the travel-time graph; choice of velocity method'),
    ('Engineering', 'Density, elastic moduli, Vs30 and custom time-averaged intervals'),
    ('Results', 'Quality verdict, findings with links, scenario comparison, history'),
    ('Report', 'Client, site and branding; preview, PDF, Excel, CSV and figures'),
]
DATA = [
    ('Seismic input', 'SEG-2 (.sg2, .seg2, .dat); integer and floating-point samples'),
    ('Project file', '.dhsx with original records, interpretation and history'),
    ('Report', 'QuakeLogic-branded PDF, Letter or A4'),
    ('Tables', 'Excel (.xlsx) at full precision and CSV'),
    ('Figures', 'PNG at any pixel size, SVG, PDF; clipboard'),
    ('Audit', 'JSON record of files, processing, picks, methods and version'),
]
CARDS = [
    ('GUIDED MODE', 'Explains each page and recommends defaults; Expert mode shows every parameter. The project is identical in both.'),
    ('BUILT-IN HELP', 'Press F1 on any page for its section of the user guide, including shortcuts, methods and troubleshooting.'),
    ('SAMPLE PROJECT', 'A complete synthetic survey with a known answer: open it from the start screen to see every page with content.'),
    ('QUAKELOGIC SUPPORT', 'Email support@quakelogic.net with the diagnostic package from Settings. Email response time: 3 business days.'),
]
SPECS_L = [
    ('Platform', 'Windows 10 (21H2+) or 11, 64-bit desktop'),
    ('Memory / disk', '8 GB RAM recommended; about 200 MB'),
    ('Installation', 'Per-user setup, no administrator rights'),
    ('Seismic format', 'SEG-2 revision 1, either byte order'),
    ('Sample types', '16/32-bit integer, 32/64-bit float'),
    ('Not supported', '20-bit packed (code 3), reported'),
    ('Channel layout', 'Vertical, X, Y, reference; saved profiles'),
    ('Depth reference', 'Ground surface or collar with stick-up'),
    ('Filters', 'Butterworth HP / LP / BP, notch; causal or zero-phase'),
    ('Other steps', 'Detrend, taper, AGC, balance, shift, resample'),
    ('Trigger', 'Subtract reference onset or align to median'),
    ('Pickers', 'AIC, STA/LTA, modified energy ratio'),
    ('S-wave tools', 'S+ / S− difference, stacking, rotation'),
    ('Correlation', 'Opposite shots and adjacent depths'),
]
SPECS_R = [
    ('Ray geometry', 'Straight-ray correction or Snell\'s law'),
    ('Velocity methods', 'Layer regression, pseudo- and true-interval, refracted-ray'),
    ('Regression', 'Ordinary least squares or Huber (robust)'),
    ('Layer suggestion', 'Piecewise-linear segmentation, BIC'),
    ('Elastic properties', 'Vp/Vs, ν, G, E, K per layer'),
    ('Density', 'Measured, constant, Gardner or Brocher'),
    ('Vs30', 'Measured depth only; extrapolation labelled'),
    ('Quality control', 'Automatic checks; Good / Review / Incomplete'),
    ('Report', 'Branded PDF with processing history'),
    ('Exports', 'XLSX, CSV, PNG, SVG, PDF, JSON audit'),
    ('Editing', 'Undo / redo, snapshots, scenarios'),
    ('Safety', 'Atomic save, backup copy, recovery'),
    ('Licensing', 'Per computer; offline activation key, no internet needed'),
    ('Validation', '87 / 87 checks; Vs30 within 0.2–1.2 %'),
]
GALLERY = [
    ('g-waveforms', 'Waveforms', 'Every trace at every depth with picks, gain and the trace inspector.'),
    ('g-processing', 'Processing', 'Per-component chain, processed over raw, spectrum and filter suggestion.'),
    ('g-ppicks', 'P picks', 'Automatic picks reviewed depth by depth, with a travel-time preview.'),
    ('g-spicks', 'S picks', 'S+ and inverted S− compared at the selected depth before accepting.'),
    ('g-engineering', 'Engineering', 'Vs30 from measured data and elastic property profiles per layer.'),
    ('g-qc', 'Quality control', 'A plain-language verdict with findings linked to depths and layers.'),
]


def document() -> str:
    galc = ''.join(f'<div><div class="ph"><img src="assets/{i}.jpg"></div><div class="cap">{c}</div><div class="d">{d}</div></div>' for i, c, d in GALLERY)
    groups = ''.join(f'<div class="pfgroup"><div class="t">{t}</div><div class="v">{v}</div></div>' for t, v in GROUPS)
    cards = ''.join(f'<div class="optbox"><div class="cap">{t}</div><p>{p}</p></div>' for t, p in CARDS)
    return f'''<!DOCTYPE html><html lang="en"><head><meta charset="UTF-8"><title>Downhole Studio — Brochure and User Guide</title>
<link rel="stylesheet" href="datasheet.css"><link rel="stylesheet" href="brochure-extra.css"><style>{EXTRA_CSS}</style></head><body>

<section class="page cover">
  <div class="greypanel"></div>
  <div class="cornerlogo"><img src="quakelogic_logo.png" alt="QuakeLogic"></div>
  <div class="ktitle"><div class="brand">QUAKELOGIC</div><div class="sub">Downhole<br>Seismic<br>Software</div></div>
  <div class="tag"><b>DOWNHOLE P- AND S-WAVE ANALYSIS</b>From raw SEG-2 records to reviewed picks, a validated velocity model, Vs30 and a professional report.</div>
  <div class="ver">VERSION {VERSION} · WINDOWS 10 / 11</div>
  <div class="vname">DOWNHOLE STUDIO</div>
  <div class="machine">{house.frame('cover.jpg')}</div>
</section>

<section class="page"><div class="pad">
  <div class="h1">DOWNHOLE STUDIO</div><div class="h1sub">DOWNHOLE SEISMIC ANALYSIS SOFTWARE</div>
  <div class="ov-media"><div class="ov-hero"><img src="assets/hero.jpg"></div>
    <div class="ov-insets"><div class="circle"><img src="assets/inset-wave.jpg"></div><div class="circle"><img src="assets/inset-profile.jpg"></div></div></div>
  <div class="ov-body"><div class="ov-left">
    <h2>FROM FIELD RECORDS TO A DEFENSIBLE VS PROFILE</h2>
    <p>Downhole Studio is QuakeLogic's Windows application for downhole seismic testing. It takes SEG-2 records from a borehole survey through validated import, geometry, non-destructive processing and trigger correction, automatic and manual P and S picking, layer interpretation with four velocity methods, elastic moduli and Vs30, to quality control and a branded report. Raw data are never modified and every step is recorded.</p>
    <ul>{li([
        'Validated import: every file is inspected before it is accepted, and corrections are applied only when the analyst approves them.',
        'Picks you can explain: each automatic pick carries a confidence score built from signal quality, sharpness and agreement.',
        'Honest results: density is never assumed, Vs30 is never silently extrapolated and invalid moduli are withheld and flagged.',
        'Client-ready output: branded PDF reports, full-precision Excel tables, figures and an audit record from one project file.'])}</ul>
  </div><div class="sidebar"><h3>STANDARD FEATURES &raquo;</h3><ul>{li(STANDARD)}</ul></div></div>
</div>{house.footer()}</section>

<section class="page"><div class="pad"><div class="p3">
  <div class="kf"><div class="lab">KEY FEATURES</div><ul>{li(KEY)}</ul></div>
  <div class="pf"><div class="bar">PRODUCT FEATURES</div>{groups}
    <div class="note">For more information about features, licensing and training for Downhole Studio, please contact QuakeLogic.</div></div>
</div></div>{house.footer()}</section>

<section class="page"><div class="pad">
  <div class="seclabel">WORKFLOW — ONE PAGE PER STEP · THE NEXT BUTTON SHOWS THE WAY</div>
  <table class="tbl left"><thead><tr><th style="width:26%">Page</th><th>What you do</th></tr></thead><tbody>{rows(WORKFLOW)}</tbody></table>
  <div class="seclabel" style="margin-top:16px;">DATA, PROJECTS &amp; OUTPUTS</div>
  <table class="tbl left"><thead><tr><th style="width:26%">Item</th><th>Supported</th></tr></thead><tbody>{rows(DATA)}</tbody></table>
  <div class="optwrap"><div class="opth">HELP &amp; SUPPORT BUILT IN</div><div class="optgrid">{cards}</div></div>
</div>{house.footer()}</section>

<section class="page"><div class="pad">
  <div class="seclabel">TECHNICAL SPECIFICATIONS</div>
  <div class="spec2"><div class="col"><table><thead><tr><th>Specification</th><th>Description</th></tr></thead><tbody>{rows(SPECS_L)}</tbody></table></div>
  <div class="col"><table><thead><tr><th>Specification</th><th>Description</th></tr></thead><tbody>{rows(SPECS_R)}</tbody></table></div></div>
  <p class="intro" style="margin-top:18px;">Licensing: per-computer licences with online activation; paid licences work offline for 30 days between checks. Without an active licence, projects still open read-only and are never modified. Downhole Studio supports the established downhole workflow described in ASTM D7400/D7400M but does not certify compliance with any standard; results require professional review.</p>
</div>{house.footer()}</section>

<section class="page"><div class="pad">
  <div class="seclabel">PRODUCT GALLERY</div>
  <p class="intro">Screenshots show Downhole Studio {VERSION} with the bundled sample project, a synthetic survey with a known answer; they are product illustrations, not a site interpretation.</p>
  <div class="galc">{galc}</div>
</div>{house.footer()}</section>

<section class="page guide-template" data-guide="first"><div class="pad"><div class="gdoc">
  <div class="gtitle">USER GUIDE</div><h1>Downhole Studio user guide</h1>
  <div class="lead">VERSION {VERSION} · ALSO IN THE APPLICATION: PRESS F1</div>
</div></div>{house.footer('DOWNHOLE STUDIO USER GUIDE')}</section>

{house.back_page('DOWNHOLE STUDIO', house.ABOUT, '<div class="machine">' + house.frame('cover.jpg') + '</div>')}

<div id="guide-src" style="display:none">{guide_html()}</div>
<script>{house.PAGINATE}</script></body></html>'''


def build() -> None:
    house.stage(BUILD)
    prepare_images()
    text = document()
    assert '$' not in re.sub(r'<script>.*?</script>', '', text, flags=re.S), 'Brochures never carry prices'
    (BUILD / 'brochure.html').write_text(text, 'utf-8')
    pages = house.render(BUILD / 'brochure.html', OUTPUT)
    print(json.dumps({'pdf': str(OUTPUT.relative_to(ROOT)), 'pages': pages, 'bytes': OUTPUT.stat().st_size, 'version': VERSION}))


if __name__ == '__main__':
    build()
