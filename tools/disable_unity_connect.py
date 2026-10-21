"""Switch off Unity's engine-level cloud services in the CLIENT COPY: analytics, performance reporting and crash
reporting (UnityConnectSettings in globalgamemanagers: api/config.uca, cdp, perf-events, engine-data ... unity3d.com).
These run inside the engine before any managed code, so ForgeBridge cannot stop them.

The file has no type trees, so only two things are edited, both unambiguous: the object's first field (m_Enabled, the
master switch) is cleared, and every URL string in the object is overwritten, same length, with a dead loopback URL
(port 9, nothing listens), so even a service that ignores the switch has nowhere to go. Offsets never change.
Idempotent; the original is kept as globalgamemanagers.orig (shared with isolate_identity.py).
Usage: python disable_unity_connect.py <copy>\\MTGA_Data\\globalgamemanagers
"""
import os, re, shutil, struct, sys

UNITY_CONNECT_SETTINGS = 310   # Unity class id
DEAD = b'http://127.0.0.1:9/'
URL = re.compile(rb'https?://[\x21-\x7e]+')


def objects(b):
    """(class id, data start, size) of every object; SerializedFile format 22 without type trees."""
    version = struct.unpack('>I', b[8:12])[0]
    assert version == 22, f'unsupported SerializedFile version {version}'
    data_offset = struct.unpack('>Q', b[32:40])[0]
    assert b[40] == 0, 'expected little-endian metadata'
    p = b.index(b'\0', 48) + 1 + 4           # unity version string, target platform
    assert b[p] == 0, 'file has type trees: this tool only handles the stripped layout'
    p += 1
    count = struct.unpack('<i', b[p:p + 4])[0]
    p += 4
    classes = []
    for _ in range(count):
        cid, _stripped, script = struct.unpack('<iBh', b[p:p + 7])
        p += 7 + (16 if cid == 114 else 0) + 16    # script id (MonoBehaviour only), old type hash
        classes.append(cid)
    n = struct.unpack('<i', b[p:p + 4])[0]
    p += 4
    p = (p + 3) & ~3
    out = []
    for _ in range(n):
        _path_id, start, size, type_index = struct.unpack('<qQIi', b[p:p + 24])
        p += 24
        out.append((classes[type_index], data_offset + start, size))
    return out


def dead_url(length):
    return (DEAD + b'x' * length)[:length]


def main(path):
    b = bytearray(open(path, 'rb').read())
    found = [(s, n) for cid, s, n in objects(b) if cid == UNITY_CONNECT_SETTINGS]
    assert len(found) == 1, f'expected one UnityConnectSettings object, found {len(found)}'
    start, size = found[0]
    body = bytes(b[start:start + size])
    assert body[0] in (0, 1) and body[2:4] == b'\0\0', 'unexpected UnityConnectSettings header'

    urls = []
    for m in URL.finditer(body):
        length = struct.unpack('<I', body[m.start() - 4:m.start()])[0]
        if length == len(m.group()):         # a whole serialized string, not text inside one
            urls.append((m.start(), length, m.group()))
    assert urls, 'no URL strings in UnityConnectSettings'

    changed = body[0] != 0 or any(not u.startswith(DEAD) for _, _, u in urls)
    if not changed:
        print(f'already patched ({len(urls)} URLs dead, m_Enabled=0)')
        return
    if not os.path.exists(path + '.orig'):
        shutil.copy2(path, path + '.orig')
    b[start] = 0
    for off, length, old in urls:
        if not old.startswith(DEAD):
            print('  ', old.decode(), '->', dead_url(length).decode())
        b[start + off:start + off + length] = dead_url(length)
    open(path, 'wb').write(b)
    print(f'patched UnityConnectSettings at {start:#x}: m_Enabled=0, {len(urls)} URLs dead')


if __name__ == '__main__':
    main(sys.argv[1])
