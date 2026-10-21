"""Throwaway mock Match Door (TLS + MTGA framing) used to explore what the client expects.
Run: python tools\\mock\\mock_matchdoor.py [port]   (log: tools\\mock\\mock.log)
Frame: [ver=4][type|fmt<<4][int32 LE len][body]; type Msg=1 Ping=2 Pong=3; fmt Protobuf=2
"""
import os, ssl, socket, struct, sys, threading, time, datetime, traceback

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, 'gen'))
import MatchServiceMessages_pb2 as ms
import GREMessages_pb2 as gm
import GREMessagesCommon_pb2 as gc
import ServiceMessagesCommon_pb2 as sc

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 30003
LOG = open(os.path.join(HERE, 'mock.log'), 'a', encoding='utf-8')


def log(*a):
    s = datetime.datetime.now().strftime('%H:%M:%S.%f')[:-3] + ' ' + ' '.join(str(x) for x in a)
    print(s, flush=True)
    LOG.write(s + '\n'); LOG.flush()


def enum(mod_enum_name, suffix, *mods):
    """Find enum value whose name ends with _suffix in any module (names are prefixed with the enum name)."""
    for m in mods:
        ed = m.DESCRIPTOR.enum_types_by_name.get(mod_enum_name)
        if ed:
            for v in ed.values:
                if v.name == f'{mod_enum_name}_{suffix}' or v.name == suffix:
                    return v.number
    raise KeyError(f'{mod_enum_name}.{suffix}')


def ensure_cert():
    cert, key = os.path.join(HERE, 'cert.pem'), os.path.join(HERE, 'key.pem')
    if os.path.exists(cert) and os.path.exists(key):
        return cert, key
    from cryptography import x509
    from cryptography.x509.oid import NameOID
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import rsa
    import ipaddress
    k = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, 'localhost')])
    c = (x509.CertificateBuilder().subject_name(name).issuer_name(name).public_key(k.public_key())
         .serial_number(x509.random_serial_number())
         .not_valid_before(datetime.datetime.utcnow() - datetime.timedelta(days=1))
         .not_valid_after(datetime.datetime.utcnow() + datetime.timedelta(days=3650))
         .add_extension(x509.SubjectAlternativeName([x509.DNSName('localhost'), x509.IPAddress(ipaddress.ip_address('127.0.0.1'))]), False)
         .sign(k, hashes.SHA256()))
    open(cert, 'wb').write(c.public_bytes(serialization.Encoding.PEM))
    open(key, 'wb').write(k.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.TraditionalOpenSSL, serialization.NoEncryption()))
    return cert, key


def recv_exact(sock, n):
    buf = b''
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            raise ConnectionError('closed')
        buf += chunk
    return buf


def read_frame(sock):
    ver = recv_exact(sock, 1)[0]
    if ver >= 4:
        tb = recv_exact(sock, 1)[0]
        typ, fmt = tb & 0xF, (tb >> 4) & 0xF
    elif ver == 3:
        typ, fmt = recv_exact(sock, 1)[0] & 0xF, 2
    else:
        typ, fmt = 1, 2
    n = struct.unpack('<i', recv_exact(sock, 4))[0]
    return ver, typ, fmt, recv_exact(sock, n)


def send_frame(sock, typ, body, fmt=2):
    sock.sendall(bytes([4, (typ & 0xF) | (fmt << 4)]) + struct.pack('<i', len(body)) + body)


