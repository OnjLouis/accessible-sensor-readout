#!/usr/bin/env python3
"""Verified install, update, rollback, and service control for Sensor Readout Server."""

from __future__ import annotations

import argparse
import base64
from contextlib import contextmanager
import hashlib
import hmac
import ipaddress
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import threading
import time
import urllib.request
import zipfile
from pathlib import PurePosixPath
from urllib.parse import urlparse
from typing import Any, Dict, Iterable, List, Optional, Tuple


MANIFEST_FORMAT = "SensorReadoutServerPackage"
MANIFEST_COMPONENT = "LinuxServer"
MANIFEST_ALGORITHM = "RSA-SHA256"
MANIFEST_NAME = "sensor-readout-server-manifest.json"
MANIFEST_RSA_MODULUS_B64 = (
    "vWihMIt1Sm7uWv9QQD+3Svk5fzthiiILv/zbJVWlljA8Z07WpNBuIMAE2fDG19Loi9fZVrmIYV+DN1jPsLSAgoz0j"
    "n2rd/qgUz5IU1NdTikCW/QRxPw6omWwPr7Kx/xS6BabGC8vntZt+U4E1kvUzaFp+1N5f/43jKy4A7Q9dXrhvDp1jZ"
    "d+xlDfNEgagWS19EtDw2CarQ5mubD4XdRplUW2bQ4QNA8Emp36MZrQy2GMer0TGWKngINdKlVUnrnW/oabopK8EQLH"
    "vu/6iS80LNzyJ88FkH9eE+aTl5ZO/SnnnTqCkLSs1VMuoQ2rhXUzgGPcs9PFZLiXFOV4x/U9a7Epo6hiigopV+Q4j"
    "op36KPYnXyUpNb7M6qeOioZr9WuTAqTwYbAxkQnzWY4iKEkHkd5JRiPf1s08PeKg5mlQObL8PLrXGyCKkN57o7ysz3"
    "V96t5GXtDxWkdmvAhVb/KlDYUz/xGzh+KHBEzcEbt3CirjoOqoUEmG0vcODSVDsP5"
)
MANIFEST_RSA_EXPONENT_B64 = "AQAB"
SHA256_DIGEST_INFO_PREFIX = bytes.fromhex("3031300d060960864801650304020105000420")
VERSION_NAME = "VERSION"
SERVICE_NAME = "sensor-readout-server.service"
UPDATE_SERVICE_NAME = "sensor-readout-server-update.service"
UPDATE_TIMER_NAME = "sensor-readout-server-update.timer"
RELEASE_API = "https://api.github.com/repos/OnjLouis/accessible-sensor-readout/releases?per_page=100"
SERVER_TAG_PATTERN = re.compile(r"^server-v(?P<version>[0-9]+\.[0-9]+\.[0-9]+)$", re.IGNORECASE)
MAX_RELEASE_RESPONSE_BYTES = 2 * 1024 * 1024
MAX_DOWNLOAD_BYTES = 25 * 1024 * 1024
MAX_EXTRACTED_BYTES = 50 * 1024 * 1024
MAX_ZIP_ENTRIES = 64
PACKAGE_FILES = (
    VERSION_NAME,
    "Manual.html",
    "sensor_readout_server.py",
    "sensor_readout_server_control.py",
    "sensor-readout-server-settings.example.json",
    "sensor-readout-server-systemd-settings.example.json",
    "sensor-readout-server.service.example",
)
VERSION_PATTERN = re.compile(r"^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][A-Za-z0-9.-]+)?$")


class ControlError(Exception):
    pass


class DeploymentTerminated(Exception):
    pass


class Layout:
    def __init__(self) -> None:
        self.test_mode = os.environ.get("SR_SERVER_TEST_MODE", "") == "1"
        self.install_root = Path(os.environ.get("SR_SERVER_INSTALL_ROOT", "/opt/sensor-readout-server")).resolve()
        self.releases = self.install_root / "releases"
        self.current = self.install_root / "current"
        self.config = Path(os.environ.get("SR_SERVER_CONFIG", "/etc/sensor-readout-server/settings.json")).resolve()
        self.data_root = Path(os.environ.get("SR_SERVER_DATA_ROOT", "/var/lib/sensor-readout-server")).resolve()
        self.log_root = Path(os.environ.get("SR_SERVER_LOG_ROOT", "/var/log/sensor-readout-server")).resolve()
        self.service_file = Path(
            os.environ.get("SR_SERVER_SERVICE_FILE", "/etc/systemd/system/" + SERVICE_NAME)
        ).resolve()
        self.update_service_file = Path(
            os.environ.get("SR_SERVER_UPDATE_SERVICE_FILE", "/etc/systemd/system/" + UPDATE_SERVICE_NAME)
        ).resolve()
        self.update_timer_file = Path(
            os.environ.get("SR_SERVER_UPDATE_TIMER_FILE", "/etc/systemd/system/" + UPDATE_TIMER_NAME)
        ).resolve()
        self.control_link = Path(
            os.environ.get("SR_SERVER_CONTROL_LINK", "/usr/local/bin/sensor-readout-server-control")
        ).resolve()
        self.systemctl = os.environ.get("SR_SERVER_SYSTEMCTL", "systemctl")
        self.skip_health = self.test_mode and os.environ.get("SR_SERVER_TEST_HEALTH", "") != "1"


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        while True:
            block = source.read(1024 * 1024)
            if not block:
                break
            digest.update(block)
    return digest.hexdigest().upper()


def reject_duplicate_json_keys(pairs: List[Tuple[str, Any]]) -> Dict[str, Any]:
    value: Dict[str, Any] = {}
    for key, item in pairs:
        if key in value:
            raise ValueError("duplicate JSON key: %s" % key)
        value[key] = item
    return value


def canonical_manifest_bytes(manifest: Dict[str, Any]) -> bytes:
    canonical = "\n".join(
        (
            str(manifest["Format"]),
            str(manifest["Component"]),
            str(manifest["Version"]),
            str(manifest["Algorithm"]),
        )
    ) + "\n"
    for path in sorted(manifest["Files"]):
        canonical += "%s\t%s\n" % (path, str(manifest["Files"][path]).upper())
    return canonical.encode("utf-8")


def verify_manifest_signature(manifest: Dict[str, Any]) -> None:
    signature_text = manifest.get("Signature")
    if not isinstance(signature_text, str) or not signature_text:
        raise ControlError("Package manifest signature is missing.")
    try:
        signature = base64.b64decode(signature_text, validate=True)
        modulus_bytes = base64.b64decode(MANIFEST_RSA_MODULUS_B64, validate=True)
        exponent_bytes = base64.b64decode(MANIFEST_RSA_EXPONENT_B64, validate=True)
    except (ValueError, TypeError) as error:
        raise ControlError("Package manifest signature or public key is malformed: %s" % error)
    if not modulus_bytes or not exponent_bytes:
        raise ControlError("Package manifest public key is malformed.")
    modulus = int.from_bytes(modulus_bytes, "big")
    exponent = int.from_bytes(exponent_bytes, "big")
    key_bytes = (modulus.bit_length() + 7) // 8
    signature_value = int.from_bytes(signature, "big")
    if len(signature) != key_bytes or not 1 <= signature_value < modulus:
        raise ControlError("Package manifest signature is malformed.")
    digest_info = SHA256_DIGEST_INFO_PREFIX + hashlib.sha256(canonical_manifest_bytes(manifest)).digest()
    padding_length = key_bytes - len(digest_info) - 3
    if padding_length < 8:
        raise ControlError("Package manifest public key is too short.")
    expected = b"\x00\x01" + (b"\xff" * padding_length) + b"\x00" + digest_info
    recovered = pow(signature_value, exponent, modulus).to_bytes(key_bytes, "big")
    if not hmac.compare_digest(recovered, expected):
        raise ControlError("Package manifest signature verification failed.")


