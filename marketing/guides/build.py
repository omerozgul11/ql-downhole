"""Build the Downhole Studio customer guide PDFs (QuakeLogic house format) for the client package and the product page.

    Downhole-Studio-Quick-Start-and-Activation.pdf   docs/QUICK-START.md
    Downhole-Studio-User-Guide.pdf                   docs/USER-GUIDE.md with the figures of the brochure
    Downhole-Studio-Methods-and-Validation.pdf       SCIENTIFIC-VALIDATION.md, docs/algorithms, docs/validation

Output: docs/marketing/. Needs a Python with Playwright + Chromium and Pillow (see marketing/common/house.py), e.g.:
    /opt/apps/masw-studio/engine/.venv/bin/python marketing/guides/build.py [quick-start|user-guide|methods ...]
"""
from pathlib import Path
import html
import importlib.util
import json
import re
import sys

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / 'common'))
import house  # noqa: E402

# marketing/brochure/build.py supplies the screenshot crops and the illustrated user guide.
_spec = importlib.util.spec_from_file_location('dhs_brochure', HERE.parent / 'brochure/build.py')
brochure = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(brochure)

ROOT = house.ROOT
OUTPUT = ROOT / 'docs/marketing'
VERSION = house.version()

EXTRA_CSS = """
.gdoc .tip{border-left:4px solid var(--orange);background:#fdf3ec;padding:8px 12px;margin:6px 0 10px;font-size:11.4px;line-height:1.5;color:#333;}
.gdoc li ul{margin:3px 0 2px;}
.gdoc .tbl td{text-align:left;vertical-align:top;}
.gdoc .tbl.wide th{font-size:8.4px;padding:4px 4px;}
.gdoc .tbl.wide td{font-size:8.6px;line-height:1.3;padding:3px 4px;}
.gdoc .figure{display:block;margin:6px 0 12px;}
.gdoc .figure .ph{width:100%;}
.gdoc .figure .cap{margin-top:6px;}
.gdoc .part{font-family:'Oswald';font-weight:700;font-size:26px;color:var(--ink);border-bottom:3px solid var(--orange);padding-bottom:6px;margin:4px 0 12px;}
.cover .machine{bottom:46px;left:70px;width:690px;}
.toc{columns:2;column-gap:26px;margin-top:10px;}
.toc div{font-size:12px;line-height:1.9;color:#333;break-inside:avoid;}
.toc b{color:var(--orange2);font-family:'Oswald';font-weight:600;margin-right:6px;}
"""


def document(*, title: str, cover_sub: str, tag: str, gtitle: str, h1: str, lead: str, run: str, intro: str, body: str) -> str:
    return f'''<!DOCTYPE html><html lang="en"><head><meta charset="UTF-8"><title>Downhole Studio — {html.escape(title)}</title>
<link rel="stylesheet" href="datasheet.css"><link rel="stylesheet" href="brochure-extra.css"><style>{EXTRA_CSS}</style></head><body>

<section class="page cover">
  <div class="greypanel"></div>
  <div class="cornerlogo"><img src="quakelogic_logo.png" alt="QuakeLogic"></div>
  <div class="ktitle"><div class="brand">QUAKELOGIC</div><div class="sub">{cover_sub}</div></div>
  <div class="tag">{tag}</div>
  <div class="ver">DOWNHOLE STUDIO {VERSION} · WINDOWS 10 / 11</div>
  <div class="vname">DOWNHOLE STUDIO</div>
  <div class="machine">{house.frame('cover.jpg')}</div>
</section>

<section class="page guide-template" data-guide="first"><div class="pad"><div class="gdoc">
  <div class="gtitle">{gtitle}</div><h1>{html.escape(h1)}</h1>
  <div class="lead">{lead}</div>{intro}
</div></div>{house.footer(run)}</section>

{house.back_page('DOWNHOLE STUDIO', house.ABOUT, '<div class="machine">' + house.frame('cover.jpg') + '</div>')}

<div id="guide-src" style="display:none">{body}</div>
<script>{house.PAGINATE}</script></body></html>'''


def toc(source: str) -> str:
    heads = re.findall(r'^## (\d+)\. (.+)$', source, flags=re.M)
    return '<div class="toc">' + ''.join(f'<div><b>{n}</b>{html.escape(t)}</div>' for n, t in heads) + '</div>'


# ---------------------------------------------------------------- documents
def quick_start() -> str:
    source = (ROOT / 'docs/QUICK-START.md').read_text('utf-8')
    body = house.markdown(re.sub(r'^# .*\n', '', source))
    return document(
        title='Quick Start and Activation', cover_sub='Quick<br>Start &amp;<br>Activation',
        tag='<b>INSTALL · ACTIVATE · FIRST SURVEY</b>Install Downhole Studio, activate it without an internet connection and see your first results in about fifteen minutes.',
        gtitle='QUICK START', h1='Install, activate and start', lead=f'DOWNHOLE STUDIO {VERSION} · NO INTERNET CONNECTION NEEDED',
        run='DOWNHOLE STUDIO QUICK START', intro=toc(source), body=body)


