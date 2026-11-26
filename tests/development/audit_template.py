"""Register a template with the requested native control as its target."""
import ctypes as c


class Resource(c.Structure):
    _fields_ = [('uri', c.c_char_p), ('utf8', c.c_char_p), ('length', c.c_size_t)]


def register_template(native, app, kind, diagnostic):
    xml = (f'<ControlTemplate xmlns="urn:sunfleck:v1" Class="Audit.Face" '
           f'Namespace="urn:audit" TargetType="{kind}"><ContentPresenter/></ControlTemplate>').encode()
    entry = Resource(b'audit-face.fleck', xml, len(xml))
    status = native.ui_app_register_fleck_resources(app, c.byref(entry), c.c_size_t(1), c.byref(diagnostic))
    assert status == 0, diagnostic.message.decode()
