"""Convert a FileDescriptorSet (gre.desc) into readable .proto files (proto3)."""
import sys, os
from google.protobuf import descriptor_pb2 as d

F = d.FieldDescriptorProto
SCALAR = {F.TYPE_DOUBLE: 'double', F.TYPE_FLOAT: 'float', F.TYPE_INT64: 'int64', F.TYPE_UINT64: 'uint64',
          F.TYPE_INT32: 'int32', F.TYPE_FIXED64: 'fixed64', F.TYPE_FIXED32: 'fixed32', F.TYPE_BOOL: 'bool',
          F.TYPE_STRING: 'string', F.TYPE_BYTES: 'bytes', F.TYPE_UINT32: 'uint32', F.TYPE_SFIXED32: 'sfixed32',
          F.TYPE_SFIXED64: 'sfixed64', F.TYPE_SINT32: 'sint32', F.TYPE_SINT64: 'sint64'}


def tname(f):
    if f.type in SCALAR:
        return SCALAR[f.type]
    return f.type_name.lstrip('.')


def emit_msg(m, ind, out):
    p = '  ' * ind
    out.append(f'{p}message {m.name} {{')
    map_entries = {n.name: n for n in m.nested_type if n.options.map_entry}
    for n in m.nested_type:
        if not n.options.map_entry:
            emit_msg(n, ind + 1, out)
    for e in m.enum_type:
        emit_enum(e, ind + 1, out)
    in_oneof = {}
    for f in m.field:
        if f.HasField('oneof_index') and not f.proto3_optional:
            in_oneof.setdefault(f.oneof_index, []).append(f)
    done = set()
    for f in m.field:
        if f.HasField('oneof_index') and not f.proto3_optional:
            if f.oneof_index in done:
                continue
            done.add(f.oneof_index)
            out.append(f'{p}  oneof {m.oneof_decl[f.oneof_index].name} {{')
            for g in in_oneof[f.oneof_index]:
                out.append(f'{p}    {tname(g)} {g.name} = {g.number};')
            out.append(f'{p}  }}')
            continue
        entry = None
        if f.type == F.TYPE_MESSAGE:
            nm = f.type_name.split('.')[-1]
            entry = map_entries.get(nm)
        if entry:
            k, v = entry.field
            out.append(f'{p}  map<{tname(k)}, {tname(v)}> {f.name} = {f.number};')
            continue
        label = 'repeated ' if f.label == F.LABEL_REPEATED else ('optional ' if f.proto3_optional else '')
        out.append(f'{p}  {label}{tname(f)} {f.name} = {f.number};')
    out.append(f'{p}}}')


SEEN_TOP_ENUM_VALUES = None  # set() in merge mode: top-level enum values share one package scope


def emit_enum(e, ind, out):
    p = '  ' * ind
    out.append(f'{p}enum {e.name} {{')
    for v in e.value:
        name = v.name
        if ind == 0 and SEEN_TOP_ENUM_VALUES is not None:
            if name in SEEN_TOP_ENUM_VALUES:
                name = e.name + '_' + name  # wire format only uses numbers
            SEEN_TOP_ENUM_VALUES.add(name)
        out.append(f'{p}  {name} = {v.number};')
    out.append(f'{p}}}')


fds = d.FileDescriptorSet()
fds.ParseFromString(open(sys.argv[1], 'rb').read())
outdir = sys.argv[2]
os.makedirs(outdir, exist_ok=True)
java_pkg = sys.argv[3] if len(sys.argv) > 3 else 'com.wizards.mtga.gre'
merge = len(sys.argv) > 4 and sys.argv[4] == 'merge'
if merge:  # one .proto per package (keeps protoc's command line short on Windows)
    pkg_of = {f.name: f.package for f in fds.file}
    groups = {}
    for f in fds.file:
        if not f.package.startswith('google'):
            groups.setdefault(f.package, []).append(f)
    for pkg, files in groups.items():
        SEEN_TOP_ENUM_VALUES = {m.name for f in files for m in f.message_type} | {e.name for f in files for e in f.enum_type}
        out = ['syntax = "proto3";', f'package {pkg};', '']
        deps = set()
        for f in files:
            for dep in f.dependency:
                dp = pkg_of.get(dep, '')
                deps.add(dep if dep.startswith('google/') else (dp + '.proto' if dp != pkg else None))
        for dep in sorted(x for x in deps if x):
            out.append(f'import "{dep}";')
        out.append('option java_multiple_files = true;')
        out.append(f'option java_package = "{java_pkg}";')
        out.append(f'option java_outer_classname = "{pkg.split(".")[-1]}Protos";')
        out.append('')
        for f in files:
            out.append(f'// ---- {f.name}')
            for e in f.enum_type:
                emit_enum(e, 0, out)
            for m in f.message_type:
                emit_msg(m, 0, out)
        open(os.path.join(outdir, pkg + '.proto'), 'w', encoding='utf-8').write(chr(10).join(out) + chr(10))
        print('wrote', pkg + '.proto', len(files), 'files')
    sys.exit(0)
for f in fds.file:
    if f.name.startswith('google/'):
        continue  # well-known types ship with protoc
    out = ['syntax = "proto3";', f'package {f.package};', '']
    for dep in f.dependency:
        out.append(f'import "{dep}";')
    if f.options.csharp_namespace:
        out.append(f'option csharp_namespace = "{f.options.csharp_namespace}";')
    out.append('option java_multiple_files = true;')
    out.append(f'option java_package = "{java_pkg}";')
    out.append('')
    for e in f.enum_type:
        emit_enum(e, 0, out)
    for m in f.message_type:
        emit_msg(m, 0, out)
    os.makedirs(os.path.dirname(os.path.join(outdir, f.name)) or outdir, exist_ok=True)
    open(os.path.join(outdir, f.name), 'w', encoding='utf-8').write('\n'.join(out) + '\n')
    print('wrote', f.name)
