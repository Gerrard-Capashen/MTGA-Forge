"""Give the CLIENT COPY its own Unity company/product identity (same-length strings) so its
PlayerPrefs (registry) and persistentDataPath (LocalLow) never touch the original install's data.
Idempotent. Usage: python isolate_identity.py <copy>\\MTGA_Data\\globalgamemanagers
"""
import sys, shutil, os

path = sys.argv[1]
b = bytearray(open(path, 'rb').read())
old = b'\x14\x00\x00\x00Wizards Of The Coast\x04\x00\x00\x00MTGA'
new = b'\x14\x00\x00\x00Forge Bridge Project\x04\x00\x00\x00MTGF'
assert len(old) == len(new)
if b.count(new) == 1:
    print('already patched')
    sys.exit(0)
assert b.count(old) == 1, f'expected exactly one occurrence, found {b.count(old)}'
if not os.path.exists(path + '.orig'):
    shutil.copy2(path, path + '.orig')
i = b.index(old)
b[i:i + len(old)] = new
open(path, 'wb').write(b)
print('patched at offset', i)
