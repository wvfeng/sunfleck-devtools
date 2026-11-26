"""Check every visual tag/property against the native parser and all SDK maps.
Run: python tests/development/audit-element-properties.py schema.json native.dll report.md
"""
import ctypes as c
import json
import sys
from audit_template import register_template
from pathlib import Path
schema = json.loads(Path(sys.argv[1]).read_text(encoding='utf-8-sig'))
native = c.CDLL(str(Path(sys.argv[2]).resolve()))
class Diagnostic(c.Structure):
    _fields_ = [('code', c.c_int), ('line', c.c_uint32), ('column', c.c_uint32), ('message', c.c_char * 128)]
native.ui_fleck_validate.argtypes = [c.c_char_p, c.c_size_t, c.POINTER(Diagnostic)]
def validate(text, template_target=None):
    raw = text.encode(); error = Diagnostic()
    if template_target:
        options = (c.c_uint32 * 4)(16,native.ui_api_version(),2,1)
        app = c.c_void_p()
        assert native.ui_app_create(c.byref(options),c.byref(app)) == 0
        try:
            register_template(native,app,template_target,error)
            status = native.ui_app_validate_fleck(app,raw,c.c_size_t(len(raw)),c.byref(error))
        finally:
            native.ui_app_destroy(app)
    else:
        status = native.ui_fleck_validate(raw, len(raw), c.byref(error))
    return status, error.message.decode()
def document(name, attrs):
    content = {'DockHost':'<DockGroup><DockDocument Id="doc"/></DockGroup>',
        'DockSplit':'<DockGroup><DockDocument Id="doc"/></DockGroup><DockGroup><DockTool Id="tool"/></DockGroup>',
        'DockGroup':'<DockDocument Id="doc"/>', 'TabView':'<TabItem/>', 'NavigationView':'<TabItem/>',
        'ComboBox':'<ListItem/>', 'ListView':'<ListItem/>'}.get(name, '')
    if name in ('DockDocument','DockTool') and not attrs.startswith('Id='): attrs += ' Id="doc"'
    node = f'<{name} {attrs}>{content}</{name}>'
    if name == 'ListItem': node = f'<ListView>{node}</ListView>'
    if name == 'TabItem': node = f'<TabView>{node}</TabView>'
    if name == 'TreeItem': node = f'<TreeView>{node}</TreeView>'
    if name in ('Run','Span'): node = f'<TextBlock>{node}</TextBlock>'
    if name in ('DockDocument','DockTool'): node = f'<DockGroup>{node}</DockGroup>'
    if name in ('DockGroup','DockSplit','DockDocument','DockTool'): node = f'<DockHost>{node}</DockHost>'
    resources = f'<Window.Resources><Style Key="sample" TargetType="{name}"/></Window.Resources>' if 'Style=' in attrs else ''
    return f'<Window xmlns="urn:sunfleck:v1" xmlns:a="urn:audit">{resources}<StackPanel><Label Id="anchor"/>{node}</StackPanel></Window>'
errors = []; checked = 0
repo = Path(__file__).resolve().parents[2]
catalogs = [json.loads((repo/'bindings/types'/f'{lang}.json').read_text()) for lang in ('c','cpp','csharp')]
rows=[]
for element in schema['elements']:
    name = element['name']; props = element['attributes']
    for catalog in catalogs:
        entry = next(t for t in catalog['types'] if t['group']=='' and t['name']==name)
        if {p['name'] for p in entry['properties']} != {p['name'] for p in props}: errors.append(f'{name}: SDK map differs')
    for prop in props:
        key = prop['name']
        value = ('M0,0 L10,0 L0,10 Z' if key == 'Data' else '{Binding Action}' if key == 'Command' else '1,0,0,1,3,4' if key in ('RenderTransform','LayoutTransform') else '0.25,0.5' if key == 'RenderTransformOrigin' else '0.375' if key == 'Opacity' else '0' if key == 'Margin' else 'anchor' if key == 'Anchor' else 'node' if key == 'Id'
            else 'Auto,2*' if key in ('RowDefinitions','ColumnDefinitions') else '23.5' if prop['type'] in ('number','length') else '#80402010' if prop['type']=='color' else '1,2,3,4' if prop['type'] in ('thickness','cornerRadius') else str(prop.get('minimum',0)) if prop['type']=='integer' else prop['values'][0] if prop.get('values') else 'sample')
        if key == 'ItemsSource': value = '{Binding Rows}'
        if key == 'Template': value = 'a:Face'
        attrs = f'{key}="{value}"' + (' Command="{Binding Action}"' if key == 'CommandMode' else '')
        status, message = validate(document(name,attrs), name if key == 'Template' else None); checked += 1
        if status: errors.append(f'{name}.{key}: {message}')
        if prop.get('binding'):
            status, message = validate(document(name,f'{key}="{{Binding Value, Mode=OneWay}}"'))
            if status: errors.append(f'{name}.{key} binding: {message}')
    status, message = validate(document(name,'DefinitelyUnknown="1"'))
    if not status: errors.append(f'{name}: unknown attribute accepted')
    rows.append(f"| {name} | {', '.join(p['name'] for p in props)} | {', '.join(p['name'] for p in props if p.get('binding'))} |")
report = '\n'.join(['# Built-in visual property audit', '',
    f'{len(rows)} visual tags, {checked} tag/property pairs. All three SDK navigation maps checked.', '',
    'Generated from the native schema; this is the supported Sunfleck contract, not WPF API parity.', '',
    '| Element | Supported attributes | Bindable attributes |', '|---|---|---|', *rows, '',
    '## Failures', '', *(errors or ['None.']), ''])
Path(sys.argv[3]).write_text(report, encoding='utf-8')
print(json.dumps(dict(elements=len(rows), properties=checked, errors=errors), indent=2))
sys.exit(bool(errors))
