# Windows integration regression against the published executable, without an in-process test host.
# Run with Windows PowerShell 5.1: powershell -NoProfile -File tools/test_native_export_cancel.ps1
param(
    [string]$Executable = "$PSScriptRoot/../bin/win-x64/MZTools.Desktop.exe",
    [string]$Output = "$PSScriptRoot/../TestResults/SaveCancelFollowup/release-matrix",
    [string[]]$Inputs = @("$PSScriptRoot/../MZTool.Tests/MZF/Barbarian2.mzt", "$PSScriptRoot/../MZTool.Tests/MZF/Starquake.mzt", "$PSScriptRoot/../MZTool.Tests/MZF/AudioStarB03A.mzt")
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ExportCancelNative {
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr window);
}
'@
$Executable = (Resolve-Path $Executable).Path
$Output = [IO.Path]::GetFullPath($Output)
[IO.Directory]::CreateDirectory($Output) | Out-Null
$trace = Join-Path $Output 'trace.jsonl'
if (Test-Path $trace) { throw "Use a fresh output directory: $Output" }
function Log($stage, $data) { @{stage=$stage;data=$data;utc=[DateTime]::UtcNow.ToString('o')} | ConvertTo-Json -Depth 5 -Compress | Add-Content -Encoding UTF8 $trace }
function ByName($root, $name) { $root.FindFirst(4, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $name)) }
function ById($root, $id) { $root.FindFirst(4, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)) }
function Invoke($element) { $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function WaitElement([scriptblock]$find) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do { $element = & $find; if ($element) { return $element }; Start-Sleep -Milliseconds 50 } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Timed out waiting for the application dialog'
}
function MainWindow($processId) {
    [System.Windows.Automation.AutomationElement]::RootElement.FindAll(2, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)) | Where-Object { $_.Current.ClassName -eq 'MainWindow' } | Select-Object -First 1
}
$oldSettings = $env:MZTOOLS_SETTINGS_ROOT
try {
    $env:MZTOOLS_SETTINGS_ROOT = Join-Path $Output 'preferences'
    [IO.Directory]::CreateDirectory($env:MZTOOLS_SETTINGS_ROOT) | Out-Null
    '{"Schema":1,"Width":1706.6666666666667,"Height":937.3333333333334,"LastFolder":null}' | Set-Content -Encoding ASCII (Join-Path $env:MZTOOLS_SETTINGS_ROOT 'desktop.json')
    Log 'build' @{executable=$Executable;sha256=(Get-FileHash $Executable).Hash;desktopSha256=(Get-FileHash ([IO.Path]::ChangeExtension($Executable,'.dll'))).Hash}
    foreach ($inputFile in $Inputs) {
        $inputFile = (Resolve-Path $inputFile).Path
        $before = (Get-FileHash $inputFile).Hash
        $app = Start-Process -FilePath $Executable -ArgumentList ('"'+$inputFile+'"') -WorkingDirectory (Resolve-Path "$PSScriptRoot/..").Path -PassThru
        Log 'launch' @{processId=$app.Id;source=$inputFile;sha256=$before}
        try {
            $root = WaitElement { MainWindow $app.Id }
            $table = WaitElement { ById $root 'RecordsTable' }
            $rows = $table.FindAll(4, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem))
            if ($rows.Count -lt 2) { throw "Expected multiple records in $inputFile" }
            foreach ($row in $rows) { $row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).AddToSelection() }
            foreach ($format in @('L16','WAV audio','LEP')) {
                foreach ($separate in @($false,$true)) {
                    foreach ($cancelMethod in @('button','escape','close')) {
                        Invoke (ById $root 'ExportButton')
                        $options = WaitElement { ByName $root 'Export options' }
                        $combo = $options.FindAll(4,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ComboBox)) | Where-Object { $_.Current.Name -eq 'Output format' } | Select-Object -First 1
                        $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
                        $item = WaitElement { ByName $root $format }
                        $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
                        if (ById $options 'GapPreset') { throw 'Pause presets should not be displayed' }
                        foreach ($field in @('StartGapMs','BetweenGapMs','EndGapMs')) {
                            $gap = ById $options $field
                            if (!$gap -or !$gap.Current.IsEnabled) { throw "Pause field is not directly editable: $field" }
                        }
                        if ($separate) { (ByName $options 'Separate file for each record').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() }
                        Log 'continue' @{source=$inputFile;format=$format;separate=$separate;cancel=$cancelMethod;records=$rows.Count}
                        Invoke (ByName $options 'Continue')
                        $picker = WaitElement { $root.FindFirst(4,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ClassNameProperty,'#32770')) }
                        $handle = [IntPtr]$picker.Current.NativeWindowHandle
                        switch ($cancelMethod) {
                            'button' { Invoke (ById $picker '2') }
                            'escape' { [ExportCancelNative]::PostMessage($handle,0x100,[IntPtr]0x1b,[IntPtr]::Zero) | Out-Null; [ExportCancelNative]::PostMessage($handle,0x101,[IntPtr]0x1b,[IntPtr]::Zero) | Out-Null }
                            'close' { [ExportCancelNative]::PostMessage($handle,0x10,[IntPtr]::Zero,[IntPtr]::Zero) | Out-Null }
                        }
                        $export = WaitElement { $button=ById $root 'ExportButton'; if ($button.Current.IsEnabled -and $root.Current.IsEnabled) { $button } }
                        if (![ExportCancelNative]::IsWindowEnabled([IntPtr]$root.Current.NativeWindowHandle)) { throw 'Native owner remained disabled after Cancel' }
                        # A second operation proves input dispatch really resumed after the picker closed.
                        Invoke $export
                        $again = WaitElement { ByName $root 'Export options' }
                        Invoke (ByName $again 'Cancel')
                        if ((Get-FileHash $inputFile).Hash -ne $before) { throw 'Cancel changed the input file' }
                        $selection = $table.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
                        if ($selection.Count -ne $rows.Count) { throw 'Cancel lost the multiple-record selection' }
                        Log 'case-pass' @{source=$inputFile;format=$format;separate=$separate;cancel=$cancelMethod;selected=$selection.Count}
                    }
                }
            }
        } finally {
            # Only the process launched by this test may be closed; never stop other MZTools instances.
            if (!$app.HasExited) { $app.CloseMainWindow() | Out-Null; if (!$app.WaitForExit(5000)) { Log 'close-timeout' @{processId=$app.Id}; $app.Kill() } }
        }
    }
    Log 'pass' @{}
} catch { Log 'failed' @{error=$_.ToString()}; throw } finally { $env:MZTOOLS_SETTINGS_ROOT = $oldSettings }