def load_manifest(package: Path) -> Dict[str, Any]:
    manifest_path = package / MANIFEST_NAME
    try:
        value = json.loads(
            manifest_path.read_text(encoding="utf-8"),
            object_pairs_hook=reject_duplicate_json_keys,
        )
    except (OSError, ValueError) as error:
        raise ControlError("Could not read package manifest: %s" % error)
    if not isinstance(value, dict) or value.get("Format") != MANIFEST_FORMAT:
        raise ControlError("Package manifest format is not supported.")
    if value.get("Component") != MANIFEST_COMPONENT:
        raise ControlError("Package manifest component is not supported.")
    if value.get("Algorithm") != MANIFEST_ALGORITHM:
        raise ControlError("Package manifest signature algorithm is not supported.")
    version = value.get("Version")
    if not isinstance(version, str) or not VERSION_PATTERN.fullmatch(version):
        raise ControlError("Package manifest has an invalid version.")
    files = value.get("Files")
    if not isinstance(files, dict) or set(files) != set(PACKAGE_FILES):
        raise ControlError("Package manifest file allow-list is incomplete or unexpected.")
    for name, expected_hash in files.items():
        if not isinstance(name, str) or not re.fullmatch(r"[A-F0-9]{64}", str(expected_hash)):
            raise ControlError("Manifest contains an invalid uppercase SHA-256 for %s." % name)
    verify_manifest_signature(value)
    return value


def verify_package(package: Path) -> Dict[str, Any]:
    package = package.resolve()
    if not package.is_dir():
        raise ControlError("Package folder does not exist: %s" % package)
    expected = set(PACKAGE_FILES) | {MANIFEST_NAME}
    actual = set()
    for item in package.iterdir():
        if item.is_symlink() or not item.is_file():
            raise ControlError("Package contains a non-regular entry: %s" % item.name)
        actual.add(item.name)
    if actual != expected:
        missing = sorted(expected - actual)
        unexpected = sorted(actual - expected)
        raise ControlError("Package allow-list mismatch; missing=%s unexpected=%s" % (missing, unexpected))
    manifest = load_manifest(package)
    version = str(manifest["Version"])
    if (package / VERSION_NAME).read_text(encoding="utf-8").strip() != version:
        raise ControlError("VERSION does not match the package manifest.")
    server_text = (package / "sensor_readout_server.py").read_text(encoding="utf-8")
    version_match = re.search(r'^SERVER_VERSION\s*=\s*"([^"]+)"', server_text, re.MULTILINE)
    if not version_match or version_match.group(1) != version:
        raise ControlError("Server source version does not match the package manifest.")
    for name, expected_hash in manifest["Files"].items():
        actual_hash = sha256_file(package / name)
        if actual_hash != str(expected_hash):
            raise ControlError("SHA-256 verification failed for %s." % name)
    return manifest


def stable_version_tuple(value: str) -> Tuple[int, int, int]:
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", value.strip()):
        raise ControlError("Invalid stable server version: %s" % value)
    parts = tuple(int(part) for part in value.strip().split("."))
    return parts[0], parts[1], parts[2]


def allowed_update_url(value: str, allow_loopback_http: bool = False) -> bool:
    parsed = urlparse(value)
    if parsed.scheme.lower() == "https":
        return True
    if parsed.scheme.lower() != "http" or not allow_loopback_http:
        return False
    hostname = (parsed.hostname or "").strip().lower()
    if hostname == "localhost":
        return True
    try:
        return ipaddress.ip_address(hostname).is_loopback
    except ValueError:
        return False


def read_releases(api_url: str = RELEASE_API, allow_loopback_http: bool = False) -> List[Dict[str, Any]]:
    if not allowed_update_url(api_url, allow_loopback_http):
        raise ControlError("The server update service must use HTTPS.")
    request = urllib.request.Request(
        api_url,
        headers={"Accept": "application/vnd.github+json", "User-Agent": "Sensor-Readout-Server-Updater"},
    )
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            if not allowed_update_url(response.geturl(), allow_loopback_http):
                raise ControlError("The server update service redirected outside HTTPS.")
            payload = response.read(MAX_RELEASE_RESPONSE_BYTES + 1)
    except ControlError:
        raise
    except Exception as error:
        raise ControlError("Could not read Sensor Readout Server releases: %s" % error)
    if len(payload) > MAX_RELEASE_RESPONSE_BYTES:
        raise ControlError("The server update service response was unexpectedly large.")
    try:
        releases = json.loads(payload.decode("utf-8"))
    except (UnicodeDecodeError, ValueError) as error:
        raise ControlError("The server update service returned invalid JSON: %s" % error)
    if not isinstance(releases, list):
        raise ControlError("The server update service returned an unexpected response.")
    return releases


def find_server_update(
    releases: Iterable[Dict[str, Any]], current_version: str, allow_loopback_http: bool = False
) -> Optional[Tuple[str, Dict[str, Any]]]:
    candidates: List[Tuple[str, Dict[str, Any]]] = []
    for release in releases:
        if not isinstance(release, dict) or release.get("draft") or release.get("prerelease"):
            continue
        match = SERVER_TAG_PATTERN.fullmatch(str(release.get("tag_name", "")).strip())
        if match:
            candidates.append((match.group("version"), release))
    if not candidates:
        return None
    version, release = max(candidates, key=lambda item: stable_version_tuple(item[0]))
    if stable_version_tuple(version) <= stable_version_tuple(current_version):
        return None
    expected_name = "SensorReadout-Server-%s.zip" % version
    matching_assets = [
        asset
        for asset in release.get("assets", [])
        if isinstance(asset, dict) and str(asset.get("name", "")).lower() == expected_name.lower()
    ]
    if len(matching_assets) != 1:
        raise ControlError(
            "Sensor Readout Server %s is available, but its exact server archive %s is missing or duplicated."
            % (version, expected_name)
        )
    asset = matching_assets[0]
    download_url = str(asset.get("browser_download_url", "")).strip()
    if not allowed_update_url(download_url, allow_loopback_http):
        raise ControlError("The server update download must use HTTPS.")
    return version, asset


def verify_release_digest(expected_digest: str, actual_hex: str) -> None:
    expected = str(expected_digest or "").strip().lower()
    actual = "sha256:" + actual_hex.lower()
    if not re.fullmatch(r"sha256:[0-9a-f]{64}", expected):
        raise ControlError("GitHub did not provide a valid SHA-256 digest for the server update.")
    if expected != actual:
        raise ControlError("The downloaded server update failed its SHA-256 check.")


def download_server_asset(
    asset: Dict[str, Any], destination: Path, allow_loopback_http: bool = False
) -> None:
    download_url = str(asset.get("browser_download_url", "")).strip()
    if not allowed_update_url(download_url, allow_loopback_http):
        raise ControlError("The server update download must use HTTPS.")
    request = urllib.request.Request(
        download_url,
        headers={"Accept": "application/octet-stream", "User-Agent": "Sensor-Readout-Server-Updater"},
    )
    digest = hashlib.sha256()
    total = 0
    try:
        with urllib.request.urlopen(request, timeout=90) as response, destination.open("wb") as output:
            if not allowed_update_url(response.geturl(), allow_loopback_http):
                raise ControlError("The server update download redirected outside HTTPS.")
            while True:
                block = response.read(64 * 1024)
                if not block:
                    break
                total += len(block)
                if total > MAX_DOWNLOAD_BYTES:
                    raise ControlError("The server update package was unexpectedly large.")
                digest.update(block)
                output.write(block)
    except ControlError:
        raise
    except Exception as error:
        raise ControlError("Could not download the server update: %s" % error)
    verify_release_digest(str(asset.get("digest", "")), digest.hexdigest())


