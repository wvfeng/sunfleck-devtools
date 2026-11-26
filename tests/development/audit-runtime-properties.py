"""Audit every built-in default and literal assignment in real headless pages.
Usage: python audit-runtime-properties.py schema.json native.dll report.md
"""
import ctypes as c
import json
import sys
from audit_template import register_template
from pathlib import Path
schema = json.loads(Path(sys.argv[1]).read_text(encoding='utf-8-sig'))
native = c.CDLL(str(Path(sys.argv[2]).resolve()))
native.ui_api_version.restype = c.c_uint32
assert native.ui_api_version() == 5, 'This audit requires Sunfleck API v5'
class Options(c.Structure):
    _fields_ = [('size',c.c_uint32),('version',c.c_uint32),('renderer',c.c_uint32),('flags',c.c_uint32)]
class Diagnostic(c.Structure):
    _fields_ = [('code',c.c_int),('line',c.c_uint32),('column',c.c_uint32),('message',c.c_char*128)]
class Value(c.Structure):
    _fields_ = [('kind',c.c_int),('integer',c.c_int64),('number',c.c_double),('string',c.c_char_p),('command',c.c_void_p)]
Getter = c.CFUNCTYPE(c.c_int,c.c_void_p,c.POINTER(Value))
Action = c.CFUNCTYPE(None,c.c_void_p,c.c_void_p)
class Property(c.Structure):
    _fields_ = [('size',c.c_uint32),('version',c.c_uint32),('path',c.c_char_p),('kind',c.c_int),('get',Getter),('set',c.c_void_p),('context',c.c_void_p)]
for name,args in {
 'ui_app_create':[c.POINTER(Options),c.POINTER(c.c_void_p)],
 'ui_app_destroy':[c.c_void_p], 'ui_app_open_window':[c.c_void_p], 'ui_app_poll':[c.c_void_p,c.c_uint32],
 'ui_model_create':[c.c_void_p,c.POINTER(c.c_void_p)],
 'ui_command_create':[c.c_void_p,Action,c.c_void_p,c.POINTER(c.c_void_p)],
 'ui_model_add_property':[c.c_void_p,c.POINTER(Property)],
 'ui_app_load_fleck_with_model':[c.c_void_p,c.c_char_p,c.c_size_t,c.c_void_p,c.c_void_p,c.POINTER(Diagnostic)],
 'ui_app_get_debug_snapshot':[c.c_void_p,c.c_void_p,c.POINTER(c.c_size_t)],
}.items(): getattr(native,name).argtypes=args

def document(name,attrs):
    content={'DockHost':'<DockGroup><DockDocument Id="doc"/></DockGroup>',
      'DockSplit':'<DockGroup><DockDocument Id="doc"/></DockGroup><DockGroup><DockTool Id="tool"/></DockGroup>',
      'DockGroup':'<DockDocument Id="doc"/>','TabView':'<TabItem/>','NavigationView':'<TabItem/>',
      'ComboBox':'<ListItem/>','ListView':'<ListItem/>'}.get(name,'')
    if 'ItemsSource=' in attrs: content=''
    node=f'<{name} Id="probe" {attrs}>{content}</{name}>'
    parent={'Run':'TextBlock','Span':'TextBlock','ListItem':'ListView','TabItem':'TabView','TreeItem':'TreeView','DockDocument':'DockGroup','DockTool':'DockGroup'}.get(name)
    if parent: node=f'<{parent}>{node}</{parent}>'
    if name in ('DockSplit','DockGroup','DockDocument','DockTool'): node=f'<DockHost>{node}</DockHost>'
    resources = f'<Window.Resources><Style Key="sample" TargetType="{name}"/></Window.Resources>' if 'Style=' in attrs else ''
    return f'<Window xmlns="urn:sunfleck:v1" xmlns:a="urn:audit" Width="400" Height="300">{resources}<StackPanel><Label Id="anchor"/>{node}</StackPanel></Window>'

