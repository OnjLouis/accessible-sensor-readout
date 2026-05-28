<?php
declare(strict_types=1);

header('Content-Type: application/json; charset=utf-8');
header('Cache-Control: no-store');

if ($_SERVER['REQUEST_METHOD'] !== 'POST') {
    http_response_code(405);
    echo json_encode(['ok' => false, 'error' => 'POST required']);
    exit;
}

$raw = file_get_contents('php://input');
if ($raw === false || strlen($raw) === 0 || strlen($raw) > 65536) {
    http_response_code(400);
    echo json_encode(['ok' => false, 'error' => 'Invalid payload size']);
    exit;
}
$raw = preg_replace('/^\xEF\xBB\xBF/', '', $raw);

$payload = json_decode($raw, true);
if (!is_array($payload)) {
    http_response_code(400);
    echo json_encode(['ok' => false, 'error' => 'Invalid JSON']);
    exit;
}

function safe_string($value, int $max = 120): string {
    if (!is_string($value)) {
        return '';
    }
    $value = trim($value);
    if (strlen($value) > $max) {
        $value = substr($value, 0, $max);
    }
    if (preg_match('/(?:\d{1,3}\.){3}\d{1,3}/', $value)) {
        return '';
    }
    if (preg_match('/[0-9a-f]{2}(?:[:-][0-9a-f]{2}){5}/i', $value)) {
        return '';
    }
    if (strpos($value, '\\') !== false || strpos($value, '/') !== false || strpos($value, '@') !== false) {
        return '';
    }
    return $value;
}

function safe_bool($value): bool {
    return $value === true;
}

function safe_int($value, int $min = 0, int $max = 1000000): int {
    if (!is_numeric($value)) {
        return 0;
    }
    $value = (int)$value;
    return max($min, min($max, $value));
}

function safe_string_list($value): array {
    if (!is_array($value)) {
        return [];
    }
    $result = [];
    foreach ($value as $item) {
        $safe = safe_string($item, 80);
        if ($safe !== '') {
            $result[$safe] = true;
        }
    }
    $items = array_keys($result);
    sort($items, SORT_NATURAL | SORT_FLAG_CASE);
    return array_slice($items, 0, 100);
}

function safe_count_map($value): array {
    if (!is_array($value)) {
        return [];
    }
    $result = [];
    foreach ($value as $key => $count) {
        $safeKey = safe_string((string)$key, 80);
        if ($safeKey !== '') {
            $result[$safeKey] = safe_int($count);
        }
    }
    ksort($result, SORT_NATURAL | SORT_FLAG_CASE);
    return $result;
}

function rate_limit_response(string $message, int $retryAfterSeconds): void {
    http_response_code(429);
    header('Retry-After: ' . max(1, $retryAfterSeconds));
    echo json_encode(['ok' => false, 'error' => $message]);
    exit;
}