def safe_extract_server_archive(zip_path: Path, destination: Path) -> None:
    try:
        with zipfile.ZipFile(zip_path) as archive:
            entries = archive.infolist()
            if len(entries) > MAX_ZIP_ENTRIES:
                raise ControlError("The server update archive contains too many entries.")
            if sum(entry.file_size for entry in entries) > MAX_EXTRACTED_BYTES:
                raise ControlError("The extracted server update would be unexpectedly large.")
            destination_root = destination.resolve()
            extracted_paths = set()
            for entry in entries:
                relative = PurePosixPath(entry.filename.replace("\\", "/"))
                unix_mode = (entry.external_attr >> 16) & 0o170000
                if (
                    relative.is_absolute()
                    or ".." in relative.parts
                    or not relative.parts
                    or unix_mode not in (0, 0o040000, 0o100000)
                    or entry.flag_bits & 0x1
                ):
                    raise ControlError("The server update archive contains an unsafe entry.")
                output = destination_root.joinpath(*relative.parts).resolve()
                try:
                    output.relative_to(destination_root)
                except ValueError:
                    raise ControlError("The server update archive contains an unsafe entry.")
                normalized = str(output)
                if normalized in extracted_paths:
                    raise ControlError("The server update archive contains a duplicated entry.")
                extracted_paths.add(normalized)
                if entry.is_dir():
                    output.mkdir(parents=True, exist_ok=True)
                    continue
                output.parent.mkdir(parents=True, exist_ok=True)
                with archive.open(entry) as source, output.open("xb") as target:
                    shutil.copyfileobj(source, target, length=64 * 1024)
    except ControlError:
        raise
    except (OSError, zipfile.BadZipFile) as error:
        raise ControlError("Could not extract the server update archive: %s" % error)


def locate_server_package(extracted: Path, expected_version: str) -> Path:
    manifests = list(extracted.rglob(MANIFEST_NAME))
    if len(manifests) != 1:
        raise ControlError("The server update archive must contain exactly one server package manifest.")
    package = manifests[0].parent
    manifest = verify_package(package)
    if str(manifest.get("Version", "")) != expected_version:
        raise ControlError("The server update package version does not match its server release tag.")
    return package


def require_root(layout: Layout) -> None:
    if layout.test_mode or os.name == "nt":
        return
    if not hasattr(os, "geteuid") or os.geteuid() != 0:
        raise ControlError("Run this command as root, for example with sudo.")


def require_supported_service_host(layout: Layout) -> None:
    if layout.test_mode:
        return
    if not sys.platform.startswith("linux"):
        raise ControlError("The managed Sensor Readout Server service installer currently supports Linux with systemd.")
    if sys.version_info < (3, 8):
        raise ControlError("Sensor Readout Server requires Python 3.8 or later.")
    if not shutil.which(layout.systemctl):
        raise ControlError("systemctl was not found. This installer currently requires a systemd-based Linux system.")
    if not Path("/run/systemd/system").is_dir():
        raise ControlError("systemd is not running. The managed service installer cannot continue on this Linux system.")


def run(command: List[str], check: bool = True, capture: bool = False) -> subprocess.CompletedProcess:
    return subprocess.run(command, check=check, text=True, capture_output=capture)


def systemctl(layout: Layout, *arguments: str, check: bool = True) -> subprocess.CompletedProcess:
    if layout.test_mode and not layout.systemctl:
        return subprocess.CompletedProcess(["systemctl"] + list(arguments), 0, "", "")
    return run([layout.systemctl] + list(arguments), check=check, capture=True)


def ensure_service_account(layout: Layout) -> None:
    if layout.test_mode or os.name == "nt":
        return
    if run(["getent", "group", "sensor-readout"], check=False).returncode != 0:
        run(["groupadd", "--system", "sensor-readout"])
    if run(["id", "-u", "sensor-readout"], check=False).returncode != 0:
        run(
            [
                "useradd",
                "--system",
                "--gid",
                "sensor-readout",
                "--home-dir",
                str(layout.data_root),
                "--shell",
                "/usr/sbin/nologin",
                "sensor-readout",
            ]
        )


def secure_directory(path: Path, mode: int = 0o750) -> None:
    path.mkdir(parents=True, exist_ok=True)
    if os.name != "nt":
        path.chmod(mode)


def ensure_parent_directory(path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)


def chown_service(path: Path) -> None:
    if os.name != "nt":
        shutil.chown(str(path), user="sensor-readout", group="sensor-readout")


def secure_config_directory(path: Path) -> None:
    secure_directory(path, 0o750)
    if os.name != "nt":
        shutil.chown(str(path), user="root", group="sensor-readout")


def normalize_public_url(value: str) -> str:
    parsed = urlparse(value)
    try:
        public_port = parsed.port
    except ValueError:
        raise ControlError("Public URL contains an invalid port.")
    path = parsed.path or "/"
    segments = [segment for segment in path.split("/") if segment]
    hostname = (parsed.hostname or "").strip().lower()
    try:
        unreachable_host = ipaddress.ip_address(hostname.split("%", 1)[0]).is_loopback
    except ValueError:
        unreachable_host = hostname == "localhost"
    if (
        parsed.scheme not in ("http", "https")
        or not hostname
        or hostname in ("0.0.0.0", "::")
        or unreachable_host
        or parsed.username is not None
        or parsed.password is not None
        or parsed.params
        or parsed.query
        or parsed.fragment
        or public_port == 0
        or "\\" in path
        or "%" in path
        or "//" in path
        or any(segment in (".", "..") or not re.fullmatch(r"[A-Za-z0-9._~-]+", segment) for segment in segments)
    ):
        raise ControlError("Public URL must be an HTTP or HTTPS address with a reachable non-loopback, non-wildcard host, an optional simple path prefix, and no credentials, query text, or fragment.")
    return value.rstrip("/") + "/"


def interactive_terminal() -> bool:
    return bool(sys.stdin.isatty() and sys.stdout.isatty())


def prompt_choice(title: str, choices: List[str], default: int = 1) -> int:
    print(title)
    for index, choice in enumerate(choices, start=1):
        print("  %s. %s" % (index, choice))
    while True:
        answer = input("Choice [%s]: " % default).strip()
        if not answer:
            return default
        if answer.isdigit() and 1 <= int(answer) <= len(choices):
            return int(answer)
        print("Enter a number from 1 to %s." % len(choices))


def prompt_port(default: int = 48673) -> int:
    while True:
        answer = input("Listening port [%s]: " % default).strip()
        if not answer:
            return default
        try:
            port = int(answer)
        except ValueError:
            print("Enter a whole-number port.")
            continue
        if 1024 <= port <= 65535:
            return port
        print("Enter a port from 1024 to 65535.")


def normalize_share_host(value: str) -> str:
    host = value.strip().strip("[]")
    if not host or any(character.isspace() for character in host) or "/" in host or "@" in host:
        raise ControlError("Enter a computer name or IP address, not a URL or path.")
    try:
        address = ipaddress.ip_address(host.split("%", 1)[0])
        if address.is_loopback or address.is_unspecified or address.is_multicast or address.is_link_local:
            raise ControlError("Choose an address that another computer can reach.")
        return host
    except ValueError:
        hostname = host.rstrip(".")
        labels = hostname.split(".")
        if (
            not hostname
            or len(hostname) > 253
            or any(
                len(label) > 63
                or not re.fullmatch(r"[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?", label)
                for label in labels
            )
        ):
            raise ControlError("Enter a valid computer or DNS name, or an IP address.")
        if hostname.lower() == "localhost":
            raise ControlError("localhost can only be reached from this server.")
        return hostname


def public_url_for_host(host: str, port: int) -> str:
    display_host = host
    try:
        if ipaddress.ip_address(host.split("%", 1)[0]).version == 6:
            display_host = "[%s]" % host
    except ValueError:
        pass
    return "http://%s:%s/" % (display_host, port)


def detected_reachable_addresses() -> List[str]:
    candidates: List[str] = []

    def add(value: str) -> None:
        try:
            normalized = normalize_share_host(value)
        except ControlError:
            return
        if normalized not in candidates:
            candidates.append(normalized)

    if os.name != "nt":
        try:
            result = run(["hostname", "-I"], check=False, capture=True)
            for value in (result.stdout or "").split():
                add(value)
        except OSError:
            pass
    try:
        for result in socket.getaddrinfo(socket.gethostname(), None, type=socket.SOCK_STREAM):
            add(str(result[4][0]))
    except OSError:
        pass
    return candidates