def snapshot(name,attrs):
    app=c.c_void_p(); options=Options(c.sizeof(Options),native.ui_api_version(),2,1)
    assert native.ui_app_create(c.byref(options),c.byref(app))==0
    try:
        model=c.c_void_p(); command=c.c_void_p()
        action=Action(lambda *_:None)
        assert native.ui_model_create(app,c.byref(model))==0
        assert native.ui_command_create(app,action,None,c.byref(command))==0
        def get(_,out): out.contents.kind=4; out.contents.command=command.value; return 0
        getter=Getter(get); prop=Property(c.sizeof(Property),1,b'Action',4,getter,None,None)
        assert native.ui_model_add_property(model,c.byref(prop))==0
        if 'ItemsSource=' in attrs:
            rows=c.c_void_p()
            assert native.ui_model_create(app,c.byref(rows))==0
            assert native.ui_model_enable_text_collection(rows)==0
            def get_rows(_,out): out.contents.kind=5; out.contents.command=rows.value; return 0
            rows_getter=Getter(get_rows)
            rows_prop=Property(c.sizeof(Property),1,b'Rows',5,rows_getter,None,None)
            assert native.ui_model_add_property(model,c.byref(rows_prop))==0
        raw=document(name,attrs).encode(); diagnostic=Diagnostic()
        if 'Template=' in attrs: register_template(native,app,name,diagnostic)
        status=native.ui_app_load_fleck_with_model(app,raw,len(raw),model if '{Binding' in attrs else None,None,c.byref(diagnostic))
        assert status==0,diagnostic.message.decode()
        assert native.ui_app_open_window(app)==0
        assert native.ui_app_poll(app,0)==0
        size=c.c_size_t(); assert native.ui_app_get_debug_snapshot(app,None,c.byref(size))==0
        buffer=c.create_string_buffer(size.value); assert native.ui_app_get_debug_snapshot(app,buffer,c.byref(size))==0
        return next(n for n in json.loads(buffer.value)['nodes'] if n['fleckId']=='probe')
    finally: native.ui_app_destroy(app)

