"""Length-prefixed JSON shared with the native desktop host (protocol v1)."""
import json
import struct
import time

MAX_FRAME = 65536


def encode_frame(message):
    payload = json.dumps(message, ensure_ascii=False, separators=(',', ':')).encode('utf-8')
    if not 0 < len(payload) <= MAX_FRAME:
        raise ValueError('分身消息长度超出限制')
    return struct.pack('<I', len(payload)) + payload


class FrameDecoder:
    def __init__(self):
        self.buffer = bytearray()

    def feed(self, data):
        self.buffer.extend(data)
        messages = []
        while len(self.buffer) >= 4:
            length, = struct.unpack_from('<I', self.buffer)
            if not 0 < length <= MAX_FRAME:
                raise ValueError('分身消息长度超出限制')
            if len(self.buffer) < 4 + length:
                break
            payload = bytes(self.buffer[4:4 + length])
            del self.buffer[:4 + length]
            message = json.loads(payload)
            if not isinstance(message, dict):
                raise ValueError('分身消息必须是对象')
            messages.append(message)
        return messages


class MouseReceiver:
    """Reject duplicates/stale input and recheck the foreground before every batch."""
    def __init__(self, is_game_active, send_relative, now=None):
        self.is_game_active = is_game_active
        self.send_relative = send_relative
        self.now = now or (lambda: int(time.time() * 1000))
        self.last_sequence = 0

    def handle(self, message):
        if message.get('op') != 'mouse':
            raise ValueError('未知分身输入消息')
        epoch, sequence = message.get('epoch'), message.get('seq')
        sent_at, samples = message.get('sent_at'), message.get('samples')
        if any(type(value) is not int for value in (epoch, sequence, sent_at)):
            raise ValueError('分身输入序号无效')
        if not isinstance(samples, list) or len(samples) > 64:
            raise ValueError('分身输入样本数量无效')
        for sample in samples:
            if not isinstance(sample, list) or len(sample) != 2 or any(
                    type(value) is not int or abs(value) > 32767 for value in sample):
                raise ValueError('分身相对鼠标位移无效')
        handled = False
        fresh = sequence > self.last_sequence and -100 <= self.now() - sent_at <= 250
        self.last_sequence = max(self.last_sequence, sequence)
        if fresh and self.is_game_active():
            handled = bool(self.send_relative(samples)) if samples else True
        return {'op': 'ack', 'epoch': epoch, 'seq': sequence, 'handled': handled}
