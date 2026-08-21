#!/usr/bin/env python3
"""Opaque storage and relay server for Sensor Readout remote monitoring."""

from __future__ import annotations

import argparse
import base64
from collections import deque
import hashlib
import hmac
import http.server
import ipaddress
import json
import logging
from logging.handlers import RotatingFileHandler
import os
from pathlib import Path
import re
import secrets
import shutil
import socket
import socketserver
import tempfile
import threading
import time
from typing import Any, Dict, List, Optional, Tuple
from urllib.parse import parse_qs, urlparse


PROTOCOL_VERSION = 1
SERVER_VERSION = "6.0.0"
DEFAULT_MAX_ENVELOPE_BYTES = 8 * 1024 * 1024
DEFAULT_MAX_DELTAS = 64
DEFAULT_MAX_DELTA_BYTES = 8 * 1024 * 1024
DEFAULT_MAX_BUFFERED_DELTA_BYTES = 32 * 1024 * 1024
DEFAULT_ACTIVITY_PERSIST_INTERVAL_SECONDS = 3600
DEFAULT_MAX_SPACES = 64
DEFAULT_MAX_MACHINES_PER_SPACE = 128
DEFAULT_MAX_MACHINES_TOTAL = 512
DEFAULT_MAX_STORAGE_BYTES = 2 * 1024 * 1024 * 1024
DEFAULT_MAX_STORAGE_BYTES_PER_MACHINE = 64 * 1024 * 1024
DEFAULT_RETENTION_DAYS = 90
DEFAULT_MAINTENANCE_INTERVAL_SECONDS = 300
DEFAULT_MAX_CONCURRENT_REQUESTS = 4
DEFAULT_REQUEST_BACKLOG = 64
DEFAULT_REQUEST_TIMEOUT_SECONDS = 30
DEFAULT_MAX_REQUESTS_PER_MINUTE_PER_CLIENT = 0
DEFAULT_MAX_COMMAND_BYTES = 64 * 1024
DEFAULT_MAX_COMMANDS_PER_MACHINE = 32
DEFAULT_MAX_COMMAND_BYTES_PER_MACHINE = 1024 * 1024
METADATA_STORAGE_RESERVE = 16 * 1024
MANAGED_MEMORY_LIMIT_BYTES = 256 * 1024 * 1024
SERVER_MEMORY_RESERVE_BYTES = 64 * 1024 * 1024
RESPONSE_SERIALIZATION_COPIES = 3
RESPONSE_SERIALIZATION_OVERHEAD_BYTES = 1024 * 1024
ID_PATTERN = re.compile(r"^[A-Za-z0-9_-]{32,128}$")
MACHINE_ROUTE = re.compile(
    r"^/api/v1/spaces/(?P<space>[A-Za-z0-9_-]{32,128})/machines/"
    r"(?P<machine>[A-Za-z0-9_-]{32,128})(?P<suffix>/snapshot|/deltas|/heartbeat|/commands(?:/[A-Za-z0-9_-]{32,128})?)?$"
)
SPACE_ROUTE = re.compile(
    r"^/api/v1/spaces/(?P<space>[A-Za-z0-9_-]{32,128})/machines$"
)


def now_ms() -> int:
    return int(time.time() * 1000)


def random_token() -> str:
    return secrets.token_urlsafe(32)


def validate_auth_token(value: Any) -> str:
    token = str(value or "").strip()
    if len(token) < 32 or token == "replace-with-a-random-server-token":
        raise ValueError("AuthToken must be replaced with a random value of at least 32 characters.")
    return token


def normalize_public_url(value: str) -> str:
    parsed = urlparse(value)
    try:
        public_port = parsed.port
    except ValueError:
        raise ValueError("PublicUrl contains an invalid port.")
    path = parsed.path or "/"
    segments = [segment for segment in path.split("/") if segment]
    host = parsed.hostname or ""
    try:
        loopback = ipaddress.ip_address(host).is_loopback
    except ValueError:
        loopback = host.lower() == "localhost"
    if (
        parsed.scheme not in ("http", "https")
        or not host
        or host in ("0.0.0.0", "::")
        or loopback
        or parsed.username
        or parsed.password
        or parsed.params
        or parsed.query
        or parsed.fragment
        or public_port == 0
        or "\\" in path
        or "%" in path
        or "//" in path
        or any(segment in (".", "..") or not re.fullmatch(r"[A-Za-z0-9._~-]+", segment) for segment in segments)
    ):
        raise ValueError("PublicUrl must be an HTTP or HTTPS address another computer can reach, with an optional simple path prefix and no credentials, query text, or fragment.")
    return value.rstrip("/") + "/"


def bounded_int(
    settings: Dict[str, Any],
    name: str,
    default: int,
    minimum: int,
    maximum: int,
    aliases: Tuple[str, ...] = (),
) -> int:
    raw = settings.get(name)
    if raw is None:
        for alias in aliases:
            if settings.get(alias) is not None:
                raw = settings[alias]
                break
    if raw is None:
        raw = default
    try:
        value = int(raw)
    except (TypeError, ValueError):
        raise ValueError("%s must be a whole number." % name)
    if value < minimum or value > maximum:
        raise ValueError("%s must be between %s and %s." % (name, minimum, maximum))
    return value