function enforce_rate_limit(string $dataDir, string $clientHash): void {
    $now = time();
    $hourAgo = $now - 3600;
    $dayAgo = $now - 86400;
    $minClientIntervalSeconds = 30;
    $maxClientPerHour = 8;
    $maxClientPerDay = 24;
    $maxGlobalPerHour = 300;
    $maxGlobalPerDay = 2000;

    $lockPath = $dataDir . '/rate-limits.lock';
    $lock = @fopen($lockPath, 'c');
    if ($lock === false) {
        return;
    }

    if (!flock($lock, LOCK_EX)) {
        fclose($lock);
        return;
    }

    $ratePath = $dataDir . '/rate-limits.json';
    $state = ['global' => [], 'clients' => []];
    if (is_file($ratePath)) {
        $decoded = json_decode((string)file_get_contents($ratePath), true);
        if (is_array($decoded)) {
            $state = array_merge($state, $decoded);
        }
    }

    $global = array_values(array_filter((array)($state['global'] ?? []), static function ($timestamp) use ($dayAgo): bool {
        return is_numeric($timestamp) && (int)$timestamp >= $dayAgo;
    }));
    $clients = is_array($state['clients'] ?? null) ? $state['clients'] : [];
    foreach ($clients as $hash => $timestamps) {
        if (!preg_match('/^[a-f0-9]{64}$/', (string)$hash) || !is_array($timestamps)) {
            unset($clients[$hash]);
            continue;
        }
        $clients[$hash] = array_values(array_filter($timestamps, static function ($timestamp) use ($dayAgo): bool {
            return is_numeric($timestamp) && (int)$timestamp >= $dayAgo;
        }));
        if (count($clients[$hash]) === 0) {
            unset($clients[$hash]);
        }
    }
    $client = array_values($clients[$clientHash] ?? []);

    $globalHour = count(array_filter($global, static function ($timestamp) use ($hourAgo): bool {
        return (int)$timestamp >= $hourAgo;
    }));
    if ($globalHour >= $maxGlobalPerHour || count($global) >= $maxGlobalPerDay) {
        flock($lock, LOCK_UN);
        fclose($lock);
        rate_limit_response('Too many community stats submissions right now. Please try again later.', 3600);
    }

    if (count($client) > 0) {
        $last = max(array_map('intval', $client));
        $sinceLast = $now - $last;
        if ($sinceLast < $minClientIntervalSeconds) {
            flock($lock, LOCK_UN);
            fclose($lock);
            rate_limit_response('Community stats were submitted too recently. Please wait a moment and try again.', $minClientIntervalSeconds - $sinceLast);
        }
    }

    $clientHour = count(array_filter($client, static function ($timestamp) use ($hourAgo): bool {
        return (int)$timestamp >= $hourAgo;
    }));
    if ($clientHour >= $maxClientPerHour || count($client) >= $maxClientPerDay) {
        flock($lock, LOCK_UN);
        fclose($lock);
        rate_limit_response('This anonymous client has submitted community stats too often. Please try again later.', 3600);
    }

    $global[] = $now;
    $client[] = $now;
    $clients[$clientHash] = $client;
    $state = ['global' => $global, 'clients' => $clients];
    $tmp = $ratePath . '.tmp';
    if (file_put_contents($tmp, json_encode($state, JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES), LOCK_EX) !== false) {
        @rename($tmp, $ratePath);
    }

    flock($lock, LOCK_UN);
    fclose($lock);
}

$clientHash = isset($payload['anonymousClientIdHash']) ? (string)$payload['anonymousClientIdHash'] : '';
if (!preg_match('/^[a-f0-9]{64}$/', $clientHash)) {
    http_response_code(400);
    echo json_encode(['ok' => false, 'error' => 'Invalid anonymous client hash']);
    exit;
}

$system = is_array($payload['system'] ?? null) ? $payload['system'] : [];
$counts = is_array($payload['counts'] ?? null) ? $payload['counts'] : [];
$availability = is_array($payload['availability'] ?? null) ? $payload['availability'] : [];
$hardware = is_array($payload['hardwareSummary'] ?? null) ? $payload['hardwareSummary'] : [];
$accessibility = is_array($payload['accessibility'] ?? null) ? $payload['accessibility'] : [];
$configuration = is_array($payload['configuration'] ?? null) ? $payload['configuration'] : [];
$coverage = is_array($payload['coverage'] ?? null) ? $payload['coverage'] : [];

