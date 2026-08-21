import base64
from concurrent.futures import ThreadPoolExecutor
import http.client
import importlib.util
import json
import os
from pathlib import Path
import socket
import tempfile
import threading
import time
import unittest
from unittest import mock


MODULE_PATH = Path(__file__).with_name("sensor_readout_server.py")
SPEC = importlib.util.spec_from_file_location("sensor_readout_server", MODULE_PATH)
SERVER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SERVER)


SPACE = "s" * 43
MACHINE = "m" * 43
TOKEN = "test-token"
MACHINE_TOKEN = "w" * 43


def direct_http_connection(host, port, timeout):
    connection = http.client.HTTPConnection(host, port, timeout=timeout)

    def create_connection(address, socket_timeout=None, source_address=None):
        family = socket.AF_INET6 if ":" in address[0] else socket.AF_INET
        client = socket.socket(family, socket.SOCK_STREAM)
        try:
            client.settimeout(socket_timeout)
            if source_address:
                client.bind(source_address)
            client.connect(address)
            return client
        except BaseException:
            client.close()
            raise

    connection._create_connection = create_connection
    return connection


def create_high_port_server(host, handler, settings):
    """Create a live server on an OS-selected high port, with a bounded fallback."""
    try:
        server = SERVER.ThreadingServer((host, 0), handler, settings)
        if server.server_address[1] >= 49152:
            return server
        server.server_close()
    except OSError:
        pass

    first_port = 49152 + (os.getpid() % 10000)
    for offset in range(4096):
        port = 49152 + ((first_port - 49152 + offset) % (65535 - 49152))
        try:
            return SERVER.ThreadingServer((host, port), handler, settings)
        except OSError:
            continue
    raise OSError("No available high loopback port was found for the server tests.")


class ServerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server_temp = tempfile.TemporaryDirectory()
        settings = {
            "DataPath": str(Path(cls.server_temp.name) / "Data"),
            "AuthToken": TOKEN,
            "MaxEnvelopeBytes": 1024 * 1024,
            "MaxDeltasPerMachine": 8,
        }
        # Live protocol tests use a fresh high IPv4 loopback port. Some Windows
        # installations extend the ephemeral range into low ports that local
        # security policy silently filters. Socket-family selection for IPv4
        # and IPv6 has separate deterministic coverage.
        cls.host = "127.0.0.1"
        cls.server = create_high_port_server(cls.host, SERVER.Handler, settings)
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()
        cls.port = cls.server.server_address[1]
        deadline = time.monotonic() + 3
        while True:
            try:
                connection = direct_http_connection(cls.host, cls.port, timeout=0.5)
                connection.request("GET", "/api/v1/health")
                response = connection.getresponse()
                response.read()
                connection.close()
                if response.status == 200:
                    break
            except OSError:
                if time.monotonic() >= deadline:
                    raise
                time.sleep(0.05)

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join(timeout=3)
        cls.server_temp.cleanup()

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        settings = {
            "DataPath": str(Path(self.temp.name) / "Data"),
            "AuthToken": TOKEN,
            "MaxEnvelopeBytes": 1024 * 1024,
            "MaxDeltasPerMachine": 8,
        }
        self.server.settings = settings
        self.server.store = SERVER.StateStore(settings)
        self.port = type(self).port

    def tearDown(self):
        self.temp.cleanup()

    def request(self, method, path, body=b"", authorized=True, machine_authorized=True, machine_token=None):
        connection = direct_http_connection(type(self).host, self.port, timeout=3)
        headers = {"Content-Length": str(len(body))}
        if authorized:
            headers["Authorization"] = "Bearer " + TOKEN
        if machine_authorized:
            headers["X-SR-Machine-Token"] = machine_token or MACHINE_TOKEN
        connection.request(method, path, body=body, headers=headers)
        response = connection.getresponse()
        data = response.read()
        result = response.status, dict(response.getheaders()), data
        connection.close()
        return result

    def test_health_does_not_require_authentication(self):
        status, _, data = self.request("GET", "/api/v1/health", authorized=False)
        self.assertEqual(200, status)
        health = json.loads(data)
        self.assertEqual(1, health["ProtocolVersion"])
        self.assertEqual(SERVER.SERVER_VERSION, health["Version"])

    def test_data_routes_require_token(self):
        status, _, _ = self.request("GET", f"/api/v1/spaces/{SPACE}/machines", authorized=False)
        self.assertEqual(401, status)

    def test_snapshot_delta_heartbeat_and_index_round_trip(self):
        snapshot = b"opaque-snapshot"
        status, _, _ = self.request("PUT", f"/api/v1/spaces/{SPACE}/machines/{MACHINE}/snapshot?sequence=1", snapshot)
        self.assertEqual(200, status)

        delta = b"opaque-delta"
        status, _, _ = self.request("POST", f"/api/v1/spaces/{SPACE}/machines/{MACHINE}/deltas?sequence=2", delta)
        self.assertEqual(200, status)

        status, headers, data = self.request("GET", f"/api/v1/spaces/{SPACE}/machines/{MACHINE}/snapshot")
        self.assertEqual(200, status)
        self.assertEqual(snapshot, data)
        self.assertEqual("1", headers["X-SR-Snapshot-Sequence"])
        self.assertEqual("2", headers["X-SR-Latest-Sequence"])

        status, _, data = self.request("GET", f"/api/v1/spaces/{SPACE}/machines/{MACHINE}/deltas?after=1")
        self.assertEqual(200, status)
        decoded = json.loads(data)
        self.assertEqual(2, decoded["LatestSequence"])
        self.assertEqual(delta, base64.b64decode(decoded["Deltas"][0]["Payload"]))

        status, _, _ = self.request("POST", f"/api/v1/spaces/{SPACE}/machines/{MACHINE}/heartbeat")
        self.assertEqual(200, status)
        status, _, data = self.request("GET", f"/api/v1/spaces/{SPACE}/machines")
        self.assertEqual(200, status)
        machines = json.loads(data)["Machines"]
        self.assertEqual(MACHINE, machines[0]["MachineId"])
        self.assertGreater(machines[0]["LastSeenUnixMs"], 0)

    def test_out_of_order_delta_requires_resync(self):
        self.request("PUT", f"/api/v1/spaces/{SPACE}/machines/{MACHINE}/snapshot?sequence=4", b"snapshot")
        status, headers, _ = self.request("POST", f"/api/v1/spaces/{SPACE}/machines/{MACHINE}/deltas?sequence=6", b"delta")
        self.assertEqual(409, status)
        self.assertEqual("4", headers["X-SR-Latest-Sequence"])

    def test_old_delta_reader_is_told_to_fetch_snapshot(self):
        self.request("PUT", f"/api/v1/spaces/{SPACE}/machines/{MACHINE}/snapshot?sequence=10", b"snapshot")
        status, _, _ = self.request("GET", f"/api/v1/spaces/{SPACE}/machines/{MACHINE}/deltas?after=4")
        self.assertEqual(428, status)

    def test_payload_limit_is_enforced_before_storage(self):
        body = b"x" * (1024 * 1024 + 1)
        status, _, _ = self.request("PUT", f"/api/v1/spaces/{SPACE}/machines/{MACHINE}/snapshot?sequence=1", body)
        self.assertEqual(413, status)

    def test_storage_limit_returns_insufficient_storage(self):
        settings = {
            "DataPath": str(Path(self.temp.name) / "LimitedData"),
            "MaxEnvelopeBytes": 64 * 1024,
            "MaxStorageBytesPerMachine": 64 * 1024,
            "MaxStorageBytes": 1024 * 1024,
        }
        self.server.store = SERVER.StateStore(settings)
        status, _, _ = self.request(
            "PUT",
            f"/api/v1/spaces/{SPACE}/machines/{MACHINE}/snapshot?sequence=1",
            b"x" * (50 * 1024),
        )
        self.assertEqual(507, status)

    def test_snapshot_compaction_removes_covered_deltas(self):
        self.request("PUT", f"/api/v1/spaces/{SPACE}/machines/{MACHINE}/snapshot?sequence=1", b"snapshot")
        self.request("POST", f"/api/v1/spaces/{SPACE}/machines/{MACHINE}/deltas?sequence=2", b"delta-2")
        self.request("POST", f"/api/v1/spaces/{SPACE}/machines/{MACHINE}/deltas?sequence=3", b"delta-3")
        self.request("PUT", f"/api/v1/spaces/{SPACE}/machines/{MACHINE}/snapshot?sequence=3", b"new-snapshot")
        status, _, data = self.request("GET", f"/api/v1/spaces/{SPACE}/machines/{MACHINE}/deltas?after=3")
        self.assertEqual(200, status)
        self.assertEqual([], json.loads(data)["Deltas"])

    def test_delta_limit_requires_a_compacted_snapshot(self):
        self.request("PUT", f"/api/v1/spaces/{SPACE}/machines/{MACHINE}/snapshot?sequence=1", b"snapshot")
        for sequence in range(2, 10):
            status, _, _ = self.request("POST", f"/api/v1/spaces/{SPACE}/machines/{MACHINE}/deltas?sequence={sequence}", b"delta")
            self.assertEqual(200, status)
        status, _, _ = self.request("POST", f"/api/v1/spaces/{SPACE}/machines/{MACHINE}/deltas?sequence=10", b"delta")
        self.assertEqual(428, status)
        status, _, _ = self.request("PUT", f"/api/v1/spaces/{SPACE}/machines/{MACHINE}/snapshot?sequence=10", b"compacted")
        self.assertEqual(200, status)

    def test_placeholder_and_short_access_tokens_are_rejected(self):
        with self.assertRaises(ValueError):
            SERVER.validate_auth_token("replace-with-a-random-server-token")
        with self.assertRaises(ValueError):
            SERVER.validate_auth_token("short")
        self.assertEqual("x" * 32, SERVER.validate_auth_token("x" * 32))

    def test_connection_file_can_be_written_outside_the_config_folder(self):
        output = Path(self.temp.name) / "export" / "office.srconnection"
        settings = {"Host": "10.0.0.8", "Port": 48673, "AuthToken": "x" * 32}
        path = SERVER.write_connection_info(Path(self.temp.name) / "settings.json", settings, output)
        document = json.loads(path.read_text(encoding="utf-8"))
        self.assertEqual(output, path)
        self.assertEqual("http://10.0.0.8:48673/", document["ServerUrl"])
        self.assertEqual("x" * 32, document["Token"])
        self.assertNotIn("Password", document)

    def test_loopback_listener_requires_an_advertised_address(self):
        for host in ("127.0.0.1", "::1", "localhost"):
            settings = {"Host": host, "Port": 48673, "AuthToken": "x" * 32}
            with self.subTest(host=host), self.assertRaises(ValueError):
                SERVER.write_connection_info(Path(self.temp.name) / "settings.json", settings)
        settings = {
            "Host": "127.0.0.1",
            "Port": 48673,
            "PublicUrl": "http://100.64.0.8:48673/",
            "AuthToken": "x" * 32,
        }
        path = SERVER.write_connection_info(Path(self.temp.name) / "settings.json", settings)
        self.assertEqual("http://100.64.0.8:48673/", json.loads(path.read_text(encoding="utf-8"))["ServerUrl"])

    def test_wildcard_listener_requires_an_advertised_public_url(self):
        settings = {"Host": "0.0.0.0", "Port": 48673, "AuthToken": "x" * 32}
        with self.assertRaises(ValueError):
            SERVER.write_connection_info(Path(self.temp.name) / "settings.json", settings)
        settings["PublicUrl"] = "https://sensors.example.test/"
        path = SERVER.write_connection_info(Path(self.temp.name) / "settings.json", settings)
        self.assertEqual("https://sensors.example.test/", json.loads(path.read_text(encoding="utf-8"))["ServerUrl"])

    def test_public_url_accepts_safe_prefix_and_rejects_unsafe_addresses(self):
        settings = {"Host": "0.0.0.0", "Port": 48673, "PublicUrl": "https://sensors.example.test/srrelay", "AuthToken": "x" * 32}
        path = SERVER.write_connection_info(Path(self.temp.name) / "settings.json", settings)
        self.assertEqual("https://sensors.example.test/srrelay/", json.loads(path.read_text(encoding="utf-8"))["ServerUrl"])
        for public_url in (
            "https://sensors.example.test/base/../private",
            "https://sensors.example.test/base%2Fprivate",
            "https://user:password@sensors.example.test/",
            "https://sensors.example.test/?token=x",
            "https://sensors.example.test/#fragment",
            "http://0.0.0.0:48673/",
            "http://127.0.0.1:48673/",
            "http://localhost:48673/",
            "https://sensors.example.test:99999/",
            "https://sensors.example.test:0/",
        ):
            settings = {"Host": "0.0.0.0", "Port": 48673, "PublicUrl": public_url, "AuthToken": "x" * 32}
            with self.subTest(public_url=public_url), self.assertRaises(ValueError):
                SERVER.write_connection_info(Path(self.temp.name) / "settings.json", settings)

    def test_opaque_command_round_trip_and_delete(self):
        command_id = "c" * 43
        command = b"opaque-encrypted-command"
        self.request("PUT", f"/api/v1/spaces/{SPACE}/machines/{MACHINE}/snapshot?sequence=1", b"snapshot")
        status, _, _ = self.request("POST", f"/api/v1/spaces/{SPACE}/machines/{MACHINE}/commands/{command_id}", command)
        self.assertEqual(201, status)
        status, _, data = self.request("GET", f"/api/v1/spaces/{SPACE}/machines/{MACHINE}/commands")
        self.assertEqual(200, status)
        commands = json.loads(data)["Commands"]
        self.assertEqual(command_id, commands[0]["CommandId"])
        self.assertEqual(command, base64.b64decode(commands[0]["Payload"]))
        status, _, _ = self.request("DELETE", f"/api/v1/spaces/{SPACE}/machines/{MACHINE}/commands/{command_id}")
        self.assertEqual(200, status)
        status, _, data = self.request("GET", f"/api/v1/spaces/{SPACE}/machines/{MACHINE}/commands")
        self.assertEqual([], json.loads(data)["Commands"])

    def test_machine_token_prevents_another_client_from_overwriting_data(self):
        status, _, _ = self.request("PUT", f"/api/v1/spaces/{SPACE}/machines/{MACHINE}/snapshot?sequence=1", b"snapshot")
        self.assertEqual(200, status)
        status, _, _ = self.request(
            "PUT",
            f"/api/v1/spaces/{SPACE}/machines/{MACHINE}/snapshot?sequence=2",
            b"replacement",
            machine_authorized=False,
        )
        self.assertEqual(403, status)

    def test_only_publishing_machine_token_can_remove_computer(self):
        self.request("PUT", f"/api/v1/spaces/{SPACE}/machines/{MACHINE}/snapshot?sequence=1", b"snapshot")
        status, _, _ = self.request(
            "DELETE",
            f"/api/v1/spaces/{SPACE}/machines/{MACHINE}",
            machine_authorized=False,
        )
        self.assertEqual(403, status)
        status, _, _ = self.request(
            "DELETE",
            f"/api/v1/spaces/{SPACE}/machines/{MACHINE}",
            machine_token="wrong-machine-token-000000000000000000000",
        )
        self.assertEqual(403, status)
        status, _, data = self.request("GET", f"/api/v1/spaces/{SPACE}/machines")
        self.assertEqual(MACHINE, json.loads(data)["Machines"][0]["MachineId"])
        status, _, _ = self.request("DELETE", f"/api/v1/spaces/{SPACE}/machines/{MACHINE}")
        self.assertEqual(200, status)
        status, _, data = self.request("GET", f"/api/v1/spaces/{SPACE}/machines")
        self.assertEqual([], json.loads(data)["Machines"])

    def test_access_log_redacts_opaque_identifiers(self):
        command_id = "c" * 43
        with self.assertLogs(level="INFO") as captured:
            self.request("GET", f"/api/v1/spaces/{SPACE}/machines/{MACHINE}/commands")
            self.request("DELETE", f"/api/v1/spaces/{SPACE}/machines/{MACHINE}/commands/{command_id}")
        output = "\n".join(captured.output)
        self.assertNotIn(SPACE, output)
        self.assertNotIn(MACHINE, output)
        self.assertNotIn(command_id, output)
        self.assertIn("<space>", output)
        self.assertIn("<machine>", output)
        self.assertIn("<command>", output)


class StateStoreLimitTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.store_index = 0

    def tearDown(self):
        self.temp.cleanup()

    def store(self, **overrides):
        self.store_index += 1
        settings = {
            "DataPath": str(Path(self.temp.name) / ("Data-%s" % self.store_index)),
            "MaxEnvelopeBytes": 64 * 1024,
            "MaxDeltasPerMachine": 8,
            "MaxDeltaBytesPerMachine": 128 * 1024,
            "MaxBufferedDeltaBytes": 1024 * 1024,
            "ActivityPersistIntervalSeconds": 3600,
            "MaxSpaces": 8,
            "MaxMachinesPerSpace": 8,
            "MaxMachinesTotal": 16,
            "MaxStorageBytes": 2 * 1024 * 1024,
            "MaxStorageBytesPerMachine": 128 * 1024,
            "RetentionDays": 90,
            "MaintenanceIntervalSeconds": 300,
            "MaxCommandBytes": 16 * 1024,
            "MaxCommandsPerMachine": 8,
            "MaxCommandBytesPerMachine": 64 * 1024,
        }
        settings.update(overrides)
        return SERVER.StateStore(settings)

    def test_space_machine_and_global_machine_limits(self):
        first_space = "a" * 43
        second_space = "b" * 43
        store = self.store(MaxSpaces=1, MaxMachinesPerSpace=2, MaxMachinesTotal=3)
        store.put_snapshot(first_space, "c" * 43, 1, b"one", "t" * 43)
        store.put_snapshot(first_space, "d" * 43, 1, b"two", "u" * 43)
        with self.assertRaises(SERVER.StorageLimitExceeded):
            store.put_snapshot(first_space, "e" * 43, 1, b"three", "v" * 43)
        with self.assertRaises(SERVER.StorageLimitExceeded):
            store.put_snapshot(second_space, "f" * 43, 1, b"four", "w" * 43)

        total_store = self.store(MaxSpaces=3, MaxMachinesPerSpace=1, MaxMachinesTotal=2)
        total_store.put_snapshot(first_space, "g" * 43, 1, b"one", "x" * 43)
        total_store.put_snapshot(second_space, "h" * 43, 1, b"two", "y" * 43)
        with self.assertRaises(SERVER.StorageLimitExceeded):
            total_store.put_snapshot("i" * 43, "j" * 43, 1, b"three", "z" * 43)

    def test_concurrent_registration_cannot_exceed_space_limit(self):
        store = self.store(MaxSpaces=2, MaxMachinesPerSpace=1, MaxMachinesTotal=2)
        space = "k" * 43

        def register(machine):
            try:
                store.put_snapshot(space, machine, 1, b"snapshot", machine)
                return "stored"
            except SERVER.StorageLimitExceeded:
                return "limited"

        with ThreadPoolExecutor(max_workers=2) as executor:
            outcomes = list(executor.map(register, ("l" * 43, "m" * 43)))
        self.assertEqual(["limited", "stored"], sorted(outcomes))
        self.assertEqual(1, len(store.list_machines(space)))

    def test_existing_machine_data_cannot_be_reowned_without_valid_metadata(self):
        for damage in ("missing", "corrupt"):
            with self.subTest(damage=damage):
                store = self.store()
                space = "a" * 42 + ("1" if damage == "missing" else "2")
                machine = "b" * 43
                original_token = "c" * 43
                store.put_snapshot(space, machine, 1, b"original", original_token)
                metadata_path = store.metadata_path(store.machine_dir(space, machine))
                if damage == "missing":
                    metadata_path.unlink()
                else:
                    metadata_path.write_text("{not-json", encoding="utf-8")

                with self.assertRaises(SERVER.MachineTokenRejected):
                    store.put_snapshot(space, machine, 2, b"replacement", "d" * 43)
                self.assertEqual(b"original", (store.machine_dir(space, machine) / "snapshot.bin").read_bytes())

    def test_first_snapshot_failure_reserves_machine_ownership(self):
        store = self.store()
        space = "e" * 43
        machine = "f" * 43
        owner_token = "g" * 43
        original_atomic_write = SERVER.atomic_write
        failed = False

        def fail_first_snapshot(path, payload):
            nonlocal failed
            if path.name == "snapshot.bin" and not failed:
                failed = True
                raise OSError("simulated snapshot failure")
            return original_atomic_write(path, payload)

        with mock.patch.object(SERVER, "atomic_write", side_effect=fail_first_snapshot):
            with self.assertRaises(OSError):
                store.put_snapshot(space, machine, 1, b"snapshot", owner_token)

        with self.assertRaises(SERVER.MachineTokenRejected):
            store.put_snapshot(space, machine, 1, b"replacement", "h" * 43)
        store.put_snapshot(space, machine, 1, b"snapshot", owner_token)
        self.assertEqual(b"snapshot", store.get_snapshot(space, machine)[0])

    def test_machine_and_global_storage_limits(self):
        machine_store = self.store(MaxStorageBytesPerMachine=64 * 1024)
        with self.assertRaises(SERVER.StorageLimitExceeded):
            machine_store.put_snapshot("n" * 43, "o" * 43, 1, b"x" * (50 * 1024), "p" * 43)

        global_store = self.store(
            MaxSpaces=2,
            MaxMachinesPerSpace=32,
            MaxMachinesTotal=32,
            MaxStorageBytes=1024 * 1024,
            MaxStorageBytesPerMachine=128 * 1024,
        )
        stored = 0
        for index in range(32):
            machine = ("%032d" % index) + "machine-token"
            try:
                global_store.put_snapshot("q" * 43, machine, 1, b"x" * (60 * 1024), machine)
                stored += 1
            except SERVER.StorageLimitExceeded:
                break
        self.assertGreater(stored, 0)
        self.assertLess(stored, 32)

    def test_retention_removes_stale_machine_and_empty_space(self):
        store = self.store(RetentionDays=1)
        space = "r" * 43
        machine = "s" * 43
        store.put_snapshot(space, machine, 1, b"snapshot", "t" * 43)
        directory = store.machine_dir(space, machine)
        metadata = store.load_metadata(directory)
        metadata["LastSeenUnixMs"] = 1000
        store.save_metadata(directory, metadata)
        removed = store.maintain(force=True, now_unix_ms=2 * 24 * 60 * 60 * 1000)
        self.assertEqual(1, removed)
        self.assertFalse(directory.exists())
        self.assertFalse((store.root / "Spaces" / space).exists())

    def test_deltas_are_served_from_bounded_memory_without_per_delta_files(self):
        store = self.store(MaxDeltasPerMachine=4, MaxBufferedDeltaBytes=1024 * 1024)
        space = "d" * 43
        machine = "e" * 43
        token = "f" * 43
        store.put_snapshot(space, machine, 1, b"snapshot", token)
        store.append_delta(space, machine, 2, b"delta-2", token)
        store.append_delta(space, machine, 3, b"delta-3", token)
        directory = store.machine_dir(space, machine)
        self.assertFalse((directory / "Deltas").exists())
        deltas, metadata = store.get_deltas(space, machine, 1)
        self.assertEqual([2, 3], [item["Sequence"] for item in deltas])
        self.assertEqual(3, metadata["LatestSequence"])
        self.assertEqual(len(b"delta-2") + len(b"delta-3"), store._buffered_delta_bytes)

        restarted = SERVER.StateStore({
            "DataPath": str(store.root),
            "MaxEnvelopeBytes": 64 * 1024,
            "MaxDeltasPerMachine": 4,
            "MaxDeltaBytesPerMachine": 128 * 1024,
            "MaxBufferedDeltaBytes": 1024 * 1024,
            "MaxStorageBytes": 2 * 1024 * 1024,
            "MaxStorageBytesPerMachine": 128 * 1024,
        })
        self.assertEqual(1, restarted.list_machines(space)[0]["LatestSequence"])
        with self.assertRaises(SERVER.SequenceConflict):
            restarted.append_delta(space, machine, 4, b"delta-4", token)
        restarted.put_snapshot(space, machine, 4, b"fresh-snapshot", token)
        self.assertEqual(4, restarted.list_machines(space)[0]["LatestSequence"])

    def test_global_memory_limit_requests_a_fresh_snapshot(self):
        store = self.store(
            MaxEnvelopeBytes=1024 * 1024,
            MaxDeltasPerMachine=8,
            MaxDeltaBytesPerMachine=2 * 1024 * 1024,
            MaxBufferedDeltaBytes=1024 * 1024,
            MaxStorageBytesPerMachine=2 * 1024 * 1024,
            MaxStorageBytes=4 * 1024 * 1024,
        )
        space = "g" * 43
        first = "h" * 43
        second = "i" * 43
        store.put_snapshot(space, first, 1, b"snapshot", "j" * 43)
        store.put_snapshot(space, second, 1, b"snapshot", "k" * 43)
        store.append_delta(space, first, 2, b"x" * (700 * 1024), "j" * 43)
        with self.assertRaises(SERVER.SnapshotRequired):
            store.append_delta(space, second, 2, b"y" * (400 * 1024), "k" * 43)

    def test_command_count_and_combined_bytes_are_bounded(self):
        store = self.store(MaxCommandsPerMachine=1, MaxCommandBytesPerMachine=1024, MaxCommandBytes=2048)
        space = "u" * 43
        machine = "v" * 43
        store.put_snapshot(space, machine, 1, b"snapshot", "w" * 43)
        store.put_command(space, machine, "x" * 43, b"a" * 700)
        with self.assertRaises(SERVER.StorageLimitExceeded):
            store.put_command(space, machine, "y" * 43, b"b")
        with self.assertRaises(SERVER.StorageLimitExceeded):
            store.put_command(space, machine, "x" * 43, b"c" * 1025)

    def test_invalid_limit_relationships_are_rejected(self):
        with self.assertRaises(ValueError):
            self.store(MaxMachinesPerSpace=3, MaxMachinesTotal=2)
        with self.assertRaises(ValueError):
            self.store(MaxStorageBytes=1024 * 1024, MaxStorageBytesPerMachine=2 * 1024 * 1024)

    def test_request_concurrency_limit_rejects_excess_work(self):
        settings = {
            "DataPath": str(Path(self.temp.name) / "ConcurrencyData"),
            "MaxConcurrentRequests": 1,
            "RequestBacklog": 7,
            "RequestTimeoutSeconds": 9,
        }
        server = SERVER.ThreadingServer(("127.0.0.1", 0), SERVER.Handler, settings)

        class FakeRequest:
            def __init__(self):
                self.data = b""
                self.closed = False

            def sendall(self, data):
                self.data += data

            def shutdown(self, _):
                pass

            def close(self):
                self.closed = True

        try:
            self.assertEqual(1, server.max_concurrent_requests)
            self.assertEqual(7, server.request_queue_size)
            self.assertEqual(9, server.request_timeout_seconds)
            self.assertTrue(server.request_slots.acquire(blocking=False))
            request = FakeRequest()
            server.process_request(request, ("127.0.0.1", 1))
            self.assertIn(b"503 Service Unavailable", request.data)
            self.assertTrue(request.closed)
            server.request_slots.release()
        finally:
            server.server_close()

    def test_request_concurrency_is_clamped_to_memorymax_budget(self):
        settings = {
            "DataPath": str(Path(self.temp.name) / "MemoryBudgetData"),
            "MaxConcurrentRequests": 32,
        }
        with self.assertLogs(level="WARNING"):
            server = SERVER.ThreadingServer(("127.0.0.1", 0), SERVER.Handler, settings)
        try:
            self.assertEqual(4, server.max_concurrent_requests)
        finally:
            server.server_close()

        settings["DataPath"] = str(Path(self.temp.name) / "UnsafeMemoryBudgetData")
        settings["MaxBufferedDeltaBytes"] = 128 * 1024 * 1024
        settings["MaxDeltaBytesPerMachine"] = 32 * 1024 * 1024
        with self.assertRaisesRegex(ValueError, "256 MiB memory budget"):
            SERVER.ThreadingServer(("127.0.0.1", 0), SERVER.Handler, settings)

    def test_public_rate_limit_is_bounded_by_forwarded_client(self):
        settings = {
            "DataPath": str(Path(self.temp.name) / "RateLimitData"),
            "MaxRequestsPerMinutePerClient": 2,
        }
        server = SERVER.ThreadingServer(("127.0.0.1", 0), SERVER.Handler, settings)
        try:
            self.assertTrue(server.allow_client_request("203.0.113.10"))
            self.assertTrue(server.allow_client_request("203.0.113.10"))
            self.assertFalse(server.allow_client_request("203.0.113.10"))
            self.assertTrue(server.allow_client_request("203.0.113.11"))

            handler = object.__new__(SERVER.Handler)
            handler.client_address = ("127.0.0.1", 12345)
            handler.headers = {"X-Forwarded-For": "198.51.100.99, 203.0.113.12"}
            self.assertEqual("203.0.113.12", handler._client_key())
            handler.headers = {"X-Forwarded-For": "203.0.113.12, not-an-address"}
            self.assertEqual("127.0.0.1", handler._client_key())
        finally:
            server.server_close()

    def test_ipv6_listener_selects_ipv6_socket_family(self):
        if not socket.has_ipv6:
            self.skipTest("IPv6 is not available")
        settings = {"DataPath": str(Path(self.temp.name) / "IPv6Data")}
        try:
            server = SERVER.ThreadingServer(("::1", 0), SERVER.Handler, settings)
        except OSError as error:
            self.skipTest("IPv6 loopback is unavailable: %s" % error)
        try:
            self.assertEqual(socket.AF_INET6, server.address_family)
        finally:
            server.server_close()

    def test_ipv4_listener_selects_ipv4_socket_family(self):
        settings = {"DataPath": str(Path(self.temp.name) / "IPv4Data")}
        server = SERVER.ThreadingServer(("127.0.0.1", 0), SERVER.Handler, settings)
        try:
            self.assertEqual(socket.AF_INET, server.address_family)
        finally:
            server.server_close()


if __name__ == "__main__":
    unittest.main()
