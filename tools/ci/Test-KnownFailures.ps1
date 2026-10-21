# Test-KnownFailures.ps1 — честный CI для проектов с давними падениями (R00, DEC-052).
#
# Назначение: прочитать все *.trx из папки результатов, сравнить упавшие тесты со списком известных
# падений tests/KnownFailures.txt и вернуть код 1, если упало что-то НЕ из списка. Известные падения,
# которые прошли, печатаются как подсказка сократить список. Так Rendering/Persistence/Presentation
# снова краснят сборку при новой регрессии, а давние падения (протокол §J) её не маскируют.
#
# Команда (локально и в .github/workflows/build.yml):
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/ci/Test-KnownFailures.ps1 `
#     -ResultsDir artifacts/test-results -KnownList tests/KnownFailures.txt
#
# Формат tests/KnownFailures.txt: по одной строке на тест — полное имя `Namespace.Class.Method`;
# допускается `*` в конце (префикс) и комментарии `#`. Пустые строки игнорируются.
#
# Параметры: -ResultsDir (папка с *.trx), -KnownList (список), -FailOnFixed (упавшая сборка, если
# известный тест прошёл — по умолчанию выключено, только предупреждение).

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $ResultsDir,
    [string] $KnownList = 'tests/KnownFailures.txt',
    [switch] $FailOnFixed
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ResultsDir)) {
    Write-Host "::error::results folder not found: $ResultsDir"
    exit 2
}

$known = @()
if (Test-Path -LiteralPath $KnownList) {
    $known = Get-Content -LiteralPath $KnownList -Encoding UTF8 |
        ForEach-Object { ($_ -replace '#.*$', '').Trim() } |
        Where-Object { $_ -ne '' }
}

function Test-IsKnown([string] $fullName, [string[]] $patterns) {
    foreach ($p in $patterns) {
        if ($p.EndsWith('*')) {
            if ($fullName.StartsWith($p.TrimEnd('*'), [System.StringComparison]::Ordinal)) { return $true }
        } elseif ($fullName -eq $p) {
            return $true
        }
    }
    return $false
}

$trxFiles = Get-ChildItem -LiteralPath $ResultsDir -Filter '*.trx' -Recurse
if ($trxFiles.Count -eq 0) {
    Write-Host "::error::no .trx files in $ResultsDir"
    exit 2
}

$failed = New-Object System.Collections.Generic.List[string]
$passed = New-Object System.Collections.Generic.HashSet[string]
$total = 0

foreach ($file in $trxFiles) {
    [xml] $doc = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
    $ns = New-Object System.Xml.XmlNamespaceManager($doc.NameTable)
    $ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')

    # testId → полное имя из определения теста (className + name)
    $names = @{}
    foreach ($def in $doc.SelectNodes('//t:TestDefinitions/t:UnitTest', $ns)) {
        $method = $def.SelectSingleNode('t:TestMethod', $ns)
        if ($null -eq $method) { continue }
        $className = $method.GetAttribute('className')
        $methodName = $method.GetAttribute('name')
        # xunit пишет className как "Namespace.Class", name — "Method" или "Method(args)" / display name
        $names[$def.GetAttribute('id')] = "$className.$methodName"
    }

    foreach ($result in $doc.SelectNodes('//t:Results/t:UnitTestResult', $ns)) {
        $total++
        $id = $result.GetAttribute('testId')
        $name = if ($names.ContainsKey($id)) { $names[$id] } else { $result.GetAttribute('testName') }
        switch ($result.GetAttribute('outcome')) {
            'Failed' { $failed.Add($name) }
            'Passed' { [void] $passed.Add($name) }
            default { }
        }
    }
}

$new = @($failed | Where-Object { -not (Test-IsKnown $_ $known) } | Sort-Object -Unique)
$knownFailed = @($failed | Where-Object { Test-IsKnown $_ $known } | Sort-Object -Unique)
$fixed = @($known | Where-Object { $p = $_; -not $p.EndsWith('*') -and $passed.Contains($p) })

Write-Host ("Tests: {0}; failed: {1} (known {2}, NEW {3}); known entries: {4}; files: {5}" -f `
    $total, $failed.Count, $knownFailed.Count, $new.Count, $known.Count, $trxFiles.Count)

foreach ($n in $knownFailed) { Write-Host "  known: $n" }
foreach ($n in $fixed) { Write-Host "::notice::known failure now passes, remove from $KnownList : $n" }
foreach ($n in $new) { Write-Host "::error::NEW failing test (not in $KnownList): $n" }

if ($new.Count -gt 0) { exit 1 }
if ($FailOnFixed -and $fixed.Count -gt 0) { exit 1 }
exit 0