def prompt_share_host(current: str = "") -> str:
    detected = detected_reachable_addresses()
    if current:
        try:
            normalized_current = normalize_share_host(current)
            if normalized_current not in detected:
                detected.insert(0, normalized_current)
        except ControlError:
            pass
    choices = ["%s (detected on this computer)" % value for value in detected]
    choices.append("Enter another local-network, private-network, VPN, or DNS address")
    selected = prompt_choice("Which address should other computers use?", choices)
    if selected <= len(detected):
        return detected[selected - 1]
    while True:
        answer = input("Reachable computer name or IP address: ").strip()
        try:
            return normalize_share_host(answer)
        except ControlError as error:
            print(error)


def prompt_https_url(current: str = "") -> str:
    while True:
        suffix = " [%s]" % current if current else ""
        answer = input("Full HTTPS address other computers will use%s: " % suffix).strip()
        if not answer and current:
            answer = current
        try:
            normalized = normalize_public_url(answer)
            if urlparse(normalized).scheme != "https":
                raise ControlError("This option requires an address beginning with https://.")
            return normalized
        except ControlError as error:
            print(error)


def prompt_confirmation() -> bool:
    while True:
        answer = input("Install with these settings? [Y/n]: ").strip().lower()
        if answer in ("", "y", "yes"):
            return True
        if answer in ("n", "no"):
            return False
        print("Enter Y or N.")


def prompt_number(label: str, default: int, minimum: int, maximum: int) -> int:
    while True:
        answer = input("%s [%s]: " % (label, default)).strip()
        if not answer:
            return default
        try:
            value = int(answer)
        except ValueError:
            print("Enter a whole number.")
            continue
        if minimum <= value <= maximum:
            return value
        print("Enter a value from %s to %s." % (minimum, maximum))


def prompt_yes_no(label: str, default: bool) -> bool:
    suffix = "Y/n" if default else "y/N"
    while True:
        answer = input("%s [%s]: " % (label, suffix)).strip().lower()
        if not answer:
            return default
        if answer in ("y", "yes"):
            return True
        if answer in ("n", "no"):
            return False
        print("Enter Y or N.")


def configure_install_wizard(args: argparse.Namespace) -> argparse.Namespace:
    if not interactive_terminal():
        raise ControlError(
            "The guided installer needs an interactive terminal. Provide a package folder and options "
            "for unattended installation; run --help for details."
        )
    package = Path(__file__).resolve().parent
    manifest = verify_package(package)
    print("Sensor Readout Server %s guided installation" % manifest["Version"])
    print("This installs the verified service and creates a connection file for Sensor Readout.")
    print("It does not change your firewall, router, VPN, reverse proxy, or TLS certificate.")
    mode = prompt_choice(
        "How will Sensor Readout computers reach this server?",
        [
            "Trusted local network or protected private network, including a VPN (recommended)",
            "Existing HTTPS reverse proxy or public HTTPS address",
        ],
    )
    port = prompt_port()
    if mode == 1:
        share_host = prompt_share_host()
        host = "0.0.0.0"
        public_url = public_url_for_host(share_host, port)
        exposure = "private LAN or VPN"
    else:
        host = "127.0.0.1"
        public_url = prompt_https_url()
        exposure = "HTTPS reverse proxy"
    print("\nInstallation summary")
    print("  Package: %s" % package)
    print("  Connection type: %s" % exposure)
    print("  Server listens on: %s:%s" % (host, port))
    print("  Sensor Readout connects to: %s" % public_url)
    if not prompt_confirmation():
        raise ControlError("Installation cancelled; no changes were made.")
    args.package = str(package)
    args.host = host
    args.port = port
    args.public_url = public_url
    args.guided = True
    return args


def configure_setup_wizard(args: argparse.Namespace, layout: Layout) -> argparse.Namespace:
    if not interactive_terminal():
        raise ControlError(
            "The setup wizard needs an interactive terminal. Supply setup options with --yes for unattended use."
        )
    settings = read_config(layout)
    current_url = str(settings.get("PublicUrl", "")).strip()
    parsed = urlparse(current_url) if current_url else None
    current_private_host = parsed.hostname if parsed and parsed.scheme == "http" else ""
    default_mode = 2 if parsed and parsed.scheme == "https" else 1
    print("Sensor Readout Server setup")
    print("This changes relay settings, restarts the service, and restores the old settings if health checks fail.")
    print("The monitoring password cannot be changed here because it never reaches or resides on the server.")
    mode = prompt_choice(
        "How do Sensor Readout computers reach this server?",
        [
            "Trusted local network or protected private network, including a VPN",
            "Existing HTTPS reverse proxy or public HTTPS address",
        ],
        default_mode,
    )
    port = prompt_port(int(settings.get("Port", 48673)))
    if mode == 1:
        share_host = prompt_share_host(current_private_host)
        host = "0.0.0.0"
        public_url = public_url_for_host(share_host, port)
        exposure = "trusted local or protected private network"
    else:
        host = "127.0.0.1"
        public_url = prompt_https_url(current_url if parsed and parsed.scheme == "https" else "")
        exposure = "HTTPS reverse proxy"
    request_timeout = prompt_number(
        "Per-connection timeout in seconds", int(settings.get("RequestTimeoutSeconds", 30)), 5, 300
    )
    retention_days = prompt_number(
        "Remove inactive computers after this many days; 0 keeps them", int(settings.get("RetentionDays", 90)), 0, 3650
    )
    automatic_updates = prompt_yes_no(
        "Install verified Sensor Readout Server updates automatically", bool(settings.get("AutomaticUpdatesEnabled", True))
    )
    regenerate_token = prompt_yes_no(
        "Generate a new server access token and invalidate existing connection files", False
    )
    print("\nSetup summary")
    print("  Connection type: %s" % exposure)
    print("  Server listens on: %s:%s" % (host, port))
    print("  Sensor Readout connects to: %s" % public_url)
    print("  Request timeout: %s seconds" % request_timeout)
    print("  Inactive-computer retention: %s days" % retention_days)
    print("  Automatic server updates: %s" % ("enabled" if automatic_updates else "disabled"))
    print("  New server access token: %s" % ("yes" if regenerate_token else "no"))
    if not prompt_confirmation():
        raise ControlError("Setup cancelled; no changes were made.")
    args.host = host
    args.port = port
    args.public_url = public_url
    args.request_timeout = request_timeout
    args.retention_days = retention_days
    args.automatic_updates = automatic_updates
    args.regenerate_token = regenerate_token
    args.write_connection = regenerate_token or public_url != current_url
    args.yes = True
    return args


