<?php
declare(strict_types=1);

$storePath = __DIR__ . '/data/community-stats.json';
$items = [];
if (is_file($storePath)) {
    $decoded = json_decode((string)file_get_contents($storePath), true);
    if (is_array($decoded)) {
        $items = array_values($decoded);
    }
}

function h(string $value): string {
    return htmlspecialchars($value, ENT_QUOTES | ENT_SUBSTITUTE, 'UTF-8');
}

function count_if(array $items, string $section, string $key): int {
    $count = 0;
    foreach ($items as $item) {
        if (!empty($item[$section][$key])) {
            $count++;
        }
    }
    return $count;
}

function count_flag_overlap(array $items, string $sectionA, string $keyA, bool $valueA, string $sectionB, string $keyB, bool $valueB): int {
    $count = 0;
    foreach ($items as $item) {
        $a = !empty($item[$sectionA][$keyA]);
        $b = !empty($item[$sectionB][$keyB]);
        if ($a === $valueA && $b === $valueB) {
            $count++;
        }
    }
    return $count;
}

function sum_key(array $items, string $section, string $key): int {
    $total = 0;
    foreach ($items as $item) {
        $total += (int)($item[$section][$key] ?? 0);
    }
    return $total;
}

function normalize_architecture_value(string $value): string {
    $clean = trim($value);
    $compact = strtolower(str_replace([' ', '_'], '-', $clean));
    $compact = preg_replace('/-+/', '-', $compact);

    if (preg_match('/(?:^|[^0-9])64[^0-9]*bits?(?:[^a-z]|$)/i', $clean) === 1) {
        return '64-bit';
    }

    if (preg_match('/(?:^|[^0-9])32[^0-9]*bits?(?:[^a-z]|$)/i', $clean) === 1) {
        return '32-bit';
    }

    if (in_array($compact, ['64-bit', '64-bits', '64bit', '64bits', 'x64', 'amd64', 'x86-64', 'x86-64-bit', 'x86-64-bits'], true)) {
        return '64-bit';
    }

    if (in_array($compact, ['32-bit', '32-bits', '32bit', '32bits', 'x86', 'i386', 'i686'], true)) {
        return '32-bit';
    }

    if (in_array($compact, ['arm64', 'aarch64'], true)) {
        return 'ARM64';
    }

    if ($compact === 'arm') {
        return 'ARM';
    }

    return $clean;
}

function normalize_size_value(string $value): string {
    $clean = trim($value);
    return preg_replace_callback('/\b(\d+),(\d+)\s*([KMGTPE]?i?B)\b/i', function ($matches) {
        return $matches[1] . '.' . $matches[2] . ' ' . strtoupper($matches[3]);
    }, $clean);
}

function normalize_cpu_processor_type_value(string $value, string $architecture): string {
    $clean = trim($value);
    $compact = strtolower(str_replace([' ', '_'], '-', $clean));
    $architecture = normalize_architecture_value($architecture);

    if (($clean === '' || in_array($compact, ['1', 'other', 'unknown'], true)) &&
        in_array($architecture, ['ARM', 'ARM64'], true)) {
        return 'ARM';
    }

    return $clean;
}

function normalize_cpu_vendor_value(string $value, string $architecture): string {
    $clean = trim($value);
    $compact = strtolower(str_replace([' ', '_'], '-', $clean));
    $architecture = normalize_architecture_value($architecture);

    if (($clean === '' || in_array($compact, ['other', 'unknown'], true)) &&
        in_array($architecture, ['ARM', 'ARM64'], true)) {
        return 'ARM';
    }

    return $clean;
}

function aggregate_value(array $items, string $section, string $key, ?callable $normalizer = null): array {
    $result = [];
    foreach ($items as $item) {
        $value = trim((string)($item[$section][$key] ?? ''));
        if ($value === '') {
            continue;
        }
        if ($normalizer !== null) {
            $value = $normalizer($value);
        }
        if (!isset($result[$value])) {
            $result[$value] = 0;
        }
        $result[$value]++;
    }
    arsort($result, SORT_NUMERIC);
    return $result;
}

function aggregate_cpu_vendors(array $items): array {
    $result = [];
    foreach ($items as $item) {
        $summary = is_array($item['hardwareSummary'] ?? null) ? $item['hardwareSummary'] : [];
        $value = normalize_cpu_vendor_value(
            (string)($summary['cpuVendor'] ?? ''),
            (string)($summary['cpuArchitecture'] ?? '')
        );
        if ($value === '') {
            continue;
        }
        if (!isset($result[$value])) {
            $result[$value] = 0;
        }
        $result[$value]++;
    }
    arsort($result, SORT_NUMERIC);
    return $result;
}