$clean = [
    'schemaVersion' => safe_int($payload['schemaVersion'] ?? 0, 1, 100),
    'appVersion' => safe_string($payload['appVersion'] ?? '', 40),
    'generatedUtc' => safe_string($payload['generatedUtc'] ?? '', 40),
    'receivedUtc' => gmdate('c'),
    'anonymousClientIdHash' => $clientHash,
    'system' => [
        'windowsVersion' => safe_string($system['windowsVersion'] ?? '', 60),
        'platform' => safe_string($system['platform'] ?? '', 40),
        'is64BitOperatingSystem' => safe_bool($system['is64BitOperatingSystem'] ?? false),
        'is64BitProcess' => safe_bool($system['is64BitProcess'] ?? false),
        'logicalProcessorCount' => safe_int($system['logicalProcessorCount'] ?? 0, 0, 1024),
        'refreshIntervalSeconds' => safe_int($system['refreshIntervalSeconds'] ?? 0, 0, 300),
        'temperatureUnit' => safe_string($system['temperatureUnit'] ?? '', 8),
        'language' => safe_string($system['language'] ?? '', 40),
        'installMode' => safe_string($system['installMode'] ?? '', 20),
    ],
    'counts' => [
        'totalRows' => safe_int($counts['totalRows'] ?? 0),
        'selectableRows' => safe_int($counts['selectableRows'] ?? 0),
        'rowsWithDetails' => safe_int($counts['rowsWithDetails'] ?? 0),
        'rowsByCategory' => safe_count_map($counts['rowsByCategory'] ?? []),
        'selectableRowsByCategory' => safe_count_map($counts['selectableRowsByCategory'] ?? []),
    ],
    'availability' => [
        'hasTemperatures' => safe_bool($availability['hasTemperatures'] ?? false),
        'hasFans' => safe_bool($availability['hasFans'] ?? false),
        'hasSmart' => safe_bool($availability['hasSmart'] ?? false),
        'hasNetwork' => safe_bool($availability['hasNetwork'] ?? false),
        'hasUsb' => safe_bool($availability['hasUsb'] ?? false),
        'hasAudio' => safe_bool($availability['hasAudio'] ?? false),
        'hasDisplay' => safe_bool($availability['hasDisplay'] ?? false),
        'hasBattery' => safe_bool($availability['hasBattery'] ?? false),
        'hasDevices' => safe_bool($availability['hasDevices'] ?? false),
        'hasGpuMemory' => safe_bool($availability['hasGpuMemory'] ?? false),
        'hasBitLockerStatus' => safe_bool($availability['hasBitLockerStatus'] ?? false),
        'hasPrinterRows' => safe_bool($availability['hasPrinterRows'] ?? false),
        'hasNonWorkingDevices' => safe_bool($availability['hasNonWorkingDevices'] ?? false),
        'enabledPlugInCount' => safe_int($availability['enabledPlugInCount'] ?? 0, 0, 100),
        'enabledPlugIns' => safe_string_list($availability['enabledPlugIns'] ?? []),
    ],
    'hardwareSummary' => [
        'cpuVendor' => safe_string($hardware['cpuVendor'] ?? '', 40),
        'cpuArchitecture' => safe_string($hardware['cpuArchitecture'] ?? '', 40),
        'cpuProcessorType' => safe_string($hardware['cpuProcessorType'] ?? '', 40),
        'cpuCoreCount' => safe_int($hardware['cpuCoreCount'] ?? 0, 0, 1024),
        'cpuThreadCount' => safe_int($hardware['cpuThreadCount'] ?? 0, 0, 1024),
        'gpuVendorCounts' => safe_count_map($hardware['gpuVendorCounts'] ?? []),
        'memoryTotal' => safe_string($hardware['memoryTotal'] ?? '', 40),
        'pagingFileTotal' => safe_string($hardware['pagingFileTotal'] ?? '', 40),
        'dedicatedGpuMemoryTotal' => safe_string($hardware['dedicatedGpuMemoryTotal'] ?? '', 40),
        'smartDeviceCount' => safe_int($hardware['smartDeviceCount'] ?? 0, 0, 1000),
        'networkAdapterGroupCount' => safe_int($hardware['networkAdapterGroupCount'] ?? 0, 0, 1000),
        'usbGroupCount' => safe_int($hardware['usbGroupCount'] ?? 0, 0, 1000),
        'audioGroupCount' => safe_int($hardware['audioGroupCount'] ?? 0, 0, 1000),
        'displayGroupCount' => safe_int($hardware['displayGroupCount'] ?? 0, 0, 1000),
        'deviceInventoryGroupCount' => safe_int($hardware['deviceInventoryGroupCount'] ?? 0, 0, 1000),
    ],
    'accessibility' => [
        'screenReaderOutputAvailable' => safe_bool($accessibility['screenReaderOutputAvailable'] ?? false),
        'detectedScreenReaders' => safe_string_list($accessibility['detectedScreenReaders'] ?? []),
        'startupSpeechEnabled' => safe_bool($accessibility['startupSpeechEnabled'] ?? false),
        'showHideHotKeyConfigured' => safe_bool($accessibility['showHideHotKeyConfigured'] ?? false),
        'speakTrayHotKeyConfigured' => safe_bool($accessibility['speakTrayHotKeyConfigured'] ?? false),
        'trayStatusEnabled' => safe_bool($accessibility['trayStatusEnabled'] ?? false),
        'trayReadoutCount' => safe_int($accessibility['trayReadoutCount'] ?? 0, 0, 1000),
        'spokenHotKeyProfileCount' => safe_int($accessibility['spokenHotKeyProfileCount'] ?? 0, 0, 1000),
        'startMinimizedToTray' => safe_bool($accessibility['startMinimizedToTray'] ?? false),
        'tipsOnStartupEnabled' => safe_bool($accessibility['tipsOnStartupEnabled'] ?? false),
    ],
    'configuration' => [
        'autoRefreshEnabled' => safe_bool($configuration['autoRefreshEnabled'] ?? false),
        'refreshWhileFocused' => safe_bool($configuration['refreshWhileFocused'] ?? false),
        'checkForUpdatesAtStartup' => safe_bool($configuration['checkForUpdatesAtStartup'] ?? false),
        'quietUpdatesEnabled' => safe_bool($configuration['quietUpdatesEnabled'] ?? false),
        'alarmCount' => safe_int($configuration['alarmCount'] ?? 0, 0, 1000),
        'fanProfileCount' => safe_int($configuration['fanProfileCount'] ?? 0, 0, 1000),
        'fanProfileHotKeyCount' => safe_int($configuration['fanProfileHotKeyCount'] ?? 0, 0, 1000),
        'hiddenReadingCount' => safe_int($configuration['hiddenReadingCount'] ?? 0, 0, 10000),
    ],
    'coverage' => [
        'temperatureRowCount' => safe_int($coverage['temperatureRowCount'] ?? 0, 0, 100000),
        'fanRowCount' => safe_int($coverage['fanRowCount'] ?? 0, 0, 100000),
        'smartRowCount' => safe_int($coverage['smartRowCount'] ?? 0, 0, 100000),
        'networkRowCount' => safe_int($coverage['networkRowCount'] ?? 0, 0, 100000),
        'usbRowCount' => safe_int($coverage['usbRowCount'] ?? 0, 0, 100000),
        'deviceRowCount' => safe_int($coverage['deviceRowCount'] ?? 0, 0, 100000),
        'nonWorkingDeviceCount' => safe_int($coverage['nonWorkingDeviceCount'] ?? 0, 0, 100000),
        'printerRowCount' => safe_int($coverage['printerRowCount'] ?? 0, 0, 100000),
        'batteryRowCount' => safe_int($coverage['batteryRowCount'] ?? 0, 0, 100000),
        'gpuMemoryRowCount' => safe_int($coverage['gpuMemoryRowCount'] ?? 0, 0, 100000),
    ],
];

$dataDir = __DIR__ . '/data';
if (!is_dir($dataDir) && !mkdir($dataDir, 0750, true)) {
    http_response_code(500);
    echo json_encode(['ok' => false, 'error' => 'Could not create data folder']);
    exit;
}

$denyPath = $dataDir . '/.htaccess';
if (!is_file($denyPath)) {
    @file_put_contents($denyPath, "Require all denied\nDeny from all\n");
}

enforce_rate_limit($dataDir, $clientHash);

$storePath = $dataDir . '/community-stats.json';
$store = [];
if (is_file($storePath)) {
    $existing = json_decode((string)file_get_contents($storePath), true);
    if (is_array($existing)) {
        $store = $existing;
    }
}

$store[$clientHash] = $clean;
$tmp = $storePath . '.tmp';
if (file_put_contents($tmp, json_encode($store, JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES), LOCK_EX) === false || !rename($tmp, $storePath)) {
    http_response_code(500);
    echo json_encode(['ok' => false, 'error' => 'Could not save payload']);
    exit;
}

echo json_encode(['ok' => true, 'updated' => true, 'machines' => count($store)]);
