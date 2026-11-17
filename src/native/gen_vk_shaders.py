# Genera vk_shaders.h: i due shader SPIR-V dell'overlay Vulkan, scritti istruzione per istruzione
# (non serve nessun compilatore di shader). Uso: python gen_vk_shaders.py
#
# Vertex: quadrilatero da 4 vertici senza buffer, ricavato dal numero del vertice.
#     p = (indice & 1, (indice >> 1) & 1);  uv = p;  posizione = p * 2 - 1
# Fragment: colore = texture(overlay, uv)
import os
import struct

OP = dict(Capability=17, MemoryModel=14, EntryPoint=15, ExecutionMode=16, Decorate=71,
          TypeVoid=19, TypeInt=21, TypeFloat=22, TypeVector=23, TypeImage=25, TypeSampledImage=27,
          TypePointer=32, TypeFunction=33, Constant=43, Function=54, FunctionEnd=56, Variable=59,
          Load=61, Store=62, CompositeConstruct=80, ImageSampleImplicitLod=87, ConvertSToF=111,
          FSub=131, FMul=133, ShiftRightArithmetic=195, BitwiseAnd=199, Label=248, Return=253,
          ExtInstImport=11, ExtInst=12, TypeStruct=30, ConstantComposite=44, AccessChain=65,
          VectorShuffle=79, CompositeExtract=81, FAdd=129, FDiv=136, VectorTimesScalar=142, Dot=148,
          MemberDecorate=72)
INPUT, OUTPUT, UNIFORM_CONSTANT, PUSH_CONSTANT = 1, 3, 0, 9
DEC_BUILTIN, DEC_LOCATION, DEC_BINDING, DEC_SET, DEC_BLOCK, DEC_OFFSET = 11, 30, 33, 34, 2, 35
# GLSL.std.450
GL_POW, GL_FMIX, GL_STEP, GL_FCLAMP = 26, 46, 48, 43
BUILTIN_POSITION, BUILTIN_VERTEX_INDEX = 0, 42