function aggregate_cpu_processor_types(array $items): array {
    $result = [];
    foreach ($items as $item) {
        $summary = is_array($item['hardwareSummary'] ?? null) ? $item['hardwareSummary'] : [];
        $value = normalize_cpu_processor_type_value(
            (string)($summary['cpuProcessorType'] ?? ''),
            (string)($summary['cpuArchitecture'] ?? '')
        );
        if ($value === '') {
            continue;
        }
        if (!isset($result[$value])) {
            $result[$value] = 0;
        }
        $result[$value]++;
    }
    arsort($result, SORT_NUMERIC);
    return $result;
}

function aggregate_map(array $items, string $section, string $key): array {
    $result = [];
    foreach ($items as $item) {
        $map = $item[$section][$key] ?? [];
        if (!is_array($map)) {
            continue;
        }
        foreach ($map as $name => $count) {
            if (!isset($result[$name])) {
                $result[$name] = 0;
            }
            $result[$name] += (int)$count;
        }
    }
    arsort($result, SORT_NUMERIC);
    return $result;
}

function aggregate_windows_system_value(array $items, string $key): array {
    $result = [];
    foreach ($items as $item) {
        $value = trim((string)($item['system'][$key] ?? ''));
        $build = trim((string)($item['system']['windowsBuild'] ?? ''));

        if ($key === 'windowsVersion' && $build === '' && preg_match('/^6\./', $value) === 1) {
            $value = '';
        }

        if ($key === 'windowsArchitecture' && $value !== '') {
            $value = normalize_architecture_value($value);
        }

        if ($value === '') {
            $value = 'Unknown (older submission)';
        }

        if (!isset($result[$value])) {
            $result[$value] = 0;
        }
        $result[$value]++;
    }
    arsort($result);
    return $result;
}

function aggregate_list(array $items, string $section, string $key): array {
    $result = [];
    foreach ($items as $item) {
        $list = $item[$section][$key] ?? [];
        if (!is_array($list)) {
            continue;
        }
        foreach ($list as $name) {
            if (!isset($result[$name])) {
                $result[$name] = 0;
            }
            $result[$name]++;
        }
    }
    arsort($result, SORT_NUMERIC);
    return $result;
}

function render_table(string $heading, string $firstColumn, string $secondColumn, array $rows): void {
    echo '<h2>' . h($heading) . '</h2>';
    echo '<table><tr><th>' . h($firstColumn) . '</th><th>' . h($secondColumn) . '</th></tr>';
    if (count($rows) === 0) {
        echo '<tr><td colspan="2">No data yet.</td></tr>';
    } else {
        foreach ($rows as $name => $count) {
            echo '<tr><td>' . h((string)$name) . '</td><td>' . (int)$count . '</td></tr>';
        }
    }
    echo '</table>';
}

