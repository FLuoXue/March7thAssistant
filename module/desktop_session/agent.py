import os

from PySide6.QtCore import QObject, QTimer
from PySide6.QtNetwork import QLocalSocket

from .native_input import GameMouse, verify_pipe_server
from .protocol import FrameDecoder, MouseReceiver, encode_frame


class DesktopSessionAgent(QObject):
    """Lives in the child GUI; reconnects without retaining queued mouse movement."""
    def __init__(self, pipe_name, host_pid, process_name, logger, parent=None):
        super().__init__(parent)
        if not pipe_name.startswith('March7th.Desktop.') or host_pid <= 0:
            raise ValueError('桌面分身连接参数无效')
        self.pipe_name, self.host_pid = pipe_name, host_pid
        self.logger = logger
        self.mouse = GameMouse(process_name)
        self.socket = QLocalSocket(self)
        self.timer = QTimer(self)
        self.timer.setInterval(2000)
        self.timer.timeout.connect(self._connect)
        self.socket.connected.connect(self._connected)
        self.socket.readyRead.connect(self._read)
        self.socket.disconnected.connect(self._disconnected)
        self.socket.errorOccurred.connect(lambda _: self._disconnected())
        self.authenticated = False
        self.decoder = FrameDecoder()
        self.receiver = MouseReceiver(self.mouse.is_active, self.mouse.send)
        self.timer.start()
        self._connect()

    def _connect(self):
        if self.socket.state() != QLocalSocket.LocalSocketState.UnconnectedState:
            return
        self.socket.connectToServer(self.pipe_name)

    def _connected(self):
        try:
            if not verify_pipe_server(self.socket.socketDescriptor(), self.host_pid):
                raise ValueError('桌面分身服务端身份不匹配')
            self.decoder = FrameDecoder()
            self.receiver = MouseReceiver(self.mouse.is_active, self.mouse.send)
            self.socket.write(encode_frame({'op': 'hello', 'version': 1, 'pid': os.getpid()}))
        except Exception as error:
            self.logger.error(f'桌面分身鼠标连接失败：{error}')
            self.socket.abort()

    def _disconnected(self):
        self.authenticated = False
        self.decoder = FrameDecoder()

    def _read(self):
        try:
            for message in self.decoder.feed(bytes(self.socket.readAll())):
                if not self.authenticated:
                    if message != {'op': 'hello', 'version': 1}:
                        raise ValueError('桌面分身握手失败')
                    self.authenticated = True
                    self.logger.info('桌面分身相对鼠标已连接')
                else:
                    self.socket.write(encode_frame(self.receiver.handle(message)))
        except Exception as error:
            self.logger.error(f'桌面分身鼠标消息处理失败：{error}')
            self.socket.abort()

    def close(self):
        self.timer.stop()
        self.socket.abort()
