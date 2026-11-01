<#
  DepthRim 遊戲內驗收測試（RimWorld 1.7），流程比照 DMSE.BVRAcceptance/run.ps1。

  1. 建置 DepthRim.Acceptance.dll（參考 1.7/Assemblies/DepthRim.dll，所以先建置 .source/DepthRim）。
  2. 只在這次執行期間把它複製進 1.7/Assemblies。
  3. 以 -quicktest（新的墜毀殖民地）與 -depthrim-acceptance=<OutDir> 啟動 RimWorld。
     測試跑完會把 depthrim_acceptance_log.txt / depthrim_acceptance_results.tsv 寫進 OutDir、拍截圖，然後關閉遊戲。
  4. 移除 DLL（遊戲當掉或逾時也會移除），並把截圖搬進 OutDir。

  模組清單：預設會建立一個暫存的存檔資料夾（RimWorld 的 -savedatafolder），複製你的 Config，
  並在 ModsConfig.xml 的 DMSE 後面加上 DepthRim，所以不會動到你自己的模組清單。
  指定 -SaveDataFolder 時改用那個資料夾的設定（自行確認 DepthRim 已啟用）。

  用法：powershell -ExecutionPolicy Bypass -File .source/DepthRim.Acceptance/run.ps1 -OutDir <dir> [-TimeoutMinutes 20] [-Only F2,P1] [-SaveDataFolder <dir>]
  RimWorld 不能正在執行（它會鎖住 1.7/Assemblies）。
#>
param(
    [Parameter(Mandatory = $true)][string]$OutDir,
    [int]$TimeoutMinutes = 20,
    [string]$GameExe = '',
    [string]$Only = '',            # 以逗號分隔的測試 id，例如 'F2,P1'；空白 = 全部
    [string]$SaveDataFolder = ''   # 選填：自備的 RimWorld -savedatafolder
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$modRoot = (Resolve-Path (Join-Path $here '..\..')).Path
if ($GameExe -eq '') { $GameExe = (Resolve-Path (Join-Path $modRoot '..\..\RimWorld.exe')).Path }
$assemblies = Join-Path $modRoot '1.7\Assemblies'
$runnerDll = Join-Path $assemblies 'DepthRim.Acceptance.dll'

if (Get-Process -Name 'RimWorld', 'RimWorldWin64' -ErrorAction SilentlyContinue) { throw 'RimWorld is running; close it first.' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

# 暫存的存檔資料夾：複製使用者的 Config，啟用 DepthRim
$tempSaveData = $null
if ($SaveDataFolder -eq '') {
    $userData = Join-Path $env:USERPROFILE 'AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios'
    $tempSaveData = Join-Path $OutDir 'savedata'
    if (Test-Path $tempSaveData) { Remove-Item $tempSaveData -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $tempSaveData | Out-Null
    Copy-Item (Join-Path $userData 'Config') (Join-Path $tempSaveData 'Config') -Recurse
    # 測試一律用 DepthRim 的預設設定：玩家自己調過的混濁度等設定會讓材質數值和 Def 對不上（只刪暫存複本，玩家的設定不動）
    Get-ChildItem (Join-Path $tempSaveData 'Config') -Filter 'Mod_*DepthRim*.xml' | Remove-Item -Force
    $modsConfig = Join-Path $tempSaveData 'Config\ModsConfig.xml'
    [xml]$xml = Get-Content $modsConfig -Encoding UTF8
    $active = $xml.ModsConfigData.activeMods
    $ids = @($active.li | ForEach-Object { $_.ToLowerInvariant() })
    if ($ids -notcontains 'depthrim.core') {
        $li = $xml.CreateElement('li'); $li.InnerText = 'depthrim.core'
        $anchor = $active.ChildNodes | Where-Object { $_.InnerText.ToLowerInvariant() -eq 'aoba.deadmanswitch.expedition' } | Select-Object -First 1
        if ($anchor) { [void]$active.InsertAfter($li, $anchor) } else { [void]$active.AppendChild($li) }
        $xml.Save($modsConfig)
    }
    $SaveDataFolder = $tempSaveData
    Write-Output "Using temporary save data folder with DepthRim enabled: $SaveDataFolder"
}

dotnet build (Join-Path $here 'DepthRim.Acceptance.csproj') -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'acceptance build failed' }

Copy-Item (Join-Path $here 'bin\DepthRim.Acceptance.dll') $runnerDll -Force
$started = Get-Date

try {
    $gameArgs = @('-quicktest', "-depthrim-acceptance=$OutDir", "-savedatafolder=$SaveDataFolder")
    if ($Only -ne '') { $gameArgs += "-depthrim-only=$Only" }
    $proc = Start-Process -FilePath $GameExe -ArgumentList $gameArgs -WorkingDirectory (Split-Path $GameExe) -PassThru
    Write-Output "RimWorld started (pid $($proc.Id)); waiting up to $TimeoutMinutes min..."
    $deadline = $started.AddMinutes($TimeoutMinutes)
    Start-Sleep -Seconds 10
    while ((Get-Process -Name 'RimWorld', 'RimWorldWin64' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 5
    }
    if (Get-Process -Name 'RimWorld', 'RimWorldWin64' -ErrorAction SilentlyContinue) {
        Write-Output 'Timeout: stopping RimWorld.'
        Get-Process -Name 'RimWorld', 'RimWorldWin64' -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Seconds 3
    }
}
finally {
    for ($i = 0; $i -lt 20 -and (Test-Path $runnerDll); $i++) {
        try { Remove-Item $runnerDll -Force } catch { Start-Sleep -Seconds 1 }
    }
    if (Test-Path $runnerDll) { Write-Warning "Could not remove $runnerDll - delete it manually." }
}

$list = Join-Path $OutDir 'depthrim_acceptance_screenshots.txt'
if (Test-Path $list) {
    foreach ($shot in Get-Content $list) { if (Test-Path $shot) { Move-Item $shot $OutDir -Force } }
}
$playerLog = Join-Path $SaveDataFolder 'Logs\Player.log'
if (-not (Test-Path $playerLog)) { $playerLog = Join-Path $env:USERPROFILE 'AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Logs\Player.log' }
if (Test-Path $playerLog) {
    $log = Get-Item $playerLog
    if ($log.LastWriteTime -ge $started) { Copy-Item $playerLog (Join-Path $OutDir 'Player.log') -Force }
}
$results = Join-Path $OutDir 'depthrim_acceptance_results.tsv'
if (Test-Path $results) { Get-Content $results } else { Write-Output 'No results file: the run did not finish (see Player.log / depthrim_acceptance_log.txt).' }