$machineCount = count($items);
$plugins = aggregate_list($items, 'availability', 'enabledPlugIns');
$screenReaders = aggregate_list($items, 'accessibility', 'detectedScreenReaders');
$cpuVendors = aggregate_cpu_vendors($items);
$cpuArchitectures = aggregate_value($items, 'hardwareSummary', 'cpuArchitecture', 'normalize_architecture_value');
$cpuTypes = aggregate_cpu_processor_types($items);
$gpuVendors = aggregate_map($items, 'hardwareSummary', 'gpuVendorCounts');
$memoryTotals = aggregate_value($items, 'hardwareSummary', 'memoryTotal', 'normalize_size_value');
$connectedDiskTotals = aggregate_value($items, 'hardwareSummary', 'connectedDiskTotal', 'normalize_size_value');
$connectedDiskUsed = aggregate_value($items, 'hardwareSummary', 'connectedDiskUsed', 'normalize_size_value');
$connectedDiskFree = aggregate_value($items, 'hardwareSummary', 'connectedDiskFree', 'normalize_size_value');
$connectedDiskCounts = aggregate_value($items, 'hardwareSummary', 'connectedDiskCount');
$gpuMemoryTotals = aggregate_value($items, 'hardwareSummary', 'dedicatedGpuMemoryTotal', 'normalize_size_value');
$languages = aggregate_value($items, 'system', 'language');
$installModes = aggregate_value($items, 'system', 'installMode');
$windowsEditions = aggregate_windows_system_value($items, 'windowsCaption');
$windowsVersions = aggregate_windows_system_value($items, 'windowsVersion');
$windowsBuilds = aggregate_windows_system_value($items, 'windowsBuild');
$windowsArchitectures = aggregate_windows_system_value($items, 'windowsArchitecture');
?>
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Sensor Readout Community Stats</title>
  <style>
    body { font-family: system-ui, sans-serif; line-height: 1.45; margin: 2rem; max-width: 1100px; color: #111; }
    table { border-collapse: collapse; width: 100%; margin: 1rem 0 2rem; }
    th, td { border: 1px solid #bbb; padding: .45rem .6rem; text-align: left; vertical-align: top; }
    th { background: #eee; }
    .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(18rem, 1fr)); gap: 1.25rem; }
    .grid table { margin-top: .5rem; }
  </style>
</head>
<body>
  <h1>Sensor Readout Community Stats</h1>
  <p>These are opt-in aggregate hardware and accessibility coverage stats submitted from Sensor Readout. The upload payload is allow-listed and does not include computer names, usernames, serial numbers, MAC or IP addresses, paths, drive labels, device IDs, PnP IDs, raw details, installed programs, program usage, or full report rows.</p>
  <p><a href="https://github.com/OnjLouis/accessible-sensor-readout/releases/latest">Download Sensor Readout</a> or visit the <a href="https://github.com/OnjLouis/accessible-sensor-readout">project page</a>.</p>

  <h2>How to submit your stats</h2>
  <p>In Sensor Readout, open <strong>Help</strong> and choose <strong>Share anonymous community stats...</strong>. Sensor Readout will generate a small privacy-safe payload and show you the exact text before anything is sent. Press <strong>Upload</strong> only if you are happy to contribute it.</p>
  <p>Submission is optional, manual, and never automatic. You can also save the payload instead of uploading it.</p>

  <h2>Summary</h2>
  <table>
    <tr><th>Metric</th><th>Machines</th></tr>
    <tr><td>Machines</td><td><?= $machineCount ?></td></tr>
    <tr><td>With temperatures</td><td><?= count_if($items, 'availability', 'hasTemperatures') ?></td></tr>
    <tr><td>With fans</td><td><?= count_if($items, 'availability', 'hasFans') ?></td></tr>
    <tr><td>With SMART</td><td><?= count_if($items, 'availability', 'hasSmart') ?></td></tr>
    <tr><td>With connected disk totals</td><td><?= count_if($items, 'availability', 'hasConnectedDiskTotals') ?></td></tr>
    <tr><td>With GPU memory</td><td><?= count_if($items, 'availability', 'hasGpuMemory') ?></td></tr>
    <tr><td>With BitLocker status</td><td><?= count_if($items, 'availability', 'hasBitLockerStatus') ?></td></tr>
    <tr><td>With printer rows</td><td><?= count_if($items, 'availability', 'hasPrinterRows') ?></td></tr>
    <tr><td>With device issues</td><td><?= count_if($items, 'availability', 'hasNonWorkingDevices') ?></td></tr>
  </table>

  <h2>Fans and device issues overlap</h2>
  <table>
    <tr><th>Group</th><th>Machines</th></tr>
    <tr><td>Fans and device issues</td><td><?= count_flag_overlap($items, 'availability', 'hasFans', true, 'availability', 'hasNonWorkingDevices', true) ?></td></tr>
    <tr><td>Fans only</td><td><?= count_flag_overlap($items, 'availability', 'hasFans', true, 'availability', 'hasNonWorkingDevices', false) ?></td></tr>
    <tr><td>Device issues only</td><td><?= count_flag_overlap($items, 'availability', 'hasFans', false, 'availability', 'hasNonWorkingDevices', true) ?></td></tr>
    <tr><td>Neither</td><td><?= count_flag_overlap($items, 'availability', 'hasFans', false, 'availability', 'hasNonWorkingDevices', false) ?></td></tr>
  </table>

  <h2>Accessibility setup</h2>
  <table>
    <tr><th>Metric</th><th>Machines</th></tr>
    <tr><td>Screen-reader output available</td><td><?= count_if($items, 'accessibility', 'screenReaderOutputAvailable') ?></td></tr>
    <tr><td>High contrast enabled</td><td><?= count_if($items, 'accessibility', 'highContrastEnabled') ?></td></tr>
    <tr><td>Sticky Keys enabled</td><td><?= count_if($items, 'accessibility', 'stickyKeysEnabled') ?></td></tr>
    <tr><td>Toggle Keys enabled</td><td><?= count_if($items, 'accessibility', 'toggleKeysEnabled') ?></td></tr>
    <tr><td>Filter Keys enabled</td><td><?= count_if($items, 'accessibility', 'filterKeysEnabled') ?></td></tr>
    <tr><td>Show sounds enabled</td><td><?= count_if($items, 'accessibility', 'showSoundsEnabled') ?></td></tr>
    <tr><td>Audio descriptions enabled</td><td><?= count_if($items, 'accessibility', 'audioDescriptionsEnabled') ?></td></tr>
    <tr><td>Show/hide hotkey configured</td><td><?= count_if($items, 'accessibility', 'showHideHotKeyConfigured') ?></td></tr>
    <tr><td>Speak tray status hotkey configured</td><td><?= count_if($items, 'accessibility', 'speakTrayHotKeyConfigured') ?></td></tr>
    <tr><td>Notification area status enabled</td><td><?= count_if($items, 'accessibility', 'trayStatusEnabled') ?></td></tr>
    <tr><td>Starts minimized to notification area</td><td><?= count_if($items, 'accessibility', 'startMinimizedToTray') ?></td></tr>
    <tr><td>Spoken hotkey profiles</td><td><?= sum_key($items, 'accessibility', 'spokenHotKeyProfileCount') ?> total</td></tr>
    <tr><td>Notification area readouts</td><td><?= sum_key($items, 'accessibility', 'trayReadoutCount') ?> total</td></tr>
  </table>

  <div class="grid">
    <div><?php render_table('Detected screen readers', 'Screen reader', 'Machines', $screenReaders); ?></div>
    <div><?php render_table('Windows editions', 'Edition', 'Machines', $windowsEditions); ?></div>
    <div><?php render_table('Windows versions', 'Version', 'Machines', $windowsVersions); ?></div>
    <div><?php render_table('Windows builds', 'Build', 'Machines', $windowsBuilds); ?></div>
    <div><?php render_table('Windows architecture', 'Architecture', 'Machines', $windowsArchitectures); ?></div>
    <div><?php render_table('CPU vendors', 'Vendor', 'Machines', $cpuVendors); ?></div>
    <div><?php render_table('CPU architecture', 'Architecture', 'Machines', $cpuArchitectures); ?></div>
    <div><?php render_table('CPU processor types', 'Type', 'Machines', $cpuTypes); ?></div>
    <div><?php render_table('GPU vendors', 'Vendor', 'Display rows', $gpuVendors); ?></div>
    <div><?php render_table('Memory totals', 'Memory', 'Machines', $memoryTotals); ?></div>
    <div><?php render_table('Connected disk totals', 'Storage', 'Machines', $connectedDiskTotals); ?></div>
    <div><?php render_table('Connected disk used', 'Storage', 'Machines', $connectedDiskUsed); ?></div>
    <div><?php render_table('Connected disk free', 'Storage', 'Machines', $connectedDiskFree); ?></div>
    <div><?php render_table('Connected disk counts', 'Drives', 'Machines', $connectedDiskCounts); ?></div>
    <div><?php render_table('Dedicated GPU memory totals', 'Memory', 'Machines', $gpuMemoryTotals); ?></div>
    <div><?php render_table('Languages', 'Language', 'Machines', $languages); ?></div>
    <div><?php render_table('Install mode', 'Mode', 'Machines', $installModes); ?></div>
  </div>

  <h2>Coverage totals</h2>
  <table>
    <tr><th>Area</th><th>Total rows</th></tr>
    <tr><td>Temperatures</td><td><?= sum_key($items, 'coverage', 'temperatureRowCount') ?></td></tr>
    <tr><td>Fans</td><td><?= sum_key($items, 'coverage', 'fanRowCount') ?></td></tr>
    <tr><td>SMART/storage</td><td><?= sum_key($items, 'coverage', 'smartRowCount') ?></td></tr>
    <tr><td>Network</td><td><?= sum_key($items, 'coverage', 'networkRowCount') ?></td></tr>
    <tr><td>USB</td><td><?= sum_key($items, 'coverage', 'usbRowCount') ?></td></tr>
    <tr><td>Device inventory</td><td><?= sum_key($items, 'coverage', 'deviceRowCount') ?></td></tr>
    <tr><td>Non-working devices</td><td><?= sum_key($items, 'coverage', 'nonWorkingDeviceCount') ?></td></tr>
    <tr><td>Printer rows</td><td><?= sum_key($items, 'coverage', 'printerRowCount') ?></td></tr>
    <tr><td>Battery rows</td><td><?= sum_key($items, 'coverage', 'batteryRowCount') ?></td></tr>
    <tr><td>GPU memory rows</td><td><?= sum_key($items, 'coverage', 'gpuMemoryRowCount') ?></td></tr>
  </table>

  <?php render_table('Enabled plug-ins', 'Plug-in', 'Machines', $plugins); ?>
</body>
</html>