def text(s):
    b = s.encode() + b'\0'
    b += b'\0' * (-len(b) % 4)
    return list(struct.unpack('<%dI' % (len(b) // 4), b))


def f32(x):
    return struct.unpack('<I', struct.pack('<f', x))[0]


class Module:
    def __init__(self):
        self.words = []
        self.next = 1

    def id(self):
        self.next += 1
        return self.next - 1

    def op(self, name, *operands):
        ops = []
        for o in operands:
            ops.extend(o if isinstance(o, list) else [o])
        self.words.append(((len(ops) + 1) << 16) | OP[name])
        self.words.extend(ops)

    def finish(self):
        return [0x07230203, 0x00010000, 0, self.next, 0] + self.words


def vertex():
    m = Module()
    main, vindex, pos, uv = m.id(), m.id(), m.id(), m.id()
    void, fn, i32, f, v2, v4 = m.id(), m.id(), m.id(), m.id(), m.id(), m.id()
    p_in_i32, p_out_v4, p_out_v2 = m.id(), m.id(), m.id()
    i1, c0, c1, c2 = m.id(), m.id(), m.id(), m.id()

    m.op('Capability', 1)                      # Shader
    m.op('MemoryModel', 0, 1)                  # Logical, GLSL450
    m.op('EntryPoint', 0, main, text('main'), vindex, pos, uv)   # Vertex
    m.op('Decorate', vindex, DEC_BUILTIN, BUILTIN_VERTEX_INDEX)
    m.op('Decorate', pos, DEC_BUILTIN, BUILTIN_POSITION)
    m.op('Decorate', uv, DEC_LOCATION, 0)

    m.op('TypeVoid', void)
    m.op('TypeFunction', fn, void)
    m.op('TypeInt', i32, 32, 1)
    m.op('TypeFloat', f, 32)
    m.op('TypeVector', v2, f, 2)
    m.op('TypeVector', v4, f, 4)
    m.op('TypePointer', p_in_i32, INPUT, i32)
    m.op('TypePointer', p_out_v4, OUTPUT, v4)
    m.op('TypePointer', p_out_v2, OUTPUT, v2)
    m.op('Constant', i32, i1, 1)
    m.op('Constant', f, c0, f32(0.0))
    m.op('Constant', f, c1, f32(1.0))
    m.op('Constant', f, c2, f32(2.0))
    m.op('Variable', p_in_i32, vindex, INPUT)
    m.op('Variable', p_out_v4, pos, OUTPUT)
    m.op('Variable', p_out_v2, uv, OUTPUT)

    m.op('Function', void, main, 0, fn)
    m.op('Label', m.id())
    vi = m.id(); m.op('Load', i32, vi, vindex)
    a = m.id(); m.op('BitwiseAnd', i32, a, vi, i1)
    s = m.id(); m.op('ShiftRightArithmetic', i32, s, vi, i1)
    b = m.id(); m.op('BitwiseAnd', i32, b, s, i1)
    fa = m.id(); m.op('ConvertSToF', f, fa, a)
    fb = m.id(); m.op('ConvertSToF', f, fb, b)
    t = m.id(); m.op('CompositeConstruct', v2, t, fa, fb)
    m.op('Store', uv, t)
    xa = m.id(); m.op('FMul', f, xa, fa, c2)
    x = m.id(); m.op('FSub', f, x, xa, c1)
    ya = m.id(); m.op('FMul', f, ya, fb, c2)
    y = m.id(); m.op('FSub', f, y, ya, c1)
    p = m.id(); m.op('CompositeConstruct', v4, p, x, y, c0, c1)
    m.op('Store', pos, p)
    m.op('Return')
    m.op('FunctionEnd')
    return m.finish()


def fragment():
    m = Module()
    main, uv, color, tex = m.id(), m.id(), m.id(), m.id()
    void, fn, f, v2, v4, img, simg = m.id(), m.id(), m.id(), m.id(), m.id(), m.id(), m.id()
    p_in_v2, p_out_v4, p_tex = m.id(), m.id(), m.id()

    m.op('Capability', 1)
    m.op('MemoryModel', 0, 1)
    m.op('EntryPoint', 4, main, text('main'), uv, color)   # Fragment
    m.op('ExecutionMode', main, 7)                           # OriginUpperLeft
    m.op('Decorate', uv, DEC_LOCATION, 0)
    m.op('Decorate', color, DEC_LOCATION, 0)
    m.op('Decorate', tex, DEC_SET, 0)
    m.op('Decorate', tex, DEC_BINDING, 0)

    m.op('TypeVoid', void)
    m.op('TypeFunction', fn, void)
    m.op('TypeFloat', f, 32)
    m.op('TypeVector', v2, f, 2)
    m.op('TypeVector', v4, f, 4)
    m.op('TypeImage', img, f, 1, 0, 0, 0, 1, 0)   # 2D, non depth, non array, non MS, campionata, formato libero
    m.op('TypeSampledImage', simg, img)
    m.op('TypePointer', p_in_v2, INPUT, v2)
    m.op('TypePointer', p_out_v4, OUTPUT, v4)
    m.op('TypePointer', p_tex, UNIFORM_CONSTANT, simg)
    m.op('Variable', p_in_v2, uv, INPUT)
    m.op('Variable', p_out_v4, color, OUTPUT)
    m.op('Variable', p_tex, tex, UNIFORM_CONSTANT)

    m.op('Function', void, main, 0, fn)
    m.op('Label', m.id())
    s = m.id(); m.op('Load', simg, s, tex)
    c = m.id(); m.op('Load', v2, c, uv)
    out = m.id(); m.op('ImageSampleImplicitLod', v4, out, s, c)
    m.op('Store', color, out)
    m.op('Return')
    m.op('FunctionEnd')
    return m.finish()


def fragment_hdr():
    # Variante per gli swapchain HDR, stessa matematica dello shader HLSL di hook_dxgi.cpp:
    #   x = clamp(c.rgb); lin = pad0 ? x : sRGB->lineare(x)        (c = colore premoltiplicato in sRGB)
    #   scRGB:  rgb = lin * scale,                   a = 1 - (1 - c.a)^2.2
    #   HDR10:  rgb = PQ(BT.2020(lin) * scale nit),  a = c.a
    # Costanti (push constant, ff::ColorConsts): mode, scale, pad0, pad1. HDR10 se mode >= 2.5.
    m = Module()
    main, uv, color, tex, glsl, pc = m.id(), m.id(), m.id(), m.id(), m.id(), m.id()
    void, fn, f, v2, v3, v4, i32, img, simg, st = (m.id() for _ in range(10))
    p_in_v2, p_out_v4, p_tex, p_pc, p_pc_f = m.id(), m.id(), m.id(), m.id(), m.id()

    m.op('Capability', 1)
    m.op('ExtInstImport', glsl, text('GLSL.std.450'))
    m.op('MemoryModel', 0, 1)
    m.op('EntryPoint', 4, main, text('main'), uv, color)
    m.op('ExecutionMode', main, 7)
    m.op('Decorate', uv, DEC_LOCATION, 0)
    m.op('Decorate', color, DEC_LOCATION, 0)
    m.op('Decorate', tex, DEC_SET, 0)
    m.op('Decorate', tex, DEC_BINDING, 0)
    m.op('Decorate', st, DEC_BLOCK)
    for k in range(4):
        m.op('MemberDecorate', st, k, DEC_OFFSET, 4 * k)

    m.op('TypeVoid', void)
    m.op('TypeFunction', fn, void)
    m.op('TypeFloat', f, 32)
    m.op('TypeInt', i32, 32, 1)
    m.op('TypeVector', v2, f, 2)
    m.op('TypeVector', v3, f, 3)
    m.op('TypeVector', v4, f, 4)
    m.op('TypeImage', img, f, 1, 0, 0, 0, 1, 0)
    m.op('TypeSampledImage', simg, img)
    m.op('TypeStruct', st, f, f, f, f)
    m.op('TypePointer', p_in_v2, INPUT, v2)
    m.op('TypePointer', p_out_v4, OUTPUT, v4)
    m.op('TypePointer', p_tex, UNIFORM_CONSTANT, simg)
    m.op('TypePointer', p_pc, PUSH_CONSTANT, st)
    m.op('TypePointer', p_pc_f, PUSH_CONSTANT, f)

    consts = {}

    def cf(x):
        if ('f', x) not in consts:
            i = m.id(); m.op('Constant', f, i, f32(x)); consts[('f', x)] = i
        return consts[('f', x)]

    def c3(x):
        if ('v3', x) not in consts:
            s = cf(x)
            i = m.id(); m.op('ConstantComposite', v3, i, s, s, s); consts[('v3', x)] = i
        return consts[('v3', x)]

    def ci(x):
        i = m.id(); m.op('Constant', i32, i, x); return i

    idx = [ci(k) for k in range(3)]
    k0, k1 = cf(0.0), cf(1.0)
    z3, o3 = c3(0.0), c3(1.0)
    edge3, inv1292, add3, inv1055, gam3 = c3(0.04045), cf(1 / 12.92), c3(0.055), cf(1 / 1.055), c3(2.4)
    g22, half_up = cf(2.2), cf(2.5)
    inv1e4, m1, c13, c2, c3k, m2 = cf(1e-4), c3(0.1593017578125), c3(0.8359375), cf(18.8515625), cf(18.6875), c3(78.84375)
    rows = []
    for r in ((0.627404, 0.329283, 0.043313), (0.069097, 0.919540, 0.011362), (0.016391, 0.088013, 0.895595)):
        a, b, c = cf(r[0]), cf(r[1]), cf(r[2])
        i = m.id(); m.op('ConstantComposite', v3, i, a, b, c); rows.append(i)

    m.op('Variable', p_in_v2, uv, INPUT)
    m.op('Variable', p_out_v4, color, OUTPUT)
    m.op('Variable', p_tex, tex, UNIFORM_CONSTANT)
    m.op('Variable', p_pc, pc, PUSH_CONSTANT)

    def ext(t, inst, *args):
        i = m.id(); m.op('ExtInst', t, i, glsl, inst, *args); return i

    def o(name, t, *args):
        i = m.id(); m.op(name, t, i, *args); return i

    m.op('Function', void, main, 0, fn)
    m.op('Label', m.id())
    s = o('Load', simg, tex)
    coord = o('Load', v2, uv)
    c = o('ImageSampleImplicitLod', v4, s, coord)
    pcv = []
    for k in range(3):
        ptr = o('AccessChain', p_pc_f, pc, idx[k])
        pcv.append(o('Load', f, ptr))
    mode, scale, already = pcv

    rgb = o('VectorShuffle', v3, c, c, 0, 1, 2)
    alpha = o('CompositeExtract', f, c, 3)
    x = ext(v3, GL_FCLAMP, rgb, z3, o3)
    lo = o('VectorTimesScalar', v3, x, inv1292)
    hi = ext(v3, GL_POW, o('VectorTimesScalar', v3, o('FAdd', v3, x, add3), inv1055), gam3)
    sel = ext(v3, GL_STEP, edge3, x)
    lin = ext(v3, GL_FMIX, lo, hi, sel)
    already3 = o('CompositeConstruct', v3, already, already, already)
    lin = ext(v3, GL_FMIX, lin, x, already3)

    # scRGB
    scr = o('VectorTimesScalar', v3, lin, scale)
    inv_a = ext(f, GL_FCLAMP, o('FSub', f, k1, alpha), k0, k1)
    a_lin = o('FSub', f, k1, ext(f, GL_POW, inv_a, g22))

    # HDR10
    r2020 = o('CompositeConstruct', v3, *[o('Dot', f, row, lin) for row in rows])
    n = o('VectorTimesScalar', v3, o('VectorTimesScalar', v3, r2020, scale), inv1e4)
    y = ext(v3, GL_POW, ext(v3, GL_FCLAMP, n, z3, o3), m1)
    num = o('FAdd', v3, c13, o('VectorTimesScalar', v3, y, c2))
    den = o('FAdd', v3, o3, o('VectorTimesScalar', v3, y, c3k))
    pq = ext(v3, GL_POW, o('FDiv', v3, num, den), m2)

    is_pq = ext(f, GL_STEP, half_up, mode)
    is_pq3 = o('CompositeConstruct', v3, is_pq, is_pq, is_pq)
    out_rgb = ext(v3, GL_FMIX, scr, pq, is_pq3)
    out_a = ext(f, GL_FMIX, a_lin, alpha, is_pq)
    r0 = o('CompositeExtract', f, out_rgb, 0)
    r1 = o('CompositeExtract', f, out_rgb, 1)
    r2 = o('CompositeExtract', f, out_rgb, 2)
    out = o('CompositeConstruct', v4, r0, r1, r2, out_a)
    m.op('Store', color, out)
    m.op('Return')
    m.op('FunctionEnd')
    return m.finish()


def emit(name, words):
    lines = ['static const uint32_t %s[] = {' % name]
    for i in range(0, len(words), 8):
        lines.append('    ' + ' '.join('0x%08x,' % w for w in words[i:i + 8]))
    lines.append('};')
    return '\n'.join(lines)


here = os.path.dirname(os.path.abspath(__file__))
with open(os.path.join(here, 'vk_shaders.h'), 'w', encoding='utf-8', newline='\n') as out:
    out.write('// Generato da gen_vk_shaders.py: non modificare a mano.\n#pragma once\n#include <cstdint>\n\n')
    out.write(emit('kOverlayVertSpv', vertex()) + '\n\n' + emit('kOverlayFragSpv', fragment()) + '\n\n' +
              emit('kOverlayFragHdrSpv', fragment_hdr()) + '\n')
print('vk_shaders.h scritto')
