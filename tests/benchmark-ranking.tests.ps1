# Execute the actual ranking assignment from the production script with offline fixtures.
$scriptPath = Join-Path $PSScriptRoot '../scripts/benchmark-duplicate-models.ps1'
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw $errors[0] }
$assignment = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$sorted' }, $true)
foreach ($fixture in @(@(-1,100,200), @(-1,-1), @(200,100))) {
    $group = @{ Group = @($fixture | ForEach-Object { [pscustomobject]@{SuccessCount = [int]($_ -ge 0); AvgLatencyMs = $_} }) }
    Invoke-Expression $assignment.Extent.Text
    $expected = @($fixture | Where-Object { $_ -ge 0 } | Sort-Object)
    if ($sorted.Count -ne $expected.Count) { throw 'Invalid eligible count' }
    if ($expected.Count -and $sorted[0].AvgLatencyMs -ne $expected[0]) { throw 'Invalid winner' }
}
Write-Output 'Benchmark ranking: 3 fixtures passed'