def atomic_write(path: Path, data: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temp_name = tempfile.mkstemp(prefix=path.name + ".", suffix=".tmp", dir=str(path.parent))
    try:
        with os.fdopen(fd, "wb") as output:
            output.write(data)
            output.flush()
            os.fsync(output.fileno())
        os.replace(temp_name, path)
    except Exception:
        try:
            os.unlink(temp_name)
        except OSError:
            pass
        raise


def atomic_json(path: Path, value: Dict[str, Any]) -> None:
    atomic_write(path, json.dumps(value, indent=2, sort_keys=True).encode("utf-8"))


def default_data_dir() -> Path:
    if os.name == "nt":
        root = os.environ.get("LOCALAPPDATA") or str(Path.home())
        return Path(root) / "Sensor Readout Server"
    return Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local" / "share")) / "sensor-readout-server"


def default_config_path() -> Path:
    if os.name == "nt":
        return default_data_dir() / "sensor-readout-server-settings.json"
    root = Path(os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config"))
    return root / "sensor-readout-server" / "sensor-readout-server-settings.json"


def load_settings(path: Path) -> Tuple[Dict[str, Any], bool]:
    defaults: Dict[str, Any] = {
        "Host": "127.0.0.1",
        "Port": 48673,
        "PublicUrl": "",
        "DataPath": str(default_data_dir() / "Data"),
        "AuthToken": random_token(),
        "LogPath": str(default_data_dir() / "Logs" / "sensor-readout-server.log"),
        "MaxEnvelopeBytes": DEFAULT_MAX_ENVELOPE_BYTES,
        "MaxDeltasPerMachine": DEFAULT_MAX_DELTAS,
        "MaxDeltaBytesPerMachine": DEFAULT_MAX_DELTA_BYTES,
        "MaxBufferedDeltaBytes": DEFAULT_MAX_BUFFERED_DELTA_BYTES,
        "ActivityPersistIntervalSeconds": DEFAULT_ACTIVITY_PERSIST_INTERVAL_SECONDS,
        "MaxSpaces": DEFAULT_MAX_SPACES,
        "MaxMachinesPerSpace": DEFAULT_MAX_MACHINES_PER_SPACE,
        "MaxMachinesTotal": DEFAULT_MAX_MACHINES_TOTAL,
        "MaxStorageBytes": DEFAULT_MAX_STORAGE_BYTES,
        "MaxStorageBytesPerMachine": DEFAULT_MAX_STORAGE_BYTES_PER_MACHINE,
        "RetentionDays": DEFAULT_RETENTION_DAYS,
        "MaintenanceIntervalSeconds": DEFAULT_MAINTENANCE_INTERVAL_SECONDS,
        "MaxConcurrentRequests": DEFAULT_MAX_CONCURRENT_REQUESTS,
        "RequestBacklog": DEFAULT_REQUEST_BACKLOG,
        "RequestTimeoutSeconds": DEFAULT_REQUEST_TIMEOUT_SECONDS,
        "MaxRequestsPerMinutePerClient": DEFAULT_MAX_REQUESTS_PER_MINUTE_PER_CLIENT,
        "MaxCommandBytes": DEFAULT_MAX_COMMAND_BYTES,
        "MaxCommandsPerMachine": DEFAULT_MAX_COMMANDS_PER_MACHINE,
        "MaxCommandBytesPerMachine": DEFAULT_MAX_COMMAND_BYTES_PER_MACHINE,
    }
    if path.exists():
        loaded = json.loads(path.read_text(encoding="utf-8"))
        if not isinstance(loaded, dict):
            raise ValueError("Server settings must be a JSON object.")
        defaults.update(loaded)
        return defaults, False

    path.parent.mkdir(parents=True, exist_ok=True)
    atomic_json(path, defaults)
    return defaults, True


def configure_logging(path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    handler = RotatingFileHandler(path, maxBytes=1024 * 1024, backupCount=5, encoding="utf-8")
    handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(message)s"))
    logging.basicConfig(level=logging.INFO, handlers=[handler, logging.StreamHandler()])


class StateStore:
    def __init__(self, settings: Dict[str, Any]) -> None:
        self.root = Path(str(settings["DataPath"])).expanduser().resolve()
        self.root.mkdir(parents=True, exist_ok=True)
        self.max_envelope_bytes = bounded_int(settings, "MaxEnvelopeBytes", DEFAULT_MAX_ENVELOPE_BYTES, 1024, 32 * 1024 * 1024)
        self.max_deltas = bounded_int(settings, "MaxDeltasPerMachine", DEFAULT_MAX_DELTAS, 1, 4096)
        self.max_delta_bytes = bounded_int(settings, "MaxDeltaBytesPerMachine", DEFAULT_MAX_DELTA_BYTES, 1024, 512 * 1024 * 1024)
        self.max_buffered_delta_bytes = bounded_int(
            settings, "MaxBufferedDeltaBytes", DEFAULT_MAX_BUFFERED_DELTA_BYTES, 1024 * 1024, 4 * 1024 * 1024 * 1024
        )
        self.activity_persist_interval_seconds = bounded_int(
            settings, "ActivityPersistIntervalSeconds", DEFAULT_ACTIVITY_PERSIST_INTERVAL_SECONDS, 60, 86400
        )
        self.max_spaces = bounded_int(settings, "MaxSpaces", DEFAULT_MAX_SPACES, 1, 4096)
        self.max_machines_per_space = bounded_int(
            settings, "MaxMachinesPerSpace", DEFAULT_MAX_MACHINES_PER_SPACE, 1, 4096, ("MaxMachines",)
        )
        self.max_machines_total = bounded_int(settings, "MaxMachinesTotal", DEFAULT_MAX_MACHINES_TOTAL, 1, 65536)
        self.max_storage_bytes = bounded_int(settings, "MaxStorageBytes", DEFAULT_MAX_STORAGE_BYTES, 1024 * 1024, 1024 * 1024 * 1024 * 1024)
        self.max_machine_storage_bytes = bounded_int(
            settings, "MaxStorageBytesPerMachine", DEFAULT_MAX_STORAGE_BYTES_PER_MACHINE, 64 * 1024, 4 * 1024 * 1024 * 1024
        )
        self.retention_days = bounded_int(settings, "RetentionDays", DEFAULT_RETENTION_DAYS, 0, 3650)
        self.maintenance_interval_seconds = bounded_int(
            settings, "MaintenanceIntervalSeconds", DEFAULT_MAINTENANCE_INTERVAL_SECONDS, 1, 86400
        )
        self.max_command_bytes = bounded_int(settings, "MaxCommandBytes", DEFAULT_MAX_COMMAND_BYTES, 1024, 1024 * 1024)
        self.max_commands_per_machine = bounded_int(settings, "MaxCommandsPerMachine", DEFAULT_MAX_COMMANDS_PER_MACHINE, 1, 1024)
        self.max_command_bytes_per_machine = bounded_int(
            settings, "MaxCommandBytesPerMachine", DEFAULT_MAX_COMMAND_BYTES_PER_MACHINE, 1024, 64 * 1024 * 1024
        )
        if self.max_machine_storage_bytes < self.max_envelope_bytes:
            raise ValueError("MaxStorageBytesPerMachine cannot be smaller than MaxEnvelopeBytes.")
        if self.max_storage_bytes < self.max_machine_storage_bytes:
            raise ValueError("MaxStorageBytes cannot be smaller than MaxStorageBytesPerMachine.")
        if self.max_machines_total < self.max_machines_per_space:
            raise ValueError("MaxMachinesTotal cannot be smaller than MaxMachinesPerSpace.")
        self._locks = [threading.RLock() for _ in range(64)]
        self._global_lock = threading.RLock()
        self._last_maintenance_monotonic = 0.0
        self._pending_deltas: Dict[Tuple[str, str], List[Tuple[int, bytes]]] = {}
        self._volatile_metadata: Dict[Tuple[str, str], Dict[str, Any]] = {}
        self._buffered_delta_bytes = 0
        self._storage_bytes = self._directory_size(self.root)
        self.maintain(force=True)

    @staticmethod
    def _directory_size(root: Path) -> int:
        total = 0
        if not root.exists():
            return total
        for current, directories, files in os.walk(str(root), followlinks=False):
            directories[:] = [name for name in directories if not Path(current, name).is_symlink()]
            for name in files:
                path = Path(current, name)
                try:
                    if not path.is_symlink():
                        total += path.stat().st_size
                except OSError:
                    continue
        return total

    def _space_directories_locked(self) -> List[Path]:
        root = self.root / "Spaces"
        if not root.exists():
            return []
        return [item for item in root.iterdir() if item.is_dir() and not item.is_symlink() and ID_PATTERN.fullmatch(item.name)]

    def _machine_directories_locked(self, space: Optional[Path] = None) -> List[Path]:
        spaces = [space] if space is not None else self._space_directories_locked()
        output: List[Path] = []
        for item in spaces:
            root = item / "Machines"
            if not root.exists():
                continue
            output.extend(
                machine
                for machine in root.iterdir()
                if machine.is_dir() and not machine.is_symlink() and ID_PATTERN.fullmatch(machine.name) and (machine / "snapshot.bin").is_file()
            )
        return output

    def _assert_new_machine_capacity_locked(self, space_id: str) -> None:
        spaces = self._space_directories_locked()
        space = self.root / "Spaces" / space_id
        machines_in_space = self._machine_directories_locked(space) if space in spaces else []
        if not machines_in_space and len([item for item in spaces if self._machine_directories_locked(item)]) >= self.max_spaces:
            raise StorageLimitExceeded("The server monitoring-space limit has been reached.")
        if len(machines_in_space) >= self.max_machines_per_space:
            raise StorageLimitExceeded("The monitoring-space computer limit has been reached.")
        if len(self._machine_directories_locked()) >= self.max_machines_total:
            raise StorageLimitExceeded("The server computer limit has been reached.")

    def _assert_storage_capacity_locked(self, machine_dir: Path, projected_machine_bytes: int, projected_total_bytes: int) -> None:
        if projected_machine_bytes > self.max_machine_storage_bytes:
            raise StorageLimitExceeded("The computer storage limit has been reached.")
        if projected_total_bytes > self.max_storage_bytes:
            raise StorageLimitExceeded("The server storage limit has been reached.")

    def _finish_storage_change_locked(self, machine_dir: Path, before: int) -> None:
        after = self._directory_size(machine_dir)
        self._storage_bytes = max(0, self._storage_bytes - before + after)

    def maintain(self, force: bool = False, now_unix_ms: Optional[int] = None) -> int:
        with self._global_lock:
            monotonic_now = time.monotonic()
            if not force and monotonic_now - self._last_maintenance_monotonic < self.maintenance_interval_seconds:
                return 0
            self._last_maintenance_monotonic = monotonic_now
            removed = 0
            cutoff = 0
            if self.retention_days > 0:
                current_ms = now_ms() if now_unix_ms is None else now_unix_ms
                cutoff = current_ms - self.retention_days * 24 * 60 * 60 * 1000
            for space in self._space_directories_locked():
                machines_root = space / "Machines"
                if machines_root.exists():
                    for machine in list(machines_root.iterdir()):
                        if not machine.is_dir() or machine.is_symlink() or not ID_PATTERN.fullmatch(machine.name):
                            continue
                        metadata = self._effective_metadata_locked(space.name, machine.name, machine)
                        last_seen = int(metadata.get("LastSeenUnixMs", 0) or 0)
                        snapshot_exists = (machine / "snapshot.bin").is_file()
                        remove = cutoff > 0 and last_seen > 0 and last_seen < cutoff
                        if not snapshot_exists:
                            try:
                                remove = remove or machine.stat().st_mtime < time.time() - 24 * 60 * 60
                            except OSError:
                                pass
                        if remove:
                            with self.machine_lock(space.name, machine.name):
                                self._clear_pending_locked(space.name, machine.name)
                                shutil.rmtree(machine, ignore_errors=True)
                            removed += 1
                try:
                    if machines_root.exists() and not any(machines_root.iterdir()):
                        machines_root.rmdir()
                    if space.exists() and not any(space.iterdir()):
                        space.rmdir()
                except OSError:
                    pass
            for current, _, files in os.walk(str(self.root), followlinks=False):
                for name in files:
                    if ".tmp" not in name:
                        continue
                    path = Path(current, name)
                    try:
                        if path.stat().st_mtime < time.time() - 24 * 60 * 60:
                            path.unlink()
                    except OSError:
                        pass
            self._storage_bytes = self._directory_size(self.root)
            if removed:
                logging.info("Pruned %s stale remote computer(s).", removed)
            return removed

    def machine_lock(self, space_id: str, machine_id: str) -> threading.RLock:
        digest = hashlib.sha256((space_id + "/" + machine_id).encode("utf-8")).digest()
        return self._locks[int.from_bytes(digest[:4], "big") % len(self._locks)]

    def machine_dir(self, space_id: str, machine_id: str) -> Path:
        if not ID_PATTERN.fullmatch(space_id) or not ID_PATTERN.fullmatch(machine_id):
            raise ValueError("Invalid remote identifier.")
        return self.root / "Spaces" / space_id / "Machines" / machine_id

    @staticmethod
    def metadata_path(machine_dir: Path) -> Path:
        return machine_dir / "metadata.json"

    def load_metadata(self, machine_dir: Path) -> Dict[str, Any]:
        path = self.metadata_path(machine_dir)
        if not path.exists():
            return {
                "ProtocolVersion": PROTOCOL_VERSION,
                "SnapshotSequence": 0,
                "LatestSequence": 0,
                "LastSeenUnixMs": 0,
                "DeltaCount": 0,
                "DeltaBytes": 0,
            }
        try:
            value = json.loads(path.read_text(encoding="utf-8"))
            return value if isinstance(value, dict) else {}
        except Exception:
            logging.exception("Could not read remote machine metadata: %s", path)
            return {}

    def save_metadata(self, machine_dir: Path, metadata: Dict[str, Any]) -> None:
        metadata["ProtocolVersion"] = PROTOCOL_VERSION
        atomic_json(self.metadata_path(machine_dir), metadata)

    @staticmethod
    def _machine_key(space_id: str, machine_id: str) -> Tuple[str, str]:
        return space_id, machine_id

    def _effective_metadata_locked(self, space_id: str, machine_id: str, machine_dir: Optional[Path] = None) -> Dict[str, Any]:
        key = self._machine_key(space_id, machine_id)
        volatile = self._volatile_metadata.get(key)
        if volatile is not None:
            return dict(volatile)
        return dict(self.load_metadata(machine_dir or self.machine_dir(space_id, machine_id)))

    def _clear_pending_locked(self, space_id: str, machine_id: str) -> None:
        key = self._machine_key(space_id, machine_id)
        pending = self._pending_deltas.pop(key, [])
        self._buffered_delta_bytes = max(0, self._buffered_delta_bytes - sum(len(payload) for _, payload in pending))
        self._volatile_metadata.pop(key, None)

    def _persist_activity_if_due_locked(self, machine_dir: Path, effective: Dict[str, Any]) -> None:
        stable = self.load_metadata(machine_dir)
        last_persisted = int(stable.get("LastSeenUnixMs", 0) or 0)
        current = int(effective.get("LastSeenUnixMs", 0) or 0)
        if current - last_persisted < self.activity_persist_interval_seconds * 1000:
            return
        before = self._directory_size(machine_dir)
        stable["LastSeenUnixMs"] = current
        self.save_metadata(machine_dir, stable)
        self._finish_storage_change_locked(machine_dir, before)

    def list_machines(self, space_id: str) -> List[Dict[str, Any]]:
        if not ID_PATTERN.fullmatch(space_id):
            raise ValueError("Invalid remote space identifier.")
        with self._global_lock:
            self.maintain()
            root = self.root / "Spaces" / space_id / "Machines"
            if not root.exists():
                return []
            machines: List[Dict[str, Any]] = []
            for directory in sorted(root.iterdir(), key=lambda item: item.name):
                if not directory.is_dir() or directory.is_symlink() or not ID_PATTERN.fullmatch(directory.name):
                    continue
                metadata = self._effective_metadata_locked(space_id, directory.name, directory)
                if not (directory / "snapshot.bin").is_file():
                    continue
                machines.append({
                    "MachineId": directory.name,
                    "SnapshotSequence": int(metadata.get("SnapshotSequence", 0)),
                    "LatestSequence": int(metadata.get("LatestSequence", 0)),
                    "LastSeenUnixMs": int(metadata.get("LastSeenUnixMs", 0)),
                })
            return machines

    def put_snapshot(self, space_id: str, machine_id: str, sequence: int, payload: bytes, machine_token: str) -> Dict[str, Any]:
        self._validate_payload(payload)
        if sequence < 1:
            raise ValueError("Snapshot sequence must be positive.")
        directory = self.machine_dir(space_id, machine_id)
        with self._global_lock:
            self.maintain()
            with self.machine_lock(space_id, machine_id):
                snapshot = directory / "snapshot.bin"
                is_new = not snapshot.is_file()
                if is_new:
                    self._assert_new_machine_capacity_locked(space_id)
                metadata_path = self.metadata_path(directory)
                retained_files = directory.exists() and any(item.is_file() for item in directory.rglob("*"))
                allow_registration = is_new and not metadata_path.exists() and not retained_files
                stable_metadata = self.load_metadata(directory)
                self._verify_machine_token(stable_metadata, machine_token, allow_registration=allow_registration)
                metadata = self._effective_metadata_locked(space_id, machine_id, directory)
                if "MachineWriteTokenHash" not in metadata and "MachineWriteTokenHash" in stable_metadata:
                    metadata["MachineWriteTokenHash"] = stable_metadata["MachineWriteTokenHash"]
                latest = int(metadata.get("LatestSequence", 0))
                if latest > sequence:
                    raise SequenceConflict(latest)
                before = self._directory_size(directory)
                old_snapshot = snapshot.stat().st_size if snapshot.is_file() else 0
                removable_delta_bytes = 0
                deltas = directory / "Deltas"
                if deltas.exists():
                    for item in deltas.glob("*.bin"):
                        try:
                            if int(item.stem) <= sequence:
                                removable_delta_bytes += item.stat().st_size
                        except (OSError, ValueError):
                            continue
                projected = max(0, before - old_snapshot - removable_delta_bytes) + len(payload) + METADATA_STORAGE_RESERVE
                self._assert_storage_capacity_locked(directory, projected, self._storage_bytes - before + projected)
                try:
                    if allow_registration:
                        # Establish ownership before data exists. A crash during
                        # the first snapshot can then be retried only by the same publisher.
                        self.save_metadata(directory, stable_metadata)
                    atomic_write(snapshot, payload)
                    if deltas.exists():
                        for item in deltas.glob("*.bin"):
                            try:
                                if int(item.stem) <= sequence:
                                    item.unlink()
                            except (OSError, ValueError):
                                continue
                    self._clear_pending_locked(space_id, machine_id)
                    metadata["SnapshotSequence"] = sequence
                    metadata["LatestSequence"] = sequence
                    metadata["LastSeenUnixMs"] = now_ms()
                    metadata["DeltaCount"] = len(list(deltas.glob("*.bin"))) if deltas.exists() else 0
                    metadata["DeltaBytes"] = sum(item.stat().st_size for item in deltas.glob("*.bin")) if deltas.exists() else 0
                    self.save_metadata(directory, metadata)
                    return metadata
                finally:
                    self._finish_storage_change_locked(directory, before)

    def get_snapshot(self, space_id: str, machine_id: str) -> Tuple[bytes, Dict[str, Any]]:
        directory = self.machine_dir(space_id, machine_id)
        with self._global_lock:
            self.maintain()
            lock = self.machine_lock(space_id, machine_id)
            lock.acquire()
        try:
            path = directory / "snapshot.bin"
            if not path.is_file():
                raise FileNotFoundError("Remote snapshot was not found.")
            data = path.read_bytes()
            self._validate_payload(data)
            return data, self._effective_metadata_locked(space_id, machine_id, directory)
        finally:
            lock.release()

    def append_delta(self, space_id: str, machine_id: str, sequence: int, payload: bytes, machine_token: str) -> Dict[str, Any]:
        self._validate_payload(payload)
        directory = self.machine_dir(space_id, machine_id)
        with self._global_lock:
            self.maintain()
            with self.machine_lock(space_id, machine_id):
                stable_metadata = self.load_metadata(directory)
                self._verify_machine_token(stable_metadata, machine_token)
                metadata = self._effective_metadata_locked(space_id, machine_id, directory)
                latest = int(metadata.get("LatestSequence", 0))
                if not (directory / "snapshot.bin").is_file():
                    raise SnapshotRequired()
                if sequence != latest + 1:
                    raise SequenceConflict(latest)
                delta_count = int(metadata.get("DeltaCount", 0))
                delta_bytes = int(metadata.get("DeltaBytes", 0))
                if delta_count >= self.max_deltas or delta_bytes + len(payload) > self.max_delta_bytes:
                    raise SnapshotRequired()
                if self._buffered_delta_bytes + len(payload) > self.max_buffered_delta_bytes:
                    raise SnapshotRequired()
                key = self._machine_key(space_id, machine_id)
                self._pending_deltas.setdefault(key, []).append((sequence, payload))
                self._buffered_delta_bytes += len(payload)
                metadata["LatestSequence"] = sequence
                metadata["LastSeenUnixMs"] = now_ms()
                metadata["DeltaCount"] = delta_count + 1
                metadata["DeltaBytes"] = delta_bytes + len(payload)
                self._volatile_metadata[key] = dict(metadata)
                self._persist_activity_if_due_locked(directory, metadata)
                return metadata

    def get_deltas(self, space_id: str, machine_id: str, after: int) -> Tuple[List[Dict[str, Any]], Dict[str, Any]]:
        directory = self.machine_dir(space_id, machine_id)
        with self._global_lock:
            self.maintain()
            lock = self.machine_lock(space_id, machine_id)
            lock.acquire()
        try:
            metadata = self._effective_metadata_locked(space_id, machine_id, directory)
            snapshot_sequence = int(metadata.get("SnapshotSequence", 0))
            if after < snapshot_sequence:
                raise SnapshotRequired()
            output: List[Dict[str, Any]] = []
            output_bytes = 0
            deltas = directory / "Deltas"
            if deltas.exists():
                for path in sorted(deltas.glob("*.bin"), key=lambda item: item.name):
                    try:
                        sequence = int(path.stem)
                    except ValueError:
                        continue
                    if sequence <= after:
                        continue
                    data = path.read_bytes()
                    self._validate_payload(data)
                    output_bytes += len(data)
                    if output_bytes > self.max_delta_bytes:
                        raise SnapshotRequired()
                    output.append({"Sequence": sequence, "Payload": base64.b64encode(data).decode("ascii")})
            for sequence, data in self._pending_deltas.get(self._machine_key(space_id, machine_id), []):
                if sequence > after:
                    output_bytes += len(data)
                    if output_bytes > self.max_delta_bytes:
                        raise SnapshotRequired()
                    output.append({"Sequence": sequence, "Payload": base64.b64encode(data).decode("ascii")})
            output.sort(key=lambda item: int(item["Sequence"]))
            return output, metadata
        finally:
            lock.release()

    def heartbeat(self, space_id: str, machine_id: str, machine_token: str) -> Dict[str, Any]:
        directory = self.machine_dir(space_id, machine_id)
        with self._global_lock:
            self.maintain()
            with self.machine_lock(space_id, machine_id):
                if not (directory / "snapshot.bin").is_file():
                    raise SnapshotRequired()
                stable_metadata = self.load_metadata(directory)
                self._verify_machine_token(stable_metadata, machine_token)
                metadata = self._effective_metadata_locked(space_id, machine_id, directory)
                metadata["LastSeenUnixMs"] = now_ms()
                self._volatile_metadata[self._machine_key(space_id, machine_id)] = dict(metadata)
                self._persist_activity_if_due_locked(directory, metadata)
                return metadata

    def put_command(self, space_id: str, machine_id: str, command_id: str, payload: bytes) -> None:
        if not ID_PATTERN.fullmatch(command_id):
            raise ValueError("Invalid remote command identifier.")
        if not payload or len(payload) > self.max_command_bytes:
            raise ValueError("Encrypted command is empty or too large.")
        directory = self.machine_dir(space_id, machine_id)
        with self._global_lock:
            self.maintain()
            with self.machine_lock(space_id, machine_id):
                if not (directory / "snapshot.bin").is_file():
                    raise SnapshotRequired()
                commands = directory / "Commands"
                existing = list(commands.glob("*.bin")) if commands.exists() else []
                target = commands / (command_id + ".bin")
                if not target.exists() and len(existing) >= self.max_commands_per_machine:
                    raise StorageLimitExceeded("The remote command queue is full.")
                command_bytes = sum(item.stat().st_size for item in existing)
                old_size = target.stat().st_size if target.is_file() else 0
                if command_bytes - old_size + len(payload) > self.max_command_bytes_per_machine:
                    raise StorageLimitExceeded("The remote command storage limit has been reached.")
                before = self._directory_size(directory)
                projected = before - old_size + len(payload) + METADATA_STORAGE_RESERVE
                self._assert_storage_capacity_locked(directory, projected, self._storage_bytes - before + projected)
                atomic_write(target, payload)
                self._finish_storage_change_locked(directory, before)

    def get_commands(self, space_id: str, machine_id: str, machine_token: str) -> List[Dict[str, Any]]:
        directory = self.machine_dir(space_id, machine_id)
        with self._global_lock:
            self.maintain()
            lock = self.machine_lock(space_id, machine_id)
            lock.acquire()
        try:
            if not (directory / "snapshot.bin").exists():
                raise SnapshotRequired()
            self._verify_machine_token(self.load_metadata(directory), machine_token)
            commands = directory / "Commands"
            if not commands.exists():
                return []
            output: List[Dict[str, Any]] = []
            output_bytes = 0
            for path in sorted(commands.glob("*.bin"), key=lambda item: item.stat().st_mtime_ns):
                if not ID_PATTERN.fullmatch(path.stem):
                    continue
                payload = path.read_bytes()
                if not payload or len(payload) > self.max_command_bytes:
                    continue
                if output_bytes + len(payload) > self.max_command_bytes_per_machine:
                    break
                output_bytes += len(payload)
                output.append({"CommandId": path.stem, "Payload": base64.b64encode(payload).decode("ascii")})
            return output[:self.max_commands_per_machine]
        finally:
            lock.release()

    def delete_command(self, space_id: str, machine_id: str, command_id: str, machine_token: str) -> None:
        if not ID_PATTERN.fullmatch(command_id):
            raise ValueError("Invalid remote command identifier.")
        directory = self.machine_dir(space_id, machine_id)
        with self._global_lock:
            with self.machine_lock(space_id, machine_id):
                self._verify_machine_token(self.load_metadata(directory), machine_token)
                before = self._directory_size(directory)
                try:
                    (directory / "Commands" / (command_id + ".bin")).unlink()
                except FileNotFoundError:
                    pass
                self._finish_storage_change_locked(directory, before)

    def delete_machine(self, space_id: str, machine_id: str, machine_token: str) -> None:
        directory = self.machine_dir(space_id, machine_id)
        with self._global_lock:
            with self.machine_lock(space_id, machine_id):
                if not directory.exists():
                    raise FileNotFoundError("Remote computer was not found.")
                self._verify_machine_token(self.load_metadata(directory), machine_token)
                before = self._directory_size(directory)
                self._clear_pending_locked(space_id, machine_id)
                shutil.rmtree(directory)
                self._storage_bytes = max(0, self._storage_bytes - before)
                space = directory.parent.parent
                try:
                    if directory.parent.exists() and not any(directory.parent.iterdir()):
                        directory.parent.rmdir()
                    if space.exists() and not any(space.iterdir()):
                        space.rmdir()
                except OSError:
                    pass

    def _validate_payload(self, payload: bytes) -> None:
        if not payload:
            raise ValueError("Encrypted payload is empty.")
        if len(payload) > self.max_envelope_bytes:
            raise PayloadTooLarge(self.max_envelope_bytes)

    @staticmethod
    def _verify_machine_token(metadata: Dict[str, Any], supplied: str, allow_registration: bool = False) -> None:
        token = str(supplied or "").strip()
        if len(token) < 32 or len(token) > 4096:
            raise MachineTokenRejected()
        digest = base64.b64encode(hashlib.sha256(token.encode("utf-8")).digest()).decode("ascii")
        stored = str(metadata.get("MachineWriteTokenHash", ""))
        if not stored and allow_registration:
            metadata["MachineWriteTokenHash"] = digest
            return
        if not stored or not hmac.compare_digest(stored.encode("ascii"), digest.encode("ascii")):
            raise MachineTokenRejected()

class SequenceConflict(Exception):
    def __init__(self, latest: int) -> None:
        super().__init__("Remote sequence changed.")
        self.latest = latest


class SnapshotRequired(Exception):
    pass


class PayloadTooLarge(Exception):
    def __init__(self, maximum: int) -> None:
        super().__init__("Encrypted payload exceeds the server limit.")
        self.maximum = maximum


class StorageLimitExceeded(Exception):
    pass


class MachineTokenRejected(Exception):
    pass


class ThreadingServer(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True
    allow_reuse_address = True

    def __init__(self, address: Tuple[str, int], handler: type, settings: Dict[str, Any]) -> None:
        self.settings = settings
        self.store = StateStore(settings)
        requested_concurrency = bounded_int(
            settings, "MaxConcurrentRequests", DEFAULT_MAX_CONCURRENT_REQUESTS, 1, 1024
        )
        self.max_concurrent_requests = self._memory_safe_concurrency(requested_concurrency)
        self.request_queue_size = bounded_int(settings, "RequestBacklog", DEFAULT_REQUEST_BACKLOG, 1, 4096)
        self.request_timeout_seconds = bounded_int(
            settings, "RequestTimeoutSeconds", DEFAULT_REQUEST_TIMEOUT_SECONDS, 1, 600
        )
        self.max_requests_per_minute_per_client = bounded_int(
            settings, "MaxRequestsPerMinutePerClient", DEFAULT_MAX_REQUESTS_PER_MINUTE_PER_CLIENT, 0, 100000
        )
        self.request_slots = threading.BoundedSemaphore(self.max_concurrent_requests)
        self.rate_limit_lock = threading.Lock()
        self.rate_limit_requests: Dict[str, deque] = {}
        if ":" in address[0]:
            self.address_family = socket.AF_INET6
        super().__init__(address, handler)

    def _memory_safe_concurrency(self, requested: int) -> int:
        response_source_bytes = max(self.store.max_delta_bytes, self.store.max_command_bytes_per_machine)
        encoded_response_bytes = ((response_source_bytes + 2) // 3) * 4
        response_peak_bytes = (
            encoded_response_bytes * RESPONSE_SERIALIZATION_COPIES + RESPONSE_SERIALIZATION_OVERHEAD_BYTES
        )
        per_request_bytes = max(self.store.max_envelope_bytes, response_peak_bytes)
        available_bytes = (
            MANAGED_MEMORY_LIMIT_BYTES
            - SERVER_MEMORY_RESERVE_BYTES
            - self.store.max_buffered_delta_bytes
        )
        if available_bytes < per_request_bytes:
            raise ValueError(
                "Configured payload, delta, command, and buffer limits exceed the managed 256 MiB memory budget."
            )
        safe = max(1, available_bytes // per_request_bytes)
        if requested > safe:
            logging.warning(
                "MaxConcurrentRequests was reduced from %s to %s to stay within the managed memory budget.",
                requested,
                safe,
            )
        return min(requested, safe)

    def allow_client_request(self, client_key: str) -> bool:
        if self.max_requests_per_minute_per_client <= 0:
            return True
        current = time.monotonic()
        cutoff = current - 60.0
        with self.rate_limit_lock:
            stale_clients = []
            for key, requests in self.rate_limit_requests.items():
                while requests and requests[0] <= cutoff:
                    requests.popleft()
                if not requests:
                    stale_clients.append(key)
            for key in stale_clients:
                self.rate_limit_requests.pop(key, None)

            requests = self.rate_limit_requests.get(client_key)
            if requests is None:
                if len(self.rate_limit_requests) >= 4096:
                    return False
                requests = deque()
                self.rate_limit_requests[client_key] = requests
            if len(requests) >= self.max_requests_per_minute_per_client:
                return False
            requests.append(current)
            return True

    def get_request(self) -> Tuple[Any, Any]:
        request, client_address = super().get_request()
        request.settimeout(self.request_timeout_seconds)
        return request, client_address

    def process_request(self, request: Any, client_address: Tuple[str, int]) -> None:
        if not self.request_slots.acquire(blocking=False):
            try:
                request.sendall(
                    b"HTTP/1.1 503 Service Unavailable\r\n"
                    b"Connection: close\r\n"
                    b"Content-Type: text/plain; charset=utf-8\r\n"
                    b"Content-Length: 16\r\n\r\nServer is busy.\n"
                )
            finally:
                self.shutdown_request(request)
            return
        try:
            super().process_request(request, client_address)
        except Exception:
            self.request_slots.release()
            raise

    def process_request_thread(self, request: Any, client_address: Tuple[str, int]) -> None:
        try:
            super().process_request_thread(request, client_address)
        finally:
            self.request_slots.release()


class Handler(http.server.BaseHTTPRequestHandler):
    server_version = "SensorReadoutServer/1"

    def do_GET(self) -> None:
        self._dispatch()

    def do_HEAD(self) -> None:
        self._dispatch()

    def do_POST(self) -> None:
        self._dispatch()

    def do_PUT(self) -> None:
        self._dispatch()

    def do_DELETE(self) -> None:
        self._dispatch()

    def log_message(self, pattern: str, *args: Any) -> None:
        path = urlparse(self.path).path
        path = re.sub(r"(/api/v1/spaces/)[^/]+", r"\1<space>", path)
        path = re.sub(r"(/machines/)[^/]+", r"\1<machine>", path)
        path = re.sub(r"(/commands/)[^/]+", r"\1<command>", path)
        status = str(args[1]) if len(args) > 1 else ""
        try:
            status_code = int(status)
        except (TypeError, ValueError):
            status_code = 0
        log = logging.warning if status_code >= 400 else logging.debug
        log("%s %s %s %s", self._client_key(), self.command, path, status)

    @property
    def settings(self) -> Dict[str, Any]:
        return self.server.settings  # type: ignore[attr-defined]

    @property
    def store(self) -> StateStore:
        return self.server.store  # type: ignore[attr-defined]

    def _dispatch(self) -> None:
        try:
            parsed = urlparse(self.path)
            if parsed.path == "/api/v1/health" and self.command in ("GET", "HEAD"):
                self._json(200, {"Name": "Sensor Readout Server", "Version": SERVER_VERSION, "ProtocolVersion": PROTOCOL_VERSION})
                return
            if not self.server.allow_client_request(self._client_key()):  # type: ignore[attr-defined]
                self._text(429, "Too many requests", {"Retry-After": "60"})
                return
            if not self._authorized():
                self._text(401, "Unauthorized", {"WWW-Authenticate": "Bearer"})
                return

            space_match = SPACE_ROUTE.fullmatch(parsed.path)
            if space_match and self.command == "GET":
                self._json(200, {"ProtocolVersion": PROTOCOL_VERSION, "Machines": self.store.list_machines(space_match.group("space"))})
                return

            match = MACHINE_ROUTE.fullmatch(parsed.path)
            if not match:
                self._text(404, "Not found")
                return
            if not (match.group("suffix") or "") and self.command == "DELETE":
                self.store.delete_machine(match.group("space"), match.group("machine"), self._machine_token())
                self._json(200, {"Deleted": True})
                return
            self._machine_request(match.group("space"), match.group("machine"), match.group("suffix") or "", parsed.query)
        except SequenceConflict as error:
            self._text(409, "Remote sequence changed", {"X-SR-Latest-Sequence": str(error.latest)})
        except SnapshotRequired:
            self._text(428, "A complete snapshot is required")
        except PayloadTooLarge as error:
            self._text(413, "Encrypted payload is too large", {"X-SR-Max-Envelope-Bytes": str(error.maximum)})
        except StorageLimitExceeded as error:
            self._text(507, str(error))
        except MachineTokenRejected:
            self._text(403, "The computer publishing credential was rejected")
        except FileNotFoundError:
            self._text(404, "Not found")
        except (ValueError, json.JSONDecodeError) as error:
            self._text(400, str(error))
        except Exception:
            logging.exception("Request failed")
            self._text(500, "Server request failed")

    def _machine_request(self, space_id: str, machine_id: str, suffix: str, query: str) -> None:
        values = parse_qs(query)
        if suffix == "/snapshot" and self.command == "PUT":
            sequence = self._positive_int(values, "sequence")
            metadata = self.store.put_snapshot(space_id, machine_id, sequence, self._read_payload(), self._machine_token())
            self._json(200, metadata)
            return
        if suffix == "/snapshot" and self.command in ("GET", "HEAD"):
            payload, metadata = self.store.get_snapshot(space_id, machine_id)
            headers = {
                "X-SR-Snapshot-Sequence": str(int(metadata.get("SnapshotSequence", 0))),
                "X-SR-Latest-Sequence": str(int(metadata.get("LatestSequence", 0))),
            }
            self._bytes(200, payload, headers)
            return
        if suffix == "/deltas" and self.command == "POST":
            sequence = self._positive_int(values, "sequence")
            metadata = self.store.append_delta(space_id, machine_id, sequence, self._read_payload(), self._machine_token())
            self._json(200, metadata)
            return
        if suffix == "/deltas" and self.command == "GET":
            after = self._nonnegative_int(values, "after")
            deltas, metadata = self.store.get_deltas(space_id, machine_id, after)
            self._json(200, {
                "ProtocolVersion": PROTOCOL_VERSION,
                "SnapshotSequence": int(metadata.get("SnapshotSequence", 0)),
                "LatestSequence": int(metadata.get("LatestSequence", 0)),
                "Deltas": deltas,
            })
            return
        if suffix == "/heartbeat" and self.command == "POST":
            self._json(200, self.store.heartbeat(space_id, machine_id, self._machine_token()))
            return
        if suffix == "/commands" and self.command == "GET":
            self._json(200, {"ProtocolVersion": PROTOCOL_VERSION, "Commands": self.store.get_commands(space_id, machine_id, self._machine_token())})
            return
        if suffix.startswith("/commands/"):
            command_id = suffix.rsplit("/", 1)[1]
            if self.command == "POST":
                self.store.put_command(space_id, machine_id, command_id, self._read_payload())
                self._json(201, {"Accepted": True})
                return
            if self.command == "DELETE":
                self.store.delete_command(space_id, machine_id, command_id, self._machine_token())
                self._json(200, {"Deleted": True})
                return
        self._text(405, "Method not allowed", {"Allow": "GET, HEAD, POST, PUT, DELETE"})

    def _authorized(self) -> bool:
        configured = str(self.settings.get("AuthToken", "")).strip()
        supplied = self.headers.get("Authorization", "").strip()
        expected = "Bearer " + configured
        return bool(configured) and hmac.compare_digest(supplied.encode("utf-8"), expected.encode("utf-8"))

    def _client_key(self) -> str:
        direct = str(self.client_address[0])
        try:
            direct_address = ipaddress.ip_address(direct)
        except ValueError:
            return direct
        if not direct_address.is_loopback:
            return direct
        forwarded_values = self.headers.get("X-Forwarded-For", "").split(",")
        forwarded = forwarded_values[-1].strip() if forwarded_values else ""
        try:
            return str(ipaddress.ip_address(forwarded)) if forwarded else direct
        except ValueError:
            return direct

    def _machine_token(self) -> str:
        return self.headers.get("X-SR-Machine-Token", "").strip()

    def _read_payload(self) -> bytes:
        length_text = self.headers.get("Content-Length", "")
        try:
            length = int(length_text)
        except ValueError:
            raise ValueError("Content-Length is required.")
        if length < 1:
            raise ValueError("Encrypted payload is empty.")
        if length > self.store.max_envelope_bytes:
            # Drain a narrowly oversized request so Windows can receive the
            # 413 response instead of resetting the connection mid-upload.
            if length <= self.store.max_envelope_bytes + 64 * 1024:
                remaining = length
                while remaining > 0:
                    chunk = self.rfile.read(min(64 * 1024, remaining))
                    if not chunk:
                        break
                    remaining -= len(chunk)
            raise PayloadTooLarge(self.store.max_envelope_bytes)
        data = self.rfile.read(length)
        if len(data) != length:
            raise ValueError("Encrypted payload ended unexpectedly.")
        return data

    @staticmethod
    def _positive_int(values: Dict[str, List[str]], name: str) -> int:
        value = Handler._nonnegative_int(values, name)
        if value < 1:
            raise ValueError(name + " must be positive.")
        return value

    @staticmethod
    def _nonnegative_int(values: Dict[str, List[str]], name: str) -> int:
        raw = values.get(name, [""])[0]
        try:
            value = int(raw)
        except ValueError:
            raise ValueError(name + " must be a number.")
        if value < 0:
            raise ValueError(name + " cannot be negative.")
        return value

    def _json(self, status: int, value: Dict[str, Any]) -> None:
        self._bytes(status, json.dumps(value, separators=(",", ":")).encode("utf-8"), {"Content-Type": "application/json; charset=utf-8"})

    def _text(self, status: int, value: str, headers: Optional[Dict[str, str]] = None) -> None:
        actual = {"Content-Type": "text/plain; charset=utf-8"}
        actual.update(headers or {})
        self._bytes(status, value.encode("utf-8"), actual)

    def _bytes(self, status: int, value: bytes, headers: Optional[Dict[str, str]] = None) -> None:
        self.send_response(status)
        for name, item in (headers or {}).items():
            self.send_header(name, item)
        self.send_header("Content-Length", str(len(value)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.end_headers()
        if self.command != "HEAD":
            self.wfile.write(value)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Sensor Readout remote monitoring server")
    parser.add_argument("--version", action="version", version="Sensor Readout Server " + SERVER_VERSION)
    parser.add_argument("--config", type=Path, default=default_config_path())
    parser.add_argument("--host")
    parser.add_argument("--port", type=int)
    parser.add_argument("--public-url", help="public HTTP or HTTPS address clients should use")
    parser.add_argument(
        "--write-connection-info",
        nargs="?",
        const="",
        metavar="PATH",
        help="write an importable .srconnection file, optionally at PATH",
    )
    parser.add_argument(
        "--connection-info-only",
        action="store_true",
        help="write connection information and exit without starting the relay",
    )
    return parser.parse_args()


def write_connection_info(config_path: Path, settings: Dict[str, Any], output_path: Optional[Path] = None) -> Path:
    host = str(settings.get("Host", "127.0.0.1"))
    port = int(settings.get("Port", 48673))
    public_url = str(settings.get("PublicUrl", "")).strip()
    if public_url:
        server_url = normalize_public_url(public_url)
    else:
        normalized_host = host.strip("[]").lower()
        try:
            unusable_host = ipaddress.ip_address(normalized_host).is_loopback
        except ValueError:
            unusable_host = normalized_host == "localhost"
        if normalized_host in ("0.0.0.0", "::", "") or unusable_host:
            raise ValueError("Set PublicUrl or use --public-url before generating connection information. Loopback and wildcard listening addresses cannot be reached by another computer.")
        display_host = "[%s]" % host if ":" in host and not host.startswith("[") else host
        server_url = "http://%s:%s/" % (display_host, port)
    document = {
        "Format": "SensorReadoutRemoteConnection",
        "ProtocolVersion": PROTOCOL_VERSION,
        "ServerUrl": server_url,
        "Token": str(settings.get("AuthToken", "")),
    }
    path = output_path or config_path.with_name("sensor-readout-connection.srconnection")
    atomic_json(path, document)
    return path


def main() -> int:
    args = parse_args()
    settings, created = load_settings(args.config.expanduser().resolve())
    if args.host:
        settings["Host"] = args.host
    if args.port:
        settings["Port"] = args.port
    if args.public_url:
        settings["PublicUrl"] = args.public_url
    if args.host or args.port or args.public_url:
        atomic_json(args.config, settings)
    validate_auth_token(settings.get("AuthToken"))
    configure_logging(Path(str(settings["LogPath"])).expanduser().resolve())
    if args.connection_info_only and args.write_connection_info is None:
        raise ValueError("--connection-info-only requires --write-connection-info")
    if args.write_connection_info is not None:
        output_path = None
        if args.write_connection_info:
            output_path = Path(args.write_connection_info).expanduser().resolve()
        path = write_connection_info(args.config, settings, output_path)
        print("Connection details: " + str(path))
    elif created:
        try:
            path = write_connection_info(args.config, settings)
            print("Connection details: " + str(path))
        except ValueError:
            print("No shareable connection file was written. Set PublicUrl or use --public-url with an address other computers can reach, then use --write-connection-info.")
    if args.connection_info_only:
        return 0
    host = str(settings["Host"])
    port = int(settings["Port"])
    server = ThreadingServer((host, port), Handler, settings)
    logging.info("Sensor Readout Server %s protocol %s listening on %s:%s", SERVER_VERSION, PROTOCOL_VERSION, host, port)
    try:
        server.serve_forever(poll_interval=0.5)
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
