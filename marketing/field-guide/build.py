"""Build the Downhole Studio field guide PDF (borehole, sources, recording and analysis for first-time users).

Input: docs/FIELD-GUIDE.md and the diagrams in diagrams.py, in the QuakeLogic house style (marketing/common).
Output: docs/marketing/QuakeLogic-Downhole-Studio-Field-Guide.pdf

Needs a Python with Playwright + Chromium and Pillow (see marketing/common/house.py), e.g.:
    /opt/apps/masw-studio/engine/.venv/bin/python marketing/field-guide/build.py
"""
from pathlib import Path
import html
import json
import re
import sys

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / 'common'))
sys.path.insert(0, str(HERE))
import house  # noqa: E402
from diagrams import DIAGRAMS, borehole  # noqa: E402

ROOT = house.ROOT
SOURCE = ROOT / 'docs/FIELD-GUIDE.md'
BUILD = ROOT / 'artifacts/marketing/field-guide'
OUTPUT = ROOT / 'docs/marketing/QuakeLogic-Downhole-Studio-Field-Guide.pdf'

EXTRA_CSS = """
.gdoc .tip{border-left:4px solid var(--orange);background:#fdf3ec;padding:8px 12px;margin:6px 0 10px;font-size:11.4px;line-height:1.5;color:#333;}
.gdoc .diagram{margin:6px 0 12px;}
.gdoc .diagram svg{display:block;width:100%;height:auto;}
.gdoc .diagram .cap{font-family:'Oswald';font-weight:600;font-size:11px;color:var(--grey);letter-spacing:.5px;text-transform:uppercase;border-left:3px solid var(--orange);padding-left:9px;margin-top:4px;}
.gdoc .tbl th{font-size:10.6px;}
.gdoc .tbl td{font-size:10.8px;line-height:1.35;text-align:left;vertical-align:top;}
.gdoc .tbl td:first-child{white-space:normal;}
.gdoc li ul{margin:3px 0 2px;}
.gdoc .fg-sheet td{height:31px;}
.gdoc .fg-sheet th:nth-child(1){width:13%;} .gdoc .fg-sheet th:nth-child(5){width:33%;}
.gdoc .sheetline{font-size:11.4px;line-height:2.2;color:#333;margin:0;}
.cover .fieldcard{position:absolute;left:46px;bottom:56px;width:724px;background:#fff;border-radius:10px;padding:14px 18px 8px;box-shadow:0 14px 30px rgba(0,0,0,.28);}
.cover .fieldcard svg{display:block;width:100%;height:auto;}
.toc{columns:2;column-gap:26px;margin-top:10px;}
.toc div{font-size:12px;line-height:1.9;color:#333;break-inside:avoid;}
.toc b{color:var(--orange2);font-family:'Oswald';font-weight:600;margin-right:6px;}
"""


def body_html() -> str:
    body = house.markdown(SOURCE.read_text('utf-8'))
    body = re.sub(r'^<h1>.*?</h1>\n', '', body)
    body = body.replace('<table class="tbl"><thead><tr><th>Depth (m)</th>', '<table class="tbl fg-sheet"><thead><tr><th>Depth (m)</th>')
    # The two fill-in lines under the field log sheet.
    body = re.sub(r'<p>(Site: _+.*?)</p>', r'<p class="sheetline">\1</p>', body)
    body = re.sub(r'<p>(P offset: _+.*?)</p>', r'<p class="sheetline">\1</p>', body)
    body = re.sub(r'<p>(Sample interval: _+.*?)</p>', r'<p class="sheetline">\1</p>', body)

    def diagram(m):
        make, caption = DIAGRAMS[m.group(1)]
        return f'<div class="diagram">{make()}<div class="cap">{html.escape(caption)}</div></div>'

    body = re.sub(r'<p>\{\{diagram:([a-z]+)\}\}</p>', diagram, body)
    assert '{{' not in body, 'unknown diagram marker'
    return body


def toc() -> str:
    heads = re.findall(r'^## (\d+)\. (.+)$', SOURCE.read_text('utf-8'), flags=re.M)
    return ''.join(f'<div><b>{n}</b>{html.escape(t)}</div>' for n, t in heads)


def document() -> str:
    version = house.version()
    safety = ('<h4>Safety first</h4><p>The hammer operator wears ear defenders, safety glasses, gloves and safety boots. '
              'Never strike the beam while anyone is near the vehicle wheels.</p>'
              '<h4>About QuakeLogic:</h4><p>QuakeLogic Inc. is a California-based engineering and technology company '
              'providing innovative, reliable, high-quality solutions to customers worldwide.</p>')
    return f'''<!DOCTYPE html><html lang="en"><head><meta charset="UTF-8"><title>Downhole Studio — Field Guide</title>
<link rel="stylesheet" href="datasheet.css"><link rel="stylesheet" href="brochure-extra.css"><style>{EXTRA_CSS}</style></head><body>

<section class="page cover">
  <div class="greypanel"></div>
  <div class="cornerlogo"><img src="quakelogic_logo.png" alt="QuakeLogic"></div>
  <div class="ktitle"><div class="brand">QUAKELOGIC</div><div class="sub">Downhole<br>Field<br>Guide</div></div>
  <div class="tag"><b>PREPARE · RECORD · ANALYSE</b>Borehole, sources, seismograph settings and Downhole Studio, step by step, for your first downhole survey.</div>
  <div class="ver">DOWNHOLE STUDIO {version} · WINDOWS</div>
  <div class="vname">DOWNHOLE STUDIO</div>
  <div class="fieldcard">{borehole()}</div>
</section>

<section class="page guide-template" data-guide="first"><div class="pad"><div class="gdoc">
  <div class="gtitle">FIELD GUIDE</div><h1>From the borehole to a Vs report</h1>
  <div class="lead">FOR FIRST-TIME DOWNHOLE USERS · DOWNHOLE STUDIO {version}</div>
  <div class="toc">{toc()}</div>
</div></div>{house.footer('DOWNHOLE FIELD GUIDE')}</section>

{house.back_page('DOWNHOLE STUDIO', safety)}

<div id="guide-src" style="display:none">{body_html()}</div>
<script>{house.PAGINATE}</script></body></html>'''


def build() -> None:
    house.stage(BUILD)
    (BUILD / 'field-guide.html').write_text(document(), 'utf-8')
    pages = house.render(BUILD / 'field-guide.html', OUTPUT)
    print(json.dumps({'pdf': str(OUTPUT.relative_to(ROOT)), 'pages': pages, 'bytes': OUTPUT.stat().st_size}))


if __name__ == '__main__':
    build()