def create_or_update_config(
    layout: Layout,
    host: Optional[str],
    port: Optional[int],
    public_url: Optional[str],
) -> None:
    secure_config_directory(layout.config.parent)
    if layout.config.exists():
        value = json.loads(layout.config.read_text(encoding="utf-8"))
        if not isinstance(value, dict):
            raise ControlError("Existing server settings are not a JSON object.")
    else:
        value = {
            "Host": "127.0.0.1",
            "Port": 48673,
            "PublicUrl": "",
            "DataPath": str(layout.data_root / "Data"),
            "AuthToken": secrets.token_urlsafe(48),
            "LogPath": str(layout.log_root / "server.log"),
            "MaxEnvelopeBytes": 8388608,
            "MaxDeltasPerMachine": 64,
            "MaxDeltaBytesPerMachine": 8388608,
            "MaxBufferedDeltaBytes": 33554432,
            "ActivityPersistIntervalSeconds": 3600,
            "MaxSpaces": 64,
            "MaxMachinesPerSpace": 128,
            "MaxMachinesTotal": 512,
            "MaxStorageBytes": 2147483648,
            "MaxStorageBytesPerMachine": 67108864,
            "RetentionDays": 90,
            "MaintenanceIntervalSeconds": 300,
            "MaxConcurrentRequests": 4,
            "RequestBacklog": 64,
            "RequestTimeoutSeconds": 30,
            "AutomaticUpdatesEnabled": True,
            "MaxRequestsPerMinutePerClient": 0,
            "MaxCommandBytes": 65536,
            "MaxCommandsPerMachine": 32,
            "MaxCommandBytesPerMachine": 1048576,
        }
    if host is not None:
        value["Host"] = host
    if port is not None:
        if port < 1024 or port > 65535:
            raise ControlError("Port must be between 1024 and 65535.")
        value["Port"] = port
    if public_url is not None:
        value["PublicUrl"] = public_url
    value.setdefault("AutomaticUpdatesEnabled", True)
    configured_public_url = str(value.get("PublicUrl", "")).strip()
    if configured_public_url:
        value["PublicUrl"] = normalize_public_url(configured_public_url)
    if str(value.get("Host", "")).strip() in ("", "0.0.0.0", "::", "[::]") and not configured_public_url:
        raise ControlError("A root --public-url is required when listening on a wildcard address.")
    temp = layout.config.with_name(layout.config.name + ".new-" + secrets.token_hex(8))
    temp.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    if os.name != "nt":
        temp.chmod(0o640)
        shutil.chown(str(temp), user="root", group="sensor-readout")
    os.replace(str(temp), str(layout.config))


def write_config(layout: Layout, value: Dict[str, Any]) -> None:
    secure_config_directory(layout.config.parent)
    host = str(value.get("Host", "")).strip()
    try:
        port = int(value.get("Port", 0))
        request_timeout = int(value.get("RequestTimeoutSeconds", 0))
        retention_days = int(value.get("RetentionDays", -1))
    except (TypeError, ValueError):
        raise ControlError("Port, request timeout, and retention must be whole numbers.")
    if not host:
        raise ControlError("The listening address cannot be empty.")
    if not 1024 <= port <= 65535:
        raise ControlError("Port must be between 1024 and 65535.")
    if not 5 <= request_timeout <= 300:
        raise ControlError("Request timeout must be between 5 and 300 seconds.")
    if not 0 <= retention_days <= 3650:
        raise ControlError("Retention must be between 0 and 3650 days.")
    public_url = str(value.get("PublicUrl", "")).strip()
    if public_url:
        value["PublicUrl"] = normalize_public_url(public_url)
    if host in ("0.0.0.0", "::", "[::]") and not public_url:
        raise ControlError("A reachable client address is required when listening on a wildcard address.")
    token = str(value.get("AuthToken", ""))
    if len(token) < 32:
        raise ControlError("The server access token must contain at least 32 characters.")
    value["Port"] = port
    value["RequestTimeoutSeconds"] = request_timeout
    value["RetentionDays"] = retention_days
    value["AutomaticUpdatesEnabled"] = bool(value.get("AutomaticUpdatesEnabled", True))
    temp = layout.config.with_name(layout.config.name + ".new-" + secrets.token_hex(8))
    try:
        temp.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")
        if os.name != "nt":
            temp.chmod(0o640)
            shutil.chown(str(temp), user="root", group="sensor-readout")
        os.replace(str(temp), str(layout.config))
    finally:
        if temp.exists():
            temp.unlink()


def restore_config(layout: Layout, content: Optional[bytes]) -> None:
    if content is None:
        if layout.config.exists():
            layout.config.unlink()
        return
    ensure_parent_directory(layout.config)
    temp = layout.config.with_name(layout.config.name + ".restore-" + secrets.token_hex(8))
    try:
        temp.write_bytes(content)
        if os.name != "nt":
            temp.chmod(0o640)
            shutil.chown(str(temp), user="root", group="sensor-readout")
        os.replace(str(temp), str(layout.config))
    finally:
        if temp.exists():
            temp.unlink()


def restore_generated_file(path: Path, content: Optional[bytes]) -> None:
    if content is None:
        if path.exists() or path.is_symlink():
            path.unlink()
        return
    ensure_parent_directory(path)
    temp = path.with_name(path.name + ".restore-" + secrets.token_hex(8))
    try:
        temp.write_bytes(content)
        if os.name != "nt":
            temp.chmod(0o644)
        os.replace(str(temp), str(path))
    finally:
        if temp.exists():
            temp.unlink()


def copy_verified_release(package: Path, layout: Layout, manifest: Dict[str, Any]) -> Path:
    version = str(manifest["Version"])
    secure_directory(layout.releases, 0o755)
    destination = layout.releases / version
    if destination.exists():
        installed = verify_package(destination)
        if installed != manifest:
            raise ControlError("Version %s is already installed with different contents." % version)
        if os.name != "nt":
            destination.chmod(0o755)
            (destination / "sensor_readout_server.py").chmod(0o755)
            (destination / "sensor_readout_server_control.py").chmod(0o755)
        return destination
    stage = Path(tempfile.mkdtemp(prefix=".stage-%s-" % version, dir=str(layout.releases)))
    try:
        for name in sorted(set(PACKAGE_FILES) | {MANIFEST_NAME}):
            shutil.copy2(str(package / name), str(stage / name))
        if os.name != "nt":
            stage.chmod(0o755)
            (stage / "sensor_readout_server.py").chmod(0o755)
            (stage / "sensor_readout_server_control.py").chmod(0o755)
        verify_package(stage)
        os.replace(str(stage), str(destination))
    finally:
        if stage.exists():
            shutil.rmtree(stage, ignore_errors=True)
    return destination


def current_release(layout: Layout) -> Optional[Path]:
    if not layout.current.exists() and not layout.current.is_symlink():
        return None
    try:
        resolved = layout.current.resolve(strict=True)
    except OSError:
        return None
    try:
        resolved.relative_to(layout.releases)
    except ValueError:
        raise ControlError("Current release link points outside the managed releases folder.")
    return resolved


def activate_release(layout: Layout, release: Path) -> None:
    link = layout.install_root / (".current-" + secrets.token_hex(8))
    relative = os.path.relpath(str(release), str(layout.install_root))
    os.symlink(relative, str(link), target_is_directory=True)
    os.replace(str(link), str(layout.current))


def install_service_file(layout: Layout, release: Path) -> None:
    ensure_parent_directory(layout.service_file)
    service_text = (release / "sensor-readout-server.service.example").read_text(encoding="utf-8")
    service_text = service_text.replace("/opt/sensor-readout-server", str(layout.install_root))
    service_text = service_text.replace("/etc/sensor-readout-server/settings.json", str(layout.config))
    service_text = service_text.replace("/var/lib/sensor-readout-server", str(layout.data_root))
    service_text = service_text.replace("/var/log/sensor-readout-server", str(layout.log_root))
    temp = layout.service_file.with_name(layout.service_file.name + ".new")
    temp.write_text(service_text, encoding="utf-8")
    if os.name != "nt":
        temp.chmod(0o644)
    os.replace(str(temp), str(layout.service_file))


def install_update_service_files(layout: Layout) -> None:
    ensure_parent_directory(layout.update_service_file)
    control_command = str(layout.control_link)
    service_text = """[Unit]
Description=Update Sensor Readout Server from its dedicated release channel
Wants=network-online.target
After=network-online.target

[Service]
Type=oneshot
TimeoutStartSec=15min
TimeoutStopSec=2min
ExecStart=%s update-online --yes
""" % control_command
    timer_text = """[Unit]
Description=Check daily for Sensor Readout Server updates

[Timer]
OnBootSec=15min
OnUnitActiveSec=24h
RandomizedDelaySec=2h
Persistent=true
Unit=%s

[Install]
WantedBy=timers.target
""" % UPDATE_SERVICE_NAME
    for destination, content in (
        (layout.update_service_file, service_text),
        (layout.update_timer_file, timer_text),
    ):
        temp = destination.with_name(destination.name + ".new")
        temp.write_text(content, encoding="utf-8")
        if os.name != "nt":
            temp.chmod(0o644)
        os.replace(str(temp), str(destination))


