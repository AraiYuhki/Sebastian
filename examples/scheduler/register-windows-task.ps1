# =============================================================================
# Windows タスクスケジューラ登録スクリプト — sebastian-ci を定期実行する
# =============================================================================
# 使い方（PowerShell を管理者不要のユーザー権限で）:
#   .\register-windows-task.ps1 -RepoPath C:\work\myrepo -IntervalMinutes 5
#
# 解除:
#   Unregister-ScheduledTask -TaskName "sebastian-ci" -Confirm:$false
#
# ポイント:
#   - sebastian-ci が PATH に無い場合は -SebastianCi で実行ファイルを指定する
#   - 同じコミットなら sebastian-ci 側がスキップするため、短い間隔でも低コスト
#   - タスクスケジューラは既定で多重起動しない（前回実行中なら次はスキップ）
# =============================================================================
param(
    [Parameter(Mandatory = $true)][string]$RepoPath,
    [int]$IntervalMinutes = 5,
    [string]$SebastianCi = "sebastian-ci",
    [string]$TaskName = "sebastian-ci"
)

$action = New-ScheduledTaskAction -Execute $SebastianCi -Argument "$RepoPath --yes" -WorkingDirectory $RepoPath
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date) `
    -RepetitionInterval (New-TimeSpan -Minutes $IntervalMinutes)
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Force
Write-Host "✅ タスク '$TaskName' を登録しました（$IntervalMinutes 分おきに $RepoPath を実行）"