errors=[]; rows=[]; defaults=assignments=geometry=0
for element in schema['elements']:
    name=element['name']; baseline=snapshot(name,''); values={p['name']:p for p in baseline['properties']}
    checked=[]
    for prop in element['attributes']:
        key=prop['name']; defaults+=1
        expected=prop['defaultValue']
        if key=='Id': expected='probe'
        if values[key]['value']!=expected: errors.append(f'{name}.{key} default: {values[key]["value"]!r} != {expected!r}')
        if key=='Id': checked.append('Id (fixture)'); continue
        value = {'ItemsSource':'{Binding Rows}','Style':'sample','Data':'M0,0 L10,0 L0,10 Z','Fill':'#80402010','Stroke':'#FF203040','StrokeThickness':'2.5','DataContext':'null','Command':'{Binding Action}','CommandMode':'Sync','Anchor':'anchor','Text':'sample','Content':'sample',
          'Foreground':'#80402010','TextAlignment':'Center','TextWrapping':'NoWrap','TextTrimming':'WordEllipsis','LineHeight':'32','TextDecorations':'Underline','FlowDirection':'RightToLeft','FontFamily':'Consolas','FontSize':'23.5','FontWeight':'Bold','FontStyle':'Italic','FontStretch':'Condensed','Visibility':'Hidden','IsHitTestVisible':'false','Background':'#80402010','BorderBrush':'#ff203040','BorderThickness':'1,2,3,4','CornerRadius':'5,6,7,8','Margin':'1,2,3,4','Width':'61','Height':'27','MinWidth':'31','MinHeight':'13','MaxWidth':'123','MaxHeight':'83',
          'RowDefinitions':'Auto,2*','ColumnDefinitions':'Auto,2*','HorizontalScrollBarVisibility':'Visible','VerticalScrollBarVisibility':'Hidden','Stretch':'Fill','LayoutTransform':'1,0,0,1,3,4','RenderTransform':'1,0,0,1,3,4','RenderTransformOrigin':'0.25,0.5','Opacity':'0.375','Canvas.Left':'-4.5','Canvas.Top':'7.25','Panel.ZIndex':'-3','Grid.Row':'1','Grid.Column':'1','Grid.RowSpan':'2','Grid.ColumnSpan':'2',
          'TabIndex':'7','IsFocusScope':'true','Template':'a:Face','Weight':'3','Spacing':'5','Padding':'4','Ratio':'37','Dock':'Right',
          'HorizontalAlignment':'Center','VerticalAlignment':'Bottom','Orientation':'Vertical',
          'Title':'Test title','Icon':'test','Value':'0' if prop.get('maximumFrom') else '1'}.get(key,'false')
        attrs=f'{key}="{value}"'+(' Command="{Binding Action}"' if key=='CommandMode' else '')
        if key in ('Width','Height') and name in ('Popup','Dialog','Tooltip','ContextMenu'): attrs+=' Value="1"'
        try:
            result=snapshot(name,attrs); actual=next(p['value'] for p in result['properties'] if p['name']==key)
            wanted= None if key=='DataContext' else 'Attached' if key in ('Command','ItemsSource') else float(value) if prop['type'] in ('number','length') else int(value) if prop['type']=='integer' else value=='true' if prop['type']=='bool' else value
            if prop['type'] in ('thickness','cornerRadius'): wanted=[float(x) for x in value.split(',')]
            if prop['type']=='color': wanted=value.upper()
            if key=='Template': wanted='urn:audit:Face'
            if name=='PasswordBox' and key=='Text': wanted='' # Never disclose secrets through the inspector.
            assert actual==wanted,f'{actual!r} != {wanted!r}'
            assignments+=1; checked.append(key)
            if key in ('Width','Height') and name not in ('DockSplit','DockGroup','DockDocument','DockTool','Run','Span'):
                bounds=result['bounds']; size=bounds[2]-bounds[0] if key=='Width' else bounds[3]-bounds[1]
                assert size==wanted,f'actual {key} {size} != requested {wanted}'
                geometry+=1
        except Exception as e: errors.append(f'{name}.{key}: {e}')
    rows.append(f"| {name} | {len(element['attributes'])} | {', '.join(checked)} |")
    print(f'{name}: checked',flush=True)
report=['# Built-in runtime property audit','',f'{len(rows)} tags; {defaults} defaults; {assignments} literal assignments and {geometry} actual size checks verified in headless runtime.','',
 'Checks load real pages, poll layout, and compare snapshot property values with the declared defaults and explicit values. Id is exercised by node lookup. PasswordBox.Text is deliberately masked. These checks verify values reach runtime; they do not imply every property affects every parent layout.','',
 '## Layout and interaction checks','',
 '- visual_layout: requested/actual dimensions, Min/Max, margins, alignment, DPI, negative margins, Dock order, TextBlock content measurement, Width/Auto restoration, and content changes.',
 '- property_bindings / components: command execution, enabled state, scalar binding and template behavior.',
 '- Weight acts in Grid/Flex. Dock acts in DockPanel (last child fills). Explicit Width fixes the track; Auto tracks use Weight and Flex grows from MinWidth.',
 '- Auto can stretch in the parent slot. Spacing is container-specific; Overlay does not create gaps. Dock structural nodes are controlled by the Dock workspace.',
 '- Run/Span retain assigned property values but have no independent layout slot; paragraph ranges determine their geometry.',
 '- Image/Icon pixels and DocumentView document state are supplied through their dedicated APIs, not a Fleck Source attribute.','',
 '| Element | Defaults | Explicit values checked |','|---|---:|---|',*rows,'','## Failures','',*(errors or ['None.']),'']
Path(sys.argv[3]).write_text('\n'.join(report),encoding='utf-8')
print(json.dumps(dict(defaults=defaults,assignments=assignments,geometry=geometry,errors=errors),indent=2))
sys.exit(bool(errors))