def configure_automatic_updates(layout: Layout, enabled: bool, check: bool = True) -> None:
    if enabled:
        systemctl(layout, "enable", "--now", UPDATE_TIMER_NAME, check=check)
    else:
        systemctl(layout, "disable", "--now", UPDATE_TIMER_NAME, check=check)


def restore_automatic_update_files(
    layout: Layout,
    service_content: Optional[bytes],
    timer_content: Optional[bytes],
    enabled: bool,
) -> None:
    restore_generated_file(layout.update_service_file, service_content)
    restore_generated_file(layout.update_timer_file, timer_content)
    systemctl(layout, "daemon-reload", check=False)
    configure_automatic_updates(layout, enabled, check=False)


def update_status(layout: Layout) -> int:
    settings = read_config(layout)
    enabled = bool(settings.get("AutomaticUpdatesEnabled", True))
    print("Automatic server updates: %s" % ("enabled" if enabled else "disabled"))
    result = systemctl(layout, "status", UPDATE_TIMER_NAME, "--no-pager", check=False)
    if result.stdout:
        print(result.stdout.rstrip())
    if result.stderr:
        print(result.stderr.rstrip(), file=sys.stderr)
    return result.returncode


def read_config(layout: Layout) -> Dict[str, Any]:
    try:
        value = json.loads(layout.config.read_text(encoding="utf-8"))
    except (OSError, ValueError) as error:
        raise ControlError("Could not read server settings: %s" % error)
    if not isinstance(value, dict):
        raise ControlError("Server settings are not a JSON object.")
    return value


def local_health_url(layout: Layout) -> str:
    value = read_config(layout)
    host = str(value.get("Host", "127.0.0.1"))
    if host in ("0.0.0.0", ""):
        host = "127.0.0.1"
    elif host in ("::", "[::]"):
        host = "[::1]"
    elif ":" in host and not host.startswith("["):
        host = "[%s]" % host
    return "http://%s:%s/api/v1/health" % (host, int(value.get("Port", 48673)))


def health(layout: Layout, expected_version: Optional[str] = None) -> Dict[str, Any]:
    if layout.skip_health:
        return {"Name": "Sensor Readout Server", "Version": expected_version or "test", "ProtocolVersion": 1}
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    try:
        with opener.open(local_health_url(layout), timeout=10) as response:
            value = json.loads(response.read().decode("utf-8"))
    except Exception as error:
        raise ControlError("Server health check failed: %s" % error)
    if value.get("Name") != "Sensor Readout Server" or int(value.get("ProtocolVersion", 0)) != 1:
        raise ControlError("Server health response is not valid.")
    if expected_version is not None and value.get("Version") != expected_version:
        raise ControlError("Server health version is %s, expected %s." % (value.get("Version"), expected_version))
    return value


def wait_for_health(layout: Layout, expected_version: str, timeout_seconds: int = 30) -> Dict[str, Any]:
    deadline = time.monotonic() + timeout_seconds
    last_error: Optional[Exception] = None
    while True:
        try:
            return health(layout, expected_version)
        except (ControlError, OSError, ValueError) as error:
            last_error = error
            if time.monotonic() >= deadline:
                raise ControlError("Server did not become healthy within %s seconds: %s" % (timeout_seconds, last_error))
            time.sleep(1)


def version_key(version: str) -> Tuple[Any, ...]:
    match = re.fullmatch(r"([0-9]+)\.([0-9]+)\.([0-9]+)(?:[-+]([A-Za-z0-9.-]+))?", version)
    if not match:
        raise ControlError("Invalid installed release version: %s" % version)
    suffix = match.group(4)
    return (int(match.group(1)), int(match.group(2)), int(match.group(3)), suffix is None, suffix or "")


def cleanup_releases(layout: Layout, keep: int = 3) -> None:
    active = current_release(layout)
    releases: List[Tuple[Tuple[Any, ...], Path]] = []
    for item in layout.releases.iterdir() if layout.releases.exists() else []:
        if not item.is_dir() or item.is_symlink() or not VERSION_PATTERN.fullmatch(item.name):
            continue
        releases.append((version_key(item.name), item))
    retained = {path for _, path in sorted(releases, reverse=True)[:keep]}
    if active is not None:
        retained.add(active)
    for _, item in releases:
        if item not in retained:
            shutil.rmtree(item)


def install_control_link(layout: Layout) -> None:
    if not layout.control_link.parent.exists() and layout.test_mode:
        return
    ensure_parent_directory(layout.control_link)
    temp_link = layout.control_link.with_name(layout.control_link.name + ".new")
    try:
        if temp_link.exists() or temp_link.is_symlink():
            temp_link.unlink()
        os.symlink(str(layout.current / "sensor_readout_server_control.py"), str(temp_link))
        os.replace(str(temp_link), str(layout.control_link))
    finally:
        if temp_link.exists() or temp_link.is_symlink():
            temp_link.unlink()


def remove_managed_artifact(path: Path) -> None:
    if path.is_symlink() or path.is_file():
        path.unlink()
    elif path.exists():
        raise ControlError("Could not remove failed installation artifact because it is not a file: %s" % path)


def cleanup_failed_first_install(layout: Layout) -> None:
    systemctl(layout, "disable", "--now", SERVICE_NAME, check=False)
    remove_managed_artifact(layout.control_link)
    remove_managed_artifact(layout.service_file)
    remove_managed_artifact(layout.current)
    systemctl(layout, "daemon-reload", check=False)


@contextmanager
def deployment_sigterm_rollback() -> Iterable[None]:
    if not hasattr(signal, "SIGTERM") or threading.current_thread() is not threading.main_thread():
        yield
        return
    previous_handler = signal.getsignal(signal.SIGTERM)

    def terminate(_signum: int, _frame: Any) -> None:
        signal.signal(signal.SIGTERM, previous_handler)
        raise DeploymentTerminated("deployment received SIGTERM")

    signal.signal(signal.SIGTERM, terminate)
    try:
        yield
    finally:
        signal.signal(signal.SIGTERM, previous_handler)


def deploy(args: argparse.Namespace, layout: Layout) -> None:
    require_root(layout)
    require_supported_service_host(layout)
    package = Path(args.package).resolve()
    manifest = verify_package(package)
    ensure_service_account(layout)
    secure_directory(layout.install_root, 0o755)
    secure_directory(layout.data_root)
    secure_directory(layout.log_root)
    if not layout.test_mode and os.name != "nt":
        chown_service(layout.data_root)
        chown_service(layout.log_root)
    previous = current_release(layout)
    previous_config = layout.config.read_bytes() if layout.config.is_file() else None
    previous_settings = read_config(layout) if previous_config is not None else {}
    previous_update_service = layout.update_service_file.read_bytes() if layout.update_service_file.is_file() else None
    previous_update_timer = layout.update_timer_file.read_bytes() if layout.update_timer_file.is_file() else None
    previous_updates_enabled = bool(previous_settings.get("AutomaticUpdatesEnabled", False))
    with deployment_sigterm_rollback():
        try:
            create_or_update_config(layout, args.host, args.port, args.public_url)
            release = copy_verified_release(package, layout, manifest)
            activate_release(layout, release)
            install_service_file(layout, release)
            systemctl(layout, "daemon-reload")
            systemctl(layout, "enable", SERVICE_NAME)
            systemctl(layout, "restart", SERVICE_NAME)
            wait_for_health(layout, str(manifest["Version"]))
            install_control_link(layout)
            install_update_service_files(layout)
            systemctl(layout, "daemon-reload")
            configure_automatic_updates(layout, bool(read_config(layout).get("AutomaticUpdatesEnabled", True)))
        except (Exception, KeyboardInterrupt) as error:
            restore_config(layout, previous_config)
            restore_automatic_update_files(
                layout,
                previous_update_service,
                previous_update_timer,
                previous_updates_enabled,
            )
            if previous is not None and previous.exists():
                activate_release(layout, previous)
                install_service_file(layout, previous)
                systemctl(layout, "daemon-reload", check=False)
                systemctl(layout, "restart", SERVICE_NAME, check=False)
                recovery = "the previous release was restored"
            else:
                cleanup_failed_first_install(layout)
                recovery = "the failed first installation was disabled and its active artifacts were removed"
            raise ControlError("Deployment failed; %s: %s" % (recovery, error))
    cleanup_releases(layout)
    print("Sensor Readout Server %s is active and healthy." % manifest["Version"])


