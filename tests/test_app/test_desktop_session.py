import json
import os
from pathlib import Path
import subprocess
import sys

import pytest


def test_tools_entry_opens_manager(qtbot, monkeypatch):
    from PySide6.QtWidgets import QWidget
    from app.tools_interface import ToolsInterface
    monkeypatch.delenv('MARCH7TH_DESKTOP_SESSION', raising=False)

    class Parent(QWidget):
        count = 0

        def openDesktopSession(self):
            self.count += 1

    parent = Parent()
    qtbot.addWidget(parent)
    tools = ToolsInterface(parent)
    assert tools.desktopSessionCard.isEnabled() == (sys.platform == 'win32')
    tools.desktopSessionCard.clicked.emit()
    assert parent.count == 1


def test_child_pause_path_is_independent(qapp, monkeypatch):
    from app.log_interface import LogInterface
    from utils import windows_session
    monkeypatch.setattr(windows_session, 'process_session_id', lambda: 77)
    monkeypatch.delenv('MARCH7TH_DESKTOP_SESSION', raising=False)
    parent_path = LogInterface._pauseControlPath(None)
    monkeypatch.setenv('MARCH7TH_DESKTOP_SESSION', '1')
    child_path = LogInterface._pauseControlPath(None)
    assert child_path != parent_path
    assert '77' in child_path


@pytest.mark.skipif(sys.platform != 'win32', reason='Native Windows named pipe')
def test_native_pipe_to_qt_agent(qtbot, monkeypatch):
    helper = Path(__file__).resolve().parents[2] / 'build' / 'desktop-session' / 'March7th.Desktop.exe'
    if not helper.exists():
        pytest.skip('Build native helper with python tools/build_desktop_session.py first')
    from module.desktop_session import agent, native_input
    from unittest.mock import Mock
    movements = []

    class Mouse:
        def __init__(self, _):
            pass

        def is_active(self):
            return True

        def send(self, samples):
            movements.extend(samples)
            return True

    monkeypatch.setattr(agent, 'GameMouse', Mouse)
    # Test mode runs in this session: change only the session check; the actual
    # Win32 pipe-server PID validation and Qt socket descriptor remain real.
    monkeypatch.setattr(native_input, 'process_session_id', lambda pid=None: 2 if pid else 1)
    process = subprocess.Popen([str(helper), '--pipe-test'], stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                               stdin=subprocess.PIPE, creationflags=subprocess.CREATE_NO_WINDOW)
    receiver = None
    try:
        line = process.stdout.readline()
        address = json.loads(line.decode('utf-8'))
        assert address['type'] == 'pipe'
        receiver = agent.DesktopSessionAgent(address['message'], process.pid, lambda: 'StarRail.exe', Mock())
        qtbot.waitUntil(lambda: process.poll() is not None, timeout=15000)
        output, error = process.communicate(timeout=2)
        assert process.returncode == 0, (output, error)
        assert movements == [[7, -4]]
    finally:
        if receiver:
            receiver.close()
        if process.poll() is None:
            process.terminate()
            process.wait(timeout=5)


@pytest.mark.skipif(sys.platform != 'win32', reason='Native Windows host')
def test_native_host_graceful_shutdown():
    helper = Path(__file__).resolve().parents[2] / 'build' / 'desktop-session' / 'March7th.Desktop.exe'
    if not helper.exists():
        pytest.skip('Build native helper first')
    result = subprocess.run([str(helper), '--lifecycle-test'], input=b'shutdown\n', capture_output=True,
                            creationflags=subprocess.CREATE_NO_WINDOW, timeout=15)
    assert result.returncode == 0, result.stderr
    messages = [json.loads(line) for line in result.stdout.decode('utf-8').splitlines() if line.strip()]
    assert any(message['type'] == 'test' for message in messages), messages