class Session:
    def __init__(self, sock):
        self.sock = sock
        self.lock = threading.Lock()
        self.msg_id = 0
        self.gs_id = 0
        self.user = ''

    def send_svc(self, m, req_id=0):
        m.requestId = req_id
        m.timestamp = int(time.time() * 1000)
        with self.lock:
            send_frame(self.sock, 1, m.SerializeToString())

    def send_gre(self, msgs):
        m = ms.MatchServiceToClientMessage()
        for g in msgs:
            self.msg_id += 1
            g.msgId = self.msg_id
            m.greToClientEvent.greToClientMessages.add().CopyFrom(g)
        log('-> GreToClientEvent', [gc_name(g.type) for g in msgs])
        self.send_svc(m)

    def run(self):
        threading.Thread(target=self.pinger, daemon=True).start()
        while True:
            ver, typ, fmt, body = read_frame(self.sock)
            if typ == 2:      # ping -> pong
                with self.lock:
                    send_frame(self.sock, 3, body)
                continue
            if typ == 3:
                continue
            m = ms.ClientToMatchServiceMessage()
            m.ParseFromString(body)
            kind = ms.ClientToMatchServiceMessageType.Name(m.clientToMatchServiceMessageType)
            log('<-', kind, 'req', m.requestId)
            try:
                self.handle(m, kind)
            except Exception:
                log('handler error', traceback.format_exc())

    def pinger(self):
        while True:
            time.sleep(5)
            try:
                with self.lock:
                    send_frame(self.sock, 2, struct.pack('<i', int(time.time()) & 0x7FFFFFFF))
            except Exception:
                return

    def handle(self, m, kind):
        if kind.endswith('AuthenticateRequest'):
            r = sc.AuthenticateRequest(); r.ParseFromString(m.payload)
            self.user = r.clientId
            log('   auth clientId=%s version=%s type=%s' % (r.clientId, r.clientInfo.clientVersion, r.clientInfo.clientType))
            out = ms.MatchServiceToClientMessage()
            out.authenticateResponse.clientId = r.clientId
            out.authenticateResponse.sessionId = 'local-session'
            out.authenticateResponse.screenName = 'Forge Player'
            out.transactionId = m.transactionId
            self.send_svc(out, m.requestId)
        elif kind.endswith('ClientToMatchDoorConnectRequest'):
            r = ms.ClientToMatchDoorConnectRequest(); r.ParseFromString(m.payload)
            log('   connect matchId=%s fabric=%s' % (r.matchId, r.mcFabricUri))
            c = gm.ClientToGREMessage(); c.ParseFromString(r.clientToGreMessageBytes)
            log('   inner ConnectReq:', str(c).replace('\n', ' ')[:400])
            self.on_connect(c)
        elif kind.endswith('ClientToGREMessage') or kind.endswith('ClientToGREUIMessage'):
            c = gm.ClientToGREMessage(); c.ParseFromString(m.payload)
            log('   GRE msg', gc.ClientMessageType.Name(c.type) if c.type in gc.ClientMessageType.values() else c.type, str(c).replace('\n', ' ')[:500])
            self.on_gre(c)
        else:
            log('   (unhandled)')

    # ---- game logic stubs, to be replaced by Forge ----
    DECK1 = [107496] * 16 + [93940] * 4 + [79334] * 20      # Forest, Llanowar Elves, Grizzly Bears
    DECK2 = [107495] * 16 + [105816] * 24                     # Mountain, Lightning Bolt

    def on_connect(self, c):
        g = gm.GREToClientMessage()
        g.type = enum('GREMessageType', 'ConnectResp', gc)
        g.systemSeatIds.append(1)
        g.connectResp.status = enum('ConnectionStatus', 'Success', gc)
        g.connectResp.protoVer = c.connectReq.protoVer
        g.connectResp.deckMessage.deckCards.extend(self.DECK1)
        msgs = [g]
        msgs += self.initial_state()
        self.send_gre(msgs)
        # room state: Playing
        out = ms.MatchServiceToClientMessage()
        info = out.matchGameRoomStateChangedEvent.gameRoomInfo
        info.stateType = enum('MatchGameRoomStateType', 'Playing', ms)
        info.gameRoomConfig.matchId = 'local-match-1'
        self.send_svc(out)

    def initial_state(self):
        E = lambda n, s: enum(n, s, gc)
        # zone ids
        Z = {'stack': 27, 'bf': 28, 'exile': 29, 'limbo': 30, 'command': 26, 'revealed': 25}
        for seat, base in ((1, 31), (2, 41)):
            Z[(seat, 'lib')], Z[(seat, 'hand')], Z[(seat, 'gy')] = base, base + 1, base + 2
        gs = gm.GameStateMessage()
        gs.type = E('GameStateType', 'Full')
        self.gs_id += 1
        gs.gameStateId = self.gs_id
        gi = gs.gameInfo
        gi.matchID = 'local-match-1'
        gi.gameNumber = 1
        gi.stage = E('GameStage', 'Start')
        gi.type = E('GameType', 'Duel')
        gi.variant = E('GameVariant', 'Normal')
        gi.matchState = E('MatchState', 'GameInProgress')
        gi.matchWinCondition = E('MatchWinCondition', 'SingleElimination')
        gi.maxTimeoutCount = 3
        gi.timeoutDurationSec = 75
        gi.superFormat = E('SuperFormat', 'Constructed')
        gi.mulliganType = E('MulliganType', 'London')
        gi.freeMulliganCount = 0
        gi.deckConstraintInfo.minDeckSize = 40
        gi.deckConstraintInfo.maxDeckSize = 250
        for seat in (1, 2):
            t = gs.teams.add(); t.id = seat; t.playerIds.append(seat); t.status = E('TeamStatus', 'InGame')
            p = gs.players.add()
            p.lifeTotal = 20; p.startingLifeTotal = 20; p.systemSeatNumber = seat; p.status = E('PlayerStatus', 'InGame')
            p.maxHandSize = 7; p.teamId = seat; p.controllerSeatId = seat; p.controllerType = E('ControllerType', 'Player')
        gs.turnInfo.activePlayer = 1      # starting player (client dereferences ActivePlayer in the mulligan UI)
        gs.turnInfo.decisionPlayer = 1
        # zones + objects
        inst = [100]
        zones = {}

        def zone(zid, ztype, vis, owner):
            z = gs.zones.add(); z.zoneId = zid; z.type = E('ZoneType', ztype); z.visibility = E('Visibility', vis); z.ownerSeatId = owner
            zones[zid] = z
            return z
        for seat in (1, 2):
            zone(Z[(seat, 'lib')], 'Library', 'Hidden', seat)
            zone(Z[(seat, 'hand')], 'Hand', 'Private', seat)
            zone(Z[(seat, 'gy')], 'Graveyard', 'Public', seat)
        zone(Z['stack'], 'Stack', 'Public', 0)
        zone(Z['bf'], 'Battlefield', 'Public', 0)
        zone(Z['exile'], 'Exile', 'Public', 0)
        zone(Z['limbo'], 'Limbo', 'Public', 0)
        zone(Z['command'], 'Command', 'Public', 0)
        zone(Z['revealed'], 'Revealed', 'Public', 1)

        def add_obj(seat, grp, zid, known_to):
            inst[0] += 1
            o = gs.gameObjects.add()
            o.instanceId = inst[0]
            o.grpId = grp if known_to else 0
            o.type = E('GameObjectType', 'Card')
            o.zoneId = zid
            o.visibility = E('Visibility', 'Private' if zones[zid].type == E('ZoneType', 'Hand') else 'Hidden')
            o.ownerSeatId = seat; o.controllerSeatId = seat
            if known_to:
                o.viewers.append(known_to)
            zones[zid].objectInstanceIds.append(o.instanceId)
            if known_to:
                zones[zid].viewers.append(known_to) if known_to not in zones[zid].viewers else None
            return o
        import random
        for seat, deck in ((1, self.DECK1), (2, self.DECK2)):
            cards = list(deck); random.shuffle(cards)
            if seat == 1: cards.sort(key=lambda g: g != 107496)   # deterministic: Forest is instance 101 (test clicks)
            for i, grp in enumerate(cards):
                zid = Z[(seat, 'hand')] if i < 7 else Z[(seat, 'lib')]
                add_obj(seat, grp, zid, seat if i < 7 else 0)
        self.Z = Z
        self.cur = {o.instanceId: [o.ownerSeatId, o.grpId, o.zoneId] for o in gs.gameObjects}
        self.ann_id = 0
        m = gm.GREToClientMessage()
        m.type = E('GREMessageType', 'GameStateMessage')
        m.systemSeatIds.extend([1])
        m.gameStateId = gs.gameStateId
        m.gameStateMessage.CopyFrom(gs)
        mull = gm.GREToClientMessage()
        mull.type = E('GREMessageType', 'MulliganReq')
        mull.systemSeatIds.append(1)
        mull.gameStateId = gs.gameStateId
        mull.mulliganReq.mulliganType = E('MulliganType', 'London')
        mull.mulliganReq.freeMulliganCount = 0
        mull.mulliganReq.mulliganCount = 0
        mull.prompt.promptId = 34
        prm = mull.prompt.parameters.add()
        prm.parameterName = 'NumberOfCards'
        prm.type = E('ParameterType', 'Number')
        prm.numberValue = 7
        return [m, mull]

    def E(self, n, s):
        return enum(n, s, gc)

    def diff_msg(self, gs):
        m = gm.GREToClientMessage()
        m.type = self.E('GREMessageType', 'GameStateMessage')
        m.systemSeatIds.append(1)
        m.gameStateId = gs.gameStateId
        m.gameStateMessage.CopyFrom(gs)
        return m

    def new_diff(self):
        gs = gm.GameStateMessage()
        gs.type = self.E('GameStateType', 'Diff')
        prev = self.gs_id
        self.gs_id += 1
        gs.gameStateId = self.gs_id
        gs.prevGameStateId = prev
        return gs

    def hand_zone(self, gs, seat=1):
        z = gs.zones.add(); z.zoneId = self.Z[(seat, 'hand')]; z.type = self.E('ZoneType', 'Hand')
        z.visibility = self.E('Visibility', 'Private'); z.ownerSeatId = seat; z.viewers.append(seat)
        z.objectInstanceIds.extend(i for i, v in self.cur.items() if v[2] == z.zoneId)
        return z

    def actions_req(self, can_play_land=True):
        a = gm.GREToClientMessage()
        a.type = self.E('GREMessageType', 'ActionsAvailableReq')
        a.systemSeatIds.append(1)
        a.gameStateId = self.gs_id
        if can_play_land:
            for iid, (seat, grp, zid) in self.cur.items():
                if seat == 1 and zid == self.Z[(1, 'hand')] and grp == 107496:    # Forest
                    ac = a.actionsAvailableReq.actions.add()
                    ac.actionType = self.E('ActionType', 'Play'); ac.grpId = grp; ac.instanceId = iid
        ac = a.actionsAvailableReq.actions.add(); ac.actionType = self.E('ActionType', 'Pass')
        return a

    def turn_diff(self, turn, active, phase, step):
        gs = self.new_diff()
        ti = gs.turnInfo
        ti.phase = self.E('Phase', phase); ti.step = self.E('Step', step) if step else 0
        ti.turnNumber = turn; ti.activePlayer = active; ti.priorityPlayer = active; ti.decisionPlayer = active
        return gs

    def next_turns(self):
        # opponent's turn 2 (no plays), then our turn 3 with a draw
        gs = self.turn_diff(2, 2, 'Beginning', 'Upkeep')
        self.ann_id += 1
        an = gs.annotations.add(); an.id = self.ann_id; an.affectorId = 2; an.type.append(self.E('AnnotationType', 'NewTurnStarted')); an.affectedIds.append(2)
        self.send_gre([self.diff_msg(gs)])
        time.sleep(1.0)
        top = next(i for i, v in self.cur.items() if v[0] == 1 and v[2] == self.Z[(1, 'lib')])
        self.cur[top][2] = self.Z[(1, 'hand')]
        gs = self.turn_diff(3, 1, 'Main1', None)
        o = gs.gameObjects.add(); o.instanceId = top; o.grpId = self.cur[top][1]
        o.type = self.E('GameObjectType', 'Card'); o.zoneId = self.Z[(1, 'hand')]
        o.visibility = self.E('Visibility', 'Private'); o.ownerSeatId = 1; o.controllerSeatId = 1; o.viewers.append(1)
        self.hand_zone(gs)
        z = gs.zones.add(); z.zoneId = self.Z[(1, 'lib')]; z.type = self.E('ZoneType', 'Library'); z.visibility = self.E('Visibility', 'Hidden'); z.ownerSeatId = 1
        z.objectInstanceIds.extend(i for i, v in self.cur.items() if v[2] == z.zoneId)
        self.ann_id += 1
        an = gs.annotations.add(); an.id = self.ann_id; an.affectorId = 1; an.affectedIds.append(top); an.type.append(self.E('AnnotationType', 'ZoneTransfer'))
        for k, v in (('zone_src', self.Z[(1, 'lib')]), ('zone_dest', self.Z[(1, 'hand')])):
            d = an.details.add(); d.key = k; d.type = self.E('KeyValuePairValueType', 'int32'); d.value_int32.append(v)
        d = an.details.add(); d.key = 'category'; d.type = self.E('KeyValuePairValueType', 'string'); d.value_string.append('Draw')
        self.send_gre([self.diff_msg(gs), self.actions_req()])

    def on_gre(self, c):
        t = gc.ClientMessageType.Name(c.type) if c.type in gc.ClientMessageType.values() else str(c.type)
        if t.endswith('MulliganResp'):
            if c.mulliganResp.decision == self.E('MulliganOption', 'AcceptHand'):
                gs = self.new_diff()
                gs.gameInfo.stage = self.E('GameStage', 'Play')
                ti = gs.turnInfo
                ti.phase = self.E('Phase', 'Main1'); ti.step = 0; ti.turnNumber = 1
                ti.activePlayer = 1; ti.priorityPlayer = 1; ti.decisionPlayer = 1
                self.send_gre([self.diff_msg(gs), self.actions_req()])
            else:
                log('   (mulligan requested; not implemented in mock)')
        elif t.endswith('PerformActionResp'):
            for a in c.performActionResp.actions:
                at = gc.ActionType.Name(a.actionType)
                log('   action', at, a.instanceId, a.grpId)
                if at.endswith('Play') and a.instanceId in self.cur:
                    old = self.cur[a.instanceId][2]
                    self.cur[a.instanceId][2] = self.Z['bf']
                    gs = self.new_diff()
                    o = gs.gameObjects.add(); o.instanceId = a.instanceId; o.grpId = self.cur[a.instanceId][1]
                    o.type = self.E('GameObjectType', 'Card'); o.zoneId = self.Z['bf']
                    o.visibility = self.E('Visibility', 'Public'); o.ownerSeatId = 1; o.controllerSeatId = 1
                    o.cardTypes.append(self.E('CardType', 'Land')); o.subtypes.append(self.E('SubType', 'Forest'))
                    o.color.extend([]); 
                    self.hand_zone(gs)
                    z = gs.zones.add(); z.zoneId = self.Z['bf']; z.type = self.E('ZoneType', 'Battlefield')
                    z.visibility = self.E('Visibility', 'Public')
                    z.objectInstanceIds.extend(i for i, v in self.cur.items() if v[2] == self.Z['bf'])
                    self.ann_id += 1
                    an = gs.annotations.add(); an.id = self.ann_id; an.affectorId = 1
                    an.affectedIds.append(a.instanceId); an.type.append(self.E('AnnotationType', 'ZoneTransfer'))
                    for k, v in (('zone_src', old), ('zone_dest', self.Z['bf'])):
                        d = an.details.add(); d.key = k; d.type = self.E('KeyValuePairValueType', 'int32'); d.value_int32.append(v)
                    d = an.details.add(); d.key = 'category'; d.type = self.E('KeyValuePairValueType', 'string'); d.value_string.append('PlayLand')
                    gs.turnInfo.priorityPlayer = 1; gs.turnInfo.decisionPlayer = 1
                    self.send_gre([self.diff_msg(gs), self.actions_req(can_play_land=False)])
                elif at.endswith('Pass'):
                    self.next_turns()


def gc_name(t):
    try:
        return gc.GREMessageType.Name(t)
    except Exception:
        return str(t)


def main():
    cert, key = ensure_cert()
    ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    ctx.minimum_version = ssl.TLSVersion.TLSv1_2
    ctx.load_cert_chain(cert, key)
    srv = socket.socket(); srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind(('127.0.0.1', PORT)); srv.listen(5)
    log('mock match door listening on 127.0.0.1:%d' % PORT)
    while True:
        raw, addr = srv.accept()
        log('connection from', addr)
        try:
            s = ctx.wrap_socket(raw, server_side=True)
        except Exception as e:
            log('TLS failed:', e); continue
        threading.Thread(target=lambda: Session(s).run(), daemon=True).start()


if __name__ == '__main__':
    main()
