import json
import os
import subprocess
import sys
from pathlib import Path

from PySide6.QtCore import QObject, QProcess, Signal


class DesktopSessionManager(QObject):
    statusChanged = Signal(str)
    errorOccurred = Signal(str)

    def __init__(self, parent=None):
        super().__init__(parent)
        self.process = None
        self.builder = None
        self._buffer = bytearray()
        self._build_output = bytearray()
        self._closing = False
        self.root = Path(sys.executable).parent if getattr(sys, 'frozen', False) else Path(__file__).resolve().parents[2]

    def open(self, title):
        self.title = title
        if sys.platform != 'win32' or os.environ.get('MARCH7TH_DESKTOP_SESSION') == '1':
            self.errorOccurred.emit('桌面分身只能从 Windows 主桌面启动。')
            return
        if self.process and self.process.state() != QProcess.ProcessState.NotRunning:
            self.process.write(b'show\n')
            return
        if self.builder and self.builder.state() != QProcess.ProcessState.NotRunning:
            self.statusChanged.emit('正在构建桌面分身，请稍候。')
            return
        if getattr(sys, 'frozen', False):
            helper = Path(sys._MEIPASS) / 'desktop_session' / 'March7th.Desktop.exe'
            if not helper.exists():
                self.errorOccurred.emit('未找到桌面分身程序，请重新解压完整发行包。')
                return
            self._launch(helper)
            return
        # Build outside the GUI thread, including the first SDK restore.
        self.builder = QProcess(self)
        self.builder.setWorkingDirectory(str(self.root))
        self.builder.setProcessChannelMode(QProcess.ProcessChannelMode.MergedChannels)
        self._build_output.clear()
        self.builder.readyReadStandardOutput.connect(self._read_build)
        self.builder.errorOccurred.connect(lambda _: self.errorOccurred.emit('无法启动桌面分身构建程序。'))
        self.builder.finished.connect(self._build_finished)
        self.statusChanged.emit('正在准备桌面分身；源码首次启动需要构建 Windows 辅助程序。')
        self.builder.start(sys.executable, [str(self.root / 'tools' / 'build_desktop_session.py')])

    def _read_build(self):
        self._build_output.extend(bytes(self.builder.readAllStandardOutput()))
        del self._build_output[:-16384]

    def _build_finished(self, code, status):
        self._read_build()
        if self._closing:
            return
        if code or status != QProcess.ExitStatus.NormalExit:
            self.errorOccurred.emit(self._build_output.decode('utf-8', errors='replace'))
            return
        self._launch(self.root / 'build' / 'desktop-session' / 'March7th.Desktop.exe')

    def _launch(self, helper):
        if self._closing:
            return
        self.process = QProcess(self)
        self.process.setWorkingDirectory(str(self.root))
        self.process.setProcessChannelMode(QProcess.ProcessChannelMode.MergedChannels)
        self._buffer.clear()
        self.process.readyReadStandardOutput.connect(self._read)
        self.process.errorOccurred.connect(lambda _: self.errorOccurred.emit('桌面分身程序启动失败。'))
        self.process.finished.connect(lambda code, _: self.statusChanged.emit(
            '桌面分身已关闭。' if code == 0 else '桌面分身已退出，请检查日志。'))
        arguments = [] if getattr(sys, 'frozen', False) else [str(self.root / 'app.py')]
        self.process.start(str(helper), ['--owner-pid', str(os.getpid()), '--launch-exe', sys.executable,
                                        '--launch-args', subprocess.list2cmdline(arguments),
                                        '--working-directory', str(self.root), '--title', self.title])

    def _read(self):
        self._buffer.extend(bytes(self.process.readAllStandardOutput()))
        while b'\n' in self._buffer:
            line, _, rest = self._buffer.partition(b'\n')
            self._buffer = bytearray(rest)
            try:
                message = json.loads(line.decode('utf-8-sig'))
            except (ValueError, UnicodeError):
                continue
            signal = self.errorOccurred if message.get('type') == 'error' else self.statusChanged
            signal.emit(message.get('message', ''))
        if len(self._buffer) > 65536:
            self._buffer.clear()

    def shutdown(self):
        self._closing = True
        if self.process and self.process.state() != QProcess.ProcessState.NotRunning:
            self.process.write(b'shutdown\n')
            self.process.waitForBytesWritten(1000)
            # Do not kill the host: it must complete logout. It also watches owner PID.
            if not self.process.waitForFinished(5000):
                self._closing = False
                self.errorOccurred.emit('桌面分身尚未完成注销，请在分身控制中心关闭后再退出。')
                return False
        if self.builder and self.builder.state() != QProcess.ProcessState.NotRunning:
            self.builder.terminate()
            self.builder.waitForFinished(1000)
        return True