def installed_server_version(layout: Layout) -> str:
    release = current_release(layout)
    if release is None:
        raise ControlError("Sensor Readout Server is not installed.")
    return str(verify_package(release)["Version"])


def check_online_update(layout: Layout, api_url: str = RELEASE_API) -> Optional[Tuple[str, Dict[str, Any]]]:
    current_version = installed_server_version(layout)
    update = find_server_update(
        read_releases(api_url, layout.test_mode), current_version, layout.test_mode
    )
    if update is None:
        print("Sensor Readout Server %s is up to date." % current_version)
    else:
        print("Sensor Readout Server %s is available; installed version: %s." % (update[0], current_version))
    return update


def install_online_update(args: argparse.Namespace, layout: Layout) -> None:
    require_root(layout)
    update = check_online_update(layout, args.release_api_url)
    if update is None:
        return
    version, asset = update
    if not args.yes:
        if not interactive_terminal():
            raise ControlError("Use --yes for an unattended server update.")
        if not prompt_yes_no("Download, verify, and install Sensor Readout Server %s" % version, True):
            raise ControlError("Server update cancelled; no changes were made.")
    with tempfile.TemporaryDirectory(prefix="sensor-readout-server-update-") as temporary:
        root = Path(temporary)
        archive = root / ("SensorReadout-Server-%s.zip" % version)
        extracted = root / "extracted"
        extracted.mkdir()
        download_server_asset(asset, archive, layout.test_mode)
        safe_extract_server_archive(archive, extracted)
        package = locate_server_package(extracted, version)
        deploy(
            argparse.Namespace(package=str(package), host=None, port=None, public_url=None),
            layout,
        )


def apply_setup(args: argparse.Namespace, layout: Layout) -> bool:
    require_root(layout)
    require_supported_service_host(layout)
    release = current_release(layout)
    if release is None:
        raise ControlError("Sensor Readout Server is not installed.")
    manifest = verify_package(release)
    previous_content = layout.config.read_bytes()
    previous = read_config(layout)
    previous_update_service = layout.update_service_file.read_bytes() if layout.update_service_file.is_file() else None
    previous_update_timer = layout.update_timer_file.read_bytes() if layout.update_timer_file.is_file() else None
    updated = dict(previous)
    for key, argument in (
        ("Host", args.host),
        ("Port", args.port),
        ("PublicUrl", args.public_url),
        ("RequestTimeoutSeconds", args.request_timeout),
        ("RetentionDays", args.retention_days),
        ("AutomaticUpdatesEnabled", args.automatic_updates),
    ):
        if argument is not None:
            updated[key] = argument
    if args.regenerate_token:
        updated["AuthToken"] = secrets.token_urlsafe(48)
    connection_changed = (
        str(updated.get("PublicUrl", "")) != str(previous.get("PublicUrl", ""))
        or str(updated.get("AuthToken", "")) != str(previous.get("AuthToken", ""))
    )
    try:
        write_config(layout, updated)
        install_service_file(layout, release)
        install_update_service_files(layout)
        systemctl(layout, "daemon-reload")
        configure_automatic_updates(layout, bool(updated.get("AutomaticUpdatesEnabled", True)))
        systemctl(layout, "restart", SERVICE_NAME)
        wait_for_health(layout, str(manifest["Version"]))
    except (Exception, KeyboardInterrupt) as error:
        restore_config(layout, previous_content)
        restore_automatic_update_files(
            layout,
            previous_update_service,
            previous_update_timer,
            bool(previous.get("AutomaticUpdatesEnabled", True)),
        )
        systemctl(layout, "restart", SERVICE_NAME, check=False)
        raise ControlError("Setup failed and the previous settings were restored: %s" % error)
    print("Sensor Readout Server settings were applied and the service is healthy.")
    return connection_changed


def set_automatic_updates(layout: Layout, enabled: bool) -> None:
    require_root(layout)
    previous_content = layout.config.read_bytes()
    settings = read_config(layout)
    previous_enabled = bool(settings.get("AutomaticUpdatesEnabled", True))
    previous_update_service = layout.update_service_file.read_bytes() if layout.update_service_file.is_file() else None
    previous_update_timer = layout.update_timer_file.read_bytes() if layout.update_timer_file.is_file() else None
    settings["AutomaticUpdatesEnabled"] = enabled
    try:
        write_config(layout, settings)
        install_update_service_files(layout)
        systemctl(layout, "daemon-reload")
        configure_automatic_updates(layout, enabled)
    except (Exception, KeyboardInterrupt) as error:
        restore_config(layout, previous_content)
        restore_automatic_update_files(
            layout,
            previous_update_service,
            previous_update_timer,
            previous_enabled,
        )
        raise ControlError("Could not change automatic server updates; the prior setting was restored: %s" % error)
    print("Automatic Sensor Readout Server updates are %s." % ("enabled" if enabled else "disabled"))


def rollback(args: argparse.Namespace, layout: Layout) -> None:
    require_root(layout)
    active = current_release(layout)
    candidates = []
    for item in layout.releases.iterdir() if layout.releases.exists() else []:
        if item.is_dir() and not item.is_symlink() and VERSION_PATTERN.fullmatch(item.name) and item != active:
            candidates.append(item)
    if args.version:
        target = layout.releases / args.version
        if target not in candidates:
            raise ControlError("Requested rollback version is not installed: %s" % args.version)
    else:
        if not candidates:
            raise ControlError("No previous release is available for rollback.")
        target = sorted(candidates, key=lambda item: version_key(item.name), reverse=True)[0]
    manifest = verify_package(target)
    previous = active
    try:
        activate_release(layout, target)
        install_service_file(layout, target)
        systemctl(layout, "daemon-reload")
        systemctl(layout, "restart", SERVICE_NAME)
        wait_for_health(layout, str(manifest["Version"]))
    except (Exception, KeyboardInterrupt) as error:
        if previous is not None:
            activate_release(layout, previous)
            install_service_file(layout, previous)
            systemctl(layout, "daemon-reload", check=False)
            systemctl(layout, "restart", SERVICE_NAME, check=False)
        raise ControlError("Rollback health check failed; the prior active release was restored: %s" % error)
    print("Rolled back to Sensor Readout Server %s." % manifest["Version"])


def service_command(command: str, layout: Layout) -> None:
    require_root(layout)
    systemctl(layout, command, SERVICE_NAME)
    print("%s: %s" % (SERVICE_NAME, command))


def write_connection(args: argparse.Namespace, layout: Layout) -> None:
    output = write_connection_file(Path(args.output).resolve(), layout)
    if os.name != "nt":
        output.chmod(0o600)
    return_file_to_invoking_user(output)
    print("Connection file: %s" % output)


def write_connection_file(output: Path, layout: Layout) -> Path:
    release = current_release(layout)
    if release is None:
        raise ControlError("Sensor Readout Server is not installed.")
    command = [
        sys.executable,
        str(release / "sensor_readout_server.py"),
        "--config",
        str(layout.config),
        "--write-connection-info",
        str(output),
        "--connection-info-only",
    ]
    run(command)
    return output


def available_connection_path(folder: Path) -> Path:
    base = folder / "sensor-readout-connection.srconnection"
    if not base.exists():
        return base
    for number in range(2, 1000):
        candidate = folder / ("sensor-readout-connection-%s.srconnection" % number)
        if not candidate.exists():
            return candidate
    raise ControlError("Could not choose an unused connection filename in %s." % folder)


