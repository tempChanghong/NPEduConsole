"""Package Illustrator PNG exports; create ICO and previews; verify real bytes."""
from pathlib import Path
from PIL import Image, ImageColor, ImageDraw, ImageFont
import tempfile, struct, io, json, xml.etree.ElementTree as ET

ROOT=Path(__file__).resolve().parents[1]
RENDERS=Path(tempfile.gettempdir())/'NPEduConsole-branding-renders'
PNG=ROOT/'png'; PNG.mkdir(exist_ok=True)
SIZES=[16,24,32,48,64,256]
for variant,sizes in [('standard',[1024,512,256,128]),('small',[16,24,32,48,64,256]),('mono',[16,24,32,48,64,256]),('mono-light',[16,24,32,48,64,256])]:
    for n in sizes:
        im=Image.open(RENDERS/f'{variant}-{n}.png').convert('RGBA')
        expected=n*4 if n<=64 else n
        assert im.size==(expected,expected), (variant,n,im.size)
        if n<=64: im=im.resize((n,n),Image.Resampling.LANCZOS)
        # Clear the <= 4/255 alpha ringing created by Lanczos at very small sizes.
        pix=list(im.get_flattened_data()); im.putdata([(0,0,0,0) if p[3]<=4 else p for p in pix])
        name=f'npeduconsole-{n}.png' if variant=='standard' or (variant=='small' and n<=64) else f'npeduconsole-{variant}-{n}.png'
        im.save(PNG/name)