def user_guide() -> str:
    source = (ROOT / 'docs/USER-GUIDE.md').read_text('utf-8')
    return document(
        title='User Guide', cover_sub='Downhole<br>Studio<br>User Guide',
        tag='<b>EVERY PAGE, STEP BY STEP</b>From raw SEG-2 records to reviewed picks, a velocity model, Vs30 and a client-ready report.',
        gtitle='USER GUIDE', h1='Downhole Studio user guide', lead=f'VERSION {VERSION} · ALSO IN THE APPLICATION: PRESS F1',
        run='DOWNHOLE STUDIO USER GUIDE', intro=toc(source), body=brochure.guide_html())


ALGORITHMS = ['signal-processing', 'picking', 'trigger-correction', 'velocity', 'layers', 'engineering', 'quality-control']


def customer_text(source: str) -> str:
    """Removes developer references (source files, build commands, repository paths) from a methods document."""
    source = re.sub(r'^Code: (?:`[^`]*`(?:, applied by |, )?)+\.\s*', '', source, flags=re.M)
    source = source.replace('(see trigger-correction.md)', '(see Reference geophone and trigger correction)')
    source = source.replace('see engineering.md', 'see Vs30, engineering parameters and density')
    source = source.replace(' (`dhs validate`)', '')
    source = re.sub(r'`dhs validate --out docs/validation` \(run by `scripts/build.sh`\) produces\n\[docs/validation/validation-report\.md\]\([^)]*\) and a JSON copy\.',
                    'Every release is checked by an automated validation run, reproduced in full in Part 3 of this document.', source)
    return source


def demote(source: str) -> str:
    """'# Title' becomes an h2 and '## Section' an h3, so each methods document is a chapter of Part 2."""
    source = re.sub(r'^## ', '### ', source, flags=re.M)
    return re.sub(r'^# ', '## ', source, flags=re.M)


def methods() -> str:
    validation = customer_text((ROOT / 'SCIENTIFIC-VALIDATION.md').read_text('utf-8'))
    algorithms = [customer_text((ROOT / f'docs/algorithms/{name}.md').read_text('utf-8')) for name in ALGORITHMS]
    report = customer_text((ROOT / 'docs/validation/validation-report.md').read_text('utf-8'))
    units = ('Units: all calculations use SI units (m, s, m/s, kg/m³, Pa) in double precision. Display units are applied only '
             'in the user interface and in exports; converted values are never fed back into calculations.')
    titles = [re.match(r'# (.*)', a)[1] for a in [validation] + algorithms + [report]]

    def part(n: int, name: str) -> str:
        return f'<div class="pagebreak"></div>\n<div class="part">PART {n} · {html.escape(name.upper())}</div>'

    body = '\n'.join([
        part(1, 'Scientific validation'), house.markdown(re.sub(r'^# .*\n', '', validation)),
        part(2, 'Methods'), f'<p>{html.escape(units)}</p>', *[house.markdown(demote(a)) for a in algorithms],
        part(3, 'Validation report'), house.markdown(re.sub(r'^# .*\n', '', report)),
    ])
    body = body.replace('<table class="tbl"><thead><tr><th>Check</th>', '<table class="tbl wide"><thead><tr><th>Check</th>')
    leftovers = re.findall(r'src/|\.cs\b|scripts/|tests/|\.md\b|dhs validate', body)
    assert not leftovers, f'developer references left in the methods document: {leftovers}'
    intro = ('<p>How Downhole Studio calculates every result, the assumptions behind each method and the independent checks '
             'that verify them. Downhole Studio supports the established downhole workflow described in ASTM D7400/D7400M '
             'but does not certify compliance with any standard; results require professional review.</p>'
             '<div class="toc">' + ''.join(f'<div><b>{i}</b>{html.escape(t)}</div>' for i, t in enumerate(titles, 1)) + '</div>')
    return document(
        title='Methods and Validation', cover_sub='Methods<br>&amp;<br>Validation',
        tag='<b>EQUATIONS · ASSUMPTIONS · INDEPENDENT CHECKS</b>Every calculation with its assumptions and limitations, and the validation report for this version.',
        gtitle='TECHNICAL REFERENCE', h1='Methods and scientific validation', lead=f'DOWNHOLE STUDIO {VERSION} · FOR REVIEWING ENGINEERS',
        run='DOWNHOLE STUDIO METHODS AND VALIDATION', intro=intro, body=body)


DOCUMENTS = {
    'quick-start': ('Downhole-Studio-Quick-Start-and-Activation.pdf', quick_start),
    'user-guide': ('Downhole-Studio-User-Guide.pdf', user_guide),
    'methods': ('Downhole-Studio-Methods-and-Validation.pdf', methods),
}


def build(names: list[str]) -> None:
    for name in names or DOCUMENTS:
        filename, make = DOCUMENTS[name]
        work = ROOT / f'artifacts/marketing/guides/{name}'
        house.stage(work)
        brochure.BUILD = work  # the screenshot crops (cover and figures) go into this document's assets
        brochure.prepare_images()
        page = work / f'{name}.html'
        page.write_text(make(), 'utf-8')
        pages = house.render(page, OUTPUT / filename)
        issues = house.layout_issues(page)
        print(json.dumps({'pdf': f'docs/marketing/{filename}', 'pages': pages, 'bytes': (OUTPUT / filename).stat().st_size, 'layout_issues': issues}))


if __name__ == '__main__':
    build(sys.argv[1:])
