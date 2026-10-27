import logging
import os
import posixpath
import socket
import sys
import threading
import time
from pathlib import Path

import paramiko

logging.getLogger("paramiko").setLevel(logging.CRITICAL)
root = Path(sys.argv[1]).resolve()
remote = root / "remote"
remote.mkdir()
host_key = paramiko.RSAKey.generate(2048)
host_key.write_private_key_file(str(root / "client.key"))
host_key.write_private_key_file(str(root / "client-encrypted.key"), password="synthetic-passphrase")
stopped = threading.Event()
transports = []


class FixtureSftp(paramiko.SFTPServerInterface):
    def canonicalize(self, path):
        return posixpath.normpath("/" + path.lstrip("/"))

    def local(self, path):
        target = (remote / path.lstrip("/")).resolve()
        if target != remote and remote not in target.parents:
            raise PermissionError("Fixture paths must stay within synthetic data")
        return target

    def stat(self, path):
        try:
            return paramiko.SFTPAttributes.from_stat(self.local(path).stat())
        except OSError as error:
            return paramiko.SFTPServer.convert_errno(error.errno)

    lstat = stat

    def list_folder(self, path):
        entries = []
        for item in self.local(path).iterdir():
            attributes = paramiko.SFTPAttributes.from_stat(item.stat())
            attributes.filename = item.name
            entries.append(attributes)
        return entries

    def open(self, path, flags, attributes):
        try:
            descriptor = os.open(self.local(path), flags | getattr(os, "O_BINARY", 0), 0o600)
            mode = "r+b" if flags & os.O_RDWR else "wb" if flags & os.O_WRONLY else "rb"
            stream = os.fdopen(descriptor, mode)
            handle = paramiko.SFTPHandle(flags)
            handle.readfile = stream
            handle.writefile = stream
            return handle
        except OSError as error:
            return paramiko.SFTPServer.convert_errno(error.errno)

    def remove(self, path):
        self.local(path).unlink()
        return paramiko.SFTP_OK

    def rename(self, source, destination):
        target = self.local(destination)
        if target.exists():
            return paramiko.SFTP_FAILURE
        self.local(source).rename(target)
        return paramiko.SFTP_OK


class FixtureServer(paramiko.ServerInterface):
    def __init__(self):
        self.username = ""
        self.ready = threading.Event()

    def check_auth_password(self, username, password):
        self.username = username
        return paramiko.AUTH_SUCCESSFUL if password == "test-only" else paramiko.AUTH_FAILED

    def get_allowed_auths(self, username):
        return "password,publickey"

    def check_auth_publickey(self, username, key):
        self.username = username
        return paramiko.AUTH_SUCCESSFUL if key == host_key else paramiko.AUTH_FAILED

    def check_channel_request(self, kind, channel_id):
        return paramiko.OPEN_SUCCEEDED if kind == "session" else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED

    def check_channel_pty_request(self, *arguments):
        return True

    def check_channel_shell_request(self, channel):
        self.ready.set()
        return True

    def check_channel_window_change_request(self, channel, width, height, pixel_width, pixel_height):
        (root / "resized.txt").write_text(f"{width}x{height}")
        return True


def flood(channel):
    chunk = b"bounded-output-test-" * 1024
    remaining = 32 * 1024 * 1024
    try:
        while remaining:
            count = min(remaining, len(chunk))
            channel.sendall(chunk[:count])
            remaining -= count
    except (EOFError, OSError):
        pass
    finally:
        print(f"Flood attempted bytes: {32 * 1024 * 1024 - remaining}", flush=True)


def serve(connection):
    transport = paramiko.Transport(connection)
    transports.append(transport)
    transport.add_server_key(host_key)
    transport.set_subsystem_handler("sftp", paramiko.SFTPServer, FixtureSftp)
    server = FixtureServer()
    original_send = transport._send_user_message
    delayed = False

    def send(message):
        nonlocal delayed
        if server.username == "startup-flood" and message.asbytes()[0] == 99 and not delayed:
            delayed = True
            channel = transport._channels.get(0)
            threading.Thread(target=flood, args=(channel,), daemon=True).start()
            return
        original_send(message)

    transport._send_user_message = send
    try:
        transport.start_server(server=server)
        channel = transport.accept(15)
        if channel is None:
            return
        channel.settimeout(15)
        if server.username in ("sftp", "startup-flood"):
            stopped.wait(45)
            return
        if not server.ready.wait(15):
            return
        if server.username == "quiet":
            channel.sendall(b"renderer-ack-timeout")
            stopped.wait(45)
            return
        if server.username == "flood":
            time.sleep(0.2)
            flood(channel)
            stopped.wait(45)
            return
        pending = b""
        while transport.is_active():
            data = channel.recv(65536)
            if not data:
                break
            pending += data
            if b"finish\n" in pending:
                channel.sendall("最后一行 ✓\n".encode("utf-8"))
                channel.send_exit_status(0)
                channel.close()
                stopped.wait(45)
                return
            channel.sendall(data[:1])
            channel.sendall(data[1:])
    except (EOFError, OSError, paramiko.SSHException):
        pass
    finally:
        transport.close()


def watch_stdin():
    sys.stdin.readline()
    stopped.set()


listener = socket.socket()
listener.bind(("127.0.0.1", 0))
listener.listen(8)
listener.settimeout(0.25)
(root / "port.txt").write_text(str(listener.getsockname()[1]))
threading.Thread(target=watch_stdin, daemon=True).start()
try:
    while not stopped.is_set():
        try:
            connection, _ = listener.accept()
        except socket.timeout:
            continue
        threading.Thread(target=serve, args=(connection,), daemon=True).start()
finally:
    listener.close()
    for transport in transports:
        transport.close()
