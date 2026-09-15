import ctypes
from ctypes import wintypes
from pathlib import Path
import sys
import uuid
import winreg

CLSID = '{A0C98030-4177-44CC-BD4B-63A047C3F30A}'
SYS_WIN64 = 3
addin_key = rf'SOFTWARE\SolidWorks\AddIns\{CLSID}'
startup_key = rf'Software\SolidWorks\AddInsStartup\{CLSID}'
class_key = rf'SOFTWARE\Classes\CLSID\{CLSID}'
payload = Path(sys.argv[1]).resolve()


def snapshot(root, path):
    try:
        with winreg.OpenKey(root, path, 0, winreg.KEY_READ | winreg.KEY_WOW64_64KEY) as key:
            children, values, _ = winreg.QueryInfoKey(key)
            return (
                [winreg.EnumValue(key, i) for i in range(values)],
                {name: snapshot(root, path + '\\' + name)
                 for name in [winreg.EnumKey(key, i) for i in range(children)]},
            )
    except FileNotFoundError:
        return None


def value(root, path, name=''):
    with winreg.OpenKey(root, path) as key:
        return winreg.QueryValueEx(key, name)[0]


roots = [(winreg.HKEY_LOCAL_MACHINE, class_key),
         (winreg.HKEY_LOCAL_MACHINE, addin_key),
         (winreg.HKEY_CURRENT_USER, startup_key)]
before = [snapshot(*item) for item in roots]
scratch = rf'Software\Coincident\DahliaInstallerTest\{uuid.uuid4()}'
handles = [winreg.CreateKeyEx(winreg.HKEY_CURRENT_USER, scratch + '\\' + suffix, 0, winreg.KEY_ALL_ACCESS)
           for suffix in ['Machine', 'User', r'Machine\SOFTWARE\Classes']]
override = ctypes.WinDLL('advapi32', use_last_error=True).RegOverridePredefKey
override.argtypes = [wintypes.HKEY, wintypes.HKEY]
override.restype = wintypes.LONG
redirected = []

try:
    for root, handle in zip([winreg.HKEY_LOCAL_MACHINE, winreg.HKEY_CURRENT_USER, winreg.HKEY_CLASSES_ROOT], handles):
        native_root = wintypes.HKEY(ctypes.c_int32(int(root)).value)
        result = override(native_root, int(handle))
        if result:
            raise ctypes.WinError(result)
        redirected.append(native_root)

    import pythoncom

    library = pythoncom.LoadTypeLib(str(payload / 'SldworksPlugin.tlb'))
    _, locale, architecture, major, minor, flags = library.GetLibAttr()
    assert architecture == SYS_WIN64
    host_path = payload / 'SldworksPlugin.comhost.dll'
    host = ctypes.WinDLL(str(host_path))
    for operation in ['DllRegisterServer', 'DllUnregisterServer']:
        getattr(host, operation).restype = ctypes.c_long
    for _ in range(2):
        assert host.DllRegisterServer() == 0
        assert Path(value(winreg.HKEY_LOCAL_MACHINE, class_key + '\\InprocServer32')) == host_path
        assert value(winreg.HKEY_CLASSES_ROOT, 'Sldworks.Plugin\\CLSID').lower() == CLSID.lower()
        assert value(winreg.HKEY_LOCAL_MACHINE, addin_key, 'Title') == 'Dahlia for SOLIDWORKS'
        assert value(winreg.HKEY_CURRENT_USER, startup_key) == 1

    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, startup_key, 0, winreg.KEY_SET_VALUE) as key:
        winreg.SetValueEx(key, '', 0, winreg.REG_DWORD, 0)
    assert host.DllRegisterServer() == 0
    assert value(winreg.HKEY_CURRENT_USER, startup_key) == 0

    assert host.DllUnregisterServer() == 0
    assert host.DllUnregisterServer() == 0
    for root, path in roots:
        assert snapshot(root, path) is None, path
finally:
    for root in reversed(redirected):
        result = override(root, None)
        if result:
            raise ctypes.WinError(result)
    for handle in handles:
        handle.Close()
    assert [snapshot(*item) for item in roots] == before, 'Live registration changed.'

    def remove_scratch(path):
        assert path == scratch or path.startswith(scratch + '\\')
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, path, 0, winreg.KEY_ALL_ACCESS) as key:
            while winreg.QueryInfoKey(key)[0]:
                remove_scratch(path + '\\' + winreg.EnumKey(key, 0))
        winreg.DeleteKey(winreg.HKEY_CURRENT_USER, path)

    remove_scratch(scratch)

print('COM registration, repeated registration, unregistration, and type-library loading passed; live registration unchanged.')
