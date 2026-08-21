import argparse
import base64
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import importlib.util
import json
import os
from pathlib import Path
import re
import signal
import tempfile
import threading
import unittest
import warnings
import zipfile
from unittest import mock


MODULE_PATH = Path(__file__).with_name("sensor_readout_server_control.py")
SPEC = importlib.util.spec_from_file_location("sensor_readout_server_control", MODULE_PATH)
CONTROL = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(CONTROL)
SOURCE_ROOT = Path(__file__).parent


class PackageControlTests(unittest.TestCase):
    TEST_RSA_MODULUS = 101087323656038509200231393179421221869307956598910430809090593673575120900081691287105367137239164583077834639174356064586546717428945228279511111750615651550494860408499527027908561118471437688795710459964546473327912885232493387132256128273283906442409072146490594680548246981833855089257313270113650919903
    TEST_RSA_PRIVATE_EXPONENT = 77836483416309432683389181592278150040296129115626547903003473283025181591785134922581109020042113040814452437503326824347023925440848117165402280120715269377602437466064158964845536459768480692489868718499132139517208784887667665166396503526622135952210333940917263842027450384711646134235358071698851757713

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.chown_patcher = mock.patch.object(CONTROL.shutil, "chown")
        self.chown_patcher.start()
        modulus = self.TEST_RSA_MODULUS.to_bytes((self.TEST_RSA_MODULUS.bit_length() + 7) // 8, "big")
        self.public_key_patcher = mock.patch.object(
            CONTROL,
            "MANIFEST_RSA_MODULUS_B64",
            base64.b64encode(modulus).decode("ascii"),
        )
        self.public_key_patcher.start()

    def tearDown(self):
        self.public_key_patcher.stop()
        self.chown_patcher.stop()
        self.temp.cleanup()

    def sign_manifest(self, manifest):
        digest_info = CONTROL.SHA256_DIGEST_INFO_PREFIX + hashlib.sha256(
            CONTROL.canonical_manifest_bytes(manifest)
        ).digest()
        key_bytes = (self.TEST_RSA_MODULUS.bit_length() + 7) // 8
        encoded = b"\x00\x01" + (b"\xff" * (key_bytes - len(digest_info) - 3)) + b"\x00" + digest_info
        signature = pow(
            int.from_bytes(encoded, "big"),
            self.TEST_RSA_PRIVATE_EXPONENT,
            self.TEST_RSA_MODULUS,
        ).to_bytes(key_bytes, "big")
        manifest["Signature"] = base64.b64encode(signature).decode("ascii")

    def make_package(self, version="6.1.0"):
        package = self.root / ("package-" + version)
        package.mkdir()
        for name in CONTROL.PACKAGE_FILES:
            if name == CONTROL.VERSION_NAME:
                (package / name).write_text(version + "\n", encoding="utf-8")
            else:
                (package / name).write_bytes((SOURCE_ROOT / name).read_bytes())
        files = {}
        for name in CONTROL.PACKAGE_FILES:
            files[name] = hashlib.sha256((package / name).read_bytes()).hexdigest().upper()
        manifest = {
            "Format": CONTROL.MANIFEST_FORMAT,
            "Component": CONTROL.MANIFEST_COMPONENT,
            "Version": version,
            "Algorithm": CONTROL.MANIFEST_ALGORITHM,
            "Files": files,
        }
        self.sign_manifest(manifest)
        (package / CONTROL.MANIFEST_NAME).write_text(
            json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8"
        )
        return package

    def layout(self):
        base = self.root / "layout"
        environment = {
            "SR_SERVER_TEST_MODE": "1",
            "SR_SERVER_INSTALL_ROOT": str(base / "install"),
            "SR_SERVER_CONFIG": str(base / "config" / "settings.json"),
            "SR_SERVER_DATA_ROOT": str(base / "data"),
            "SR_SERVER_LOG_ROOT": str(base / "logs"),
            "SR_SERVER_SERVICE_FILE": str(base / "systemd" / CONTROL.SERVICE_NAME),
            "SR_SERVER_UPDATE_SERVICE_FILE": str(base / "systemd" / CONTROL.UPDATE_SERVICE_NAME),
            "SR_SERVER_UPDATE_TIMER_FILE": str(base / "systemd" / CONTROL.UPDATE_TIMER_NAME),
            "SR_SERVER_CONTROL_LINK": str(base / "bin" / "sensor-readout-server-control"),
            "SR_SERVER_SYSTEMCTL": "",
        }
        with mock.patch.dict(os.environ, environment, clear=False):
            return CONTROL.Layout()

    def test_package_allow_list_version_and_hashes_are_verified(self):
        package = self.make_package()
        manifest = CONTROL.verify_package(package)
        self.assertEqual("6.1.0", manifest["Version"])
        (package / "unexpected.log").write_text("private data", encoding="utf-8")
        with self.assertRaises(CONTROL.ControlError):
            CONTROL.verify_package(package)

    def test_manifest_signature_is_required_and_strictly_encoded(self):
        package = self.make_package()
        manifest_path = package / CONTROL.MANIFEST_NAME
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        del manifest["Signature"]
        manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
        with self.assertRaisesRegex(CONTROL.ControlError, "signature is missing"):
            CONTROL.verify_package(package)

        manifest["Signature"] = "not base64!"
        manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
        with self.assertRaisesRegex(CONTROL.ControlError, "malformed"):
            CONTROL.verify_package(package)

    def test_manifest_canonical_bytes_follow_the_release_contract(self):
        manifest = {
            "Format": CONTROL.MANIFEST_FORMAT,
            "Component": CONTROL.MANIFEST_COMPONENT,
            "Version": "6.1.0",
            "Algorithm": CONTROL.MANIFEST_ALGORITHM,
            "Files": {"z-file": "a" * 64, "a-file": "b" * 64},
        }
        expected = (
            "SensorReadoutServerPackage\n"
            "LinuxServer\n"
            "6.1.0\n"
            "RSA-SHA256\n"
            "a-file\t" + ("B" * 64) + "\n"
            "z-file\t" + ("A" * 64) + "\n"
        ).encode("utf-8")
        self.assertEqual(expected, CONTROL.canonical_manifest_bytes(manifest))

    def test_manifest_signature_rejects_wrong_key_and_tampering(self):
        package = self.make_package()
        with mock.patch.object(CONTROL, "MANIFEST_RSA_MODULUS_B64", base64.b64encode(b"\xff" * 128).decode("ascii")):
            with self.assertRaisesRegex(CONTROL.ControlError, "signature verification failed"):
                CONTROL.verify_package(package)

        manifest_path = package / CONTROL.MANIFEST_NAME
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        manifest["Files"]["Manual.html"] = "0" * 64
        manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
        with self.assertRaisesRegex(CONTROL.ControlError, "signature verification failed"):
            CONTROL.verify_package(package)

    def test_tampering_and_version_mismatch_are_rejected(self):
        package = self.make_package()
        (package / "Manual.html").write_text("tampered", encoding="utf-8")
        with self.assertRaisesRegex(CONTROL.ControlError, "SHA-256"):
            CONTROL.verify_package(package)

        package = self.make_package("6.1.1")
        with self.assertRaisesRegex(CONTROL.ControlError, "Server source version"):
            CONTROL.verify_package(package)

    def test_configuration_preserves_token_and_normalizes_public_url(self):
        layout = self.layout()
        CONTROL.create_or_update_config(layout, "0.0.0.0", 48674, "https://sensors.example.test")
        first = json.loads(layout.config.read_text(encoding="utf-8"))
        CONTROL.create_or_update_config(layout, None, None, None)
        second = json.loads(layout.config.read_text(encoding="utf-8"))
        self.assertEqual(first["AuthToken"], second["AuthToken"])
        self.assertEqual("https://sensors.example.test/", second["PublicUrl"])
        self.assertEqual(48674, second["Port"])
        self.assertEqual(8388608, second["MaxDeltaBytesPerMachine"])
        self.assertEqual(33554432, second["MaxBufferedDeltaBytes"])
        self.assertEqual(4, second["MaxConcurrentRequests"])
        CONTROL.create_or_update_config(layout, None, None, "https://sensors.example.test/srrelay")
        prefixed = json.loads(layout.config.read_text(encoding="utf-8"))
        self.assertEqual("https://sensors.example.test/srrelay/", prefixed["PublicUrl"])
        with self.assertRaises(CONTROL.ControlError):
            CONTROL.create_or_update_config(layout, None, None, "https://sensors.example.test/base/../private")

    def test_public_url_rejects_loopback_and_wildcard_hosts(self):
        for public_url in (
            "http://127.0.0.1:48673/",
            "https://localhost/",
            "http://[::1]:48673/",
            "http://0.0.0.0:48673/",
            "http://[::]:48673/",
        ):
            with self.subTest(public_url=public_url), self.assertRaises(CONTROL.ControlError):
                CONTROL.normalize_public_url(public_url)

    def test_wildcard_install_requires_public_url(self):
        layout = self.layout()
        with self.assertRaisesRegex(CONTROL.ControlError, "public-url"):
            CONTROL.create_or_update_config(layout, "0.0.0.0", None, None)

    def test_ipv6_wildcard_health_uses_loopback(self):
        layout = self.layout()
        layout.config.parent.mkdir(parents=True)
        layout.config.write_text(json.dumps({"Host": "::", "Port": 48673}), encoding="utf-8")
        self.assertEqual("http://[::1]:48673/api/v1/health", CONTROL.local_health_url(layout))

    def test_failed_deploy_restores_previous_release(self):
        layout = self.layout()
        package = self.make_package()
        release = self.root / "release-6.1.0"
        previous = self.root / "release-5.9.0"
        release.mkdir()
        previous.mkdir()
        layout.config.parent.mkdir(parents=True)
        original_config = json.dumps({"Host": "127.0.0.1", "Port": 48673, "PublicUrl": "", "AuthToken": "a" * 64}).encode("utf-8")
        layout.config.write_bytes(original_config)
        args = argparse.Namespace(
            package=str(package), host="0.0.0.0", port=None, public_url="https://sensors.example.test/"
        )
        with mock.patch.object(CONTROL, "ensure_service_account"), \
             mock.patch.object(CONTROL, "secure_directory"), \
             mock.patch.object(CONTROL, "copy_verified_release", return_value=release), \
             mock.patch.object(CONTROL, "current_release", return_value=previous), \
             mock.patch.object(CONTROL, "activate_release") as activate, \
             mock.patch.object(CONTROL, "install_service_file"), \
             mock.patch.object(CONTROL, "systemctl"), \
             mock.patch.object(CONTROL, "wait_for_health", side_effect=CONTROL.ControlError("unhealthy")):
            with self.assertRaisesRegex(CONTROL.ControlError, "previous release was restored"):
                CONTROL.deploy(args, layout)
        self.assertEqual([mock.call(layout, release), mock.call(layout, previous)], activate.call_args_list)
        self.assertEqual(original_config, layout.config.read_bytes())

    def test_failed_first_install_disables_and_removes_active_artifacts(self):
        layout = self.layout()
        package = self.make_package()
        release = self.root / "first-release"
        release.mkdir()
        layout.current.parent.mkdir(parents=True)
        layout.service_file.parent.mkdir(parents=True)
        layout.control_link.parent.mkdir(parents=True)
        args = argparse.Namespace(package=str(package), host=None, port=None, public_url=None)

        def create_current(_layout, _release):
            layout.current.write_text("failed current", encoding="utf-8")

        def create_service(_layout, _release):
            layout.service_file.write_text("failed service", encoding="utf-8")

        def create_control(_layout):
            layout.control_link.write_text("failed control", encoding="utf-8")

        with mock.patch.object(CONTROL, "ensure_service_account"), \
             mock.patch.object(CONTROL, "secure_directory"), \
             mock.patch.object(CONTROL, "create_or_update_config"), \
             mock.patch.object(CONTROL, "copy_verified_release", return_value=release), \
             mock.patch.object(CONTROL, "current_release", return_value=None), \
             mock.patch.object(CONTROL, "activate_release", side_effect=create_current), \
             mock.patch.object(CONTROL, "install_service_file", side_effect=create_service), \
             mock.patch.object(CONTROL, "install_control_link", side_effect=create_control), \
             mock.patch.object(CONTROL, "install_update_service_files", side_effect=CONTROL.ControlError("failed")), \
             mock.patch.object(CONTROL, "systemctl") as systemctl:
            with self.assertRaisesRegex(CONTROL.ControlError, "failed first installation was disabled"):
                CONTROL.deploy(args, layout)

        self.assertFalse(layout.current.exists())
        self.assertFalse(layout.service_file.exists())
        self.assertFalse(layout.control_link.exists())
        self.assertIn(
            mock.call(layout, "disable", "--now", CONTROL.SERVICE_NAME, check=False),
            systemctl.call_args_list,
        )

    def test_failed_rollback_restores_active_release(self):
        layout = self.layout()
        layout.releases.mkdir(parents=True)
        active = layout.releases / "6.1.0"
        target = layout.releases / "5.9.0"
        active.mkdir()
        target.mkdir()
        args = argparse.Namespace(version=None)
        with mock.patch.object(CONTROL, "current_release", return_value=active), \
             mock.patch.object(CONTROL, "verify_package", return_value={"Version": "5.9.0"}), \
             mock.patch.object(CONTROL, "activate_release") as activate, \
             mock.patch.object(CONTROL, "install_service_file"), \
             mock.patch.object(CONTROL, "systemctl"), \
             mock.patch.object(CONTROL, "wait_for_health", side_effect=CONTROL.ControlError("unhealthy")):
            with self.assertRaisesRegex(CONTROL.ControlError, "prior active release was restored"):
                CONTROL.rollback(args, layout)
        self.assertEqual([mock.call(layout, target), mock.call(layout, active)], activate.call_args_list)

    def test_systemd_template_uses_the_atomic_current_release(self):
        service = (SOURCE_ROOT / "sensor-readout-server.service.example").read_text(encoding="utf-8")
        self.assertIn("WorkingDirectory=/opt/sensor-readout-server/current", service)
        self.assertIn("/opt/sensor-readout-server/current/sensor_readout_server.py", service)

    def test_config_directory_is_group_accessible_to_service_account(self):
        path = self.root / "config"
        with mock.patch.object(CONTROL.os, "name", "posix"), \
             mock.patch.object(Path, "chmod") as chmod, \
             mock.patch.object(CONTROL.shutil, "chown") as chown:
            CONTROL.secure_config_directory(path)
        chmod.assert_called_once_with(0o750)
        chown.assert_called_once_with(str(path), user="root", group="sensor-readout")

    def test_version_sorting_prefers_stable_release(self):
        versions = ["6.1.0-beta.2", "5.9.9", "6.1.0"]
        self.assertEqual("6.1.0", sorted(versions, key=CONTROL.version_key, reverse=True)[0])

    def test_server_update_channel_ignores_client_draft_and_prerelease_releases(self):
        releases = [
            {
                "tag_name": "v99.0.0",
                "assets": [{"name": "SensorReadout-Server-99.0.0.zip", "browser_download_url": "https://example.test/client.zip"}],
            },
            {
                "tag_name": "server-v6.1.1",
                "draft": True,
                "assets": [{"name": "SensorReadout-Server-6.1.1.zip", "browser_download_url": "https://example.test/draft.zip"}],
            },
            {
                "tag_name": "server-v6.1.2",
                "prerelease": True,
                "assets": [{"name": "SensorReadout-Server-6.1.2.zip", "browser_download_url": "https://example.test/preview.zip"}],
            },
        ]
        self.assertIsNone(CONTROL.find_server_update(releases, "6.1.0"))

    def test_server_update_requires_exact_single_server_asset(self):
        release = {
            "tag_name": "server-v6.1.1",
            "assets": [
                {"name": "SensorReadout-6.1.1.zip", "browser_download_url": "https://example.test/client.zip"},
                {"name": "SensorReadout-Server-6.1.1.zip", "browser_download_url": "https://example.test/server.zip"},
            ],
        }
        version, asset = CONTROL.find_server_update([release], "6.1.0")
        self.assertEqual("6.1.1", version)
        self.assertEqual("SensorReadout-Server-6.1.1.zip", asset["name"])
        release["assets"].append(
            {"name": "sensorreadout-server-6.1.1.ZIP", "browser_download_url": "https://example.test/duplicate.zip"}
        )
        with self.assertRaisesRegex(CONTROL.ControlError, "missing or duplicated"):
            CONTROL.find_server_update([release], "6.1.0")

    def test_server_update_digest_and_archive_paths_fail_closed(self):
        digest = "a" * 64
        with self.assertRaisesRegex(CONTROL.ControlError, "did not provide"):
            CONTROL.verify_release_digest("", digest)
        with self.assertRaisesRegex(CONTROL.ControlError, "failed its SHA-256"):
            CONTROL.verify_release_digest("sha256:" + "b" * 64, digest)
        CONTROL.verify_release_digest("sha256:" + digest, digest)

        archive = self.root / "unsafe.zip"
        with zipfile.ZipFile(archive, "w") as output:
            output.writestr("../outside.txt", "unsafe")
        with self.assertRaisesRegex(CONTROL.ControlError, "unsafe entry"):
            CONTROL.safe_extract_server_archive(archive, self.root / "unsafe-output")

        duplicate = self.root / "duplicate.zip"
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", UserWarning)
            with zipfile.ZipFile(duplicate, "w") as output:
                output.writestr("VERSION", "6.1.0")
                output.writestr("VERSION", "6.1.0")
        with self.assertRaisesRegex(CONTROL.ControlError, "duplicated entry"):
            CONTROL.safe_extract_server_archive(duplicate, self.root / "duplicate-output")

    def test_online_update_downloads_verifies_and_stages_from_local_release_service(self):
        package = self.make_package()
        archive = self.root / "SensorReadout-Server-6.1.0.zip"
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as output:
            for item in package.iterdir():
                output.write(item, "SensorReadout-Server-6.1.0/" + item.name)
        archive_bytes = archive.read_bytes()
        archive_digest = hashlib.sha256(archive_bytes).hexdigest()

        class ReleaseHandler(BaseHTTPRequestHandler):
            release_payload = b"[]"

            def do_GET(self):
                if self.path == "/releases":
                    payload = self.release_payload
                    content_type = "application/json"
                elif self.path == "/SensorReadout-Server-6.1.0.zip":
                    payload = archive_bytes
                    content_type = "application/zip"
                else:
                    self.send_error(404)
                    return
                self.send_response(200)
                self.send_header("Content-Type", content_type)
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)

            def log_message(self, *_args):
                return

        server = ThreadingHTTPServer(("127.0.0.1", 0), ReleaseHandler)
        port = server.server_address[1]
        ReleaseHandler.release_payload = json.dumps(
            [
                {
                            "tag_name": "server-v6.1.0",
                    "draft": False,
                    "prerelease": False,
                    "assets": [
                        {
                            "name": "SensorReadout-Server-6.1.0.zip",
                            "browser_download_url": "http://127.0.0.1:%s/SensorReadout-Server-6.1.0.zip" % port,
                            "digest": "sha256:" + archive_digest,
                        }
                    ],
                }
            ]
        ).encode("utf-8")
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        staged_versions = []

        def verify_staged_deploy(args, _layout):
            staged_versions.append(CONTROL.verify_package(Path(args.package))["Version"])

        try:
            args = argparse.Namespace(
                yes=True, release_api_url="http://127.0.0.1:%s/releases" % port
            )
            with mock.patch.object(CONTROL, "installed_server_version", return_value="5.9.0"), \
                 mock.patch.object(CONTROL, "deploy", side_effect=verify_staged_deploy):
                CONTROL.install_online_update(args, self.layout())
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=5)
        self.assertEqual(["6.1.0"], staged_versions)

    def test_loopback_http_update_service_is_test_mode_only(self):
        url = "http://127.0.0.1:48673/releases"
        self.assertFalse(CONTROL.allowed_update_url(url))
        self.assertTrue(CONTROL.allowed_update_url(url, True))

    def test_update_timer_is_generated_for_the_dedicated_server_channel(self):
        layout = self.layout()
        CONTROL.install_update_service_files(layout)
        service = layout.update_service_file.read_text(encoding="utf-8")
        timer = layout.update_timer_file.read_text(encoding="utf-8")
        self.assertIn("update-online --yes", service)
        self.assertIn(str(layout.control_link), service)
        self.assertIn("TimeoutStartSec=15min", service)
        self.assertIn("TimeoutStopSec=2min", service)
        self.assertIn("OnUnitActiveSec=24h", timer)
        self.assertIn("RandomizedDelaySec=2h", timer)

    def test_failed_automatic_update_change_restores_settings_and_units(self):
        layout = self.layout()
        CONTROL.create_or_update_config(layout, "0.0.0.0", 48673, "http://192.168.1.20:48673/")
        original_config = layout.config.read_bytes()
        layout.update_service_file.parent.mkdir(parents=True, exist_ok=True)
        layout.update_service_file.write_text("old update service\n", encoding="utf-8")
        layout.update_timer_file.write_text("old update timer\n", encoding="utf-8")
        with mock.patch.object(CONTROL, "configure_automatic_updates", side_effect=[CONTROL.ControlError("failed"), None]):
            with self.assertRaisesRegex(CONTROL.ControlError, "prior setting was restored"):
                CONTROL.set_automatic_updates(layout, False)
        self.assertEqual(original_config, layout.config.read_bytes())
        self.assertEqual("old update service\n", layout.update_service_file.read_text(encoding="utf-8"))
        self.assertEqual("old update timer\n", layout.update_timer_file.read_text(encoding="utf-8"))

    def test_setup_failure_restores_configuration(self):
        layout = self.layout()
        package = self.make_package()
        layout.releases.mkdir(parents=True)
        release = layout.releases / "6.1.0"
        package.rename(release)
        CONTROL.create_or_update_config(layout, "0.0.0.0", 48673, "http://192.168.1.20:48673/")
        original = layout.config.read_bytes()
        args = argparse.Namespace(
            host="0.0.0.0",
            port=48674,
            public_url="http://192.168.1.20:48674/",
            request_timeout=45,
            retention_days=120,
            automatic_updates=False,
            regenerate_token=True,
        )
        with mock.patch.object(CONTROL, "current_release", return_value=release), \
             mock.patch.object(CONTROL, "install_service_file"), \
             mock.patch.object(CONTROL, "install_update_service_files"), \
             mock.patch.object(CONTROL, "systemctl"), \
             mock.patch.object(CONTROL, "wait_for_health", side_effect=CONTROL.ControlError("unhealthy")):
            with self.assertRaisesRegex(CONTROL.ControlError, "previous settings were restored"):
                CONTROL.apply_setup(args, layout)
        self.assertEqual(original, layout.config.read_bytes())

    def test_health_wait_retries_transient_startup_failure(self):
        layout = self.layout()
        response = {"Name": "Sensor Readout Server", "Version": "6.1.0", "ProtocolVersion": 1}
        with mock.patch.object(CONTROL, "health", side_effect=[CONTROL.ControlError("starting"), response]) as health, \
             mock.patch.object(CONTROL.time, "sleep") as sleep:
            self.assertEqual(response, CONTROL.wait_for_health(layout, "6.1.0", timeout_seconds=5))
        self.assertEqual(2, health.call_count)
        sleep.assert_called_once_with(1)

    def test_install_package_is_optional_only_for_guided_flow(self):
        with mock.patch.object(CONTROL.sys, "argv", ["control", "install"]):
            args = CONTROL.parse_args()
        self.assertIsNone(args.package)
        self.assertFalse(args.non_interactive)
        with mock.patch.object(CONTROL.sys, "argv", ["control", "update"]):
            with self.assertRaises(SystemExit):
                CONTROL.parse_args()

    def test_guided_private_install_uses_script_package_and_detected_address(self):
        package = self.make_package()
        args = argparse.Namespace(package=None, host=None, port=None, public_url=None, non_interactive=False)
        answers = iter(["", "48674", "", ""])
        with mock.patch.object(CONTROL, "__file__", str(package / "sensor_readout_server_control.py")), \
             mock.patch.object(CONTROL, "interactive_terminal", return_value=True), \
             mock.patch.object(CONTROL, "detected_reachable_addresses", return_value=["100.64.1.20"]), \
             mock.patch("builtins.input", side_effect=lambda prompt="": next(answers)):
            result = CONTROL.configure_install_wizard(args)
        self.assertEqual(str(package), result.package)
        self.assertEqual("0.0.0.0", result.host)
        self.assertEqual(48674, result.port)
        self.assertEqual("http://100.64.1.20:48674/", result.public_url)
        self.assertTrue(result.guided)

    def test_guided_https_install_reprompts_until_https_is_supplied(self):
        package = self.make_package()
        args = argparse.Namespace(package=None, host=None, port=None, public_url=None, non_interactive=False)
        answers = iter(["2", "", "http://relay.example.invalid/", "https://relay.example.invalid/sr/", ""])
        with mock.patch.object(CONTROL, "__file__", str(package / "sensor_readout_server_control.py")), \
             mock.patch.object(CONTROL, "interactive_terminal", return_value=True), \
             mock.patch("builtins.input", side_effect=lambda prompt="": next(answers)):
            result = CONTROL.configure_install_wizard(args)
        self.assertEqual("127.0.0.1", result.host)
        self.assertEqual("https://relay.example.invalid/sr/", result.public_url)

    def test_guided_install_refuses_noninteractive_terminal(self):
        args = argparse.Namespace(package=None, host=None, port=None, public_url=None, non_interactive=False)
        with mock.patch.object(CONTROL, "interactive_terminal", return_value=False):
            with self.assertRaisesRegex(CONTROL.ControlError, "interactive terminal"):
                CONTROL.configure_install_wizard(args)

    def test_share_addresses_reject_loopback_and_format_ipv6(self):
        with self.assertRaises(CONTROL.ControlError):
            CONTROL.normalize_share_host("127.0.0.1")
        with self.assertRaises(CONTROL.ControlError):
            CONTROL.normalize_share_host("https://relay.example.invalid/")
        with self.assertRaises(CONTROL.ControlError):
            CONTROL.normalize_share_host("relay..example.invalid")
        self.assertEqual("http://[2001:db8::20]:48673/", CONTROL.public_url_for_host("2001:db8::20", 48673))

    def test_sigterm_during_deploy_restores_previous_release(self):
        layout = self.layout()
        package = self.make_package()
        release = self.root / "release-6.1.0-interrupted"
        previous = self.root / "release-5.9.0-interrupted"
        release.mkdir()
        previous.mkdir()
        layout.config.parent.mkdir(parents=True)
        original_config = json.dumps({"Host": "127.0.0.1", "Port": 48673, "AuthToken": "a" * 64}).encode("utf-8")
        layout.config.write_bytes(original_config)
        args = argparse.Namespace(
            package=str(package), host="0.0.0.0", port=48673, public_url="http://192.168.1.20:48673/"
        )

        def send_sigterm(*_args):
            handler = signal.getsignal(signal.SIGTERM)
            self.assertTrue(callable(handler))
            handler(signal.SIGTERM, None)

        with mock.patch.object(CONTROL, "ensure_service_account"), \
             mock.patch.object(CONTROL, "secure_directory"), \
             mock.patch.object(CONTROL, "copy_verified_release", return_value=release), \
             mock.patch.object(CONTROL, "current_release", return_value=previous), \
             mock.patch.object(CONTROL, "activate_release") as activate, \
             mock.patch.object(CONTROL, "install_service_file"), \
             mock.patch.object(CONTROL, "systemctl"), \
             mock.patch.object(CONTROL, "wait_for_health", side_effect=send_sigterm):
            with self.assertRaisesRegex(CONTROL.ControlError, "previous release was restored"):
                CONTROL.deploy(args, layout)
        self.assertEqual([mock.call(layout, release), mock.call(layout, previous)], activate.call_args_list)
        self.assertEqual(original_config, layout.config.read_bytes())

    def test_connection_output_avoids_overwrite_and_returns_to_sudo_user(self):
        first = self.root / "sensor-readout-connection.srconnection"
        first.write_text("existing", encoding="utf-8")
        self.assertEqual(
            self.root / "sensor-readout-connection-2.srconnection",
            CONTROL.available_connection_path(self.root),
        )
        target = self.root / "connection.srconnection"
        target.write_text("credential", encoding="utf-8")
        with mock.patch.dict(os.environ, {"SUDO_UID": "1000", "SUDO_GID": "1001"}, clear=False), \
             mock.patch.object(CONTROL.os, "name", "posix"), \
             mock.patch.object(CONTROL.os, "chown", create=True) as chown:
            CONTROL.return_file_to_invoking_user(target)
        chown.assert_called_once_with(str(target), 1000, 1001)

    def test_guided_install_cancellation_does_not_deploy(self):
        package = self.make_package()
        args = argparse.Namespace(package=None, host=None, port=None, public_url=None, non_interactive=False)
        answers = iter(["", "", "", "n"])
        with mock.patch.object(CONTROL, "__file__", str(package / "sensor_readout_server_control.py")), \
             mock.patch.object(CONTROL, "interactive_terminal", return_value=True), \
             mock.patch.object(CONTROL, "detected_reachable_addresses", return_value=["192.168.1.20"]), \
             mock.patch("builtins.input", side_effect=lambda prompt="": next(answers)):
            with self.assertRaisesRegex(CONTROL.ControlError, "cancelled"):
                CONTROL.configure_install_wizard(args)

    def test_explicit_install_does_not_enter_guided_flow(self):
        package = self.make_package()
        args = argparse.Namespace(
            command="install",
            package=str(package),
            host="0.0.0.0",
            port=48673,
            public_url="http://192.168.1.20:48673/",
            non_interactive=True,
        )
        with mock.patch.object(CONTROL, "parse_args", return_value=args), \
             mock.patch.object(CONTROL, "Layout"), \
             mock.patch.object(CONTROL, "configure_install_wizard") as wizard, \
             mock.patch.object(CONTROL, "deploy") as deploy, \
             mock.patch.object(CONTROL, "write_guided_connection") as connection:
            self.assertEqual(0, CONTROL.main())
        wizard.assert_not_called()
        deploy.assert_called_once()
        connection.assert_not_called()

    def test_html_manual_copy_buttons_have_matching_commands(self):
        manual = (SOURCE_ROOT / "Manual.html").read_text(encoding="utf-8")
        targets = re.findall(r'data-copy="([^"]+)"', manual)
        self.assertGreaterEqual(len(targets), 6)
        self.assertEqual(len(targets), len(set(targets)))
        for target in targets:
            self.assertRegex(manual, r'id="%s"' % re.escape(target))
        self.assertIn("sudo python3 sensor_readout_server_control.py install", manual)
        self.assertIn("sudo sensor-readout-server-control setup", manual)
        self.assertIn("server-vX.Y.Z", manual)
        self.assertIn("Ordinary Windows Sensor Readout releases and archives are explicitly ignored", manual)
        self.assertNotIn("Tailscale", manual)
        self.assertNotRegex(manual, r'data-copy="[^"]+"[^>]*>[^<]*</button>\s*<pre[^>]*><code>[^<]*example\.(?:com|invalid)')

    def test_guided_summary_reports_paths_without_credentials(self):
        layout = self.layout()
        layout.config.parent.mkdir(parents=True)
        layout.config.write_text(
            json.dumps(
                {
                    "PublicUrl": "http://192.168.1.20:48673/",
                    "DataPath": str(layout.data_root / "Data"),
                    "LogPath": str(layout.log_root / "server.log"),
                    "AuthToken": "secret-token-that-must-not-be-printed",
                }
            ),
            encoding="utf-8",
        )
        with mock.patch("builtins.print") as output:
            CONTROL.print_guided_install_summary(layout)
        text = "\n".join(" ".join(str(value) for value in call.args) for call in output.call_args_list)
        self.assertIn("Manual.html", text)
        self.assertIn("http://192.168.1.20:48673/", text)
        self.assertNotIn("secret-token", text)


if __name__ == "__main__":
    unittest.main()
