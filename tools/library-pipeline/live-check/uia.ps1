param([string]$Action = "list", [string]$Match = "", [int]$ProcessId = 0, [string]$Value = "", [int]$Max = 400)
# UI Automation over the isolated Map process only: list elements, invoke/select/toggle by Name or AutomationId, set text.
if ($Value -eq '__EMPTY__') { $Value = '' }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
$windows = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
$all = @()
foreach ($w in $windows) { $all += $w; $all += @($w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) }
function Describe($e) { $r = $e.Current.BoundingRectangle; "{0} | id={1} | name={2} | {3:F0},{4:F0} {5:F0}x{6:F0}" -f $e.Current.ControlType.ProgrammaticName.Replace('ControlType.',''), $e.Current.AutomationId, $e.Current.Name, $r.X, $r.Y, $r.Width, $r.Height }
$hits = if ($Match) { $all | Where-Object { $_.Current.AutomationId -like $Match -or $_.Current.Name -like $Match } } else { $all }
switch ($Action) {
  "list" { $hits | Select-Object -First $Max | ForEach-Object { Describe $_ } }
  "invoke" {
    $e = $hits | Select-Object -First 1
    if (-not $e) { "NOT FOUND $Match"; break }
    $p = $null
    if ($e.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke(); "INVOKED " + (Describe $e) }
    elseif ($e.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) { $p.Select(); "SELECTED " + (Describe $e) }
    elseif ($e.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { $p.Toggle(); "TOGGLED " + (Describe $e) }
    elseif ($e.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$p)) { $p.Expand(); "EXPANDED " + (Describe $e) }
    else { "NO PATTERN " + (Describe $e) }
  }
  "close" {
    $e = $hits | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window } | Select-Object -First 1
    $p = $null
    if ($e -and $e.TryGetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern, [ref]$p)) { $p.Close(); "CLOSED " + (Describe $e) } else { "NO WINDOW $Match" }
  }
  "settext" {
    $e = $hits | Select-Object -First 1
    $p = $null
    if ($e -and $e.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$p)) { $p.SetValue($Value); "SET " + (Describe $e) } else { "NO VALUE PATTERN $Match" }
  }
}