def dib(im):
    n=im.width
    xor=im.transpose(Image.Transpose.FLIP_TOP_BOTTOM).tobytes('raw','BGRA')
    stride=((n+31)//32)*4
    mask=bytearray(stride*n)
    alpha=im.getchannel('A')
    for y in range(n):
        for x in range(n):
            if alpha.getpixel((x,n-1-y))==0: mask[y*stride+x//8]|=1<<(7-x%8)
    header=struct.pack('<IiiHHIIiiII',40,n,2*n,1,32,0,len(xor)+len(mask),0,0,0,0)
    return header+xor+bytes(mask)

chunks=[]
for n in SIZES:
    im=Image.open(PNG/f'npeduconsole-{n}.png')
    if n==256:
        out=io.BytesIO(); im.save(out,format='PNG'); payload=out.getvalue()
    else: payload=dib(im)
    chunks.append((n,payload))
offset=6+16*len(chunks)
entries=[]
for n,payload in chunks:
    entries.append(struct.pack('<BBBBHHII',n%256,n%256,0,0,1,32,len(payload),offset))
    offset+=len(payload)
(ROOT/'npeduconsole.ico').write_bytes(struct.pack('<HHH',0,1,len(chunks))+b''.join(entries)+b''.join(p for n,p in chunks))

ico=Image.open(ROOT/'npeduconsole.ico')
assert ico.ico.sizes()=={(n,n) for n in SIZES}
checks={}
for n in SIZES:
    im=ico.ico.getimage((n,n)).convert('RGBA')
    expected=Image.open(PNG/f'npeduconsole-{n}.png').convert('RGBA')
    assert im.tobytes()==expected.tobytes(),f'ICO frame mismatch {n}'
    a=im.getchannel('A')
    assert a.getextrema()==(0,255)
    assert a.getpixel((int(n*.60),int(n*.80)))==0, f'central gap {n}'
    checks[str(n)]={'rgba_exact_match':True,'alpha_range':list(a.getextrema()),'bbox':list(a.getbbox()),'variant':'small' if n<=64 else 'standard','encoding':'PNG' if n==256 else '32-bit DIB + AND mask'}
svgs={}
for file in ROOT.glob('*.svg'):
    tree=ET.parse(file); elements=list(tree.iter())
    assert not any(e.tag.rsplit('}',1)[-1] in ['image','rect','mask','filter','foreignObject'] for e in elements)
    paths=[e for e in elements if e.tag.endswith('}path')]
    assert paths and all('C' in p.attrib['d'] for p in paths)
    svgs[file.name]={'paths':len(paths),'embedded_images':0,'background_shapes':0}
report={'ico_frames':checks,'svg':svgs}
workspace = ROOT.parents[2]
references = (
    ("NPEduTools", "npedutools"),
    ("NPClassworks", "npclassworks"),
)
palette = {"主蓝": "#2668B2", "深蓝折面": "#1B4878", "浅蓝书页": "#70ADE6"}

def geometry(file):
    root = ET.parse(file).getroot()
    return root.attrib['viewBox'], [p.attrib['d'] for p in root if p.tag.endswith('}path')]

for suffix in ['', '-small', '-mono', '-mono-light']:
    name = f'npeduconsole-logo{suffix}'
    matched = []
    for repo, basename in references:
        reference = workspace/repo/'images'/'branding'/f'{basename}-logo{suffix}.svg'
        assert geometry(ROOT/f'{name}.svg') == geometry(reference), reference
        matched.append(repo)
    svgs[f'{name}.svg']['identical_geometry_to'] = matched
    png = 'npeduconsole-256' if not suffix else f'npeduconsole{suffix}-256'
    with Image.open(RENDERS/f'verify-{name}.png') as imported, Image.open(PNG/f'{png}.png') as exported:
        assert imported.size == exported.size == (256, 256)
        assert imported.convert('RGBA').tobytes() == exported.tobytes(), name
    svgs[f'{name}.svg']['illustrator_256px_rgba_exact_match'] = True

with Image.open(PNG/'npeduconsole-1024.png') as standard:
    assert standard.mode == 'RGBA'
    assert standard.getpixel((0, 0))[3] == standard.getpixel((614, 819))[3] == 0
    # Solid fills occupy large areas in the actual RGB export.
    solid_colors = {color for count, color in standard.getcolors(standard.width * standard.height) if count > 1000}
    assert all((*ImageColor.getrgb(value), 255) in solid_colors for value in palette.values())
report['palette'] = palette
(ROOT/'source'/'validation.json').write_text(json.dumps(report, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')

# Compose the comparison from the delivered PNGs.
canvas = Image.new('RGB', (1320, 940), '#F4F6F9')
draw = ImageDraw.Draw(canvas)
regular = 'C:/Windows/Fonts/msyh.ttc'
bold = 'C:/Windows/Fonts/msyhbd.ttc'

def label(x, y, value, size, color='#182534', weight=regular):
    draw.text((x, y), value, font=ImageFont.truetype(weight, size), fill=color)

def card(bounds, color):
    draw.rounded_rectangle(bounds, radius=16, fill=color)

def place(file, x, y):
    with Image.open(file) as original:
        image = original.convert('RGBA')
        canvas.paste(image, (x, y), image)

label(48, 30, 'NPEP 系列图标', 35, weight=bold)
label(49, 86, 'NPEduConsole 蓝色版 · 2026-11-21', 17, '#536578')
for x, repo, basename, accent, color_name in [
    (48, 'NPEduTools', 'npedutools', '#147D68', '青绿'),
    (464, 'NPClassworks', 'npclassworks', '#D97732', '暖橙'),
    (880, 'NPEduConsole', 'npeduconsole', '#2668B2', '冷蓝'),
]:
    card((x, 135, x+392, 476), '#E8EFF8' if repo == 'NPEduConsole' else '#FFFFFF')
    label(x+22, 152, repo, 21, weight=bold)
    label(x+22, 184, color_name, 14, accent)
    file = workspace/repo/'images'/'branding'/'png'/f'{basename}-256.png'
    place(file, x+68, 209)

label(48, 502, '蓝色简化版与单色版', 24, weight=bold)
label(340, 511, '图标按原始像素合成，请以 100% 查看', 15, '#536578')
for y, bg, fg, variant in [(550, '#FFFFFF', '#182534', 'mono'), (698, '#202E35', '#FFFFFF', 'mono-light')]:
    card((48, y, 1272, y+132), bg)
    label(68, y+14, '彩色', 15, fg)
    label(670, y+14, '单色', 15, fg)
    for i, size in enumerate([16, 24, 32, 48, 64]):
        for start, prefix in [(148, 'npeduconsole'), (738, f'npeduconsole-{variant}')]:
            x = start + i * 103
            place(PNG/f'{prefix}-{size}.png', x+(64-size)//2, y+34+(64-size)//2)
            label(x+22, y+105, str(size), 12, fg)

for i, (name, color) in enumerate(palette.items()):
    x = 48 + i*408
    draw.rounded_rectangle((x, 870, x+40, 910), radius=8, fill=color)
    label(x+54, 871, color, 17, weight=bold)
    label(x+160, 873, name, 15, '#536578')
canvas.save(ROOT/'npep-family-preview.png')