def return_file_to_invoking_user(path: Path) -> None:
    if os.name == "nt" or not hasattr(os, "chown"):
        return
    uid = os.environ.get("SUDO_UID", "")
    gid = os.environ.get("SUDO_GID", "")
    if uid.isdigit() and gid.isdigit():
        os.chown(str(path), int(uid), int(gid))


def write_guided_connection(layout: Layout) -> Path:
    output = available_connection_path(Path.cwd())
    write_connection_file(output, layout)
    if os.name != "nt":
        output.chmod(0o600)
    return_file_to_invoking_user(output)
    print("Connection file: %s" % output)
    print("Copy it only to computers or people allowed to use this relay; it contains the server access token.")
    return output


def print_guided_install_summary(layout: Layout) -> None:
    settings = read_config(layout)
    print("\nInstallation details")
    print("  Active release: %s" % layout.current)
    print("  HTML manual: %s" % (layout.current / "Manual.html"))
    print("  Service: %s" % SERVICE_NAME)
    print("  Control command: %s" % layout.control_link)
    print("  Settings: %s" % layout.config)
    print("  Encrypted data: %s" % settings.get("DataPath", layout.data_root / "Data"))
    print("  Rotating log: %s" % settings.get("LogPath", layout.log_root / "server.log"))
    print("  Client address: %s" % settings.get("PublicUrl", ""))
    print("  Automatic server updates: %s" % ("enabled" if settings.get("AutomaticUpdatesEnabled", True) else "disabled"))
    print("Use 'sudo sensor-readout-server-control status' to check the service later.")
    print("Use 'sudo sensor-readout-server-control setup' to review or change server settings later.")


def versions(layout: Layout) -> None:
    active = current_release(layout)
    for item in sorted(layout.releases.iterdir() if layout.releases.exists() else [], key=lambda path: path.name, reverse=True):
        if item.is_dir() and not item.is_symlink() and VERSION_PATTERN.fullmatch(item.name):
            print(("* " if item == active else "  ") + item.name)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Install, update, verify, and control Sensor Readout Server")
    subparsers = parser.add_subparsers(dest="command", required=True)
    verify = subparsers.add_parser("verify", help="verify package allow-list, version, and SHA-256 hashes")
    verify.add_argument("package")
    install_parser = subparsers.add_parser("install", help="install a verified package with automatic rollback")
    install_parser.add_argument("package", nargs="?", help="package folder; omit it to use the guided installer")
    install_parser.add_argument("--host")
    install_parser.add_argument("--port", type=int)
    install_parser.add_argument("--public-url")
    install_parser.add_argument("--non-interactive", action="store_true", help="never prompt; requires a package folder")
    for name in ("update",):
        deploy_parser = subparsers.add_parser(name, help=name + " a verified package with automatic rollback")
        deploy_parser.add_argument("package")
        deploy_parser.add_argument("--host")
        deploy_parser.add_argument("--port", type=int)
        deploy_parser.add_argument("--public-url")
    setup_parser = subparsers.add_parser("setup", help="review and change installed server settings safely")
    setup_parser.add_argument("--host")
    setup_parser.add_argument("--port", type=int)
    setup_parser.add_argument("--public-url")
    setup_parser.add_argument("--request-timeout", type=int)
    setup_parser.add_argument("--retention-days", type=int)
    setup_updates = setup_parser.add_mutually_exclusive_group()
    setup_updates.add_argument("--enable-auto-updates", dest="automatic_updates", action="store_true")
    setup_updates.add_argument("--disable-auto-updates", dest="automatic_updates", action="store_false")
    setup_parser.set_defaults(automatic_updates=None)
    setup_parser.add_argument("--regenerate-token", action="store_true")
    setup_parser.add_argument("--yes", action="store_true", help="apply supplied settings without prompting")
    setup_parser.add_argument("--write-connection", action="store_true", help=argparse.SUPPRESS)
    check_update = subparsers.add_parser("check-update", help="check the dedicated server release channel")
    check_update.add_argument("--release-api-url", default=RELEASE_API, help=argparse.SUPPRESS)
    online_update = subparsers.add_parser("update-online", help="download and install a verified server release")
    online_update.add_argument("--yes", action="store_true", help="install without confirmation")
    online_update.add_argument("--release-api-url", default=RELEASE_API, help=argparse.SUPPRESS)
    subparsers.add_parser("enable-auto-updates", help="enable the daily verified server update timer")
    subparsers.add_parser("disable-auto-updates", help="disable the daily verified server update timer")
    subparsers.add_parser("update-status", help="show the automatic server update setting and timer status")
    rollback_parser = subparsers.add_parser("rollback", help="activate a previous verified release")
    rollback_parser.add_argument("--version")
    subparsers.add_parser("status", help="show systemd status and run a health check")
    subparsers.add_parser("health", help="run the local server health check")
    subparsers.add_parser("start")
    subparsers.add_parser("stop")
    subparsers.add_parser("restart")
    subparsers.add_parser("versions", help="list installed releases; the active version is marked with an asterisk")
    connection = subparsers.add_parser("connection", help="write an importable connection file")
    connection.add_argument("output")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    layout = Layout()
    if args.command == "verify":
        manifest = verify_package(Path(args.package))
        print("Verified Sensor Readout Server %s." % manifest["Version"])
    elif args.command in ("install", "update"):
        guided = False
        if args.command == "install" and not args.package:
            if args.non_interactive:
                raise ControlError("--non-interactive installation requires a package folder.")
            configure_install_wizard(args)
            guided = True
        deploy(args, layout)
        if guided:
            try:
                write_guided_connection(layout)
            except (ControlError, OSError, ValueError, subprocess.CalledProcessError) as error:
                print("The server was installed, but its connection file could not be written: %s" % error, file=sys.stderr)
                print("Run 'sudo sensor-readout-server-control connection PATH' to create it later.", file=sys.stderr)
                return 2
            print_guided_install_summary(layout)
    elif args.command == "rollback":
        rollback(args, layout)
    elif args.command == "setup":
        supplied = any(
            value is not None
            for value in (args.host, args.port, args.public_url, args.request_timeout, args.retention_days, args.automatic_updates)
        ) or args.regenerate_token
        if not supplied:
            configure_setup_wizard(args, layout)
        elif not args.yes:
            raise ControlError("Use --yes with supplied setup options, or run setup without options for the guided wizard.")
        connection_changed = apply_setup(args, layout)
        if args.write_connection or connection_changed:
            try:
                write_guided_connection(layout)
            except (ControlError, OSError, ValueError, subprocess.CalledProcessError) as error:
                print("Settings were applied, but a replacement connection file could not be written: %s" % error, file=sys.stderr)
                return 2
    elif args.command == "check-update":
        check_online_update(layout, args.release_api_url)
    elif args.command == "update-online":
        install_online_update(args, layout)
    elif args.command == "enable-auto-updates":
        set_automatic_updates(layout, True)
    elif args.command == "disable-auto-updates":
        set_automatic_updates(layout, False)
    elif args.command == "update-status":
        return update_status(layout)
    elif args.command == "health":
        print(json.dumps(health(layout), sort_keys=True))
    elif args.command == "status":
        result = systemctl(layout, "status", SERVICE_NAME, "--no-pager", check=False)
        if result.stdout:
            print(result.stdout.rstrip())
        if result.stderr:
            print(result.stderr.rstrip(), file=sys.stderr)
        print(json.dumps(health(layout), sort_keys=True))
        if result.returncode != 0:
            return result.returncode
    elif args.command in ("start", "stop", "restart"):
        service_command(args.command, layout)
    elif args.command == "versions":
        versions(layout)
    elif args.command == "connection":
        write_connection(args, layout)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        print("\nSensor Readout Server control was interrupted. Check service status before retrying.", file=sys.stderr)
        raise SystemExit(130)
    except (ControlError, OSError, ValueError, subprocess.CalledProcessError) as error:
        print("Sensor Readout Server control failed: %s" % error, file=sys.stderr)
        raise SystemExit(1)
