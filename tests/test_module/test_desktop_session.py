import json
import struct
import sys

import pytest

from module.desktop_session.protocol import FrameDecoder, MAX_FRAME, MouseReceiver, encode_frame


def mouse_message(**changes):
    message = {'op': 'mouse', 'epoch': 1, 'seq': 1, 'sent_at': 1000, 'samples': [[12, -4]]}
    message.update(changes)
    return message


def test_fragmented_and_combined_frames():
    first, second = {'op': 'hello', 'version': 1}, mouse_message()
    wire = encode_frame(first) + encode_frame(second)
    decoder = FrameDecoder()
    messages = []
    for byte in wire:
        messages.extend(decoder.feed(bytes([byte])))
    assert messages == [first, second]
    assert decoder.feed(wire) == [first, second]


@pytest.mark.parametrize('wire', [struct.pack('<I', 0), struct.pack('<I', MAX_FRAME + 1),
                                 struct.pack('<I', 2) + b'[]', struct.pack('<I', 1) + b'x'])
def test_rejects_invalid_frames(wire):
    with pytest.raises(ValueError):
        FrameDecoder().feed(wire)


def test_mouse_requires_foreground_and_successful_send():
    samples = []
    active = False
    receiver = MouseReceiver(lambda: active, lambda batch: samples.extend(batch) or True, now=lambda: 1000)
    assert receiver.handle(mouse_message())['handled'] is False
    assert samples == []
    active = True
    assert receiver.handle(mouse_message(seq=2))['handled'] is True
    assert samples == [[12, -4]]
    receiver.send_relative = lambda _: False
    assert receiver.handle(mouse_message(seq=3))['handled'] is False


def test_duplicate_and_stale_movement_never_replayed():
    samples = []
    receiver = MouseReceiver(lambda: True, lambda batch: samples.extend(batch) or True, now=lambda: 1000)
    assert receiver.handle(mouse_message())['handled']
    assert not receiver.handle(mouse_message())['handled']
    assert not receiver.handle(mouse_message(seq=2, sent_at=749))['handled']
    assert not receiver.handle(mouse_message(seq=3, sent_at=1101))['handled']
    assert samples == [[12, -4]]


@pytest.mark.parametrize('changes', [{'seq': True}, {'samples': [[1.2, 0]]}, {'samples': [[True, 0]]},
                                    {'samples': [[32768, 0]]}, {'samples': [[1, 0]] * 65}, {'samples': 'bad'}])
def test_malformed_input_rejected_before_simulation(changes):
    def never(_):
        pytest.fail('Malformed input reached SendInput')
    receiver = MouseReceiver(lambda: True, never, now=lambda: 1000)
    with pytest.raises(ValueError):
        receiver.handle(mouse_message(**changes))


def test_child_configuration_copied_once_and_inherited(tmp_path, monkeypatch):
    from module.desktop_session.bootstrap import prepare_child_environment
    monkeypatch.setenv('MARCH7TH_CONFIG_PATH', '')
    monkeypatch.setenv('MARCH7TH_DESKTOP_SESSION', '')
    (tmp_path / 'config.yaml').write_text('game_path: initial', encoding='utf-8')
    destination = prepare_child_environment(tmp_path)
    destination.write_text('game_path: child', encoding='utf-8')
    (tmp_path / 'config.yaml').write_text('game_path: parent', encoding='utf-8')
    assert prepare_child_environment(tmp_path) == destination
    assert destination.read_text(encoding='utf-8') == 'game_path: child'
    import os
    assert os.environ['MARCH7TH_CONFIG_PATH'] == str(destination.resolve())


@pytest.mark.skipif(sys.platform != 'win32', reason='Windows ABI')
def test_windows_input_layout_and_session_scope():
    import ctypes
    from module.desktop_session.native_input import Input, MouseInput
    from utils.windows_session import instance_scope, process_session_id
    assert ctypes.sizeof(Input) == (40 if ctypes.sizeof(ctypes.c_void_p) == 8 else 28)
    assert ctypes.sizeof(MouseInput) == (32 if ctypes.sizeof(ctypes.c_void_p) == 8 else 24)
    assert instance_scope().endswith(f'_{process_session_id()}')


@pytest.mark.skipif(sys.platform != 'win32', reason='Windows process isolation')
def test_stop_game_only_terminates_current_session(monkeypatch):
    from unittest.mock import Mock
    from module.game import local
    from utils import windows_session
    current, other = Mock(), Mock()
    current.info = {'pid': 101, 'name': 'StarRail.exe', 'username': 'PC\\tester'}
    other.info = {'pid': 202, 'name': 'StarRail.exe', 'username': 'PC\\tester'}
    monkeypatch.setattr(local.getpass, 'getuser', lambda: 'tester')
    monkeypatch.setattr(local.psutil, 'process_iter', lambda **_: [current, other])
    monkeypatch.setattr(local.psutil, 'Process', lambda pid: {101: current, 202: other}[pid])
    monkeypatch.setattr(windows_session, 'process_session_id', lambda pid=None: 2 if pid == 202 else 1)
    assert local.LocalGameController.terminate_named_process('StarRail.exe')
    current.terminate.assert_called_once()
    other.terminate.assert_not_called()
